// =================================================================
// [스크립트 목적]  분석 이벤트 추상화. Provider 패턴으로 Firebase/GA/Unity 등 교체·병행
//                 JSONL 로컬 파일 로그 내장 (즉시 저장 + DirtyFlag 자동 저장)
//                 빌드 환경(Live)에서는 로컬 로그 전체 비활성
// [주요 변수]      - _providers      : dict 기반 분석 제공자 목록 (계층 B + 로컬)
//                  - _kpiProviders   : KPI/퍼널 제공자 목록 (계층 A - GameAnalytics)
//                  - _jsonLogProvider: JSONL 로컬 파일 저장 Provider 참조
// [의존 관계]      - ManagerBase<AnalyticsManager>, JsonLogProvider, GameManager
//                  - GaAnalyticsProvider, UgsAnalyticsProvider
// [InitOrder]      105
// [사용 가이드]    게임플레이 코드는 이 클래스를 직접 호출하지 않는다. GameLog가 유일한 진입점이다.
//                  SDK 추가 시 IAnalyticsProvider(dict) 또는 IKpiAnalyticsProvider(타입 안전)로
//                  래핑 후 AddProvider / AddKpiProvider.
// [비고]           2계층 라우팅:
//                   계층 A (GA)  = 저카디널리티 KPI·퍼널. Design/Progression/Resource
//                   계층 B (UGS) = 리치 데이터. 노드 종료 시 JSON 1개로 flush
//                  로컬 JSONL 로그는 _useLocalLog 켜면 자동 등록·자동 저장 (Live 빌드에선 비활성)
//
//                  [개인정보 동의] SettingsManager의 확정된 동의값으로 부팅 시 GA/UGS 활성 여부를 정한다.
//                  신규 사용자는 최초 시작 UI에서 확정하기 전까지 Provider를 등록하지 않는다.
// =================================================================
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>분석 제공자 계약. 실제 SDK(Firebase 등)를 이 인터페이스로 래핑.</summary>
public interface IAnalyticsProvider
{
    void Initialize();

    // [object 사용 의도] 파라미터 값은 string/int/float/bool이 혼재하며,
    // Firebase·Unity Analytics 등 실제 SDK API가 모두 object/Parameter 기반임.
    // 제네릭으로 박싱을 피해도 Provider→SDK 전달 시 재박싱되므로 실익 없음.
    // 또한 분석 이벤트는 저빈도(이산적 사건)라 박싱 GC 영향이 무시 가능.
    // 따라서 의도적으로 object 유지. 핫패스가 아니므로 최적화 대상 아님.
    //
    // [계약 — 중요] parameters는 이 호출 동안에만 유효한 일시적 버퍼다.
    // AnalyticsManager가 GC 절감을 위해 같은 Dictionary 인스턴스를 재사용·재활용하므로,
    // 구현체는 이 메서드 내에서 값을 즉시 소비(문자열화·SDK 전달 등)해야 한다.
    // parameters 참조를 비동기 큐·필드 등에 보관하면 다음 호출 시 내용이 덮어써져 데이터가 깨진다.
    // 나중에 쓰려면 반드시 복사(new Dictionary 등)해서 보관할 것.
    void LogEvent(string eventName, IReadOnlyDictionary<string, object> parameters);

    void SetUserProperty(string key, string value);
}

/// <summary>개발용 기본 제공자. Console에 로그만 출력.</summary>
public class DebugAnalyticsProvider : IAnalyticsProvider
{
    public void Initialize()
        => GameLogger.Log(ELogCategory.Analytics, "DebugAnalyticsProvider 초기화");

    public void LogEvent(string eventName, IReadOnlyDictionary<string, object> parameters)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        var sb = new System.Text.StringBuilder();
        sb.Append($"[Analytics] {eventName}");
        if (parameters != null && parameters.Count > 0)
        {
            sb.Append(" { ");
            foreach (var kv in parameters)
                sb.Append($"{kv.Key}={kv.Value}, ");
            sb.Append("}");
        }
        GameLogger.Log(ELogCategory.Analytics, sb.ToString());
#endif
    }

    public void SetUserProperty(string key, string value)
        => GameLogger.Log(ELogCategory.Analytics, $"[Analytics] UserProperty {key}={value}");
}

