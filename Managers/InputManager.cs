// =================================================================
// [스크립트 목적]  New Input System 래핑. Action Map 전환 + 입력 차단 카운터
//                 + 전역 이벤트 + Action 캐싱/구독 헬퍼 + 키 리바인딩(저장/로드/UI연동)
// [주요 변수]      - _runtimeAsset   : 런타임 InputActionAsset 인스턴스
//                  - _blockCount     : 입력 차단 카운터 (UI 팝업 등 중첩 대응)
//                  - _currentMap     : 현재 활성 Action Map
//                  - _actionCache    : 액션명 → InputAction 캐시 (FindAction 반복 제거)
//                  - _cancelAction   : ESC/백버튼 통합 액션 (UI 맵의 Cancel)
//                  - _activeRebind   : 진행 중인 리바인딩 작업 (중복 방지)
// [의존 관계]      - ManagerBase<InputManager>, Input System 패키지, SaveManager(리바인딩 저장)
// [주의]           InputActionAsset은 Inspector 할당 또는 Resources 로드 필요
//                  Cancel 액션이 에셋에 있으면 ESC 폴링 대신 InputAction으로 동작
// [InitOrder]      60
// =================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;

public class InputManager : ManagerBase<InputManager>
{
    public override int InitOrder => 60;

    [Header("Input 설정")]
    [Tooltip("InputActionAsset. 미할당 시 Resources에서 _inputActionsKey로 로드 시도")]
    [SerializeField] private InputActionAsset _inputActionsAsset;

    [Tooltip("Asset 미할당 시 Resources 로드 키")]
    [SerializeField] private string _inputActionsKey = "InputActions";

    [Tooltip("시작 시 활성화할 Action Map")]
    [SerializeField] private EActionMap _defaultMap = EActionMap.Gameplay;

    [Header("리바인딩 설정")]
    [Tooltip("리바인딩 오버라이드 저장 슬롯 이름. SaveManager가 이 이름으로 파일 생성")]
    [SerializeField] private string _rebindSaveSlot = "InputBindings";

    [Tooltip("초기화 시 저장된 리바인딩을 자동 로드/적용할지 여부")]
    [SerializeField] private bool _autoLoadRebinds = true;

    [Tooltip("리바인딩 대기 시 입력으로 받지 않을 컨트롤 경로 (마우스 이동·우클릭 등 오인 방지)")]
    [SerializeField]
    private string[] _rebindExcludeControls =
    {
        "<Mouse>/position",
        "<Mouse>/delta",
        "<Pointer>/position",
        "<Pointer>/delta"
    };

    private InputActionAsset _runtimeAsset;
    private InputActionMap _currentMap;
    private int _blockCount;
    private int _escapeBlockCount;

    // 액션 캐시: 매번 FindAction(선형 탐색) 호출을 피하기 위함.
    // 키 = "맵이름/액션이름" (맵 무관 접근까지 한 캐시로 처리)
    private readonly Dictionary<string, InputAction> _actionCache =
        new Dictionary<string, InputAction>(32);

    // ESC/백버튼 통합 액션 (에셋에 UI/Cancel 이 있을 때만 세팅)
    private InputAction _cancelAction;

    // Alt+Enter 화면 모드 토글 액션 (에셋에 UI/ToggleFullScreen 이 있을 때만 세팅)
    private InputAction _toggleFullScreenAction;

    // Dev/Test 환경에서만 Gameplay/UI와 동시에 활성화하는 테스트 전용 맵.
    private InputActionMap _testCommandMap;

    // 진행 중인 리바인딩 작업. null이 아니면 리바인딩 중 (중복 시작 방지)
    private InputActionRebindingExtensions.RebindingOperation _activeRebind;

    public bool IsBlocked => _blockCount > 0;
    public EActionMap CurrentMap { get; private set; }

