// =================================================================
// [스크립트 목적]  월드 그리드 관리자. 슬롯 생성·카드 배치·집계(IGridStatProvider)
//                 + ON/OFF 활성 비주얼을 슬롯에 반영.
// [주요 변수]      - _slots          : Vector2Int → 슬롯 (그리드 본체)
//                  - _cardPositions  : Card → Vector2Int 역인덱스 (집계 O(1) 조회용)
//                  - _rows/_columns  : 그리드 크기
//                  - _cellSize/_origin : 월드 좌표 자동 생성 파라미터
// [의존 관계]      - GridSlot / Card / ECardDirection / IGridStatProvider
//                  - 핸드 입력 어댑터(CardPlacementInput)가 TryPlaceCard 진입점 호출
//                  - 카드 비주얼(화살표/ON강조)은 GridSlot 이 직접 표현
// [설계 노트]      - 카드 비주얼은 GridSlot 이 흡수 (GridCardObject/IGridCardView 폐기).
//                  - 방향은 카드 생성 시 확정된 Arrow.Current 를 슬롯이 표시(AssignCard 내부).
//                  - 카드 활성 상태(GridCardState)는 "카드가 소유" → 배치/회수마다 생성·폐기
//                    하지 않음(GC 없음). GridManager 는 그 상태를 슬롯 비주얼에 반영만 한다.
//                  - 셀 단위 비주얼/하이라이트는 GridSlot, 일괄 제어는 GridManager.HighlightSlot.
// =================================================================

using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 월드 공간 그리드 관리자.
/// 슬롯 생성(자동/수동 병행), 카드 데이터 배치, 그리드 집계, ON/OFF 비주얼 반영을 담당한다.
/// 카드 비주얼(화살표·활성강조)은 GridSlot 이 직접 표현하고, 입력은 어댑터가 TryPlaceCard 로 호출한다.
/// Card↔위치 역인덱스를 보유해 Row/Column/Cross 집계를 O(1) 위치 조회 후 수행한다.
/// </summary>
public sealed class GridManager : SceneSingleton<GridManager>, IGridStatProvider, ISkillApplyService
{
    [Header("슬롯 참조")]
    [Tooltip("씬에 수동 배치한 슬롯들. 비어있으면 _autoBuild 로 자동 생성")]
    [SerializeField] private List<GridSlot> _presetSlots = new List<GridSlot>();

    [Tooltip("자동 생성용 슬롯 프리팹 (_autoBuild = true 일 때 사용)")]
    [SerializeField] private GridSlot _slotPrefab;

    [Tooltip("자동 생성 슬롯의 부모 Transform")]
    [SerializeField] private Transform _slotRoot;

    [Header("그리드 설정")]
    [Tooltip("자동 생성 여부. true 면 rows×columns 격자를 코드로 생성")]
    [SerializeField] private bool _autoBuild = true;

    [Min(1)]
    [SerializeField] private int _rows = 4;

    [Min(1)]
    [SerializeField] private int _columns = 4;

    [Header("월드 좌표 (자동 생성 시)")]
    [Tooltip("셀 1칸의 월드 크기 (m). 슬롯 간 간격")]
    [SerializeField] private Vector2 _cellSize = new Vector2(1.75f, 1.75f);

    [Tooltip("슬롯 프리팹 원본 크기가 경계에 딱 맞는 기준 셀 크기")]
    [SerializeField] private Vector2 _slotScaleReferenceCellSize = new Vector2(1.6f, 1.6f);

    [Tooltip("그리드 (0,0) 슬롯의 월드 기준 위치")]
    [SerializeField] private Vector3 _origin = Vector3.zero;

    [Header("적 Intent 경고")]
    [Tooltip("선형 공격 경고 스프라이트 표시 Presenter")]
    [SerializeField] private EnemyLineIntentWarningPresenter _enemyLineIntentWarningPresenter;

    [Tooltip("선형 공격이 다음 슬롯으로 진행되기까지의 간격")]
    [Min(0f)]
    [SerializeField] private float _lineAttackStepDelay = 0.08f;

    [Tooltip("선형 공격 진행 위치를 전달받을 선택적 전용 VFX 프리팹")]
    [SerializeField] private GameObject _lineAttackVfxPrefab;

    [Header("그리드 테두리")]
    [Tooltip("그리드를 감싸는 테두리 프리팹의 Addressable 키. 비우면 테두리 미사용")]
    [SerializeField] private string _borderKey = "GridBorder";

    [Tooltip("그리드 외곽으로 더 키울 여백 (m). 값이 클수록 테두리가 그리드 바깥으로 더 커짐")]
    [SerializeField] private Vector2 _borderPadding = new Vector2(0.5f, 0.5f);

    [Tooltip("테두리 z 오프셋 (m). 양수일수록 슬롯보다 뒤로 깔림")]
    [Min(0f)]
    [SerializeField] private float _borderDepth = 0.1f;

    public int Rows => _rows;
    public int Columns => _columns;
    public float LineAttackStepDelay => _lineAttackStepDelay;
    public int ActivationVersion { get; private set; }
    public bool IsCardInfoHidden { get; private set; }
    public bool IsCardInfoHiddenKeepArrow => _cardInfoHiddenKeepArrow;

    // 그리드 본체: 좌표 → 슬롯.
    private readonly Dictionary<Vector2Int, GridSlot> _slots = new Dictionary<Vector2Int, GridSlot>();
    public IReadOnlyDictionary<Vector2Int, GridSlot> Slots => _slots;

    // 자동 생성 슬롯 풀: 생성된 좌표 → 슬롯. 스테이지별 활성 범위만 _slots 로 노출한다.
    private readonly Dictionary<Vector2Int, GridSlot> _pooledSlots = new Dictionary<Vector2Int, GridSlot>();

    // 역인덱스: 배치된 카드 → 좌표 (집계 시 카드 위치 O(1) 조회).
    private readonly Dictionary<Card, Vector2Int> _cardPositions = new Dictionary<Card, Vector2Int>();

    // 이번 턴 그리드 전체 ON 횟수.
    private int _turnToggleCount;

    // 카드 정보 은폐 시 방향 화살표를 유지하는지(EraseCardKindOnly). 신규 슬롯 전파·복구 정합용.
    private bool _cardInfoHiddenKeepArrow;

    // 그리드 외곽 테두리. 최초 1회 Addressable 로 생성하고 그리드 크기 변경 시 리사이즈만 한다.
    private GameObject _borderInstance;
    private SpriteRenderer _borderRenderer;
    private bool _isBorderLoading;
    private bool _isVisible = true;
    private CancellationTokenSource _borderCts;
    private GameObject _lineAttackVfxInstance;

    /// <summary>그리드에서 카드가 제거될 때 통지. 보존 선택 캐시 정리에 사용한다.</summary>
    public event System.Action<Card> OnCardRemoved;

    /// <summary>EraseInfo에 의한 카드 정보 은폐 상태가 실제로 변경될 때 통지한다.</summary>
    public event System.Action<bool> OnCardInfoHiddenChanged;

    /// <summary>
    /// 게임플레이 중 카드가 특정 좌표에서 이탈할 때 통지. 인자: (카드, 이탈 직전 좌표).
    ///
    /// OnCardRemoved 와 달리 전투 종료·씬 정리(ResetCards / ClearGridState 등)에서는 발행하지 않는다.
    /// 좌표가 의미를 갖는 실제 보드 변화만 싣는다. 분석 등이 구독한다.
    ///
    /// [주의] 카드 회수(BattleManager.RecallCard)도 내부에서 RemoveCard 를 거치므로
    /// 이 이벤트가 함께 발행된다. 회수와 소멸을 구분하려면 직후에 오는
    /// BattleManager.OnCardRecalled 를 함께 관찰해야 한다.
    /// </summary>
    public event System.Action<Card, Vector2Int> OnCardRemovedAt;

