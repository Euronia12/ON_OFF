// =================================================================
// [스크립트 목적]  사용자 설정(사운드·그래픽·화면·입력·언어) 중앙 관리 + 영속화 + 매니저 연동
//                 적용/취소(Draft) 모델: 값 변경 시 복사본 생성 → 확인 시 확정, 취소 시 원복
// [주요 변수]      - _saved : 저장/확정된 원본 설정 (디스크와 동기화되는 진실 원천)
//                  - _draft : 편집 중 임시 복사본 (값 변경 시 지연 생성, 확정/취소 전까지만 존재)
//                  - IsDirty: 미확정 변경 존재 여부 (UI 확인/취소 버튼 활성화 판단용)
// [의존 관계]      - ManagerBase<SettingsManager>, SaveManager
//                  - (연동) AudioManager: OnSettingsChanged 이벤트 구독으로 볼륨 반영
//                  - (연동) LocalizationManager: SelectLanguage로 위임
//                  - (연동) InputManager: 키 리바인딩 StartRebind/Save/Reset 직접 호출
//                  - (직접) AudioListener.pause: 백그라운드 음소거 (믹서 설정값 보존)
// [InitOrder]      15
// =================================================================
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 저장되는 설정 데이터. public field 금지 규칙에 따라 [SerializeField] private + 프로퍼티로 노출.
/// JsonUtility 직렬화 대상이므로 프로퍼티가 아닌 backing field가 직렬화됨.
/// ICloneableData<T> 구현으로 Draft 복사본 생성 (DataManager의 Base→Runtime 복제와 동일 컨벤션).
/// </summary>
[Serializable]
public class GameSettings : ISaveData, ICloneableData<GameSettings>
{
    [SerializeField] private int _version = 1;
    public int Version { get => _version; set => _version = value; }

    // ─────────────────────────────────────────────
    // 사운드 (0~1 linear)
    // ─────────────────────────────────────────────
    [SerializeField] private float _masterVolume = 0.5f;
    [SerializeField] private float _bgmVolume = 0.5f;
    [SerializeField] private float _sfxVolume = 0.5f;
    [SerializeField] private float _uiVolume = 0.5f;
    [SerializeField] private bool _isMuted = false;        // 전체 음소거 (마스터)
    [SerializeField] private bool _bgmMuted = false;       // BGM 채널 음소거 (볼륨값 보존)
    [SerializeField] private bool _sfxMuted = false;       // SFX 채널 음소거
    [SerializeField] private bool _uiMuted = false;        // UI 채널 음소거

    public float MasterVolume { get => _masterVolume; set => _masterVolume = value; }
    public float BgmVolume { get => _bgmVolume; set => _bgmVolume = value; }
    public float SfxVolume { get => _sfxVolume; set => _sfxVolume = value; }
    public float UiVolume { get => _uiVolume; set => _uiVolume = value; }
    public bool IsMuted { get => _isMuted; set => _isMuted = value; }
    public bool BgmMuted { get => _bgmMuted; set => _bgmMuted = value; }
    public bool SfxMuted { get => _sfxMuted; set => _sfxMuted = value; }
    public bool UiMuted { get => _uiMuted; set => _uiMuted = value; }

    /// <summary>
    /// 채널의 실제 출력 볼륨 계산. 전체 음소거(IsMuted) 또는 채널 음소거면 0, 아니면 볼륨값.
    /// AudioManager가 이 값을 믹서에 적용 → 음소거 해제 시 슬라이더 값 그대로 복구.
    /// </summary>
    public float GetEffectiveVolume(EAudioChannel channel)
    {
        if (_isMuted) return 0f; // 마스터 음소거는 전 채널 우선

        switch (channel)
        {
            case EAudioChannel.Master: return _masterVolume;
            case EAudioChannel.Bgm: return _bgmMuted ? 0f : _bgmVolume;
            case EAudioChannel.Sfx: return _sfxMuted ? 0f : _sfxVolume;
            case EAudioChannel.Ui: return _uiMuted ? 0f : _uiVolume;
            default: return _masterVolume;
        }
    }

    // ─────────────────────────────────────────────
    // 그래픽 / 화면
    // ─────────────────────────────────────────────
    [SerializeField] private int _qualityLevel = 2;
    [SerializeField] private int _screenWidth = 1920;
    [SerializeField] private int _screenHeight = 1080;
    [SerializeField] private int _refreshRate = 60;
    [SerializeField] private EScreenMode _screenMode = EScreenMode.FullScreenWindow;
    [SerializeField] private int _vSyncCount = 1;
    [SerializeField] private int _targetFrameRate = 60;
    [SerializeField] private float _brightness = 1f; // 1=기본. 0.5~1.5 권장 (URP Post Exposure 등에 전달)

    public int QualityLevel { get => _qualityLevel; set => _qualityLevel = value; }
    public int ScreenWidth { get => _screenWidth; set => _screenWidth = value; }
    public int ScreenHeight { get => _screenHeight; set => _screenHeight = value; }
    public int RefreshRate { get => _refreshRate; set => _refreshRate = value; }
    public EScreenMode ScreenMode { get => _screenMode; set => _screenMode = value; }
    public int VSyncCount { get => _vSyncCount; set => _vSyncCount = value; }
    public int TargetFrameRate { get => _targetFrameRate; set => _targetFrameRate = value; }
    public float Brightness { get => _brightness; set => _brightness = value; }

    // ─────────────────────────────────────────────
    // 입력 / 기타 (모바일 진동 등)
    // ─────────────────────────────────────────────
    [SerializeField] private bool _vibrationEnabled = true;

    public bool VibrationEnabled { get => _vibrationEnabled; set => _vibrationEnabled = value; }

    // ─────────────────────────────────────────────
    // 게임플레이 / 연출 / 접근성
    // ─────────────────────────────────────────────
    [SerializeField] private bool _screenShakeEnabled = true;   // 화면 흔들림 (접근성: 멀미 완화용 off)
    [SerializeField] private bool _fastModeEnabled = false;     // 고속모드 (게임 로직에서 상황 따라 참조)
    [SerializeField] private bool _textEffectEnabled = true;    // 텍스트 타이핑 연출 (off면 즉시 전체 표시)
    [SerializeField] private bool _muteOnBackground = true;     // 백그라운드(포커스 상실) 시 자동 음소거
    [SerializeField] private bool _skipStartCardPackOpeningAnimation = false;

    public bool ScreenShakeEnabled { get => _screenShakeEnabled; set => _screenShakeEnabled = value; }
    public bool FastModeEnabled { get => _fastModeEnabled; set => _fastModeEnabled = value; }
    public bool TextEffectEnabled { get => _textEffectEnabled; set => _textEffectEnabled = value; }
    public bool MuteOnBackground { get => _muteOnBackground; set => _muteOnBackground = value; }
    public bool SkipStartCardPackOpeningAnimation { get => _skipStartCardPackOpeningAnimation; set => _skipStartCardPackOpeningAnimation = value; }

    // ─────────────────────────────────────────────
    // 개인정보 수집 동의 (애널리틱스)
    // ─────────────────────────────────────────────
    // 신규 사용자는 동의 확정 전까지 외부 로그를 전송하지 않는다.
    // 최초 시작 UI의 토글만 표시상 true로 시작하며, 시작 입력으로 확정한 뒤 실제 설정에 저장한다.
    [SerializeField] private bool _analyticsConsent = false;             // 신규 실제 기본값 = 비동의(꺼짐)
    [SerializeField] private bool _hasAnalyticsConsentBeenAsked = false; // 최초 UI 선택 확정 여부

