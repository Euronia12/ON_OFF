// =================================================================
// [스크립트 목적]  UI 생명주기 통합 관리. Screen UI 캐싱 + 팝업 스택 + World UI 배치
// [주요 변수]      - _screenLayerRoots : Screen 공간 레이어별 부모 (영속 UIRoot)
//                  - _activeWorldRoot  : 현재 활성 World 도화지 (씬 컨트롤러가 등록)
//                  - _openUIs          : 열린 UI 추적 (타입 → 인스턴스)
//                  - _cachedUIs        : 생성 보관 (Screen 전용 / World는 씬종속이라 미캐싱)
//                  - _popupStack       : 팝업 스택 (ESC = 최상위 닫기)
//                  - _prefabCache      : 로드된 UI prefab 캐시
// [의존 관계]      - ManagerBase<UIManager>, ResourceManager, InputManager,
//                    CameraManager, SceneFlowManager, UIWorldRoot
// [개선]           - Screen Canvas를 Camera 모드로 강제 (Overlay 금지)
//                  - 씬 전환 시 메인 카메라 자동 재연결 (CameraManager 연동) → stale 카메라 해결
//                  - World UI: UIWorldRoot(도화지)에 배치. 활성 루트 참조로 인자 생략 가능
//                  - 매직 스트링 제거 (Constants.UI 사용)
// [InitOrder]      90
// =================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public class UIManager : ManagerBase<UIManager>
{
    private const string DefaultScreenSortingLayerName = "UI";

    public override int InitOrder => 90;

    [Header("Screen UI 루트 설정")]
    [Tooltip("영속 UIRoot 프리팹 (Screen Canvas + 레이어 포함). 1순위.\n" +
             "에디터에서 따로 편집 가능. Instantiate 후 DontDestroyOnLoad 적용됨")]
    [SerializeField] private GameObject _uiRootPrefab;

    [Tooltip("씬에 직접 배치한 Screen Canvas (2순위). 프리팹·이것 둘 다 없으면 코드로 자동 생성")]
    [SerializeField] private Canvas _rootCanvas;

    [Header("Screen Canvas - Camera 모드 설정")]
    [Tooltip("Screen Canvas 카메라 평면 거리 (m). 카메라 near/far 사이여야 보임.\n" +
             "월드 오브젝트가 UI를 가리지 않게 충분히 가깝게(작게) 두는 게 일반적")]
    [Range(0.1f, 100f)]
    [SerializeField] private float _screenPlaneDistance = 1f;

    [Tooltip("Screen Canvas의 Sorting Layer 이름. 기본 'UI'")]
    [SerializeField] private string _screenSortingLayer = DefaultScreenSortingLayerName;

    [Tooltip("Screen Canvas Sorting Order. 값이 클수록 위")]
    [SerializeField] private int _screenSortingOrder = 0;

    // ── Screen 공간 (영속) ──
    private readonly Dictionary<EUILayer, Transform> _screenLayerRoots = new(8);

    // ── World 공간 (씬 종속) — 현재 활성 도화지 ──
    // UIWorldRoot가 OnEnable/OnDisable에서 등록/해제. 씬당 1개가 일반적이나
    // 멀티(분할화면)면 OpenWorldAsync(root, ...) 오버로드로 명시.
    private UIWorldRoot _activeWorldRoot;

    // 타입별 열린 UI (단일 인스턴스 UI용) — IsOpen 판정·팝업 스택용
    private readonly Dictionary<Type, UIBase> _openUIs = new(16);
    // 생성 보관 캐시 (Screen 전용 — World는 씬종속이라 미캐싱)
    private readonly Dictionary<Type, UIBase> _cachedUIs = new(16);
    // 팝업 스택 (ESC 처리)
    private readonly List<UIBase> _popupStack = new(8);
    // UI prefab 캐시 (키 → UIBase prefab). GetComponent 제거
    private readonly Dictionary<string, UIBase> _prefabCache = new(32);
    // 키 기반 일회성 UI. 서로 다른 Event 프리팹이 같은 컴포넌트 타입을 써도 타입 캐시와 충돌하지 않는다.
    private readonly HashSet<UIBase> _transientUIs = new();
    // 첫 사용 때 OpenTransientAsync가 소비할 키 기반 비활성 인스턴스.
    private readonly Dictionary<string, UIBase> _preloadedTransientUIs = new(4);
    // 같은 키의 transient Open과 preload가 경합할 때 잔여 preload 생성을 막는다.
    private readonly Dictionary<UIBase, string> _transientPrefabKeys = new();
    // 캐시 전체 파괴 중이던 preload가 뒤늦게 인스턴스를 남기지 않도록 하는 세대 값.
    private int _cacheGeneration;
    // 활성 부모 아래에서 Instantiate할 때 발생하는 OnEnable 왕복을 막는 비활성 생성 루트.
    private Transform _inactivePreloadRoot;

    // 페이드용 CanvasGroup (Screen Fade 레이어에 미리 생성, alpha=0으로 대기)
    private CanvasGroup _fadeGroup;
    private Image _brightnessOverlay;

    public Canvas RootCanvas => _rootCanvas;
    public int OpenPopupCount => _popupStack.Count;
    public CanvasGroup FadeGroup => _fadeGroup;

    /// <summary>
    /// 닫을 팝업이 없는 상태에서 ESC/백버튼이 눌렸을 때 발행.
    /// 팝업 닫기가 최우선이며, 그것으로 소비되지 않은 ESC 만 위임된다.
    /// MainController 가 구독해 런 일시정지 메뉴를 토글한다(씬/런 책임은 UIManager 밖).
    /// </summary>
    public event System.Action OnEscapeUnhandled;

    /// <summary>현재 활성 World 도화지 (없으면 null). 풀링 HP바 부모 조회 등에 사용.</summary>
    public UIWorldRoot ActiveWorldRoot => _activeWorldRoot;

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        SetupRootCanvas();
        SetupScreenLayers();
        SetupFade();
        SetupBrightnessOverlay();

        if (InputManager.HasInstance)
            InputManager.Instance.OnEscapePressed += OnEscapePressed;
        if (SettingsManager.HasInstance)
        {
            SettingsManager.Instance.OnSettingsChanged += OnSettingsChanged;
            ApplyBrightness(SettingsManager.Instance.Current?.Brightness ?? 1f);
        }

        // 씬 전환 완료 시: 메인 카메라 재탐색 → Screen Canvas 카메라 재연결
        if (SceneFlowManager.HasInstance)
            SceneFlowManager.Instance.OnSceneLoadComplete += OnSceneLoadComplete;

        // CameraManager가 메인 카메라 교체를 알리면 즉시 Canvas 재연결
        if (CameraManager.HasInstance)
            CameraManager.Instance.OnMainCameraChanged += OnMainCameraChanged;

        ConnectScreenCanvasCamera(); // 초기 카메라 연결

        return UniTask.CompletedTask;
    }

    protected override UniTask OnShutdownInternalAsync()
    {
        if (InputManager.HasInstance)
            InputManager.Instance.OnEscapePressed -= OnEscapePressed;
        if (SettingsManager.HasInstance)
            SettingsManager.Instance.OnSettingsChanged -= OnSettingsChanged;

        if (SceneFlowManager.HasInstance)
            SceneFlowManager.Instance.OnSceneLoadComplete -= OnSceneLoadComplete;

        if (CameraManager.HasInstance)
            CameraManager.Instance.OnMainCameraChanged -= OnMainCameraChanged;

        OnEscapeUnhandled = null;

        DestroyAllCached();
        return UniTask.CompletedTask;
    }

    // ─────────────────────────────────────────────
    // Screen 루트 / 레이어 구성
    // ─────────────────────────────────────────────

    private void SetupRootCanvas()
    {
        // 1순위: 영속 UIRoot 프리팹
        if (_uiRootPrefab != null)
        {
            GameObject instance = Instantiate(_uiRootPrefab);
            instance.name = Constants.UI.UI_ROOT_NAME;
            DontDestroyOnLoad(instance);

            _rootCanvas = instance.GetComponentInChildren<Canvas>(includeInactive: true);
            if (_rootCanvas == null)
            {
                GameLogger.LogError(ELogCategory.UI,
                    "UIRoot 프리팹에 Canvas가 없습니다. 자동 생성으로 폴백합니다.");
            }
            else
            {
                ApplyScreenCameraMode(_rootCanvas);
                ApplyCanvasScalerMatch(_rootCanvas);
                return;
            }
        }

        // 2순위: 씬에 직접 배치한 Canvas
        if (_rootCanvas != null)
        {
            if (_rootCanvas.transform.parent == null)
                DontDestroyOnLoad(_rootCanvas.gameObject);
            ApplyScreenCameraMode(_rootCanvas);
            ApplyCanvasScalerMatch(_rootCanvas);
            return;
        }

        // 3순위: 코드 자동 생성 (폴백)
        var go = new GameObject(Constants.UI.UI_ROOT_NAME);
        DontDestroyOnLoad(go);
        _rootCanvas = go.AddComponent<Canvas>();
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        go.AddComponent<GraphicRaycaster>();
        ApplyScreenCameraMode(_rootCanvas);
    }

    public void SetUIRootCamera()
    {
        ApplyScreenCameraMode(_rootCanvas);
        ConnectScreenCanvasCamera();
    }

    /// <summary>
    /// Screen Canvas를 Screen Space - Camera 모드로 강제 (Overlay 금지 정책).
    /// 카메라 자체는 ConnectScreenCanvasCamera에서 런타임에 연결.
    /// </summary>
    private void ApplyScreenCameraMode(Canvas canvas)
    {
        if (canvas == null) return;

        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = Camera.main;
        canvas.planeDistance = _screenPlaneDistance;
        ApplyScreenCanvasSorting(canvas);
    }

    private static void ApplyCanvasScalerMatch(Canvas canvas)
    {
        if (canvas == null) return;

        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler == null)
            scaler = canvas.gameObject.AddComponent<CanvasScaler>();

        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
    }

    private void SetupScreenLayers()
    {
        BuildLayers(_rootCanvas.transform, _screenLayerRoots, Constants.UI.LAYER_PREFIX);
    }

    /// <summary>
    /// 부모 아래 EUILayer 순서대로 레이어 Transform 구성.
    /// "{prefix}{layer}" 자식이 있으면 재사용(에디터 존중), 없으면 생성.
    /// </summary>
    private void BuildLayers(Transform parent, Dictionary<EUILayer, Transform> target, string prefix)
    {
        target.Clear();

        foreach (EUILayer layer in Enum.GetValues(typeof(EUILayer)))
        {
            string layerName = $"{prefix}{layer}";
            var existing = parent.Find(layerName);

            if (existing != null)
            {
                target[layer] = existing;
                continue;
            }

            var go = new GameObject(layerName);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, worldPositionStays: false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            target[layer] = rt;
        }

        int sibling = 0;
        foreach (EUILayer layer in Enum.GetValues(typeof(EUILayer)))
            target[layer].SetSiblingIndex(sibling++);
    }

    // ─────────────────────────────────────────────
    // 카메라 연결 (Screen Space - Camera)
    // ─────────────────────────────────────────────

    /// <summary>씬 전환 후 카메라 재연결용 (외부 직접 지정). Camera 모드 전제.</summary>
    public void SetCanvasCamera(Camera camera)
    {
        if (_rootCanvas == null || camera == null) return;
        if (_rootCanvas.renderMode != RenderMode.ScreenSpaceCamera) return;

        _rootCanvas.worldCamera = camera;
        _rootCanvas.planeDistance = _screenPlaneDistance;
        ApplyScreenCanvasSorting(_rootCanvas);
    }

    private void ApplyScreenCanvasSorting(Canvas canvas)
    {
        if (canvas == null) return;

        string sortingLayer = string.IsNullOrWhiteSpace(_screenSortingLayer) || _screenSortingLayer == "Default"
            ? DefaultScreenSortingLayerName
            : _screenSortingLayer;

        canvas.overrideSorting = true;
        canvas.sortingLayerName = sortingLayer;
        canvas.sortingOrder = _screenSortingOrder;
    }

    /// <summary>현재 씬의 메인 카메라를 찾아 Screen Canvas에 연결.</summary>
    private void ConnectScreenCanvasCamera()
    {
        var cam = GetMainCamera();
        if (cam == null)
        {
            GameLogger.LogWarning(ELogCategory.UI,
                "Screen Canvas 카메라 연결 실패 — 현재 씬에 카메라 없음");
            return;
        }
        SetCanvasCamera(cam);
    }

    private Camera GetMainCamera()
    {
        if (CameraManager.HasInstance && CameraManager.Instance.MainCamera != null)
            return CameraManager.Instance.MainCamera;
        return Camera.main;
    }

    // 씬 로드 완료: 카메라 재탐색 → Canvas 재연결
    private void OnSceneLoadComplete(string sceneName)
    {
        // CameraManager가 stale 카메라를 새 씬 카메라로 갱신 (내부에서 OnMainCameraChanged 발행)
        if (CameraManager.HasInstance)
            CameraManager.Instance.RefreshMainCamera();
        else
            ConnectScreenCanvasCamera(); // CameraManager 없으면 직접 연결

        // 이전 씬 World 도화지(UIWorldRoot)는 씬과 함께 파괴되고, OnDisable의 ClearActiveWorldRoot가
        // _activeWorldRoot를 해제한다. 설령 참조가 남아도 Unity의 == 오버로드가 파괴된 객체를
        // null로 평가하므로 ActiveWorldRoot getter·사용처에서 안전하게 걸러진다(별도 정리 불필요).
    }

    private void OnMainCameraChanged(Camera camera)
    {
        SetCanvasCamera(camera);

        // 활성 World 도화지의 이벤트 카메라도 함께 갱신
        if (_activeWorldRoot != null)
            _activeWorldRoot.SetEventCamera(camera);
    }

    // ─────────────────────────────────────────────
    // 페이드 (Screen Fade 레이어)
    // ─────────────────────────────────────────────

    private void SetupFade()
    {
        if (!_screenLayerRoots.TryGetValue(EUILayer.Fade, out var fadeLayer))
        {
            GameLogger.LogWarning(ELogCategory.UI, "Fade 레이어 없음 — 페이드 생성 스킵");
            return;
        }

        var existing = fadeLayer.Find(Constants.UI.FADE_PANEL_NAME);
        if (existing != null && existing.TryGetComponent<CanvasGroup>(out var existingGroup))
        {
            _fadeGroup = existingGroup;
        }
        else
        {
            var go = new GameObject(Constants.UI.FADE_PANEL_NAME);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(fadeLayer, worldPositionStays: false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;

            var image = go.AddComponent<Image>();
            image.color = Color.black;

            _fadeGroup = go.AddComponent<CanvasGroup>();
        }

        _fadeGroup.alpha = 0f;
        _fadeGroup.blocksRaycasts = false;
    }

    private void SetupBrightnessOverlay()
    {
        if (!_screenLayerRoots.TryGetValue(EUILayer.Top, out var topLayer))
            return;

        const string overlayName = "BrightnessOverlay";
        var existing = topLayer.Find(overlayName);
        if (existing != null && existing.TryGetComponent(out Image existingImage))
        {
            _brightnessOverlay = existingImage;
        }
        else
        {
            var go = new GameObject(overlayName);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(topLayer, worldPositionStays: false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;

            _brightnessOverlay = go.AddComponent<Image>();
        }

        _brightnessOverlay.raycastTarget = false;
        _brightnessOverlay.transform.SetAsLastSibling();
        ApplyBrightness(1f);
    }

    private void OnSettingsChanged(GameSettings settings)
    {
        if (settings != null)
            ApplyBrightness(settings.Brightness);
    }

    private void ApplyBrightness(float brightness)
    {
        if (_brightnessOverlay == null) return;

        brightness = Mathf.Clamp(brightness, 0.1f, 2f);
        if (brightness < 1f)
        {
            _brightnessOverlay.color = new Color(0f, 0f, 0f, 1f - brightness);
        }
        else
        {
            _brightnessOverlay.color = new Color(1f, 1f, 1f, Mathf.Clamp01((brightness - 1f) * 0.35f));
        }

        _brightnessOverlay.enabled = !Mathf.Approximately(brightness, 1f);
    }

    // ─────────────────────────────────────────────
    // World 도화지 등록 (UIWorldRoot가 호출)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 현재 활성 World 도화지 지정. UIWorldRoot가 OnEnable에서 자동 호출하거나
    /// 씬 컨트롤러가 멀티 도화지 중 하나를 명시적으로 지정할 때 사용.
    /// </summary>
    public void SetActiveWorldRoot(UIWorldRoot root)
    {
        _activeWorldRoot = root;
    }

    /// <summary>활성 World 도화지 해제. 지정한 root가 현재 활성일 때만 해제(오염 방지).</summary>
    public void ClearActiveWorldRoot(UIWorldRoot root)
    {
        if (_activeWorldRoot == root)
            _activeWorldRoot = null;
    }

    // ─────────────────────────────────────────────
    // Open — Screen UI (비동기 로드)
    // ─────────────────────────────────────────────

    /// <summary>Screen UI 열기 (키 자동 = 타입 이름).</summary>
    public UniTask<T> OpenAsync<T>(CancellationToken token = default) where T : UIBase
        => OpenAsync<T>(typeof(T).Name, token);

    /// <summary>
    /// Screen UI를 로드·생성해 비활성 캐시에 넣는다.
    /// UIBase의 Init/Setup/OnOpen/OnClose는 호출하지 않고 실제 Open 때 기존 흐름으로 실행한다.
    /// </summary>
    public async UniTask PreloadAsync<T>(CancellationToken token = default) where T : UIBase
    {
        int generation = _cacheGeneration;
        Type type = typeof(T);
        if (_openUIs.TryGetValue(type, out UIBase open) && open != null) return;
        if (_cachedUIs.TryGetValue(type, out UIBase cached) && cached != null) return;

        UIBase prefab = await GetOrLoadPrefab(type.Name, token);
        if (prefab == null) return;

        // Addressables 대기 중 실제 Open 또는 다른 Preload가 먼저 끝났을 수 있다.
        if (generation != _cacheGeneration) return;
        if (_openUIs.TryGetValue(type, out open) && open != null) return;
        if (_cachedUIs.TryGetValue(type, out cached) && cached != null) return;
        token.ThrowIfCancellationRequested();

        if (prefab is not T typedPrefab)
        {
            GameLogger.LogError(ELogCategory.UI,
                $"UI preload 타입 불일치: {type.Name} (실제 {prefab.GetType().Name})");
            return;
        }

        if (typedPrefab.Space != EUISpace.Screen)
        {
            GameLogger.LogWarning(ELogCategory.UI,
                $"Screen UI preload 대상이 아님: {type.Name}/{typedPrefab.Space}");
            return;
        }

        Transform layerRoot = GetScreenLayerRoot(typedPrefab.Layer);
        if (layerRoot == null)
        {
            GameLogger.LogError(ELogCategory.UI,
                $"UI preload 레이어 없음: {type.Name}/{typedPrefab.Layer}");
            return;
        }

        Transform preloadRoot = GetInactivePreloadRoot();
        if (preloadRoot == null) return;

        T ui = UnityEngine.Object.Instantiate(typedPrefab, preloadRoot);
        ui.gameObject.SetActive(false);
        ui.transform.SetParent(layerRoot, worldPositionStays: false);
        _cachedUIs[type] = ui;
    }

    /// <summary>
    /// 키 기반 일회성 Screen UI 한 개를 비활성 상태로 준비한다.
    /// 첫 OpenTransientAsync가 인스턴스를 소비하고, 닫을 때는 기존 정책대로 파괴한다.
    /// </summary>
    public async UniTask PreloadTransientAsync(
        string prefabKey,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(prefabKey)) return;
        int generation = _cacheGeneration;
        if (HasOpenTransient(prefabKey)) return;
        if (_preloadedTransientUIs.TryGetValue(prefabKey, out UIBase existing) &&
            existing != null)
            return;

        UIBase prefab = await GetOrLoadPrefab(prefabKey, token);
        if (prefab == null) return;

        if (generation != _cacheGeneration) return;
        if (HasOpenTransient(prefabKey)) return;
        if (_preloadedTransientUIs.TryGetValue(prefabKey, out existing) &&
            existing != null)
            return;
        token.ThrowIfCancellationRequested();

        if (prefab.Space != EUISpace.Screen)
        {
            GameLogger.LogWarning(ELogCategory.UI,
                $"Screen transient preload 대상이 아님: {prefabKey}/{prefab.Space}");
            return;
        }

        Transform layerRoot = GetScreenLayerRoot(prefab.Layer);
        if (layerRoot == null)
        {
            GameLogger.LogError(ELogCategory.UI,
                $"Transient preload 레이어 없음: {prefabKey}/{prefab.Layer}");
            return;
        }

        Transform preloadRoot = GetInactivePreloadRoot();
        if (preloadRoot == null) return;

        UIBase ui = UnityEngine.Object.Instantiate(prefab, preloadRoot);
        ui.gameObject.SetActive(false);
        ui.transform.SetParent(layerRoot, worldPositionStays: false);
        _preloadedTransientUIs[prefabKey] = ui;
    }

    /// <summary>
    /// Screen UI 열기. prefab 로드 → Instantiate(또는 캐시 재사용) → 레이어 배치 → OnOpen.
    /// 이미 열린 단일 UI면 기존 인스턴스 반환.
    /// </summary>
    public async UniTask<T> OpenAsync<T>(string prefabKey, CancellationToken token = default)
        where T : UIBase
    {
        if (TryReuseOpen<T>(out var reused)) return reused;
        if (TryReuseCached<T>(out var cached)) return cached;

        var prefab = await GetOrLoadPrefab(prefabKey, token);
        if (prefab == null)
        {
            GameLogger.LogError(ELogCategory.UI, $"UI prefab 로드 실패: {prefabKey}");
            return null;
        }

        // await(로드) 사이에 같은 UI를 여는 다른 호출이 먼저 끝냈을 수 있으므로 재확인.
        // 이 재확인이 없으면 동시 호출 시 둘 다 Instantiate되어 UI가 중복 생성된다.
        if (TryReuseOpen<T>(out reused)) return reused;
        if (TryReuseCached<T>(out cached)) return cached;

        if (prefab is not T typedPrefab)
        {
            GameLogger.LogError(ELogCategory.UI,
                $"prefab 타입 불일치: {prefabKey} (요청 {typeof(T).Name}, 실제 {prefab.GetType().Name})");
            return null;
        }

        return OpenInternal(typedPrefab, GetScreenLayerRoot(typedPrefab.Layer));
    }

    /// <summary>키 기반 일회성 UI 열기. 닫을 때 캐시하지 않고 즉시 파괴한다.</summary>
    public async UniTask<UIBase> OpenTransientAsync(string prefabKey, CancellationToken token = default)
    {
        UIBase prefab = await GetOrLoadPrefab(prefabKey, token);
        if (prefab == null)
        {
            GameLogger.LogError(ELogCategory.UI, $"일회성 UI prefab 로드 실패: {prefabKey}");
            return null;
        }

        Transform layerRoot = prefab.Space == EUISpace.Screen
            ? GetScreenLayerRoot(prefab.Layer)
            : _activeWorldRoot?.GetLayer(prefab.Layer);
        if (layerRoot == null)
        {
            GameLogger.LogError(ELogCategory.UI, $"일회성 UI 레이어 없음: {prefabKey}/{prefab.Layer}");
            return null;
        }

        UIBase ui;
        if (_preloadedTransientUIs.TryGetValue(prefabKey, out UIBase preloaded) &&
            preloaded != null)
        {
            _preloadedTransientUIs.Remove(prefabKey);
            ui = preloaded;
            ui.gameObject.SetActive(true);
            ui.transform.SetAsLastSibling();
        }
        else
        {
            _preloadedTransientUIs.Remove(prefabKey);
            ui = UnityEngine.Object.Instantiate(prefab, layerRoot);
        }

        ui.HandleOpen();
        _transientUIs.Add(ui);
        _transientPrefabKeys[ui] = prefabKey;
        if (ui.CloseOnEscape)
            PushPopup(ui);
        return ui;
    }

    /// <summary>이미 로드된 prefab으로 Screen UI 열기 (동기).</summary>
    public T Open<T>(T prefab) where T : UIBase
    {
        if (TryReuseOpen<T>(out var reused)) return reused;
        if (TryReuseCached<T>(out var cached)) return cached;
        if (prefab == null) return null;

        return OpenInternal(prefab, GetScreenLayerRoot(prefab.Layer));
    }

    // ─────────────────────────────────────────────
    // Open — World UI (도화지에 배치)
    // ─────────────────────────────────────────────

    /// <summary>
    /// World UI 열기 (활성 도화지 사용 + 키 자동). 가장 흔한 호출.
    /// 사전에 UIWorldRoot가 SetActiveWorldRoot로 등록돼 있어야 함.
    /// </summary>
    public UniTask<T> OpenWorldAsync<T>(CancellationToken token = default) where T : UIBase
        => OpenWorldAsync<T>(typeof(T).Name, token);

    /// <summary>World UI 열기 (활성 도화지 사용 + 키 지정).</summary>
    public UniTask<T> OpenWorldAsync<T>(string prefabKey, CancellationToken token = default)
        where T : UIBase
        => OpenWorldAsync<T>(_activeWorldRoot, prefabKey, token);

    /// <summary>
    /// World UI 열기 (도화지 명시). 분할화면 등 멀티 도화지 환경용.
    /// root가 null이면 경고 후 null 반환 (씬 컨트롤러가 도화지 생성 안 한 상태).
    /// </summary>
    public async UniTask<T> OpenWorldAsync<T>(UIWorldRoot root, string prefabKey,
        CancellationToken token = default) where T : UIBase
    {
        if (root == null)
        {
            GameLogger.LogWarning(ELogCategory.UI,
                $"World UI Open 실패: 활성 도화지(UIWorldRoot) 없음. 키={prefabKey}. " +
                "씬 컨트롤러가 UIWorldRoot를 생성·등록했는지 확인");
            return null;
        }

        if (TryReuseOpen<T>(out var reused)) return reused;

        var prefab = await GetOrLoadPrefab(prefabKey, token);
        if (prefab == null)
        {
            GameLogger.LogError(ELogCategory.UI, $"World UI prefab 로드 실패: {prefabKey}");
            return null;
        }

        // await(로드) 사이에 같은 UI를 연 다른 호출이 먼저 끝냈을 수 있으므로 재확인 (중복 생성 방지)
        if (TryReuseOpen<T>(out reused)) return reused;

        if (prefab is not T typedPrefab)
        {
            GameLogger.LogError(ELogCategory.UI,
                $"prefab 타입 불일치: {prefabKey} (요청 {typeof(T).Name}, 실제 {prefab.GetType().Name})");
            return null;
        }

        var layerRoot = root.GetLayer(typedPrefab.Layer);
        if (layerRoot == null)
        {
            GameLogger.LogError(ELogCategory.UI,
                $"도화지에 World 레이어 없음: {typedPrefab.Layer}");
            return null;
        }

        // World UI는 캐싱하지 않음 (씬종속) → cacheable=false
        return OpenInternal(typedPrefab, layerRoot, cacheable: false);
    }

    // ─────────────────────────────────────────────
    // Open 공통 내부 처리
    // ─────────────────────────────────────────────

    // 이미 열린 단일 UI 재사용 시도
    private bool TryReuseOpen<T>(out T result) where T : UIBase
    {
        if (_openUIs.TryGetValue(typeof(T), out var existing) && existing != null)
        {
            // 같은 레이어 안에서는 sibling 순서가 곧 그리기 순서다.
            // 재사용 UI 를 맨 뒤로 보내 "나중에 연 UI 가 위" 를 보장한다.
            // (레이어별 Canvas 가 분리돼 있어 리빌드는 해당 레이어로 격리된다)
            existing.transform.SetAsLastSibling();
            existing.OnFocus();
            result = (T)existing;
            return true;
        }
        result = null;
        return false;
    }

    // 캐시된(비활성) Screen UI 재활성화 시도
    private bool TryReuseCached<T>(out T result) where T : UIBase
    {
        if (_cachedUIs.TryGetValue(typeof(T), out var cached) && cached != null)
        {
            cached.gameObject.SetActive(true);
            // 캐시된 UI 는 이전 sibling 위치를 그대로 갖고 있다 → 맨 뒤로 보내야
            // 먼저 생성됐던 UI 가 나중에 열린 UI 뒤로 숨지 않는다.
            cached.transform.SetAsLastSibling();
            cached.HandleOpen();

            _openUIs[typeof(T)] = cached;
            if (cached.CloseOnEscape)
                PushPopup(cached);

            result = (T)cached;
            return true;
        }
        result = null;
        return false;
    }

    // 신규 Instantiate → 배치 → 생명주기 → 추적/캐시 등록
    private T OpenInternal<T>(T prefab, Transform layerRoot, bool cacheable = true) where T : UIBase
    {
        if (prefab == null || layerRoot == null) return null;

        // Instantiate(prefab, layerRoot)가 이미 layerRoot 자식으로 생성하므로 SetParent 불필요
        T ui = UnityEngine.Object.Instantiate(prefab, layerRoot);
        ui.HandleOpen();

        _openUIs[typeof(T)] = ui;

        // Screen UI만 캐시 (재오픈 GC 0). World UI는 씬종속이라 캐시 안 함.
        if (cacheable && ui.Space == EUISpace.Screen)
            _cachedUIs[typeof(T)] = ui;

        if (ui.CloseOnEscape)
            PushPopup(ui);

        return ui;
    }

    private Transform GetScreenLayerRoot(EUILayer layer)
        => _screenLayerRoots.TryGetValue(layer, out var root) ? root : null;

    // ─────────────────────────────────────────────
    // Close
    // ─────────────────────────────────────────────

    public void Close<T>() where T : UIBase
    {
        if (_openUIs.TryGetValue(typeof(T), out var ui) && ui != null)
            Close(ui);
    }

    /// <summary>
    /// UI 닫기. HandleClose()가 OnClose → SetActive(false)까지 수행.
    /// World UI는 캐시 안 하므로 비활성된 직후 파괴(씬종속·일회성).
    /// </summary>
    public void Close(UIBase ui)
    {
        if (ui == null) return;

        var type = ui.GetType();
        if (_openUIs.TryGetValue(type, out UIBase tracked) && tracked == ui)
            _openUIs.Remove(type);
        bool transient = _transientUIs.Remove(ui);
        if (transient)
            _transientPrefabKeys.Remove(ui);

        if (ui.CloseOnEscape)
            RemovePopup(ui);

        // HandleClose 내부에서 OnClose 후 SetActive(false)까지 처리됨
        ui.HandleClose();

        // World UI는 캐시 대상이 아니므로 비활성 직후 파괴
        if (transient || ui.Space == EUISpace.World)
            UnityEngine.Object.Destroy(ui.gameObject);
    }

    /// <summary>열린 UI 전부 닫기. Screen은 비활성, World는 파괴.</summary>
    public void CloseAll()
    {
        var toClose = new List<UIBase>(_openUIs.Values);
        toClose.AddRange(_transientUIs);
        foreach (var ui in toClose)
            if (ui != null) Close(ui);

        _openUIs.Clear();
        _transientUIs.Clear();
        _popupStack.Clear();
    }

    /// <summary>캐시된 Screen UI 실제 파괴 (메모리 해제). 씬 전환 시 호출 권장.</summary>
    public void DestroyAllCached()
    {
        _cacheGeneration++;
        CloseAll();

        foreach (var ui in _cachedUIs.Values)
            if (ui != null) UnityEngine.Object.Destroy(ui.gameObject);

        foreach (var ui in _preloadedTransientUIs.Values)
            if (ui != null) UnityEngine.Object.Destroy(ui.gameObject);

        _cachedUIs.Clear();
        _preloadedTransientUIs.Clear();
        _transientPrefabKeys.Clear();
        _prefabCache.Clear();
    }

    /// <summary>특정 UI만 캐시에서 제거 + 파괴.</summary>
    public void DestroyCached<T>() where T : UIBase
    {
        var type = typeof(T);
        if (_openUIs.TryGetValue(type, out var open) && open != null)
            Close(open);

        if (_cachedUIs.TryGetValue(type, out var cached) && cached != null)
        {
            UnityEngine.Object.Destroy(cached.gameObject);
            _cachedUIs.Remove(type);
        }
    }

    // ─────────────────────────────────────────────
    // 팝업 스택 / ESC
    // ─────────────────────────────────────────────

    private void PushPopup(UIBase popup)
    {
        if (_popupStack.Count > 0)
            _popupStack[_popupStack.Count - 1].OnLostFocus();

        _popupStack.Add(popup);

        if (InputManager.HasInstance)
            InputManager.Instance.PushInputBlock();
    }

    private void RemovePopup(UIBase popup)
    {
        int idx = _popupStack.IndexOf(popup);
        if (idx < 0) return;

        _popupStack.RemoveAt(idx);

        if (InputManager.HasInstance)
            InputManager.Instance.PopInputBlock();

        if (_popupStack.Count > 0)
            _popupStack[_popupStack.Count - 1].OnFocus();
    }

    private void OnEscapePressed()
    {
        // 1) 열린 팝업이 있으면 ESC 는 최상위 닫기에 우선 소비된다.
        if (_popupStack.Count > 0)
        {
            var top = _popupStack[_popupStack.Count - 1];
            if (top != null && top.OnBackPressed())
            {
                Close(top);
            }
            // 닫기를 거부(OnBackPressed==false)해도 ESC 는 그 팝업이 소비한 것으로 본다.
            return;
        }

        // 2) 닫을 팝업이 없을 때만 위임 — 런 메뉴 등 상위 라우팅이 처리.
        OnEscapeUnhandled?.Invoke();
    }

    // ─────────────────────────────────────────────
    // 조회 / 헬퍼
    // ─────────────────────────────────────────────

    public T Get<T>() where T : UIBase
        => _openUIs.TryGetValue(typeof(T), out var ui) ? (T)ui : null;

    public bool IsOpen<T>() where T : UIBase
        => _openUIs.TryGetValue(typeof(T), out var ui) && ui != null;

    public void RefreshManagedLocalization()
    {
        var visited = new HashSet<UIBase>();

        RefreshLocalization(_openUIs.Values, visited);
    }

    private static void RefreshLocalization(IEnumerable<UIBase> targets, HashSet<UIBase> visited)
    {
        foreach (var ui in targets)
        {
            if (ui == null || !visited.Add(ui))
                continue;

            try
            {
                ui.OnLocalize();
            }
            catch (Exception e)
            {
                GameLogger.LogError(ELogCategory.UI, $"UI OnLocalize 예외({ui.GetType().Name}): {e}");
            }
        }
    }

    /// <summary>Screen 공간 레이어 루트 조회.</summary>
    public Transform GetLayerRoot(EUILayer layer)
        => _screenLayerRoots.TryGetValue(layer, out var root) ? root : null;

    private Transform GetInactivePreloadRoot()
    {
        if (_inactivePreloadRoot != null) return _inactivePreloadRoot;
        if (_rootCanvas == null) return null;

        var go = new GameObject("UI_InactivePreloadRoot");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(_rootCanvas.transform, worldPositionStays: false);
        go.SetActive(false);
        _inactivePreloadRoot = rt;
        return rt;
    }

    private bool HasOpenTransient(string prefabKey)
        => _transientPrefabKeys.ContainsValue(prefabKey);

    /// <summary>입력 차단 토글 (외부 직접 제어용).</summary>
    public void SetInputBlock(bool block)
    {
        if (!InputManager.HasInstance) return;
        if (block) InputManager.Instance.PushInputBlock();
        else InputManager.Instance.PopInputBlock();
    }

    private async UniTask<UIBase> GetOrLoadPrefab(string key, CancellationToken token)
    {
        if (_prefabCache.TryGetValue(key, out var cached) && cached != null)
            return cached;

        if (!ResourceManager.HasInstance) return null;

        // 실제 ResourceManager의 기본 API만 사용
        var go = await ResourceManager.Instance.LoadAsync<GameObject>(key, token);
        if (go == null)
        {
            GameLogger.LogError(ELogCategory.UI, $"UI prefab 로드 실패: {key}");
            return null;
        }

        var prefab = go.GetComponent<UIBase>();
        if (prefab == null)
        {
            GameLogger.LogError(ELogCategory.UI, $"'{key}' 프리팹에 UIBase 컴포넌트 없음");
            return null;
        }

        _prefabCache[key] = prefab;
        return prefab;
    }
}
