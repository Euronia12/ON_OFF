// =================================================================
// [스크립트 목적]  호버된 카드의 키워드 설명을 모아 보여주는 패널(슬더스식).
//                 카드의 ECardKeyword 비트마스크를 받아, 켜진 키워드마다 항목을
//                 풀에서 스폰해 세로로 나열하고 표시/숨김(CanvasGroup)만 담당.
// [주요 변수]      - _canvasGroup   : 표시 토글 + 레이캐스트 차단(호버 깜빡임 방지)
//                  - _entryPrefab   : 항목 프리팹(UIKeywordEntry)
//                  - _entryRoot     : 항목들이 붙는 컨테이너(VerticalLayoutGroup 권장)
// [의존 관계]      - UIKeywordEntry / CardKeywordInfo / PoolManager / Card
//                  - UICardHand 가 Show/Hide 호출 (호버 카드의 BoundCard.Keywords 전달)
// [배치]           Screen Space Canvas 자식. 위치는 프리팹 앵커로 고정(코드 미관여).
//                  호버 카드 "오른쪽"에 두는 건 앵커/레이아웃으로 배치.
// [설계 노트]      - UICardDetailPanel 과 동일 철학: 표시 토글은 alpha, 위치는 앵커.
//                  - 항목은 풀에서 스폰 → Hide 시 전량 회수(누수 방지).
//                  - 키워드 없으면 표시하지 않는다(빈 패널 방지).
// =================================================================

using System.Collections.Generic;
using UnityEngine;

/// <summary>호버 카드의 키워드 설명을 나열하는 패널. 내용은 항목이 그리고, 이 클래스는 구성·표시만.</summary>
public sealed class UIKeywordPanel : MonoBehaviour
{
    private const string PHANTOM_MAGIC_CARD_ID = "Spawner";

    [Header("표시")]
    [Tooltip("표시 토글 + 레이캐스트 차단용. 비우면 자동 추가")]
    [SerializeField] private CanvasGroup _canvasGroup;

    [Header("항목")]
    [Tooltip("키워드 항목 프리팹(UIKeywordEntry)")]
    [SerializeField] private UIKeywordEntry _entryPrefab;

    [Tooltip("항목이 붙는 컨테이너. VerticalLayoutGroup 으로 세로 나열 권장. 비우면 이 오브젝트 사용")]
    [SerializeField] private RectTransform _entryRoot;

    [Header("위치")]
    [Tooltip("MoveTo 시 호버 카드 위치에서 더할 오프셋(px). 카드 절반 너비만큼 X 를 주면 카드 오른쪽 모서리부터 펼쳐진다.")]
    [SerializeField] private Vector2 _offset = new Vector2(100f, 0f);

    [Header("환영 마법 미리보기")]
    [Tooltip("환영 마법 호버 시 키워드 패널 대신 표시할 정적 카드 프리팹.")]
    [SerializeField] private UICardDetailView _cardPreviewPrefab;

    [Tooltip("환영 검 미리보기의 원본 카드 대비 크기 배율.")]
    [SerializeField, Range(0.1f, 1.5f)] private float _cardPreviewScale = 0.75f;

    [Tooltip("호버한 카드 중심을 기준으로 한 환영 검 전용 위치 오프셋(px). 일반 키워드 패널 위치에는 영향을 주지 않습니다.")]
    [SerializeField] private Vector2 _phantomPreviewOffset = new Vector2(150f, 60f);

    private RectTransform _cardPreviewRoot;
    private UICardDetailView _cardPreview;
    private RectTransform _currentAnchor;

    private readonly List<UIKeywordEntry> _activeEntries = new();

    private void Awake()
    {
        if (_canvasGroup == null)
        {
            _canvasGroup = GetComponent<CanvasGroup>();
            if (_canvasGroup == null) _canvasGroup = gameObject.AddComponent<CanvasGroup>();
        }
        Hide();
    }

