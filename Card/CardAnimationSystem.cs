// =================================================================
// [스크립트 목적]  카드 영역 이동 연출 전담 씬 싱글톤.
//                 실제 UICard 를 움직이지 않고, 단일 UI Image 풀 오브젝트로 이동만 표현한다.
// [주요 메서드]    PlayDrawAnim / PlayDiscardAnim : 기존 호출부 호환
//                 PlayDrawTransferAsync           : DrawImage → 목표 Rect
//                 PlayHandToDiscardAsync          : 손패 카드 위치 → DiscardImage
//                 PlayWorldToDiscardAsync         : 월드 좌표 → DiscardImage
//                 PlayDiscardToDrawAsync          : DiscardImage → DrawImage
// [의존 관계]      SceneSingleton, UICard, UICardHand, UIBattle, UIManager, PoolManager
// =================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

public class CardAnimationSystem : SceneSingleton<CardAnimationSystem>
{
    [Header("전송 이미지")]
    [Tooltip("카드 이동을 표현할 단일 UI Image 프리팹 (CardTransferImage)")]
    [SerializeField] private CardTransferImage _transferImagePrefab;

    [Tooltip("전송 이미지에 임시로 적용할 스프라이트. 비우면 프리팹 Image 스프라이트 사용")]
    [SerializeField] private Sprite _transferSprite;

    [Header("드로우 연출")]
    [Tooltip("드로우 전송 시간 (초)")]
    [SerializeField] private float _drawDuration = 0.4f;

    [Tooltip("드로우 전송 시작 스케일")]
    [SerializeField] private float _drawStartScale = 0.4f;

    [Tooltip("카드 사이 딜레이 (촤라락 간격)")]
    [SerializeField] private float _drawInterval = 0.2f;

    [Header("디스카드 연출")]
    [Tooltip("디스카드 전송 시간 (초)")]
    [SerializeField] private float _discardDuration = 0.3f;

    [Tooltip("디스카드 도착 스케일")]
    [SerializeField] private float _discardEndScale = 0.15f;

    [Tooltip("여러 장 이동 시 카드 사이 딜레이")]
    [SerializeField] private float _discardInterval = 0.1f;

    private Transform TransferLayer
        => UIManager.HasInstance ? UIManager.Instance.GetLayerRoot(EUILayer.HUD) : null;

    private Canvas RootCanvas
        => UIManager.HasInstance ? UIManager.Instance.RootCanvas : null;

    private RectTransform DrawPile
    {
        get
        {
            if (!UIManager.HasInstance) return null;
            UIBattle battle = UIManager.Instance.Get<UIBattle>();
            return battle != null ? battle.DrawPileRect : null;
        }
    }

    private RectTransform DiscardPile
    {
        get
        {
            if (!UIManager.HasInstance) return null;
            UIBattle battle = UIManager.Instance.Get<UIBattle>();
            return battle != null ? battle.DiscardPileRect : null;
        }
    }

    /// <summary>기존 호출부 호환. 실제 카드는 숨겼다가 전송 이미지 도착 후 표시한다.</summary>
    public async UniTask PlayDrawAnim(UICard card, UICardHand hand, CancellationToken token = default)
    {
        if (card == null || hand == null) return;

        hand.RefreshLayout();
        Canvas.ForceUpdateCanvases();
        Vector3 targetWorldPosition = card.GetRectTransform().position;

        card.SetVisualVisible(false);
        await PlayDrawTransferAsync(targetWorldPosition, token);

        if (card != null)
        {
            card.SetVisualVisible(true);
            card.SetInteractable(true);
        }
        hand.RefreshLayout();
    }

    /// <summary>DrawImage → 목표 RectTransform 으로 단일 전송 이미지 이동.</summary>
    public UniTask PlayDrawTransferAsync(RectTransform target, CancellationToken token = default)
    {
        if (DrawPile == null || target == null) return UniTask.CompletedTask;
        Vector3 targetWorldPosition = target.position;
        return PlayDrawTransferAsync(targetWorldPosition, token);
    }

    /// <summary>DrawImage → 스냅샷된 월드 좌표로 단일 전송 이미지 이동.</summary>
    public UniTask PlayDrawTransferAsync(Vector3 targetWorldPosition, CancellationToken token = default)
    {
        if (DrawPile == null) return UniTask.CompletedTask;
        return PlayTransferAsync(DrawPile.position, targetWorldPosition, _drawDuration, _drawStartScale, 1f, token);
    }

    /// <summary>기존 테스트 호출부 호환. 데이터 없는 임시 카드들을 손패에 추가하고 전송 이미지만 재생한다.</summary>
    public async UniTask PlayDrawMultipleAnim(UICard cardPrefab, UICardHand hand, int count,
        CancellationToken token = default)
    {
        if (cardPrefab == null || hand == null || count <= 0) return;

        for (int i = 0; i < count; i++)
        {
            UICard card = SpawnCard(cardPrefab, hand);
            if (card == null) continue;

            card.Initialize(hand);
            hand.RegisterCard(card);
            PlayDrawAnim(card, hand, token).Forget();

            await UniTask.Delay(TimeSpan.FromSeconds(_drawInterval), cancellationToken: token);
            if (token.IsCancellationRequested) return;
        }
    }

