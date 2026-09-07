// =================================================================
// [스크립트 목적]  전투 총괄 관리자. 페이즈 상태머신 + 턴 흐름 + 시스템 조립
// [주요 변수]      - _player/_enemyHandler : 액터/적 행동 경계 (인터페이스)
//                  - _chain      : 체인 실행기 (카드 배치 즉시 실행)
//                  - _dispatcher : 효과 디스패처
//                  - _cts        : 턴 비동기 취소 토큰
// [의존 관계]      - GridManager / ChainExecutor / CardPhaseDispatcher / CardStatCalculator
//                  - IBattleActor / IEnemyTurnHandler / IChainPresenter
// [설계 노트]      - zip 의 튜토리얼·오염·저주·모션·DamageNumber 는 게임 고유라 제외 (골격만)
//                  - 체인은 카드 배치 즉시 실행 (PlaceCardAsync → ExecuteFromAsync)
//                  - 턴 흐름은 UniTask, 페이즈는 EBattlePhase 상태머신
// =================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 전투 총괄. 페이즈 상태머신을 돌리며 플레이어/적 턴을 교대 진행한다.
/// 카드 배치 시 즉시 체인을 실행하고, 턴 해결은 UniTask 로 비동기 처리한다.
/// 액터·적 행동·연출은 인터페이스로 분리해 게임 고유 로직과 결합하지 않는다.
/// </summary>
public sealed class BattleManager : SceneSingleton<BattleManager>, ICardPlacementService
{

    [Header("연출/대기 (초)")]
    [Tooltip("플레이어 해결 후 적 턴 전 대기")]
    [Min(0f)]
    [SerializeField] private float _interTurnDelay = 0.5f;


    // ── 주입 의존성 (Initialize 로 받음) ───────────────────
    private IBattleActor _player;
    private IBattleActor _enemy;            // 적 액터 (OnDied 구독으로 승리 판정)
    private IEnemyTurnHandler _enemyHandler;
    private GridManager _grid;
    private ChainExecutor _chain;
    private CardPhaseDispatcher _dispatcher;
    private ICardZoneService _cardZone;     // 카드 영역 이동 (턴 종료 그리드/핸드 → 묘지)
    private CancellationTokenSource _cts;

    // 턴 종료 임시 필드 카드 제거용 스크래치 (재사용 → GC 없음).
    private readonly System.Collections.Generic.List<Card> _recoveredScratch =
        new System.Collections.Generic.List<Card>(16);

    // ── 상태 ────────────────────────────────────────────────
    public EBattlePhase CurrentPhase { get; private set; } = EBattlePhase.None;

