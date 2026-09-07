// =================================================================
// [스크립트 목적]  Steam 업적(SteamUserStats) 해금·조회를 격리하는 매니저
//                 게임플레이 코드는 enum(EAchievement)으로만 요청 → Magic String 차단
// [주요 변수]      - _apiNames  : EAchievement → Steam API Name(string) 매핑
//                  - _statsReady: UserStatsReceived 콜백으로 초기 stat 로드 완료 여부
// [의존 관계]      - ManagerBase<SteamAchievementManager>, SteamManager, Steamworks.NET
// [InitOrder]      5 (SteamManager=0 다음)
// [주의]           IsCritical=false. Steam 미초기화 시 모든 호출은 no-op
// =================================================================
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

/// <summary>
/// Steam 업적 식별자. 게임 코드는 이 enum으로만 업적을 다룬다(Magic String 차단).
/// 판정 로직은 SteamAchievementBridge 가 전담한다.
/// </summary>
public enum EAchievement
{
    FirstWin,        // 첫 전투 승리
    ClearStage1,     // 1스테이지 클리어
    ClearStage2,     // 2스테이지 클리어
    GameClear,       // 게임 클리어
    GetLegendary,    // 레전더리 등급 카드 획득
    InfiniteLoop,    // 무한 루프 성공

    // ── 게임플레이 특화 업적 (SteamAchievementBridge가 EventManager 신호로 판정) ──
    ManaOver10,           // 마나(Energy) 10 이상 도달
    GlassBladeZero,       // 유리칼 타입 데미지가 0 도달
    AllCardsActiveOnKill, // 적 사망 시 그리드 전 카드 활성
    CastingResolved,      // 캐스팅 카운트 소진으로 효과 발동
    OffspringArrow,       // 분신 카드에 화살표 추가
    BlockGridCut,         // 마지막 보스 그리드 자르기를 카드로 막음
    ArrowWhileBlind,      // 첫 보스 실명(EraseInfo) 중 카드에 화살표 추가
    PreserveDisturbance,  // 적 방해카드를 보존
    EightPendingCasts,    // 동시 시전대기 캐스팅 8개
    ShardAllArrows,       // 파편화살에 4방위 전부 부여
    Predate7InOne,        // 같은 포식 카드로 누적 7장 포식
    NoSkillClear,         // 스킬 미사용 클리어
    GridFull,             // 그리드 전 슬롯 채움(빈 슬롯 0)
    HellClear,            // Hell 난이도 게임 클리어
}

public class SteamAchievementManager : ManagerBase<SteamAchievementManager>
{
    public override int InitOrder => 5;
    public override bool IsCritical => false;

    // EAchievement → Steam 파트너사이트에 등록한 API Name.
    // ★ 파트너사이트의 API Name 과 문자열이 정확히 일치해야 한다(한 글자만 달라도 해당 업적만 조용히 실패).
    private static readonly Dictionary<EAchievement, string> _apiNames = new()
    {
        { EAchievement.FirstWin,             "ACH_FIRST_WIN" },
        { EAchievement.ClearStage1,          "ACH_STAGE_CLEAR_1" },
        { EAchievement.ClearStage2,          "ACH_STAGE_CLEAR_2" },
        { EAchievement.GameClear,            "ACH_GAME_CLEAR" },
        { EAchievement.HellClear,             "ACH_HELL_CLEAR" },
        { EAchievement.GetLegendary,         "ACH_GET_LEGENDARY" },
        { EAchievement.InfiniteLoop,         "ACH_INFINITE_LOOP" },

        { EAchievement.ManaOver10,           "ACH_MANA_OVER_10" },
        { EAchievement.GlassBladeZero,       "ACH_GLASSBLADE_ZERO" },
        { EAchievement.AllCardsActiveOnKill, "ACH_ALL_ACTIVE_ON_KILL" },
        { EAchievement.CastingResolved,      "ACH_CASTING_RESOLVED" },
        { EAchievement.OffspringArrow,       "ACH_OFFSPRING_ARROW" },
        { EAchievement.BlockGridCut,         "ACH_BLOCK_GRID_CUT" },
        { EAchievement.ArrowWhileBlind,      "ACH_ARROW_WHILE_BLIND" },
        { EAchievement.PreserveDisturbance,  "ACH_PRESERVE_DISTURBANCE" },
        { EAchievement.EightPendingCasts,    "ACH_EIGHT_PENDING_CASTS" },
        { EAchievement.ShardAllArrows,       "ACH_SHARD_ALL_ARROWS" },
        { EAchievement.Predate7InOne,        "ACH_PREDATE_7_IN_ONE" },
        { EAchievement.NoSkillClear,         "ACH_NO_SKILL_CLEAR" },
        { EAchievement.GridFull,             "ACH_GRID_FULL" },
    };

    private bool _statsReady;

    // 세션 로컬 해금 캐시. 이미 해금된 업적은 Steam 재조회 없이 즉시 스킵한다
    // → 조건형 업적(마나 10↑ 등)이 무한루프에서 반복 신호로 들어와도 부담 최소화.
    private readonly HashSet<EAchievement> _unlockedCache = new HashSet<EAchievement>();

#if !DISABLESTEAMWORKS
    private Callback<UserStatsReceived_t> _statsReceivedCallback;
#endif

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        if (!IsSteamReady())
        {
            GameLogger.LogWarning(ELogCategory.System,
                "[Achievement] Steam 미초기화 — 업적 서비스 비활성");
            return UniTask.CompletedTask;
        }

#if !DISABLESTEAMWORKS
        // 최신 Steam SDK(1.57+)는 RequestCurrentStats가 제거됨.
        // 로컬 유저 stat/업적은 SteamAPI.Init 성공 시 자동 로드되므로 준비 완료로 간주한다.
        _statsReady = true;