    /// <summary>현재 리바인딩(키 변경 대기)이 진행 중인지 여부</summary>
    public bool IsRebinding => _activeRebind != null;

    /// <summary>ESC / 백버튼 전역 이벤트. UIManager가 구독해 최상위 팝업 닫기 처리.</summary>
    public event Action OnEscapePressed;

    /// <summary>Alt+Enter 전역 이벤트. SettingsManager가 구독해 전체화면 ↔ 창모드 전환.</summary>
    public event Action OnToggleFullScreenPressed;

    /// <summary>Dev/Test 전용 테스트 커맨드 이벤트. 인자: Input Action 이름.</summary>
    public event Action<string> OnTestCommandPerformed;

    /// <summary>Action Map 전환 시 발행</summary>
    public event Action<EActionMap> OnActionMapChanged;

    /// <summary>리바인딩 완료 시 발행 (성공/취소 무관, 적용 후). 인자: 변경된 액션명</summary>
    public event Action<string> OnRebindFinished;

    // ─────────────────────────────────────────────
    // 초기화 / 종료
    // ─────────────────────────────────────────────

    protected override async UniTask OnInitializeAsync(CancellationToken token)
    {
        _runtimeAsset = _inputActionsAsset;

        if (_runtimeAsset == null && ResourceManager.HasInstance)
            _runtimeAsset = ResourceManager.Instance.LoadResourceSync<InputActionAsset>(_inputActionsKey);

        if (_runtimeAsset == null)
        {
            GameLogger.LogError(ELogCategory.Input,
                "InputActionAsset 미할당. Inspector 또는 Resources 확인");
            return;
        }

        // 저장된 리바인딩을 맵 활성화보다 먼저 적용 (오버라이드된 상태로 시작)
        if (_autoLoadRebinds)
            await LoadRebindsAsync(token);

        // ESC/백버튼 통합 액션 바인딩 (UI 맵의 Cancel)
        SetupCancelAction();
        SetupToggleFullScreenAction();
        SetupTestCommandMap();

        SwitchMap(_defaultMap);
    }

    protected override UniTask OnShutdownInternalAsync()
    {
        CancelActiveRebind();
        ReleaseCancelAction();
        ReleaseToggleFullScreenAction();
        ReleaseTestCommandMap();

        _currentMap?.Disable();
        _actionCache.Clear();

        // Domain Reload OFF 환경에서 이전 구독자가 남아 중복 호출되는 것 방지
        OnEscapePressed = null;
        OnToggleFullScreenPressed = null;
        OnTestCommandPerformed = null;
        OnActionMapChanged = null;
        OnRebindFinished = null;

        return UniTask.CompletedTask;
    }

    // ─────────────────────────────────────────────
    // ESC / 백버튼 (InputAction 기반 — Update 폴링 제거)
    // ─────────────────────────────────────────────

    /// <summary>
    /// UI 맵의 Cancel 액션을 찾아 콜백 구독.
    /// 에셋에 Cancel 액션이 있어야 동작 (ESC + Android 백버튼 매핑 권장).
    /// 없으면 경고만 남기고 OnEscapePressed는 발행되지 않음.
    /// </summary>
    private void SetupCancelAction()
    {
        _cancelAction = GetAction(EActionMap.UI, InputActionNames.Cancel);
        if (_cancelAction == null)
        {
            _cancelAction = FindActionAcrossMaps(InputActionNames.Cancel);
        }

        if (_cancelAction == null)
        {
            GameLogger.LogWarning(ELogCategory.Input,
                $"'{EActionMap.UI}/{InputActionNames.Cancel}' 액션 없음. ESC/백버튼 이벤트 비활성. " +
                "에셋에 Cancel 액션(<Keyboard>/escape, <Gamepad>/start, Android 백버튼 등) 추가 권장");
            return;
        }

        // Cancel은 맵 전환과 무관하게 항상 받아야 하므로 액션 자체를 enable.
        _cancelAction.performed += OnCancelPerformed;
        _cancelAction.Enable();
    }