    // ── 초기화 ──────────────────────────────────────────────

    /// <summary>그리드 초기화. 자동/수동 슬롯 구성.</summary>
    public void Init()
    {
        BuildGrid();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[GridManager] Init ({_slots.Count} slots)");
#endif
    }

    /// <summary>그리드 크기 변경 후 풀에서 필요한 슬롯만 활성화 (자동 생성 모드 전용).</summary>
    public void SetGridSize(int rows, int columns)
    {
        ClearAllCards();
        _rows = Mathf.Max(1, rows);
        _columns = Mathf.Max(1, columns);
        BuildGrid();
    }

    /// <summary>슬롯 구성. _autoBuild 면 격자 자동 생성, 아니면 _presetSlots 사용.</summary>
    public void BuildGrid()
    {
        if (_autoBuild)
        {
            BuildAuto();
        }
        else
        {
            BuildFromPreset();
        }

        RefreshBorderAsync().Forget();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[GridManager] 그리드 활성화 완료 ({_slots.Count}/{_pooledSlots.Count} slots)");
#endif
    }

    /// <summary>rows×columns 격자를 월드 좌표로 자동 생성 또는 기존 풀에서 재활성화.</summary>
    private void BuildAuto()
    {
        if (_slotPrefab == null)
            return;

        float startX = -(_columns - 1) * _cellSize.x * 0.5f;
        float startY = -(_rows - 1) * _cellSize.y * 0.5f;

        Transform parent = _slotRoot != null ? _slotRoot : transform;
        int sortingIndex = 0;

        _slots.Clear();

        for (int row = 0; row < _rows; row++)
        {
            for (int col = 0; col < _columns; col++)
            {
                Vector2Int pos = new Vector2Int(col, row);

                GridSlot slot = GetOrCreatePooledSlot(pos, parent);

                slot.transform.localPosition = new Vector3(
                    startX + col * _cellSize.x,
                    startY + row * _cellSize.y,
                    0f);

                slot.transform.localRotation = Quaternion.identity;
                slot.transform.localScale = ResolveSlotScale();

                slot.Setup(pos, sortingIndex++, this);

                _slots[pos] = slot;
            }
        }

        foreach (KeyValuePair<Vector2Int, GridSlot> pair in _pooledSlots)
        {
            GridSlot slot = pair.Value;
            if (slot == null) continue;
            bool active = _slots.ContainsKey(pair.Key);
            if (slot.gameObject.activeSelf != active)
                slot.gameObject.SetActive(active);
        }
    }

    private GridSlot GetOrCreatePooledSlot(Vector2Int pos, Transform parent)
    {
        if (_pooledSlots.TryGetValue(pos, out GridSlot slot) && slot != null)
        {
            return slot;
        }

        slot = Instantiate(_slotPrefab, parent);
        _pooledSlots[pos] = slot;
        return slot;
    }

    private Vector3 ResolveSlotScale()
    {
        float referenceX = Mathf.Approximately(_slotScaleReferenceCellSize.x, 0f)
            ? 1f
            : _slotScaleReferenceCellSize.x;
        float referenceY = Mathf.Approximately(_slotScaleReferenceCellSize.y, 0f)
            ? 1f
            : _slotScaleReferenceCellSize.y;

        return new Vector3(
            _cellSize.x / referenceX,
            _cellSize.y / referenceY,
            1f);
    }

    /// <summary>
    /// 그리드 비주얼(슬롯 + 테두리) 표시/숨김. GridManager 자체(SceneSingleton)는 끄지 않는다.
    /// </summary>
    public void SetVisible(bool visible)
    {
        _isVisible = visible;

        if (_slotRoot != null && _slotRoot.gameObject.activeSelf != visible)
            _slotRoot.gameObject.SetActive(visible);
        if (_borderInstance != null && _borderInstance.activeSelf != visible)
            _borderInstance.SetActive(visible);
    }

    private async UniTaskVoid RefreshBorderAsync()
    {
        if (string.IsNullOrEmpty(_borderKey)) return;

        if (_borderInstance != null)
        {
            ResizeBorder();
            return;
        }

        if (_isBorderLoading || !ResourceManager.HasInstance) return;

        _isBorderLoading = true;
        _borderCts ??= new CancellationTokenSource();

        try
        {
            GameObject instance = await ResourceManager.Instance
                .InstantiateAsync<GameObject>(_borderKey, transform, _borderCts.Token);

            if (instance == null) return;

            _borderInstance = instance;
            _borderRenderer = instance.GetComponentInChildren<SpriteRenderer>();
            ResizeBorder();
            _borderInstance.SetActive(_isVisible);
        }
        catch (System.OperationCanceledException)
        {
            // 씬 종료 등으로 취소된 정상 흐름.
        }
        finally
        {
            _isBorderLoading = false;
        }
    }

    private void ResizeBorder()
    {
        if (_borderInstance == null) return;

        Vector2 borderSize = new Vector2(
            _columns * _cellSize.x + _borderPadding.x * 2f,
            _rows * _cellSize.y + _borderPadding.y * 2f);

        _borderInstance.transform.localPosition = new Vector3(_origin.x, _origin.y, _origin.z + _borderDepth);
        _borderInstance.transform.localRotation = Quaternion.identity;

        if (_borderRenderer != null && _borderRenderer.drawMode != SpriteDrawMode.Simple)
        {
            _borderInstance.transform.localScale = Vector3.one;
            _borderRenderer.size = borderSize;
        }
        else
        {
            ApplyBorderScaleFallback(borderSize);
        }
    }

    private void ApplyBorderScaleFallback(Vector2 targetSize)
    {
        if (_borderRenderer == null || _borderRenderer.sprite == null)
        {
            _borderInstance.transform.localScale = new Vector3(targetSize.x, targetSize.y, 1f);
            return;
        }

        Vector2 spriteSize = _borderRenderer.sprite.bounds.size;
        float scaleX = Mathf.Approximately(spriteSize.x, 0f) ? 1f : targetSize.x / spriteSize.x;
        float scaleY = Mathf.Approximately(spriteSize.y, 0f) ? 1f : targetSize.y / spriteSize.y;
        _borderInstance.transform.localScale = new Vector3(scaleX, scaleY, 1f);
    }

    /// <summary>씬에 수동 배치된 슬롯들로 구성. 각 슬롯의 Position 은 디자이너가 사전 설정한 값 사용.</summary>
    private void BuildFromPreset()
    {
        ClearGridState();
        for (int i = 0; i < _presetSlots.Count; i++)
        {
            GridSlot slot = _presetSlots[i];
            if (slot == null) continue;
            slot.Setup(slot.Position, i, this);
            _slots[slot.Position] = slot;
        }
    }

    /// <summary>그리드 좌표 → 월드 위치 (자동 생성용).</summary>
    public Vector3 GridToWorld(Vector2Int pos)
    {
        float startX = -(_columns - 1) * _cellSize.x * 0.5f;
        float startY = -(_rows - 1) * _cellSize.y * 0.5f;

        return transform.TransformPoint(new Vector3(
            startX + pos.x * _cellSize.x,
            startY + pos.y * _cellSize.y,
            0f));
    }

    // ── 배치 진입점 (입력 어댑터가 호출) ────────────────────

