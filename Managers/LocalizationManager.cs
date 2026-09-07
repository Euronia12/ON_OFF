// =================================================================
// [스크립트 목적]  키 기반 다국어 텍스트 관리. SO 테이블 + 언어 전환 + 포맷팅
// [주요 변수]      - _table         : key → (언어 → 텍스트)
//                  - _currentLang   : 현재 언어
// [의존 관계]      - ManagerBase<LocalizationManager>, SettingsManager
// [InitOrder]      75
// =================================================================
using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Threading;
using TMPro;
using UnityEngine;

// 로컬라이즈 엔트리 SO (구글 시트 임포트 대상)
[CreateAssetMenu(fileName = "LocalizationEntry", menuName = "Framework/Localization Entry")]
public class LocalizationEntry : ScriptableObject
{
    [Serializable]
    public class Translation
    {
        public SystemLanguage Language;
        [TextArea] public string Text;
    }

    [Tooltip("로컬라이즈 키 (예: UI_START_BUTTON)")]
    public string Key;

    [Tooltip("언어별 번역")]
    public List<Translation> Translations = new();
}

// 로컬라이즈 데이터 로드 소스 선택
public enum ELocalizationSource
{
    // StringChart CSV를 DataManager가 로드 → 여기서 캐싱 (권장, SO 폭증 없음)
    StringChart,
    // LocalizationEntry SO들을 Addressables 라벨로 직접 로드 (구버전 방식)
    EntryAsset,
    // 외부(DataManager 등)가 RegisterEntries를 직접 호출 (자체 로드 안 함)
    External,
}

public class LocalizationManager : ManagerBase<LocalizationManager>
{
    public override int InitOrder => 75;

    [Header("로컬라이즈 설정")]
    [Tooltip("로컬라이즈 데이터 로드 소스.\n" +
             "StringChart : 구글 시트 CSV(StringChart.csv)를 DataManager가 로드 → 캐싱 (권장)\n" +
             "EntryAsset  : LocalizationEntry SO를 Addressables 라벨로 로드 (구버전)\n" +
             "External    : 외부에서 RegisterEntries 직접 호출")]
    [SerializeField] private ELocalizationSource _source = ELocalizationSource.StringChart;

    [Tooltip("로컬라이즈 엔트리 SO들을 묶은 Addressables Label (EntryAsset 모드에서만 사용)")]
    [SerializeField] private string _localizationLabel = "Localization";

    [Tooltip("번역 누락 시 폴백 언어")]
    [SerializeField] private SystemLanguage _fallbackLanguage = SystemLanguage.English;

    [Header("폰트 전환")]
    [Tooltip("언어별 TMP 폰트 매핑 테이블.\n언어 전환 시 UI가 GetCurrentFont()로 조회해 폰트 교체")]
    [SerializeField] private LanguageFontTableSO _fontTable;

    [Tooltip("이 게임이 지원하는 언어 목록. 시스템 언어 감지 시 이 목록에 있으면 채택, 없으면 폴백.\n확장 시 여기에 언어 추가 + 번역 데이터 채우면 됨")]
    [SerializeField]
    private List<SystemLanguage> _supportedLanguages = new()
    {
        SystemLanguage.Korean,
        SystemLanguage.English,
        SystemLanguage.ChineseSimplified,
        SystemLanguage.ChineseTraditional,
        SystemLanguage.Japanese,
        SystemLanguage.German,
    };

    // key → (언어 → 텍스트)
    private readonly Dictionary<string, Dictionary<SystemLanguage, string>> _table = new();
    private readonly CardDescriptionBuilder _cardDescBuilder = new CardDescriptionBuilder();
    private SystemLanguage _currentLang = SystemLanguage.Korean;

    public SystemLanguage CurrentLanguage => _currentLang;

    /// <summary>언어 전환 시 발행. UI가 구독해 텍스트 갱신.</summary>
    public event Action<SystemLanguage> OnLanguageChanged;

    protected override async UniTask OnInitializeAsync(CancellationToken token)
    {
        // 언어 결정: 신규 유저(저장값 없음) → 시스템 언어 자동 감지, 기존 유저 → 저장값
        ResolveInitialLanguage();

        // 데이터 소스에 따라 로드 분기 (External은 RegisterEntries 외부 호출 대기 → 자체 로드 안 함)
        switch (_source)
        {
            case ELocalizationSource.StringChart:
                LoadFromStringChart();
                break;

            case ELocalizationSource.EntryAsset:
                await LoadEntriesAsync(token);
                break;

            case ELocalizationSource.External:
                // 외부(DataManager 통합 등)가 RegisterEntries/RegisterStringCharts 직접 호출
                break;
        }
    }

