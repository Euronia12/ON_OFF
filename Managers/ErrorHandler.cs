// =================================================================
// [스크립트 목적]  전역 예외/에러 수집. 개발빌드 화면 표시 + 출시빌드 Analytics 전송
// [주요 변수]      - _errorCounts        : 중복 에러 카운트 (스팸 방지)
//                  - _maxReportsPerError : 동일 에러 최대 보고 횟수
// [의존 관계]      - ManagerBase<ErrorHandler>, AnalyticsManager
// [InitOrder]      1 (최우선 — 다른 매니저 초기화 에러도 잡기 위해)
// [주의]           logMessageReceivedThreaded는 백그라운드 스레드 호출 가능
//                  → 메인 스레드 마샬링 필요
// =================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

public class ErrorHandler : ManagerBase<ErrorHandler>
{
    public override int InitOrder => 1; // 최우선

    [Header("에러 핸들링 설정")]
    [Tooltip("동일 에러 최대 보고 횟수 (스팸 방지)")]
    [SerializeField] private int _maxReportsPerError = 3;

    [Tooltip("개발 빌드에서 화면에 에러 오버레이 표시")]
    [SerializeField] private bool _showOverlayInDev = true;

    [Tooltip("Debug.LogError·Exception을 로컬 JSONL 로그 파일에도 기록 (Live 환경은 AnalyticsManager가 자동 차단)")]
    [SerializeField] private bool _logErrorToFile = true;

    // 에러 메시지(시그니처) → 누적 카운트
    private readonly Dictionary<string, int> _errorCounts = new(32);
    // 에러 시그니처 누적 상한 (초과 시 카운트 전체 리셋 → 메모리 증가 방지)
    private const int MAX_ERROR_SIGNATURES = 256;
    // 메인 스레드 표시용 최근 에러 목록
    private readonly List<string> _recentErrors = new(8);
    private readonly object _lock = new();

    private bool _hasPendingError;
    private string _pendingErrorText;

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        Application.logMessageReceivedThreaded += OnLogMessageThreaded;
        return UniTask.CompletedTask;
    }

    protected override UniTask OnShutdownInternalAsync()
    {
        Application.logMessageReceivedThreaded -= OnLogMessageThreaded;
        return UniTask.CompletedTask;
    }

    /// <summary>
    /// 로그 콜백. 백그라운드 스레드일 수 있으므로 Unity API 직접 호출 금지.
    /// UniTask.Post로 메인 스레드에 마샬링.
    /// </summary>
    private void OnLogMessageThreaded(string condition, string stackTrace, LogType type)
    {
        if (type != LogType.Exception && type != LogType.Error)
            return;

        // 시그니처(메시지 첫 줄) 기준 중복 카운트
        string signature = condition;

        lock (_lock)
        {
            // 서로 다른 에러 시그니처가 과도하게 쌓이면(좌표 포함 메시지 등) 전체 초기화.
            // 스팸 차단은 동일 에러엔 유지되지만, 고유 에러 누적으로 인한 메모리 증가 방지.
            if (_errorCounts.Count > MAX_ERROR_SIGNATURES)
                _errorCounts.Clear();

            _errorCounts.TryGetValue(signature, out int count);
            count++;
            _errorCounts[signature] = count;

            if (count > _maxReportsPerError)
                return; // 스팸 차단

            _recentErrors.Add(condition);
            if (_recentErrors.Count > 5)
                _recentErrors.RemoveAt(0);
        }

        // 메인 스레드로 마샬링하여 처리
        string reportCondition = condition;
        string reportStack = stackTrace;
        LogType reportType = type;
        UniTask.Post(() => HandleErrorMainThread(reportCondition, reportStack, reportType));
    }

    private void HandleErrorMainThread(string condition, string stackTrace, LogType type)
    {
        // 에러를 로컬 JSONL 로그에도 기록 (Debug.LogError·Exception 추적용)
        // Live 환경에서는 AnalyticsManager가 로컬 로그 자체를 비활성하므로 자동 차단됨
        // (출시 빌드에서 외부 Analytics SDK로 전송하려면 해당 Provider를 AddProvider)
        if (_logErrorToFile && AnalyticsManager.HasInstance)
        {
            AnalyticsManager.Instance.LogEvent("runtime_error",
                new Dictionary<string, object>
                {
                    { "message", Truncate(condition, 200) },
                    { "type", type.ToString() }
                });
        }

        // 개발 빌드: 화면 오버레이
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (_showOverlayInDev)
        {
            _pendingErrorText = $"[{type}] {condition}";
            _hasPendingError = true;
        }
#endif
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private GUIStyle _overlayStyle;          // OnGUI 매 호출 new 방지 (지연 초기화)
    private static readonly Color OverlayBg = new Color(0.8f, 0f, 0f, 0.85f);

    private void OnGUI()
    {
        if (!_hasPendingError || !_showOverlayInDev) return;

        // 스타일은 최초 1회만 생성 (OnGUI는 프레임당 여러 번 호출 → 매번 new면 GC 누적)
        if (_overlayStyle == null)
        {
            _overlayStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(10, 10, 5, 5)
            };
        }

        const float height = 80f;
        var rect = new Rect(0, Screen.height - height, Screen.width, height);

        var prevColor = GUI.color;
        GUI.color = OverlayBg;
        GUI.Box(rect, GUIContent.none);
        GUI.color = Color.white;

        GUI.Label(rect, _pendingErrorText, _overlayStyle);

        var btnRect = new Rect(Screen.width - 70, Screen.height - height + 5, 60, 25);
        if (GUI.Button(btnRect, "닫기"))
            _hasPendingError = false;

        GUI.color = prevColor;
    }
#endif

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

    /// <summary>수동 에러 보고 (try-catch 블록 등에서).</summary>
    public void ReportError(string message, Exception exception = null)
    {
        string full = exception != null ? $"{message}\n{exception}" : message;
        GameLogger.LogError(ELogCategory.System, full);
        // logMessageReceived 경로로 자동 수집됨
    }

    public void ClearErrorCounts()
    {
        lock (_lock)
        {
            _errorCounts.Clear();
            _recentErrors.Clear();
        }
    }
}