    public bool AnalyticsConsent { get => _analyticsConsent; set => _analyticsConsent = value; }
    public bool HasAnalyticsConsentBeenAsked { get => _hasAnalyticsConsentBeenAsked; set => _hasAnalyticsConsentBeenAsked = value; }

    // ─────────────────────────────────────────────
    // 언어
    // ─────────────────────────────────────────────
    [SerializeField] private EGameLanguage _language = EGameLanguage.Korean;

    // 유저가 언어를 직접 선택한 적이 있는지 (false = 신규 유저 → 시스템 언어 자동 감지 대상)
    [SerializeField] private bool _hasLanguageBeenSet = false;

    public EGameLanguage Language { get => _language; set => _language = value; }
    public bool HasLanguageBeenSet { get => _hasLanguageBeenSet; set => _hasLanguageBeenSet = value; }

    public bool ValueEquals(GameSettings other)
    {
        if (other == null) return false;

        return _version == other._version
            && Mathf.Approximately(_masterVolume, other._masterVolume)
            && Mathf.Approximately(_bgmVolume, other._bgmVolume)
            && Mathf.Approximately(_sfxVolume, other._sfxVolume)
            && Mathf.Approximately(_uiVolume, other._uiVolume)
            && _isMuted == other._isMuted
            && _bgmMuted == other._bgmMuted
            && _sfxMuted == other._sfxMuted
            && _uiMuted == other._uiMuted
            && _qualityLevel == other._qualityLevel
            && _screenWidth == other._screenWidth
            && _screenHeight == other._screenHeight
            && _refreshRate == other._refreshRate
            && _screenMode == other._screenMode
            && _vSyncCount == other._vSyncCount
            && _targetFrameRate == other._targetFrameRate
            && Mathf.Approximately(_brightness, other._brightness)
            && _vibrationEnabled == other._vibrationEnabled
            && _screenShakeEnabled == other._screenShakeEnabled
            && _fastModeEnabled == other._fastModeEnabled
            && _textEffectEnabled == other._textEffectEnabled
            && _muteOnBackground == other._muteOnBackground
            && _skipStartCardPackOpeningAnimation == other._skipStartCardPackOpeningAnimation
            && _analyticsConsent == other._analyticsConsent
            && _hasAnalyticsConsentBeenAsked == other._hasAnalyticsConsentBeenAsked
            && _language == other._language
            && _hasLanguageBeenSet == other._hasLanguageBeenSet;
    }

    /// <summary>
    /// 깊은 복사본 반환. 모든 필드가 값 타입(또는 enum)이라 멤버 단위 대입으로 완전 복제됨.
    /// 참조 타입 필드를 추가할 경우 반드시 여기서 별도 복제할 것.
    /// </summary>
    public GameSettings Clone()
    {
        return new GameSettings
        {
            _version = _version,
            _masterVolume = _masterVolume,
            _bgmVolume = _bgmVolume,
            _sfxVolume = _sfxVolume,
            _uiVolume = _uiVolume,
            _isMuted = _isMuted,
            _bgmMuted = _bgmMuted,
            _sfxMuted = _sfxMuted,
            _uiMuted = _uiMuted,
            _qualityLevel = _qualityLevel,
            _screenWidth = _screenWidth,
            _screenHeight = _screenHeight,
            _refreshRate = _refreshRate,
            _screenMode = _screenMode,
            _vSyncCount = _vSyncCount,
            _targetFrameRate = _targetFrameRate,
            _brightness = _brightness,
            _vibrationEnabled = _vibrationEnabled,
            _screenShakeEnabled = _screenShakeEnabled,
            _fastModeEnabled = _fastModeEnabled,
            _textEffectEnabled = _textEffectEnabled,
            _muteOnBackground = _muteOnBackground,
            _skipStartCardPackOpeningAnimation = _skipStartCardPackOpeningAnimation,
            _analyticsConsent = _analyticsConsent,
            _hasAnalyticsConsentBeenAsked = _hasAnalyticsConsentBeenAsked,
            _language = _language,
            _hasLanguageBeenSet = _hasLanguageBeenSet
        };
    }
}

public class SettingsManager : ManagerBase<SettingsManager>
{
    public override int InitOrder => 15;

    [Header("저장 설정")]
    [Tooltip("설정 저장 슬롯 이름. SaveManager가 이 이름으로 파일 생성")]
    [SerializeField] private string _settingsSlot = "settings";

    [Header("화면 적용 확인")]
    [Tooltip("해상도·화면모드 적용 후 자동 원복까지의 제한 시간(초). 이 시간 내 ConfirmScreenChange()를 호출하지 않으면 직전 화면으로 되돌림. 0 이하면 확인 절차 생략(즉시 확정)")]
    [Range(0f, 30f)]
    [SerializeField] private float _screenConfirmTimeout = 15f;

    // 저장/확정된 원본 (디스크와 동기화). 외부 조회는 항상 이 값을 본다.
    private GameSettings _saved;

    // 편집 중 임시 복사본. 첫 변경 시 지연 생성, Commit/Cancel 시 null로 정리.
    private GameSettings _draft;
    private bool _lastDirty;

    // 화면 변경 확인 타이머용. 적용 직전 화면 상태 백업.
    private CancellationTokenSource _screenConfirmCts;
    private GameSettings _screenBackup; // 화면 항목(해상도/모드/주사율)만 의미 있는 백업

    // 화면 모드 핫키(Alt+Enter)용. 창모드로 나갈 때의 전체화면 "종류"를 기억해 복귀 시 되돌린다.
    // 해상도는 바꾸지 않으므로 기억할 필요가 없다(유저가 고른 값 그대로 유지).
    private EScreenMode _hotkeyFullScreenMode = EScreenMode.FullScreenWindow;

    /// <summary>
    /// 게임이 지원하는 해상도 목록(작은 순). 옵션 드롭다운 구성과 로드 시 보정이 공유한다.
    /// 읽기 전용으로만 사용할 것 (배열이라 요소 대입은 막히지 않는다).
    /// </summary>
    public static readonly EResolutionOption[] SupportedResolutions =
    {
        EResolutionOption.R1280x720,
        EResolutionOption.R1280x800,
        EResolutionOption.R1600x900,
        EResolutionOption.R1680x1050,
        EResolutionOption.R1920x1080,
        EResolutionOption.R1920x1200,
        EResolutionOption.R2560x1440,
        EResolutionOption.R2560x1600,
        EResolutionOption.R3840x2160
    };

    // 마지막으로 화면에 적용을 "요청"한 값 = 현재 화면 상태의 진실 원천.
    // Screen.fullScreenMode/width는 프레임 끝에 반영돼 직후 조회가 옛 값이고,
    // draft/saved는 옵션에서 고르기만 하고 적용 안 한 값일 수 있어 둘 다 기준이 될 수 없다.
    private int _appliedScreenWidth;
    private int _appliedScreenHeight;
    private EScreenMode _appliedScreenMode = EScreenMode.FullScreenWindow;

