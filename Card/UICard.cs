// =================================================================
// [스크립트 목적]  카드 비주얼 + 드래그 인터랙션 + 런타임 Card 데이터 보유.
//                 드롭/호버 결과는 어댑터(CardPlacementController)에 콜백으로 통지.
// [주요 변수]      - _shakeParent    : 호버/드래그 시 펀치 애니메이션 대상 Transform
//                  - Owner           : 이 카드를 소유한 UICardHand
//                  - BoundCard       : 이 UICard 가 표현하는 런타임 카드 데이터
//                  - _detailView     : 앞면+설명 표시(UICardDetailView) 컴포넌트
// [콜백]           - OnDropRequested(this, screenPos)   — 드롭됨, 배치 시도 요청
//                  - OnHoverPositionChanged(this, screenPos) — 드래그 중 위치 변경
//                  - OnDragEnded()                      — 드래그 종료 (하이라이트 정리)
// [의존 관계]      MonoBehaviour, UICardHand, DOTween, Card, UICardFrontView
//                  ※ GridCell / EventManager 의존 제거 — 좌표·전투호출은 어댑터 책임
// [배치]           Card 프리팹 루트에 부착.
//                 프리팹 구조:
//                   Card (UICard)
//                   ├─ HoverHitbox (런타임 생성 — 판정 전용 투명 이미지, 제자리 고정)
//                   └─ CardVis (레이캐스트 비대상 — 비주얼 전용)
//                       └─ ShakeParent  ← _shakeParent
//                           ├─ ShadowVis
//                           └─ TiltParent (ShaderManager 붙일 때 재도입)
//                               └─ CardVis (Image)
// [설계 결정]      - UIBase 미사용: 카드는 다수 동적 생성/삭제 → UIManager 패턴 부적합
//                  - sibling 순서로 카드 렌더 순서 제어 (Screen Space Camera)
//                  - 셀 탐지/배치 호출은 UICard 가 직접 안 함 → 어댑터가 스크린좌표로 처리
//                    (UICard 는 월드 그리드·BattleManager 를 모름 — 로직/비주얼 경계)
//                  - _moveTween으로 위치 트윈 독립 관리 (외부 Kill 간섭 방지)
// =================================================================

