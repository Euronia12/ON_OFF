// =================================================================
// [스크립트 목적]  손패(Hand) UI. UICard 생성/제거 + 원호 배치.
//                 UIBattle의 자식으로 존재. UIBattle이 직접 관리.
// [주요 변수]      - _cardPrefab    : UICard 컴포넌트를 가진 카드 프리팹
//                  - _anglePerCard  : 카드 1장당 펼침 각도
//                  - _arcRadius     : 원호 반지름
//                  - _restLowerY    : 평소 손패를 화면 아래로 내리는 양(슬더스식 — 호버 시 되올림)
//                  - _cards         : 현재 손패 카드 목록
//                  - _placement     : 카드 제거 시 어댑터 구독 해제용 참조
// [의존 관계]      MonoBehaviour, UICard, CardPlacementController
//                 ※ GridCell 직접 의존 제거 — 선택-클릭 배치도 어댑터 경유
// [배치]           UIBattle 프리팹 자식으로 배치.
//                 UIManager로 직접 열지 않음 — UIBattle.OnOpen/OnClose에서 관리.
// =================================================================

using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.InputSystem;

public class UICardHand : MonoBehaviour
{
    // ─────────────────────────────────────────────
    // Inspector
    // ─────────────────────────────────────────────

    [Header("카드 설정")]
    [Tooltip("UICard 컴포넌트를 가진 카드 프리팹.")]
    [SerializeField] private UICard _cardPrefab;

    [Header("입력 연동")]
    [Tooltip("카드 배치 입력 어댑터. 선택-클릭 배치 위임 + 제거 시 구독 해제")]
    [SerializeField] private CardPlacementInput _placement;

    [Header("손패 배치")]
    [Tooltip("카드 1장당 차지하는 각도 (도). 카드 수에 따라 전체 각도 자동 계산.")]
    [SerializeField] private float _anglePerCard = 6f;

    [Tooltip("최대 펼침 각도 (도). 카드가 많아도 이 이상 벌어지지 않음.")]
    [SerializeField] private float _maxFanAngle = 60f;

    [Tooltip("원호 반지름 (px). 클수록 완만한 곡선, 작을수록 급한 곡선.")]
    [SerializeField] private float _arcRadius = 800f;

    [Tooltip("평소 손패가 화면 아래로 내려가 숨는 정도 (px). 호버/선택한 카드만 이만큼 다시 올라와 전체가 보인다. " +
             "0이면 슬더스식 숨김 없이 항상 전체 표시.")]
    [SerializeField, Min(0f)] private float _restLowerY = 150f;

    [Header("키워드 패널")]
    [Tooltip("카드 호버 시 키워드 설명을 띄우는 공유 패널. 비우면 표시 생략")]
    [SerializeField] private UIKeywordPanel _keywordPanel;

    [Header("스킬 타겟팅 중 흐림")]
    [Tooltip("스킬 타겟팅 중 손패가 대상이 아님을 알리는 흐림 alpha (0=완전 투명, 1=원본).")]
    [SerializeField, Range(0f, 1f)] private float _dimmedAlpha = 0.35f;

    [Tooltip("흐림/복원 페이드 시간(초).")]
    [SerializeField] private float _dimDuration = 0.15f;

    [Tooltip("스킬 선택 중 손패를 화면 아래로 추가 이동할 거리(px).")]
    [SerializeField, Min(0f)] private float _skillTargetLowerY = 130f;

    [Tooltip("스킬 선택 중 손패 이동/복원 시간(초).")]
    [SerializeField, Min(0f)] private float _skillTargetMoveDuration = 0.2f;

    // ─────────────────────────────────────────────
    // 공개 API
    // ─────────────────────────────────────────────

    public IReadOnlyList<UICard> Cards => _cards;
    public int CardCount => _cards.Count;
    public UICard CardPrefab => _cardPrefab;
    public UICard SelectedCard => _selectedCard;

    /// <summary>평소 손패가 아래로 내려간 정도(px). UICard 가 호버/선택 시 이만큼 되올려 카드 전체를 보여준다.</summary>
    public float RestLowerY => _restLowerY;

    // ─────────────────────────────────────────────
    // Private
    // ─────────────────────────────────────────────

    private readonly List<UICard> _cards = new();
    private readonly List<UICard> _tutorialStagedCards = new();
    private UICard _selectedCard;  // 현재 선택된 카드 (단일 선택 보장)
    private PlayerActor _player;
    private int _currentEnergy = int.MaxValue;
    private int _currentBlock;