public class AnalyticsManager : ManagerBase<AnalyticsManager>
{
    public override int InitOrder => 105;
    // 분석은 실패해도 게임 진행에 영향 없음 → 선택적

    [Header("분석 설정")]
    [Tooltip("개발용 Debug Provider 자동 등록 (콘솔 로그). UGS 계열 이벤트만 출력")]
    [SerializeField] private bool _addDebugProvider = true;

    [Tooltip("GA(계층 A) 호출을 콘솔에 출력.\n" +
             "[중요] GameAnalytics SDK는 에디터에서 이벤트를 전송하지 않는다(지원 플랫폼 목록에\n" +
             "WindowsEditor가 없어 키가 전달되지 않음). 에디터 검증은 이걸 켜고 콘솔로 한다")]
    [SerializeField] private bool _addDebugKpiProvider = true;

    [Space]
    [Header("외부 SDK 전송")]
    [Tooltip("GameAnalytics(계층 A - KPI/퍼널) 전송 사용.\n" +
             "[주의] Resource 이벤트의 currency(gold) / itemType(shop_buy, card_remove)이\n" +
             "GA 설정 에셋에 사전 등록되어 있어야 한다. 미등록 시 해당 이벤트만 조용히 버려진다.")]
    [SerializeField] private bool _useGameAnalytics = true;

    [Tooltip("UGS Analytics(계층 B - 리치 데이터) 전송 사용.\n" +
             "[주의] 대시보드 Event Manager에 run_start / node_end / run_end 이벤트와\n" +
             "파라미터를 사전 선언해야 한다. 미선언 이벤트는 에러 없이 버려진다.")]
    [SerializeField] private bool _useUgsAnalytics = true;

    [Tooltip("에디터에서도 외부 SDK로 실제 전송. 끄면 에디터 실행은 대시보드 데이터를 오염시키지 않는다")]
    [SerializeField] private bool _sendFromEditor = false;

    [Space]
    [Header("로컬 JSONL 로그 설정")]
    [Tooltip("유저 행동 로그를 로컬 JSONL 파일로 저장. 한 줄 = 한 이벤트")]
    [SerializeField] private bool _useLocalLog = true;

    [Tooltip("저장 파일명 접두사. 최종 파일: {접두사}_{날짜}.jsonl")]
    [SerializeField] private string _logFileName = "user_log";

    [Tooltip("켜면 설치폴더(dataPath)에 저장. 끄면 persistentDataPath(권장).\n[경고] 설치폴더는 플랫폼에 따라 읽기 전용 → 실패 시 자동 폴백")]
    [SerializeField] private bool _useDataPath = false;

    [Tooltip("자동 저장 사용. 변경점(DirtyFlag) 있을 때만 실제 저장")]
    [SerializeField] private bool _autoSave = true;

    [Tooltip("자동 저장 주기 (초). 변경점 없으면 건너뜀")]
    [Range(5f, 300f)]
    [SerializeField] private float _autoSaveInterval = 60f;

    private readonly List<IAnalyticsProvider> _providers = new(4);

    // KPI/퍼널 제공자(계층 A). dict 기반 _providers와 파라미터 모양이 달라 별도 보관.
    private readonly List<IKpiAnalyticsProvider> _kpiProviders = new(2);

    // 파라미터 딕셔너리 재사용 (GC 절감)
    private readonly Dictionary<string, object> _paramBuffer = new(8);

    // 로컬 JSONL 저장 Provider 참조 (즉시 저장·자동 저장 제어용)
    private JsonLogProvider _jsonLogProvider;

    // UGS Provider 참조 (즉시 업로드 제어용). 미등록·동의 철회 시 null.
    private UgsAnalyticsProvider _ugsProvider;