    /// <summary>
    /// 카드 배치 시도. 입력 어댑터(CardPlacementInput)가 드래그 드롭 시 호출하는 진입점.
    /// 슬롯에 데이터를 배치하면, 슬롯이 카드의 확정 방향 화살표까지 함께 표시한다.
    /// </summary>
    /// <param name="card">배치할 런타임 카드 데이터.</param>
    /// <param name="position">목표 그리드 좌표.</param>
    /// <returns>배치 성공 여부.</returns>
    public bool TryPlaceCard(Card card, Vector2Int position)
    {
        if (card == null) return false;

        GridSlot slot = GetSlot(position);
        if (slot == null || !slot.IsUsable || !slot.IsEmpty)
        {
            return false;
        }

        // 슬롯에 데이터 배치 (AssignCard 가 내부에서 확정 방향 화살표까지 표시).
        slot.AssignCard(card);
        _cardPositions[card] = position;

        // GridCardState 는 카드가 소유 → 새로 만들지 않음. 깨끗한 상태로 시작 보장.
        card.GridState.Reset();

        // 배치도 그리드 집계(행/열 ON 수 등)를 바꿔 다른 카드의 표시 수치에 영향을 준다.
        // 버전을 올려 이 값을 캐시 키로 쓰는 쪽(호버 프리뷰·카드 상세)이 갱신되게 한다.
        ActivationVersion++;

        // 배치 결과 그리드가 가득 차면 업적 신호.
        if (IsGridFull() && EventManager.HasInstance)
            EventManager.Instance.Publish(new GridFilledSignal());

        return true;
    }

    /// <summary>특정 좌표의 카드 회수.</summary>
    public bool RemoveCard(Vector2Int position)
    {
        GridSlot slot = GetSlot(position);
        if (slot == null || slot.IsEmpty) return false;

        Card removed = slot.OccupiedCard;
        if (removed != null)
        {
            _cardPositions.Remove(removed);
            // 카드가 GridState 를 소유 → 떠난 상태만 초기화 (Dictionary 제거 불필요).
            // 스킬로 건 화살표 오버레이도 함께 소멸 (그리드 떠남).
            removed.OnLeaveGrid();
            OnCardRemoved?.Invoke(removed);
            OnCardRemovedAt?.Invoke(removed, position);
        }

        _preserveSlots.Remove(slot); // 빈 칸이 된 슬롯에 보존 테두리가 되살아나지 않도록 레이어에서 제거.
        slot.ClearCard();
        ActivationVersion++;
        return true;
    }

    /// <summary>
    /// 턴 종료 시 필드 전용 임시 카드만 제거한다.
    /// 일반 카드는 턴 사이에 그리드에 남기고, 임시 카드는 덱/묘지에 편입하지 않는다.
    /// </summary>
    public void CollectTemporaryFieldOnlyCards(List<Card> removed)
    {
        _recoverScratch.Clear();
        foreach (Card card in _cardPositions.Keys)
        {
            _recoverScratch.Add(card);
        }

        for (int i = 0; i < _recoverScratch.Count; i++)
        {
            Card card = _recoverScratch[i];
            if (card == null) continue;

            if (!card.IsTemporaryFieldOnly)
            {
                card.GridState.ResetTurnOnCount();
                continue;
            }

            bool hadPosition = _cardPositions.TryGetValue(card, out Vector2Int pos);
            if (hadPosition)
            {
                GridSlot slot = GetSlot(pos);
                if (slot != null && slot.OccupiedCard == card)
                {
                    _preserveSlots.Remove(slot);
                    slot.ClearCard();
                }
                _cardPositions.Remove(card);
            }

            card.OnLeaveGrid();
            OnCardRemoved?.Invoke(card);

            // 턴 종료 임시카드 소멸도 실제 보드 변화다. 좌표를 알 때만 통지한다.
            if (hadPosition)
                OnCardRemovedAt?.Invoke(card, pos);

            removed?.Add(card);
        }

        _turnToggleCount = 0;
    }

    // 턴 종료 그리드 정리 순회용 스크래치 (재사용 → GC 없음).
    private readonly List<Card> _recoverScratch = new List<Card>(16);

    // ── 리셋 ───────────────────────────────────────────────

    /// <summary>모든 슬롯의 카드 비우기 (전투 종료·재시작).</summary>
    public void ResetCards()
    {
        // 그리드 위 카드들의 상태 초기화 (카드 소유 상태).
        foreach (Card card in _cardPositions.Keys)
        {
            card.OnLeaveGrid();
            OnCardRemoved?.Invoke(card);
        }

        _preserveSlots.Clear();
        foreach (GridSlot slot in _slots.Values)
        {
            if (!slot.IsEmpty)
            {
                slot.ClearCard();
            }
        }

        _cardPositions.Clear();
        _turnToggleCount = 0;
    }

    /// <summary>이번 턴 ON 횟수 누적 (활성화 로직이 호출).</summary>
    public void IncrementTurnToggleCount() => _turnToggleCount++;

    /// <summary>턴 종료 시 그리드 ON 카운트 초기화.</summary>
    public void ResetTurnToggleCount() => _turnToggleCount = 0;

    /// <summary>그리드 카드의 정보 비주얼만 숨기거나 복구한다.</summary>
    /// <param name="hidden">true 면 카드 정보 은폐.</param>
    /// <param name="keepArrow">true 면 카드 종류(이름/스탯)만 숨기고 방향 화살표는 유지(EraseCardKindOnly).</param>
    public void SetCardInfoHidden(bool hidden, bool keepArrow = false)
    {
        // 복구(false) 시엔 keepArrow 는 의미가 없으므로 정규화한다.
        if (!hidden) keepArrow = false;

        if (IsCardInfoHidden == hidden && _cardInfoHiddenKeepArrow == keepArrow) return;

        IsCardInfoHidden = hidden;
        _cardInfoHiddenKeepArrow = keepArrow;
        foreach (GridSlot slot in _slots.Values)
        {
            slot?.SetCardInfoHidden(hidden, keepArrow);
        }

        OnCardInfoHiddenChanged?.Invoke(hidden);
    }

    // ── 활성(ON/OFF) 비주얼 반영 ────────────────────────────

    /// <summary>
    /// 카드의 활성(ON/OFF) 상태를 그 카드가 놓인 슬롯 비주얼에 반영한다.
    /// 활성 "데이터"(GridCardState)는 카드가 소유하고, 이 메서드는 그 변화를
    /// 슬롯의 강조 비주얼로 비춘다 (데이터/비주얼 경계). ChainExecutor 가 토글 직후 호출.
    /// </summary>
    public void SetCardActivated(Card card, bool activated)
    {
        if (card == null) return;
        if (!_cardPositions.TryGetValue(card, out Vector2Int pos)) return;

        GridSlot slot = GetSlot(pos);
        if (slot != null && slot.OccupiedCard == card)
        {
            ApplyCardActivatedVisual(slot, card, activated);
            ActivationVersion++;
            slot.PlayToggleFeedback();
        }
    }

    public async UniTask SetCardActivatedAsync(Card card, bool activated, CancellationToken token)
    {
        if (card == null) return;
        if (!_cardPositions.TryGetValue(card, out Vector2Int pos)) return;

        GridSlot slot = GetSlot(pos);
        if (slot != null && slot.OccupiedCard == card)
        {
            ApplyCardActivatedVisual(slot, card, activated);
            ActivationVersion++;
            await slot.PlayToggleFeedbackAsync(token);
        }
    }

    public void SetCardActivatedImmediate(Card card, bool activated)
    {
        if (card == null) return;
        if (!_cardPositions.TryGetValue(card, out Vector2Int pos)) return;

        GridSlot slot = GetSlot(pos);
        if (slot != null && slot.OccupiedCard == card)
        {
            ApplyCardActivatedVisual(slot, card, activated);
            ActivationVersion++;
        }
    }

    public async UniTask PlayCardToggleFeedbackAsync(Card card, CancellationToken token)
    {
        await PlayCardToggleFeedbackAsync(card, ECardDirection.None, 1f, 0f, token);
    }

    /// <summary>토글 피드백을 fromDirection(신호 진행 방향) 기준 도미노 모션으로 재생.</summary>
    public async UniTask PlayCardToggleFeedbackAsync(Card card, ECardDirection fromDirection, CancellationToken token)
    {
        await PlayCardToggleFeedbackAsync(card, fromDirection, 1f, 0f, token);
    }