        // 서버에서 stat이 갱신 수신될 때(다른 기기 동기화 등) 준비 상태를 재확인하는 콜백은 유지.
        _statsReceivedCallback = Callback<UserStatsReceived_t>.Create(OnUserStatsReceived);

        // 초기 1회: 이미 해금된 업적을 로컬 캐시에 미리 채워 "선판정" 효과를 얻는다.
        RefreshUnlockedCache();
#endif
        return UniTask.CompletedTask;
    }

    private static bool IsSteamReady()
        => SteamManager.HasInstance && SteamManager.Instance.IsSteamRunning;

#if !DISABLESTEAMWORKS
    // 현재 유저(본인)의 stat 수신 시에만 준비 완료 처리.
    private void OnUserStatsReceived(UserStatsReceived_t cb)
    {
        if (cb.m_eResult != EResult.k_EResultOK) return;
        _statsReady = true;

        // 다른 기기에서 해금돼 동기화 수신된 상태도 캐시에 반영.
        RefreshUnlockedCache();
    }

    // Steam의 현재 해금 상태를 읽어 로컬 캐시를 채운다(초기 1회 + stat 동기화 수신 시).
    private void RefreshUnlockedCache()
    {
        if (!_statsReady) return;
        foreach (KeyValuePair<EAchievement, string> pair in _apiNames)
        {
            if (SteamUserStats.GetAchievement(pair.Value, out bool unlocked) && unlocked)
                _unlockedCache.Add(pair.Key);
        }
    }
#endif

    // ─────────────────────────────────────────────
    // 업적 해금 / 조회 (게임 코드가 이벤트로 호출)
    // ─────────────────────────────────────────────

    /// <summary>업적 해금. 이미 해금됐거나 Steam 미초기화면 무동작(중복 StoreStats 방지).</summary>
    public void Unlock(EAchievement achievement)
    {
        // 튜토리얼 런 중에는 모든 업적 판정 제외 (분석 집계 제외와 동일 플래그 사용).
        if (TutorialDirector.IsActive) return;

        // 이미 해금된 업적은 Steam 재조회 없이 즉시 종료 (반복 신호 부담 차단).
        if (_unlockedCache.Contains(achievement)) return;

        if (!IsSteamReady() || !_statsReady) return;

#if !DISABLESTEAMWORKS
        if (!_apiNames.TryGetValue(achievement, out string apiName))
        {
            GameLogger.LogWarning(ELogCategory.System, $"[Achievement] 매핑 없음: {achievement}");
            return;
        }

        // 중복 해금 방지 — 이미 unlocked면 StoreStats 생략(캐시에도 기록해 다음부턴 조회 생략).
        if (SteamUserStats.GetAchievement(apiName, out bool unlocked) && unlocked)
        {
            _unlockedCache.Add(achievement);
            return;
        }

        SteamUserStats.SetAchievement(apiName);
        SteamUserStats.StoreStats(); // 즉시 서버 반영(팝업 표시 트리거)
        _unlockedCache.Add(achievement);

        GameLogger.Log(ELogCategory.System, $"[Achievement] 해금: {achievement}");
#endif
    }

    /// <summary>업적 해금 여부 조회. 미초기화·매핑 없음이면 false.</summary>
    public bool IsUnlocked(EAchievement achievement)
    {
        if (!IsSteamReady() || !_statsReady) return false;

#if !DISABLESTEAMWORKS
        if (!_apiNames.TryGetValue(achievement, out string apiName)) return false;
        return SteamUserStats.GetAchievement(apiName, out bool unlocked) && unlocked;
#else
        return false;
#endif
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    /// <summary>디버그용 업적 초기화(리셋). 개발 빌드에서만 컴파일.</summary>
    public void ClearAchievement(EAchievement achievement)
    {
        if (!IsSteamReady()) return;
#if !DISABLESTEAMWORKS
        if (!_apiNames.TryGetValue(achievement, out string apiName)) return;
        SteamUserStats.ClearAchievement(apiName);
        SteamUserStats.StoreStats();
        _unlockedCache.Remove(achievement); // 리셋 후 재판정 가능하도록 캐시에서도 제거.
        GameLogger.Log(ELogCategory.System, $"[Achievement] 초기화: {achievement}");
#endif
    }
#endif

    /// <summary>
    /// Dev/Test 전용 Steam 업적 전체 초기화. Live 환경에서는 항상 거부한다.
    /// 모든 업적을 지운 뒤 StoreStats를 한 번만 호출한다.
    /// </summary>
    public bool ClearAllAchievementsForTesting()
    {
        if (!GameManager.HasInstance || !GameManager.Instance.CanUseTestCommands ||
            GameManager.Instance.IsLive || !IsSteamReady() || !_statsReady)
            return false;

#if !DISABLESTEAMWORKS
        bool allCleared = true;
        foreach (KeyValuePair<EAchievement, string> pair in _apiNames)
        {
            if (SteamUserStats.ClearAchievement(pair.Value))
                _unlockedCache.Remove(pair.Key);
            else
                allCleared = false;
        }

        bool stored = SteamUserStats.StoreStats();
        if (allCleared && stored)
        {
            GameLogger.Log(ELogCategory.System, "[Achievement] 전체 초기화 완료");
            return true;
        }

        GameLogger.LogWarning(ELogCategory.System,
            $"[Achievement] 전체 초기화 일부 실패 (Clear={allCleared}, Store={stored})");
        return false;
#else
        return false;
#endif
    }

    protected override void OnDestroy()
    {
#if !DISABLESTEAMWORKS
        _statsReceivedCallback?.Dispose();
        _statsReceivedCallback = null;
#endif
        base.OnDestroy();
    }
}