    /// <summary>카드 키워드 비트마스크로 패널을 채우고 표시. 키워드 없으면 숨긴 채 둔다.</summary>
    public void Show(ECardKeyword keywords)
    {
        HideCardPreview();
        ClearEntries();

        if (!CardKeywordInfo.HasAny(keywords) || _entryPrefab == null)
        {
            Hide();
            return;
        }

        Transform parent = EntryParent;
        bool anySpawned = false;
        foreach (var keys in CardKeywordInfo.Enumerate(keywords))
        {
            UIKeywordEntry entry = SpawnEntry(parent);
            if (entry == null) continue;

            entry.Setup(keys);
            _activeEntries.Add(entry);
            anySpawned = true;
        }

        if (!anySpawned)
        {
            Hide();
            return;
        }

        _canvasGroup.alpha = 1f;
        _canvasGroup.blocksRaycasts = false; // 마우스 통과 → 카드 호버 유지(깜빡임 방지).
    }

    /// <summary>카드 정보에 따라 일반 키워드 또는 환영 마법 전용 생성 카드 미리보기를 표시한다.</summary>
    public void Show(Card card)
    {
        if (TryShowPhantomMagicPreview(card))
            return;

        if (TryShowTurnEndGeneratedCardPreview(card))
            return;

        Show(card != null ? card.Keywords : ECardKeyword.None);
    }

    /// <summary>패널 숨김 + 항목 전량 회수.</summary>
    public void Hide()
    {
        ClearEntries();
        HideCardPreview();
        _currentAnchor = null;
        if (_canvasGroup != null)
        {
            _canvasGroup.alpha = 0f;
            _canvasGroup.blocksRaycasts = false;
        }
    }

    // ── 내부 ───────────────────────────────────────────────

    private Transform EntryParent => _entryRoot != null ? _entryRoot : transform;

    public void MoveTo(RectTransform anchor)
    {
        if (anchor == null) return;

        _currentAnchor = anchor;

        // 카드 위치 + 오프셋. 오프셋은 호버 카드 기준 로컬 방향(스케일 반영)으로 적용해
        // 카드가 커지거나 회전해도 일정한 위치 관계를 유지한다.
        transform.position = anchor.TransformPoint(_offset);
        SyncCardPreviewPosition();
    }

    private bool TryShowPhantomMagicPreview(Card card)
    {
        // 현재는 환영 마법 한 장만 대상으로 한다.
        // 추후 모든 생성 카드로 확장하려면 CardId 조건을 제거하고 SpawnOffspringEffect 보유 여부만 검사하면 된다.
        if (card == null || card.CardId != PHANTOM_MAGIC_CARD_ID || _cardPreviewPrefab == null)
            return false;

        string spawnedCardId = GetSpawnedCardId(card);
        return TryShowCardPreview(spawnedCardId);
    }

    /// <summary>턴 종료 생성 블록에는 키워드 대신 실제로 생성되는 카드를 표시한다.</summary>
    private bool TryShowTurnEndGeneratedCardPreview(Card card)
    {
        if (card == null || !card.GeneratesCardsOnTurnEnd || _cardPreviewPrefab == null)
            return false;

        return TryShowCardPreview(card.TurnEndGeneratedCardId);
    }

    private bool TryShowCardPreview(string previewCardId)
    {
        if (string.IsNullOrEmpty(previewCardId) || !DataManager.HasInstance)
            return false;

        CardDataSO previewCardData = DataManager.Instance.GetData<CardDataSO>(previewCardId);
        Card previewCard = previewCardData != null
            ? CardFactory.CreatePreviewFromSO(previewCardData)
            : null;
        if (previewCard == null)
            return false;

        EnsureCardPreview();
        if (_cardPreview == null)
            return false;

        ClearEntries();
        _canvasGroup.alpha = 1f;
        _canvasGroup.blocksRaycasts = false;

        _cardPreview.Setup(previewCard);
        _cardPreview.transform.localScale = Vector3.one * _cardPreviewScale;
        SyncCardPreviewPosition();
        _cardPreviewRoot.SetAsLastSibling();
        _cardPreviewRoot.gameObject.SetActive(true);
        return true;
    }

