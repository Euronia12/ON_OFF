// =================================================================
// [스크립트 목적]  씬 로드/언로드 통합. Single/Additive + 페이드 + 진행도 콜백
//                 + 씬 생명주기(ISceneLifecycle) 흐름 제어 + 로딩 화면 최소 표시 시간
// [주요 변수]      - _fadeCanvasGroup       : 페이드용 CanvasGroup (선택, 없으면 UIManager 것 사용)
//                  - _defaultFadeDuration   : 페이드 인/아웃 기본 시간 (초)
//                  - _loadingScreenKey      : 로딩 화면 UI 키 (미지정 시 페이드만)
//                  - _minLoadingScreenTime  : 로딩 화면 최소 표시 시간 (초, 깜빡임 방지)
//                  - _isTransitioning       : 동시 전환 방지 락
// [의존 관계]      - ManagerBase<SceneFlowManager>, UniTask, UIManager(페이드/로딩화면), AsyncBridge
//                  - AudioManager(선택): RunWithFadeAsync 의 bgmKey 로 화면 전환과 BGM 크로스페이드 동기화
// [InitOrder]      80
// [개선]           1) Coroutine 버전을 UniTask 경로로 위임 → 생명주기·전환 락·예외 안전 단일 출처
//                  2) 로딩 화면 하드코딩 1초 대기 제거 → 최소 표시 시간 필드(빠르면 부족분만 대기)
// =================================================================
using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class SceneFlowManager : ManagerBase<SceneFlowManager>
{
    public override int InitOrder => 80;

    [Header("페이드 설정")]
    [Tooltip("화면 전환용 CanvasGroup (선택). 비워두면 UIManager가 Fade 레이어에 미리 만든 페이드를 자동 사용.\n" +
             "직접 디자인한 페이드를 쓰고 싶을 때만 연결")]
    [SerializeField] private CanvasGroup _fadeCanvasGroup;

    [Tooltip("페이드 인/아웃 기본 시간 (초). 값이 클수록 전환이 느긋해짐")]
    [Range(0f, 2f)]
    [SerializeField] private float _defaultFadeDuration = 0.6f;

    [Header("로딩 화면")]
    [Tooltip("로딩 화면 UI Addressable/Resource 키. 미지정 시 페이드만")]
    [SerializeField] private string _loadingScreenKey;

    [Tooltip("로딩 화면 최소 표시 시간 (초). 로드가 너무 빨라 화면이 깜빡 지나가는 것을 방지.\n" +
             "0이면 즉시 닫음 / 로드가 이 시간보다 길면 추가 대기 없음")]
    [Range(0f, 5f)]
    [SerializeField] private float _minLoadingScreenTime = 0.5f;

    private bool _isTransitioning;

    public bool IsTransitioning => _isTransitioning;
    public bool IsFadeFullyCovered
    {
        get
        {
            CanvasGroup fade = GetFadeGroup();
            return fade != null &&
                   fade.alpha == 1f &&
                   fade.blocksRaycasts;
        }
    }

    /// <summary>씬 로드 시작 (씬 이름)</summary>
    public event Action<string> OnSceneLoadStart;

    /// <summary>씬 로드 완료 (씬 이름)</summary>
    public event Action<string> OnSceneLoadComplete;

    // ─────────────────────────────────────────────
    // 씬 생명주기 (ISceneLifecycle)
    // ─────────────────────────────────────────────

    // 현재 씬의 생명주기 핸들러 (씬 컨트롤러가 등록). 단일 — 씬당 하나의 지휘자
    private ISceneLifecycle _currentLifecycle;

    /// <summary>
    /// 씬 컨트롤러가 자기 자신을 생명주기 핸들러로 등록.
    /// 보통 씬 컨트롤러의 Awake에서 호출.
    /// 새 씬의 컨트롤러가 등록하면 이전 핸들러는 자동 교체됨.
    /// </summary>
    public void RegisterLifecycle(ISceneLifecycle lifecycle)
    {
        _currentLifecycle = lifecycle;
    }

    /// <summary>생명주기 핸들러 해제 (씬 컨트롤러 OnDestroy에서 호출 권장).</summary>
    public void UnregisterLifecycle(ISceneLifecycle lifecycle)
    {
        if (_currentLifecycle == lifecycle)
            _currentLifecycle = null;
    }

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        // Inspector에 직접 연결된 페이드가 있으면 초기 상태만 정리.
        // 없으면 UIManager가 만든 페이드를 사용하므로 여기선 아무것도 안 함
        // (UIManager가 이미 alpha=0으로 초기화).
        if (_fadeCanvasGroup != null)
        {
            _fadeCanvasGroup.alpha = 0f;
            _fadeCanvasGroup.blocksRaycasts = false;
        }
        return UniTask.CompletedTask;
    }

    // ─────────────────────────────────────────────
    // Load (UniTask)
    // ─────────────────────────────────────────────

    /// <summary>씬 로드. 페이드 아웃 → 로드 → 페이드 인. 진행도 0~1 콜백.</summary>
    public async UniTask LoadSceneAsync(string sceneName,
        LoadSceneMode mode = LoadSceneMode.Single,
        IProgress<float> progress = null,
        bool useFade = true,
        CancellationToken token = default)
    {
        if (_isTransitioning)
        {
            GameLogger.LogWarning(ELogCategory.Scene, $"전환 중 중복 요청 무시: {sceneName}");
            return;
        }

        _isTransitioning = true;
        OnSceneLoadStart?.Invoke(sceneName);

        try
        {
            if (useFade) await FadeAsync(1f, _defaultFadeDuration, token);

            // ① 떠나는 씬 정리 (페이드 뒤, 로드 전) — 현재 등록된 핸들러
            var leaving = _currentLifecycle;
            if (leaving != null)
                await leaving.OnSceneExit(token);
            CleanupScenePools();

            var op = SceneManager.LoadSceneAsync(sceneName, mode);
            op.allowSceneActivation = false;

            // 0.9까지는 로드, 0.9 도달 시 활성화
            while (op.progress < 0.9f)
            {
                progress?.Report(op.progress);
                await UniTask.Yield(token);
            }

            progress?.Report(1f);
            op.allowSceneActivation = true;
            await op.ToUniTask(cancellationToken: token);

            // 씬 활성화 후 새 씬 컨트롤러가 RegisterLifecycle 할 때까지 대기.
            // 한 프레임만 기다리면 새 씬 UI 준비 전에 페이드 인이 시작될 수 있다.
            var entering = await WaitForEnteringLifecycleAsync(leaving, token);

            // 새 씬의 MainCamera를 다시 잡고 투명 오브젝트 정렬 설정을 적용한다.
            if (CameraManager.HasInstance)
                CameraManager.Instance.RefreshMainCamera();

            // ② 새 씬 진입 준비 (페이드 뒤 = 화면 가려진 상태) — 새로 등록된 핸들러
            if (entering != null)
                await entering.OnSceneEnter(token);

            if (useFade) await FadeAsync(0f, _defaultFadeDuration, token);

            // ③ 페이드 인 완료 후 (화면 보이는 상태)
            entering?.OnSceneReady();

            OnSceneLoadComplete?.Invoke(sceneName);
        }
        finally
        {
            if (useFade)
                SetFadeAlpha(0f);
            _isTransitioning = false;
        }
    }

    /// <summary>
    /// 로딩 화면을 표시하며 씬 전환. 페이드 → 로딩화면 → 로드(진행도) → (최소 표시 시간 보장) → 페이드.
    /// 로딩화면은 반드시 DontDestroyOnLoad UI 레이어에 떠야 함 (UIManager의 Top 레이어).
    /// </summary>
    public async UniTask LoadSceneWithScreenAsync(string sceneName,
        LoadSceneMode mode = LoadSceneMode.Single,
        CancellationToken token = default)
    {
        if (_isTransitioning)
        {
            GameLogger.LogWarning(ELogCategory.Scene, $"전환 중 중복 요청 무시: {sceneName}");
            return;
        }

        using var lifetimeCts = CreateLifetimeLinkedTokenSource(token);
        token = lifetimeCts.Token;
        token.ThrowIfCancellationRequested();

        _isTransitioning = true;
        OnSceneLoadStart?.Invoke(sceneName);

        UILoadingScreen loadingScreen = null;

        try
        {
            // 1. 페이드 아웃 (검게 가림)
            await FadeAsync(1f, _defaultFadeDuration, token);

            // 2. 로딩 화면 표시 (가려진 상태에서 — 키 있을 때만)
            //    표시 시작 시각 기록 → 최소 표시 시간 보장에 사용
            float screenShownAt = -1f;
            if (!string.IsNullOrEmpty(_loadingScreenKey) && UIManager.HasInstance)
            {
                loadingScreen = await UIManager.Instance.OpenAsync<UILoadingScreen>(_loadingScreenKey, token);
                if (loadingScreen != null)
                    screenShownAt = Time.realtimeSinceStartup; // 일시정지·timeScale 영향 없는 실시간 기준
            }

            // ① 떠나는 씬 정리
            var leaving = _currentLifecycle;
            if (leaving != null)
                await leaving.OnSceneExit(token);
            CleanupScenePools();

            // 3. 실제 로드 (진행도 → 로딩화면, 없으면 무시)
            var op = SceneManager.LoadSceneAsync(sceneName, mode);
            op.allowSceneActivation = false;

            while (op.progress < 0.9f)
            {
                loadingScreen?.SetProgress(op.progress);
                await UniTask.Yield(token);
            }

            loadingScreen?.SetProgress(1f);
            op.allowSceneActivation = true;
            await op.ToUniTask(cancellationToken: token);

            // 새 씬 컨트롤러가 RegisterLifecycle 할 때까지 대기.
            // 이 대기 없이 넘어가면 UIWindowDeckSelect 준비 전에 페이드 인이 시작될 수 있다.
            var entering = await WaitForEnteringLifecycleAsync(leaving, token);

            // 새 씬의 MainCamera를 다시 잡고 투명 오브젝트 정렬 설정을 적용한다.
            if (CameraManager.HasInstance)
                CameraManager.Instance.RefreshMainCamera();

            // ② 새 씬 진입 준비 (가려진 상태에서 내용 처리)
            if (entering != null)
                await entering.OnSceneEnter(token);

            // 4. 로딩 화면 최소 표시 시간 보장 (로드가 너무 빨라 깜빡 지나가는 것 방지)
            //    이미 최소 시간을 넘겼으면 추가 대기 없음.
            if (loadingScreen != null && screenShownAt >= 0f && _minLoadingScreenTime > 0f)
            {
                float elapsed = Time.realtimeSinceStartup - screenShownAt;
                float remain = _minLoadingScreenTime - elapsed;
                if (remain > 0f)
                    await UniTask.Delay(TimeSpan.FromSeconds(remain),
                        DelayType.UnscaledDeltaTime, cancellationToken: token);
            }

            // 5. 로딩 화면 닫기 (아직 페이드로 가려진 상태)
            if (loadingScreen != null)
                UIManager.Instance.Close(loadingScreen);

            // 6. 페이드 인 (걷음) — 모든 준비 끝난 뒤 한 번만
            await FadeAsync(0f, _defaultFadeDuration, token);

            // ③ 페이드 인 완료 후
            entering?.OnSceneReady();

            OnSceneLoadComplete?.Invoke(sceneName);
        }
        finally
        {
            if (loadingScreen != null && UIManager.HasInstance)
                UIManager.Instance.Close(loadingScreen);
            SetFadeAlpha(0f);
            _isTransitioning = false;
        }
    }

    public async UniTask UnloadSceneAsync(string sceneName, CancellationToken token = default)
    {
        var op = SceneManager.UnloadSceneAsync(sceneName);
        if (op == null)
        {
            GameLogger.LogWarning(ELogCategory.Scene, $"언로드 실패(미로드 씬?): {sceneName}");
            return;
        }
        await op.ToUniTask(cancellationToken: token);
    }

    private void CleanupScenePools()
    {
        if (PoolManager.HasInstance)
            PoolManager.Instance.ClearAll();
    }

    private async UniTask<ISceneLifecycle> WaitForEnteringLifecycleAsync(
        ISceneLifecycle leaving,
        CancellationToken token)
    {
        const int maxFrames = 120;
        for (int i = 0; i < maxFrames; i++)
        {
            ISceneLifecycle current = _currentLifecycle;
            if (current != null && current != leaving)
                return current;

            await UniTask.Yield(token);
        }

        GameLogger.LogWarning(ELogCategory.Scene,
            "새 씬 lifecycle 등록 대기 시간 초과 — OnSceneEnter 없이 페이드 인될 수 있습니다.");
        return _currentLifecycle != leaving ? _currentLifecycle : null;
    }

    /// <summary>
    /// 같은 씬 안에서 화면을 가린 채 비동기 전환 작업을 수행한다.
    /// 맵→전투처럼 씬 로드 없이 UI/게임플레이 구성을 교체할 때 사용한다.
    /// </summary>
    /// <param name="fadeDuration">페이드 인/아웃 시간(초). 음수면 기본값 사용.</param>
    /// <param name="bgmKey">
    /// 지정 시, 화면 전환과 함께 이 BGM 으로 크로스페이드한다(화면 전환이 BGM 핸드오프를 소유).
    /// 크로스페이드 길이를 전환 창(페이드아웃+페이드인 ≈ 2×fadeDuration)에 결속해, 화면이 다시
    /// 보일 때쯤 새 곡이 자리잡고 이전 곡 잔재가 안 남게 한다. (이전 곡을 빠르게 빼는 것은
    /// AudioManager 의 비대칭 크로스페이드가 담당) 같은 곡이 이미 흐르고 있으면 곡을 유지하고
    /// bgmVolume 으로만 페이드하므로, 맵/전투가 BGM 을 공유해도 재시작 없이 이어진다.
    /// </param>
    /// <param name="bgmVolume">bgmKey 를 재생할 볼륨(0~1). AudioKeys.BgmVolume 상수 사용 권장.</param>
    public async UniTask RunWithFadeAsync(
        System.Func<CancellationToken, UniTask> transition,
        CancellationToken token = default,
        float fadeDuration = -1f,
        string bgmKey = null,
        float bgmVolume = 1f,
        Color? fadeColor = null)
    {
        if (transition == null || _isTransitioning)
        {
            return;
        }

        // 음수면 기본 페이드 시간 사용. 호출부에서 전환별로 페이드 길이를 조절할 수 있다.
        float duration = fadeDuration >= 0f ? fadeDuration : _defaultFadeDuration;

        _isTransitioning = true;
        Image fadeGraphic = GetFadeGraphic();
        Color previousFadeColor = fadeGraphic != null ? fadeGraphic.color : Color.black;
        try
        {
            if (fadeColor.HasValue && fadeGraphic != null)
                fadeGraphic.color = fadeColor.Value;

            // 화면 전환 시작과 동시에 BGM 크로스페이드 킥 — 전환 창에 결속돼 잔재 없이 이어진다.
            if (!string.IsNullOrEmpty(bgmKey) && AudioManager.HasInstance)
                AudioManager.Instance.PlayBgmAsync(
                    bgmKey, crossfade: duration * 2f, volume: bgmVolume).Forget();

            await FadeAsync(1f, duration, token);
            await transition(token);
            await FadeAsync(0f, duration, token);
        }
        finally
        {
            SetFadeAlpha(0f);
            if (fadeGraphic != null)
                fadeGraphic.color = previousFadeColor;
            _isTransitioning = false;
        }
    }

    /// <summary>
    /// 로딩 화면 전환을 외부 취소와 SceneFlowManager 수명에 결속한다.
    /// Play Mode 종료/Bootstrap 파괴 후 비동기 전환이 계속되는 것을 방지한다.
    /// </summary>
    private CancellationTokenSource CreateLifetimeLinkedTokenSource(CancellationToken token)
        => CancellationTokenSource.CreateLinkedTokenSource(
            token,
            this.GetCancellationTokenOnDestroy());

    // ─────────────────────────────────────────────
    // Load (Coroutine)
    // ─────────────────────────────────────────────

    /// <summary>
    /// Coroutine 버전. 내부적으로 UniTask 경로(LoadSceneAsync)로 위임한다.
    /// → 씬 생명주기(OnSceneExit/Enter/Ready)·전환 락·예외 안전(finally)을
    ///   UniTask 버전과 단일 출처로 공유 (로직 중복·불일치 제거).
    /// ※ 기존 독립 구현은 생명주기 호출 누락 + 예외 시 _isTransitioning 영구 락 위험이 있어 폐기.
    /// </summary>
    public IEnumerator LoadSceneCoroutine(string sceneName,
        LoadSceneMode mode = LoadSceneMode.Single,
        Action<float> onProgress = null,
        Action onComplete = null,
        bool useFade = true)
    {
        // onProgress → IProgress<float> 변환 (DataManager 등과 동일 패턴)
        var progress = onProgress != null
            ? Progress.Create<float>(p => onProgress(p))
            : null;

        // UniTask 경로로 위임. 완료(성공) 시 onComplete 호출.
        return AsyncBridge.ToCoroutine(
            LoadSceneAsync(sceneName, mode, progress, useFade),
            onComplete);
    }

    // ─────────────────────────────────────────────
    // 페이드
    // ─────────────────────────────────────────────

    /// <summary>
    /// 실제 사용할 페이드 그룹 반환.
    /// Inspector에 직접 연결된 게 있으면 그걸, 없으면 UIManager가 미리 만든 페이드를 사용.
    /// </summary>
    private CanvasGroup GetFadeGroup()
    {
        if (_fadeCanvasGroup != null) return _fadeCanvasGroup;
        if (UIManager.HasInstance) return UIManager.Instance.FadeGroup;
        return null;
    }

    /// <summary>페이드 그룹과 같은 오브젝트에 배치된 전체 화면 이미지를 반환한다.</summary>
    private Image GetFadeGraphic()
    {
        CanvasGroup fade = GetFadeGroup();
        if (fade == null) return null;

        Image image = fade.GetComponent<Image>();
        return image != null ? image : fade.GetComponentInChildren<Image>(includeInactive: true);
    }

    private async UniTask FadeAsync(float targetAlpha, float duration, CancellationToken token)
    {
        var fade = GetFadeGroup();
        if (fade == null) return;

        fade.blocksRaycasts = true;
        float start = fade.alpha;
        float elapsed = 0f;

        // duration이 0 이하이면 즉시 적용 (0 나눗셈 방지)
        if (duration > 0f)
        {
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime; // 일시정지 중에도 동작
                fade.alpha = Mathf.Lerp(start, targetAlpha, elapsed / duration);
                await UniTask.Yield(token);
            }
        }

        fade.alpha = targetAlpha;
        fade.blocksRaycasts = targetAlpha > 0.01f;
    }

    /// <summary>
    /// Coroutine 환경에서 직접 페이드만 쓰고 싶을 때용 (씬 전환과 무관한 단발 페이드).
    /// 씬 전환은 LoadSceneCoroutine을 쓰면 내부에서 페이드까지 처리됨.
    /// </summary>
    public IEnumerator FadeCoroutine(float targetAlpha, float duration)
    {
        var fade = GetFadeGroup();
        if (fade == null) yield break;

        fade.blocksRaycasts = true;
        float start = fade.alpha;
        float elapsed = 0f;

        if (duration > 0f)
        {
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                fade.alpha = Mathf.Lerp(start, targetAlpha, elapsed / duration);
                yield return null;
            }
        }

        fade.alpha = targetAlpha;
        fade.blocksRaycasts = targetAlpha > 0.01f;
    }

    public void SetFadeAlpha(float alpha)
    {
        var fade = GetFadeGroup();
        if (fade == null) return;
        fade.alpha = Mathf.Clamp01(alpha);
        fade.blocksRaycasts = alpha > 0.01f;
    }
}