    // GA Provider 참조 (동의 철회 시 전송 차단용). 미등록·동의 철회 시 null.
    private GaAnalyticsProvider _gaProvider;

    // 외부 SDK(GA/UGS) 활성 여부. 동의가 확정되기 전에는 false로 시작한다.
    private bool _externalEnabled;

    // 동의 변경 구독 여부. 종료 시 해제 대상을 가린다.
    private bool _consentSubscribed;

    // 구독 당시 SettingsManager 참조. 종료 시 Singleton 조회 상태와 무관하게 같은 대상에서 해제한다.
    private SettingsManager _consentSettings;

    // 마지막으로 반영한 동의 상태. 같은 값 재호출을 완전히 무동작으로 만드는 멱등 가드.
    private bool _hasAppliedConsent;
    private bool _lastConsentGranted;

    /// <summary>로컬 JSONL 로그 Provider 직접 접근 (이력 조회·수동 저장 등). 비활성 시 null.</summary>
    public JsonLogProvider LocalLog => _jsonLogProvider;

    /// <summary>외부 SDK(GA/UGS)가 실제로 등록·동작 중인지. 디버그 표시·테스트 검증용.</summary>
    public bool IsExternalAnalyticsEnabled => _externalEnabled;

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        if (_addDebugProvider)
            AddProvider(new DebugAnalyticsProvider());

        // GA 호출 콘솔 출력 (에디터·개발빌드 전용). 실제 전송은 하지 않는다.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (_addDebugKpiProvider)
            AddKpiProvider(new DebugKpiProvider());
#endif

        // 로컬 JSONL 로그: Live(출시) 환경에서는 전체 비활성
        if (_useLocalLog && !IsLiveBuild())
        {
            _jsonLogProvider = new JsonLogProvider(_logFileName, _useDataPath);
            AddProvider(_jsonLogProvider);

            if (_autoSave)
                _jsonLogProvider.StartAutoSave(_autoSaveInterval);
        }
        else if (_useLocalLog)
        {
            GameLogger.Log(ELogCategory.Analytics, "Live 환경: 로컬 JSONL 로그 비활성");
        }

        // 외부 SDK(GA/UGS)는 현재 저장된 확정 동의값을 확인한 뒤에만 등록한다.
        SubscribeConsent();

