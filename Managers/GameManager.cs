// =================================================================
// [스크립트 목적]  앱 생명주기(일시정지/포커스 저장·종료) + 빌드 환경·로그 정책 처리
// [주요 변수]      - _isQuitting    : 종료 처리 중복 진입 가드
//                  - _lastSaveTime  : 생명주기 저장 중복 호출 쿨다운 기준
//                  - _buildEnv      : 빌드 환경 구분 (Dev/Test/Live)
// [의존 관계]      - ManagerBase<GameManager>, BootstrapInitializer, UserManager
//                  - GameLogger(로그 정책 적용)
// [InitOrder]      110 (마지막 — 모든 매니저 준비 후)
// [역할 구분]      GameManager = 앱 생명주기·종료·빌드환경 / GameFlowManager = 전역 "상태"
// =================================================================
using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using System.Threading;

public class GameManager : ManagerBase<GameManager>
{
    public override int InitOrder => 110;

    [Header("게임 흐름 설정")]
    [Tooltip("앱 백그라운드 전환 시 자동 저장")]
    [SerializeField] private bool _saveOnPause = true;

    [Tooltip("앱 종료 시 자동 저장")]
    [SerializeField] private bool _saveOnQuit = true;

    [Space]
    [Header("빌드 환경")]
    [Tooltip("빌드 환경 구분. Live면 로컬 파일 로그 등 개발 기능 비활성.\n출시 빌드 직전 Live로 설정")]
    [SerializeField] private EBuildEnv _buildEnv = EBuildEnv.Dev;

    // 생명주기 저장 중복 호출(Pause+Focus 동시 발화) 방지용 최소 간격 (초, unscaled)
    private const float SAVE_LIFECYCLE_COOLDOWN = 1f;

    // 종료 시퀀스(저장·매니저 정리) 최대 대기 시간(초). 초과 시 미완료 작업을 버리고 강제 종료.
    // 저장/정리가 디스크 멈춤 등으로 무한 대기하면 wantsToQuit이 계속 막아 창을 못 닫는 것을 방지.
    private const float SHUTDOWN_TIMEOUT_SECONDS = 3f;

    private bool _isQuitting;
    // 비동기 종료 시퀀스 완료 후 Application.wantsToQuit 재진입 시 실제 종료를 허용하는 플래그.
    private bool _shutdownComplete;
    private float _lastSaveTime = -999f;
    private float _sessionStartedAt;
    private float _sessionAccumulatedSeconds;
    private bool _isSessionClockRunning;
    private int _runsPlayed;

    /// <summary>현재 빌드 환경. Live면 운영 모드.</summary>
    public EBuildEnv BuildEnv => _buildEnv;

    /// <summary>개발 환경 여부.</summary>
    public bool IsDev => _buildEnv == EBuildEnv.Dev;

    /// <summary>내부 테스트 환경 여부.</summary>
    public bool IsTest => _buildEnv == EBuildEnv.Test;

    /// <summary>출시 환경 여부. 로컬 로그·디버그 기능 차단 판단용.</summary>
    public bool IsLive => _buildEnv == EBuildEnv.Live;

    /// <summary>
    /// 테스트 커맨드 사용 가능 여부.
    /// Dev와 Test 빌드에서만 허용하며, Live 빌드에서는 항상 차단한다.
    /// </summary>
    public bool CanUseTestCommands => IsDev || IsTest;

    /// <summary>현재 실행에서 포그라운드로 플레이한 누적 시간. 저장하지 않는다.</summary>
    public float CurrentSessionPlaySeconds
    {
        get
        {
            float active = _isSessionClockRunning
                ? Mathf.Max(0f, Time.realtimeSinceStartup - _sessionStartedAt)
                : 0f;
            return _sessionAccumulatedSeconds + active;
        }
    }

    protected override void OnAwakeInternal()
    {
        _sessionStartedAt = Time.realtimeSinceStartup;
        _sessionAccumulatedSeconds = 0f;
        _isSessionClockRunning = true;

        // 다른 매니저 초기화보다 먼저 원격 Addressable 서버를 환경에 맞춘다.
        AddressableWrapper.ConfigureForBuildEnvironment(_buildEnv);
    }

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        // 빌드 환경에 맞춰 로그 카테고리 활성 범위 설정.
        ApplyLogPolicy(_buildEnv);
        InputManager.Instance?.RefreshTestCommandMap();
#if ONOFF_TEST_COMMANDS
        DebugCommandManager.EnsureInitialized();
#else
        DebugCommandManager.Shutdown();
#endif
        if (GameFlowManager.HasInstance)
            GameFlowManager.Instance.OnStateChanged += HandleGameStateChanged;