    /// <summary>토글 피드백을 체인별 배속으로 재생한다.</summary>
    public async UniTask PlayCardToggleFeedbackAsync(
        Card card,
        ECardDirection fromDirection,
        float visualSpeedMultiplier,
        float minimumDuration,
        CancellationToken token)
    {
        if (card == null) return;
        if (!_cardPositions.TryGetValue(card, out Vector2Int pos)) return;

        GridSlot slot = GetSlot(pos);
        if (slot != null && slot.OccupiedCard == card)
        {
            await slot.PlayToggleFeedbackAsync(fromDirection, visualSpeedMultiplier, minimumDuration, token);
        }
    }

    private static void ApplyCardActivatedVisual(GridSlot slot, Card card, bool activated)
    {
        slot.SetActivated(activated);
        slot.ShowArrows(
            card.Arrow != null ? card.Arrow.Current : ECardDirection.None,
            card.Arrow != null ? card.Arrow.HighlightDirs : ECardDirection.None);
    }

    public void RefreshCardStats(Card card)
    {
        if (card == null || !_cardPositions.TryGetValue(card, out Vector2Int pos)) return;

        GridSlot slot = GetSlot(pos);
        if (slot != null && slot.OccupiedCard == card)
            slot.RefreshStats();
    }

    // ── 조회 ───────────────────────────────────────────────

    public GridSlot GetSlot(Vector2Int position)
    {
        _slots.TryGetValue(position, out GridSlot slot);
        return slot;
    }

    /// <summary>좌표에 점유 카드가 있으면 true 와 카드를 반환한다.</summary>
    public bool TryGetOccupiedCard(Vector2Int position, out Card card)
    {
        card = null;
        GridSlot slot = GetSlot(position);
        if (slot == null || slot.IsEmpty) return false;

        card = slot.OccupiedCard;
        return card != null;
    }

    /// <summary>특정 슬롯 기준, 방향(4방위, Flags 단일 비트 권장) 이웃 슬롯.</summary>
    public GridSlot GetNeighbor(GridSlot origin, ECardDirection direction)
    {
        if (origin == null || direction == ECardDirection.None) return null;
        return GetSlot(origin.Position + DirectionToDelta(direction));
    }

    public List<GridSlot> GetEmptySlots()
    {
        List<GridSlot> result = new List<GridSlot>();
        foreach (GridSlot slot in _slots.Values)
        {
            if (slot.IsUsable && slot.IsEmpty) result.Add(slot);
        }
        return result;
    }

    /// <summary>사용 가능한 빈 슬롯이 하나도 없으면 true(그리드 가득 참). 카드 0장이면 false. (무할당)</summary>
    public bool IsGridFull()
    {
        bool anyPlaced = false;
        foreach (GridSlot slot in _slots.Values)
        {
            if (!slot.IsUsable) continue;
            if (slot.IsEmpty) return false;
            anyPlaced = true;
        }
        return anyPlaced;
    }

    /// <summary>그리드에 배치된 카드가 1장 이상이고 전부 활성(ON) 상태면 true. (무할당)</summary>
    public bool AreAllPlacedCardsActive()
    {
        if (_cardPositions.Count == 0) return false;
        foreach (Card card in _cardPositions.Keys)
        {
            if (card == null) continue;
            if (!card.GridState.IsActivated) return false;
        }
        return true;
    }

    /// <summary>
    /// 배치된 카드의 월드 위치를 반환 (투사체 연출 출발점용).
    /// 카드 → 좌표(_cardPositions) → 슬롯 → 슬롯 월드 위치 순으로 조회.
    /// </summary>
    /// <returns>그리드에 배치돼 있으면 true + 월드 위치, 아니면 false.</returns>
    public bool TryGetCardWorldPosition(Card card, out Vector3 worldPos)
    {
        worldPos = Vector3.zero;
        if (card == null) return false;

        if (!_cardPositions.TryGetValue(card, out Vector2Int coord))
        {
            return false;
        }

        GridSlot slot = GetSlot(coord);
        if (slot == null) return false;

        worldPos = slot.transform.position;
        return true;
    }

    // ── 하이라이트 (배치 입력 피드백) ───────────────────────

    /// <summary>특정 좌표 슬롯만 하이라이트 (드래그 중 마우스가 올라온 칸). 나머지는 끈다.</summary>
    public void HighlightSlot(Vector2Int position)
    {
        foreach (GridSlot slot in _slots.Values)
        {
            slot.SetHighlight(slot.Position == position);
        }
    }

    /// <summary>모든 슬롯 하이라이트 끄기 (드래그 종료·취소 시).</summary>
    public void ClearHighlights()
    {
        foreach (GridSlot slot in _slots.Values)
        {
            slot.SetHighlight(false);
        }
    }

    /// <summary>
    /// 특정 카드가 놓인 슬롯의 보존 하이라이트를 켜거나 끈다. isEffectPreserved=true 면 연한 파랑.
    /// 보존도 프리뷰 레이어의 하나로 등록해 다른 레이어(호버·적 의도) 갱신에 지워지지 않게 한다.
    /// </summary>
    public void SetCardPreserveHighlight(Card card, bool on, bool isEffectPreserved = false)
    {
        if (card == null || !_cardPositions.TryGetValue(card, out Vector2Int pos)) return;

        GridSlot slot = GetSlot(pos);
        if (slot == null || slot.OccupiedCard != card) return;

        if (on)
            _preserveSlots[slot] = isEffectPreserved;
        else
            _preserveSlots.Remove(slot);

        ApplyPreviewVisual(slot);
    }

    /// <summary>모든 슬롯의 보존 하이라이트를 해제한다. 다른 프리뷰 레이어는 그대로 복원된다.</summary>
    public void ClearPreserveHighlights()
    {
        if (_preserveSlots.Count == 0) return;

        _previewClearScratch.Clear();
        foreach (GridSlot slot in _preserveSlots.Keys)
        {
            _previewClearScratch.Add(slot);
        }

        _preserveSlots.Clear();

        for (int i = 0; i < _previewClearScratch.Count; i++)
        {
            ApplyPreviewVisual(_previewClearScratch[i]);
        }
        _previewClearScratch.Clear();
    }

    /// <summary>
    /// 그리드 전체 카드 제거 (다음 층 전환·전투 재시작 시).
    /// 보존 여부와 무관하게 모든 슬롯을 비우고 역인덱스·ON 카운트를 초기화한다.
    /// 회수된 카드를 묘지로 보내지 않으므로(전투가 끝나 덱이 재구성됨) 호출자가 흐름을 책임진다.
    /// </summary>
    public void ClearAllCards()
    {
        EndLineAttackVfx();
        RestoreBrokenSlotsImmediate();
        ClearHighlights();
        ClearPlacementPreview();
        ClearHoverPreview();
        ClearEnemyLineIntentWarning();
        ClearPreserveHighlights();
        ClearTutorialTargetMarker();
        SetCardInfoHidden(false);
        foreach (GridSlot slot in _slots.Values)
        {
            if (slot != null && !slot.IsEmpty)
            {
                Card card = slot.OccupiedCard;
                card?.OnLeaveGrid();
                if (card != null)
                    OnCardRemoved?.Invoke(card);
                slot.ClearCard();
            }
        }
        _cardPositions.Clear();
        _turnToggleCount = 0;
    }

    // ── 배치 프리뷰 (신호 경로 미리보기) ────────────────────