        return UniTask.CompletedTask;
    }

    /// <summary>
    /// 저장된 동의값을 읽어 반영하고, 이후 변경도 따라가도록 구독한다.
    /// SettingsManager(InitOrder 15)는 이 매니저(105)보다 먼저 초기화되므로 저장값 조회가 안전하다.
    /// SettingsManager가 없으면 동의 여부를 알 수 없으므로 비활성을 유지한다(안전한 쪽으로 실패).
    /// </summary>
    private void SubscribeConsent()
    {
        if (_consentSubscribed) return;

        if (!SettingsManager.HasInstance)
        {
            GameLogger.LogWarning(ELogCategory.Analytics,
                "SettingsManager 없음 - 개인정보 수집 동의를 알 수 없어 외부 SDK(GA/UGS)를 비활성 유지합니다");
            return;
        }

        _consentSettings = SettingsManager.Instance;
        _consentSettings.OnAnalyticsConsentChanged += ApplyAnalyticsConsent;
        _consentSubscribed = true;

        ApplyAnalyticsConsent(_consentSettings.IsAnalyticsConsentGranted);
    }

    /// <summary>종료 시 자동 저장 중단 + 미저장 로그 마지막 flush 보장.</summary>
    protected override async UniTask OnShutdownInternalAsync()
    {
        if (_consentSubscribed && _consentSettings != null)
            _consentSettings.OnAnalyticsConsentChanged -= ApplyAnalyticsConsent;
        _consentSubscribed = false;
        _consentSettings = null;

        // 외부 SDK 버퍼를 먼저 비운다. 앱 종료 시 미업로드 이벤트 유실 방지.
        _ugsProvider?.Flush();

        if (_jsonLogProvider != null)
        {
            _jsonLogProvider.StopAutoSave();
            await _jsonLogProvider.FlushAsync();
        }
    }

    /// <summary>
    /// Live(출시) 빌드 여부. GameManager.IsLive 우선,
    /// 아직 GameManager 미초기화면 false(개발 취급)로 안전 처리.
    /// </summary>
    private bool IsLiveBuild()
        => GameManager.HasInstance && GameManager.Instance.IsLive;

    /// <summary>외부 SDK(GA/UGS)로 실제 전송할지. 에디터는 _sendFromEditor로 명시 허용해야 한다.</summary>
    private bool CanSendToExternalSdk()
    {
#if UNITY_EDITOR
        return _sendFromEditor;
#else
        return true;
#endif
    }

    // ─────────────────────────────────────────────
    // 개인정보 수집 동의 반영
    // ─────────────────────────────────────────────

    /// <summary>
    /// 개인정보 수집 동의 상태를 외부 SDK(GA/UGS)에 반영한다. 여러 번 호출해도 안전하다(멱등).
    ///
    /// 동의값이 false이면 SDK Initialize 자체를 하지 않으므로 세션·기기 식별자도 전송되지 않는다.
    /// 비동의로 바뀌면 Provider 목록에서 제거(전송 경로 차단) + SDK 수집 중단을 함께 수행한다.
    /// </summary>
    public void ApplyAnalyticsConsent(bool granted)
    {
        if (IsShutdown) return;
        if (_hasAppliedConsent && _lastConsentGranted == granted) return;

        _hasAppliedConsent = true;
        _lastConsentGranted = granted;

        if (granted)
            EnableExternalProviders();
        else
            DisableExternalProviders();
    }

    /// <summary>동의 확정 후 GA/UGS Provider를 등록·초기화한다. 이미 켜져 있으면 무동작.</summary>
    private void EnableExternalProviders()
    {
        if (_externalEnabled) return;

        // 에디터에서는 _sendFromEditor를 켜야만 실제 전송한다(대시보드 오염 방지).
        if (!CanSendToExternalSdk())
        {
            GameLogger.Log(ELogCategory.Analytics, "에디터 전송 비활성 - 외부 SDK Provider를 등록하지 않습니다");
            return;
        }

        if (_useGameAnalytics && _gaProvider == null)
        {
            _gaProvider = new GaAnalyticsProvider();
            AddKpiProvider(_gaProvider);
        }

        if (_useUgsAnalytics && _ugsProvider == null)
        {
            _ugsProvider = new UgsAnalyticsProvider();
            AddProvider(_ugsProvider);
        }

        _externalEnabled = _gaProvider != null || _ugsProvider != null;
        if (_externalEnabled)
            GameLogger.Log(ELogCategory.Analytics, "개인정보 수집 동의 확인 - 외부 SDK(GA/UGS) 활성화");
    }

    /// <summary>
    /// 비동의·동의 철회 시 GA/UGS를 끈다. 등록 전에 불려도 안전하다(무동작).
    /// Provider 제거와 SDK 중단을 모두 수행해 어느 한쪽이 실패해도 수집이 이어지지 않게 한다.
    /// </summary>
    private void DisableExternalProviders()
    {
        if (_gaProvider != null)
        {
            _gaProvider.Disable();
            _kpiProviders.Remove(_gaProvider);
            _gaProvider = null;
        }

        if (_ugsProvider != null)
        {
            _ugsProvider.Disable();
            _providers.Remove(_ugsProvider);
            _ugsProvider = null;
        }

        _externalEnabled = false;
        GameLogger.Log(ELogCategory.Analytics, "개인정보 수집 비동의 - 외부 SDK(GA/UGS) 비활성화");
    }

    /// <summary>제공자 등록 + 초기화. 게임 시작 시 Firebase 등 추가.</summary>
    public void AddProvider(IAnalyticsProvider provider)
    {
        if (provider == null) return;
        provider.Initialize();
        _providers.Add(provider);
    }

    /// <summary>KPI/퍼널 제공자(계층 A) 등록 + 초기화. GameAnalytics 등.</summary>
    public void AddKpiProvider(IKpiAnalyticsProvider provider)
    {
        if (provider == null) return;
        provider.Initialize();
        _kpiProviders.Add(provider);
    }

    // ─────────────────────────────────────────────
    // KPI 로깅 (계층 A) — GameLog만 호출한다
    // ─────────────────────────────────────────────

    /// <summary>Design 이벤트 (집계값 포함).</summary>
    public void LogKpiDesign(string eventPath, float value)
    {
        for (int i = 0; i < _kpiProviders.Count; i++)
            _kpiProviders[i].DesignEvent(eventPath, value);
    }

    /// <summary>Design 이벤트 (순수 카운트).</summary>
    public void LogKpiDesign(string eventPath)
    {
        for (int i = 0; i < _kpiProviders.Count; i++)
            _kpiProviders[i].DesignEvent(eventPath);
    }

    /// <summary>Progression 이벤트. score에는 도달 층을 넣는다.</summary>
    public void LogKpiProgression(EProgressionStatus status, string stage, int score)
    {
        for (int i = 0; i < _kpiProviders.Count; i++)
            _kpiProviders[i].ProgressionEvent(status, stage, score);
    }

    /// <summary>재화 소비(Sink) 이벤트.</summary>
    public void LogKpiResourceSink(string currency, float amount, string itemType, string itemId)
    {
        for (int i = 0; i < _kpiProviders.Count; i++)
            _kpiProviders[i].ResourceSink(currency, amount, itemType, itemId);
    }

    public void LogKpiError(string message)
    {
        for (int i = 0; i < _kpiProviders.Count; i++)
            _kpiProviders[i].ErrorEvent(message);
    }

    // ─────────────────────────────────────────────
    // 이벤트 로깅
    // ─────────────────────────────────────────────

    public void LogEvent(string eventName)
    {
        for (int i = 0; i < _providers.Count; i++)
            _providers[i].LogEvent(eventName, null);
    }

    /// <summary>단일 파라미터 이벤트 (버퍼 재사용으로 GC 절감).</summary>
    public void LogEvent(string eventName, string key, object value)
    {
        _paramBuffer.Clear();
        _paramBuffer[key] = value;
        for (int i = 0; i < _providers.Count; i++)
            _providers[i].LogEvent(eventName, _paramBuffer);
    }

    /// <summary>다중 파라미터 이벤트. 호출부가 딕셔너리 전달.</summary>
    public void LogEvent(string eventName, IReadOnlyDictionary<string, object> parameters)
    {
        for (int i = 0; i < _providers.Count; i++)
            _providers[i].LogEvent(eventName, parameters);
    }

    public void SetUserProperty(string key, string value)
    {
        for (int i = 0; i < _providers.Count; i++)
            _providers[i].SetUserProperty(key, value);
    }

    // ─────────────────────────────────────────────
    // 로컬 로그 즉시 저장
    // ─────────────────────────────────────────────

    /// <summary>
    /// 로컬 JSONL 로그 즉시 저장 (비동기). 큐에 쌓인 항목을 묶어서 파일에 append.
    /// 스테이지 클리어 직후 등 중요 시점에 호출 권장. 비활성 시 무동작.
    /// </summary>
    public async UniTask SaveLogAsync(CancellationToken token = default)
    {
        if (_jsonLogProvider != null)
            await _jsonLogProvider.FlushAsync(token);
    }

    /// <summary>로컬 JSONL 로그 즉시 저장 (동기). await 불가 상황용.</summary>
    public void SaveLogSync()
        => _jsonLogProvider?.FlushSync();

    /// <summary>
    /// 외부 SDK(UGS)의 이벤트 버퍼를 즉시 업로드한다.
    /// UGS는 기본 60초 주기로만 올리므로, 런 종료처럼 유실되면 안 되는 시점에 호출한다.
    /// </summary>
    public void FlushExternal()
        => _ugsProvider?.Flush();
}
