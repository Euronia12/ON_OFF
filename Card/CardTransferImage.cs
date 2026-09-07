// =================================================================
// [스크립트 목적]  카드 영역 이동을 표현하는 단일 UI Image 풀 오브젝트.
//                 실제 UICard 를 움직이지 않고, 임시 이미지 한 장만 Canvas 위에서 이동시킨다.
// [의존 관계]      - UIPoolableMonoBehaviour / PoolManager / DOTween / Image
// =================================================================

using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Image))]
public sealed class CardTransferImage : UIPoolableMonoBehaviour
{
    [Header("표시")]
    [Tooltip("이동 이미지의 기본 크기. 0 이하면 프리팹 크기 유지")]
    [SerializeField] private Vector2 _overrideSize = Vector2.zero;

    private Image _image;
    private RectTransform _rect;
    private Tween _moveTween;
    private Tween _scaleTween;
    private Tween _fadeTween;

    private RectTransform TransferRect
    {
        get
        {
            if (_rect == null) _rect = GetComponent<RectTransform>();
            return _rect;
        }
    }

    protected override void OnPoolCreate()
    {
        base.OnPoolCreate();
        _rect = GetComponent<RectTransform>();
        _image = GetComponent<Image>();
    }

    public override void OnSpawnFromPool()
    {
        base.OnSpawnFromPool();
        if (_image != null)
        {
            _image.enabled = true;
            _image.raycastTarget = false;
        }
    }

    public override void OnReturnToPool()
    {
        KillTweens();
        base.OnReturnToPool();
    }

    /// <summary>이동 이미지 표시값 세팅. sprite 가 null이면 프리팹 Image 설정을 그대로 사용한다.</summary>
    public void Setup(Sprite sprite)
    {
        if (_image == null) _image = GetComponent<Image>();
        if (_image != null && sprite != null)
        {
            _image.sprite = sprite;
        }

        if (_overrideSize.x > 0f && _overrideSize.y > 0f)
        {
            TransferRect.sizeDelta = _overrideSize;
        }
    }

    /// <summary>Canvas 월드 좌표 start → end 로 이동 후 풀 반환.</summary>
    public async UniTask PlayAsync(
        Vector3 startWorld,
        Vector3 endWorld,
        float duration,
        float startScale,
        float endScale,
        CancellationToken token)
    {
        KillTweens();

        TransferRect.position = startWorld;
        TransferRect.localRotation = Quaternion.identity;
        TransferRect.localScale = Vector3.one * Mathf.Max(0.01f, startScale);

        try
        {
            float safeDuration = Mathf.Max(0.01f, duration);
            _moveTween = TransferRect.DOMove(endWorld, safeDuration).SetEase(Ease.OutCubic);
            _scaleTween = TransferRect.DOScale(Vector3.one * Mathf.Max(0.01f, endScale), safeDuration)
                .SetEase(Ease.OutCubic);

            await _moveTween.AsUniTask(token);

            // AsUniTask 는 트윈 OnComplete 콜백(= TweenManager.Update 루프 안)에서
            // 동기 재개된다. 그대로 진행하면 아래 KillTweens 와 호출부(PlayDrawAnim 등)의
            // RefreshLayout 이 DOTween 활성 배열 순회 도중 트윈을 Kill/생성해 배열이 깨진다
            // (IndexOutOfRangeException 연쇄). 한 틱 양보해 루프 밖으로 탈출한다.
            await UniTask.Yield(PlayerLoopTiming.Update);
        }
        catch (OperationCanceledException)
        {
            // 전투 종료·씬 이탈로 취소 — 반환은 finally 에서 처리.
        }
        finally
        {
            if (this != null)
            {
                ReturnToPoolOrDestroy();
            }
        }
    }

    private void ReturnToPoolOrDestroy()
    {
        if (this == null) return;

        KillTweens();

        if (PoolManager.HasInstance && PoolManager.Instance.IsPooledInstance(gameObject))
        {
            PoolManager.Instance.Despawn(this);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void KillTweens()
    {
        _moveTween?.Kill();
        _scaleTween?.Kill();
        _fadeTween?.Kill();
        _moveTween = null;
        _scaleTween = null;
        _fadeTween = null;
    }

    private void OnDestroy()
    {
        KillTweens();
    }
}