    /// <summary>현재 적용 기준이 되는 설정. 편집 중이면 draft, 아니면 saved 반환.</summary>
    public GameSettings Current => _draft ?? _saved;

    /// <summary>미확정 변경이 존재하는지. UI가 확인/취소 버튼 활성화 판단에 사용.</summary>
    public bool IsDirty => _draft != null && !_draft.ValueEquals(_saved);

    /// <summary>화면 변경 확인 대기 중인지 (자동 원복 타이머 동작 중).</summary>
    public bool IsAwaitingScreenConfirm => _screenBackup != null;

    /// <summary>설정 값이 바뀔 때마다 발행. AudioManager 등이 구독해 즉시 반영(GameSettings는 class → 참조 전달, 박싱 없음).</summary>
    public event Action<GameSettings> OnSettingsChanged;

    /// <summary>IsDirty 상태가 바뀔 때 발행. UI 버튼 활성/비활성 토글용.</summary>
    public event Action<bool> OnDirtyChanged;

    /// <summary>
    /// 개인정보 수집 동의가 확정·변경될 때 발행. AnalyticsManager가 구독해 GA/UGS를 켜고 끈다.
    /// (SettingsManager는 애널리틱스를 직접 알지 않는다 — 단방향 의존 유지)
    /// </summary>
    public event Action<bool> OnAnalyticsConsentChanged;

    /// <summary>화면 변경 확인 대기 중 매초 발행. 인자: 남은 시간(초). 0이면 종료. UI 카운트다운 표시용.</summary>
    public event Action<float> OnScreenConfirmTick;

    /// <summary>
    /// 옵션 UI 외부 경로(Alt+Enter 핫키)로 화면 설정이 적용됐을 때 발행.
    /// 옵션 창이 열려 있는 동안 표시를 최신 상태로 맞추기 위함.
    /// 옵션의 적용·원복 흐름은 자체적으로 UI를 갱신하므로 그쪽에서는 발행하지 않는다.
    /// </summary>
    public event Action OnScreenSettingsApplied;

    // ─────────────────────────────────────────────
    // 초기화 / 종료
    // ─────────────────────────────────────────────

    protected override async UniTask OnInitializeAsync(CancellationToken token)
    {
        _saved = await SaveManager.Instance.LoadAsync<GameSettings>(_settingsSlot, token);
        _saved ??= new GameSettings();
        NormalizeLoadedSettings(_saved);
        await MigrateLegacyAnalyticsConsentAsync(token);

        // 신규 설치 시 화면 기본값을 현재 디스플레이 기준으로 보정
        if (_saved.ScreenWidth <= 0 || _saved.ScreenHeight <= 0)
        {
            _saved.ScreenWidth = Screen.currentResolution.width;
            _saved.ScreenHeight = Screen.currentResolution.height;
        }

        // 저장값이 현재 디스플레이에서 표시 불가능한 경우 보정 (모니터 교체·세이브 이월 대응)
        ClampScreenToDisplay(_saved);

        ApplyAll(_saved);
        InitializeHotkeyFullScreenMode();

        // Alt+Enter 화면 모드 토글 구독. InputManager는 InitOrder가 뒤(60)지만
        // 인스턴스는 Awake에서 잡히므로(Bootstrap 보장) 초기화 순서와 무관하게 안전하다.
        if (InputManager.HasInstance)
        {
            InputManager.Instance.OnToggleFullScreenPressed += ToggleFullScreenHotkey;
        }
        else
        {
            GameLogger.LogWarning(ELogCategory.System,
                "InputManager 없음. Alt+Enter 화면 모드 토글 비활성");
        }
    }

    /// <summary>
    /// 저장된 해상도가 현재 디스플레이를 넘으면 표시 가능한 최대 지원 해상도로 낮춘다.
    /// 모니터를 바꾸거나 다른 PC의 세이브를 옮겨온 경우, 그대로 적용하면 창이 화면 밖으로 나가거나
    /// 전체화면이 먹지 않아 유저가 옵션에 접근조차 못 하게 된다.
    /// </summary>
    private static void ClampScreenToDisplay(GameSettings s)
    {
        int maxWidth = Screen.currentResolution.width;
        int maxHeight = Screen.currentResolution.height;

        if (s.ScreenWidth <= maxWidth && s.ScreenHeight <= maxHeight) return;

        // 지원 목록은 작은 순 → 화면에 들어가는 마지막 항목이 최대값
        int bestWidth = 0;
        int bestHeight = 0;
        for (int i = 0; i < SupportedResolutions.Length; i++)
        {
            ToResolution(SupportedResolutions[i], out int width, out int height);
            if (width > maxWidth || height > maxHeight) continue;

            bestWidth = width;
            bestHeight = height;
        }

        // 지원 목록 최소값보다 작은 디스플레이면 디스플레이 해상도를 그대로 쓴다
        if (bestWidth <= 0)
        {
            bestWidth = maxWidth;
            bestHeight = maxHeight;
        }

        GameLogger.Log(ELogCategory.System,
            $"저장 해상도({s.ScreenWidth}x{s.ScreenHeight})가 디스플레이({maxWidth}x{maxHeight}) 초과 → {bestWidth}x{bestHeight}로 보정");

        s.ScreenWidth = bestWidth;
        s.ScreenHeight = bestHeight;
    }

    /// <summary>
    /// Alt+Enter로 창모드에서 돌아갈 전체화면 종류 초기화.
    /// 저장 상태가 이미 창모드라 알 수 없으면 기본값(테두리 없는 창 전체화면)을 쓴다.
    /// </summary>
    private void InitializeHotkeyFullScreenMode()
    {
        _hotkeyFullScreenMode = _saved.ScreenMode != EScreenMode.Windowed
            ? _saved.ScreenMode
            : EScreenMode.FullScreenWindow;
    }

    private static void NormalizeLoadedSettings(GameSettings settings)
    {
        if (settings == null) return;

        if (!Enum.IsDefined(typeof(EGameLanguage), settings.Language))
            settings.Language = ToGameLanguage((SystemLanguage)(int)settings.Language);
    }

    /// <summary>
    /// 동의 기능 도입 전에 이미 튜토리얼에 진입했던 기존 사용자만 한 번 동의 상태로 이관한다.
    /// 이관 후 Asked가 true로 저장되므로 이후 부팅에서는 반복 실행되지 않으며,
    /// 사용자가 옵션에서 명시적으로 false를 선택한 기록도 덮어쓰지 않는다.
    /// </summary>
    private async UniTask MigrateLegacyAnalyticsConsentAsync(CancellationToken token)
    {
        if (!ShouldMigrateLegacyAnalyticsConsent(_saved, FirstLaunchStore.HasEnteredTutorial))
            return;

        _saved.AnalyticsConsent = true;
        _saved.HasAnalyticsConsentBeenAsked = true;
        await SaveManager.Instance.SaveAsync(_settingsSlot, _saved, token);
    }

    private static bool ShouldMigrateLegacyAnalyticsConsent(GameSettings settings, bool hasEnteredTutorial)
        => settings != null
           && !settings.HasAnalyticsConsentBeenAsked
           && hasEnteredTutorial;

