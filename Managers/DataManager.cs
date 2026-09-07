// =================================================================
// [스크립트 목적]  Data 폴더(GameData 라벨)의 SO/CSV/JSON을 일괄 로드 → 자동 파싱 →
//                 불변 원본 데이터 테이블 구축. 외부는 GetData(참조)/CreateData(복제)로 사용.
// [절대 명제]      1) 원본 데이터는 로드 후 이 매니저 안에서만 보관, 외부 직접 노출 X
//                  2) 원본은 불변. 게임은 CreateData 복제본으로 동작 (원본 안 바뀜)
//                  3) 외부는 타입+키로 조회: GetData<T>(key) / CreateData<T>(key)
//                  4) Data 폴더 하위 SO/CSV/JSON 폴더 + 타입별 폴더링 자유
//                  5) 파일만 넣으면 자동 로드·파싱 (파일명 = 클래스명)
// [주요 변수]      - _dataTables : Type → (key → 원본 데이터). SO·POCO 통합. 외부 노출 금지
// [복제 우선순위]  ICloneableData<T>.Clone() → SO는 Instantiate → POCO는 JSON 재직렬화(자동)
// [의존 관계]      - ManagerBase<DataManager>, ICloneableData<T>, DataParser
// [InitOrder]      32 (ResourceManager(30) 이후)
// =================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public class DataManager : ManagerBase<DataManager>
{
    public override int InitOrder => 32;          // ResourceManager(30) 이후
    public override bool IsCritical => true;       // 게임 데이터는 필수

    [Header("데이터 로드 설정")]
    [Tooltip("SO/CSV/JSON 데이터 에셋을 묶은 Addressables Label")]
    [SerializeField] private string _dataLabel = "GameData";

    [Tooltip("CSV·JSON에서 키로 사용할 공통 컬럼/필드명")]
    [SerializeField] private string _idKey = "Id";

    [Tooltip("자동 프리로드. false면 외부에서 PreloadAllAsync 명시 호출")]
    [SerializeField] private bool _autoPreload = true;

    // 통합 원본 테이블. Type → (key → 원본). SO든 POCO든 동일하게 보관.
    // [명제 1] 외부에 절대 직접 노출하지 않음 (GetData는 SO=참조주의/POCO=복제본 반환)
    [SerializeField]  private Dictionary<Type, Dictionary<string, object>> _dataTables = new();

    // 파일명 → Type 사전. [GameData] 표식이 붙은 게임 어셈블리 클래스만 수집(전수 스캔 X).
    // 부팅 시 1회 구축, 이후 전부 O(1) 조회. null이면 아직 미구축.
    [SerializeField] private Dictionary<string, Type> _dataTypeMap;

    // 라벨 일괄 로드 핸들 (로드된 에셋 참조 유지용)
    private AsyncOperationHandle _loadedHandle;
    private bool _hasHandle;

    public bool IsLoaded { get; private set; }
    public event Action OnAllDataLoaded;

    // ─────────────────────────────────────────────
    // 초기화
    // ─────────────────────────────────────────────

    protected override async UniTask OnInitializeAsync(CancellationToken token)
    {
        if (_autoPreload)
            await PreloadAllAsync(null, token);
    }

    /// <summary>
    /// GameData 라벨의 모든 에셋(SO/TextAsset)을 일괄 로드 후 자동 파싱.
    /// 에셋 0개여도 정상 종료. 재호출 시 기존 데이터/핸들 정리 후 재로드.
    /// </summary>
    public async UniTask PreloadAllAsync(IProgress<float> progress, CancellationToken token = default)
    {
        // 재호출 방어: 이전 핸들·테이블 정리 (핸들 누수 + 중복 등록 방지)
        ReleaseLoadedHandle();
        _dataTables.Clear();
        IsLoaded = false;

        // 라벨 존재 사전 체크 (0개면 InvalidKeyException 방지 → 빈 상태 정상 종료)
        var locHandle = Addressables.LoadResourceLocationsAsync(_dataLabel);
        var locations = await locHandle.ToUniTask(cancellationToken: token);
        bool hasAny = locations != null && locations.Count > 0;
        Addressables.Release(locHandle);

        if (!hasAny)
        {
            IsLoaded = true;
            OnAllDataLoaded?.Invoke();
            GameLogger.LogWarning(ELogCategory.Data,
                $"데이터 없음 (라벨 '{_dataLabel}'에 에셋 0개) — 로드 스킵");
            return;
        }

        // 라벨의 모든 에셋을 UnityEngine.Object로 일괄 로드 (SO + TextAsset 혼재)
        var handle = Addressables.LoadAssetsAsync<UnityEngine.Object>(_dataLabel, null);
        var assets = await handle.ToUniTask(
            Progress.Create<float>(p => progress?.Report(p)),
            cancellationToken: token);

        int soCount = 0, textCount = 0;

        foreach (var asset in assets)
        {
            if (asset == null) continue;
            token.ThrowIfCancellationRequested();

            switch (asset)
            {
                case ScriptableObject so:
                    RegisterSO(so);
                    soCount++;
                    break;

                case TextAsset text:
                    RegisterTextAsset(text);
                    textCount++;
                    break;
            }
        }

        // 라벨 핸들은 로드된 SO 참조를 유지하므로 게임 수명 내내 보관.
        _loadedHandle = handle;
        _hasHandle = true;

        IsLoaded = true;
        OnAllDataLoaded?.Invoke();
        GameLogger.Log(ELogCategory.Data,
            $"데이터 로드 완료 — {_dataTables.Count}개 타입 (SO에셋:{soCount} 텍스트:{textCount})");

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        VerifyGameDataCoverage();
#endif
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    /// <summary>
    /// [GameData] 표식은 붙었으나 실제 로드된 데이터가 없는 타입을 경고(개발 빌드 한정).
    /// "표식만 달고 CSV/JSON 파일을 안 넣은" 누락 사고를 부팅 단계에서 조기 발견.
    /// </summary>
    private void VerifyGameDataCoverage()
    {
        EnsureDataTypeMap();
        foreach (var type in _dataTypeMap.Values.Distinct())
        {
            // SO는 RegisterSO 경로(표식 무관)라 여기선 TextAsset 대상만 의미 있음.
            // 테이블에 항목이 0개면 데이터 파일이 없는 것으로 간주해 경고.
            if (!_dataTables.TryGetValue(type, out var table) || table.Count == 0)
            {
                GameLogger.LogWarning(ELogCategory.Data,
                    $"[GameData] '{type.Name}'에 표식은 있으나 로드된 데이터가 없습니다 " +
                    $"— 파일명 '{type.Name}'의 CSV/JSON이 '{_dataLabel}' 라벨에 있는지 확인하세요.");
            }
        }
    }
#endif

    protected override UniTask OnShutdownInternalAsync()
    {
        // 앱 종료 중에는 GameData Addressables 핸들과 테이블을 유지한다.
        // 프로세스 종료 직전의 늦은 콜백이 데이터 SO를 참조할 수 있고,
        // 여기서 먼저 Release해도 메모리 회수 이득 없이 수명 충돌만 생긴다.
        // 런타임 재로드 시 정리는 PreloadAllAsync의 ReleaseLoadedHandle 경로가 담당한다.
        return UniTask.CompletedTask;
    }

    private void ReleaseLoadedHandle()
    {
        if (_hasHandle && _loadedHandle.IsValid())
            Addressables.Release(_loadedHandle);
        _hasHandle = false;
    }

    // ─────────────────────────────────────────────
    // 등록: SO
    // ─────────────────────────────────────────────

    private void RegisterSO(ScriptableObject so)
    {
        // key = SO 에셋 이름. 원본 SO 참조를 통합 테이블에 보관.
        GetOrCreateTable(so.GetType())[so.name] = so;
    }

    // ─────────────────────────────────────────────
    // 등록: TextAsset (CSV/JSON 자동 판별 → DataParser 위임)
    // ─────────────────────────────────────────────

    private void RegisterTextAsset(TextAsset text)
    {
        // 파일명 = 클래스명 규칙으로 타입 탐색
        var type = ResolveType(text.name);
        if (type == null)
        {
            GameLogger.LogWarning(ELogCategory.Data,
                $"[Text] '{text.name}'에 대응하는 클래스 없음 — 스킵 (파일명=클래스명 규칙)");
            return;
        }

        // 파싱은 DataParser에 위임. 결과(key, 인스턴스)를 통합 테이블에 등록.
        bool isJson = IsJsonContent(text.text);
        var entries = isJson
            ? DataParser.ParseJson(text.text, type, _idKey, text.name)
            : DataParser.ParseCsv(text.text, type, _idKey, text.name);

        var table = GetOrCreateTable(type);
        foreach (var entry in entries)
            table[entry.Id] = entry.Instance;

        GameLogger.Log(ELogCategory.Data,
            $"[{(isJson ? "JSON" : "CSV")}] {type.Name} 로드: {entries.Count}개 ({text.name})");
    }

    /// <summary>첫 비공백 문자가 '[' 또는 '{' 면 JSON으로 간주.</summary>
    private static bool IsJsonContent(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (char.IsWhiteSpace(c)) continue;
            return c == '[' || c == '{';
        }
        return false;
    }

    // ─────────────────────────────────────────────
    // 조회: GetData (원본 참조 — 읽기 전용)
    // ─────────────────────────────────────────────

    /// <summary>
    /// [명제 2,3] 타입+키로 원본 데이터 조회.
    /// 반환값은 불변 원본이므로 수정 금지 — 수정이 필요하면 CreateData 사용.
    /// </summary>
    public T GetData<T>(string key) where T : class
    {
        if (_dataTables.TryGetValue(typeof(T), out var table) && table.TryGetValue(key, out var obj))
            return obj as T;

        GameLogger.LogWarning(ELogCategory.Data, $"데이터 없음: {typeof(T).Name}/{key}");
        return null;
    }

    /// <summary>타입 전체 원본 목록 반환 (읽기 전용으로 다룰 것).</summary>
    public List<T> GetAllData<T>() where T : class
    {
        var result = new List<T>();
        if (_dataTables.TryGetValue(typeof(T), out var table))
            foreach (var obj in table.Values)
                if (obj is T t) result.Add(t);
        return result;
    }

    /// <summary>데이터 존재 여부.</summary>
    public bool HasData<T>(string key) where T : class
        => _dataTables.TryGetValue(typeof(T), out var table) && table.ContainsKey(key);

    /// <summary>타입의 키 목록 반환.</summary>
    public List<string> GetKeys<T>() where T : class
    {
        var result = new List<string>();
        if (_dataTables.TryGetValue(typeof(T), out var table))
            result.AddRange(table.Keys);
        return result;
    }

    // ─────────────────────────────────────────────
    // 복제: CreateData (게임에서 쓸 복사본 — 원본 불변)
    // ─────────────────────────────────────────────

    /// <summary>
    /// [명제 2] 원본을 복제해 수정 가능한 복사본 반환. 원본은 절대 변하지 않음.
    /// 복제 우선순위: ICloneableData<T>.Clone() → SO는 Instantiate → POCO는 JSON 재직렬화(자동).
    /// </summary>
    public T CreateData<T>(string key) where T : class
    {
        if (!_dataTables.TryGetValue(typeof(T), out var table) || !table.TryGetValue(key, out var src))
        {
            GameLogger.LogWarning(ELogCategory.Data, $"데이터 없음(복제 실패): {typeof(T).Name}/{key}");
            return null;
        }

        return CloneObject(src) as T;
    }

    /// <summary>타입 전체를 복제한 목록 반환.</summary>
    public List<T> CreateDataAll<T>() where T : class
    {
        var result = new List<T>();
        if (_dataTables.TryGetValue(typeof(T), out var table))
            foreach (var src in table.Values)
                if (CloneObject(src) is T t) result.Add(t);
        return result;
    }

    /// <summary>복제 우선순위 적용한 단일 객체 깊은 복사.</summary>
    private static object CloneObject(object src)
    {
        if (src == null) return null;

        // ① ICloneableData<T> 구현 시 그것을 최우선 (성능·정확성 최고)
        //    제네릭 인터페이스라 리플렉션으로 Clone 호출
        var srcType = src.GetType();
        var cloneInterface = typeof(ICloneableData<>).MakeGenericType(srcType);
        if (cloneInterface.IsInstanceOfType(src))
        {
            var method = cloneInterface.GetMethod("Clone");
            return method?.Invoke(src, null);
        }

        // ② ScriptableObject → Instantiate (참조 필드는 공유될 수 있음 — 경고)
        if (src is ScriptableObject so)
        {
            GameLogger.LogWarning(ELogCategory.Data,
                $"[{srcType.Name}] ICloneableData 미구현 — Instantiate 폴백 (참조 필드 공유 주의)");
            return UnityEngine.Object.Instantiate(so);
        }

        // ③ POCO → JSON 재직렬화로 자동 깊은 복사 (Newtonsoft)
        try
        {
            string json = JsonConvert.SerializeObject(src);
            return JsonConvert.DeserializeObject(json, srcType);
        }
        catch (Exception e)
        {
            GameLogger.LogError(ELogCategory.Data,
                $"[{srcType.Name}] 자동 깊은 복사 실패: {e.Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────
    // 유틸리티
    // ─────────────────────────────────────────────

    private Dictionary<string, object> GetOrCreateTable(Type type)
    {
        if (!_dataTables.TryGetValue(type, out var table))
        {
            table = new Dictionary<string, object>();
            _dataTables[type] = table;
        }
        return table;
    }

    /// <summary>파일명(=클래스명)으로 데이터 Type 조회. [GameData] 수집 사전에서 O(1) 매칭.</summary>
    private Type ResolveType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;

        EnsureDataTypeMap();
        return _dataTypeMap.TryGetValue(typeName, out var type) ? type : null;
    }

    /// <summary>
    /// 게임 코드 어셈블리에서 [GameData] 표식이 붙은 클래스만 수집해 "이름 → Type" 사전 1회 구축.
    /// 전체 어셈블리 스캔(AppDomain) 대신 DataManager가 속한 어셈블리 1개만 조회한다.
    /// 동명 클래스가 있으면(네임스페이스 다름) FullName 키를 함께 등록해 충돌을 회피한다.
    /// </summary>
    private void EnsureDataTypeMap()
    {
        if (_dataTypeMap != null) return;
        _dataTypeMap = new Dictionary<string, Type>();

        // 데이터 클래스는 DataManager와 같은 게임 어셈블리에 존재한다는 전제
        var gameAssembly = typeof(DataManager).Assembly;

        Type[] types;
        try { types = gameAssembly.GetTypes(); }
        catch (ReflectionTypeLoadException e)
        {
            // 일부 타입 로드 실패해도 로드된 것만 사용 (빌드 환경 방어)
            types = e.Types.Where(t => t != null).ToArray();
        }

        foreach (var t in types)
        {
            // [GameData] 표식이 붙은 구체 클래스만 대상
            if (!t.IsClass || t.IsAbstract) continue;
            if (!t.IsDefined(typeof(GameDataAttribute), inherit: false)) continue;

            // FullName(네임스페이스 포함)은 항상 고유 — 함께 등록(명시적 FullName 파일명 대비)
            if (t.FullName != null) _dataTypeMap.TryAdd(t.FullName, t);

            // 단순명 등록. 동명 충돌 시 경고 후 단순명 키 제거(FullName으로만 매칭하도록)
            if (_dataTypeMap.TryGetValue(t.Name, out var existing) && existing != t)
            {
                GameLogger.LogWarning(ELogCategory.Data,
                    $"[DataTypeMap] [GameData] 동명 클래스 충돌: '{t.Name}' " +
                    $"({existing.FullName} vs {t.FullName}). 파일명을 FullName으로 지정하세요.");
                _dataTypeMap.Remove(t.Name);
            }
            else
            {
                _dataTypeMap[t.Name] = t;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        GameLogger.Log(ELogCategory.Data,
            $"[DataTypeMap] [GameData] 데이터 타입 {_dataTypeMap.Values.Distinct().Count()}개 수집 완료");
#endif
    }
}