    // 프리뷰 레이어를 분리해 드래그/스킬 clear 가 호버 프리뷰를 지우지 않도록 한다.
    private readonly Dictionary<GridSlot, Color> _previewSlots = new Dictionary<GridSlot, Color>(8);
    private readonly Dictionary<GridSlot, Color> _enemyIntentPreviewSlots = new Dictionary<GridSlot, Color>(8);
    private readonly Dictionary<GridSlot, Color> _hoverPreviewSlots = new Dictionary<GridSlot, Color>(8);
    private readonly Dictionary<GridSlot, Color> _hoverPreviewBuildSlots = new Dictionary<GridSlot, Color>(8);
    // 보존 하이라이트 레이어. value = 효과(ReserveEffect)로 이미 보존된 카드인가 (색 구분은 GridSlot 이 소유).
    // 보존 페이즈 동안 최우선으로 적용되어 호버·적 의도 레이어 갱신에 덮이지 않는다.
    private readonly Dictionary<GridSlot, bool> _preserveSlots = new Dictionary<GridSlot, bool>(8);
    private readonly List<GridSlot> _previewClearScratch = new List<GridSlot>(16);
    private readonly List<GridSlot> _linePreviewScratch = new List<GridSlot>(8);
    private bool _deferPreviewVisualApply;

    // 스킬 타겟 프리뷰 활성 여부. true 동안에는 호버 경로 프리뷰(ShowPlacedCardPreview)를 양보한다.
    private bool _skillPreviewActive;

    /// <summary>스킬 시전 프리뷰 활성 여부 (호버 경로 프리뷰 충돌 방지용). SkillCastInput 이 제어.</summary>
    public bool IsSkillPreviewActive => _skillPreviewActive;

    /// <summary>스킬 타겟 프리뷰 활성 상태 설정. 활성 동안 호버 경로 프리뷰는 표시되지 않는다.</summary>
    public void SetSkillPreviewActive(bool active)
    {
        _skillPreviewActive = active;
        if (active)
        {
            ClearHoverPreview();
        }
    }

    /// <summary>
    /// 카드를 target 위치에 놓는다고 가정했을 때, 화살표 신호가 닿는 경로 슬롯을 하이라이트한다.
    /// 카드의 방향·사거리·전파 타입(Pierce/Block/StopOnGap)을 그대로 적용해 실제 전파와 일치시킨다.
    /// 드래그 호버 중 호출 — 어느 칸이 영향을 받는지 플레이어에게 미리 보여주는 용도.
    /// </summary>
    /// <param name="card">배치하려는 카드 (방향/사거리/전파 타입 출처).</param>
    /// <param name="target">배치 예정 좌표.</param>
    /// <summary>
    /// 스킬 타겟 표시 — 대상 칸만 초록/빨강으로 칠한다.
    /// </summary>
    public void ShowSkillTargetPreview(Vector2Int target, bool canApply)
    {
        ClearPlacementPreview();

        GridSlot targetSlot = GetSlot(target);
        if (targetSlot == null) return;

        // 스킬 적용 가능한 호버 대상은 기존 테두리 셰이더를 밝고 연한 분홍색으로 강조한다.
        // (경로=노랑 / 배치 가능=초록 / 불가=빨강 / 보존=시안 과 모두 구분)
        Color skillColor = new Color(1f, 0.7f, 0.85f, 1f); // 밝은 연분홍
        SetPreviewSlot(_previewSlots, targetSlot, canApply ? skillColor : Color.red);
    }

    public void ShowPlacementPreview(Card card, Vector2Int target, bool canPlace)
    {
        ClearPlacementPreview();
        if (card == null) return;

        GridSlot targetSlot = GetSlot(target);
        if (targetSlot == null) return;

        // 배치 대상은 가능 여부를 초록/빨강으로 표시한다.
        SetPreviewSlot(_previewSlots, targetSlot, canPlace ? Color.green : Color.red);
        if (!canPlace) return;

        PreviewCardPropagation(card, target, _previewSlots);
    }

    /// <summary>
    /// 그리드에 이미 배치된 카드에 마우스를 올렸을 때, 그 카드가 영향을 주는 칸(경로/패턴/오라)을
    /// 노란 테두리 셰이더로 강조한다. 스킬 시전 프리뷰가 활성일 때는 충돌 방지를 위해 무시한다.
    /// </summary>
    public void ShowPlacedCardPreview(Card card)
    {
        if (_skillPreviewActive) return; // 스킬 타겟 프리뷰가 우선 — 호버 경로 프리뷰 양보.

        _hoverPreviewBuildSlots.Clear();
        if (card == null) return;
        if (!TryGetPosition(card, out Vector2Int origin)) return;

        // 카드 자신 칸은 강조하지 않고, 영향을 주는 칸(경로/패턴/오라)만 표시한다.
        _deferPreviewVisualApply = true;
        PreviewCardPropagation(card, origin, _hoverPreviewBuildSlots);
        _deferPreviewVisualApply = false;

        ReplaceHoverPreviewLayer();
    }

    /// <summary>
    /// 카드의 방향·사거리·전파 타입(또는 오라/패턴)대로 영향 칸을 노란 테두리로 강조한다.
    /// 배치 프리뷰(ShowPlacementPreview)와 호버 프리뷰(ShowPlacedCardPreview) 공용 로직.
    /// </summary>
    private void PreviewCardPropagation(Card card, Vector2Int target, Dictionary<GridSlot, Color> previewLayer)
    {
        if (card == null) return;

        if (TryPreviewTotemAura(card, target, previewLayer))
        {
            return;
        }

        ECardDirection dirs = (card.Arrow != null) ? card.Arrow.PreviewDirs : ECardDirection.None;
        if (dirs == ECardDirection.None && card.PatternType == EChainPatternType.Line) return;

        if (card.PatternType != EChainPatternType.Line)
        {
            PreviewPattern(card, target, previewLayer);
            return;
        }

        int range = Mathf.Max(1, card.Range);
        EChainPropagation rule = card.Propagation;

        PreviewDirection(target, dirs, ECardDirection.Up, range, rule, previewLayer);
        PreviewDirection(target, dirs, ECardDirection.Down, range, rule, previewLayer);
        PreviewDirection(target, dirs, ECardDirection.Left, range, rule, previewLayer);
        PreviewDirection(target, dirs, ECardDirection.Right, range, rule, previewLayer);
        PreviewDirection(target, dirs, ECardDirection.UpLeft, range, rule, previewLayer);
        PreviewDirection(target, dirs, ECardDirection.UpRight, range, rule, previewLayer);
        PreviewDirection(target, dirs, ECardDirection.DownLeft, range, rule, previewLayer);
        PreviewDirection(target, dirs, ECardDirection.DownRight, range, rule, previewLayer);
    }

    private bool TryPreviewTotemAura(Card card, Vector2Int origin, Dictionary<GridSlot, Color> previewLayer)
    {
        if (card == null || card.Data == null || card.Data.CardType != ECardType.Totem)
        {
            return false;
        }

        bool foundAura = false;
        IReadOnlyList<CardEffectBase> effects = card.Effects;
        for (int i = 0; i < effects.Count; i++)
        {
            if (effects[i] is not TotemAuraEffect aura) continue;

            foundAura = true;
            PreviewAuraScope(origin, aura.BuffScope, previewLayer);
        }
        return foundAura;
    }

    private void PreviewAuraScope(Vector2Int origin, ECardAuraScope scope, Dictionary<GridSlot, Color> previewLayer)
    {
        foreach (KeyValuePair<Vector2Int, GridSlot> pair in _slots)
        {
            Vector2Int pos = pair.Key;
            if (pos == origin || !IsInAuraScope(origin, pos, scope)) continue;

            GridSlot slot = pair.Value;
            if (slot == null || previewLayer.ContainsKey(slot)) continue;
            SetPreviewSlot(previewLayer, slot, Color.yellow);
        }
    }