    private void ReleaseCancelAction()
    {
        if (_cancelAction == null) return;
        _cancelAction.performed -= OnCancelPerformed;
        _cancelAction = null;
    }

    private void OnCancelPerformed(InputAction.CallbackContext ctx)
    {
        // 리바인딩 대기 중에는 취소 입력을 전역 ESC로 흘리지 않음 (리바인딩이 가로챔)
        if (IsRebinding || _escapeBlockCount > 0) return;
        OnEscapePressed?.Invoke();
    }

    // ─────────────────────────────────────────────
    // 화면 모드 토글 (Alt+Enter — InputAction 기반)
    // ─────────────────────────────────────────────

    /// <summary>
    /// UI 맵의 ToggleFullScreen 액션을 찾아 콜백 구독.
    /// 에셋에 Alt+Enter 바인딩이 있어야 동작하며, 없으면 경고만 남기고 이벤트는 발행되지 않음.
    /// Player Settings의 "Allow Fullscreen Switch"를 꺼야 엔진 기본 토글과 중복 발동하지 않는다.
    /// </summary>
    private void SetupToggleFullScreenAction()
    {
        _toggleFullScreenAction = GetAction(EActionMap.UI, InputActionNames.ToggleFullScreen);
        if (_toggleFullScreenAction == null)
        {
            _toggleFullScreenAction = FindActionAcrossMaps(InputActionNames.ToggleFullScreen);
        }

        if (_toggleFullScreenAction == null)
        {
            GameLogger.LogWarning(ELogCategory.Input,
                $"'{EActionMap.UI}/{InputActionNames.ToggleFullScreen}' 액션 없음. Alt+Enter 화면 모드 토글 비활성. " +
                "에셋에 ToggleFullScreen 액션(<Keyboard>/alt + <Keyboard>/enter) 추가 필요");
            return;
        }

        // 화면 모드 전환은 맵 전환·입력 차단(팝업 등)과 무관하게 항상 받아야 하므로 액션 자체를 enable.
        _toggleFullScreenAction.performed += OnToggleFullScreenPerformed;
        _toggleFullScreenAction.Enable();
    }

    private void ReleaseToggleFullScreenAction()
    {
        if (_toggleFullScreenAction == null) return;
        _toggleFullScreenAction.performed -= OnToggleFullScreenPerformed;
        _toggleFullScreenAction = null;
    }

    private void OnToggleFullScreenPerformed(InputAction.CallbackContext ctx)
    {
        // 리바인딩 대기 중에는 그쪽이 입력을 가져가야 하므로 흘리지 않음
        if (IsRebinding) return;
        OnToggleFullScreenPressed?.Invoke();
    }

    // ─────────────────────────────────────────────
    // 테스트 커맨드 (Dev/Test 환경에서만 별도 맵 동시 활성화)
    // ─────────────────────────────────────────────

    private void SetupTestCommandMap()
    {
#if !ONOFF_TEST_COMMANDS
        _testCommandMap = null;
        return;
#else
        _testCommandMap = FindActionMap(EActionMap.TestCommand.ToString());
        if (_testCommandMap == null)
        {
            GameLogger.LogWarning(ELogCategory.Input,
                $"테스트 Action Map 없음: {EActionMap.TestCommand}. 테스트 커맨드를 사용할 수 없습니다.");
            return;
        }

        foreach (InputAction action in _testCommandMap.actions)
            action.performed += OnTestCommandActionPerformed;

        RefreshTestCommandMap();
#endif
    }

    private void ReleaseTestCommandMap()
    {
        if (_testCommandMap == null) return;

        foreach (InputAction action in _testCommandMap.actions)
            action.performed -= OnTestCommandActionPerformed;

        _testCommandMap.Disable();
        _testCommandMap = null;
    }