    protected override async UniTask OnShutdownInternalAsync()
    {
        CancelScreenConfirmTimer();

        if (InputManager.HasInstance)
            InputManager.Instance.OnToggleFullScreenPressed -= ToggleFullScreenHotkey;

        // 종료 시 미확정 draft는 버린다(취소 처리). 확정본만 저장.
        // teardown 중에는 SaveManager가 이미 파괴됐을 수 있으므로 HasInstance로 가드.
        if (_saved != null && SaveManager.HasInstance)
            await SaveManager.Instance.SaveAsync(_settingsSlot, _saved);
    }

    // ─────────────────────────────────────────────
    // 일괄 적용 (대상 설정 인자로 받음 → saved/draft 모두 적용 가능)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 지정한 설정을 실제 시스템에 적용.
    /// 사운드는 OnSettingsChanged 구독자(AudioManager)가 반영하므로 여기선 이벤트만 발행.
    /// </summary>
    private void ApplyAll(GameSettings s)
    {
        ApplyGraphics(s);
        ApplyScreenImmediate(s); // 초기화 시점은 확인 절차 없이 즉시 적용(저장된 안전값이므로)
        ApplyFrameRate(s);
        ApplyLanguage(s);
        OnSettingsChanged?.Invoke(s);
    }

    /// <summary>품질 레벨 적용.</summary>
    private void ApplyGraphics(GameSettings s)
    {
        QualitySettings.SetQualityLevel(s.QualityLevel, true);
    }

    /// <summary>해상도 + 화면 모드 즉시 적용 (확인 절차 없음).</summary>
    private void ApplyScreenImmediate(GameSettings s)
    {
        var mode = ToFullScreenMode(s.ScreenMode);
        var refresh = new RefreshRate
        {
            numerator = (uint)Mathf.Max(1, s.RefreshRate),
            denominator = 1u
        };
        Screen.SetResolution(s.ScreenWidth, s.ScreenHeight, mode, refresh);

        // 적용 요청값 기록. 모든 화면 적용 경로가 이 함수를 지나므로 여기 한 곳에서만 갱신하면 된다.
        _appliedScreenWidth = s.ScreenWidth;
        _appliedScreenHeight = s.ScreenHeight;
        _appliedScreenMode = s.ScreenMode;
    }

    /// <summary>VSync + 목표 프레임레이트 적용.</summary>
    private void ApplyFrameRate(GameSettings s)
    {
        QualitySettings.vSyncCount = s.VSyncCount;
        Application.targetFrameRate = s.TargetFrameRate;
    }

    /// <summary>언어를 LocalizationManager에 적용 (직접 선택 기록이 있을 때만).</summary>
    private void ApplyLanguage(GameSettings s)
    {
        if (!s.HasLanguageBeenSet) return;
        if (!LocalizationManager.HasInstance) return;
        LocalizationManager.Instance.SetLanguage(ToSystemLanguage(s.Language));
    }

    // ─────────────────────────────────────────────
    // Draft 관리 (값 변경 시 지연 복사 + Dirty 토글)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 변경 직전 호출. draft가 없으면 saved의 복사본을 생성한다(첫 변경에만).
    /// 반환된 draft를 수정하면 원본(saved)은 보존된다.
    /// </summary>
    private GameSettings EnsureDraft()
    {
        if (_draft == null)
        {
            _draft = _saved.Clone();
        }
        return _draft;
    }

    // ─────────────────────────────────────────────
    // 사운드 설정 (안전 항목: draft 수정 후 즉시 미리듣기)
    // ─────────────────────────────────────────────

    public void SetMasterVolume(float v) { EnsureDraft().MasterVolume = Mathf.Clamp01(v); PreviewChanged(); }
    public void SetBgmVolume(float v) { EnsureDraft().BgmVolume = Mathf.Clamp01(v); PreviewChanged(); }
    public void SetSfxVolume(float v) { EnsureDraft().SfxVolume = Mathf.Clamp01(v); PreviewChanged(); }
    public void SetUiVolume(float v) { EnsureDraft().UiVolume = Mathf.Clamp01(v); PreviewChanged(); }
    public void SetMuted(bool on) { EnsureDraft().IsMuted = on; PreviewChanged(); }

    /// <summary>BGM 채널 음소거. 볼륨값은 보존(해제 시 이전 볼륨 복구).</summary>
    public void SetBgmMuted(bool on) { EnsureDraft().BgmMuted = on; PreviewChanged(); }

    /// <summary>SFX 채널 음소거.</summary>
    public void SetSfxMuted(bool on) { EnsureDraft().SfxMuted = on; PreviewChanged(); }

    /// <summary>UI 채널 음소거.</summary>
    public void SetUiMuted(bool on) { EnsureDraft().UiMuted = on; PreviewChanged(); }

    // ─────────────────────────────────────────────
    // 그래픽 / 기타 설정 (안전 항목: draft 수정 후 즉시 미리보기)
    // ─────────────────────────────────────────────

    /// <summary>품질 프리셋 변경 + 즉시 미리보기 적용.</summary>
    public void SetQuality(int level)
    {
        EnsureDraft().QualityLevel = Mathf.Max(0, level);
        ApplyGraphics(_draft);
        PreviewChanged();
    }

    /// <summary>VSync 카운트 변경(0=끔, 1=수직동기) + 즉시 적용.</summary>
    public void SetVSync(int count)
    {
        EnsureDraft().VSyncCount = Mathf.Clamp(count, 0, 4);
        ApplyFrameRate(_draft);
        PreviewChanged();
    }

    /// <summary>목표 프레임레이트 변경 + 즉시 적용. -1=무제한.</summary>
    public void SetTargetFrameRate(int fps)
    {
        EnsureDraft().TargetFrameRate = fps;
        ApplyFrameRate(_draft);
        PreviewChanged();
    }

    /// <summary>밝기 값 변경(0.5~1.5 권장). 실제 화면 반영은 구독자가 Post Exposure 등에 전달.</summary>
    public void SetBrightness(float value)
    {
        EnsureDraft().Brightness = Mathf.Clamp(value, 0.1f, 2f);
        PreviewChanged();
    }

    /// <summary>진동 On/Off (모바일). 햅틱 호출 측에서 VibrationEnabled 확인 후 동작.</summary>
    public void SetVibration(bool on) { EnsureDraft().VibrationEnabled = on; PreviewChanged(); }

    /// <summary>화면 흔들림 On/Off (접근성). 카메라 흔들림 연출 측에서 ScreenShakeEnabled 확인 후 동작.</summary>
    public void SetScreenShake(bool on) { EnsureDraft().ScreenShakeEnabled = on; PreviewChanged(); }

    /// <summary>고속모드 On/Off. 게임 로직에서 FastModeEnabled를 참조해 배속·연출 생략 등 처리.</summary>
    public void SetFastMode(bool on) { EnsureDraft().FastModeEnabled = on; PreviewChanged(); }

    /// <summary>텍스트 효과 On/Off. 대사 타이핑 연출 측에서 TextEffectEnabled 확인(off면 즉시 전체 표시).</summary>
    public void SetTextEffect(bool on) { EnsureDraft().TextEffectEnabled = on; PreviewChanged(); }