    private static bool IsInAuraScope(
        Vector2Int origin,
        Vector2Int position,
        ECardAuraScope scope)
    {
        int dx = Mathf.Abs(position.x - origin.x);
        int dy = Mathf.Abs(position.y - origin.y);
        return scope switch
        {
            ECardAuraScope.Adjacent4 => dx + dy == 1,
            ECardAuraScope.Adjacent8 => Mathf.Max(dx, dy) == 1,
            ECardAuraScope.Row => position.y == origin.y,
            ECardAuraScope.Column => position.x == origin.x,
            ECardAuraScope.Cross => position.x == origin.x || position.y == origin.y,
            ECardAuraScope.All => true,
            _ => false,
        };
    }

    /// <summary>프리뷰 하이라이트 끄기 (드래그 종료·취소·배치 확정 시).</summary>
    public void ClearPlacementPreview()
    {
        ClearPreviewLayer(_previewSlots);
    }

    // ── 튜토리얼 지정 슬롯 마커 ──────────────────────────────
    // "여기에 놓으세요" 상시 표시. 드래그 프리뷰 레이어와 분리되어
    // 호버 갱신·드래그 종료(ClearPlacementPreview)에 지워지지 않는다.

    private GridSlot _tutorialMarkerSlot;
    private Color _tutorialMarkerColor;

    /// <summary>튜토리얼 지정 슬롯 마커 표시. 기존 마커는 교체된다.</summary>
    public void SetTutorialTargetMarker(Vector2Int position, Color color)
    {
        GridSlot previous = _tutorialMarkerSlot;
        _tutorialMarkerSlot = GetSlot(position);
        _tutorialMarkerColor = color;

        if (previous != null && previous != _tutorialMarkerSlot)
            ApplyPreviewVisual(previous);
        ApplyPreviewVisual(_tutorialMarkerSlot);
    }

    /// <summary>튜토리얼 지정 슬롯 마커 해제.</summary>
    public void ClearTutorialTargetMarker()
    {
        if (_tutorialMarkerSlot == null) return;

        GridSlot cleared = _tutorialMarkerSlot;
        _tutorialMarkerSlot = null;
        ApplyPreviewVisual(cleared);
    }

    /// <summary>선형 공격 경고를 실제 진행 순서와 동일한 행 또는 열에 표시한다.</summary>
    public void ShowEnemyLineIntentWarning(GridLineAttackPlan plan)
    {
        ClearEnemyLineIntentWarning();
        AddEnemyLineIntentWarning(plan);
    }

    /// <summary>여러 라인의 선형 공격 경고를 한 번에 표시한다(자를 라인 수 &gt; 1).</summary>
    public void ShowEnemyLineIntentWarning(IReadOnlyList<GridLineAttackPlan> plans)
    {
        ClearEnemyLineIntentWarning();
        if (plans == null) return;

        for (int i = 0; i < plans.Count; i++)
        {
            AddEnemyLineIntentWarning(plans[i]);
        }
    }

    // 라인 하나의 붉은 테두리 + 경고 스프라이트를 기존 표시에 누적한다.
    private void AddEnemyLineIntentWarning(GridLineAttackPlan plan)
    {
        GetLineSlots(plan, _linePreviewScratch);

        for (int i = 0; i < _linePreviewScratch.Count; i++)
        {
            GridSlot slot = _linePreviewScratch[i];
            if (slot != null)
            {
                SetPreviewSlot(_enemyIntentPreviewSlots, slot, Color.red);
            }
        }

        _enemyLineIntentWarningPresenter?.Show(_linePreviewScratch);
    }

    /// <summary>선형 공격 경고 스프라이트와 붉은 테두리를 정리한다.</summary>
    public void ClearEnemyLineIntentWarning()
    {
        _enemyLineIntentWarningPresenter?.Clear();
        ClearPreviewLayer(_enemyIntentPreviewSlots);
    }