    // ─────────────────────────────────────────────
    // StringChart 캐싱 (DataManager가 로드한 CSV → 테이블 1회 구축)
    // ─────────────────────────────────────────────

    // DataManager(InitOrder 32)는 이 매니저(75)보다 먼저 초기화 완료되므로
    // 이 시점엔 StringChart가 이미 로드돼 있음. 동기 조회로 충분.
    private void LoadFromStringChart()
    {
        if (!DataManager.HasInstance)
        {
            GameLogger.LogWarning(ELogCategory.System,
                "Localization: DataManager 없음 — StringChart 로드 스킵");
            return;
        }

        var charts = DataManager.Instance.GetAllData<StringChart>();
        if (charts == null || charts.Count == 0)
        {
            GameLogger.LogWarning(ELogCategory.System,
                "Localization: StringChart 데이터 0개 — 키 테이블 비어있음 (CSV 임포트 확인)");
            return;
        }

        RegisterStringCharts(charts);
        GameLogger.Log(ELogCategory.System, $"Localization(StringChart) 로드: {_table.Count}개 키");
    }

    /// <summary>StringChart 목록을 key → (언어 → 텍스트) 테이블로 등록. 중복 키는 덮어씀.</summary>
    public void RegisterStringCharts(IEnumerable<StringChart> charts)
    {
        foreach (var chart in charts)
        {
            if (chart == null || string.IsNullOrEmpty(chart.Id)) continue;
            _table[chart.Id] = chart.ToLanguageMap();
        }
    }

    // 초기 언어 결정 로직
    // - 유저가 직접 선택한 적 있음(HasLanguageBeenSet) → 저장된 언어 사용
    // - 신규 유저 → 시스템 언어(Application.systemLanguage) 감지, 지원 목록에 있으면 채택, 없으면 폴백
    private void ResolveInitialLanguage()
    {
        if (!SettingsManager.HasInstance)
        {
            // SettingsManager 없으면 시스템 언어 단독 판단
            _currentLang = ResolveSystemLanguage();
            return;
        }

        var settings = SettingsManager.Instance.Current;

        if (settings.HasLanguageBeenSet)
        {
            // 기존 유저: 저장값이 지원 목록에 있으면 사용, 아니면 폴백
            SystemLanguage savedLanguage = SettingsManager.ToSystemLanguage(settings.Language);
            _currentLang = IsSupported(savedLanguage) ? savedLanguage : _fallbackLanguage;
        }
        else
        {
            // 신규 유저: 시스템 언어 감지 후 즉시 저장 확정
            // (HasLanguageBeenSet=true → 이후 OS 언어가 바뀌어도 게임 언어 고정, 예측 가능)
            _currentLang = ResolveSystemLanguage();
            SettingsManager.Instance.SetLanguage(SettingsManager.ToGameLanguage(_currentLang)); // HasLanguageBeenSet 기록 + 즉시 영속화
        }
    }

    // 시스템 언어를 지원 목록과 대조 → 지원하면 그대로, 아니면 폴백 언어 반환
    private SystemLanguage ResolveSystemLanguage()
    {
        var sysLang = Application.systemLanguage;
        return IsSupported(sysLang) ? sysLang : _fallbackLanguage;
    }

    // 지원 언어 여부 (List foreach는 struct enumerator → GC 0)
    public bool IsSupported(SystemLanguage lang)
    {
        foreach (var l in _supportedLanguages)
        {
            if (l == lang) return true;
        }
        return false;
    }

    /// <summary>지원 언어 목록 (읽기 전용 조회용. 옵션 UI 드롭다운 구성 등에 사용).</summary>
    public IReadOnlyList<SystemLanguage> SupportedLanguages => _supportedLanguages;