    /// <summary>기존 호출부 호환. 손패 카드 위치에서 묘지로 전송 이미지를 보낸 뒤 실제 카드는 풀 반환.</summary>
    public async UniTask PlayDiscardAnim(UICard card, CancellationToken token = default)
    {
        if (card == null) return;

        card.SetVisualVisible(false);
        await PlayHandToDiscardAsync(card, token);

        if (card != null)
        {
            card.ReturnToPoolOrDestroy();
        }
    }

    /// <summary>손패 UICard 현재 위치 → DiscardImage.</summary>
    public UniTask PlayHandToDiscardAsync(UICard card, CancellationToken token = default)
    {
        if (card == null || DiscardPile == null) return UniTask.CompletedTask;
        return PlayTransferAsync(card.GetRectTransform().position, DiscardPile.position,
            _discardDuration, 1f, _discardEndScale, token);
    }

    /// <summary>
    /// DrawImage → DiscardImage. 손패가 가득 차 드로우한 카드가 손에 못 들어오고
    /// 곧바로 묘지로 갈 때의 연출 (손패를 거치지 않으므로 UICard 가 없다).
    /// </summary>
    public UniTask PlayDrawToDiscardAsync(CancellationToken token = default)
    {
        if (DrawPile == null || DiscardPile == null) return UniTask.CompletedTask;
        return PlayTransferAsync(DrawPile.position, DiscardPile.position,
            _discardDuration, _drawStartScale, _discardEndScale, token);
    }

    /// <summary>월드 좌표(그리드 슬롯) → DiscardImage. 월드 회전은 쓰지 않고 화면 좌표만 Canvas로 변환한다.</summary>
    public UniTask PlayWorldToDiscardAsync(Vector3 worldPosition, CancellationToken token = default)
    {
        if (DiscardPile == null) return UniTask.CompletedTask;

        Vector3 start = ResolveCanvasWorldPosition(worldPosition);
        return PlayTransferAsync(start, DiscardPile.position, _discardDuration, 1f, _discardEndScale, token);
    }

    /// <summary>DiscardImage → DrawImage 를 count 장만큼 짧은 간격으로 재생하고 모두 완료될 때까지 대기.</summary>
    public async UniTask PlayDiscardToDrawAsync(int count, CancellationToken token = default)
    {
        if (count <= 0 || DrawPile == null || DiscardPile == null) return;

        List<UniTask> tasks = new List<UniTask>(count);
        for (int i = 0; i < count; i++)
        {
            tasks.Add(PlayTransferAsync(DiscardPile.position, DrawPile.position,
                _drawDuration, _discardEndScale, 1f, token));

            await UniTask.Delay(TimeSpan.FromSeconds(_discardInterval), cancellationToken: token);
            if (token.IsCancellationRequested) break;
        }

        await UniTask.WhenAll(tasks);
    }

    /// <summary>손패 전체 디스카드 테스트용 기존 API.</summary>
    public async UniTask PlayDiscardAllAnim(UICardHand hand, CancellationToken token = default)
    {
        if (hand == null) return;

        List<UICard> cards = new List<UICard>(hand.Cards);
        hand.ClearHand(destroy: false);

        List<UniTask> tasks = new List<UniTask>(cards.Count);
        foreach (UICard card in cards)
        {
            if (card == null) continue;
            tasks.Add(PlayDiscardAnim(card, token));
            await UniTask.Delay(TimeSpan.FromSeconds(_discardInterval), cancellationToken: token);
            if (token.IsCancellationRequested) break;
        }

        await UniTask.WhenAll(tasks);
    }

    private UICard SpawnCard(UICard prefab, UICardHand hand)
    {
        if (PoolManager.HasInstance)
        {
            return PoolManager.Instance.Spawn(prefab, hand.transform);
        }
        return Instantiate(prefab, hand.transform);
    }

    private UniTask PlayTransferAsync(
        Vector3 startWorld,
        Vector3 endWorld,
        float duration,
        float startScale,
        float endScale,
        CancellationToken token)
    {
        CardTransferImage image = SpawnTransferImage();
        if (image == null) return UniTask.CompletedTask;

        image.Setup(_transferSprite);
        return image.PlayAsync(startWorld, endWorld, duration, startScale, endScale, token);
    }

    private CardTransferImage SpawnTransferImage()
    {
        if (_transferImagePrefab == null)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogWarning("[CardAnimationSystem] _transferImagePrefab 이 비어있어 카드 이동 연출을 생략합니다.");
#endif
            return null;
        }

        Transform parent = TransferLayer != null ? TransferLayer : transform;
        if (PoolManager.HasInstance)
        {
            return PoolManager.Instance.Spawn(_transferImagePrefab);
        }
        return Instantiate(_transferImagePrefab);
    }

    private Vector3 ResolveCanvasWorldPosition(Vector3 worldPosition)
    {
        Canvas canvas = RootCanvas;
        Camera worldCamera = Camera.main;
        if (canvas == null || worldCamera == null)
        {
            return worldPosition;
        }

        RectTransform canvasRect = canvas.transform as RectTransform;
        if (canvasRect == null)
        {
            return worldPosition;
        }

        Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(worldCamera, worldPosition);
        Camera uiCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        if (RectTransformUtility.ScreenPointToWorldPointInRectangle(
                canvasRect, screenPoint, uiCamera, out Vector3 canvasWorld))
        {
            return canvasWorld;
        }

        return worldPosition;
    }
}
