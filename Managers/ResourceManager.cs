// =================================================================
// [스크립트 목적]  리소스 로드 통합 진입점. Addressable/Resources 두 로더 보유·전환
//                 + addressableMap 키 테이블 통합 (키 검증·프리웜 목록 조회)
// [주요 변수]      - _addressableLoader : Addressables 구현체
//                  - _resourceLoader    : Resources 구현체
//                  - _keyTable          : addressableMap.json 기반 키-경로 테이블
// [의존 관계]      - ManagerBase<ResourceManager>, AddressableLoader, ResourceFolderLoader
//                  - AddressableKeyTable
// [InitOrder]      30
// =================================================================
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.U2D;

public class ResourceManager : ManagerBase<ResourceManager>
{
    public override int InitOrder => 30;
    public override bool IsCritical => true; // 리소스 로딩은 필수

    [Header("기본 로더 설정")]
    [Tooltip("LoadAsync 등 통합 API 호출 시 사용할 기본 로더.\nAddressable: Production 권장 / Resource: 동기 로드·소규모")]
    [SerializeField] private EResourceMode _defaultMode = EResourceMode.Addressable;

    [Header("키 테이블 설정")]
    [Tooltip("addressableMap.json들에 붙은 Addressables 라벨")]
    [SerializeField] private string _mapLabel = "addressableMap";

    [Tooltip("Addressable 모드일 때 시작 시 키 테이블 자동 로드")]
    [SerializeField] private bool _loadKeyTable = true;

    private AddressableLoader _addressableLoader;
    private ResourceFolderLoader _resourceLoader;

    // 인스턴스 추적 (ReleaseInstance에서 어떤 키로 만들었는지 역추적)
    private readonly Dictionary<GameObject, string> _instanceKeys = new();

    // 키-경로 매핑 테이블 (addressableMap.json 기반). 키 검증·프리웜 목록 조회용
    private readonly AddressableKeyTable _keyTable = new();
    private readonly Dictionary<EAtlasType, SpriteAtlas> _atlasCache = new();

    public EResourceMode DefaultMode => _defaultMode;
    public AddressableKeyTable KeyTable => _keyTable;

    protected override async UniTask OnInitializeAsync(CancellationToken token)
    {
        _addressableLoader = new AddressableLoader();
        _resourceLoader = new ResourceFolderLoader();

        // Addressable 모드 + 키 테이블 사용 시 매핑 로드
        if (_loadKeyTable && _defaultMode == EResourceMode.Addressable)
        {
            try
            {
                await _keyTable.LoadAsync(_mapLabel, token);
                GameLogger.Log(ELogCategory.Resource, $"키 테이블 로드 완료 ({_keyTable.Count}개)");
            }
            catch (System.OperationCanceledException) { throw; }
            catch (System.Exception e)
            {
                // 키 테이블은 보조 기능 — 실패해도 직접 키 로드는 동작하므로 경고만
                GameLogger.LogWarning(ELogCategory.Resource,
                    $"키 테이블 로드 실패(직접 키 로드는 가능): {e.Message}");
            }
        }
    }

    protected override UniTask OnShutdownInternalAsync()
    {
        // 앱 종료 시엔 Addressables 해제뿐 아니라 런타임 캐시도 건드리지 않는다.
        // 프로세스가 곧 죽으므로 회수 이득이 없고, Application.Quit이 실제로 적용되기까지
        // 남은 프레임 동안 늦은 비동기 콜백이 캐시·키 테이블을 조회할 수 있다.
        // (씬 전환 시 부분 해제는 UnloadUnusedAssets/개별 Release 경로가 담당하며 이 종료 경로와 무관하다.)
        return UniTask.CompletedTask;
    }

    public async UniTask<Sprite> LoadAtlasSpriteAsync(
        EAtlasType atlasType, string spriteKey, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(spriteKey)) return null;

        SpriteAtlas atlas = await LoadAtlasAsync(atlasType, token);

        if (atlas == null)
        {
            GameLogger.LogWarning(ELogCategory.Resource,
                $"아틀라스 로드 실패: {atlasType} ({GetAtlasKey(atlasType)})");
            return null;
        }

