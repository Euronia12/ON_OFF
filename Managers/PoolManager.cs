// =================================================================
// [스크립트 목적]  오브젝트 풀링 통합. PoolableMonoBehaviour 기준 (타입 안전 + GetComponent 제거)
// [주요 변수]      - _pools             : prefab(Poolable) → Pool 매핑
//                  - _instanceToPool    : 인스턴스 GameObject → Pool (Despawn 역추적)
//                  - _instanceToPoolable: 인스턴스 GameObject → Poolable 캐시 (GetComponent 제거)
//                  - _listPools         : List 풀 (인덱스 슬롯 방식)
//                  - _addressableKeys   : prefab → Addressable 키
//                  - _despawnBuffer     : DespawnAll 순회용 재사용 버퍼 (GC 회피)
// [의존 관계]      - ManagerBase<PoolManager>, PoolableMonoBehaviour, ResourceManager
// [InitOrder]      40
// [설계]           Instantiate<T>(T)로 컴포넌트 직접 복제 → 스폰/디스폰 경로 GetComponent 0회
// =================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

public class PoolManager : ManagerBase<PoolManager>
{
    public override int InitOrder => 40;

    [Header("풀 기본 설정")]
    [Tooltip("풀 최대 크기 초과 시 정책.\nDestroyExcess: 초과분 Despawn 시 파괴 / KeepAll: 무제한 보관")]
    [SerializeField] private EPoolOverflow _overflowPolicy = EPoolOverflow.DestroyExcess;

    // ─────────────────────────────────────────────
    // 내부 자료구조
    // ─────────────────────────────────────────────

    private class Pool
    {
        // prefab의 PoolableMonoBehaviour. Instantiate<T>(T)로 인스턴스화 → GetComponent 불필요
        public readonly PoolableMonoBehaviour Prefab;
        public readonly EPoolCategory Category;
        public readonly int MaxSize;                  // 0 이하 = 무제한
        public Transform Root;

        // 대기: Stack (LIFO, 최근 반환 객체 우선 재사용 → 캐시 적중률↑)
        public readonly Stack<PoolableMonoBehaviour> Inactive = new();
        // 활성: HashSet (Despawn 시 소속 검증 O(1))
        public readonly HashSet<PoolableMonoBehaviour> Active = new();

        public Pool(PoolableMonoBehaviour prefab, EPoolCategory category, int maxSize, Transform root)
        {
            Prefab = prefab;
            Category = category;
            MaxSize = maxSize;
            Root = root;
        }

        public int TotalCount => Inactive.Count + Active.Count;
    }

    // 풀 식별: prefab의 PoolableMonoBehaviour 기준 (타입 안전 + GetComponent 제거)
    private readonly Dictionary<PoolableMonoBehaviour, Pool> _pools = new(32);
    // 인스턴스 역추적: GameObject 키 유지 (Despawn이 GameObject/컴포넌트 양쪽 수용)
    private readonly Dictionary<GameObject, Pool> _instanceToPool = new(256);
    // 인스턴스 GameObject → PoolableMonoBehaviour 캐시 (Despawn 시 GetComponent 제거)
    private readonly Dictionary<GameObject, PoolableMonoBehaviour> _instanceToPoolable = new(256);

    // 카테고리별 부모 Transform
    private readonly Dictionary<EPoolCategory, Transform> _categoryRoots = new(4);

    // UIPoolableMonoBehaviour용으로 생성한 프리팹별 parent 폴더.
    // 씬 전환 ClearAll 시 풀 목록과 별개로 남은 parent 및 하위 오브젝트를 확실히 제거한다.
    private readonly HashSet<Transform> _ownedPoolRoots = new();

    // Addressable 키 매핑 (ClearPool 시 핸들 Release용)
    private readonly Dictionary<PoolableMonoBehaviour, string> _addressableKeys = new(32);
    private readonly Dictionary<string, PoolableMonoBehaviour> _keyToPrefab = new(32);

    // ─── List 풀 (인덱스 슬롯 방식) ───
    // 익명 재사용이 아닌 "n번째 슬롯" 고정 접근용 (인벤토리/파티/스킬 슬롯 등)
    private class ListPool
    {
        public readonly PoolableMonoBehaviour Prefab;
        public Transform Root;
        public readonly List<PoolableMonoBehaviour> Items = new();

        public ListPool(PoolableMonoBehaviour prefab, Transform root)
        {
            Prefab = prefab;
            Root = root;
        }
    }

    private readonly Dictionary<PoolableMonoBehaviour, ListPool> _listPools = new(8);
    private readonly Dictionary<string, PoolableMonoBehaviour> _listKeyToPrefab = new(8);

    // 지연 Despawn 취소용
    private CancellationTokenSource _delayCts;

    // DespawnAll 순회용 재사용 버퍼 (HashSet 순회 중 Remove 방지 + 매 호출 GC 회피)
    private readonly List<PoolableMonoBehaviour> _despawnBuffer = new(256);

    // ─────────────────────────────────────────────
    // 초기화
    // ─────────────────────────────────────────────

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        _delayCts = new CancellationTokenSource();

        // World / Effect / Audio 루트 생성 (UI는 외부에서 SetUIRoot로 지정)
        CreateCategoryRoot(EPoolCategory.World);
        CreateCategoryRoot(EPoolCategory.Effect);
        CreateCategoryRoot(EPoolCategory.Audio);