    private async UniTask LoadEntriesAsync(CancellationToken token)
    {
        if (!ResourceManager.HasInstance)
        {
            GameLogger.LogWarning(ELogCategory.System, "Localization: ResourceManager 없음");
            return;
        }

        // 빈 라벨 방어: location 먼저 조회 (0개면 LoadAssetsAsync가 예외를 던지므로)
        var locHandle = UnityEngine.AddressableAssets.Addressables
            .LoadResourceLocationsAsync(_localizationLabel, typeof(LocalizationEntry));
        var locations = await locHandle.ToUniTask(cancellationToken: token);
        bool hasAny = locations != null && locations.Count > 0;
        UnityEngine.AddressableAssets.Addressables.Release(locHandle);

        if (!hasAny)
        {
            GameLogger.LogWarning(ELogCategory.System,
                $"Localization 엔트리 없음 (라벨 '{_localizationLabel}' 0개) — 로드 스킵");
            return;
        }

        var handle = UnityEngine.AddressableAssets.Addressables
            .LoadAssetsAsync<LocalizationEntry>(_localizationLabel, null);
        var entries = await handle.ToUniTask(cancellationToken: token);

        RegisterEntries(entries);
        GameLogger.Log(ELogCategory.System, $"Localization 로드: {_table.Count}개 키");
    }

    /// <summary>외부에서 로드한 엔트리 등록 (DataManager 통합 시).</summary>
    public void RegisterEntries(IEnumerable<LocalizationEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Key)) continue;

            if (!_table.TryGetValue(entry.Key, out var langMap))
            {
                langMap = new Dictionary<SystemLanguage, string>();
                _table[entry.Key] = langMap;
            }

            foreach (var tr in entry.Translations)
                langMap[tr.Language] = tr.Text;
        }
    }

    // ─────────────────────────────────────────────
    // 조회
    // ─────────────────────────────────────────────

    /// <summary>키로 현재 언어 텍스트 조회. 없으면 폴백 → 키 자체 반환.</summary>
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        if (_table.TryGetValue(key, out var langMap))
        {
            if (langMap.TryGetValue(_currentLang, out var text))
                return NormalizeNewlines(text);
            if (langMap.TryGetValue(_fallbackLanguage, out var fallback))
                return NormalizeNewlines(fallback);
        }

        // 누락 키 경고. 같은 키 반복 조회 시 로그 스팸 방지(최초 1회만).
        // LogWarning은 [Conditional] → 릴리스 빌드에선 호출·인자 생성 모두 제거되어 GC 0.
        WarnMissingKeyOnce(key);
        return key; // 키 자체 반환 (디버깅 용이)
    }

    // CSV 셀에 입력한 리터럴 "\n"(역슬래시+n)을 실제 개행으로 변환. 없으면 원문 그대로(GC 0).
    private static string NormalizeNewlines(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0) return text;
        return text.Replace("\\n", "\n");
    }

    // 이미 경고한 누락 키 추적(에디터/개발 빌드 한정). 릴리스에선 컴파일 제외.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private readonly HashSet<string> _warnedMissingKeys = new();