    /// <summary>
    /// BuildEnv 정책에 따라 테스트 맵을 Gameplay/UI와 별도로 켜거나 끈다.
    /// GameManager 초기화 완료 시에도 호출되어 초기화 순서와 무관하게 상태를 보정한다.
    /// </summary>
    public void RefreshTestCommandMap()
    {
        if (_testCommandMap == null) return;

#if ONOFF_TEST_COMMANDS
        // Dev/Test 빌드에서만 테스트 키 입력을 받는다. Live 빌드에서는 액션맵 자체를 꺼
        // 입력 표면적을 제거한다(실제 기능 실행 차단은 DebugCommandManager가 이중으로 유지).
        bool allow = GameManager.HasInstance && GameManager.Instance.CanUseTestCommands;
        if (allow)
            _testCommandMap.Enable();
        else
            _testCommandMap.Disable();
#else
        _testCommandMap.Disable();
#endif
    }

    private void OnTestCommandActionPerformed(InputAction.CallbackContext ctx)
    {
        OnTestCommandPerformed?.Invoke(ctx.action.name);
    }

    // ─────────────────────────────────────────────
    // Action Map 전환
    // ─────────────────────────────────────────────

    public void SwitchMap(EActionMap map)
    {
        if (_runtimeAsset == null) return;

        // 리바인딩 대기 중 맵 전환 시: 진행 중 리바인딩을 먼저 안전 취소.
        // (전환으로 대상 액션의 enable 상태가 바뀌면 wasEnabled 복구가 틀어지고 입력 흐름이 꼬임)
        if (IsRebinding)
        {
            GameLogger.LogWarning(ELogCategory.Input, $"맵 전환({map}) 중 진행 리바인딩 자동 취소");
            CancelActiveRebind();
        }

        _currentMap?.Disable();

        string mapName = map.ToString();
        var newMap = FindActionMap(mapName);
        if (newMap == null)
        {
            GameLogger.LogError(ELogCategory.Input, $"Action Map 없음: {mapName}");
            return;
        }

        _currentMap = newMap;
        CurrentMap = map;

        if (_blockCount <= 0)
            _currentMap.Enable();

        OnActionMapChanged?.Invoke(map);
        GameLogger.Log(ELogCategory.Input, $"Action Map 전환: {mapName}");
    }

    // ─────────────────────────────────────────────
    // 입력 차단 (카운터 방식 — 중첩 안전)
    // ─────────────────────────────────────────────

    /// <summary>입력 차단 1 증가. 팝업 열 때 호출. PopInputBlock과 쌍으로 사용.</summary>
    public void PushInputBlock()
    {
        _blockCount++;
        if (_blockCount == 1)
            _currentMap?.Disable();
    }

    /// <summary>입력 차단 1 감소. 0이 되면 입력 복구.</summary>
    public void PopInputBlock()
    {
        if (_blockCount <= 0) return;
        _blockCount--;
        if (_blockCount == 0 && _currentMap != null)
            _currentMap.Enable();
    }

    public void ClearInputBlock()
    {
        _blockCount = 0;
        _currentMap?.Enable();
    }

    /// <summary>강제 연출 중 전역 ESC 처리를 차단한다. PopEscapeBlock과 쌍으로 사용한다.</summary>
    public void PushEscapeBlock()
    {
        _escapeBlockCount++;
    }

    /// <summary>전역 ESC 차단을 1 감소시킨다.</summary>
    public void PopEscapeBlock()
    {
        if (_escapeBlockCount <= 0) return;
        _escapeBlockCount--;
    }

    // ─────────────────────────────────────────────
    // Action 조회 헬퍼 (캐싱)
    // ─────────────────────────────────────────────