using System;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class UICard : UIPoolableMonoBehaviour,
    IBeginDragHandler, IDragHandler, IEndDragHandler,
    IPointerEnterHandler, IPointerExitHandler,
    IPointerDownHandler
{
    // ─────────────────────────────────────────────
    // Inspector
    // ─────────────────────────────────────────────

    [Header("레퍼런스")]
    [Tooltip("펀치 애니메이션 대상. 프리팹의 ShakeParent.")]
    [SerializeField] private Transform _shakeParent;

    [Header("유휴 흔들림")]
    [Tooltip("가만히 있을 때 Z축 흔들림 최대 각도 (도).")]
    [SerializeField] private float _idleSwayAmount = 2f;

    [Tooltip("흔들림 속도.")]
    [SerializeField] private float _idleSwaySpeed = 1.2f;

    [Header("선택")]
    [Tooltip("클릭 선택 시 카드가 올라오는 Y 오프셋 (px).")]
    [SerializeField] private float _selectOffsetY = 40f;

    [Tooltip("선택/해제 이동 시간.")]
    [SerializeField] private float _selectDuration = 0.15f;

    [Tooltip("선택 해제 시 내려오는 이동 시간. 선택보다 길게 잡아 스르륵 내려오게 한다.")]
    [SerializeField] private float _deselectDuration = 0.35f;

    [Tooltip("선택 하이라이트 페이드 인/아웃 시간.")]
    [SerializeField] private float _highlightFadeDuration = 0.2f;

    [Tooltip("선택 상태를 표시하는 하이라이트 CanvasGroup.")]
    [SerializeField] private CanvasGroup _selectedHighlight;

    [Header("스케일")]
    [Tooltip("호버 시 스케일 배율.")]
    [SerializeField] private float _hoverScale = 1.15f;

    [Tooltip("호버 시 Y 오프셋 (px).")]
    [SerializeField] private float _hoverOffsetY = 20f;

    [Tooltip("호버 이동 시간.")]
    [SerializeField] private float _hoverDuration = 0.12f;

    [Tooltip("호버 시 카드를 똑바로 세우는(부채꼴 기울기 해제) 회전 시간.")]
    [SerializeField] private float _straightenDuration = 0.12f;

    [Tooltip("드래그 시 스케일 배율.")]
    [SerializeField] private float _dragScale = 1.2f;

    [Tooltip("스케일 트윈 시간.")]
    [SerializeField] private float _scaleDuration = 0.12f;

    [Header("ShakeParent 펀치")]
    [Tooltip("호버 진입 시 펀치 각도.")]
    [SerializeField] private float _hoverPunchAngle = 5f;

    [Tooltip("호버 펀치 시간.")]
    [SerializeField] private float _hoverPunchDuration = 0.15f;

    [Tooltip("드래그 시작/종료 시 위치 펀치 강도.")]
    [SerializeField] private float _dragPunchAmount = 15f;

    [Tooltip("드래그 펀치 시간.")]
    [SerializeField] private float _dragPunchDuration = 0.2f;

    // ─────────────────────────────────────────────
    // 공개 상태
    // ─────────────────────────────────────────────

    public bool IsDragging { get; private set; }
    public bool IsHovering { get; private set; }
    public bool IsSelected { get; private set; }

    /// <summary>이 카드를 소유한 손패. UICardHand가 생성 시 주입.</summary>
    public UICardHand Owner { get; private set; }

    /// <summary>이 UICard 가 표현하는 런타임 카드 데이터. Bind 로 주입.</summary>
    public Card BoundCard { get; private set; }

    // ── 어댑터 콜백 (CardPlacementController 가 구독) ───────
    // UICard 는 그리드·BattleManager 를 직접 모른다. 입력 결과만 통지하고,
    // 좌표 변환·배치 호출·하이라이트는 어댑터가 처리한다 (로직/비주얼 경계).

    /// <summary>드롭됨 — 배치 시도 요청. (카드 자신, 스크린 위치)</summary>
    public event Action<UICard, Vector2> OnDropRequested;

    /// <summary>드래그 중 위치 변경 — 하이라이트/방향 프리뷰 갱신용. (카드 자신, 스크린 위치)</summary>
    public event Action<UICard, Vector2> OnHoverPositionChanged;

    /// <summary>드래그 종료 — 하이라이트 정리용.</summary>
    public event Action OnDragEnded;

    /// <summary>드래그 시작 — 스킬 시전 등 다른 입력 상태 해제용. (카드 자신)</summary>
    public event Action<UICard> OnDragBegan;

    // ─────────────────────────────────────────────
    // Private
    // ─────────────────────────────────────────────

    private Vector3 _originalLocalPos;  // 손패 배치 기준 위치 (Select/Hover 기준점)

    private float _randomOffset;   // 카드마다 흔들림 타이밍 다르게
    private float _baseRotationZ;  // UICardHand가 설정한 손패 부채꼴 회전 기준값

    private Tween _moveTween;   // 위치 트윈 독립 관리
    private Tween _scaleTween;  // 스케일 트윈 독립 관리
    private Tween _punchTween;  // 펀치 트윈 독립 관리
    private Tween _rotateTween; // 호버 정렬/복원 회전 트윈 독립 관리
    private Tween _highlightTween; // 선택 하이라이트 페이드 트윈 독립 관리

    // ── 고정 호버 히트박스 ──────────────────────────
    // 호버/클릭 판정 전용 투명 레이캐스트 영역. 카드 아트는 레이캐스트를 받지 않고,
    // 히트박스는 손패 "제자리"(rest pose)에 매 프레임 역보정으로 고정된다.
    // → 카드가 호버로 떠오르고 확대돼도 판정 영역이 움직이지 않아
    //   가장자리 호버 시 Enter/Exit 무한 반복(떨림)이 구조적으로 불가능하다.
    // 포인터 이벤트는 자식(히트박스)에서 발생해도 부모(UICard) 핸들러로 전달된다.
    private RectTransform _hoverHitbox;
    private Image _hoverHitboxImage;
    private Vector2 _hitboxBaseSize;    // 카드 원본 크기 (rect 기준)
    private Vector2 _hitboxBottomLocal; // 카드 로컬 기준 바닥 중앙점 (핀 기준)

    /// <summary>손패가 평소 내려가 있는 양(px). 호버/선택 시 이만큼 되올려 카드 전체를 노출한다.</summary>
    private float RestLowerY => Owner != null ? Owner.RestLowerY : 0f;

    // ─────────────────────────────────────────────
    // Unity
    // ─────────────────────────────────────────────

    // ─────────────────────────────────────────────
    // 풀 생명주기 (PoolManager 가 호출)
    // ─────────────────────────────────────────────

    /// <summary>최초 풀 생성 시 1회 — 무거운 캐싱 처리 (Awake 대용).</summary>
    protected override void OnPoolCreate()
    {
        base.OnPoolCreate(); // RectTransform/CanvasGroup 캐싱 + sizeDelta 보관
        _randomOffset = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        CreateHoverHitbox();
    }

    /// <summary>
    /// 호버 판정 전용 투명 히트박스 생성(1회) + 카드 아트의 레이캐스트 차단.
    /// 이후 이 카드의 포인터 판정(호버/클릭/드래그 시작)은 오직 히트박스가 받는다.
    /// </summary>
    private void CreateHoverHitbox()
    {
        if (_hoverHitbox != null) return;

        Rect r = RectTransform.rect;
        _hitboxBaseSize = r.size;
        _hitboxBottomLocal = new Vector2(r.center.x, r.yMin);

        var go = new GameObject("HoverHitbox", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.layer = gameObject.layer;

        _hoverHitbox = (RectTransform)go.transform;
        _hoverHitbox.SetParent(transform, false);
        _hoverHitbox.SetAsFirstSibling();
        _hoverHitbox.anchorMin = _hoverHitbox.anchorMax = new Vector2(0.5f, 0f);
        _hoverHitbox.pivot = new Vector2(0.5f, 0f); // 바닥 고정 — 호버 확장은 위로만
        _hoverHitbox.sizeDelta = _hitboxBaseSize;

        _hoverHitboxImage = go.GetComponent<Image>();
        _hoverHitboxImage.color = Color.clear; // 보이지 않지만 레이캐스트는 받는다
        _hoverHitboxImage.raycastTarget = true;

        // 아트/텍스트는 판정에서 제외 — 움직이는 비주얼이 Enter/Exit 을 만들지 않게.
        foreach (Graphic g in GetComponentsInChildren<Graphic>(true))
        {
            if (!ReferenceEquals(g, _hoverHitboxImage))
                g.raycastTarget = false;
        }
    }

    /// <summary>스폰(드로우) 시마다 — 상태 초기화. 데이터 연결은 Bind 가 별도 처리.</summary>
    public override void OnSpawnFromPool()
    {
        base.OnSpawnFromPool();

        IsDragging = false;
        IsHovering = false;
        IsSelected = false;
        SetSelectedHighlightVisible(false);
        // 호버 중 손패가 정리되면 SetHoverRaycast 로 꺼진 raycastTarget 이 박제될 수 있다
        // — 재사용 카드가 클릭/호버 불가가 되지 않게 복원.
        SetRaycastTarget(true);
        SetHitboxHovered(false); // 호버 확장 크기 잔류 방지
        _randomOffset = UnityEngine.Random.Range(0f, Mathf.PI * 2f); // 카드마다 흔들림 타이밍 분산
    }

    /// <summary>풀 반환 시마다 — 트윈·콜백·데이터 정리 (베이스가 RectTransform/CanvasGroup 리셋).</summary>
    public override void OnReturnToPool()
    {
        KillAllTweens();
        SetSelectedHighlightVisible(false);

        // 콜백 구독 정리 (어댑터 참조 누수 방지).
        OnDropRequested = null;
        OnHoverPositionChanged = null;
        OnDragEnded = null;
        OnDragBegan = null;

        // 데이터·소유자 연결 해제 (다음 스폰 시 Bind/Initialize 로 재설정).
        BoundCard = null;
        Owner = null;

        base.OnReturnToPool(); // RectTransform/CanvasGroup 리셋 + DOTween.Kill(transform)
    }

    private void LateUpdate()
    {
        PinHoverHitbox(); // 판정 영역은 상태와 무관하게 항상 제자리 고정

        if (IsDragging) return;

        EnsurePoseConsistency();

        // 호버/선택 중에는 흔들지 않는다 (똑바로 세운 상태 유지).
        if (IsSelected || IsHovering || _shakeParent == null) return;

        float sway = Mathf.Sin(Time.time * _idleSwaySpeed + _randomOffset) * _idleSwayAmount;
        var euler = _shakeParent.localEulerAngles;
        _shakeParent.localEulerAngles = new Vector3(euler.x, euler.y, sway);
    }

    /// <summary>
    /// 상태(선택/호버)가 요구하는 위치·회전·스케일과 실제 값이 어긋나 있고
    /// 진행 중인 트윈도 없으면, 올바른 자세로 되돌리는 보정 트윈을 건다.
    /// ★ 빠른 클릭/드래그 반복으로 이벤트(Exit·복귀 등)가 유실·역전되면 복귀 트윈이
    ///   안 걸린 채 카드가 엉뚱한 위치에 박제될 수 있다 — 이벤트에만 의존하지 않는
    ///   최종 안전망으로, 어떤 인터리빙에서도 카드가 한 프레임 안에 제자리로 수렴한다.
    /// (비용: 트윈 핸들 체크 + 산술 비교 몇 개, 할당 없음 — 어긋났을 때만 트윈 생성)
    /// </summary>
    private void EnsurePoseConsistency()
    {
        if (Owner == null || RectTransform == null) return; // 손패 소속 카드만

        // 위치 — 이동 트윈이 없을 때만 (트윈 진행 중엔 트윈이 책임).
        if (_moveTween == null || !_moveTween.IsActive())
        {
            var target = new Vector3(_originalLocalPos.x, StateTargetY(), _originalLocalPos.z);
            if ((RectTransform.localPosition - target).sqrMagnitude > 1f)
            {
                _moveTween = RectTransform.DOLocalMove(target, 0.15f).SetEase(Ease.OutCubic);
            }
        }

        // 회전 — 호버/선택 중엔 똑바로(0), 아니면 부채꼴 기울기.
        if (_rotateTween == null || !_rotateTween.IsActive())
        {
            float targetZ = (IsHovering || IsSelected) ? 0f : _baseRotationZ;
            if (Mathf.Abs(Mathf.DeltaAngle(transform.localEulerAngles.z, targetZ)) > 0.5f)
            {
                _rotateTween = transform.DOLocalRotate(new Vector3(0f, 0f, targetZ), 0.15f)
                    .SetEase(Ease.OutCubic);
            }
        }

        // 스케일 — 호버 중에만 확대(선택은 원본 크기), 드래그 축소 잔류 등도 복구.
        if (_scaleTween == null || !_scaleTween.IsActive())
        {
            float targetScale = (IsHovering && !IsSelected) ? _hoverScale : 1f;
            if (Mathf.Abs(transform.localScale.x - targetScale) > 0.01f)
            {
                _scaleTween = transform.DOScale(Vector3.one * targetScale, _scaleDuration)
                    .SetEase(Ease.OutCubic);
            }
        }

        // 선택 하이라이트 — alpha 를 IsSelected 와 동기화 (페이드 유실로 남는 잔상 복구).
        if (_selectedHighlight != null
            && (_highlightTween == null || !_highlightTween.IsActive()))
        {
            float targetAlpha = IsSelected ? 1f : 0f;
            if (!Mathf.Approximately(_selectedHighlight.alpha, targetAlpha))
            {
                _highlightTween = _selectedHighlight.DOFade(targetAlpha, _highlightFadeDuration);
            }
        }
    }

    /// <summary>풀 미사용(폴백 Instantiate) 경로 대비 — 파괴 시에도 콜백 정리.</summary>
    private void OnDestroy()
    {
        KillAllTweens();

        OnDropRequested = null;
        OnHoverPositionChanged = null;
        OnDragEnded = null;
        OnDragBegan = null;
    }

    /// <summary>풀로 반환 (풀 소속이면 Despawn, 아니면 Destroy 폴백). 손패에서 빠질 때 호출.</summary>
    public void ReturnToPoolOrDestroy()
    {
        if (PoolManager.HasInstance && PoolManager.Instance.IsPooledInstance(gameObject))
        {
            PoolManager.Instance.Despawn(this);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    [Header("상세 표시")]
    [Tooltip("카드 앞면+설명을 그리는 상세 뷰(UICardDetailView). Bind 시 데이터로 갱신")]
    [SerializeField] private UICardDetailView _detailView;

    // ─────────────────────────────────────────────
    // 초기화 (UICardHand가 호출)
    // ─────────────────────────────────────────────

    /// <summary>UICardHand가 카드 생성 직후 호출해 소유자를 주입.</summary>
    public void Initialize(UICardHand owner)
    {
        Owner = owner;
    }

    /// <summary>
    /// 런타임 카드 데이터를 연결하고 앞면을 갱신한다 (드로우 직후 호출).
    /// 데이터 보유는 UICard 책임(앞면 세팅·드롭 시 어떤 Card 인지 전달).
    /// </summary>
    public void Bind(Card card)
    {
        BoundCard = card;

        if (_detailView != null)
        {
            _detailView.Setup(card);
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        else if (card != null)
        {
            Debug.LogWarning($"[UICard] _detailView 미할당 — 앞면/설명 표시 생략 ({card.CardId}).");
        }
#endif
    }

    public void SetCurrentEnergy(int currentEnergy)
    {
        _detailView?.SetCurrentEnergy(currentEnergy);
    }

    public void SetCurrentBlock(int currentBlock)
    {
        _detailView?.SetCurrentBlock(currentBlock);
    }

    /// <summary>UICardHand가 레이아웃 배치 시 호출. 카드 로컬 위치 설정.</summary>
    public void SetLocalPosition(Vector3 pos)
    {
        _originalLocalPos = pos;

        if (IsDragging) return;

        // 진행 중 이동 트윈(해제 하강/복귀 등)이 있으면 새 레이아웃 위치로 재타겟 —
        // 방치하면 트윈이 옛 자리(시작 시점 _originalLocalPos 기준)로 끌고 가
        // 재배치 후 카드가 어긋난 위치에 정착한다.
        if (_moveTween != null && _moveTween.IsActive())
        {
            KillTween(ref _moveTween);
            _moveTween = RectTransform.DOLocalMove(pos, 0.15f).SetEase(Ease.OutCubic);
            return;
        }

        RectTransform.localPosition = pos;
    }
    /// <summary>
    /// UICardHand가 손패 배치 시 부채꼴 회전 기준값 주입.
    /// 선택/호버 중이 아닐 때만 기준 위치 업데이트.
    /// </summary>
    public void SetBaseRotation(float rotationZ)
    {
        _baseRotationZ = rotationZ;
    }

    /// <summary>
    /// 떠 있는(호버/선택/드래그) 카드의 손패 재배치 갱신. UICardHand.RefreshLayout 이 호출.
    /// 기준 위치·기울기를 항상 새 레이아웃 값으로 갱신하고, 호버/선택 오프셋을
    /// 새 기준에 다시 적용해 카드가 새 자리를 따라가게 한다.
    /// ★ 갱신 없이 건너뛰면 드로우/디스카드로 손패가 재배치될 때 기준값이 낡아,
    ///   호버 해제/선택 해제 시 옛 레이아웃 자리로 돌아가 다른 카드와 겹친다.
    /// </summary>
    public void UpdateRestPose(Vector3 restPos, float rotationZ)
    {
        _originalLocalPos = restPos;
        _baseRotationZ = rotationZ;

        if (IsDragging) return; // 드래그 중엔 손끝을 따라가므로 시각 갱신 없음

        // 현재 상태(호버/선택)에 맞는 오프셋을 새 기준 위치에 다시 적용.
        MoveToY(StateTargetY(), _hoverDuration);

        // 호버/선택 카드는 직립 유지 — 재배치로 기울기를 다시 입히지 않는다.
        if (!IsHovering && !IsSelected)
        {
            KillTween(ref _rotateTween);
            _rotateTween = transform
                .DOLocalRotate(new Vector3(0f, 0f, rotationZ), _hoverDuration)
                .SetEase(Ease.OutCubic);
        }
    }

    /// <summary>
    /// Raycast 대상 여부 설정. UICardHand가 호버 시 겹침 이벤트 방지용으로 사용.
    /// 판정은 고정 히트박스가 전담하므로 히트박스만 토글한다 (아트는 항상 비대상).
    /// </summary>
    public void SetRaycastTarget(bool value)
    {
        if (_hoverHitboxImage != null) _hoverHitboxImage.raycastTarget = value;
    }

    /// <summary>
    /// 히트박스를 손패 제자리(rest pose)에 고정한다. 카드 루트의 이동·회전·확대를
    /// 매 프레임 역보정해 판정 영역이 절대 움직이지 않게 한다.
    /// 손패 소속이 아니면(연출 전용 스폰 등) 카드 바닥에 붙어 함께 움직인다.
    /// </summary>
    private void PinHoverHitbox()
    {
        if (_hoverHitbox == null) return;

        Transform parent = transform.parent;
        if (Owner == null || parent == null)
        {
            _hoverHitbox.localPosition = _hitboxBottomLocal;
            _hoverHitbox.localRotation = Quaternion.identity;
            _hoverHitbox.localScale = Vector3.one;
            return;
        }

        Quaternion restRot = Quaternion.Euler(0f, 0f, _baseRotationZ);
        Vector3 bottomLocal = _originalLocalPos + restRot * (Vector3)_hitboxBottomLocal;
        _hoverHitbox.SetPositionAndRotation(
            parent.TransformPoint(bottomLocal),
            parent.rotation * restRot);

        // 루트 스케일(호버 확대)의 역보정 — 히트박스 월드 크기를 일정하게 유지.
        Vector3 s = transform.localScale;
        _hoverHitbox.localScale = new Vector3(
            1f / Mathf.Max(0.0001f, s.x),
            1f / Mathf.Max(0.0001f, s.y),
            1f);
    }

    /// <summary>
    /// 호버 중에는 히트박스를 위로 늘려(확대 배율 + 떠오르는 높이) 떠오른 카드까지 덮는다.
    /// 진입 시 영역이 넓어지기만 하고(커서 밑에서 줄지 않음) 해제 시엔 커서가 이미
    /// 밖이므로, 크기 전환이 새 Enter/Exit 을 유발하지 않는다.
    /// </summary>
    private void SetHitboxHovered(bool hovered)
    {
        if (_hoverHitbox == null) return;

        Vector2 size = _hitboxBaseSize;
        if (hovered)
        {
            size.x *= _hoverScale;
            size.y = size.y * _hoverScale + RestLowerY + _hoverOffsetY;
        }
        _hoverHitbox.sizeDelta = size;
    }
    public void SetInteractable(bool interactable)
    {
        var cg = GetComponent<CanvasGroup>();
        if (cg == null) cg = gameObject.AddComponent<CanvasGroup>();
        cg.interactable = interactable;
        cg.blocksRaycasts = interactable;
    }

    /// <summary>튜토리얼 설명 중 기존 선택 테두리를 강조 표시로 재사용한다.</summary>
    public void SetTutorialHighlightVisible(bool visible)
    {
        SetSelectedHighlightVisible(visible);
    }

    /// <summary>드로우 전송 이미지가 도착할 때까지 실제 카드 표시를 숨긴다.</summary>
    public void SetVisualVisible(bool visible)
    {
        var cg = GetComponent<CanvasGroup>();
        if (cg == null) cg = gameObject.AddComponent<CanvasGroup>();
        cg.alpha = visible ? 1f : 0f;
        cg.interactable = visible;
        cg.blocksRaycasts = visible;
    }

    // ─────────────────────────────────────────────
    // 드래그 핸들러
    // ─────────────────────────────────────────────

    public void OnBeginDrag(PointerEventData eventData)
    {
        // 드래그 시작 시 현재 선택 해제 — 자기 자신이든 다른 카드든
        // (선택 카드의 배치 미리보기가 드래그 미리보기와 겹치지 않게).
        // 해제만 하고 손패 "쉬는 위치"(_originalLocalPos)는 건드리지 않는다 —
        // SetLocalPosition(RefreshLayout)이 항상 유지하므로, 호버로 올라가 있을 수 있는
        // 현재 localPosition 으로 덮어쓰면 드롭 복귀 시 올라간 위치로 돌아가는 버그가 생긴다.
        Owner?.DeselectCurrent();
        // 진행 중인 이동 트윈(해제 하강·호버 상승·press 선택 상승)이 드래그 위치와
        // 싸우지 않게 무조건 중단. (press 선택 직후 드래그로 이어지는 경우 포함)
        KillTween(ref _moveTween);

        IsDragging = true;
        // 드래그가 호버를 대체 — IsHovering 을 박제 상태로 두면 복귀 후 RefreshLayout 이
        // 이 카드를 건너뛰어(IsHovering continue) 위치를 바로잡지 못한다.
        IsHovering = false;
        SetHitboxHovered(false);
        Owner?.HideKeywordPanel(); // 드래그 시작 시 키워드 패널 정리

        // 손패 카드 드래그 시작 → 진행 중이던 스킬 시전 등 다른 입력 상태를 해제하도록 통지.
        OnDragBegan?.Invoke(this);

        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            RectTransform.parent as RectTransform,
            eventData.position,
            eventData.pressEventCamera,
            out var localPos);
        RectTransform.localPosition = localPos;

        ScaleTo(_dragScale, _scaleDuration, Ease.OutBack);
        PunchShake(Vector3.up * _dragPunchAmount, _dragPunchDuration);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!IsDragging) return;

        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                RectTransform.parent as RectTransform,
                eventData.position,
                eventData.pressEventCamera,
                out var localPos))
        {
            RectTransform.localPosition = localPos;
        }

        // 셀 탐지/하이라이트는 어댑터가 처리 (UICard 는 위치만 통지).
        OnHoverPositionChanged?.Invoke(this, eventData.position);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        IsDragging = false;
        Owner?.ResetRaycast();

        // 드롭 즉시 스케일 복원 — 배치 판정(비동기)이 예외/취소로 끝까지 못 가도
        // 카드가 드래그 축소 스케일로 손패에 박제되지 않게 한다.
        // (성공 시엔 ConfirmPlaced → 풀 반환이 어차피 스케일을 리셋한다.)
        ScaleTo(1f, _scaleDuration, Ease.OutBack);

        // 하이라이트 정리는 어댑터에 위임.
        OnDragEnded?.Invoke();

        // 드롭 위치를 어댑터에 통지 → 어댑터가 배치 성공/실패를 판정 후
        // ConfirmPlaced() 또는 ReturnToHand() 를 호출한다.
        OnDropRequested?.Invoke(this, eventData.position);
    }

    // ─────────────────────────────────────────────
    // 포인터 핸들러
    // ─────────────────────────────────────────────

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (IsDragging) return;
        IsHovering = true;
        // 떠오른 카드 높이까지 판정 영역을 위로 확장. 선택 카드는 호버 연출(떠오름)이
        // 없어 확장이 불필요하고, 확장하면 그리드 하단 칸 클릭 배치를 가로챈다.
        SetHitboxHovered(!IsSelected);

        if (AudioManager.HasInstance)
            AudioManager.Instance.PlayUiAsync(AudioKeys.Card.HOVER).Forget();

        transform.SetAsLastSibling();
        Owner?.SetHoverRaycast(this); // 이 카드만 Raycast 활성, 나머지 비활성

        // 선택된 카드는 이미 강조 자세(선택 높이·원본 크기·직립) — 호버 효과를 겹치지 않는다.
        if (!IsSelected)
        {
            ScaleTo(_hoverScale, _scaleDuration, Ease.OutBack);
            PunchShake(Vector3.forward * _hoverPunchAngle, _hoverPunchDuration);

            // 평소 내려가 있던 만큼(RestLowerY) 되올린 뒤 호버 오프셋을 더해 카드 전체를 노출한다.
            MoveToY(StateTargetY(), _hoverDuration);
        }

        // 유휴 흔들림을 즉시 멈추고(셰이크 부모 0), 부채꼴 기울기를 펴서 카드를 똑바로 세운다.
        // 다 선 뒤(OnComplete)에 키워드 패널을 띄운다 — 그 사이 호버가 풀렸으면 생략.
        if (_shakeParent != null) _shakeParent.localEulerAngles = Vector3.zero;
        KillTween(ref _rotateTween);
        _rotateTween = transform.DOLocalRotate(Vector3.zero, _straightenDuration)
            .SetEase(Ease.OutCubic)
            .OnComplete(() =>
            {
                if (IsHovering) Owner?.ShowKeywordPanel(this);
            });
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (IsDragging) return;
        IsHovering = false;
        SetHitboxHovered(false);

        Owner?.ResetRaycast(); // 모든 카드 Raycast 복원
        Owner?.HideKeywordPanel();

        int index = Owner?.GetCardIndex(this) ?? -1;
        if (index >= 0) transform.SetSiblingIndex(index);

        ScaleTo(1f, _scaleDuration, Ease.OutBack);

        // 똑바로 세웠던 카드를 손패 부채꼴 기울기로 되돌린다 (선택 중이면 직립 유지).
        if (!IsSelected)
        {
            KillTween(ref _rotateTween);
            _rotateTween = transform
                .DOLocalRotate(new Vector3(0f, 0f, _baseRotationZ), _hoverDuration)
                .SetEase(Ease.OutCubic);
        }

        // 선택 중이면 올라온 채 유지, 아니면 다시 평소(내려간) 위치로.
        MoveToY(StateTargetY(), _hoverDuration);
    }

    /// <summary>
    /// 선택/해제 토글은 클릭(release)이 아니라 press 에서 처리한다.
    /// ★ OnPointerClick 은 press/release 대상이 같아야 발생하는데, 드로우/재배치로
    ///   카드가 클릭 도중 움직이면 이벤트가 통째로 씹혀 "선택 해제가 한번씩 안 되는"
    ///   버그가 된다. press 기준이면 카드가 이후에 움직여도 토글이 확실히 들어간다.
    /// </summary>
    public void OnPointerDown(PointerEventData eventData)
    {
        if (IsDragging) return;

        // 우클릭 해제는 UICardHand.Update 가 전역(press)으로 처리 — 여기선 좌클릭 토글만.
        if (eventData.button != PointerEventData.InputButton.Left) return;

        if (IsSelected) Owner?.DeselectCurrent();
        else Owner?.SelectCard(this);

        if (AudioManager.HasInstance)
            AudioManager.Instance.PlayUiAsync(AudioKeys.Card.SELECT).Forget();
    }

    // ─────────────────────────────────────────────
    // 선택 / 해제 (UICardHand가 호출)
    // ─────────────────────────────────────────────

    /// <summary>선택 비주얼 적용. UICardHand.SelectCard()에서 호출.</summary>
    public void Select()
    {
        if (IsSelected) return;
        IsSelected = true;
        FadeSelectedHighlight(true);

        // 호버로 확장돼 있던 히트박스를 본체 크기로 원복 — 확장 영역이 남으면
        // 바로 위 그리드 칸 클릭이 UI 위 클릭으로 판정돼 배치가 막힌다.
        SetHitboxHovered(false);

        // 목표 높이는 항상 상태 기반(StateTargetY)으로 계산한다.
        // ★ 현재 y 를 참조(Mathf.Max)하면 하강 중 재선택 시 공중에 박제된다.
        MoveToY(StateTargetY(), _selectDuration);

        // 선택 자세: 원본 크기 + 직립 (호버 확대가 걸려 있었다면 해제).
        ScaleTo(1f, _scaleDuration, Ease.OutBack);
        if (_shakeParent != null) _shakeParent.localEulerAngles = Vector3.zero;
        KillTween(ref _rotateTween);
        _rotateTween = transform.DOLocalRotate(Vector3.zero, _straightenDuration)
            .SetEase(Ease.OutCubic);

        PunchShake(Vector3.up * 10f, 0.15f);
    }

    /// <summary>선택 해제 비주얼 적용. UICardHand.DeselectCurrent()에서 호출.</summary>
    public void Deselect()
    {
        if (!IsSelected) return;
        IsSelected = false;
        FadeSelectedHighlight(false);
        SetHitboxHovered(IsHovering); // 호버 중 해제면 확장 히트박스 복원

        // 호버 중이 아니면 직립을 풀고 부채꼴 기울기로 복귀.
        if (!IsHovering)
        {
            KillTween(ref _rotateTween);
            _rotateTween = transform
                .DOLocalRotate(new Vector3(0f, 0f, _baseRotationZ), _hoverDuration)
                .SetEase(Ease.OutCubic);
        }

        // 천천히 출발해 스르륵 내려오도록 — 다른 카드 선택 시 뚝 떨어지는 느낌 방지.
        MoveToY(StateTargetY(), _deselectDuration, Ease.InOutCubic);
    }

    /// <summary>선택 상태에서 배치 요청 (클릭 배치). 어댑터가 좌표 해석.</summary>
    public void RequestPlaceFromSelection(Vector2 screenPos)
    {
        OnDropRequested?.Invoke(this, screenPos);
    }

    // ─────────────────────────────────────────────
    // 배치 / 복귀 (어댑터가 판정 후 호출)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 배치 성공 확정 — 어댑터가 BattleManager 배치 성공 후 호출.
    /// 트윈 정리 후 손패에서 제거하고 풀로 반환한다 (그리드 비주얼은 어댑터가 별도 스폰).
    /// </summary>
    public void ConfirmPlaced()
    {
        KillAllTweens();
        SetSelectedHighlightVisible(false);
        Owner?.ResetRaycast();

        Owner?.RemoveCard(this);
        ReturnToPoolOrDestroy();
    }

    /// <summary>드롭 실패 — 손패로 복귀. 어댑터가 배치 실패 시 호출.</summary>
    public void ReturnToHand()
    {
        Owner?.ResetRaycast();

        // 비동기 판정이 늦게 끝나 이미 다음 드래그가 시작된 경우, 복귀 트윈이
        // 드래그 위치와 매 프레임 싸운다 — 위치/스케일은 새 드래그의 드롭이 책임진다.
        if (!IsDragging)
        {
            MoveToY(_originalLocalPos.y, 0.2f, Ease.OutCubic);
            ScaleTo(1f, _scaleDuration, Ease.OutBack);
            PunchShake(Vector3.down * (_dragPunchAmount * 0.5f), _dragPunchDuration);
        }

        // RefreshLayout 이 선택/호버 카드도 UpdateRestPose 로 재정렬하므로,
        // 복귀 시점의 상태(선택 유지 등)에 맞는 높이로 최종 보정된다.
        Owner?.RefreshLayout();
    }

    // ─────────────────────────────────────────────
    // 유틸
    // ─────────────────────────────────────────────

    /// <summary>
    /// 현재 상태(선택/호버)가 요구하는 손패 로컬 Y — 위치 계산의 단일 기준.
    /// Enter/Exit/Select/Deselect/레이아웃 갱신/포즈 보정이 전부 이 값을 쓴다.
    /// (과거엔 각자 계산해서 한 곳만 어긋나도 카드가 엉뚱한 높이에 박제됐다.)
    /// </summary>
    private float StateTargetY()
    {
        float y = _originalLocalPos.y;
        // 선택이 호버보다 우선 — 선택된 카드에는 호버 오프셋을 더하지 않는다 (들썩임 방지).
        // 선택은 RestLowerY 를 되올리지 않는다 — 내려간 손패 기준에서 선택 오프셋만 올린다.
        if (IsSelected) return y + _selectOffsetY;
        if (IsHovering) y += RestLowerY + _hoverOffsetY;
        return y;
    }

    /// <summary>Y축 이동 트윈. X, Z는 _originalLocalPos 기준 고정.</summary>
    private void MoveToY(float targetY, float duration, Ease ease = Ease.OutCubic)
    {
        KillTween(ref _moveTween, true); // 진행 중 트윈 목표값으로 즉시 완료 후 새 트윈 시작
        var target = new Vector3(_originalLocalPos.x, targetY, _originalLocalPos.z);
        _moveTween = RectTransform.DOLocalMove(target, duration).SetEase(ease);
    }

    /// <summary>카드 루트 스케일 즉시 적용.</summary>
    private void ScaleTo(float scale, float duration, Ease ease)
    {
        KillTween(ref _scaleTween, true);
        _scaleTween = transform.DOScale(Vector3.one * scale, duration).SetEase(ease);
    }

    private void PunchShake(Vector3 direction, float duration)
    {
        if (_shakeParent == null) return;
        KillTween(ref _punchTween, true);
        _punchTween = _shakeParent.DOPunchPosition(direction, duration, vibrato: 8, elasticity: 0.5f);
    }

    /// <summary>선택 하이라이트 표시 상태를 즉시 갱신한다 (풀 리셋/배치 확정용).</summary>
    private void SetSelectedHighlightVisible(bool visible)
    {
        if (_selectedHighlight == null) return;

        KillTween(ref _highlightTween); // 진행 중 페이드가 즉시 적용값을 덮지 않게
        _selectedHighlight.alpha = visible ? 1f : 0f;
        _selectedHighlight.interactable = false;
        _selectedHighlight.blocksRaycasts = false;
    }

    /// <summary>선택 하이라이트를 페이드로 표시/숨김 (선택/해제 연출용).</summary>
    private void FadeSelectedHighlight(bool visible)
    {
        if (_selectedHighlight == null) return;

        _selectedHighlight.interactable = false;
        _selectedHighlight.blocksRaycasts = false;

        KillTween(ref _highlightTween);
        _highlightTween = _selectedHighlight.DOFade(visible ? 1f : 0f, _highlightFadeDuration);
    }

    /// <summary>
    /// 개별 트윈 1개를 안전하게 정리 (트윈 재시작 직전용 — MoveToY/ScaleTo/PunchShake).
    /// IsActive() 로 살아있는 핸들만 Kill 하고 참조를 끊는다.
    /// </summary>
    private static void KillTween(ref Tween tween, bool complete = false)
    {
        if (tween != null && tween.IsActive())
        {
            tween.Kill(complete);
        }
        tween = null;
    }

    /// <summary>
    /// 이 카드에 걸린 모든 트윈을 일괄 정리 (풀 반환·파괴·배치확정 공용).
    /// ★ 개별 핸들을 순차 Kill 하면 DOTween 내부 활성 배열이 Kill 도중 재배열되며
    ///   IndexOutOfRangeException 이 날 수 있다. 대상(target) 단위 DOKill 은 재진입 안전하므로
    ///   트윈이 걸린 각 Transform 에 DOKill 을 호출해 통째로 정리한다.
    ///   (_moveTween=RectTransform, _scaleTween=transform, _punchTween=_shakeParent,
    ///    _highlightTween=_selectedHighlight)
    /// </summary>
    private void KillAllTweens(bool complete = false)
    {
        // 핸들 참조부터 끊어 재진입 시 재-Kill 을 방지.
        _moveTween = null;
        _scaleTween = null;
        _punchTween = null;
        _rotateTween = null; // 대상=RectTransform 이라 아래 DOKill 로 함께 정리됨
        _highlightTween = null;

        // 대상 단위 일괄 Kill (DOTween 내부에서 안전하게 처리).
        if (RectTransform != null)
        {
            RectTransform.DOKill(complete); // _moveTween 대상
        }
        // transform 은 RectTransform 과 동일 객체이므로 _scaleTween 도 위에서 함께 정리됨.
        if (_shakeParent != null)
        {
            _shakeParent.DOKill(complete); // _punchTween 대상
        }
        if (_selectedHighlight != null)
        {
            _selectedHighlight.DOKill(complete); // _highlightTween 대상 (CanvasGroup)
        }
    }

    public RectTransform GetRectTransform() => RectTransform;
}