#endif

    [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    private void WarnMissingKeyOnce(string key)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (!_warnedMissingKeys.Add(key)) return; // 이미 경고한 키면 스킵
        GameLogger.LogWarning(ELogCategory.UI, $"로컬라이즈 키 없음: {key}");
#endif
    }

    /// <summary>포맷 인자 적용. string.Format 사용 (호출 빈도 낮은 UI 텍스트 한정).</summary>
    public string Get(string key, params object[] args)
    {
        string template = Get(key);
        try { return string.Format(template, args); }
        catch { return template; }
    }

    public bool HasKey(string key) => _table.ContainsKey(key);

    // ─────────────────────────────────────────────
    // 폰트 조회 (언어별 TMP 폰트 전환용)
    // ─────────────────────────────────────────────

    /// <summary>현재 언어의 TMP 폰트 반환. 폰트 테이블 미할당 시 null.</summary>
    public TMP_FontAsset GetCurrentFont()
    {
        if (_fontTable == null)
        {
            WarnNoFontTableOnce();
            return null;
        }
        return _fontTable.GetFont(_currentLang);
    }

    /// <summary>지정 언어의 TMP 폰트 반환. 폰트 테이블 미할당 시 null.</summary>
    public TMP_FontAsset GetFont(SystemLanguage lang)
    {
        return _fontTable != null ? _fontTable.GetFont(lang) : null;
    }

    // 폰트 테이블 미할당 경고 (개발 빌드 한정, 최초 1회)
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private bool _warnedNoFontTable;
#endif

    [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    private void WarnNoFontTableOnce()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (_warnedNoFontTable) return;
        _warnedNoFontTable = true;
        GameLogger.LogWarning(ELogCategory.UI,
            "LocalizationManager: 폰트 테이블(_fontTable) 미할당 — 폰트 전환 동작 안 함");
#endif
    }

    public string GetCardDescription(Card card, CardStatCalculator calculator)
    {
        return GetCardDescription(card, calculator, default);
    }

    public string GetCardDescription(Card card, CardStatCalculator calculator, CardDescriptionContext descriptionContext)
    {
        if (card?.Data == null || string.IsNullOrEmpty(card.Data.DescriptionKey))
        {
            return string.Empty;
        }

        string template = Get(card.Data.DescriptionKey);
        return _cardDescBuilder.Build(card, calculator, template, descriptionContext);
    }

    public string GetCardDescription(CardRuntimeData data)
    {
        if (data == null || string.IsNullOrEmpty(data.DescriptionKey))
        {
            return string.Empty;
        }

        string template = Get(data.DescriptionKey);
        return _cardDescBuilder.Build(data, template);
    }

    // ─────────────────────────────────────────────
    // 언어 전환
    // ─────────────────────────────────────────────

    // 언어 변경 시 갱신할 대상 (UIBase·비UIBase 무관). HashSet으로 중복 방지
    private readonly HashSet<ILocalizable> _localizables = new();

    /// <summary>언어 변경 갱신 대상 등록. 보통 OnEnable에서 호출.</summary>
    public void Register(ILocalizable target)
    {
        if (target == null) return;
        _localizables.Add(target);
        SafeLocalize(target); // 등록 즉시 현재 언어로 1회 갱신 (예외 격리)
    }

    /// <summary>등록 해제. 반드시 OnDisable/OnDestroy에서 호출 (누수 방지).</summary>
    public void Unregister(ILocalizable target)
    {
        if (target == null) return;
        _localizables.Remove(target);
    }

    public void SetLanguage(SystemLanguage lang)
    {
        // 지원하지 않는 언어 요청 방어 (옵션 UI는 SupportedLanguages만 노출하면 정상적으론 안 들어옴)
        if (!IsSupported(lang))
        {
            GameLogger.LogWarning(ELogCategory.UI, $"지원하지 않는 언어 요청: {lang} → 폴백({_fallbackLanguage})");
            lang = _fallbackLanguage;
        }

        if (_currentLang == lang) return;
        _currentLang = lang;

        // 등록된 모든 대상에 갱신 브로드캐스트 (순회 중 Unregister 안전하게 버퍼 경유)
        // 한 대상의 예외가 나머지 갱신·언어 전환 전체를 막지 않도록 개별 격리
        var buffer = _localizablesBuffer();
        for (int i = 0; i < buffer.Count; i++)
            SafeLocalize(buffer[i]);

        if (UIManager.HasInstance)
            UIManager.Instance.RefreshManagedLocalization();

        // 언어 변경 이벤트 발행 (구독형 갱신 대상용)
        OnLanguageChanged?.Invoke(lang);

        // SettingsManager 동기화 (SetLanguage가 HasLanguageBeenSet 기록 + 즉시 영속화함)
        if (SettingsManager.HasInstance)
        {
            SettingsManager.Instance.SetLanguage(SettingsManager.ToGameLanguage(lang));
        }
    }

    public void RefreshAll()
    {
        var buffer = _localizablesBuffer();
        for (int i = 0; i < buffer.Count; i++)
            SafeLocalize(buffer[i]);

        if (UIManager.HasInstance)
            UIManager.Instance.RefreshManagedLocalization();

        OnLanguageChanged?.Invoke(_currentLang);
    }

    // 순회 안전용 임시 버퍼 (OnLocalize 안에서 Unregister 호출돼도 안전)
    private readonly System.Collections.Generic.List<ILocalizable> _iterBuffer = new();
    private System.Collections.Generic.List<ILocalizable> _localizablesBuffer()
    {
        _iterBuffer.Clear();
        foreach (var t in _localizables) _iterBuffer.Add(t);
        return _iterBuffer;
    }

    // OnLocalize 예외 격리. 한 대상이 던져도 나머지 갱신은 계속 진행.
    private void SafeLocalize(ILocalizable target)
    {
        if (target == null) return;
        try
        {
            target.OnLocalize();
        }
        catch (Exception e)
        {
            GameLogger.LogError(ELogCategory.UI, $"OnLocalize 예외: {e}");
        }
    }
}