    /// <summary>현재 맵에서 Action 조회. 콜백 구독·값 읽기용. 결과 캐싱.</summary>
    public InputAction GetAction(string actionName)
    {
        if (_currentMap == null) return null;

        string cacheKey = _currentMap.name + "/" + actionName;
        if (_actionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var action = _currentMap.FindAction(actionName, throwIfNotFound: false);
        if (action != null)
            _actionCache[cacheKey] = action;
        return action;
    }

    /// <summary>특정 맵의 Action 조회 (맵 무관 접근). 결과 캐싱.</summary>
    public InputAction GetAction(EActionMap map, string actionName)
    {
        if (_runtimeAsset == null) return null;

        string mapName = map.ToString();
        string cacheKey = mapName + "/" + actionName;
        if (_actionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var targetMap = FindActionMap(mapName);
        var action = targetMap?.FindAction(actionName, throwIfNotFound: false);
        if (action != null)
            _actionCache[cacheKey] = action;
        return action;
    }

    private InputActionMap FindActionMap(string mapName)
    {
        if (_runtimeAsset == null || string.IsNullOrWhiteSpace(mapName)) return null;

        var map = _runtimeAsset.FindActionMap(mapName, throwIfNotFound: false);
        if (map != null) return map;

        for (int i = 0; i < _runtimeAsset.actionMaps.Count; i++)
        {
            InputActionMap candidate = _runtimeAsset.actionMaps[i];
            if (string.Equals(candidate.name, mapName, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private InputAction FindActionAcrossMaps(string actionName)
    {
        if (_runtimeAsset == null || string.IsNullOrWhiteSpace(actionName)) return null;

        var action = _runtimeAsset.FindAction(actionName, throwIfNotFound: false);
        if (action != null) return action;

        for (int i = 0; i < _runtimeAsset.actionMaps.Count; i++)
        {
            InputActionMap map = _runtimeAsset.actionMaps[i];
            action = map.FindAction(actionName, throwIfNotFound: false);
            if (action != null) return action;
        }

        return null;
    }

    // ─────────────────────────────────────────────
    // 구독 헬퍼 ("키 추가 시 동작 추가" — 콜백 등록형)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 액션의 performed 콜백 구독. 반환된 IDisposable.Dispose()로 해제.
    /// 외부 객체는 OnEnable에서 구독, OnDisable에서 Dispose 권장 (생명주기 안전).
    /// 새 입력 키를 추가하면 상수 추가 + 이 메서드로 핸들러 한 줄 등록하면 됨.
    /// </summary>
    public IDisposable SubscribePerformed(string actionName, Action<InputAction.CallbackContext> callback)
        => SubscribeInternal(GetAction(actionName), nameof(InputAction.performed), callback);

    /// <summary>특정 맵 액션의 performed 콜백 구독 (맵 무관).</summary>
    public IDisposable SubscribePerformed(EActionMap map, string actionName, Action<InputAction.CallbackContext> callback)
        => SubscribeInternal(GetAction(map, actionName), nameof(InputAction.performed), callback);

    /// <summary>액션의 started 콜백 구독.</summary>
    public IDisposable SubscribeStarted(string actionName, Action<InputAction.CallbackContext> callback)
        => SubscribeInternal(GetAction(actionName), nameof(InputAction.started), callback);

    /// <summary>액션의 canceled 콜백 구독 (버튼 뗌·입력 종료).</summary>
    public IDisposable SubscribeCanceled(string actionName, Action<InputAction.CallbackContext> callback)
        => SubscribeInternal(GetAction(actionName), nameof(InputAction.canceled), callback);

    /// <summary>
    /// 구독 공통 처리. phase 문자열로 어떤 이벤트에 붙일지 분기.
    /// IDisposable 반환 → Dispose 시 정확히 같은 핸들러 해제 (구독 누수 방지).
    /// </summary>
    private IDisposable SubscribeInternal(InputAction action, string phase, Action<InputAction.CallbackContext> callback)
    {
        if (action == null || callback == null)
        {
            GameLogger.LogWarning(ELogCategory.Input, $"구독 실패: action 또는 callback null (phase={phase})");
            return EmptyDisposable.Instance;
        }

        switch (phase)
        {
            case nameof(InputAction.performed): action.performed += callback; break;
            case nameof(InputAction.started): action.started += callback; break;
            case nameof(InputAction.canceled): action.canceled += callback; break;
        }

        return new InputActionSubscription(action, phase, callback);
    }

    // ─────────────────────────────────────────────
    // 키 리바인딩 (변경)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 인터랙티브 리바인딩 시작. 다음 입력을 받아 해당 바인딩에 적용.
    /// UI 연동용: onComplete(성공 여부), onCancel 콜백으로 흐름 제어.
    /// 충돌(이미 쓰이는 키) 감지 시 자동 취소 + onConflict 호출.
    /// </summary>
    /// <param name="map">대상 액션 맵</param>
    /// <param name="actionName">대상 액션명 (InputActionNames 상수 권장)</param>
    /// <param name="bindingIndex">복합 바인딩 시 인덱스 (단일은 0)</param>
    /// <param name="onComplete">성공 시. 인자: 새 바인딩 표시 문자열</param>
    /// <param name="onCancel">사용자 취소 시</param>
    /// <param name="onConflict">키 충돌 시. 인자: 충돌한 기존 액션명</param>
    public void StartRebind(
        EActionMap map,
        string actionName,
        int bindingIndex = 0,
        Action<string> onComplete = null,
        Action onCancel = null,
        Action<string> onConflict = null)
    {
        if (IsRebinding)
        {
            GameLogger.LogWarning(ELogCategory.Input, "이미 리바인딩 진행 중. 중복 시작 무시");
            return;
        }

        var action = GetAction(map, actionName);
        if (action == null)
        {
            GameLogger.LogError(ELogCategory.Input, $"리바인딩 대상 액션 없음: {map}/{actionName}");
            onCancel?.Invoke();
            return;
        }

        // 리바인딩 중에는 해당 액션을 disable 해야 함 (Input System 요구사항)
        bool wasEnabled = action.enabled;
        action.Disable();

        var op = action.PerformInteractiveRebinding(bindingIndex);

        // 마우스 이동 등 오인 입력 제외
        if (_rebindExcludeControls != null)
        {
            for (int i = 0; i < _rebindExcludeControls.Length; i++)
                op.WithControlsExcluding(_rebindExcludeControls[i]);
        }

        op.OnComplete(operation =>
        {
            // 충돌 검사: 같은 맵 내 다른 액션이 이 바인딩을 이미 사용 중인지
            string conflictAction = FindBindingConflict(action, bindingIndex);
            if (conflictAction != null)
            {
                // 충돌 → 변경 취소(되돌림) 후 알림
                action.RemoveBindingOverride(bindingIndex);
                FinishRebind(action, wasEnabled, actionName);
                GameLogger.Log(ELogCategory.Input, $"리바인딩 충돌: {actionName} ↔ {conflictAction}");
                onConflict?.Invoke(conflictAction);
                return;
            }

            string display = action.GetBindingDisplayString(bindingIndex);
            FinishRebind(action, wasEnabled, actionName);
            GameLogger.Log(ELogCategory.Input, $"리바인딩 완료: {actionName} → {display}");
            onComplete?.Invoke(display);
        })
            .OnCancel(operation =>
            {
                FinishRebind(action, wasEnabled, actionName);
                GameLogger.Log(ELogCategory.Input, $"리바인딩 취소: {actionName}");
                onCancel?.Invoke();
            });

        _activeRebind = op;
        op.Start();
        GameLogger.Log(ELogCategory.Input, $"리바인딩 대기: {map}/{actionName}");
    }

    /// <summary>진행 중인 리바인딩 강제 취소 (UI 닫기·ESC 등).</summary>
    public void CancelActiveRebind()
    {
        if (_activeRebind == null) return;
        _activeRebind.Cancel(); // OnCancel 콜백 경유 → FinishRebind 호출됨
    }

    /// <summary>리바인딩 작업 정리 공통 처리 (Dispose + 액션 복구 + 이벤트 발행).</summary>
    private void FinishRebind(InputAction action, bool wasEnabled, string actionName)
    {
        _activeRebind?.Dispose();
        _activeRebind = null;

        if (wasEnabled)
            action.Enable();

        OnRebindFinished?.Invoke(actionName);
    }

    /// <summary>
    /// 특정 액션의 바인딩이 같은 맵 내 다른 액션과 충돌하는지 검사.
    /// 충돌 시 충돌한 액션명 반환, 없으면 null.
    /// </summary>
    private string FindBindingConflict(InputAction action, int bindingIndex)
    {
        var newBinding = action.bindings[bindingIndex];
        var map = action.actionMap;
        if (map == null) return null;

        // 같은 맵 내 모든 바인딩과 effectivePath 비교
        foreach (var binding in map.bindings)
        {
            // 자기 자신·복합 헤더는 스킵
            if (binding.isComposite || binding.isPartOfComposite) continue;
            if (binding.action == newBinding.action) continue;

            if (binding.effectivePath == newBinding.effectivePath)
                return binding.action; // 충돌한 액션명
        }
        return null;
    }

    /// <summary>특정 액션을 기본 바인딩으로 초기화 (오버라이드 제거).</summary>
    public void ResetBinding(EActionMap map, string actionName)
    {
        var action = GetAction(map, actionName);
        if (action == null) return;
        action.RemoveAllBindingOverrides();
        GameLogger.Log(ELogCategory.Input, $"바인딩 초기화: {map}/{actionName}");
    }

    /// <summary>모든 액션을 기본 바인딩으로 초기화.</summary>
    public void ResetAllBindings()
    {
        if (_runtimeAsset == null) return;
        _runtimeAsset.RemoveAllBindingOverrides();
        GameLogger.Log(ELogCategory.Input, "전체 바인딩 초기화");
    }

    /// <summary>현재 바인딩의 표시 문자열 조회 (UI 라벨용).</summary>
    public string GetBindingDisplay(EActionMap map, string actionName, int bindingIndex = 0)
    {
        var action = GetAction(map, actionName);
        return action != null ? action.GetBindingDisplayString(bindingIndex) : string.Empty;
    }

    // ─────────────────────────────────────────────
    // 리바인딩 저장 / 로드 (SaveManager 경유)
    // ─────────────────────────────────────────────

    /// <summary>현재 리바인딩 오버라이드를 SaveManager로 저장.</summary>
    public async UniTask SaveRebindsAsync(CancellationToken token = default)
    {
        if (_runtimeAsset == null || !SaveManager.HasInstance) return;

        var data = new InputRebindData
        {
            BindingOverridesJson = _runtimeAsset.SaveBindingOverridesAsJson()
        };

        await SaveManager.Instance.SaveAsync(_rebindSaveSlot, data, token);
        GameLogger.Log(ELogCategory.Input, "리바인딩 저장 완료");
    }

    /// <summary>저장된 리바인딩 오버라이드를 로드해 에셋에 적용.</summary>
    public async UniTask LoadRebindsAsync(CancellationToken token = default)
    {
        if (_runtimeAsset == null || !SaveManager.HasInstance) return;
        if (!SaveManager.Instance.Exists(_rebindSaveSlot)) return;

        var data = await SaveManager.Instance.LoadAsync<InputRebindData>(_rebindSaveSlot, token);
        if (data == null || string.IsNullOrEmpty(data.BindingOverridesJson)) return;

        _runtimeAsset.LoadBindingOverridesFromJson(data.BindingOverridesJson);
        GameLogger.Log(ELogCategory.Input, "리바인딩 로드 적용 완료");
    }
}