    private bool _isProcessing;
    /// <summary>처리 중(체인 실행·턴 해결) 여부. 변경 시 OnProcessingChanged 발행.</summary>
    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (_isEnemyPhaseTransitioning && !value) return;
            if (_isProcessing == value) return;
            _isProcessing = value;
            OnProcessingChanged?.Invoke(value);
        }
    }

    public bool IsBattleActive { get; private set; }
    public int PlayerCurrentEnergy => _player is PlayerActor player ? player.Energy : 0;
    public int CurrentEnemyPhaseIndex => _enemy is EnemyActor enemy ? enemy.CurrentPhaseIndex : 0;
    int ICardPlacementService.CurrentEnergy => PlayerCurrentEnergy;

    public event Action<EBattlePhase> OnPhaseChanged;
    public event Action<bool> OnBattleEnded; // true=승리
    public event Action<int, int> OnBeforeEnemyAction; // enemyTurn, intentOrder
    public event Action<int, int> OnPlayerEnergyChanged;

    /// <summary>처리 중(체인 실행·턴 해결) 상태 변화 통지. UI 입력 차단용.</summary>
    public event Action<bool> OnProcessingChanged;

    /// <summary>카드 배치 성공 통지. 튜토리얼 등 선택 시스템이 구독한다.</summary>
    public event Action<Card, Vector2Int> OnCardPlaced;

    /// <summary>카드 배치 성공 후 체인 실행 전 비동기 훅. 구독자가 없으면 즉시 통과한다.</summary>
    public event Func<Card, Vector2Int, CancellationToken, UniTask> OnCardPlacedAsync;

    /// <summary>카드 회수(보드 → 손패) 통지. 인자: 카드, 회수 직전 좌표. 분석 등이 구독한다.</summary>
    public event Action<Card, Vector2Int> OnCardRecalled;

    /// <summary>플레이어 턴 확정 통지. 턴 단위로 조작을 묶는 분석 버퍼가 구독한다.</summary>
    public event Action OnPlayerTurnConfirmed;

    /// <summary>체인 중 카드 토글 통지. bool=true면 ON 전환.</summary>
    public event Action<Card, bool> OnCardToggled;

    /// <summary>체인 중 카드 토글 직후 비동기 훅. 구독자가 없으면 즉시 통과한다.</summary>
    public event Func<Card, bool, CancellationToken, UniTask> OnCardToggledAsync;

    /// <summary>배치 카드로 시작한 체인 종료 연출 훅.</summary>
    public event Func<Card, CancellationToken, UniTask> OnChainEndedAsync;

    /// <summary>적 행동 직전 비동기 연출 훅. 구독자가 없으면 즉시 통과한다.</summary>
    public event Func<int, int, CancellationToken, UniTask> OnBeforeEnemyActionAsync;

    /// <summary>적 행동 완료 직후 비동기 연출 훅. 구독자가 없으면 즉시 통과한다.</summary>
    public event Func<int, int, CancellationToken, UniTask> OnAfterEnemyActionAsync;

    private int _enemyActionTurn;
    private bool _hasPlacedCardThisTurn;
    private bool _isEnemyPhaseTransitioning;
    private Action<CancellationToken> _enemyPhaseTransitionStartedHandler;
    private Func<CancellationToken, UniTask<bool>> _enemyPhaseTransitionHandler;
    private readonly System.Collections.Generic.List<Card> _phaseDiscardScratch =
        new System.Collections.Generic.List<Card>(16);

    // ── 보존(Retain) 상태 ──────────────────────────────────

    // 보존 설정 (런 → 전투 주입). 전체 보존 / n장 보존 한도 / 런 역반영 콜백.
    private PreserveConfig _preserveConfig = PreserveConfig.None;

    // 이번 보존 페이즈에서 플레이어가 선택한 그리드 카드 (턴 단위 임시 — GC 위해 재사용).
    private readonly System.Collections.Generic.HashSet<Card> _preserveSelection =
        new System.Collections.Generic.HashSet<Card>();

    // 지난 턴 플레이어가 "수동 보존"한 카드 기록 (A 방식 자동 재선택용).
    // 카드가 그리드에서 제거되는 즉시(OnCardRemoved) 여기서도 제거해 죽은 카드 참조를 막는다.
    private readonly System.Collections.Generic.HashSet<Card> _lastManualPreserved =
        new System.Collections.Generic.HashSet<Card>();

    // 보존 후보 조회용 스크래치 (재사용 → GC 없음).
    private readonly System.Collections.Generic.List<Card> _preserveCandidateScratch =
        new System.Collections.Generic.List<Card>(16);

    /// <summary>전체 보존 모드 여부 (선택 페이즈 스킵).</summary>
    public bool IsPreserveAll => _preserveConfig != null && _preserveConfig.PreserveAll;

    /// <summary>보존 선택 가능한 최대 장수 (n장 모드). 전체 보존이면 의미 없음.</summary>
    public int MaxPreserveCount => _preserveConfig != null ? _preserveConfig.MaxPreserveCount : 0;

    /// <summary>현재 보존 선택된 장수.</summary>
    public int CurrentPreserveSelectedCount => _preserveSelection.Count;

    /// <summary>보존 선택 변화 통지 (선택수, 최대수). UIWindowPreserve 가 구독해 텍스트 갱신.</summary>
    public event System.Action<int, int> OnPreserveSelectionChanged;

    /// <summary>보존 한도 초과 선택 시도 통지. UI 가 초과 메시지를 띄운다.</summary>
    public event System.Action OnPreserveLimitExceeded;


    // ── 초기화 ──────────────────────────────────────────────

    /// <summary>
    /// 전투 시스템 주입. 씬 전투 시작 전에 1회 호출.
    /// 액터/적 액터/적 핸들러/그리드/체인/디스패처/카드영역서비스를 외부에서 조립해 넘긴다
    /// (Awake 순서 의존 회피). cardZone 은 턴 종료 시 그리드/핸드 카드를 묘지로 보내는 데 쓴다.
    /// </summary>
    public void Initialize(
        IBattleActor player,
        IBattleActor enemy,
        IEnemyTurnHandler enemyHandler,
        GridManager grid,
        ChainExecutor chain,
        CardPhaseDispatcher dispatcher,
        ICardZoneService cardZone)
    {
        if (_chain != null)
        {
            _chain.OnCardToggled -= HandleChainCardToggled;
            _chain.OnCardToggledAsync -= HandleChainCardToggledAsync;
        }

        if (_player is PlayerActor previousPlayer)
            previousPlayer.OnEnergyChanged -= HandlePlayerEnergyChanged;

        _player = player;
        if (_player is PlayerActor currentPlayer)
            currentPlayer.OnEnergyChanged += HandlePlayerEnergyChanged;
        _enemy = enemy;
        _enemyHandler = enemyHandler;
        _grid = grid;
        if (_grid != null)
        {
            _grid.OnCardRemoved -= HandleGridCardRemoved; // 중복 구독 방지
            _grid.OnCardRemoved += HandleGridCardRemoved;
        }
        _chain = chain;
        _dispatcher = dispatcher;
        _cardZone = cardZone;

        if (_chain != null)
        {
            _chain.OnCardToggled += HandleChainCardToggled;
            _chain.OnCardToggledAsync += HandleChainCardToggledAsync;
        }
    }

    /// <summary>전투 시작 전 보존 설정 갱신 (런마다 한도/전체보존 여부가 다를 수 있어 매 전투 주입).</summary>
    public void SetPreserveConfig(PreserveConfig preserveConfig)
    {
        _preserveConfig = preserveConfig ?? PreserveConfig.None;
    }

    /// <summary>
    /// 다음 층 적으로 교체 (선형 등반). OnDied 구독을 이전 적에서 떼고 새 적에 건다.
    /// EnemyView 재바인딩·TargetResolver 갱신은 조립자(BattleController)가 함께 수행한다.
    /// 전투가 진행 중이지 않을 때(전투 시작 직전) 호출하는 것을 전제로 한다.
    /// </summary>
    public void SetEnemy(IBattleActor enemy, IEnemyTurnHandler enemyHandler)
    {
        // 이전 적 사망 구독 해제.
        if (_enemy != null)
        {
            UnbindEnemyEvents();
            _dispatcher?.CancelPendingForTarget(_enemy);
        }

        _dispatcher?.AdvanceEnemyGeneration();
        _enemy = enemy;
        _enemyHandler = enemyHandler;

        // 새 적 사망 구독 (StartBattle 에서 다시 걸지만, 교체 시점에도 안전하게).
        if (_enemy != null)
        {
            BindEnemyEvents();
        }
    }

    /// <summary>중간 보스 페이즈의 데이터·View 교체를 전투 조립자에 위임한다.</summary>
    public void SetEnemyPhaseTransitionHandler(Func<CancellationToken, UniTask<bool>> handler)
    {
        _enemyPhaseTransitionHandler = handler;
    }

    /// <summary>보스 HP가 0이 된 직후, 보드 정리와 병렬로 시작할 연출을 전투 조립자에 위임한다.</summary>
    public void SetEnemyPhaseTransitionStartedHandler(Action<CancellationToken> handler)
    {
        _enemyPhaseTransitionStartedHandler = handler;
    }

    // ── 전투 시작/종료 ──────────────────────────────────────

    /// <summary>전투 시작. 플레이어를 셋업하고 플레이어 입력 페이즈로 진입.</summary>
    /// <param name="playerCurrentHp">시작 현재 체력. 0 이하면 풀피(playerMaxHp).</param>
    public void StartBattle(int playerMaxHp, int playerMaxEnergy = 3, int playerCurrentHp = 0)
    {
        if (_player == null)
        {
            Debug.LogError("[BattleManager] Initialize 가 선행되지 않았습니다.");
            return;
        }

        DisposeCts();
        _cts = new CancellationTokenSource();
        _dispatcher?.BeginBattleGeneration();

        _player.OnDied -= HandlePlayerDied;
        _player.OnDied += HandlePlayerDied;

        // 적 사망 → 승리 판정 (외부 NotifyEnemyDefeated 대신 OnDied 주 경로).
        if (_enemy != null)
        {
            BindEnemyEvents();
        }

        IsBattleActive = true;
        IsProcessing = false;
        _enemyActionTurn = 0;
        _hasPlacedCardThisTurn = false;

        // 튜토리얼 배치 제한 초기화 — 이전 전투의 잠금이 새 전투로 새지 않게.
        _placementLocked = false;
        _allowedPlacementPosition = null;
        _allowedPlacementCard = null;

        _preserveSelection.Clear();
        _lastManualPreserved.Clear();

        // 액터 셋업 — UI 오픈은 BattleController 가 전담(BindActors 선행).
        // 여기서 UIBattle 을 다시 열면 BindActors 로 연결한 위젯과 다른 인스턴스가 떠
        // HP·의도 표시가 끊긴다. 따라서 OpenAsync 호출하지 않는다.
        EnterPhase(EBattlePhase.BattleStart);

        // 플레이어 체력·에너지 셋업. currentHp 가 지정되면 그 체력으로 시작(런 HP 유지).
        int startHp = playerCurrentHp > 0 ? playerCurrentHp : playerMaxHp;
        _player.Setup(playerMaxHp, startHp);
        if (_player is PlayerActor playerActor)
        {
            playerActor.SetupEnergy(playerMaxEnergy);
        }

        // 적 체력·의도 셋업 (OnHpChanged/OnIntentChanged 발행 → 위젯 갱신).
        _enemy?.Setup(playerMaxHp); // 적은 내부적으로 SO 체력 우선 사용

        EnterPhase(EBattlePhase.PlayerInput);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log("[BattleManager] 전투 시작");
#endif
    }

    // ── 카드 배치 (체인 즉시 실행) ──────────────────────────

    /// <summary>
    /// 카드 배치 진입점. 핸드 UI 가 호출. 배치 성공 시 즉시 체인을 실행한다.
    /// 플레이어 입력 페이즈에서만, 처리 중이 아닐 때만 허용.
    /// </summary>
    /// <returns>배치 성공 여부.</returns>
    public UniTask<bool> PlaceCardAsync(Card card, Vector2Int position)
    {
        if (!CanPlaceCard(card, position))
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log("[BattleManager] 현재 카드 또는 슬롯에 배치할 수 없습니다.");
#endif
            return UniTask.FromResult(false);
        }

        PlayerActor playerActor = _player as PlayerActor;
        int cost = card.PlacementCost;
        if (!_grid.TryPlaceCard(card, position))
        {
            return UniTask.FromResult(false);
        }

        card.GridState.SetFirstPlacedThisTurn(!_hasPlacedCardThisTurn);
        _hasPlacedCardThisTurn = true;
        _grid.RefreshCardStats(card);

        // 배치 확정 시점에 에너지 소모 (그리드 배치 성공 후).
        playerActor?.SpendEnergy(cost);

        // 핸드 데이터에서 제거 (UICard 비주얼 제거와 별개로 데이터 정합성 유지).
        _cardZone?.OnCardPlayedToField(card);
        OnCardPlaced?.Invoke(card, position);

        // 체인 실행은 배치와 분리해 비동기로 굴린다 (UICard 즉시 해제 보장).
        // 호출부는 배치 성공(true)을 즉시 받아 UICard 를 딜레이 없이 해제하고,
        // 그리드 카드의 순차 발동/활성화 딜레이는 이 체인 안에서만 일어난다.
        RunChainAsync(card, position).Forget();

        return UniTask.FromResult(true);
    }

    public bool CanPlaceCard(Card card, Vector2Int position)
    {
        if (!IsBattleActive || CurrentPhase != EBattlePhase.PlayerInput || IsProcessing)
            return false;
        if (_placementLocked)
            return false;
        if (_allowedPlacementPosition.HasValue && position != _allowedPlacementPosition.Value)
            return false;
        if (_allowedPlacementCard != null && card != _allowedPlacementCard)
            return false;
        if (_grid == null || _chain == null || card == null || card.IsHandOnlyDisturbance)
            return false;

        GridSlot slot = _grid.GetSlot(position);
        if (slot == null || !slot.IsUsable || !slot.IsEmpty)
            return false;

        PlayerActor playerActor = _player as PlayerActor;
        return playerActor == null || card.CanAffordPlacement(playerActor.Energy);
    }

    // ── 배치 제한 (튜토리얼 시연) ────────────────────────────
    // 시연 중 전체 잠금 → 피날레에 지정 슬롯만 허용하는 용도.
    // CanPlaceCard 가 드롭·호버 프리뷰의 단일 관문이므로 여기서만 판정한다.

    /// <summary>true 면 모든 배치를 차단한다. 전투 시작 시 자동 해제.</summary>
    private bool _placementLocked;

    /// <summary>값이 있으면 해당 좌표에만 배치를 허용한다. 전투 시작 시 자동 해제.</summary>
    private Vector2Int? _allowedPlacementPosition;
    private Card _allowedPlacementCard;

    /// <summary>배치 전체 잠금 설정 (튜토리얼 시연용).</summary>
    public void SetPlacementLocked(bool locked)
    {
        _placementLocked = locked;
    }

    /// <summary>배치 허용 좌표 제한 (튜토리얼 지정 슬롯용). null 이면 제한 없음.</summary>
    public void SetAllowedPlacementPosition(Vector2Int? position)
    {
        _allowedPlacementPosition = position;
    }

    /// <summary>값이 있으면 해당 실제 손패 카드만 배치를 허용한다.</summary>
    public void SetAllowedPlacementCard(Card card)
    {
        _allowedPlacementCard = card;
    }

    // ── 카드 회수 (스킬) ─────────────────────
    // 그리드에 놓인 플레이어 카드를 손패에 되돌린다.
    // 방해 블록(임시 필드 카드)은 플레이어 카드가 아니므로 회수 불가.

    /// <summary>그리드 카드를 회수 가능한지 (페이즈·처리중·플레이어카드).</summary>
    public bool CanRecallCard(Card card)
    {
        if (!IsBattleActive || CurrentPhase != EBattlePhase.PlayerInput || IsProcessing)
            return false;
        if (_grid == null || card == null)
            return false;
        if (card.IsTemporaryFieldOnly)
            return false; // 방해 블록 등 적 임시 카드는 회수 불가
        if (!_grid.TryGetPosition(card, out _))
            return false; // 그리드에 없음

        return true;
    }

    /// <summary>그리드 카드를 회수해 손패로 되돌리고, ManaBurst 외 카드에 이번 턴 1회용 비용 -1 보정을 적용한다.</summary>
    /// <returns>회수 성공 여부 (가드 실패면 false).</returns>
    public bool RecallCard(Card card)
    {
        if (!CanRecallCard(card))
            return false;
        if (!_grid.TryGetPosition(card, out Vector2Int pos))
            return false;

        RecallCardAsync(card, pos, _cts != null ? _cts.Token : CancellationToken.None).Forget();
        return true;
    }

    private async UniTaskVoid RecallCardAsync(Card card, Vector2Int pos, CancellationToken token)
    {
        IsProcessing = true;
        try
        {
            if (_dispatcher != null)
            {
                await _dispatcher.NotifyCardRemovedAsync(card, _player, token);
                if (token.IsCancellationRequested) return;
            }

            // 그리드에서 제거 (RemoveCard 내부에서 OnLeaveGrid 호출 → 부착물·화살표 강화 소멸).
            if (_grid == null || !_grid.TryGetPosition(card, out Vector2Int currentPos) || currentPos != pos)
                return;

            _grid.RemoveCard(pos);
            card.ApplyRecallCostOverride();
            _cardZone?.ReturnFieldCardToHand(card);

            // 외부 구독자(분석 등)에 회수 통지. 좌표는 회수 직전 위치.
            OnCardRecalled?.Invoke(card, pos);

            if (_dispatcher != null)
            {
                await _dispatcher.FlushReactiveTogglesAsync(token);
                if (token.IsCancellationRequested) return;

                var snapshot = new System.Collections.Generic.List<Card>(_grid.PlacedCards);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    await _dispatcher.NotifyGridTopologyChangedAsync(snapshot[i], card, _player, token);
                    if (token.IsCancellationRequested) return;
                }
            }
        }
        finally
        {
            IsProcessing = false;
        }
    }

    /// <summary>
    /// 배치 직후 체인 실행 (ON 전파·순차 발동). 배치 결과 반환과 분리되어
    /// UICard 해제가 체인 딜레이에 묶이지 않도록 한다. 처리 중 플래그로 입력을 잠근다.
    /// </summary>
    private async UniTaskVoid RunChainAsync(Card card, Vector2Int position)
    {
        await RunChainCoreAsync(card, position);
    }

    /// <summary>
    /// 튜토리얼 시연용 배치 + 체인. 배치 제한·마나를 우회하되 체인 파이프라인은
    /// 정상 배치와 동일하게 실행하고, 완료까지 대기한다 (시연 연출 순차 진행용).
    /// 첫 배치 판정(_hasPlacedCardThisTurn)은 건드리지 않아 유저의 마지막 한 수가
    /// "이번 턴 첫 배치"로 계산된다.
    /// </summary>
    public async UniTask<bool> PlaceCardWithChainForTutorialAsync(
        Card card,
        Vector2Int position,
        Action onPlaced = null)
    {
        if (!IsBattleActive || IsProcessing || _grid == null || _chain == null || card == null)
            return false;

        GridSlot slot = _grid.GetSlot(position);
        if (slot == null || !slot.IsUsable || !slot.IsEmpty)
            return false;

        if (!_grid.TryPlaceCard(card, position))
            return false;

        _cardZone?.OnCardPlayedToField(card);
        _grid.RefreshCardStats(card);
        onPlaced?.Invoke();
        await RunChainCoreAsync(card, position);
        return true;
    }

    /// <summary>배치 직후 체인 파이프라인 본체 (토폴로지 갱신 → 배치 훅 → 체인 → 반응 토글 → 마무리).</summary>
    private async UniTask RunChainCoreAsync(Card card, Vector2Int position)
    {
        CancellationToken token = _cts != null ? _cts.Token : CancellationToken.None;
        IsProcessing = true;
        try
        {
            IDisposable reactiveDeferScope = _dispatcher?.ReactiveToggles?.BeginDeferFlushScope();
            try
            {
                if (_dispatcher != null)
                {
                    // 기존 토템 등의 오라를 먼저 새 카드에 반영한다. 이후 카드 자신의
                    // OnOwnerPlacedAsync(배치 시 발동 효과)가 실행되어야 버프 수치로 계산된다.
                    await NotifyGridTopologyChangedAsync(card, token);
                    await _dispatcher.NotifyCardPlacedAsync(card, _player, token);
                }

                // 외부 배치 훅도 토폴로지 및 카드 자체 배치 훅 이후 실행한다.
                if (!IsBattleActive || token.IsCancellationRequested) return;
                await InvokeCardPlacedAsync(card, position, token);
                if (!IsBattleActive || token.IsCancellationRequested) return;

                if (ShouldExecuteOpeningChain(card))
                {
                    await _chain.ExecuteFromAsync(card, token);
                }
                await WaitForPendingDamageAsync(token);
            }
            finally
            {
                reactiveDeferScope?.Dispose();
            }

            if (!IsBattleActive || token.IsCancellationRequested) return;
            if (_dispatcher != null)
            {
                await _dispatcher.FlushReactiveTogglesAsync(token);
            }
            if (!IsBattleActive || token.IsCancellationRequested) return;

            await WaitForPendingDamageAsync(token);
            if (!IsBattleActive || token.IsCancellationRequested) return;

            await InvokeChainEndedAsync(card, token);
        }
        catch (System.OperationCanceledException)
        {
            // 전투 종료·씬 이탈로 취소 — 정상 흐름.
        }
        finally
        {
            if (!IsBattleActive || token.IsCancellationRequested)
            {
                _dispatcher?.ReactiveToggles?.Clear();
            }
            IsProcessing = false;
        }
    }

    private static bool ShouldExecuteOpeningChain(Card card)
    {
        return card != null;
    }

    // ── 플레이어 턴 확정 ───────────────────────────────────

    /// <summary>플레이어가 턴 종료 확정. 손패를 먼저 묘지로 보낸 뒤 보존 선택 페이즈로 진입한다.</summary>
    public void ConfirmPlayerTurn()
    {
        if (CurrentPhase != EBattlePhase.PlayerInput || IsProcessing) return;

        // 외부 구독자(분석 등)에 턴 확정 통지. 실제 해결 전에 쏴야 이번 턴 조작이 묶인다.
        OnPlayerTurnConfirmed?.Invoke();

        ConfirmPlayerTurnAsync(_cts != null ? _cts.Token : CancellationToken.None).Forget();
    }

    private async UniTaskVoid ConfirmPlayerTurnAsync(CancellationToken token)
    {
        IsProcessing = true;
        bool readyForPreserve = false;
        try
        {
            await DiscardHandForTurnEndAsync(token);
            readyForPreserve = IsBattleActive && !token.IsCancellationRequested;
        }
        finally
        {
            if (!readyForPreserve)
                IsProcessing = false;
        }
        if (!readyForPreserve) return;

        // 보존 선택 초기화 — 이전 턴 보존되어 그리드에 남은 카드는 자동 선택 상태로 시작(A 방식).
        BeginPreservePhase();

        // 보존할 수 있는 그리드 카드가 없으면 보존 선택 턴을 기다리지 않고 바로 해결로 진행한다.
        if (!HasPreserveCandidates())
        {
            ApplyPreserveSelectionToGrid();
            _grid?.ClearPreserveHighlights();
            ResolveTurnsAsync(token).Forget();
            return;
        }

        EnterPhase(EBattlePhase.PreserveSelect);
        IsProcessing = false;

        // 전체 보존 모드면 선택 없이 즉시 해결로 진행 (UI 대기 없음).
        if (IsPreserveAll)
        {
            ConfirmPreserveSelect();
        }
    }

    private async UniTask DiscardHandForTurnEndAsync(CancellationToken token)
    {
        if (_cardZone == null) return;

        if (_dispatcher != null)
        {
            await _dispatcher.FlushReactiveTogglesAsync(token);
            if (token.IsCancellationRequested) return;
        }

        // 손패에 남은 저주 카드는 정리 직전 플레이어에게 피해를 준다.
        // 피해 후 DiscardHand 가 IsHandOnlyDisturbance 분기로 소멸시켜 "1회 피해 후 소멸"이 성립한다.
        ApplyHandCurseDamage();

        _cardZone.DiscardHand();

        if (_dispatcher != null)
        {
            await _dispatcher.FlushReactiveTogglesAsync(token);
            if (token.IsCancellationRequested) return;
        }

        await WaitForHandDiscardAnimationsAsync(token);
    }

    /// <summary>손패에 남은 저주 카드(DealsDamageInHandOnTurnEnd)마다 플레이어에게 피해를 준다.</summary>
    private void ApplyHandCurseDamage()
    {
        if (_cardZone == null || _player == null) return;

        IReadOnlyList<Card> hand = _cardZone.Hand;
        if (hand == null) return;

        for (int i = 0; i < hand.Count; i++)
        {
            Card card = hand[i];
            if (card == null || !card.DealsDamageInHandOnTurnEnd || card.HandTurnEndDamage <= 0) continue;
            if (!_player.IsAlive) break;

            _player.TakeDamage(card.HandTurnEndDamage);
        }
    }

    /// <summary>
    /// 보존 페이즈 진입 시 선택 집합을 구성한다.
    /// - 효과(ReserveEffect)로 이미 보존된 카드(IsPreserved): 하이라이트만(선택집합 제외).
    ///   이미 스택이 있어 이번 턴에도 유지되므로 확인 시 중복 AddPreserve 하지 않는다.
    /// - 지난 턴 플레이어가 수동 보존한 카드(_lastManualPreserved)로 그리드에 살아있는 것:
    ///   자동 재선택(선택집합 포함 + 하이라이트). 한도까지만 채운다(초과 방지).
    ///   플레이어가 그대로 확인하면 다시 보존(+1), 빼려면 클릭 해제.
    /// </summary>
    private void BeginPreservePhase()
    {
        _preserveSelection.Clear();
        if (_grid == null || IsPreserveAll) return;

        // 직전 페이즈의 잔여 호버/배치 경로 프리뷰를 정리 (보존 하이라이트와 셰이더 충돌 방지).
        _grid.ClearPlacementPreview();
        _grid.ClearHoverPreview();

        int limit = MaxPreserveCount;
 
        foreach (Card card in _grid.PlacedCards)
        {
            if (card == null || card.IsTemporaryFieldOnly) continue;
            GridCardState state = _grid.GetCardState(card);
 
            // (1) 효과로 이미 보존된 카드 — 연한 파랑 하이라이트만, 선택집합·카운트 제외.
            if (state != null && state.IsPreserved)
            {
                _grid.SetCardPreserveHighlight(card, true, isEffectPreserved: true);
                continue;
            }
 
            // (2) 지난 턴 수동 보존했고 아직 살아있는 카드 — 자동 재선택 (한도 내).
            if (_lastManualPreserved.Contains(card) && _preserveSelection.Count < limit)
            {
                _preserveSelection.Add(card);
                _grid.SetCardPreserveHighlight(card, true);
            }
        }
 
        OnPreserveSelectionChanged?.Invoke(_preserveSelection.Count, limit);
    }
 
    /// <summary>그리드 카드 제거 통지 수신 — 보존 재선택 기록에서 죽은 카드를 즉시 제거(버그 방지).</summary>
    private void HandleGridCardRemoved(Card card)
    {
        if (card == null) return;
        _lastManualPreserved.Remove(card);
        _preserveSelection.Remove(card);
    }

    /// <summary>
    /// 보존 후보(그리드의 배치 카드)를 result 에 채운다. 방해블록도 수동 보존 대상으로 허용한다.
    /// PreserveSelectInput·UI 가 클릭 대상 목록을 만들 때 사용. 호출부가 Clear 책임.
    /// </summary>
    public void CollectPreserveCandidates(System.Collections.Generic.List<Card> result)
    {
        if (result == null || _grid == null) return;
        foreach (Card card in _grid.PlacedCards)
        {
            if (card == null) continue;
            result.Add(card);
        }
    }

    private bool HasPreserveCandidates()
    {
        if (_grid == null) return false;

        foreach (Card card in _grid.PlacedCards)
        {
            if (card != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>해당 카드가 보존 후보(그리드 배치 카드)인지. 방해블록도 수동 보존 대상으로 허용.</summary>
    public bool IsPreserveCandidate(Card card)
    {
        if (card == null || _grid == null) return false;
        return _grid.TryGetPosition(card, out _);
    }

    /// <summary>현재 보존 선택된 카드인지 (UI 하이라이트 판정용).</summary>
    public bool IsPreserveSelected(Card card)
    {
        return card != null && _preserveSelection.Contains(card);
    }

    /// <summary>
    /// 보존 선택 토글 (그리드 카드 클릭). 보존 페이즈에서만 동작.
    /// 한도 초과로 새로 추가하지 못하면 OnPreserveLimitExceeded 를 발행하고 false 반환.
    /// </summary>
    /// <returns>토글 후 그 카드의 선택 상태 (선택=true). 거부 시 기존 상태 유지.</returns>
    public bool TogglePreserveSelection(Card card)
    {
        if (CurrentPhase != EBattlePhase.PreserveSelect || IsPreserveAll) return false;
        if (!IsPreserveCandidate(card)) return false;

        if (_preserveSelection.Contains(card))
        {
            _preserveSelection.Remove(card);
            OnPreserveSelectionChanged?.Invoke(_preserveSelection.Count, MaxPreserveCount);
            return false;
        }

        // 신규 선택 — 한도 검사.
        if (_preserveSelection.Count >= MaxPreserveCount)
        {
            OnPreserveLimitExceeded?.Invoke();
            return false; // 선택 거부 (기존 미선택 유지)
        }

        _preserveSelection.Add(card);
        OnPreserveSelectionChanged?.Invoke(_preserveSelection.Count, MaxPreserveCount);
        return true;
    }

    /// <summary>
    /// 전투 중 보존 한도를 delta 만큼 증가시킨다(카드/유물 효과용). 음수·0 은 무시.
    /// 런에도 역반영(OnMaxCountChanged)해 같은 런의 다음 전투까지 이어지게 한다.
    /// </summary>
    public void AddMaxPreserveCount(int delta)
    {
        if (delta <= 0 || _preserveConfig == null) return;

        int newMax = _preserveConfig.MaxPreserveCount + delta;
        var changed = _preserveConfig.OnMaxCountChanged;
        _preserveConfig = new PreserveConfig(_preserveConfig.PreserveAll, newMax, changed);

        changed?.Invoke(newMax); // 런 원장에 역반영 (같은 런 한정).
        OnPreserveSelectionChanged?.Invoke(_preserveSelection.Count, newMax);
    }

    /// <summary>보존 선택 완료. 선택 결과를 확정하고 플레이어 해결 → 적 턴으로 진행.</summary>
    public void ConfirmPreserveSelect()
    {
        if (CurrentPhase != EBattlePhase.PreserveSelect || IsProcessing) return;

        ApplyPreserveSelectionToGrid();
        _grid?.ClearPreserveHighlights(); // 보존 페이즈 종료 — 선택 테두리 해제.
        ResolveTurnsAsync(_cts.Token).Forget();
    }

    /// <summary>
    /// 보존 선택 결과를 그리드 카드 보존 스택에 더한다(A 방식 + ReserveEffect 공존).
    /// 이번 페이즈에 선택된 카드에만 AddPreserve(1) 한다 → 턴 종료 회수에서 ConsumePreserve 로
    /// 1 소모되며 1턴 유지된다(다음 턴 다시 선택 안 하면 묘지). 효과(ReserveEffect)가 쌓아둔
    /// 스택은 건드리지 않으므로 둘이 합산되어 공존한다(SetPreserveStack 덮어쓰기 미사용).
    /// 또한 이번 선택을 _lastManualPreserved 로 교체해 다음 턴 자동 재선택의 기준으로 삼는다.
    /// 전체 보존 모드는 선택 페이즈를 거치지 않으므로 여기 오지 않는다(스택 부여 없이 전부 유지).
    /// </summary>
    private void ApplyPreserveSelectionToGrid()
    {
        if (_grid == null || IsPreserveAll) return;

        foreach (Card card in _preserveSelection)
        {
            if (card == null) continue;
            card.GridState?.AddPreserve(1); // 방해블록 포함 — 수동 보존 스택 부여.
        }

        // 다음 턴 자동 재선택 기준 갱신 — 이번에 수동 선택한 카드 집합으로 교체.
        _lastManualPreserved.Clear();
        foreach (Card card in _preserveSelection)
        {
            if (card != null) _lastManualPreserved.Add(card);
        }
    }

    /// <summary>플레이어 입력 중 그리드 카드를 직접 묘지로 보낸다.</summary>
    public async UniTask<bool> TryDiscardGridCardAsync(Vector2Int position)
    {
        if (!IsBattleActive || CurrentPhase != EBattlePhase.PlayerInput || IsProcessing)
            return false;
        if (_grid == null || _dispatcher == null || _cardZone == null)
            return false;
        if (!_grid.TryGetOccupiedCard(position, out Card card) || card == null)
            return false;
        if (card.IsTemporaryFieldOnly)
            return false;

        CancellationToken token = _cts != null ? _cts.Token : CancellationToken.None;
        Vector3 worldPosition = Vector3.zero;
        bool hasWorldPosition = _grid.TryGetCardWorldPosition(card, out worldPosition);

        IsProcessing = true;
        try
        {
            await _dispatcher.DiscardGridCardAsync(card, _player, token);
            if (token.IsCancellationRequested) return false;

            if (hasWorldPosition && CardAnimationSystem.HasInstance)
            {
                await CardAnimationSystem.Instance.PlayWorldToDiscardAsync(worldPosition, token);
            }

            return true;
        }
        catch (System.OperationCanceledException)
        {
            return false;
        }
        finally
        {
            IsProcessing = false;
        }
    }

    // ── 턴 해결 (UniTask) ──────────────────────────────────

    /// <summary>플레이어 해결 → 적 턴 → 다음 플레이어 입력까지 진행.</summary>
    private async UniTask ResolveTurnsAsync(CancellationToken token)
    {
        IsProcessing = true;
        try
        {
            // 1) 플레이어 턴 해결.
            EnterPhase(EBattlePhase.PlayerResolve);
            await RunPlayerResolveAsync(token);
            if (!IsBattleActive || token.IsCancellationRequested) return;

            // 플레이어 턴이 완전히 종료되면 이전 적 턴에 쌓인 방어막을 정리한다.
            // 이후 적이 Defend 의도를 실행하면 새 방어막은 다음 플레이어 턴까지 유지된다.
            _enemy?.ResetBlock();

            await UniTask.Delay(TimeSpan.FromSeconds(_interTurnDelay), cancellationToken: token);
            if (!IsBattleActive || token.IsCancellationRequested) return;

            // 2) 적 턴 해결.
            EnterPhase(EBattlePhase.EnemyResolve);
            await RunEnemyResolveAsync(token);
            if (!IsBattleActive || token.IsCancellationRequested) return;

            // 3) 다음 플레이어 입력.
            EnterPhase(EBattlePhase.PlayerInput);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    /// <summary>
    /// 플레이어 턴 종료 처리.
    /// 순서: 턴종료 효과 발동 → 임시 필드 카드 제거 → 핸드 정리(묘지) → 턴 단위 카운트 리셋.
    /// 일반 그리드 카드는 배치와 ON/OFF 상태를 다음 턴까지 유지한다.
    /// </summary>
    private async UniTask RunPlayerResolveAsync(CancellationToken token)
    {
        // 1) 그리드 ON 카드들의 OnTurnEnd 페이즈 효과 실행 (회수 전에 발동해야 함).
        await DispatchPhaseToGridAsync(ETriggerPhase.OnTurnEnd, token);
        if (token.IsCancellationRequested) return;

        await WaitForPendingDamageAsync(token);
        if (_dispatcher != null)
        {
            await _dispatcher.FlushReactiveTogglesAsync(token);
        }
        if (!IsBattleActive || token.IsCancellationRequested) return;

        ClearDurationStateFromGrid(EEffectDuration.EndOfTurn);

        AddGeneratedCardsFromActiveBlocks();

        // 2) 그리드 정리:
        //    - 임시 필드 카드(방해블록): 항상 제거.
        //    - 일반 카드: 보존(PreserveStack>0)이면 ConsumePreserve 로 1 소모하며 유지, 아니면 묘지로.
        //      전체 보존 모드면 일반 카드는 전부 유지(스택 소모 없음).
        //    패시브로 건 화살표 강화·부착물은 보존 카드와 함께 그리드에 남는다.
        await DiscardNonPreservedGridCardsAsync(token);
        if (token.IsCancellationRequested) return;

        // 업적 신호: 정리 후에도 방해블록(적 임시 카드)이 그리드에 남아있으면 "방해카드 보존".
        // 방해블록은 수동 보존(AddPreserve→ConsumePreserve)으로만 생존 가능하다.
        PublishDisturbancePreservedIfAny();

        _grid?.SetCardInfoHidden(false);

        // 3) 턴 종료 시 그리드 턴 단위 카운트만 리셋한다. ON/OFF 상태는 유지한다.
        if (_grid != null)
        {
            _grid.ResetTurnToggleCount();
            foreach (Card card in _grid.PlacedCards)
            {
                _grid.GetCardState(card)?.ResetTurnState();
            }
        }
        _hasPlacedCardThisTurn = false;
    }

    // 그리드에 방해블록(적 임시 카드)이 남아있으면 보존 업적 신호 발행.
    private void PublishDisturbancePreservedIfAny()
    {
        if (_grid == null || !EventManager.HasInstance) return;
        foreach (Card card in _grid.PlacedCards)
        {
            if (card != null && card.IsTemporaryFieldOnly)
            {
                EventManager.Instance.Publish(new DisturbancePreservedSignal());
                return;
            }
        }
    }

    /// <summary>
    /// 턴 종료 그리드 정리: 보존되지 않은 일반 카드 + 임시 필드 카드를 묘지/제거한다.
    /// - 전체 보존 모드: 일반 카드는 전부 유지(스택 소모 없음), 임시 카드만 제거.
    /// - n장 보존 모드: PreserveStack>0 이면 ConsumePreserve()로 1 소모하며 유지(B 방식),
    ///   스택이 없으면 묘지로. → 수동 보존(+1)과 ReserveEffect 스택이 합산되어 함께 소모된다.
    /// 보존 카드의 부착물·화살표 강화는 그리드에 남는다.
    /// dispatcher.DiscardGridCardAsync 를 재사용해 OnOwnerRemoved 훅·토폴로지 통지를 보존.
    /// </summary>
    private async UniTask DiscardNonPreservedGridCardsAsync(CancellationToken token)
    {
        if (_grid == null) return;

        // 순회 중 제거가 일어나므로 스냅샷으로 대상 먼저 수집.
        _preserveCandidateScratch.Clear();
        foreach (Card card in _grid.PlacedCards)
        {
            if (card == null) continue;

            if (card.IsTemporaryFieldOnly)
            {
                // 방해블록: 수동 보존 스택이 있으면 1 소모하고 생존(업적 '방해카드 보존'), 없으면 제거.
                // 전체 보존 모드의 자동 생존은 밸런스상 제외 — 명시적 수동 보존만 허용(기존 동작 유지).
                GridCardState blockState = _grid.GetCardState(card);
                bool blockSurvived = !IsPreserveAll && blockState != null && blockState.ConsumePreserve();
                if (!blockSurvived)
                    _preserveCandidateScratch.Add(card);
                continue;
            }

            // 전체 보존 모드: 일반 카드는 전부 유지 (스택 소모하지 않음).
            if (IsPreserveAll) continue;

            GridCardState state = _grid.GetCardState(card);
            // 보존 스택이 있으면 1 소모하고 유지(B 방식). 소모 후 0 이 돼도 이번 턴은 버틴다.
            // 스택이 없으면(false) 묘지 대상.
            bool survived = state != null && state.ConsumePreserve();
            if (!survived)
            {
                _preserveCandidateScratch.Add(card); // 비보존 일반 카드 — 묘지로.
            }
        }

        int discardedToGraveyard = 0;
        for (int i = 0; i < _preserveCandidateScratch.Count; i++)
        {
            Card card = _preserveCandidateScratch[i];
            if (card == null) continue;

            // 묘지 연출용 월드 좌표 (제거 전 확보).
            bool hasWorldPos = _grid.TryGetCardWorldPosition(card, out Vector3 worldPos);

            if (_dispatcher != null)
            {
                // 임시 카드는 DiscardGridCardAsync 내부에서 묘지 편입을 건너뛴다(IsTemporaryFieldOnly).
                await _dispatcher.DiscardGridCardAsync(card, _player, token);
            }
            else
            {
                // 디스패처가 없으면 직접 제거 (폴백).
                if (_grid.TryGetPosition(card, out Vector2Int pos))
                {
                    _grid.RemoveCard(pos);
                    if (!card.IsTemporaryFieldOnly) _cardZone?.DiscardFromField(card);
                }
            }
            if (token.IsCancellationRequested) return;

            // 일반 카드만 묘지 더미로 날아가는 연출 (임시 카드는 조용히 제거).
            if (!card.IsTemporaryFieldOnly && hasWorldPos && CardAnimationSystem.HasInstance)
            {
                CardAnimationSystem.Instance
                    .PlayWorldToDiscardAsync(worldPos, token)
                    .Forget();
            }

            if (!card.IsTemporaryFieldOnly)
                discardedToGraveyard++;
        }

        // 턴 종료 그리드 회수는 카드 수와 무관하게 효과음을 1회만 재생한다.
        if (discardedToGraveyard > 0 && AudioManager.HasInstance)
            AudioManager.Instance.PlaySfxAsync(AudioKeys.Card.DISCARD).Forget();
    }

    /// <summary>ON 상태 생성 블록의 설정에 따라 전투 임시 카드를 드로우 더미에 섞는다.</summary>
    private void AddGeneratedCardsFromActiveBlocks()
    {
        if (_grid == null || _cardZone == null) return;

        int generatedCount = 0;
        foreach (Card card in _grid.PlacedCards)
        {
            if (card == null || !card.GeneratesCardsOnTurnEnd) continue;

            GridCardState state = _grid.GetCardState(card);
            if (state == null || !state.IsActivated) continue;

            string generatedCardId = card.TurnEndGeneratedCardId;
            int generatedCardCount = card.TurnEndGeneratedCardCount;
            if (string.IsNullOrEmpty(generatedCardId) || generatedCardCount <= 0) continue;

            CardDataSO generatedData = DataManager.HasInstance && DataManager.Instance.IsLoaded
                ? DataManager.Instance.GetData<CardDataSO>(generatedCardId)
                : null;
            if (generatedData == null)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogWarning(
                    $"[BattleManager] 턴 종료 생성 카드 '{generatedCardId}' SO 를 찾지 못했습니다. " +
                    $"원천 카드={card.CardId}");
#endif
                continue;
            }

            for (int i = 0; i < generatedCardCount; i++)
            {
                Card generatedCard = CardFactory.CreateFromSO(generatedData);
                if (generatedCard == null) continue;

                _cardZone.AddCardToDrawPileAndShuffle(
                    generatedCard,
                    EBattleDeckOwner.EnemyTemporary);
                generatedCount++;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (generatedCount > 0)
        {
            Debug.Log($"[BattleManager] ON 생성 블록 발동 — 전투 임시 카드 {generatedCount}장 추가");
        }
#endif
    }

    private async UniTask WaitForHandDiscardAnimationsAsync(CancellationToken token)
    {
        if (!BattleController.HasInstance) return;
        await BattleController.Instance.WaitForPendingCardAnimationsAsync(token);
    }

    private async UniTask WaitForPendingDamageAsync(CancellationToken token)
    {
        if (_dispatcher == null) return;
        await _dispatcher.WaitForPendingDamageAsync(token);
    }

    /// <summary>적 턴 처리. 적 행동 수행 후 플레이어 방어 정리, 의도 전진.</summary>
    private async UniTask RunEnemyResolveAsync(CancellationToken token)
    {
        if (_enemyHandler != null)
        {
            _enemyActionTurn++;
            OnBeforeEnemyAction?.Invoke(_enemyActionTurn, _enemyHandler.CurrentIntentOrder);
            await InvokeBeforeEnemyActionAsync(_enemyActionTurn, _enemyHandler.CurrentIntentOrder, token);
            if (!IsBattleActive || token.IsCancellationRequested) return;

            await _enemyHandler.ExecuteTurnAsync(_player, token);
            if (!IsBattleActive || token.IsCancellationRequested) return;
            await InvokeAfterEnemyActionAsync(_enemyActionTurn, _enemyHandler.CurrentIntentOrder, token);
            if (!IsBattleActive || token.IsCancellationRequested) return;
            _enemyHandler.AdvanceIntent();
        }

        // 적 공격 후 플레이어 방어막 정리 (소모형 블록).
        _player.ResetBlock();
    }

    private async UniTask InvokeBeforeEnemyActionAsync(int enemyTurn, int intentOrder, CancellationToken token)
    {
        if (OnBeforeEnemyActionAsync == null)
        {
            return;
        }

        Delegate[] handlers = OnBeforeEnemyActionAsync.GetInvocationList();
        for (int i = 0; i < handlers.Length; i++)
        {
            if (handlers[i] is Func<int, int, CancellationToken, UniTask> handler)
            {
                await handler.Invoke(enemyTurn, intentOrder, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async UniTask InvokeAfterEnemyActionAsync(int enemyTurn, int intentOrder, CancellationToken token)
    {
        if (OnAfterEnemyActionAsync == null)
        {
            return;
        }

        Delegate[] handlers = OnAfterEnemyActionAsync.GetInvocationList();
        for (int i = 0; i < handlers.Length; i++)
        {
            if (handlers[i] is Func<int, int, CancellationToken, UniTask> handler)
            {
                await handler.Invoke(enemyTurn, intentOrder, token);
                if (!IsBattleActive || token.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async UniTask InvokeCardPlacedAsync(Card card, Vector2Int position, CancellationToken token)
    {
        if (OnCardPlacedAsync == null)
        {
            return;
        }

        Delegate[] handlers = OnCardPlacedAsync.GetInvocationList();
        for (int i = 0; i < handlers.Length; i++)
        {
            if (handlers[i] is Func<Card, Vector2Int, CancellationToken, UniTask> handler)
            {
                await handler.Invoke(card, position, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    /// <summary>그리드의 모든 카드에 특정 페이즈 효과를 순차 디스패치.</summary>
    private async UniTask DispatchPhaseToGridAsync(ETriggerPhase phase, CancellationToken token)
    {
        if (_dispatcher == null) return;

        // 순회 중 컬렉션 변경 대비, 스냅샷으로 복사 후 디스패치.
        var snapshot = new System.Collections.Generic.List<Card>(_grid.PlacedCards);
        for (int i = 0; i < snapshot.Count; i++)
        {
            Card card = snapshot[i];
            GridCardState state = _grid.GetCardState(card);
            if (state == null) continue;
            if (!state.IsActivated && !HasFinisherEffect(card)) continue;

            await _dispatcher.DispatchAsync(card, _player, phase, null, token);
            if (token.IsCancellationRequested) return;
        }
    }

    private static bool HasFinisherEffect(Card card)
    {
        if (card == null) return false;
        for (int i = 0; i < card.Effects.Count; i++)
        {
            if (card.Effects[i] is FinisherDamageEffect)
                return true;
        }
        return false;
    }

    private void ClearDurationStateFromGrid(EEffectDuration duration)
    {
        if (_dispatcher == null || _grid == null) return;

        var snapshot = new System.Collections.Generic.List<Card>(_grid.PlacedCards);
        for (int i = 0; i < snapshot.Count; i++)
        {
            _dispatcher.ClearDurationState(snapshot[i], duration);
        }
    }

    private async UniTask NotifyGridTopologyChangedAsync(Card changedCard, CancellationToken token)
    {
        if (_dispatcher == null || _grid == null) return;

        var snapshot = new System.Collections.Generic.List<Card>(_grid.PlacedCards);
        for (int i = 0; i < snapshot.Count; i++)
        {
            await _dispatcher.NotifyGridTopologyChangedAsync(snapshot[i], changedCard, _player, token);
            if (token.IsCancellationRequested) return;
        }
    }

    // ── 전투 종료 ──────────────────────────────────────────

    private void HandlePlayerDied()
    {
        EndBattle(victory: false);
    }

    /// <summary>적 사망 콜백 (EnemyActor.OnDied 구독). 승리 처리.</summary>
    private void HandleEnemyDied()
    {
        _dispatcher?.CancelPendingForTarget(_enemy);
        EndBattle(victory: true);
    }

    private void HandleEnemyPhaseDepleted(int nextPhaseIndex)
    {
        if (!IsBattleActive || _isEnemyPhaseTransitioning) return;

        _isEnemyPhaseTransitioning = true;
        IsProcessing = true;

        _dispatcher?.CancelPendingForTarget(_enemy);
        _dispatcher?.CancelAllPendingDamage();
        _dispatcher?.ClearCastingQueue();
        _dispatcher?.ReactiveToggles?.Clear();
        _dispatcher?.AdvanceEnemyGeneration();

        DisposeCts();
        _cts = new CancellationTokenSource();

        try
        {
            _enemyPhaseTransitionStartedHandler?.Invoke(_cts.Token);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BattleManager] 보스 페이즈 전환 시작 연출 실패: {ex.Message}");
        }

        TransitionEnemyPhaseAsync(nextPhaseIndex, _cts.Token).Forget();
    }

    private async UniTaskVoid TransitionEnemyPhaseAsync(int nextPhaseIndex, CancellationToken token)
    {
        try
        {
            using (IDisposable suppressScope = _dispatcher?.ReactiveEvents?.BeginSuppressScope())
            {
                _cardZone?.DiscardHand();
                await WaitForHandDiscardAnimationsAsync(token);
            }

            if (!IsBattleActive || token.IsCancellationRequested) return;

            bool advanced = _enemyPhaseTransitionHandler != null &&
                await _enemyPhaseTransitionHandler.Invoke(token);
            if (!IsBattleActive || token.IsCancellationRequested) return;

            if (!advanced)
            {
                Debug.LogError($"[BattleManager] 보스 {nextPhaseIndex + 1}페이즈 전환 실패 — 전투를 승리 처리합니다.");
                EndBattle(victory: true);
                return;
            }

            EnterPhase(EBattlePhase.EnemyResolve);
            await RunEnemyResolveAsync(token);
            if (!IsBattleActive || token.IsCancellationRequested) return;

            EnterPhase(EBattlePhase.PlayerInput);
        }
        catch (OperationCanceledException)
        {
            // 전투 종료·씬 이탈로 취소 — 정상 흐름.
        }
        finally
        {
            _isEnemyPhaseTransitioning = false;
            IsProcessing = false;
        }
    }

    public async UniTask DiscardGridCardsForEnemyPhaseAsync(CancellationToken token)
    {
        using (IDisposable suppressScope = _dispatcher?.ReactiveEvents?.BeginSuppressScope())
        {
            await DiscardAllGridCardsForPhaseAsync(token);
        }
    }

    private async UniTask DiscardAllGridCardsForPhaseAsync(CancellationToken token)
    {
        if (_grid == null) return;

        _phaseDiscardScratch.Clear();
        foreach (Card card in _grid.PlacedCards)
        {
            if (card != null) _phaseDiscardScratch.Add(card);
        }

        int discardedToGraveyard = 0;
        for (int i = 0; i < _phaseDiscardScratch.Count; i++)
        {
            Card card = _phaseDiscardScratch[i];
            bool hasWorldPosition = _grid.TryGetCardWorldPosition(card, out Vector3 worldPosition);

            if (_dispatcher != null)
            {
                await _dispatcher.DiscardGridCardAsync(card, _player, token);
            }
            else if (_grid.TryGetPosition(card, out Vector2Int position))
            {
                _grid.RemoveCard(position);
                if (!card.IsTemporaryFieldOnly) _cardZone?.DiscardFromField(card);
            }

            if (token.IsCancellationRequested) return;

            if (!card.IsTemporaryFieldOnly)
            {
                discardedToGraveyard++;
                if (hasWorldPosition && CardAnimationSystem.HasInstance)
                {
                    CardAnimationSystem.Instance.PlayWorldToDiscardAsync(worldPosition, token).Forget();
                }
            }
        }

        _phaseDiscardScratch.Clear();
        _preserveSelection.Clear();
        _lastManualPreserved.Clear();
        _grid.EndLineAttackVfx();
        _grid.RestoreBrokenSlotsImmediate();
        _grid.ClearPlacementPreview();
        _grid.ClearHoverPreview();
        _grid.ClearPreserveHighlights();
        _grid.ClearEnemyLineIntentWarning();
        _grid.SetCardInfoHidden(false);
        _grid.ResetTurnToggleCount();
        _hasPlacedCardThisTurn = false;
        OnPreserveSelectionChanged?.Invoke(0, MaxPreserveCount);

        if (discardedToGraveyard > 0 && AudioManager.HasInstance)
        {
            AudioManager.Instance.PlaySfxAsync(AudioKeys.Card.DISCARD).Forget();
        }
    }

    /// <summary>적 사망 수동 통지 (적 액터 미사용 구조·외부 강제 승리용). 가급적 OnDied 경로 사용.</summary>
    public void NotifyEnemyDefeated()
    {
        EndBattle(victory: true);
    }

    private void EndBattle(bool victory)
    {
        if (!IsBattleActive) return;

        _dispatcher?.CancelAllPendingDamage();
        _dispatcher?.ClearCastingQueue();

        IsBattleActive = false;
        IsProcessing = false;
        _grid?.SetCardInfoHidden(false);
        EnterPhase(EBattlePhase.BattleEnd);

        // 진행 중 턴 비동기 취소.
        _cts?.Cancel();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[BattleManager] 전투 종료 — {(victory ? "승리" : "패배")}");
#endif
        OnBattleEnded?.Invoke(victory);
    }

    private void BindEnemyEvents()
    {
        if (_enemy == null) return;

        _enemy.OnDied -= HandleEnemyDied;
        _enemy.OnDied += HandleEnemyDied;
        if (_enemy is EnemyActor enemyActor)
        {
            enemyActor.OnPhaseDepleted -= HandleEnemyPhaseDepleted;
            enemyActor.OnPhaseDepleted += HandleEnemyPhaseDepleted;
        }
    }

    private void UnbindEnemyEvents()
    {
        if (_enemy == null) return;

        _enemy.OnDied -= HandleEnemyDied;
        if (_enemy is EnemyActor enemyActor)
        {
            enemyActor.OnPhaseDepleted -= HandleEnemyPhaseDepleted;
        }
    }

    // ── 내부 ───────────────────────────────────────────────

    private void EnterPhase(EBattlePhase phase)
    {
        CurrentPhase = phase;

        IDisposable reactiveSuppressScope = phase == EBattlePhase.PlayerInput
            ? _dispatcher?.ReactiveEvents?.BeginSuppressScope()
            : null;

        try
        {
            // 플레이어 입력 페이즈 진입 = 턴 시작 → 에너지 완전 회복 (슬더슬 방식).
            if (phase == EBattlePhase.PlayerInput && _player is PlayerActor playerActor)
            {
                playerActor.RefillEnergy();
            }

            OnPhaseChanged?.Invoke(phase);
        }
        finally
        {
            reactiveSuppressScope?.Dispose();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[BattleManager] Phase → {phase}");
#endif
    }

    private void HandleChainCardToggled(Card card, bool activated)
    {
        OnCardToggled?.Invoke(card, activated);
    }

    private void HandlePlayerEnergyChanged(int current, int max)
    {
        OnPlayerEnergyChanged?.Invoke(current, max);
    }

    private async UniTask HandleChainCardToggledAsync(Card card, bool activated, CancellationToken token)
    {
        if (OnCardToggledAsync == null)
        {
            return;
        }

        Delegate[] handlers = OnCardToggledAsync.GetInvocationList();
        for (int i = 0; i < handlers.Length; i++)
        {
            if (handlers[i] is Func<Card, bool, CancellationToken, UniTask> handler)
            {
                await handler.Invoke(card, activated, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async UniTask InvokeChainEndedAsync(Card card, CancellationToken token)
    {
        if (OnChainEndedAsync == null)
        {
            return;
        }

        Delegate[] handlers = OnChainEndedAsync.GetInvocationList();
        for (int i = 0; i < handlers.Length; i++)
        {
            if (handlers[i] is Func<Card, CancellationToken, UniTask> handler)
            {
                await handler.Invoke(card, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private void DisposeCts()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }

    protected override void OnDestroy()
    {
        if (_chain != null)
        {
            _chain.OnCardToggled -= HandleChainCardToggled;
            _chain.OnCardToggledAsync -= HandleChainCardToggledAsync;
        }
        if (_player != null)
        {
            _player.OnDied -= HandlePlayerDied;
            if (_player is PlayerActor playerActor)
                playerActor.OnEnergyChanged -= HandlePlayerEnergyChanged;
        }
        if (_enemy != null)
        {
            UnbindEnemyEvents();
        }
        if (_grid != null)
        {
            _grid.OnCardRemoved -= HandleGridCardRemoved;
        }
        OnPreserveSelectionChanged = null;
        OnPreserveLimitExceeded = null;
        DisposeCts();
        OnPhaseChanged = null;
        OnBattleEnded = null;
        OnBeforeEnemyAction = null;
        OnPlayerEnergyChanged = null;
        OnProcessingChanged = null;
        OnCardPlaced = null;
        OnCardPlacedAsync = null;
        OnCardToggled = null;
        OnCardToggledAsync = null;
        OnChainEndedAsync = null;
        _enemyPhaseTransitionStartedHandler = null;
        _enemyPhaseTransitionHandler = null;
        OnBeforeEnemyActionAsync = null;
        OnAfterEnemyActionAsync = null;
        base.OnDestroy();
    }
}