    private CanvasGroup _canvasGroup; // 스킬 타겟팅 중 손패 흐림용 (없으면 자동 추가)
    private Tween _dimTween;
    private RectTransform _rectTransform;
    private Tween _skillTargetMoveTween;
    private float _baseAnchoredY;

    // ─────────────────────────────────────────────
    // Unity
    // ─────────────────────────────────────────────

    private void Awake()
    {
        _rectTransform = transform as RectTransform;
        if (_rectTransform != null)
            _baseAnchoredY = _rectTransform.anchoredPosition.y;
    }

    private void Update()
    {
        if (_selectedCard == null) return;

        if (_placement != null && Mouse.current != null)
        {
            _placement.PreviewPlacement(
                _selectedCard.BoundCard,
                Mouse.current.position.ReadValue());
        }

        // 선택 상태에서 우클릭 → 어디서든(카드/그리드/빈 공간) 선택 취소.
        if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
        {
            DeselectCurrent();
            return;
        }

        // 선택된 카드가 있을 때 좌클릭 → 마우스 위치로 배치 시도 (어댑터가 슬롯 해석).
        // 카드/UI 위를 클릭한 경우는 제외 (카드 클릭과 충돌 방지).
        if (Mouse.current != null
            && Mouse.current.leftButton.wasPressedThisFrame
            && !UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
        {
            Vector2 mousePos = Mouse.current.position.ReadValue();
            UICard card = _selectedCard;
            DeselectCurrent();
            // 어댑터가 슬롯 해석 + 배치 + 성공/실패 처리.
            card.RequestPlaceFromSelection(mousePos);
        }
    }

    // ─────────────────────────────────────────────
    // 생명주기 (UIBattle이 호출)
    // ─────────────────────────────────────────────

    public void Open()
    {
        gameObject.SetActive(true);
    }

    public void Close()
    {
        UnbindPlayer();
        ClearHand();
        gameObject.SetActive(false);
    }

    public void BindPlayer(PlayerActor player)
    {
        UnbindPlayer();
        _player = player;
        if (_player == null) return;

        _player.OnEnergyChanged += HandleEnergyChanged;
        _player.OnBlockChanged += HandleBlockChanged;
        HandleEnergyChanged(_player.Energy, _player.MaxEnergy);
        HandleBlockChanged(_player.Block);
    }

    public void UnbindPlayer()
    {
        if (_player != null)
        {
            _player.OnEnergyChanged -= HandleEnergyChanged;
            _player.OnBlockChanged -= HandleBlockChanged;
        }
        _player = null;
        _currentEnergy = int.MaxValue;
        _currentBlock = 0;
    }

    // ─────────────────────────────────────────────
    // 카드 추가 / 제거
    // ─────────────────────────────────────────────

    /// <summary>손패 끝에 카드 한 장 추가. 생성된 UICard 반환.</summary>
    public UICard AddCard()
    {
        if (_cardPrefab == null)
        {
            GameLogger.LogError(ELogCategory.UI, "[UICardHand] _cardPrefab이 미지정.");
            return null;
        }

        if (!PoolManager.HasInstance)
        {
            GameLogger.LogError(ELogCategory.UI, "[UICardHand] PoolManager가 없어 UICard를 생성할 수 없습니다.");
            return null;
        }

        UICard card = PoolManager.Instance.Spawn(_cardPrefab, transform);
        if (card == null) return null;

        card.Initialize(this);
        card.SetCurrentEnergy(_currentEnergy);
        card.SetCurrentBlock(_currentBlock);
        _cards.Add(card);

        RefreshLayout();
        return card;
    }

    /// <summary>CardAnimationSystem 드로우 연출용. 카드 등록만 하고 위치는 외부에서 처리.</summary>
    public void RegisterCard(UICard card)
    {
        card.Initialize(this);
        card.SetCurrentEnergy(_currentEnergy);
        card.SetCurrentBlock(_currentBlock);
        _cards.Add(card);
        RefreshLayout();
    }

    /// <summary>튜토리얼에서 실제 손패 데이터는 유지한 채 카드 UI만 임시로 숨긴다.</summary>
    public bool StageCardForTutorial(UICard card)
    {
        if (card == null || !_cards.Remove(card)) return false;

        if (_selectedCard == card)
            DeselectCurrent();

        card.SetVisualVisible(false);
        card.SetInteractable(false);
        card.SetRaycastTarget(false);
        _tutorialStagedCards.Add(card);
        RefreshLayout();
        return true;
    }

    /// <summary>튜토리얼에서 숨긴 카드 UI를 손패 레이아웃에 다시 편입한다.</summary>
    public bool RestoreTutorialStagedCard(UICard card)
    {
        if (card == null || !_tutorialStagedCards.Remove(card)) return false;

        card.SetCurrentEnergy(_currentEnergy);
        card.SetCurrentBlock(_currentBlock);
        _cards.Add(card);
        RefreshLayout();
        return true;
    }

    /// <summary>중단·전투 종료 시 숨긴 튜토리얼 카드 UI를 모두 복구한다.</summary>
    public void RestoreAllTutorialStagedCards()
    {
        for (int i = 0; i < _tutorialStagedCards.Count; i++)
        {
            UICard card = _tutorialStagedCards[i];
            if (card == null) continue;

            card.SetCurrentEnergy(_currentEnergy);
            card.SetCurrentBlock(_currentBlock);
            card.SetVisualVisible(true);
            _cards.Add(card);
        }

        _tutorialStagedCards.Clear();
        RefreshLayout();
    }

    private void HandleEnergyChanged(int current, int max)
    {
        _currentEnergy = current;
        for (int i = 0; i < _cards.Count; i++)
            _cards[i]?.SetCurrentEnergy(current);
    }

    private void HandleBlockChanged(int current)
    {
        _currentBlock = Mathf.Max(0, current);
        for (int i = 0; i < _cards.Count; i++)
            _cards[i]?.SetCurrentBlock(_currentBlock);
    }

    /// <summary>손패에서 카드 제거.</summary>
    public void RemoveCard(UICard card)
    {
        if (!_cards.Contains(card)) return;

        // 선택된 카드가 제거되면 선택 상태·그리드 미리보기를 함께 정리.
        // (풀 재사용 시 _selectedCard 가 새 카드를 가리키는 유령 선택 방지)
        if (_selectedCard == card)
            DeselectCurrent();

        // 어댑터 콜백 구독 해제 (누수 방지).
        if (_placement != null)
        {
            _placement.UnregisterCard(card);
        }

        _cards.Remove(card);
        RefreshLayout();
    }

    /// <summary>손패 전체 카드 제거.</summary>
    /// <param name="destroy">true면 즉시 풀 반환(또는 Destroy), false면 CardAnimationSystem이 처리.</param>
    public void ClearHand(bool destroy = true)
    {
        // 카드를 선택한 채 손패가 정리(턴 종료 디스카드 등)되면 선택 상태와
        // 그리드 미리보기가 남는다 — 비우기 전에 반드시 해제.
        DeselectCurrent();

        if (destroy)
        {
            foreach (var card in _cards)
            {
                if (card != null)
                {
                    // 어댑터 구독 해제 후 풀 반환 (누수 방지).
                    if (_placement != null) _placement.UnregisterCard(card);
                    card.ReturnToPoolOrDestroy();
                }
            }
        }
        _cards.Clear();

        // 스테이징 카드는 일반 손패 목록 밖에 있으므로 정리 경로에서 별도로 회수한다.
        for (int i = 0; i < _tutorialStagedCards.Count; i++)
        {
            UICard card = _tutorialStagedCards[i];
            if (card == null) continue;
            if (_placement != null) _placement.UnregisterCard(card);
            card.ReturnToPoolOrDestroy();
        }
        _tutorialStagedCards.Clear();
    }

    // ─────────────────────────────────────────────
    // 레이아웃
    // ─────────────────────────────────────────────

    /// <summary>손패 카드 위치/회전/sibling 순서 갱신.</summary>
    public void RefreshLayout()
    {
        int count = _cards.Count;
        if (count == 0) return;

        for (int i = 0; i < count; i++)
        {
            var card = _cards[i];
            if (card == null) continue;

            ComputeRestPose(i, count, out Vector3 restPos, out float rotAngle);

            // 떠 있는 카드(드래그/선택/호버)도 기준값은 반드시 갱신 —
            // 건너뛰면 드로우/디스카드 재배치 때 기준이 낡아, 해제 시 옛 자리로 돌아간다.
            if (card.IsDragging || card.IsSelected || card.IsHovering)
            {
                card.UpdateRestPose(restPos, rotAngle);
                // 호버(맨 앞 고정)/드래그 카드는 sibling 유지, 선택만 된 카드는 정렬.
                if (!card.IsHovering && !card.IsDragging)
                    card.transform.SetSiblingIndex(i);
                continue;
            }

            card.SetLocalPosition(restPos);
            card.transform.localRotation = Quaternion.Euler(0f, 0f, rotAngle);
            card.SetBaseRotation(rotAngle);

            // sibling: 인덱스 순서대로 (왼쪽이 뒤, 오른쪽이 앞)
            card.transform.SetSiblingIndex(i);
        }
    }

    /// <summary>
    /// index/count 에 해당하는 부채꼴 쉬는 위치·기울기 계산 — 레이아웃 산식의 단일 기준.
    /// (RefreshLayout 과 GetCardWorldPosition 이 공유. 평소엔 _restLowerY 만큼 내려
    ///  일부만 보이게 하고, 호버/선택 시 UICard 가 되올린다.)
    /// </summary>
    private void ComputeRestPose(int index, int count, out Vector3 restPos, out float rotationZ)
    {
        float fanAngle = Mathf.Min(_anglePerCard * (count - 1), _maxFanAngle);
        float t = count == 1 ? 0.5f : (float)index / (count - 1);
        float posAngle = Mathf.Lerp(-fanAngle / 2f, fanAngle / 2f, t);
        float posAngleRad = posAngle * Mathf.Deg2Rad;

        float x = _arcRadius * Mathf.Sin(posAngleRad);
        float y = _arcRadius * (Mathf.Cos(posAngleRad) - 1f);

        restPos = new Vector3(x, y - _restLowerY, 0f);
        rotationZ = -posAngle;
    }

    /// <summary>카드의 _cards 리스트 인덱스 반환. 없으면 -1.</summary>
    public int GetCardIndex(UICard card) => _cards.IndexOf(card);

    // ─────────────────────────────────────────────
    // 선택 관리
    // ─────────────────────────────────────────────

    /// <summary>카드 선택. 이미 다른 카드가 선택돼있으면 먼저 해제.</summary>
    public void SelectCard(UICard card)
    {
        if (_selectedCard == card) return;

        // 기존 선택 해제
        if (_selectedCard != null)
            _selectedCard.Deselect();

        _selectedCard = card;
        _selectedCard.Select();
    }

    /// <summary>현재 선택된 카드 해제.</summary>
    public void DeselectCurrent()
    {
        if (_selectedCard == null) return;

        if (GridManager.HasInstance)
        {
            GridManager.Instance.ClearHighlights();
            GridManager.Instance.ClearPlacementPreview();
        }

        _selectedCard.Deselect();
        _selectedCard = null;
    }

    /// <summary>특정 카드 호버 시 해당 카드만 RaycastTarget 활성, 나머지 비활성.</summary>
    public void SetHoverRaycast(UICard hoveredCard)
    {
        foreach (var card in _cards)
        {
            if (card == null) continue;
            card.SetRaycastTarget(card == hoveredCard);
        }
    }

    /// <summary>호버 해제 시 모든 카드 RaycastTarget 복원.</summary>
    public void ResetRaycast()
    {
        foreach (var card in _cards)
        {
            if (card == null) continue;
            card.SetRaycastTarget(true);
        }
    }

    // ─────────────────────────────────────────────
    // 키워드 패널 (UICard 가 호버 시 위임)
    // ─────────────────────────────────────────────

    /// <summary>호버한 카드의 키워드 설명 패널을 카드 위치에 띄운다. UICard.OnPointerEnter 가 호출.</summary>
    public void ShowKeywordPanel(UICard card)
    {
        if (_keywordPanel == null || card == null || card.BoundCard == null) return;

        _keywordPanel.MoveTo(card.GetRectTransform());
        _keywordPanel.Show(card.BoundCard);
    }

    /// <summary>키워드 패널 숨김. UICard.OnPointerExit / 드래그 시작이 호출.</summary>
    public void HideKeywordPanel()
    {
        if (_keywordPanel != null) _keywordPanel.Hide();
    }

    // ─────────────────────────────────────────────
    // 스킬 타겟팅 중 흐림 (UIBattle 이 캐스팅 시작/종료에 맞춰 호출)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 스킬 타겟팅 중 손패를 흐리게(+상호작용 차단) 해 "스킬 대상이 아님"을 알린다.
    /// blocksRaycasts 는 유지 — 손패 클릭은 그대로 막아 기존 "다른 행동 → 시전 취소" 동작을 보존한다.
    /// </summary>
    public void SetDimmed(bool dimmed)
    {
        if (_canvasGroup == null)
        {
            _canvasGroup = GetComponent<CanvasGroup>();
            if (_canvasGroup == null) _canvasGroup = gameObject.AddComponent<CanvasGroup>();
        }

        // 흐릴 때 호버/드래그 차단. 흐려도 키워드 패널이 떠 있을 수 있으니 함께 정리.
        _canvasGroup.interactable = !dimmed;
        if (dimmed) HideKeywordPanel();

        _dimTween?.Kill();
        float target = dimmed ? _dimmedAlpha : 1f;
        _dimTween = _canvasGroup.DOFade(target, _dimDuration)
                                .SetUpdate(true)
                                .SetRecyclable(false)
                                .SetLink(gameObject)
                                .OnKill(() => _dimTween = null);
    }

    /// <summary>스킬 선택 단계부터 타겟팅 종료까지 손패의 흐림과 하강을 함께 제어한다.</summary>
    public void SetSkillSelectionActive(bool active)
    {
        SetDimmed(active);
        if (_rectTransform == null) return;

        _skillTargetMoveTween?.Kill();
        float targetY = active ? _baseAnchoredY - _skillTargetLowerY : _baseAnchoredY;
        _skillTargetMoveTween = _rectTransform.DOAnchorPosY(targetY, _skillTargetMoveDuration)
                                             .SetEase(Ease.OutCubic)
                                             .SetUpdate(true)
                                             .SetRecyclable(false)
                                             .SetLink(gameObject)
                                             .OnKill(() => _skillTargetMoveTween = null);
    }

    private void OnDestroy()
    {
        _dimTween?.Kill();
        _skillTargetMoveTween?.Kill();
    }

    /// <summary>CardAnimationSystem이 드로우 목표 위치를 알 수 있도록 반환.</summary>
    public Vector3 GetCardWorldPosition(UICard card)
    {
        int index = _cards.IndexOf(card);
        if (index < 0) return transform.position;

        ComputeRestPose(index, _cards.Count, out Vector3 restPos, out _);
        return transform.TransformPoint(restPos);
    }

    // ─────────────────────────────────────────────
    // 에디터 Gizmo
    // ─────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        var rt = GetComponent<RectTransform>();
        if (rt == null) return;

        var canvas = GetComponentInParent<Canvas>();
        float scale = canvas != null ? canvas.transform.lossyScale.y : 1f;
        float radiusWorld = _arcRadius * scale;

        Vector3 worldPos = rt.position;
        Vector3 centerWorld = worldPos + Vector3.down * radiusWorld;

        UnityEditor.Handles.color = new Color(1f, 0.4f, 0.1f, 0.9f);
        UnityEditor.Handles.DrawWireDisc(centerWorld, Vector3.forward, 10f * scale);
        UnityEditor.Handles.Label(centerWorld + Vector3.right * 12f * scale,
            $"원 중심  (ArcRadius: {_arcRadius}px)");

        float gizmoFanAngle = Mathf.Min(_anglePerCard * Mathf.Max(_cards.Count - 1, 1), _maxFanAngle);

        UnityEditor.Handles.color = new Color(1f, 0.8f, 0.1f, 0.4f);
        float startAngle = 90f + gizmoFanAngle / 2f;
        UnityEditor.Handles.DrawWireArc(
            centerWorld,
            Vector3.forward,
            Quaternion.Euler(0f, 0f, startAngle) * Vector3.right,
            -gizmoFanAngle,
            radiusWorld);

        UnityEditor.Handles.color = new Color(1f, 0.4f, 0.1f, 0.3f);
        UnityEditor.Handles.DrawDottedLine(worldPos, centerWorld, 4f);

        UnityEditor.Handles.color = new Color(0.3f, 0.8f, 1f, 0.25f);
        foreach (var card in _cards)
        {
            if (card == null) continue;
            var cardRt = card.GetComponent<RectTransform>();
            if (cardRt != null)
                UnityEditor.Handles.DrawDottedLine(cardRt.position, centerWorld, 3f);
        }
    }
#endif
}
