// =================================================================
// [스크립트 목적]  전역 게임 상태 머신 (Title/Loading/InGame/Paused/Result 흐름 추적·전환)
// [주요 변수]      - _state : 현재 게임 상태 (EGameState)
// [의존 관계]      - ManagerBase<GameFlowManager>, TimeManager(일시정지 연동)
//                  - EventManager(GameStateChangedEvent 발행)
// [InitOrder]      108 (GameManager 110 직전 — 매니저 준비 후 Title로 진입)
// [역할 구분]      GameFlowManager = 전역 "상태"만 / GameManager = 앱 생명주기·종료·빌드환경
//                  / 각 씬 컨트롤러 = 실제 "진행" 로직
// =================================================================
using System;
using System.Threading;
using Cysharp.Threading.Tasks;

public class GameFlowManager : ManagerBase<GameFlowManager>
{
    public override int InitOrder => 108;

    private EGameState _state = EGameState.Boot;

    /// <summary>현재 게임 상태</summary>
    public EGameState State => _state;

    /// <summary>현재 일시정지 상태인지 (상태 머신 기준)</summary>
    public bool IsPaused => _state == EGameState.Paused;

    /// <summary>
    /// 상태 전환 시 발행 (이전 상태, 새 상태).
    /// GameFlowManager를 직접 참조하는 구독자용. 느슨한 결합이 필요하면
    /// EventManager의 GameStateChangedEvent를 구독하는 쪽을 권장.
    /// </summary>
    public event Action<EGameState, EGameState> OnStateChanged;

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        // 모든 매니저 준비 완료 후 호출되므로 여기서 Title로 전환
        // (GameManager가 하던 초기 진입 역할을 상태 전담 매니저로 이관)
        SetState(EGameState.Title);
        return UniTask.CompletedTask;
    }

    // ─────────────────────────────────────────────
    // 상태 머신
    // ─────────────────────────────────────────────

    /// <summary>
    /// 게임 상태 전환. 동일 상태는 무시하고, 정의되지 않은 비정상 전이는 경고만 남기고 통과.
    /// (강제 차단하지 않는 이유: 디버그/치트/예외 흐름에서 막히면 더 위험)
    /// </summary>
    public void SetState(EGameState newState)
    {
        if (_state == newState) return;

        // 잘못된 전이 사전 점검 (차단이 아닌 경고 — 디버깅 추적용)
        if (!IsValidTransition(_state, newState))
        {
            GameLogger.LogWarning(
                ELogCategory.Gameplay,
                $"비정상 상태 전이 감지: {_state} → {newState} (그대로 진행하나 흐름 점검 필요)");
        }

        var prev = _state;
        _state = newState;

        OnStateEnter(newState, prev);

        // C# event (직접 참조 구독자) + EventManager(struct, 느슨한 결합) 둘 다 통지
        OnStateChanged?.Invoke(prev, newState);

        if (EventManager.HasInstance)
        {
            EventManager.Instance.Publish(new GameStateChangedEvent
            {
                Prev = prev,
                Current = newState
            });
        }

        GameLogger.Log(ELogCategory.Gameplay, $"게임 상태: {prev} → {newState}");
    }

    /// <summary>
    /// 상태 전이 유효성 판단. 허용된 전이만 true.
    /// 게임 고유 흐름이 확정되면 프로젝트에 맞게 보강할 것.
    /// </summary>
    private bool IsValidTransition(EGameState from, EGameState to)
    {
        switch (from)
        {
            case EGameState.Boot:
                // 부팅 직후엔 타이틀로만
                return to == EGameState.Title;

            case EGameState.Title:
                // 타이틀 → 로딩(게임 시작) 또는 곧장 인게임
                return to == EGameState.Loading || to == EGameState.InGame;

            case EGameState.Loading:
                // 로딩 → 인게임 또는 타이틀 복귀
                return to == EGameState.InGame || to == EGameState.Title;

            case EGameState.InGame:
                // 인게임 → 일시정지 / 결과 / 로딩(다음 스테이지) / 타이틀(중도 이탈)
                return to == EGameState.Paused
                    || to == EGameState.Result
                    || to == EGameState.Loading
                    || to == EGameState.Title;

            case EGameState.Paused:
                // 일시정지 → 인게임 복귀 / 타이틀(포기)
                return to == EGameState.InGame || to == EGameState.Title;

            case EGameState.Result:
                // 결과 → 로딩(재시작/다음) / 타이틀
                return to == EGameState.Loading || to == EGameState.Title;

            default:
                return true;
        }
    }

    /// <summary>
    /// 상태 진입 시 부수 효과 처리.
    /// 일시정지/복귀의 시간 제어(TimeManager)를 여기서 단일 출처로 담당한다.
    /// [참고] 시간 제어 외의 화면 흐름은 SceneFlowManager·각 컨트롤러가 담당한다.
    /// </summary>
    private void OnStateEnter(EGameState state, EGameState prev)
    {
        if (state == EGameState.Paused)
        {
            if (TimeManager.HasInstance) TimeManager.Instance.Pause();
            return;
        }

        // Paused 를 빠져나오는 "모든" 전이에서 시간을 복구한다.
        // InGame 복귀만 처리하면 Paused → Title(포기) / Paused → Loading(씬 전환) /
        // Paused → Result 경로에서 timeScale 0 이 그대로 남아 화면이 얼어붙는다.
        // (Loading/Result 자체의 화면 흐름은 여전히 SceneFlowManager·컨트롤러가 담당)
        if (prev == EGameState.Paused && TimeManager.HasInstance)
            TimeManager.Instance.Resume();
    }

    /// <summary>
    /// 인게임 ↔ 일시정지 토글. 상태 전환만 호출하면 TimeManager 제어는
    /// OnStateEnter가 단일 출처로 처리하므로 시간/상태가 항상 정합된다.
    /// </summary>
    public void TogglePause()
    {
        if (_state == EGameState.InGame) SetState(EGameState.Paused);
        else if (_state == EGameState.Paused) SetState(EGameState.InGame);
    }
}

/// <summary>
/// 게임 전역 상태가 바뀔 때 발행되는 이벤트 (struct → GC 0).
/// GameFlowManager를 직접 참조하지 않고 상태 변화를 구독하려는 시스템용
/// (UI·사운드·분석 등). 구독:
///   _sub = EventManager.Instance.Subscribe&lt;GameStateChangedEvent&gt;(OnGameStateChanged);
/// 해제(OnDisable): _sub.Dispose();
/// </summary>
public struct GameStateChangedEvent
{
    /// <summary>이전 상태</summary>
    public EGameState Prev;

    /// <summary>현재(전환된) 상태</summary>
    public EGameState Current;
}