        return UniTask.CompletedTask;
    }

    protected override UniTask OnShutdownInternalAsync()
    {
        // 앱 종료 시 풀 인스턴스 파괴와 Addressables 핸들 해제를 하지 않는다.
        // 프로세스가 곧 종료되므로 회수 이득은 없고, 아직 렌더/콜백에서 사용하는
        // 프리팹·텍스처 수명만 먼저 끝내 네이티브 크래시 위험을 만든다.
        // 씬 전환용 ClearAll() 동작은 그대로 유지한다.
        _delayCts?.Cancel();
        _delayCts?.Dispose();
        _delayCts = null;
        return UniTask.CompletedTask;
    }

    private void CreateCategoryRoot(EPoolCategory category)
    {
        var root = new GameObject($"[Pool_{category}]").transform;
        root.SetParent(transform);
        _categoryRoots[category] = root;
    }

    /// <summary>
    /// UI 카테고리 풀의 부모 Canvas 지정. UIManager 초기화 후 호출 권장.
    /// 미지정 시 UI 카테고리 풀은 World 루트로 폴백.
    /// </summary>
    public void SetUIRoot(Transform uiCanvasRoot)
    {
        if (uiCanvasRoot == null)
        {
            GameLogger.LogWarning(ELogCategory.Pool, "SetUIRoot: null 전달됨");
            return;
        }
        _categoryRoots[EPoolCategory.UI] = uiCanvasRoot;
    }

    private Transform GetCategoryRoot(EPoolCategory category)
    {
        if (_categoryRoots.TryGetValue(category, out var root) && root != null)
            return root;

        // UI 루트 미지정 시 World로 폴백
        return _categoryRoots[EPoolCategory.World];
    }

    private Transform CreatePoolRoot(PoolableMonoBehaviour prefab, EPoolCategory category)
    {
        if (prefab is UIPoolableMonoBehaviour uiPrefab)
        {
            Transform layerRoot = ResolveUILayerRoot(uiPrefab.TargetLayer);
            Transform parent = layerRoot != null ? layerRoot : GetCategoryRoot(category);
            return CreateChildRoot(parent, $"[Pool_UI_{prefab.name}]");
        }

        return GetCategoryRoot(category);
    }

    private Transform CreateListPoolRoot(PoolableMonoBehaviour prefab)
    {
        if (prefab is UIPoolableMonoBehaviour uiPrefab)
        {
            Transform layerRoot = ResolveUILayerRoot(uiPrefab.TargetLayer);
            Transform parent = layerRoot != null ? layerRoot : GetCategoryRoot(prefab.Category);
            return CreateChildRoot(parent, $"[Pool_UI_{prefab.name}_List]");
        }

        return GetCategoryRoot(prefab.Category);
    }

    private void EnsurePoolRoot(Pool pool)
    {
        if (pool == null || pool.Prefab is not UIPoolableMonoBehaviour uiPrefab) return;

        Transform layerRoot = ResolveUILayerRoot(uiPrefab.TargetLayer);
        if (layerRoot == null) return;

        if (pool.Root == null)
        {
            pool.Root = CreateChildRoot(layerRoot, $"[Pool_UI_{pool.Prefab.name}]");
            return;
        }

        if (pool.Root.parent != layerRoot)
            pool.Root.SetParent(layerRoot, worldPositionStays: false);
    }

    private void EnsureListPoolRoot(ListPool pool)
    {
        if (pool == null || pool.Prefab is not UIPoolableMonoBehaviour uiPrefab) return;

        Transform layerRoot = ResolveUILayerRoot(uiPrefab.TargetLayer);
        if (layerRoot == null) return;

        if (pool.Root == null)
        {
            pool.Root = CreateChildRoot(layerRoot, $"[Pool_UI_{pool.Prefab.name}_List]");
            return;
        }

        if (pool.Root.parent != layerRoot)
            pool.Root.SetParent(layerRoot, worldPositionStays: false);
    }

    private Transform ResolveUILayerRoot(EUILayer layer)
    {
        if (UIManager.HasInstance)
        {
            Transform layerRoot = UIManager.Instance.GetLayerRoot(layer);
            if (layerRoot != null) return layerRoot;
        }

        return _categoryRoots.TryGetValue(EPoolCategory.UI, out var uiRoot) ? uiRoot : null;
    }

    private Transform CreateChildRoot(Transform parent, string rootName)
    {
        GameObject rootObject = new GameObject(rootName, typeof(RectTransform));
        Transform root = rootObject.transform;
        root.SetParent(parent != null ? parent : transform, worldPositionStays: false);

        if (root is RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
        }

        _ownedPoolRoots.Add(root);
        return root;
    }

    // ─────────────────────────────────────────────
    // 풀 등록
    // ─────────────────────────────────────────────

    /// <summary>
    /// prefab(PoolableMonoBehaviour) 기준 풀 등록 + 초기 개수만큼 사전 생성.
    /// initial: 미리 생성할 개수 / maxSize: 최대 보관 (0 이하 = 무제한)
    /// </summary>
    public void Register(PoolableMonoBehaviour prefab, int initial = 0, int maxSize = 0)
    {
        if (prefab == null)
        {
            GameLogger.LogError(ELogCategory.Pool, "Register: prefab이 null");
            return;
        }

        if (_pools.ContainsKey(prefab))
        {
            GameLogger.LogWarning(ELogCategory.Pool, $"Register: 이미 등록된 풀 {prefab.name}");
            return;
        }

        // prefab 자체가 PoolableMonoBehaviour이므로 카테고리 즉시 획득 (GetComponent 불필요)
        var category = prefab.Category;
        var pool = new Pool(prefab, category, maxSize, CreatePoolRoot(prefab, category));
        _pools[prefab] = pool;

        if (initial > 0)
            PrewarmInternal(pool, initial);
    }

    /// <summary>
    /// Addressable 키로 prefab 로드 후 풀 등록. 비동기.
    /// 로드 결과(GameObject)에서 PoolableMonoBehaviour를 1회만 추출해 이후 GetComponent 제거.
    /// </summary>
    public async UniTask RegisterAsync(string addressableKey, int initial = 0, int maxSize = 0,
        CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(addressableKey))
        {
            GameLogger.LogError(ELogCategory.Pool, "RegisterAsync: 키가 비어있음");
            return;
        }

        // 동일 키 중복 등록 방어 (불필요한 Addressable 핸들 증가 차단)
        if (_keyToPrefab.ContainsKey(addressableKey))
        {
            GameLogger.LogWarning(ELogCategory.Pool, $"RegisterAsync: 이미 등록된 키 {addressableKey}");
            return;
        }

        // PoolableMonoBehaviour 타입으로 직접 로드 (없으면 null → 풀 대상 아님)
        var prefab = await LoadPoolablePrefabAsync(addressableKey, token);
        if (prefab == null)
        {
            GameLogger.LogError(ELogCategory.Pool,
                $"RegisterAsync: 로드 실패 또는 PoolableMonoBehaviour 없음 {addressableKey}");
            return;
        }

        Register(prefab, initial, maxSize);

        // 키 매핑 보관 (ClearPool 시 Release용)
        _addressableKeys[prefab] = addressableKey;
        _keyToPrefab[addressableKey] = prefab;
    }

    /// <summary>풀에 비활성 인스턴스 미리 생성. 풀 미등록 시 자동 등록.</summary>
    public void Prewarm(PoolableMonoBehaviour prefab, int count)
    {
        if (prefab == null || count <= 0) return;

        if (!_pools.TryGetValue(prefab, out var pool))
        {
            Register(prefab, 0);
            pool = _pools[prefab];
        }

        PrewarmInternal(pool, count);
    }

    private void PrewarmInternal(Pool pool, int count)
    {
        int created = 0;
        for (int i = 0; i < count; i++)
        {
            if (pool.MaxSize > 0 && pool.TotalCount >= pool.MaxSize)
            {
                GameLogger.LogWarning(ELogCategory.Pool,
                    $"Prewarm: {pool.Prefab.name} 요청 {count}개 중 {created}개만 생성됨 " +
                    $"(MaxSize {pool.MaxSize} 도달)");
                break;
            }

            var instance = CreateInstance(pool);
            instance.gameObject.SetActive(false);
            pool.Inactive.Push(instance);
            created++;
        }
    }

    /// <summary>
    /// 인스턴스 1개 생성. Instantiate&lt;T&gt;(T)로 컴포넌트를 직접 받아 GetComponent 제거.
    /// 인스턴스 GameObject↔컴포넌트 매핑도 캐시 (Despawn 고속화).
    /// </summary>
    private PoolableMonoBehaviour CreateInstance(Pool pool)
    {
        EnsurePoolRoot(pool);

        // Instantiate<T>(T)는 새 인스턴스의 동일 컴포넌트를 직접 반환 → GetComponent 불필요
        var instance = UnityEngine.Object.Instantiate(pool.Prefab, pool.Root);
        var go = instance.gameObject;

        _instanceToPool[go] = pool;
        _instanceToPoolable[go] = instance;

        // 최초 1회 OnPoolCreate 호출
        instance.InvokePoolCreate();

        return instance;
    }

    // ─────────────────────────────────────────────
    // Spawn (동기, 오버로드)
    // ─────────────────────────────────────────────

    // 모든 Spawn은 제네릭으로 통일. T는 PoolableMonoBehaviour 파생.
    // prefab이 T이므로 반환도 T — 캐스팅·GetComponent 없음.
    // 베이스 타입으로 충분하면 T=PoolableMonoBehaviour로 추론됨.

    public T Spawn<T>(T prefab) where T : PoolableMonoBehaviour
        => (T)SpawnInternal(prefab, Vector3.zero, Quaternion.identity, null, useTransform: false);

    public T Spawn<T>(T prefab, Transform parent) where T : PoolableMonoBehaviour
        => (T)SpawnInternal(prefab, Vector3.zero, Quaternion.identity, parent, useTransform: false);

    public T Spawn<T>(T prefab, Vector3 position, Quaternion rotation) where T : PoolableMonoBehaviour
        => (T)SpawnInternal(prefab, position, rotation, null, useTransform: true);

    /// <summary>
    /// 구체 타입 T로 Spawn. prefab이 T이므로 반환도 T — 캐스팅·GetComponent 없음.
    /// 예: var bullet = pool.Spawn(bulletPrefab, pos, rot); // bulletPrefab이 Bullet 타입
    /// </summary>
    public T Spawn<T>(T prefab, Vector3 position, Quaternion rotation, Transform parent)
        where T : PoolableMonoBehaviour
    {
        // SpawnInternal은 prefab으로부터 복제된 동일 타입 인스턴스를 반환하므로 안전한 캐스팅
        return (T)SpawnInternal(prefab, position, rotation, parent, useTransform: true);
    }

    private PoolableMonoBehaviour SpawnInternal(PoolableMonoBehaviour prefab, Vector3 position, Quaternion rotation,
        Transform parent, bool useTransform)
    {
        if (prefab == null)
        {
            GameLogger.LogError(ELogCategory.Pool, "Spawn: prefab이 null");
            return null;
        }

        // 풀 미등록 시 자동 등록
        if (!_pools.TryGetValue(prefab, out var pool))
        {
            Register(prefab, 0);
            pool = _pools[prefab];
        }

        // 대기열에서 꺼내거나 신규 생성
        EnsurePoolRoot(pool);

        PoolableMonoBehaviour instance = null;
        while (pool.Inactive.Count > 0)
        {
            instance = pool.Inactive.Pop();
            if (instance != null) break; // 파괴된 참조 스킵
        }

        if (instance == null)
            instance = CreateInstance(pool);

        var tr = instance.transform;

        // 부모 설정
        var targetParent = parent != null ? parent : pool.Root;
        tr.SetParent(targetParent, worldPositionStays: false);

        // 위치/회전 설정
        if (useTransform)
            tr.SetPositionAndRotation(position, rotation);

        instance.gameObject.SetActive(true);
        pool.Active.Add(instance);

        // 풀 상태 콜백 (prefab이 PoolableMonoBehaviour이므로 GetComponent 불필요)
        instance.IsInPool = false;
        instance.OnSpawnFromPool();

        return instance;
    }

    // ─────────────────────────────────────────────
    // Spawn (비동기, Addressable 키)
    // ─────────────────────────────────────────────

    /// <summary>Addressable 키로 소환. 풀 미등록 시 자동 등록 후 소환. 반환은 PoolableMonoBehaviour.</summary>
    public async UniTask<PoolableMonoBehaviour> SpawnAsync(string addressableKey, Vector3 position, Quaternion rotation,
        Transform parent = null, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(addressableKey))
        {
            GameLogger.LogError(ELogCategory.Pool, "SpawnAsync: 키가 비어있음");
            return null;
        }

        // 이미 등록된 키면 캐시된 prefab으로 즉시 Spawn (핸들 중복 방지)
        if (_keyToPrefab.TryGetValue(addressableKey, out var cachedPrefab))
            return SpawnInternal(cachedPrefab, position, rotation, parent, useTransform: true);

        // 미등록 → 로드 후 풀 자동 등록 + 키 매핑
        var prefab = await LoadPoolablePrefabAsync(addressableKey, token);
        if (prefab == null)
        {
            GameLogger.LogError(ELogCategory.Pool,
                $"SpawnAsync: 로드 실패 또는 PoolableMonoBehaviour 없음 {addressableKey}");
            return null;
        }

        if (!_pools.ContainsKey(prefab))
            Register(prefab, 0);

        _addressableKeys[prefab] = addressableKey;
        _keyToPrefab[addressableKey] = prefab;

        return SpawnInternal(prefab, position, rotation, parent, useTransform: true);
    }

    /// <summary>구체 타입 T로 비동기 Spawn.</summary>
    public async UniTask<T> SpawnAsync<T>(string addressableKey, Vector3 position, Quaternion rotation,
        Transform parent = null, CancellationToken token = default) where T : PoolableMonoBehaviour
    {
        var result = await SpawnAsync(addressableKey, position, rotation, parent, token);
        return result as T;
    }

    // ─────────────────────────────────────────────
    // Despawn
    // ─────────────────────────────────────────────

    /// <summary>인스턴스를 풀로 반환 (컴포넌트 직접 전달). 가장 빠른 경로.</summary>
    public void Despawn(PoolableMonoBehaviour instance)
    {
        if (instance == null) return;
        DespawnInternal(instance.gameObject, instance);
    }

    /// <summary>인스턴스를 풀로 반환 (GameObject 전달). 충돌 콜백 등 GameObject만 있을 때.</summary>
    public void Despawn(GameObject instance)
    {
        if (instance == null) return;

        // 캐시에서 컴포넌트 역참조 (GetComponent 불필요)
        _instanceToPoolable.TryGetValue(instance, out var poolable);
        DespawnInternal(instance, poolable);
    }

    private void DespawnInternal(GameObject go, PoolableMonoBehaviour poolable)
    {
        if (!_instanceToPool.TryGetValue(go, out var pool))
        {
            // 풀 소속 아님 → 일반 파괴
            UnityEngine.Object.Destroy(go);
            return;
        }

        // poolable이 null이면 캐시 재조회 (Despawn(GameObject) 경로에서 누락 대비)
        if (poolable == null)
            _instanceToPoolable.TryGetValue(go, out poolable);

        // 중복 Despawn 방어: Active 제거 성공 여부를 단일 기준으로 사용.
        // 이미 반환된 인스턴스를 다시 호출하면 Inactive에 중복 Push되어
        // 다음 Spawn 시 같은 인스턴스가 두 번 활성화되는 버그가 생긴다. 이를 차단한다.
        if (poolable != null)
        {
            if (!pool.Active.Remove(poolable))
                return; // 이미 반환됨 → 중복 호출 무시
        }
        else
        {
            // poolable을 끝내 찾지 못한 비정상 케이스: 풀 일관성 보호를 위해 일반 파괴로 처리
            GameLogger.LogWarning(ELogCategory.Pool,
                $"Despawn: 인스턴스 컴포넌트를 찾지 못함 {go.name} → 파괴 처리");
            _instanceToPool.Remove(go);
            _instanceToPoolable.Remove(go);
            UnityEngine.Object.Destroy(go);
            return;
        }

        // 풀 상태 콜백 (이 시점 poolable은 위 분기로 항상 non-null 보장)
        poolable.OnReturnToPool();
        poolable.IsInPool = true;

        // 최대치 초과 시 정책 적용
        if (_overflowPolicy == EPoolOverflow.DestroyExcess &&
            pool.MaxSize > 0 && pool.Inactive.Count >= pool.MaxSize)
        {
            _instanceToPool.Remove(go);
            _instanceToPoolable.Remove(go);
            UnityEngine.Object.Destroy(go);
            return;
        }

        EnsurePoolRoot(pool);

        go.SetActive(false);
        go.transform.SetParent(pool.Root, worldPositionStays: false);

        pool.Inactive.Push(poolable);
    }

    /// <summary>지연 후 반환. UniTask 기반. 풀 정리 시 자동 취소.</summary>
    public void Despawn(GameObject instance, float delay)
    {
        if (instance == null) return;

        if (delay <= 0f)
        {
            Despawn(instance);
            return;
        }

        DespawnDelayedAsync(instance, delay).Forget();
    }

    /// <summary>지연 후 반환 (컴포넌트 버전).</summary>
    public void Despawn(PoolableMonoBehaviour instance, float delay)
    {
        if (instance == null) return;
        Despawn(instance.gameObject, delay);
    }

    private async UniTaskVoid DespawnDelayedAsync(GameObject instance, float delay)
    {
        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(delay),
                cancellationToken: _delayCts.Token);

            if (instance != null && _instanceToPool.ContainsKey(instance))
                Despawn(instance);
        }
        catch (OperationCanceledException) { }
    }

    // ─────────────────────────────────────────────
    // 일괄 회수 (Despawn)
    // 파괴(ClearPool/ClearAll)와 달리, 활성 인스턴스를 "풀로 되돌리는" 동작.
    // OnReturnToPool 콜백이 정상 호출되므로 재사용 가능 상태로 회수된다.
    // ─────────────────────────────────────────────

    /// <summary>
    /// 특정 prefab 풀의 활성 인스턴스 전부를 풀로 회수.
    /// 예: 스테이지 클리어 시 해당 적/투사체 전부 회수.
    /// </summary>
    public void DespawnAll(PoolableMonoBehaviour prefab)
    {
        if (prefab == null) return;
        if (!_pools.TryGetValue(prefab, out var pool)) return;
        if (pool.Active.Count == 0) return;

        // HashSet 순회 중 Remove(Despawn 내부) → 컬렉션 변경 예외 방지를 위해 버퍼 복사
        _despawnBuffer.Clear();
        foreach (var active in pool.Active)
            _despawnBuffer.Add(active);

        for (int i = 0; i < _despawnBuffer.Count; i++)
        {
            var inst = _despawnBuffer[i];
            if (inst != null) Despawn(inst);
        }
        _despawnBuffer.Clear();
    }

    /// <summary>Addressable 키로 특정 풀 활성 인스턴스 전부 회수.</summary>
    public void DespawnAll(string addressableKey)
    {
        if (string.IsNullOrEmpty(addressableKey)) return;
        if (_keyToPrefab.TryGetValue(addressableKey, out var prefab))
            DespawnAll(prefab);
    }

    /// <summary>
    /// 등록된 모든 풀의 활성 인스턴스를 풀로 회수.
    /// 씬 전환 정리 시 파괴(ClearAll) 대신 재사용 보존이 필요할 때 사용.
    /// </summary>
    public void DespawnAllActive()
    {
        _despawnBuffer.Clear();

        // 모든 풀의 활성 인스턴스를 한 번에 버퍼로 모은 뒤 처리
        // (풀별로 나눠 돌면 중간 상태에서 Active가 바뀌므로 일괄 수집이 안전)
        foreach (var pool in _pools.Values)
        {
            foreach (var active in pool.Active)
                _despawnBuffer.Add(active);
        }

        for (int i = 0; i < _despawnBuffer.Count; i++)
        {
            var inst = _despawnBuffer[i];
            if (inst != null) Despawn(inst);
        }
        _despawnBuffer.Clear();
    }

    /// <summary>특정 prefab 풀 완전 제거 (활성·대기 모두 파괴). Addressable 핸들도 Release.</summary>
    public void ClearPool(PoolableMonoBehaviour prefab)
    {
        if (prefab == null) return;

        // List 풀 정리 (있으면)
        if (_listPools.TryGetValue(prefab, out var listPool))
        {
            foreach (var item in listPool.Items)
            {
                if (item != null)
                {
                    _instanceToPool.Remove(item.gameObject);
                    _instanceToPoolable.Remove(item.gameObject);
                    UnityEngine.Object.Destroy(item.gameObject);
                }
            }
            listPool.Items.Clear();
            _listPools.Remove(prefab);
            DestroyOwnedPoolRoot(listPool.Root, prefab);
        }

        if (!_pools.TryGetValue(prefab, out var pool))
        {
            // Stack 풀은 없어도 List 키 매핑 정리는 필요할 수 있음
            ReleaseKeyMapping(prefab);
            return;
        }

        foreach (var inactive in pool.Inactive)
        {
            if (inactive != null)
            {
                _instanceToPool.Remove(inactive.gameObject);
                _instanceToPoolable.Remove(inactive.gameObject);
                UnityEngine.Object.Destroy(inactive.gameObject);
            }
        }

        foreach (var active in pool.Active)
        {
            if (active != null)
            {
                _instanceToPool.Remove(active.gameObject);
                _instanceToPoolable.Remove(active.gameObject);
                UnityEngine.Object.Destroy(active.gameObject);
            }
        }

        pool.Inactive.Clear();
        pool.Active.Clear();
        _pools.Remove(prefab);
        DestroyOwnedPoolRoot(pool.Root, prefab);

        ReleaseKeyMapping(prefab);
    }

    // Addressable 키 매핑 정리 + 핸들 Release (Stack/List 공용)
    private void ReleaseKeyMapping(PoolableMonoBehaviour prefab)
    {
        if (_addressableKeys.TryGetValue(prefab, out var key))
        {
            _addressableKeys.Remove(prefab);
            _keyToPrefab.Remove(key);
            _listKeyToPrefab.Remove(key);
            if (ResourceManager.HasInstance)
                // LoadComponentAsync(=GameObject 캐시)로 로드했으므로 짝 맞는 Component 해제 사용.
                // (ReleaseAddressable<컴포넌트타입>은 엉뚱한 캐시를 뒤져 핸들 누수 발생)
                ResourceManager.Instance.ReleaseAddressableComponent(key);
        }
    }

    /// <summary>모든 풀 제거. 씬 전환 시 호출. Addressable 핸들 일괄 Release.</summary>
    public void ClearAll()
    {
        // 진행 중인 지연 Despawn 취소
        _delayCts?.Cancel();
        _delayCts?.Dispose();
        _delayCts = new CancellationTokenSource();

        foreach (var pool in _pools.Values)
        {
            foreach (var inactive in pool.Inactive)
                if (inactive != null) UnityEngine.Object.Destroy(inactive.gameObject);
            foreach (var active in pool.Active)
                if (active != null) UnityEngine.Object.Destroy(active.gameObject);
            DestroyOwnedPoolRoot(pool.Root, pool.Prefab);
        }

        // List 풀 정리
        foreach (var listPool in _listPools.Values)
        {
            foreach (var item in listPool.Items)
                if (item != null) UnityEngine.Object.Destroy(item.gameObject);
            DestroyOwnedPoolRoot(listPool.Root, listPool.Prefab);
        }

        DestroyAllOwnedPoolRoots();

        _pools.Clear();
        _instanceToPool.Clear();
        _instanceToPoolable.Clear();
        _listPools.Clear();

        // Addressable 핸들 일괄 Release
        if (ResourceManager.HasInstance)
        {
            foreach (var key in _addressableKeys.Values)
                // LoadComponentAsync(=GameObject 캐시)와 짝 맞는 해제 (핸들 누수 방지)
                ResourceManager.Instance.ReleaseAddressableComponent(key);
        }
        _addressableKeys.Clear();
        _keyToPrefab.Clear();
        _listKeyToPrefab.Clear();
    }

    private void DestroyOwnedPoolRoot(Transform root, PoolableMonoBehaviour prefab)
    {
        if (root == null || prefab is not UIPoolableMonoBehaviour) return;

        foreach (Transform categoryRoot in _categoryRoots.Values)
        {
            if (root == categoryRoot) return;
        }

        _ownedPoolRoots.Remove(root);
        UnityEngine.Object.Destroy(root.gameObject);
    }

    private void DestroyAllOwnedPoolRoots()
    {
        if (_ownedPoolRoots.Count == 0) return;

        foreach (Transform root in _ownedPoolRoots)
        {
            if (root != null)
                UnityEngine.Object.Destroy(root.gameObject);
        }

        _ownedPoolRoots.Clear();
    }

    // ─────────────────────────────────────────────
    // List 풀 (인덱스 슬롯 방식)
    // Stack 풀과 별개. 고정 슬롯 UI·파티 멤버처럼 "인덱스로 지목"이 필요할 때 사용
    // ─────────────────────────────────────────────

    /// <summary>
    /// List 풀 등록 + count개 사전 생성. 슬롯은 생성 시 활성/비활성 선택.
    /// </summary>
    public void RegisterList(PoolableMonoBehaviour prefab, int count, bool activeOnCreate = true)
    {
        if (prefab == null)
        {
            GameLogger.LogError(ELogCategory.Pool, "RegisterList: prefab이 null");
            return;
        }

        if (_listPools.ContainsKey(prefab))
        {
            GameLogger.LogWarning(ELogCategory.Pool, $"RegisterList: 이미 등록됨 {prefab.name}");
            return;
        }

        var pool = new ListPool(prefab, CreateListPoolRoot(prefab));
        _listPools[prefab] = pool;

        ExpandList(pool, count, activeOnCreate);
    }

    /// <summary>Addressable 키로 List 풀 등록 (비동기 로드).</summary>
    public async UniTask RegisterListAsync(string addressableKey, int count,
        bool activeOnCreate = true, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(addressableKey)) return;
        if (_listKeyToPrefab.ContainsKey(addressableKey))
        {
            GameLogger.LogWarning(ELogCategory.Pool, $"RegisterListAsync: 이미 등록된 키 {addressableKey}");
            return;
        }

        var prefab = await LoadPoolablePrefabAsync(addressableKey, token);
        if (prefab == null)
        {
            GameLogger.LogError(ELogCategory.Pool, $"RegisterListAsync: 로드 실패 {addressableKey}");
            return;
        }

        RegisterList(prefab, count, activeOnCreate);
        _listKeyToPrefab[addressableKey] = prefab;
        // Stack 풀과 키 매핑 공유 (ClearPool 시 Release 일관성)
        _addressableKeys[prefab] = addressableKey;
        _keyToPrefab[addressableKey] = prefab;
    }

    /// <summary>
    /// 인덱스로 슬롯 "조회" (참조 획득만, 활성화·콜백 없음). 범위 초과 시 자동 확장(비활성 생성).
    /// 슬롯을 실제로 사용(활성화)하려면 SpawnListItem을 호출할 것.
    /// 반환은 구체 타입 T.
    /// </summary>
    public T GetListItem<T>(T prefab, int index) where T : PoolableMonoBehaviour
    {
        if (!_listPools.TryGetValue(prefab, out var pool))
        {
            GameLogger.LogError(ELogCategory.Pool, $"GetListItem: 미등록 풀 {prefab?.name}");
            return null;
        }

        if (index < 0)
        {
            GameLogger.LogError(ELogCategory.Pool, $"GetListItem: 음수 인덱스 {index}");
            return null;
        }

        // 부족하면 확장 (조회 단계이므로 비활성으로 생성 — 활성화는 SpawnListItem 책임)
        if (index >= pool.Items.Count)
            ExpandList(pool, index - pool.Items.Count + 1, activeOnCreate: false);

        return (T)pool.Items[index];
    }

    /// <summary>키 기반 인덱스 조회.</summary>
    public T GetListItem<T>(string addressableKey, int index) where T : PoolableMonoBehaviour
    {
        if (!_listKeyToPrefab.TryGetValue(addressableKey, out var prefab))
        {
            GameLogger.LogError(ELogCategory.Pool, $"GetListItem: 미등록 키 {addressableKey}");
            return null;
        }
        return GetListItem((T)prefab, index);
    }

    /// <summary>
    /// 인덱스 슬롯을 "활성화(스폰)". 비활성 상태였으면 활성화 + OnSpawnFromPool 1회.
    /// 이미 활성 상태면 콜백 중복 호출 없이 참조만 반환(매 프레임 조회 시 리셋 사고 방지).
    /// 범위 초과 시 자동 확장 후 활성화.
    /// </summary>
    public T SpawnListItem<T>(T prefab, int index) where T : PoolableMonoBehaviour
    {
        var item = GetListItem(prefab, index); // 조회(필요 시 비활성 확장)
        if (item == null) return null;

        // 비활성 → 활성 전이일 때만 스폰 콜백 (중복 OnSpawnFromPool 방지)
        if (!item.gameObject.activeSelf)
        {
            item.gameObject.SetActive(true);
            item.IsInPool = false;
            item.OnSpawnFromPool();
        }
        return item;
    }

    /// <summary>키 기반 슬롯 활성화(스폰).</summary>
    public T SpawnListItem<T>(string addressableKey, int index) where T : PoolableMonoBehaviour
    {
        if (!_listKeyToPrefab.TryGetValue(addressableKey, out var prefab))
        {
            GameLogger.LogError(ELogCategory.Pool, $"SpawnListItem: 미등록 키 {addressableKey}");
            return null;
        }
        return SpawnListItem((T)prefab, index);
    }

    public int GetListCount(PoolableMonoBehaviour prefab)
        => _listPools.TryGetValue(prefab, out var pool) ? pool.Items.Count : 0;

    /// <summary>특정 인덱스 이상 슬롯 비활성화 (목록 축소 시). 파괴 아닌 숨김.</summary>
    public void HideListFrom(PoolableMonoBehaviour prefab, int fromIndex)
    {
        if (!_listPools.TryGetValue(prefab, out var pool)) return;
        if (fromIndex < 0) fromIndex = 0;

        for (int i = fromIndex; i < pool.Items.Count; i++)
        {
            var item = pool.Items[i];
            if (item == null) continue;

            // 이미 비활성이면 중복 OnReturnToPool 방지 (Stack풀 Despawn 중복 방어와 동일 취지)
            if (!item.gameObject.activeSelf) continue;

            item.OnReturnToPool();
            item.IsInPool = true;
            item.gameObject.SetActive(false);
        }
    }

    public void HideListAll(PoolableMonoBehaviour prefab) => HideListFrom(prefab, 0);

    private void ExpandList(ListPool pool, int addCount, bool activeOnCreate)
    {
        EnsureListPoolRoot(pool);

        for (int i = 0; i < addCount; i++)
        {
            var instance = UnityEngine.Object.Instantiate(pool.Prefab, pool.Root);
            instance.InvokePoolCreate();

            // 활성 생성: 즉시 사용 상태(IsInPool=false) / 비활성 생성: 풀 대기 상태(IsInPool=true)
            instance.IsInPool = !activeOnCreate;
            instance.gameObject.SetActive(activeOnCreate);

            // 활성 생성이면 스폰 콜백 1회 (RegisterList(activeOnCreate:true) 경로 일관성)
            if (activeOnCreate)
                instance.OnSpawnFromPool();

            pool.Items.Add(instance);
        }
    }

    // ─────────────────────────────────────────────
    // 조회
    // ─────────────────────────────────────────────

    public int GetActiveCount(PoolableMonoBehaviour prefab)
        => _pools.TryGetValue(prefab, out var pool) ? pool.Active.Count : 0;

    public int GetPooledCount(PoolableMonoBehaviour prefab)
        => _pools.TryGetValue(prefab, out var pool) ? pool.Inactive.Count : 0;

    public bool IsRegistered(PoolableMonoBehaviour prefab) => _pools.ContainsKey(prefab);

    /// <summary>인스턴스가 이 PoolManager 소속인지 확인 (UIManager Despawn 경로 판별용).</summary>
    public bool IsPooledInstance(GameObject instance)
        => instance != null && _instanceToPool.ContainsKey(instance);

    public string GetAddressableKey(PoolableMonoBehaviour prefab)
        => _addressableKeys.TryGetValue(prefab, out var key) ? key : null;

    public PoolableMonoBehaviour GetPrefabByKey(string addressableKey)
        => _keyToPrefab.TryGetValue(addressableKey, out var prefab) ? prefab : null;

    /// <summary>
    /// 전체 풀 현황을 로그로 덤프 (활성/대기/총합). 개발 빌드 전용.
    /// 풀 누수(Despawn 누락) 추적, 프리웜 개수 검증 시 사용.
    /// </summary>
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public void DumpStatus()
    {
        // Conditional 어트리뷰트로 출시 빌드에서는 호출 자체가 제거됨 → GC·성능 영향 0
        var sb = new System.Text.StringBuilder(256);
        sb.Append("===== PoolManager 현황 =====\n");

        int totalActive = 0;
        int totalInactive = 0;
        foreach (var kvp in _pools)
        {
            var pool = kvp.Value;
            totalActive += pool.Active.Count;
            totalInactive += pool.Inactive.Count;

            string keyInfo = _addressableKeys.TryGetValue(kvp.Key, out var key) ? $" (key:{key})" : "";
            sb.Append($"[Stack] {kvp.Key.name}{keyInfo} | 활성 {pool.Active.Count} / 대기 {pool.Inactive.Count} / 총 {pool.TotalCount}");
            if (pool.MaxSize > 0) sb.Append($" / Max {pool.MaxSize}");
            sb.Append('\n');
        }

        foreach (var kvp in _listPools)
        {
            sb.Append($"[List] {kvp.Key.name} | 슬롯 {kvp.Value.Items.Count}\n");
        }

        sb.Append($"--- 합계: Stack풀 {_pools.Count}개 / List풀 {_listPools.Count}개 / 활성 {totalActive} / 대기 {totalInactive} ---");
        GameLogger.Log(ELogCategory.Pool, sb.ToString());
    }

    // ─────────────────────────────────────────────
    // 키 테이블 기반 일괄 프리웜 (addressableMap 통합)
    // ─────────────────────────────────────────────

    /// <summary>
    /// ResourceManager 키 테이블에서 Prewarm 플래그(=Preload 폴더)가 켜진 키 중
    /// 풀링 대상(PoolableMonoBehaviour)을 풀에 등록 + 각 프리팹의 PrewarmCount만큼 미리 생성.
    /// 부팅 직후 1회 호출 권장.
    ///
    /// 개수 결정: 프리팹의 PoolableMonoBehaviour._prewarmCount (Inspector 지정)
    /// fallbackCount: 프리팹 PrewarmCount가 0일 때 대신 쓸 기본값 (0이면 미생성)
    /// </summary>
    public async UniTask PrewarmFromTableAsync(int fallbackCount = 0, CancellationToken token = default)
    {
        if (!ResourceManager.HasInstance)
        {
            GameLogger.LogWarning(ELogCategory.Pool, "PrewarmFromTable: ResourceManager 없음");
            return;
        }

        // Preload 대상 키 전체 (그룹 무관 — Prefab/Effect 등 어느 그룹이든)
        var keys = ResourceManager.Instance.GetPrewarmKeys();
        if (keys.Count == 0)
        {
            GameLogger.Log(ELogCategory.Pool, "프리웜 대상 키 없음");
            return;
        }

        int pooledCount = 0;
        foreach (var key in keys)
        {
            token.ThrowIfCancellationRequested();

            // 풀링 대상만 로드 시도 (PoolableMonoBehaviour 없으면 null → 스킵)
            var prefab = await LoadPoolablePrefabAsync(key, token);
            if (prefab == null)
            {
                // 풀 대상이 아닌 Preload 에셋(데이터/이미지 등)은 여기서 처리 안 함
                // (그건 ResourceManager.PreloadFromTableAsync가 담당 — 아래 참고)
                continue;
            }

            // 프리팹이 지정한 개수 우선, 없으면 fallback
            int count = prefab.PrewarmCount > 0 ? prefab.PrewarmCount : fallbackCount;

            // 풀 등록 + 키 매핑
            if (!_pools.ContainsKey(prefab))
                Register(prefab, count);
            else if (count > 0)
                Prewarm(prefab, count);

            _addressableKeys[prefab] = key;
            _keyToPrefab[key] = prefab;
            pooledCount++;
        }

        GameLogger.Log(ELogCategory.Pool, $"테이블 기반 풀 프리웜 완료: {pooledCount}개 풀");
    }

    // 프리팹(GameObject)에서 PoolableMonoBehaviour 추출 (ResourceManager 공통 헬퍼 사용)
    private async UniTask<PoolableMonoBehaviour> LoadPoolablePrefabAsync(
        string key, CancellationToken token)
    {
        if (!ResourceManager.HasInstance) return null;
        return await ResourceManager.Instance.LoadComponentAsync<PoolableMonoBehaviour>(key, token);
    }

    protected override void OnDestroy()
    {
        _delayCts?.Cancel();
        _delayCts?.Dispose();
        _delayCts = null;
        base.OnDestroy();
    }
}