    /// <summary>시작 카드팩 개봉 연출 생략 여부를 즉시 저장.</summary>
    public void SetSkipStartCardPackOpeningAnimation(bool skip)
    {
        if (_saved.SkipStartCardPackOpeningAnimation == skip &&
            (_draft == null || _draft.SkipStartCardPackOpeningAnimation == skip))
            return;

        _saved.SkipStartCardPackOpeningAnimation = skip;
        if (_draft != null)
            _draft.SkipStartCardPackOpeningAnimation = skip;

        if (SaveManager.HasInstance)
            SaveManager.Instance.SaveAsync(_settingsSlot, _saved).Forget();
        OnSettingsChanged?.Invoke(Current);
    }

    // ─────────────────────────────────────────────
    // 개인정보 수집 동의
    // ─────────────────────────────────────────────

    /// <summary>
    /// 애널리틱스 수집이 허용된 상태인가.
    /// 동의 UI에서 선택을 확정했고 동의값도 true일 때만 외부 SDK를 활성화한다.
    /// </summary>
    public bool IsAnalyticsConsentGranted
        => _saved != null
           && _saved.HasAnalyticsConsentBeenAsked
           && _saved.AnalyticsConsent;

    /// <summary>동의 여부를 유저에게 물어본 적이 있는지. false면 최초 실행 안내가 필요하다.</summary>
    public bool HasAnalyticsConsentBeenAsked
        => _saved != null && _saved.HasAnalyticsConsentBeenAsked;

    /// <summary>
    /// 개인정보 수집 동의를 확정 저장한다. 최초 실행 안내 패널의 시작 버튼에서 호출.
    /// 확인/취소 흐름을 타지 않는다 — 유저가 이미 명시적으로 고른 값이기 때문이다.
    /// </summary>
    public void SetAnalyticsConsent(bool granted)
    {
        if (!ApplyAnalyticsConsent(granted)) return;

        if (SaveManager.HasInstance)
            SaveManager.Instance.SaveAsync(_settingsSlot, _saved).Forget();
    }

    /// <summary>
    /// 최초 시작 화면용 확정 저장. 런타임 반영은 즉시 수행하고 저장 완료를 기다릴 수 있게 한다.
    /// 호출자는 저장 완료 후에만 최초 진입 플래그를 기록해야 사용자 선택과 진입 상태가 어긋나지 않는다.
    /// </summary>
    public async UniTask SetAnalyticsConsentAndSaveAsync(bool granted, CancellationToken token = default)
    {
        if (!ApplyAnalyticsConsent(granted)) return;

        if (SaveManager.HasInstance)
            await SaveManager.Instance.SaveAsync(_settingsSlot, _saved, token);
    }

    /// <summary>확정 동의값을 메모리와 AnalyticsManager에 즉시 반영한다.</summary>
    private bool ApplyAnalyticsConsent(bool granted)
    {
        if (_saved == null) return false;

        bool consentConfirmedOrChanged = !_saved.HasAnalyticsConsentBeenAsked
                                         || _saved.AnalyticsConsent != granted;

        _saved.AnalyticsConsent = granted;
        _saved.HasAnalyticsConsentBeenAsked = true;

        if (_draft != null)
        {
            _draft.AnalyticsConsent = granted;
            _draft.HasAnalyticsConsentBeenAsked = true;
        }

        RefreshDirtyState();
        OnSettingsChanged?.Invoke(Current);

        if (consentConfirmedOrChanged)
            OnAnalyticsConsentChanged?.Invoke(IsAnalyticsConsentGranted);

        return true;
    }

    /// <summary>
    /// 옵션 화면의 개인정보 수집 동의를 Draft에만 기록한다.
    /// 실제 저장과 GA/UGS 활성 변경은 Commit에서만 수행하며 Cancel 시 기존 저장값으로 복원된다.
    /// </summary>
    public void SetAnalyticsConsentDraft(bool granted)
    {
        if (_saved == null) return;

        GameSettings draft = EnsureDraft();
        draft.AnalyticsConsent = granted;
        draft.HasAnalyticsConsentBeenAsked = true;
        PreviewChanged();
    }

    /// <summary>백그라운드 자동 음소거 On/Off. off로 바꾸면 현재 포커스 음소거 상태도 해제.</summary>
    public void SetMuteOnBackground(bool on)
    {
        EnsureDraft().MuteOnBackground = on;
        // 옵션을 끄는 즉시 혹시 걸려있던 백그라운드 음소거 해제 (포커스 상태 무관하게 정상화)
        if (!on) ApplyBackgroundMute(false);
        PreviewChanged();
    }

    // ─────────────────────────────────────────────
    // 화면 설정 (위험 항목: draft에 값만 담고 명시 적용 + 자동 원복 타이머)
    // ─────────────────────────────────────────────

    /// <summary>해상도 값만 draft에 기록 (적용 안 함). 실제 적용은 ApplyScreenChange() 호출.</summary>
    public void SetResolution(int width, int height)
    {
        var d = EnsureDraft();
        d.ScreenWidth = Mathf.Max(1, width);
        d.ScreenHeight = Mathf.Max(1, height);
        PreviewChanged(); // 이벤트만 발행(라벨 갱신용). 화면은 안 바뀜
    }

    /// <summary>화면 모드 값만 draft에 기록 (적용 안 함). 실제 적용은 ApplyScreenChange() 호출.</summary>
    public void SetScreenMode(EScreenMode mode)
    {
        EnsureDraft().ScreenMode = mode;
        PreviewChanged();
    }

    /// <summary>주사율 값만 draft에 기록 (적용 안 함).</summary>
    public void SetRefreshRate(int rate)
    {
        EnsureDraft().RefreshRate = Mathf.Max(1, rate);
        PreviewChanged();
    }

    /// <summary>
    /// draft의 화면 설정(해상도/모드/주사율)을 임시 적용하고 자동 원복 타이머 시작.
    /// 적용 후 _screenConfirmTimeout 안에 ConfirmScreenChange()를 호출해야 확정.
    /// 호출 안 하면 직전 화면으로 자동 복구(잘못된 해상도로 화면 안 보이는 사고 방지).
    /// </summary>
    public void ApplyScreenChange()
    {
        if (_draft == null) return;

        // 이미 확인 대기 중이면 이전 타이머 정리(중복 방지)
        CancelScreenConfirmTimer();

        // 적용 직전 상태 백업(원복 대상)
        _screenBackup = _saved.Clone();

        ApplyScreenImmediate(_draft);

        // 확인 절차 생략 설정이면 즉시 확정
        if (_screenConfirmTimeout <= 0f)
        {
            ConfirmScreenChange();
            return;
        }

        _screenConfirmCts = new CancellationTokenSource();
        ScreenConfirmCountdownAsync(_screenConfirmCts.Token).Forget();
    }

    /// <summary>화면 변경 확인(확정). 자동 원복 타이머를 멈추고 변경을 유지한다.</summary>
    public void ConfirmScreenChange()
    {
        CancelScreenConfirmTimer();
        _screenBackup = null;
        OnScreenConfirmTick?.Invoke(0f);
    }

    /// <summary>화면 변경 거절(즉시 원복). 타이머 만료 전 유저가 "되돌리기" 누를 때.</summary>
    public void RevertScreenChange()
    {
        if (_screenBackup == null) return;

        // draft의 화면 값을 백업(직전 확정값)으로 되돌림
        if (_draft != null)
        {
            _draft.ScreenWidth = _screenBackup.ScreenWidth;
            _draft.ScreenHeight = _screenBackup.ScreenHeight;
            _draft.ScreenMode = _screenBackup.ScreenMode;
            _draft.RefreshRate = _screenBackup.RefreshRate;
        }

        ApplyScreenImmediate(_screenBackup);
        CancelScreenConfirmTimer();
        _screenBackup = null;
        OnScreenConfirmTick?.Invoke(0f);
        PreviewChanged();
    }