    private void EnsureCardPreview()
    {
        if (_cardPreviewRoot != null)
            return;

        GameObject root = new GameObject(
            "SpecialCardPreview",
            typeof(RectTransform),
            typeof(CanvasGroup),
            typeof(UnityEngine.UI.LayoutElement));
        root.layer = gameObject.layer;
        root.transform.SetParent(transform, false);

        _cardPreviewRoot = root.GetComponent<RectTransform>();
        _cardPreviewRoot.anchorMin = new Vector2(0f, 0.5f);
        _cardPreviewRoot.anchorMax = new Vector2(0f, 0.5f);
        _cardPreviewRoot.pivot = new Vector2(0f, 0.5f);

        UnityEngine.UI.LayoutElement layoutElement = root.GetComponent<UnityEngine.UI.LayoutElement>();
        layoutElement.ignoreLayout = true;

        CanvasGroup previewCanvasGroup = root.GetComponent<CanvasGroup>();
        previewCanvasGroup.interactable = false;
        previewCanvasGroup.blocksRaycasts = false;

        _cardPreview = Instantiate(_cardPreviewPrefab, _cardPreviewRoot);
        RectTransform previewRect = (RectTransform)_cardPreview.transform;
        previewRect.anchorMin = new Vector2(0f, 0.5f);
        previewRect.anchorMax = new Vector2(0f, 0.5f);
        previewRect.pivot = new Vector2(0f, 0.5f);
        previewRect.anchoredPosition = Vector2.zero;
        previewRect.localRotation = Quaternion.identity;
        previewRect.localScale = Vector3.one * _cardPreviewScale;

        root.SetActive(false);
    }

    private void SyncCardPreviewPosition()
    {
        if (_cardPreviewRoot == null)
            return;

        if (_currentAnchor != null)
        {
            _cardPreviewRoot.position = _currentAnchor.TransformPoint(_phantomPreviewOffset);
            _cardPreviewRoot.rotation = Quaternion.identity;
            return;
        }

        _cardPreviewRoot.anchoredPosition = Vector2.zero;
        _cardPreviewRoot.localRotation = Quaternion.identity;
    }

    private void HideCardPreview()
    {
        if (_cardPreviewRoot != null)
            _cardPreviewRoot.gameObject.SetActive(false);
    }

    private static string GetSpawnedCardId(Card card)
    {
        foreach (CardEffectBase effect in card.Effects)
        {
            if (effect is SpawnOffspringEffect spawnEffect
                && !string.IsNullOrEmpty(spawnEffect.SpawnCardId))
            {
                return spawnEffect.SpawnCardId;
            }
        }

        return string.Empty;
    }

    private UIKeywordEntry SpawnEntry(Transform parent)
    {
        if (PoolManager.HasInstance)
        {
            return PoolManager.Instance.Spawn(_entryPrefab, parent);
        }
        return Instantiate(_entryPrefab, parent);
    }

    /// <summary>
    /// 항목 전량 회수. 반드시 패널이 활성인 동안에만 호출한다(Show/Hide 경로).
    /// 풀 반환은 항목을 풀 루트로 리페어런팅하는데, Unity는 부모가 비활성화되는 도중의
    /// SetParent 를 금지하므로 OnDisable 에서 호출하면 안 된다.
    /// </summary>
    private void ClearEntries()
    {
        for (int i = 0; i < _activeEntries.Count; i++)
        {
            var entry = _activeEntries[i];
            if (entry == null) continue;

            if (PoolManager.HasInstance && PoolManager.Instance.IsPooledInstance(entry.gameObject))
            {
                PoolManager.Instance.Despawn(entry);
            }
            else
            {
                Destroy(entry.gameObject);
            }
        }
        _activeEntries.Clear();
    }
}