    /// <summary>선형 공격 경로를 실제 진행 순서대로 result에 채운다.</summary>
    public void GetLineSlots(GridLineAttackPlan plan, List<GridSlot> result)
    {
        if (result == null) return;
        result.Clear();

        int count = plan.Axis == EGridLineAxis.Row ? _columns : _rows;
        int lineCount = plan.Axis == EGridLineAxis.Row ? _rows : _columns;
        if (plan.Index < 0 || plan.Index >= lineCount) return;

        for (int i = 0; i < count; i++)
        {
            int step = plan.Forward ? i : count - 1 - i;
            Vector2Int position = plan.Axis == EGridLineAxis.Row
                ? new Vector2Int(step, plan.Index)
                : new Vector2Int(plan.Index, step);
            GridSlot slot = GetSlot(position);
            if (slot != null) result.Add(slot);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Assert(result.Count == count,
            $"[GridManager] 선형 공격 경로 누락 — axis={plan.Axis}, index={plan.Index}, count={result.Count}/{count}");
#endif
    }

    /// <summary>파괴된 모든 슬롯을 동시에 팝 복구한다.</summary>
    public async UniTask RestoreBrokenSlotsAsync(CancellationToken token)
    {
        var tasks = new List<UniTask>(_slots.Count);
        foreach (GridSlot slot in _slots.Values)
        {
            if (slot != null && slot.IsBroken)
            {
                tasks.Add(slot.RestoreBrokenAsync(token));
            }
        }

        if (tasks.Count > 0)
        {
            await UniTask.WhenAll(tasks);
        }
    }

    /// <summary>파괴된 모든 슬롯을 트윈 없이 즉시 복구한다.</summary>
    public void RestoreBrokenSlotsImmediate()
    {
        foreach (GridSlot slot in _slots.Values)
        {
            if (slot == null) continue;
            slot.RestoreBrokenImmediate();
        }
    }

    /// <summary>선형 공격 VFX를 실제 도달 구간에 맞춰 한 번 재생한다.</summary>
    public LineAttackSlashVfx BeginLineAttackVfx(IReadOnlyList<GridSlot> slots, GridLineAttackPlan plan)
    {
        EndLineAttackVfx();
        if (_lineAttackVfxPrefab == null || slots == null || slots.Count == 0 || slots[0] == null) return null;

        _lineAttackVfxInstance = Instantiate(_lineAttackVfxPrefab, transform);
        LineAttackSlashVfx slash = _lineAttackVfxInstance.GetComponent<LineAttackSlashVfx>();
        if (slash != null && slash.Play(slots, plan)) return slash;

        EndLineAttackVfx();
        return null;
    }

    /// <summary>선형 공격 종료 또는 취소 시 선택적 VFX를 정리한다.</summary>
    public void EndLineAttackVfx()
    {
        if (_lineAttackVfxInstance == null) return;
        Destroy(_lineAttackVfxInstance);
        _lineAttackVfxInstance = null;
    }

    /// <summary>호버 영향 경로 프리뷰만 끈다. 배치/스킬 프리뷰는 유지한다.</summary>
    public void ClearHoverPreview()
    {
        ClearPreviewLayer(_hoverPreviewSlots);
    }

    private void SetPreviewSlot(Dictionary<GridSlot, Color> layer, GridSlot slot, Color color)
    {
        if (layer == null || slot == null) return;
        layer[slot] = color;
        if (_deferPreviewVisualApply) return;

        ApplyPreviewVisual(slot);
    }

    private void ReplaceHoverPreviewLayer()
    {
        _previewClearScratch.Clear();
        foreach (GridSlot slot in _hoverPreviewSlots.Keys)
        {
            if (slot != null && !_previewClearScratch.Contains(slot))
            {
                _previewClearScratch.Add(slot);
            }
        }
        foreach (GridSlot slot in _hoverPreviewBuildSlots.Keys)
        {
            if (slot != null && !_previewClearScratch.Contains(slot))
            {
                _previewClearScratch.Add(slot);
            }
        }

        _hoverPreviewSlots.Clear();
        foreach (KeyValuePair<GridSlot, Color> pair in _hoverPreviewBuildSlots)
        {
            _hoverPreviewSlots[pair.Key] = pair.Value;
        }
        _hoverPreviewBuildSlots.Clear();

        for (int i = 0; i < _previewClearScratch.Count; i++)
        {
            ApplyPreviewVisual(_previewClearScratch[i]);
        }
        _previewClearScratch.Clear();
    }

    private void ClearPreviewLayer(Dictionary<GridSlot, Color> layer)
    {
        if (layer == null || layer.Count == 0) return;

        _previewClearScratch.Clear();
        foreach (GridSlot slot in layer.Keys)
        {
            _previewClearScratch.Add(slot);
        }

        layer.Clear();

        for (int i = 0; i < _previewClearScratch.Count; i++)
        {
            ApplyPreviewVisual(_previewClearScratch[i]);
        }
        _previewClearScratch.Clear();
    }

    private void ApplyPreviewVisual(GridSlot slot)
    {
        if (slot == null) return;

        // 보존 하이라이트가 최우선 — 보존 페이즈의 선택 표시는 어떤 프리뷰보다 위에 유지된다.
        if (_preserveSlots.TryGetValue(slot, out bool isEffectPreserved))
        {
            slot.SetPreserveHighlight(true, isEffectPreserved);
            return;
        }

        // 튜토리얼 지정 슬롯 마커 — 드래그 프리뷰가 갱신·해제되어도 지워지지 않는 상시 표시.
        if (_tutorialMarkerSlot == slot)
        {
            slot.SetPreview(true, _tutorialMarkerColor);
            return;
        }

        if (_previewSlots.TryGetValue(slot, out Color previewColor))
        {
            slot.SetPreview(true, previewColor);
            return;
        }

        if (_enemyIntentPreviewSlots.TryGetValue(slot, out Color enemyIntentColor))
        {
            slot.SetPreview(true, enemyIntentColor);
            return;
        }

        if (_hoverPreviewSlots.TryGetValue(slot, out Color hoverColor))
        {
            slot.SetPreview(true, hoverColor);
            return;
        }

        slot.SetPreview(false);
    }

    /// <summary>
    /// 단일 방향 프리뷰. ChainExecutor.CollectInDirection 과 동일한 전파 규칙으로
    /// 경로 슬롯을 하이라이트한다 (프리뷰=실제 일치 보장).
    /// </summary>
    private void PreviewDirection(
        Vector2Int origin,
        ECardDirection dirs,
        ECardDirection single,
        int range,
        EChainPropagation rule,
        Dictionary<GridSlot, Color> previewLayer)
    {
        if ((dirs & single) == 0) return;

        Vector2Int delta = DirectionToDelta(single);
        Vector2Int cursor = origin;

        for (int i = 0; i < range; i++)
        {
            cursor += delta;
            GridSlot slot = GetSlot(cursor);
            if (slot == null) break;

            bool occupied = !slot.IsEmpty;

            SetPreviewSlot(previewLayer, slot, Color.yellow);

            if (occupied)
            {
                if (rule == EChainPropagation.BlockOnFirstCard) break;
            }
            else
            {
                if (rule == EChainPropagation.StopOnGap) break;
            }
        }
    }

    /// <summary>카드의 활성화 상태 조회. 카드가 소유하므로 항상 그 카드의 상태 반환.</summary>
    public GridCardState GetCardState(Card card)
    {
        return card != null ? card.GridState : null;
    }

    /// <summary>카드의 현재 그리드 좌표 조회. 미배치면 false.</summary>
    public bool TryGetPosition(Card card, out Vector2Int position)
    {
        return _cardPositions.TryGetValue(card, out position);
    }

    /// <summary>현재 그리드에 배치된 모든 카드 (읽기 전용). 무한루프 시뮬레이션·일괄 처리용.</summary>
    public IReadOnlyCollection<Card> PlacedCards => _cardPositions.Keys;

    /// <summary>
    /// 기준 카드 주변의 오라 대상 카드를 result 에 채운다.
    /// 호출부가 넘긴 리스트는 Clear 하지 않으므로, 보통 호출 직전에 Clear 한다.
    /// </summary>
    public void GetCardsInAura(Card source, ECardAuraScope scope, List<Card> result)
    {
        if (source == null || result == null || scope == ECardAuraScope.None)
        {
            return;
        }

        if (!_cardPositions.TryGetValue(source, out Vector2Int origin))
        {
            return;
        }

        foreach (KeyValuePair<Card, Vector2Int> kv in _cardPositions)
        {
            Card card = kv.Key;
            if (card == null || card == source) continue;

            if (IsInAuraScope(origin, kv.Value, scope))
            {
                result.Add(card);
            }
        }
    }

    public void RefreshCardArrows(Card card)
    {
        if (card == null || !_cardPositions.TryGetValue(card, out Vector2Int pos)) return;
        GridSlot slot = GetSlot(pos);
        if (slot != null && slot.OccupiedCard == card)
        {
            slot.ShowArrows(
                card.Arrow != null ? card.Arrow.Current : ECardDirection.None,
                card.Arrow != null ? card.Arrow.HighlightDirs : ECardDirection.None);
        }
    }

    // ── 스킬 즉시 상태변경 API (체인·효과 없음) ───────────
    // 스킬이 그리드의 기존 카드 상태만 바꾼다.
    // 체인 전파/효과 발동을 일으키지 않으며, ON 카운트 집계에도 넣지 않는다
    // (체인으로 켜진 것이 아니므로 Self/Grid 콤보 수치와 분리).

    /// <summary>스킬: 대상 카드의 활성 상태를 토글한다 (비주얼만, 체인 없음).</summary>
    public void ToggleCard(Card card)
    {
        if (card == null || !_cardPositions.TryGetValue(card, out Vector2Int pos)) return;
        GridCardState state = GetCardState(card);
        if (state == null) return;

        bool next = !state.IsActivated;
        state.SetActivated(next);
        SetCardActivatedImmediate(card, next);
    }

    /// <summary>스킬: 대상 카드 화살표에 방향 비트를 추가하고 갱신한다.</summary>
    public void AddArrow(Card card, ECardDirection directions)
    {
        if (card == null || card.Arrow == null) return;
        card.Arrow.AddDirectionsOverlay(directions);
        RefreshCardArrows(card);

        // 업적 신호: 화살표가 붙은 카드 ID + 실명 여부, 그리고 샷건(파편화살) 패턴이면 방향 집합.
        if (EventManager.HasInstance)
        {
            var events = EventManager.Instance;
            events.Publish(new ArrowAddedSignal(card.CardId, IsCardInfoHidden));
            if (card.PatternType == EChainPatternType.ShotgunForward2)
                events.Publish(new ShardArrowSignal(card.Arrow.Current));
        }
    }

    /// <summary>스킬: 대상 카드 화살표를 90° 회전한다. steps: 시계 1, 반시계 3.</summary>
    public void RotateArrow(Card card, int steps)
    {
        if (card == null || card.Arrow == null) return;
        card.Arrow.RotateOverlay(steps);
        RefreshCardArrows(card);
    }

    /// <summary>패시브 화살표 오버레이를 원본으로 되돌리고 갱신한다 (턴/전투 종료 정리).</summary>
    public void RevertArrowOverlay(Card card)
    {
        if (card == null || card.Arrow == null || !card.Arrow.HasOverlay) return;
        card.Arrow.RevertOverlay();
        RefreshCardArrows(card);
    }

    private void PreviewPattern(Card card, Vector2Int origin, Dictionary<GridSlot, Color> previewLayer)
    {
        ECardDirection dirs = (card.Arrow != null) ? card.Arrow.PreviewDirs : ECardDirection.None;
        foreach (ECardDirection dir in EnumerateDirections(dirs))
        {
            Vector2Int delta = DirectionToDelta(dir);
            switch (card.PatternType)
            {
                case EChainPatternType.ForwardSkip1:
                    PreviewCell(origin + (delta * 2), previewLayer);
                    break;

                case EChainPatternType.FrontCone2:
                    {
                        Vector2Int impact = origin + (delta * 2);
                        PreviewCell(impact, previewLayer);
                        PreviewCell(impact + new Vector2Int(-delta.y, delta.x), previewLayer);
                        PreviewCell(impact + new Vector2Int(delta.y, -delta.x), previewLayer);
                        break;
                    }
                case EChainPatternType.ImpactCrossForward2:
                    PreviewCross(origin + (delta * 2), previewLayer);
                    break;
                case EChainPatternType.ShotgunForward2:
                    {
                        Vector2Int perp = new Vector2Int(-delta.y, delta.x);
                        Vector2Int forward2 = origin + (delta * 2);
                        PreviewCell(origin + delta, previewLayer);     // 1칸 앞
                        PreviewCell(forward2, previewLayer);           // 2칸 앞
                        PreviewCell(forward2 + perp, previewLayer);    // 2칸 앞 양옆
                        PreviewCell(forward2 - perp, previewLayer);
                        break;
                    }
            }
        }
    }

    private void PreviewCross(Vector2Int center, Dictionary<GridSlot, Color> previewLayer)
    {
        PreviewCell(center, previewLayer);
        PreviewCell(center + Vector2Int.up, previewLayer);
        PreviewCell(center + Vector2Int.down, previewLayer);
        PreviewCell(center + Vector2Int.left, previewLayer);
        PreviewCell(center + Vector2Int.right, previewLayer);
    }

    private void PreviewCell(Vector2Int position, Dictionary<GridSlot, Color> previewLayer)
    {
        GridSlot slot = GetSlot(position);
        if (slot == null) return;
        SetPreviewSlot(previewLayer, slot, Color.yellow);
    }

    private static IEnumerable<ECardDirection> EnumerateDirections(ECardDirection dirs)
    {
        if ((dirs & ECardDirection.Up) != 0) yield return ECardDirection.Up;
        if ((dirs & ECardDirection.Down) != 0) yield return ECardDirection.Down;
        if ((dirs & ECardDirection.Left) != 0) yield return ECardDirection.Left;
        if ((dirs & ECardDirection.Right) != 0) yield return ECardDirection.Right;
        if ((dirs & ECardDirection.UpLeft) != 0) yield return ECardDirection.UpLeft;
        if ((dirs & ECardDirection.UpRight) != 0) yield return ECardDirection.UpRight;
        if ((dirs & ECardDirection.DownLeft) != 0) yield return ECardDirection.DownLeft;
        if ((dirs & ECardDirection.DownRight) != 0) yield return ECardDirection.DownRight;
    }

    /// <summary>랜덤 그리드 좌표 1개 반환. 슬롯이 없으면 false.</summary>
    public bool TryGetRandomGridPosition(out Vector2Int position)
    {
        position = Vector2Int.zero;
        if (_slots.Count == 0)
        {
            return false;
        }

        int pick = UnityEngine.Random.Range(0, _slots.Count);
        int index = 0;
        foreach (Vector2Int key in _slots.Keys)
        {
            if (index == pick)
            {
                position = key;
                return true;
            }
            index++;
        }

        return false;
    }

    // ── IGridStatProvider 구현 ──────────────────────────────

    /// <summary>
    /// 지정 카드 기준 scope 범위의 집계 수 반환.
    /// 카드 위치는 역인덱스(_cardPositions)에서 O(1) 조회 후 범위별로 카운트한다.
    /// 미배치 카드·None 범위는 0.
    /// </summary>
    public int GetGridCount(Card card, EGridCountScope scope)
    {
        if (card == null || scope == EGridCountScope.None)
        {
            return 0;
        }

        switch (scope)
        {
            case EGridCountScope.SelfTurnOnCount:
                // 카드 자신의 이번 턴 ON 횟수 (카드 소유 상태에서 조회).
                return card.GridState.TurnOnCount;

            case EGridCountScope.GridTurnOnCount:
                return _turnToggleCount;
            case EGridCountScope.InactiveTotal:
                int inactive = 0;
                foreach (Card placed in _cardPositions.Keys)
                {
                    if (placed != null && !placed.GridState.IsActivated)
                        inactive++;
                }
                return inactive;
        }

        // 공간 집계는 카드 위치가 필요.
        if (!_cardPositions.TryGetValue(card, out Vector2Int pos))
        {
            return 0;
        }

        switch (scope)
        {
            case EGridCountScope.Row:
                return CountActivated(filterRow: true, filterCol: false, pos);
            case EGridCountScope.Column:
                return CountActivated(filterRow: false, filterCol: true, pos);
            case EGridCountScope.Cross:
                // 행 + 열 합산. 교차점(자기 칸) 중복 1회 차감.
                int row = CountActivated(filterRow: true, filterCol: false, pos);
                int col = CountActivated(filterRow: false, filterCol: true, pos);
                return row + col - 1;
            case EGridCountScope.Total:
                return CountActivated(filterRow: false, filterCol: false, pos);
            case EGridCountScope.Adjacent8:
                int adjacent = 0;
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        if (x == 0 && y == 0) continue;
                        if (TryGetOccupiedCard(pos + new Vector2Int(x, y), out _)) adjacent++;
                    }
                }
                return adjacent;
            default:
                return 0;
        }
    }

    /// <summary>
    /// 행/열 필터에 맞는 "활성화(ON)" 카드 수. filterRow=같은 y, filterCol=같은 x.
    /// 둘 다 false 면 그리드 전체 ON 카드 수.
    /// 집계는 점유가 아니라 ON 상태 기준 (인싸형·카운터 등).
    /// </summary>
    private int CountActivated(bool filterRow, bool filterCol, Vector2Int pos)
    {
        int count = 0;
        foreach (KeyValuePair<Card, Vector2Int> kv in _cardPositions)
        {
            Vector2Int p = kv.Value;
            if (filterRow && p.y != pos.y) continue;
            if (filterCol && p.x != pos.x) continue;

            // ON 상태만 집계 (카드 소유 상태 직접 조회).
            if (kv.Key.GridState.IsActivated)
            {
                count++;
            }
        }
        return count;
    }

    // ── 내부 ───────────────────────────────────────────────

    protected override void OnDestroy()
    {
        EndLineAttackVfx();
        RestoreBrokenSlotsImmediate();

        _borderCts?.Cancel();
        _borderCts?.Dispose();
        _borderCts = null;

        if (_borderInstance != null && ResourceManager.HasInstance)
        {
            ResourceManager.Instance.ReleaseInstance(_borderInstance);
        }

        _borderInstance = null;
        _borderRenderer = null;

        base.OnDestroy();
    }

    private void ClearGridState()
    {
        // 그리드 위 카드 상태 초기화 후 인덱스 비우기.
        foreach (Card card in _cardPositions.Keys)
        {
            card.OnLeaveGrid();
            OnCardRemoved?.Invoke(card);
        }

        _slots.Clear();
        _cardPositions.Clear();
        _turnToggleCount = 0;
    }

    /// <summary>
    /// 4방위 방향 → 좌표 델타. 확장 시 대각선 케이스를 추가하면 된다.
    /// Flags 복합 입력은 단일 비트로 호출하는 것을 전제 (복합은 호출 측이 분해).
    /// </summary>
    private static Vector2Int DirectionToDelta(ECardDirection direction) => direction switch
    {
        ECardDirection.Up => new Vector2Int(0, 1),
        ECardDirection.Down => new Vector2Int(0, -1),
        ECardDirection.Left => new Vector2Int(-1, 0),
        ECardDirection.Right => new Vector2Int(1, 0),
        ECardDirection.UpLeft => new Vector2Int(-1, 1),
        ECardDirection.UpRight => new Vector2Int(1, 1),
        ECardDirection.DownLeft => new Vector2Int(-1, -1),
        ECardDirection.DownRight => new Vector2Int(1, -1),
        _ => Vector2Int.zero
    };
}