        Sprite sprite = atlas.GetSprite(spriteKey);
        if (sprite == null)
        {
            GameLogger.LogWarning(ELogCategory.Resource,
                $"아틀라스에 스프라이트 없음: {atlasType}/{spriteKey}");
        }
        return sprite;
    }

    /// <summary>아틀라스를 타입별로 로드해 런타임 캐시에 유지한다.</summary>
    public async UniTask<SpriteAtlas> LoadAtlasAsync(
        EAtlasType atlasType,
        CancellationToken token = default)
    {
        if (_atlasCache.TryGetValue(atlasType, out SpriteAtlas cached) && cached != null)
            return cached;

        string atlasKey = GetAtlasKey(atlasType);
        SpriteAtlas atlas = await LoadAsync<SpriteAtlas>(atlasKey, token);
        if (atlas != null)
            _atlasCache[atlasType] = atlas;
        return atlas;
    }

    public UniTask<Sprite> LoadSpriteAsync(string key, CancellationToken token = default)
        => LoadAsync<Sprite>(key, token);

    private static string GetAtlasKey(EAtlasType atlasType)
    {
        return atlasType switch
        {
            EAtlasType.CardIcon => AtlasKeys.CARD,
            EAtlasType.Common => AtlasKeys.COMMON,
            _ => throw new ArgumentOutOfRangeException(nameof(atlasType), atlasType, null),
        };
    }

    private IResourceLoader GetLoader(EResourceMode mode)
    {
        return mode == EResourceMode.Addressable
            ? (IResourceLoader)_addressableLoader
            : _resourceLoader;
    }

    // ─────────────────────────────────────────────
    // 통합 API (기본 로더 사용)
    // ─────────────────────────────────────────────

    public UniTask<T> LoadAsync<T>(string key, CancellationToken token = default) where T : UnityEngine.Object
        => GetLoader(_defaultMode).LoadAsync<T>(ResolveKey(key), token);

    public UniTask<T> LoadAsync<T>(CancellationToken token = default) where T : UnityEngine.Object
    {
        string key = typeof(T).Name;
        return GetLoader(_defaultMode).LoadAsync<T>(ResolveKey(key), token);
    }
    /// <summary>
    /// 컴포넌트 타입 로드 전용. 프리팹은 GameObject로 저장되므로,
    /// MonoBehaviour 파생(UIBase, PoolableMonoBehaviour 등)을 요청하면
    /// GameObject로 로드 후 GetComponent로 추출해 반환.
    /// Sprite·AudioClip·Material·ScriptableObject 등 GameObject가 아닌 에셋은
    /// LoadAsync를 그대로 사용 (이미 제네릭으로 모든 타입 지원).
    /// </summary>
    public async UniTask<T> LoadComponentAsync<T>(string key, CancellationToken token = default)
        where T : Component
    {
        var go = await LoadAsync<GameObject>(key, token);
        if (go == null) return null;

        var comp = go.GetComponent<T>();
        if (comp == null)
            GameLogger.LogWarning(ELogCategory.Resource,
                $"'{key}' 프리팹에 {typeof(T).Name} 컴포넌트 없음");
        return comp;
    }

    public async UniTask<T> LoadComponentAsync<T>(CancellationToken token = default)
    where T : Component
    {
        string key = typeof(T).Name;
        var go = await LoadAsync<GameObject>(key, token);
        if (go == null) return null;

        var comp = go.GetComponent<T>();
        if (comp == null)
            GameLogger.LogWarning(ELogCategory.Resource,
                $"'{key}' 프리팹에 {typeof(T).Name} 컴포넌트 없음");
        return comp;
    }

    /// <summary>
    /// LoadComponentAsync로 로드한 프리팹 해제 전용.
    /// LoadComponentAsync는 내부적으로 GameObject 캐시에 RefCount를 적재하므로,
    /// 반드시 이 메서드(또는 Release&lt;GameObject&gt;)로 해제해야 RefCount가 맞는다.
    /// ※ ReleaseAddressable&lt;컴포넌트타입&gt;으로 풀면 엉뚱한 타입 캐시를 뒤져
    ///    RefCount가 안 줄고 핸들이 누수되므로 사용 금지.
    /// </summary>
    public void ReleaseComponent(string key)
        => GetLoader(_defaultMode).Release<GameObject>(ResolveKey(key));

    /// <summary>LoadComponentAsync(Addressable 강제)로 로드한 프리팹 해제.</summary>
    public void ReleaseAddressableComponent(string key)
        => _addressableLoader.Release<GameObject>(ResolveKey(key));

    /// <summary>
    /// 키 테이블을 거쳐 실제 Addressable 주소로 변환.
    /// 테이블에 키가 있으면 그 Path(실제 경로)를 반환 → 폴더를 자유롭게 정리해도
    /// 코드는 단순 키("UITitle")만 쓰면 됨. 테이블에 없으면 키 그대로(직접 등록 키 호환).
    /// Resource 모드이거나 테이블 미로드 시엔 변환 없이 원본 키 사용.
    /// </summary>
    private string ResolveKey(string key)
    {
        if (_defaultMode != EResourceMode.Addressable) return key;
        if (!_keyTable.IsLoaded) return key;
        if (_keyTable.TryGet(key, out var entry) && !string.IsNullOrEmpty(entry.Path))
            return entry.Path; // 실제 경로를 Addressable 키로 사용 (폴더 등록 시 경로가 곧 키)
        return key;
    }

    public IEnumerator LoadCoroutine<T>(string key, Action<T> onComplete) where T : UnityEngine.Object
        => GetLoader(_defaultMode).LoadCoroutine(ResolveKey(key), onComplete);

    /// <summary>동기 로드. 기본 로더가 Resources일 때만 동작. Addressable 모드면 예외.</summary>
    public T LoadSync<T>(string key) where T : UnityEngine.Object
        => GetLoader(_defaultMode).LoadSync<T>(ResolveKey(key));

    public void Release<T>(string key) where T : UnityEngine.Object
        => GetLoader(_defaultMode).Release<T>(ResolveKey(key));

    // ─────────────────────────────────────────────
    // 명시적 로더 선택 (모드 무관 강제 지정)
    // ─────────────────────────────────────────────

    public UniTask<T> LoadAddressableAsync<T>(string key, CancellationToken token = default) where T : UnityEngine.Object
        => _addressableLoader.LoadAsync<T>(ResolveKey(key), token);

    public UniTask<T> LoadResourceAsync<T>(string path, CancellationToken token = default) where T : UnityEngine.Object
        => _resourceLoader.LoadAsync<T>(path, token);

    public IEnumerator LoadAddressableCoroutine<T>(string key, Action<T> onComplete) where T : UnityEngine.Object
        => _addressableLoader.LoadCoroutine(ResolveKey(key), onComplete);

    public IEnumerator LoadResourceCoroutine<T>(string path, Action<T> onComplete) where T : UnityEngine.Object
        => _resourceLoader.LoadCoroutine(path, onComplete);

    public T LoadResourceSync<T>(string path) where T : UnityEngine.Object
        => _resourceLoader.LoadSync<T>(path);

    public void ReleaseAddressable<T>(string key) where T : UnityEngine.Object
        => _addressableLoader.Release<T>(ResolveKey(key));

    public void ReleaseResource<T>(string path) where T : UnityEngine.Object
        => _resourceLoader.Release<T>(path);

    // ─────────────────────────────────────────────
    // 인스턴스화 헬퍼
    // ─────────────────────────────────────────────

    /// <summary>
    /// Addressables를 통해 직접 인스턴스화. ReleaseInstance로 해제 필수.
    /// (Addressables.InstantiateAsync는 자체 RefCount 관리)
    /// 단순 키도 ResolveKey로 실제 경로 변환 후 사용 (LoadAsync와 동일 정책).
    /// </summary>
    public async UniTask<T> InstantiateAsync<T>(string key = null, Transform parent = null,
        CancellationToken token = default)
    {
        if (key == null)
            key = typeof(T).Name;
        var handle = Addressables.InstantiateAsync(ResolveKey(key), parent);
        try
        {
            var instance = await handle.ToUniTask(cancellationToken: token);

            if (instance == null)
                return default;

            _instanceKeys[instance] = key;
            if (instance is T typedInstance)
                return typedInstance;
            var obj = instance.GetComponent<T>();
            return obj;
        }
        catch (OperationCanceledException)
        {
            // 취소돼도 핸들은 이미 진행 중일 수 있으므로 결과를 회수해 누수 방지
            if (handle.IsValid())
            {
                if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
                    Addressables.ReleaseInstance(handle.Result);
                else
                    Addressables.Release(handle);
            }
            throw;
        }
    }

    /// <summary>InstantiateAsync로 생성한 인스턴스 해제.</summary>
    public void ReleaseInstance(GameObject instance)
    {
        if (instance == null) return;

        _instanceKeys.Remove(instance);

        if (!Addressables.ReleaseInstance(instance))
            UnityEngine.Object.Destroy(instance);
    }

    // ─────────────────────────────────────────────
    // 일괄 처리
    // ─────────────────────────────────────────────

    /// <summary>여러 키 동시 로드. 실패한 항목은 경고 로그 후 결과에서 제외.</summary>
    public async UniTask<List<T>> LoadAllAsync<T>(IReadOnlyList<string> keys, CancellationToken token = default)
        where T : UnityEngine.Object
    {
        var loader = GetLoader(_defaultMode);
        var tasks = new List<UniTask<T>>(keys.Count);

        for (int i = 0; i < keys.Count; i++)
            tasks.Add(loader.LoadAsync<T>(ResolveKey(keys[i]), token));

        var results = await UniTask.WhenAll(tasks);

        var filtered = new List<T>(results.Length);
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i] != null)
                filtered.Add(results[i]);
            else
                GameLogger.LogWarning(ELogCategory.Resource, $"일괄 로드 중 null 제외: {keys[i]}");
        }

        return filtered;
    }

    public void ReleaseAll()
    {
        _addressableLoader?.ReleaseAll();
        _resourceLoader?.ReleaseAll();
        _instanceKeys.Clear();
    }

    /// <summary>전역 미사용 에셋 회수. 무거운 작업이므로 씬 전환 시점 등에 제한적 호출.</summary>
    public UniTask UnloadUnusedAssets(CancellationToken token = default)
        => Resources.UnloadUnusedAssets().ToUniTask(cancellationToken: token);

    // ─────────────────────────────────────────────
    // 모드 전환
    // ─────────────────────────────────────────────

    public void SetDefaultMode(EResourceMode mode)
    {
        _defaultMode = mode;
        GameLogger.Log(ELogCategory.Resource, $"기본 로더 모드 전환: {mode}");
    }

    // ─────────────────────────────────────────────
    // 키 테이블 기반 조회 (addressableMap 통합)
    // ─────────────────────────────────────────────

    /// <summary>키가 매핑 테이블에 존재하는지 검증. 오타·누락 사전 감지.</summary>
    public bool HasKey(string key) => _keyTable.HasKey(key);

    /// <summary>키의 매핑 정보(그룹·종류·경로) 조회.</summary>
    public bool TryGetMapEntry(string key, out AddressableMapEntry entry)
        => _keyTable.TryGet(key, out entry);

    /// <summary>특정 그룹의 모든 키 (예: 모든 UI 프리팹 키).</summary>
    public IReadOnlyList<string> GetKeysInGroup(EAddressableGroup group)
        => _keyTable.GetKeys(group);

    /// <summary>프리웜 대상 키 목록. PoolManager가 부팅 시 호출.</summary>
    public List<string> GetPrewarmKeys(EAddressableGroup? group = null)
        => _keyTable.GetPrewarmKeys(group);

    /// <summary>키 존재 검증 후 로드. 미존재 시 경고 + null (디버깅 편의).</summary>
    public async UniTask<T> LoadVerifiedAsync<T>(string key, CancellationToken token = default)
        where T : UnityEngine.Object
    {
        if (_loadKeyTable && _keyTable.IsLoaded && !_keyTable.HasKey(key))
        {
            GameLogger.LogWarning(ELogCategory.Resource, $"매핑에 없는 키: {key}");
            return null;
        }
        return await LoadAsync<T>(key, token);
    }

    /// <summary>
    /// Preload 폴더에 있는(=Prewarm 플래그) 키들을 미리 메모리에 로드.
    /// 풀링 대상이 아닌 일반 에셋(데이터·이미지·오디오 등)을 게임 시작 시 캐싱하는 용도.
    /// (풀 프리웜은 PoolManager.PrewarmFromTableAsync가 별도 담당)
    ///
    /// group 지정 시 해당 그룹만, null이면 전체 Preload 키 대상.
    /// </summary>
    public async UniTask PreloadFromTableAsync(EAddressableGroup? group = null,
        IProgress<float> progress = null, CancellationToken token = default)
    {
        if (!_keyTable.IsLoaded)
        {
            GameLogger.LogWarning(ELogCategory.Resource, "PreloadFromTable: 키 테이블 미로드");
            return;
        }

        var keys = _keyTable.GetPrewarmKeys(group);
        if (keys.Count == 0) return;

        for (int i = 0; i < keys.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!_keyTable.TryGet(keys[i], out var entry)) continue;

            // 종류에 맞는 타입으로 로드해서 캐시에 적재 (핸들은 ResourceManager가 보유)
            switch (entry.Kind)
            {
                case EAssetKind.Sprite:
                    await LoadAddressableAsync<Sprite>(keys[i], token);
                    break;
                case EAssetKind.SpriteAtlas:
                    await LoadAddressableAsync<SpriteAtlas>(keys[i], token);
                    break;
                case EAssetKind.AudioClip:
                    await LoadAddressableAsync<AudioClip>(keys[i], token);
                    break;
                case EAssetKind.ScriptableObject:
                    await LoadAddressableAsync<ScriptableObject>(keys[i], token);
                    break;
                case EAssetKind.JsonData:
                case EAssetKind.Csv:
                    await LoadAddressableAsync<TextAsset>(keys[i], token);
                    break;
                // Prefab은 풀링 경로(PoolManager)에서 처리하므로 여기선 스킵
                case EAssetKind.Prefab:
                default:
                    break;
            }

            progress?.Report((i + 1f) / keys.Count);
        }

        GameLogger.Log(ELogCategory.Resource, $"일반 에셋 프리로드 완료: {keys.Count}개 검사");
    }
}