    // 자동 원복 카운트다운. 매초 남은 시간 발행, 만료 시 RevertScreenChange 호출.
    private async UniTaskVoid ScreenConfirmCountdownAsync(CancellationToken token)
    {
        try
        {
            float remaining = _screenConfirmTimeout;
            while (remaining > 0f)
            {
                OnScreenConfirmTick?.Invoke(remaining);
                // 실시간 기준(unscaled) — 일시정지 메뉴에서 Time.timeScale=0이어도 동작
                await UniTask.Delay(TimeSpan.FromSeconds(1f), DelayType.UnscaledDeltaTime,
                    cancellationToken: token);
                remaining -= 1f;
            }
            // 시간 만료 → 자동 원복
            RevertScreenChange();
        }
        catch (OperationCanceledException) { }
    }

    private void CancelScreenConfirmTimer()
    {
        _screenConfirmCts?.Cancel();
        _screenConfirmCts?.Dispose();
        _screenConfirmCts = null;
    }

    // ─────────────────────────────────────────────
    // 화면 모드 핫키 (Alt+Enter)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 전체화면 ↔ 창모드 토글. InputManager의 OnToggleFullScreenPressed(Alt+Enter)로 호출된다.
    /// 유저 의도가 명확한 조작이므로 확인 절차(15초 원복) 없이 즉시 적용하고 확정 저장한다.
    /// 해상도는 바꾸지 않고 화면 모드만 뒤집는다(유저가 고른 값 유지).
    /// </summary>
    public void ToggleFullScreenHotkey()
    {
        if (!IsInitialized || IsShutdown || _saved == null) return;

        // 화면 변경 확인 대기 중이면 무시. 원복 타이머가 잡고 있는 상태를 건드리면 백업값이 꼬인다.
        if (IsAwaitingScreenConfirm) return;

        EScreenMode mode;
        if (_appliedScreenMode == EScreenMode.Windowed)
        {
            // 창모드 → 전체화면. 창모드로 나가기 전의 전체화면 종류로 복귀
            mode = _hotkeyFullScreenMode;
        }
        else
        {
            // 전체화면 → 창모드. 복귀용으로 전체화면 종류를 기억해 둔다
            _hotkeyFullScreenMode = _appliedScreenMode;
            mode = EScreenMode.Windowed;
        }

        // 해상도는 마지막으로 적용된 값을 그대로 유지한다.
        // 창 크기가 화면보다 크면 Unity가 작업 영역에 맞춰 클램프한다.
        ApplyScreenValuesConfirmed(_appliedScreenWidth, _appliedScreenHeight, mode);
    }

    /// <summary>
    /// 화면 값을 확정 상태로 즉시 적용(확인 절차 없음). saved에 기록하고 디스크에 저장한다.
    /// 편집 중 draft가 살아 있으면 화면 항목만 함께 맞춰 준다 → 옵션 UI 표시·확정/취소 시 값이 되돌아가는 불일치 방지.
    /// </summary>
    private void ApplyScreenValuesConfirmed(int width, int height, EScreenMode mode)
    {
        _saved.ScreenWidth = width;
        _saved.ScreenHeight = height;
        _saved.ScreenMode = mode;

        if (_draft != null)
        {
            _draft.ScreenWidth = width;
            _draft.ScreenHeight = height;
            _draft.ScreenMode = mode;
        }

        ApplyScreenImmediate(_saved);

        if (SaveManager.HasInstance)
            SaveManager.Instance.SaveAsync(_settingsSlot, _saved).Forget();

        RefreshDirtyState();
        OnSettingsChanged?.Invoke(Current);
        OnScreenSettingsApplied?.Invoke();
    }