        // 창 X버튼 등 어떤 종료 트리거든 비동기 종료 시퀀스(저장·오디오 정지·매니저 정리)를
        // 거치도록 wantsToQuit를 가로챈다. 데스크톱 스탠드얼론에서 호출된다(모바일은 미호출).
        Application.wantsToQuit += HandleWantsToQuit;
        return UniTask.CompletedTask;
    }

    public void NotifyRunStarted()
    {
        _runsPlayed++;
    }

    protected override UniTask OnShutdownInternalAsync()
    {
        Application.wantsToQuit -= HandleWantsToQuit;
        if (GameFlowManager.HasInstance)
            GameFlowManager.Instance.OnStateChanged -= HandleGameStateChanged;
        StopSessionClock();
        DebugCommandManager.Shutdown();
        if (AnalyticsManager.HasInstance)
            AnalyticsManager.Instance.SaveLogSync();
        return UniTask.CompletedTask;
    }

    /// <summary>
    /// 빌드 환경별 GameLogger 카테고리 정책 적용.
    /// Dev: 전체 / Test: 에러·게임플레이 중심 / Live: 최소(에러는 GameLogger가 항상 출력).
    /// [참고] Release 빌드에서는 GameLogger.Log/Warning이 Conditional로 컴파일 제거되므로
    ///        이 설정은 Editor·Development Build 환경에서의 콘솔 노이즈 제어용.
    /// </summary>
    private void ApplyLogPolicy(EBuildEnv env)
    {
        switch (env)
        {
            case EBuildEnv.Dev:
                // 개발: 모든 카테고리 출력
                GameLogger.SetEnabledCategories(ELogCategory.All);
                break;

            case EBuildEnv.Test:
                // 테스트: 게임플레이·시스템·세이브·분석 위주 (리소스/풀 등 노이즈 제외)
                GameLogger.SetEnabledCategories(
                    ELogCategory.System
                    | ELogCategory.Gameplay
                    | ELogCategory.Save
                    | ELogCategory.Analytics);
                break;

            case EBuildEnv.Live:
                // 출시: 일반 로그 최소화 (Error는 GameLogger 정책상 항상 출력됨)
                GameLogger.SetEnabledCategories(ELogCategory.None);
                break;
        }

        GameLogger.Log(ELogCategory.System, $"빌드 환경: {env} — 로그 정책 적용 완료");
    }

    // ─────────────────────────────────────────────
    // 앱 생명주기
    // ─────────────────────────────────────────────

    private void OnApplicationPause(bool pause)
    {
        if (pause) StopSessionClock();
        else StartSessionClockIfPlayable();

        if (pause && _saveOnPause)
            SaveUserDataSafe();
    }

    private void OnApplicationFocus(bool focus)
    {
        if (focus) StartSessionClockIfPlayable();
        else StopSessionClock();

        // 모바일에서 포커스 잃을 때도 저장 (OnApplicationPause와 거의 동시 발화 가능)
        if (!focus && _saveOnPause)
            SaveUserDataSafe();
    }

    private void HandleGameStateChanged(EGameState previous, EGameState current)
    {
        if (current == EGameState.Paused)
            StopSessionClock();
        else if (previous == EGameState.Paused)
            StartSessionClockIfPlayable();
    }

    private void StartSessionClockIfPlayable()
    {
        if (_isSessionClockRunning || _isQuitting) return;
        if (!Application.isFocused) return;
        if (GameFlowManager.HasInstance && GameFlowManager.Instance.IsPaused) return;
        _sessionStartedAt = Time.realtimeSinceStartup;
        _isSessionClockRunning = true;
    }

    private void StopSessionClock()
    {
        if (!_isSessionClockRunning) return;
        _sessionAccumulatedSeconds += Mathf.Max(0f, Time.realtimeSinceStartup - _sessionStartedAt);
        _isSessionClockRunning = false;
    }

    /// <summary>
    /// 생명주기 저장. Pause+Focus가 거의 동시에 불려 중복 IO가 나는 것을
    /// 쿨다운으로 1회만 처리되도록 가드.
    /// </summary>
    private void SaveUserDataSafe()
    {
        // 종료 처리 중이면 RequestQuit 쪽 저장과 겹치므로 스킵
        if (_isQuitting) return;

        // unscaledTime 사용 — 일시정지(timeScale 0) 중에도 정확
        float now = Time.unscaledTime;
        if (now - _lastSaveTime < SAVE_LIFECYCLE_COOLDOWN) return;
        _lastSaveTime = now;

        if (UserManager.HasInstance && UserManager.Instance.HasUser)
            UserManager.Instance.SaveAsync().Forget();

        // 진행 중인 런도 함께 저장(모바일 백그라운드 킬 대비).
        if (InGameController.HasInstance)
            InGameController.Instance.SaveRunProgressOnLifecycle();
    }

    // ─────────────────────────────────────────────
    // 종료
    // ─────────────────────────────────────────────

    /// <summary>
    /// OS/플랫폼 종료 요청(창 X버튼 등)을 가로채 비동기 종료 시퀀스를 먼저 완주시킨다.
    /// 아직 정리 전이면 false로 이번 종료를 막고 RequestQuit을 돌린다.
    /// 정리 완료(Application.Quit 재호출) 시 재진입하면 true를 반환해 실제 종료를 허용한다.
    /// </summary>
    private bool HandleWantsToQuit()
    {
        if (_shutdownComplete) return true;   // 비동기 정리 끝 → 실제 종료 허용
        if (!_isQuitting) RequestQuit();      // 아직 시작 전이면 정리 개시
        return false;                         // 정리 진행/대기 중 → 이번 종료는 막는다
    }

    /// <summary>
    /// 안전한 게임 종료. 매니저 역순 종료(저장 등) 완료 후 Application.Quit.
    /// 버튼 콜백에서 직접 호출 가능 (async void 특성상 예외 격리됨).
    /// 연타·중복 호출은 _isQuitting 가드로 1회만 진행.
    /// </summary>
    public async void RequestQuit()
    {
        if (_isQuitting) return;
        _isQuitting = true;

        GameLogger.Log(ELogCategory.System, "게임 종료 요청");

        try
        {
            // 저장·정리가 무한 대기(디스크 멈춤 등)로 걸려도 창을 못 닫는 일이 없도록 타임아웃으로 감싼다.
            // Realtime 사용 — 일시정지(timeScale 0) 중 종료해도 타임아웃이 정상 작동한다.
            await RunShutdownSequenceAsync()
                .Timeout(TimeSpan.FromSeconds(SHUTDOWN_TIMEOUT_SECONDS), DelayType.Realtime);
        }
        catch (TimeoutException)
        {
            GameLogger.LogError(ELogCategory.System,
                $"종료 시퀀스 {SHUTDOWN_TIMEOUT_SECONDS}초 초과 — 미완료 작업을 버리고 강제 종료");
        }
        catch (Exception e)
        {
            GameLogger.LogError(ELogCategory.System, $"종료 처리 예외: {e.Message}");
        }

        // 이후 wantsToQuit 재진입 시 즉시 종료를 허용한다(무한 차단 방지).
        _shutdownComplete = true;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    /// <summary>
    /// 실제 종료 저장·정리 시퀀스. RequestQuit이 타임아웃으로 감싸 호출한다.
    /// 유저 데이터 저장 → 진행 런 저장 → 매니저 역순 종료(저장 플러시 등 보장) 순.
    /// </summary>
    private async UniTask RunShutdownSequenceAsync()
    {
        if (_saveOnQuit && UserManager.HasInstance && UserManager.Instance.HasUser)
            await UserManager.Instance.SaveAsync();

        // 진행 중인 런도 종료 전에 저장(정상 종료 경로).
        if (_saveOnQuit && InGameController.HasInstance)
            InGameController.Instance.SaveRunProgressOnLifecycle();

        // 매니저 역순 종료 (저장 플러시 등 보장)
        await BootstrapInitializer.ShutdownAsync();
    }
}
