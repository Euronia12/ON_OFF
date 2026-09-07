// =================================================================
// [스크립트 목적]  Steamworks.NET 래퍼. Steam API 초기화·콜백 펌프·종료를 한 곳에 격리
//                 (게임플레이 코드는 Steam API 직접 호출 금지 → 이 매니저/하위 서비스만 사용)
// [주요 변수]      - STEAM_APP_ID : Steam 앱 ID (App ID) — 중앙 관리 상수
//                  - IsSteamRunning: SteamAPI.Init 성공 여부 (실패해도 게임은 구동)
// [의존 관계]      - ManagerBase<SteamManager>, Steamworks.NET 패키지
//                  - SteamAchievementManager / SteamCloudDebugUtil 등이 IsSteamRunning 참조
// [InitOrder]      0 (가장 먼저 — 저장·분석보다 앞서 Steam 준비)
// [주의]           IsCritical = false. Steam 미실행(에디터 외부)에서도 게임이 죽지 않도록 graceful
// =================================================================
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

public class SteamManager : ManagerBase<SteamManager>
{
    // 저장·분석보다 먼저 Steam이 준비되도록 가장 앞 순서로 초기화.
    public override int InitOrder => 0;

    // Steam 실패가 게임 구동을 막지 않도록 선택적 매니저로 둔다(오프라인·에디터 외부 실행 대비).
    public override bool IsCritical => false;

    // 실제 Steam 파트너 App ID. steam_appid.txt(프로젝트 루트)의 값과 반드시 일치시킬 것.
    private const uint STEAM_APP_ID = 4914570;

    // 주의: ManagerBase에 이미 IsInitialized(매니저 초기화 완료)가 있으므로 이름을 분리한다.
    // (Odin 직렬화 백킹 필드 중복 방지 + 의미 구분: 매니저 초기화 완료 ≠ Steam 연결 성공)
    /// <summary>SteamAPI.Init 성공 여부. false면 업적·클라우드 기능은 스킵된다.</summary>
    public bool IsSteamRunning { get; private set; }

#if !DISABLESTEAMWORKS
    // Steam 경고 메시지 훅 delegate 참조 보관 (GC 수거 방지 — 네이티브가 콜백으로 보유).
    private static SteamAPIWarningMessageHook_t _warningHook;

    /// <summary>
    /// 앱 재시작 유도. Steam 클라이언트 없이 exe를 직접 실행한 경우
    /// Steam을 통해 재기동하도록 처리(에디터에서는 무시됨).
    /// Awake 시점(가능한 한 이른 시점)에 처리한다.
    /// </summary>
    protected override void OnAwakeInternal()
    {
        // 에디터에서는 RestartAppIfNecessary가 항상 false → 무동작.
        try
        {
            if (SteamAPI.RestartAppIfNecessary((AppId_t)STEAM_APP_ID))
            {
                GameLogger.LogWarning(ELogCategory.System,
                    "[SteamManager] Steam을 통해 재시작 필요 — 앱 종료");
                Application.Quit();
            }
        }
        catch (System.DllNotFoundException e)
        {
            // steam_api64.dll 누락 등 — 초기화 자체를 포기하고 게임은 계속.
            GameLogger.LogError(ELogCategory.System,
                $"[SteamManager] Steam 네이티브 로드 실패: {e.Message}");
        }
    }
#endif

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
#if !DISABLESTEAMWORKS
        try
        {
            if (!SteamAPI.Init())
            {
                // Steam 클라이언트 미실행·미로그인·appid 불일치 등. 게임은 계속 구동.
                IsSteamRunning = false;
                GameLogger.LogError(ELogCategory.System,
                    "[SteamManager] SteamAPI.Init 실패 (Steam 미실행/미로그인/AppID 불일치). " +
                    "업적·클라우드 기능 비활성으로 진행");
                return UniTask.CompletedTask;
            }

            IsSteamRunning = true;

            // Steam 내부 경고를 콘솔로 라우팅(개발 빌드 한정으로 유용)
            _warningHook = new SteamAPIWarningMessageHook_t(OnSteamWarningNative);
            SteamClient.SetWarningMessageHook(_warningHook);

            GameLogger.Log(ELogCategory.System,
                $"[SteamManager] 초기화 성공 — User: {SteamFriends.GetPersonaName()}");
        }
        catch (System.DllNotFoundException e)
        {
            IsSteamRunning = false;
            GameLogger.LogError(ELogCategory.System,
                $"[SteamManager] Steam 네이티브 로드 실패: {e.Message}");
        }
#else
        IsSteamRunning = false;
        GameLogger.LogWarning(ELogCategory.System,
            "[SteamManager] DISABLESTEAMWORKS 정의됨 — Steam 비활성");
#endif
        return UniTask.CompletedTask;
    }

#if !DISABLESTEAMWORKS
    // Steam 콜백 펌프. 초기화 성공 시에만 매 프레임 실행.
    private void Update()
    {
        if (IsSteamRunning)
            SteamAPI.RunCallbacks();
    }

    // native 시그니처 경고 훅 (Steamworks.NET 규약)
    [AOT.MonoPInvokeCallback(typeof(SteamAPIWarningMessageHook_t))]
    private static void OnSteamWarningNative(int severity, System.Text.StringBuilder text)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        GameLogger.LogWarning(ELogCategory.System, $"[Steam Warning {severity}] {text}");
#endif
    }
#endif

    /// <summary>종료 시 Steam API 정리. 매니저 역순 종료로 안전하게 호출됨.</summary>
    protected override UniTask OnShutdownInternalAsync()
    {
#if !DISABLESTEAMWORKS
        if (IsSteamRunning)
        {
            SteamAPI.Shutdown();
            IsSteamRunning = false;
            GameLogger.Log(ELogCategory.System, "[SteamManager] SteamAPI.Shutdown");
        }
#endif
        return UniTask.CompletedTask;
    }
}