    // ─────────────────────────────────────────────
    // 백그라운드 음소거 (앱 포커스 / 일시정지 감지)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 앱이 포커스를 얻거나 잃을 때 호출(Unity 콜백). MuteOnBackground가 켜져 있으면 포커스 상실 시 음소거.
    /// </summary>
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!IsInitialized) return;
        if (!Current.MuteOnBackground) return;
        ApplyBackgroundMute(!hasFocus);
    }

    /// <summary>
    /// 앱이 일시정지/재개될 때 호출(모바일 홈버튼 등). 포커스 콜백과 함께 모바일 커버.
    /// </summary>
    private void OnApplicationPause(bool isPaused)
    {
        if (!IsInitialized) return;
        if (!Current.MuteOnBackground) return;
        ApplyBackgroundMute(isPaused);
    }

    /// <summary>
    /// 실제 음소거 적용. AudioListener.pause로 전체 오디오를 멈춤(믹서 볼륨 설정값은 보존 → 복귀 시 그대로 원복).
    /// </summary>
    private void ApplyBackgroundMute(bool mute)
    {
        AudioListener.pause = mute;
    }

    // ─────────────────────────────────────────────
    // 언어 설정
    // ─────────────────────────────────────────────

    /// <summary>
    /// 언어 변경 기록 (값만 변경, 매니저 호출 안 함 → LocalizationManager 역호출 시 무한루프 방지).
    /// 언어는 화면 텍스트가 즉시 바뀌는 게 자연스러우므로 LocalizationManager는 즉시 전환하되,
    /// 설정값 자체는 draft에 기록해 확인/취소 흐름을 따른다.
    /// LocalizationManager.SetLanguage가 이 메서드를 역호출한다.
    /// </summary>
    public void SetLanguage(EGameLanguage lang)
    {
        // 최초 언어 자동 결정은 옵션 편집이 아니므로 즉시 확정한다.
        if (!_saved.HasLanguageBeenSet && _draft == null)
        {
            _saved.Language = lang;
            _saved.HasLanguageBeenSet = true;
            SaveManager.Instance.SaveAsync(_settingsSlot, _saved).Forget();
            OnSettingsChanged?.Invoke(_saved);
            RefreshDirtyState();
            return;
        }

        var d = EnsureDraft();
        d.Language = lang;
        d.HasLanguageBeenSet = true;

        OnSettingsChanged?.Invoke(Current);
        RefreshDirtyState();
    }

    /// <summary>
    /// 옵션 UI에서 유저가 언어를 직접 선택할 때 호출.
    /// LocalizationManager에 실제 전환 위임(화면 텍스트 갱신) → LocalizationManager가 SetLanguage 역호출.
    /// </summary>
    public void SelectLanguage(EGameLanguage lang)
    {
        if (LocalizationManager.HasInstance)
            LocalizationManager.Instance.SetLanguage(ToSystemLanguage(lang));
        else
            SetLanguage(lang);
    }

    public static SystemLanguage ToSystemLanguage(EGameLanguage language)
    {
        return language switch
        {
            EGameLanguage.Korean => SystemLanguage.Korean,
            EGameLanguage.English => SystemLanguage.English,
            EGameLanguage.ChineseSimplified => SystemLanguage.ChineseSimplified,
            EGameLanguage.ChineseTraditional => SystemLanguage.ChineseTraditional,
            EGameLanguage.Japanese => SystemLanguage.Japanese,
            EGameLanguage.German => SystemLanguage.German,
            _ => SystemLanguage.Korean
        };
    }

    public static EGameLanguage ToGameLanguage(SystemLanguage language)
    {
        return language switch
        {
            SystemLanguage.Korean => EGameLanguage.Korean,
            SystemLanguage.English => EGameLanguage.English,
            SystemLanguage.ChineseSimplified => EGameLanguage.ChineseSimplified,
            SystemLanguage.ChineseTraditional => EGameLanguage.ChineseTraditional,
            SystemLanguage.Japanese => EGameLanguage.Japanese,
            SystemLanguage.German => EGameLanguage.German,
            _ => EGameLanguage.English
        };
    }

    public static int ToFrameRate(EFrameRateOption option)
    {
        return option switch
        {
            EFrameRateOption.Fps30 => 30,
            EFrameRateOption.Fps60 => 60,
            EFrameRateOption.Fps120 => 120,
            _ => 60
        };
    }

    public static EFrameRateOption ToFrameRateOption(int fps)
    {
        return fps switch
        {
            30 => EFrameRateOption.Fps30,
            120 => EFrameRateOption.Fps120,
            _ => EFrameRateOption.Fps60
        };
    }

    public static void ToResolution(EResolutionOption option, out int width, out int height)
    {
        switch (option)
        {
            case EResolutionOption.R1280x720:
                width = 1280;
                height = 720;
                break;
            case EResolutionOption.R1280x800:
                width = 1280;
                height = 800;
                break;
            case EResolutionOption.R1600x900:
                width = 1600;
                height = 900;
                break;
            case EResolutionOption.R1680x1050:
                width = 1680;
                height = 1050;
                break;
            case EResolutionOption.R1920x1200:
                width = 1920;
                height = 1200;
                break;
            case EResolutionOption.R2560x1440:
                width = 2560;
                height = 1440;
                break;
            case EResolutionOption.R2560x1600:
                width = 2560;
                height = 1600;
                break;
            case EResolutionOption.R3840x2160:
                width = 3840;
                height = 2160;
                break;
            default:
                width = 1920;
                height = 1080;
                break;
        }
    }

    public static EResolutionOption ToResolutionOption(int width, int height)
    {
        return (width, height) switch
        {
            (1280, 720) => EResolutionOption.R1280x720,
            (1280, 800) => EResolutionOption.R1280x800,
            (1600, 900) => EResolutionOption.R1600x900,
            (1680, 1050) => EResolutionOption.R1680x1050,
            (1920, 1200) => EResolutionOption.R1920x1200,
            (2560, 1440) => EResolutionOption.R2560x1440,
            (2560, 1600) => EResolutionOption.R2560x1600,
            (3840, 2160) => EResolutionOption.R3840x2160,
            _ => EResolutionOption.R1920x1080
        };
    }

    // ─────────────────────────────────────────────
    // 키 리바인딩 (InputManager 연계)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 키 리바인딩 시작. 옵션 UI의 "키 변경" 버튼에서 호출.
    /// InputManager에 위임하고 완료/취소/충돌 콜백을 그대로 UI로 전달.
    /// 키 리바인딩은 Draft 대상이 아님(InputManager가 자체 오버라이드/저장 관리). 성공 시 즉시 저장.
    /// </summary>
    public void StartRebind(
        EActionMap map,
        string actionName,
        int bindingIndex = 0,
        Action<string> onComplete = null,
        Action onCancel = null,
        Action<string> onConflict = null)
    {
        if (!InputManager.HasInstance)
        {
            GameLogger.LogWarning(ELogCategory.Input, "리바인딩 실패: InputManager 없음");
            onCancel?.Invoke();
            return;
        }

        InputManager.Instance.StartRebind(
            map, actionName, bindingIndex,
            onComplete: display =>
            {
                InputManager.Instance.SaveRebindsAsync().Forget();
                onComplete?.Invoke(display);
            },
            onCancel: onCancel,
            onConflict: onConflict);
    }

    /// <summary>특정 액션 키를 기본값으로 초기화 + 저장.</summary>
    public void ResetBinding(EActionMap map, string actionName)
    {
        if (!InputManager.HasInstance) return;
        InputManager.Instance.ResetBinding(map, actionName);
        InputManager.Instance.SaveRebindsAsync().Forget();
    }

    /// <summary>모든 키를 기본값으로 초기화 + 저장.</summary>
    public void ResetAllBindings()
    {
        if (!InputManager.HasInstance) return;
        InputManager.Instance.ResetAllBindings();
        InputManager.Instance.SaveRebindsAsync().Forget();
    }

    /// <summary>현재 바인딩 표시 문자열 조회 (옵션 UI 키 라벨용).</summary>
    public string GetBindingDisplay(EActionMap map, string actionName, int bindingIndex = 0)
    {
        if (!InputManager.HasInstance) return string.Empty;
        return InputManager.Instance.GetBindingDisplay(map, actionName, bindingIndex);
    }

    // ─────────────────────────────────────────────
    // 확정 / 취소 / 저장
    // ─────────────────────────────────────────────

    /// <summary>
    /// 편집 내용 확정(확인 버튼). draft를 saved에 반영하고 디스크 저장.
    /// 화면 변경 확인 대기 중이면 먼저 확정 처리한다.
    /// 화면값이 바뀌었는데 ApplyScreenChange를 거치지 않은 경우(값만 기록 후 바로 Commit),
    /// 확정 시점에 즉시 적용해 "확정했는데 화면이 그대로"인 불일치를 막는다.
    /// </summary>
    public void Commit()
    {
        if (_draft == null) return;

        bool previousAnalyticsConsent = _saved.AnalyticsConsent;
        bool wasAnalyticsConsentAsked = _saved.HasAnalyticsConsentBeenAsked;

        // 화면 확인 대기 중이라면 Commit은 화면 OK 의사 → 확정
        if (IsAwaitingScreenConfirm)
        {
            ConfirmScreenChange();
        }
        else if (IsScreenChanged(_draft, _saved))
        {
            // 화면값이 변경됐으나 적용 절차를 안 거친 상태 → 확정 의사이므로 즉시 적용
            // (이미 확인 대기 중이었다면 위 분기에서 처리되므로 여기 안 옴)
            ApplyScreenImmediate(_draft);
        }

        _saved = _draft;
        _draft = null;

        bool analyticsConsentConfirmedOrChanged = previousAnalyticsConsent != _saved.AnalyticsConsent
                                                   || wasAnalyticsConsentAsked != _saved.HasAnalyticsConsentBeenAsked;

        if (SaveManager.HasInstance)
            SaveManager.Instance.SaveAsync(_settingsSlot, _saved).Forget();
        RefreshDirtyState();
        OnSettingsChanged?.Invoke(_saved);

        if (analyticsConsentConfirmedOrChanged)
            OnAnalyticsConsentChanged?.Invoke(IsAnalyticsConsentGranted);
    }

    /// <summary>
    /// 실제 적용된 화면 상태가 지정 설정과 어긋나 있는지.
    /// 값만 바뀌고 화면에는 적용되지 않은 경우를 걸러 불필요한 SetResolution 재호출을 막는다.
    /// </summary>
    private bool IsScreenOutOfSync(GameSettings s)
    {
        if (s == null || _appliedScreenWidth <= 0) return false;

        // Screen.width/height 대신 적용 요청값과 비교한다.
        // 실제 화면 크기는 DPI 스케일·클램프로 요청값과 미세하게 다를 수 있어 오탐이 난다.
        return _appliedScreenWidth != s.ScreenWidth
            || _appliedScreenHeight != s.ScreenHeight
            || _appliedScreenMode != s.ScreenMode;
    }

    /// <summary>두 설정의 화면 항목(해상도/모드/주사율)이 다른지 비교.</summary>
    private static bool IsScreenChanged(GameSettings a, GameSettings b)
    {
        if (a == null || b == null) return false;
        return a.ScreenWidth != b.ScreenWidth
            || a.ScreenHeight != b.ScreenHeight
            || a.ScreenMode != b.ScreenMode
            || a.RefreshRate != b.RefreshRate;
    }

    /// <summary>
    /// 편집 내용 취소(취소 버튼). draft를 버리고 saved 값으로 시스템 재적용(미리보기 원복).
    /// </summary>
    public void Cancel()
    {
        if (_draft == null) return;

        // 화면 확인 대기 중이면 화면도 원복
        if (IsAwaitingScreenConfirm)
        {
            RevertScreenChange();
        }
        else if (IsScreenChanged(_draft, _saved) && IsScreenOutOfSync(_saved))
        {
            // 확인 절차까지 마쳤지만 아직 Commit 안 된 화면 변경이 draft에만 남아 있는 경우.
            // draft를 버리면 설정은 saved 기준이 되므로 화면도 saved로 되돌려야 값·화면 불일치가 없다.
            // (이 처리가 없으면 화면은 변경된 채, 저장값은 이전 값으로 남아 재시작 시 되돌아감)
            // 실제 화면이 이미 saved와 같으면(값만 고르고 적용은 안 한 경우) 재호출하지 않는다 → 불필요한 깜빡임 방지
            ApplyScreenImmediate(_saved);
        }

        _draft = null;

        // saved 기준으로 사운드·그래픽 등 미리보기했던 항목 전부 되돌림
        ApplyGraphics(_saved);
        ApplyFrameRate(_saved);
        ApplyLanguage(_saved);
        OnSettingsChanged?.Invoke(_saved);

        RefreshDirtyState();
    }

    /// <summary>전체 설정을 기본값으로 되돌려 draft에 담음(아직 미확정). Commit으로 확정 필요.</summary>
    public void ResetToDefault()
    {
        _draft = new GameSettings
        {
            ScreenWidth = Screen.currentResolution.width,
            ScreenHeight = Screen.currentResolution.height,
            // 언어 선택 기록은 유지(기본값 초기화로 언어까지 날리면 UX 혼란)
            Language = _saved.Language,
            HasLanguageBeenSet = _saved.HasLanguageBeenSet,
            // 개인정보 수집 동의도 유지. 기본값 복원으로 사용자가 선택한 값을 뒤집지 않는다.
            AnalyticsConsent = _saved.AnalyticsConsent,
            HasAnalyticsConsentBeenAsked = _saved.HasAnalyticsConsentBeenAsked
        };

        // 안전 항목은 미리보기 적용
        ApplyGraphics(_draft);
        ApplyFrameRate(_draft);

        // 미확정 언어 변경이 남아 있으면 화면 텍스트도 saved 기준으로 되돌림
        // (이 처리가 없으면 드롭다운만 원복되고 실제 텍스트는 변경된 언어로 남는다)
        ApplyLanguage(_draft);

        // 기본값은 음소거 해제 상태 → 걸려있던 백그라운드 음소거도 해제 (ResetCategory(Sound)와 일관)
        ApplyBackgroundMute(false);

        RefreshDirtyState();
        // RefreshDirtyState에서 draft가 saved와 같아져 null로 정리될 수 있으므로 Current로 발행
        OnSettingsChanged?.Invoke(Current);
    }

    /// <summary>
    /// 특정 카테고리만 기본값으로 되돌려 draft에 담음(미확정). Commit으로 확정 필요.
    /// 해당 카테고리 항목만 default 값으로 덮어쓰고 나머지는 현재 값 유지.
    /// </summary>
    public void ResetCategory(ESettingsCategory category)
    {
        var d = EnsureDraft();
        var def = new GameSettings(); // 기본값 소스

        switch (category)
        {
            case ESettingsCategory.Sound:
                d.MasterVolume = def.MasterVolume;
                d.BgmVolume = def.BgmVolume;
                d.SfxVolume = def.SfxVolume;
                d.UiVolume = def.UiVolume;
                d.IsMuted = def.IsMuted;
                d.BgmMuted = def.BgmMuted;
                d.SfxMuted = def.SfxMuted;
                d.UiMuted = def.UiMuted;
                d.MuteOnBackground = def.MuteOnBackground;
                ApplyBackgroundMute(false); // 음소거 옵션 초기화 시 현재 음소거 해제
                break;

            case ESettingsCategory.Graphics:
                d.QualityLevel = def.QualityLevel;
                d.VSyncCount = def.VSyncCount;
                d.TargetFrameRate = def.TargetFrameRate;
                d.Brightness = def.Brightness;
                ApplyGraphics(d);
                ApplyFrameRate(d);
                break;

            case ESettingsCategory.Screen:
                // 화면은 위험 항목 → 값만 기본값으로(현재 디스플레이 기준). 적용은 ApplyScreenChange로.
                d.ScreenWidth = Screen.currentResolution.width;
                d.ScreenHeight = Screen.currentResolution.height;
                d.ScreenMode = def.ScreenMode;
                d.RefreshRate = def.RefreshRate;
                break;

            case ESettingsCategory.Gameplay:
                d.VibrationEnabled = def.VibrationEnabled;
                d.ScreenShakeEnabled = def.ScreenShakeEnabled;
                d.FastModeEnabled = def.FastModeEnabled;
                d.TextEffectEnabled = def.TextEffectEnabled;
                d.SkipStartCardPackOpeningAnimation = def.SkipStartCardPackOpeningAnimation;
                break;
        }

        OnSettingsChanged?.Invoke(d);
        RefreshDirtyState();
    }

    // ─────────────────────────────────────────────
    // 내부 헬퍼
    // ─────────────────────────────────────────────

    /// <summary>미리보기 변경 알림. draft 기준으로 이벤트 발행(구독자 즉시 반영). 저장은 안 함.</summary>
    private void PreviewChanged()
    {
        RefreshDirtyState();
        OnSettingsChanged?.Invoke(Current);
    }

    private void RefreshDirtyState()
    {
        if (_draft != null && _draft.ValueEquals(_saved))
            _draft = null;

        bool isDirty = _draft != null;
        if (_lastDirty != isDirty)
        {
            _lastDirty = isDirty;
            OnDirtyChanged?.Invoke(isDirty);
        }
    }

    /// <summary>EScreenMode → Unity FullScreenMode 변환.</summary>
    private FullScreenMode ToFullScreenMode(EScreenMode mode)
    {
        switch (mode)
        {
            case EScreenMode.FullScreen: return FullScreenMode.ExclusiveFullScreen;
            case EScreenMode.FullScreenWindow: return FullScreenMode.FullScreenWindow;
            case EScreenMode.Windowed: return FullScreenMode.Windowed;
            default: return FullScreenMode.FullScreenWindow;
        }
    }
}
