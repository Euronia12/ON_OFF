// =================================================================
// [스크립트 목적]  TimeScale 스택 관리 + Pause/Resume + SlowMotion + 누적 게임 시간
// [주요 변수]      - _scaleStack    : TimeScale 요청 스택 (다중 요청 안전)
//                  - _gameTime      : 일시정지 제외 누적 플레이 시간 (벽시계 기준 실제 초)
// [의존 관계]      - ManagerBase<TimeManager>
//                  - EventManager (GamePauseChangedEvent 발행, 선택적)
// [주의]           TimeScale 직접 수정 대신 Push/Pop으로 관리 (충돌 방지)
// [InitOrder]      70
// =================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

public class TimeManager : ManagerBase<TimeManager>
{
    public override int InitOrder => 70;

    // SlowMotion 전용 예약 id. 외부에서 같은 id로 Push/Pop 하지 말 것.
    private const string SLOW_MOTION_ID = "__slowmotion__";

    // (식별자, 스케일) 스택. 최상위 요청이 적용됨
    private readonly List<(string id, float scale)> _scaleStack = new(8);

    private float _baseScale = 1f;
    private bool _isPaused;
    private float _gameTime;
    private CancellationTokenSource _slowMotionCts;

    // 슬로우모션 세대 번호. 취소/재호출마다 증가 → 이전 작업의 지연된 finally가
    // 새 슬로우모션(같은 SLOW_MOTION_ID)을 PopScale로 제거하는 것을 방지.
    private int _slowMotionGeneration;

    /// <summary>일시정지 제외 누적 게임 시간 (벽시계 기준 실제 경과 초). timeScale 영향 없음.</summary>
    public float GameTime => _gameTime;
    public bool IsPaused => _isPaused;
    public float CurrentScale => Time.timeScale;

    /// <summary>일시정지 상태 변화 통지 (직접 참조 구독자용). true=일시정지, false=해제.</summary>
    public event Action<bool> OnPauseChanged;

    protected override UniTask OnInitializeAsync(CancellationToken token)
    {
        Time.timeScale = _baseScale;
        return UniTask.CompletedTask;
    }

    /// <summary>종료 시 timeScale 원복 (씬 재시작·도메인 리로드 OFF 시 0 잔존 방지).</summary>
    protected override UniTask OnShutdownInternalAsync()
    {
        CancelSlowMotion(); // 취소 + 세대 증가 (진행 작업 finally 무효화) — 일관 처리

        _scaleStack.Clear();
        // 일시정지 상태도 함께 해제한다. _isPaused 가 남으면 도메인 리로드 OFF 환경에서
        // 다음 ApplyScale() 이 곧바로 timeScale 0 을 다시 써 화면이 얼어붙는다.
        _isPaused = false;
        Time.timeScale = 1f;
        return UniTask.CompletedTask;
    }

    private void Update()
    {
        // 일시정지가 아닐 때만 누적. 벽시계 기준 실제 경과 초이므로 unscaled 그대로 사용
        // (timeScale 0이어도 정확, baseScale·슬로우모션 영향 없음).
        if (!_isPaused)
            _gameTime += Time.unscaledDeltaTime;
    }

    // ─────────────────────────────────────────────
    // Pause / Resume
    // ─────────────────────────────────────────────

    public void Pause()
    {
        if (_isPaused) return;
        _isPaused = true;
        Time.timeScale = 0f;
        NotifyPauseChanged(true);
    }

    public void Resume()
    {
        if (!_isPaused) return;
        _isPaused = false;
        ApplyScale();
        NotifyPauseChanged(false);
    }

    public void TogglePause()
    {
        if (_isPaused) Resume();
        else Pause();
    }

    // C# event(직접 참조 구독자) + EventManager(struct, 느슨한 결합) 둘 다 통지
    private void NotifyPauseChanged(bool paused)
    {
        OnPauseChanged?.Invoke(paused);

        if (EventManager.HasInstance)
            EventManager.Instance.Publish(new GamePauseChangedEvent { IsPaused = paused });
    }

    // ─────────────────────────────────────────────
    // TimeScale 스택
    // ─────────────────────────────────────────────

    /// <summary>
    /// 스케일 요청 추가. id로 중복 방지. 같은 id 재요청 시 갱신.
    /// 예: 슬로우모션 연출, 불릿타임 등. 반드시 PopScale로 해제.
    /// id는 호출부에서 const로 관리 권장 (Magic String 방지).
    /// </summary>
    public void PushScale(string id, float scale)
    {
        if (string.IsNullOrEmpty(id)) return;

        int idx = _scaleStack.FindIndex(e => e.id == id);
        if (idx >= 0) _scaleStack[idx] = (id, scale);
        else _scaleStack.Add((id, scale));

        ApplyScale();
    }

    public void PopScale(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        int idx = _scaleStack.FindIndex(e => e.id == id);
        if (idx >= 0) _scaleStack.RemoveAt(idx);
        ApplyScale();
    }

    /// <summary>
    /// 모든 스케일 요청 해제 후 baseScale 복귀.
    /// 진행 중인 SlowMotion도 함께 취소 (스택과 CTS 상태 불일치 방지).
    /// </summary>
    public void ClearScales()
    {
        CancelSlowMotion();
        _scaleStack.Clear();
        ApplyScale();
    }

    private void ApplyScale()
    {
        if (_isPaused)
        {
            Time.timeScale = 0f;
            return;
        }

        // 스택 최상위 요청 적용 (없으면 baseScale)
        Time.timeScale = _scaleStack.Count > 0
            ? _scaleStack[_scaleStack.Count - 1].scale
            : _baseScale;
    }

    public void SetBaseScale(float scale)
    {
        _baseScale = Mathf.Max(0f, scale);
        ApplyScale();
    }

    // ─────────────────────────────────────────────
    // SlowMotion (지속시간 후 자동 복구)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 지정 시간 동안 슬로우모션 후 자동 복구. 재호출 시 이전 슬로우모션 취소.
    /// duration은 벽시계 기준(timeScale 영향 없음).
    /// </summary>
    public void SlowMotion(float scale, float duration)
    {
        CancelSlowMotion(); // 이전 작업 취소 + 세대 증가
        _slowMotionCts = new CancellationTokenSource();
        int generation = _slowMotionGeneration; // 취소로 이미 증가된 현재 세대 캡처

        SlowMotionAsync(scale, duration, generation, _slowMotionCts.Token).Forget();
    }

    /// <summary>진행 중인 슬로우모션 즉시 취소 + 스케일 원복.</summary>
    public void StopSlowMotion()
    {
        CancelSlowMotion(); // 취소 + 세대 증가(진행 작업 finally 무효화)
        PopScale(SLOW_MOTION_ID);
    }

    private void CancelSlowMotion()
    {
        _slowMotionCts?.Cancel();
        _slowMotionCts?.Dispose();
        _slowMotionCts = null;

        // 세대 증가 → 진행 중이던 SlowMotionAsync의 finally가 PopScale을 못 하도록 무효화.
        // (SlowMotion/StopSlowMotion/ClearScales 모든 취소 경로가 이 메서드를 거치므로 일관 처리)
        _slowMotionGeneration++;
    }

    private async UniTaskVoid SlowMotionAsync(float scale, float duration, int generation, CancellationToken token)
    {
        try
        {
            PushScale(SLOW_MOTION_ID, scale);
            // 실제 경과 시간 기준 (timeScale 영향 안 받도록 unscaled)
            await UniTask.Delay(TimeSpan.FromSeconds(duration),
                ignoreTimeScale: true, cancellationToken: token);
        }
        catch (OperationCanceledException)
        {
            // 외부 취소(재호출·ClearScales·파괴)는 정상 흐름. PopScale은 finally에서 세대 확인 후 처리.
        }
        finally
        {
            // 내가 최신 세대일 때만 PopScale. 재호출·취소로 낡은 세대가 되었으면
            // 새 슬로우모션이 스택을 관리하므로 건드리지 않음 (새 작업 취소 방지).
            if (generation == _slowMotionGeneration)
                PopScale(SLOW_MOTION_ID);
        }
    }

    protected override void OnDestroy()
    {
        CancelSlowMotion();
        OnPauseChanged = null;
        base.OnDestroy();
    }
}

/// <summary>
/// 일시정지 상태가 바뀔 때 발행되는 이벤트 (struct → GC 0).
/// TimeManager를 직접 참조하지 않고 일시정지에 반응하려는 시스템용
/// (UI·사운드 등). 구독:
///   _sub = EventManager.Instance.Subscribe&lt;GamePauseChangedEvent&gt;(OnPauseChanged);
/// 해제(OnDisable): _sub.Dispose();
/// </summary>
public struct GamePauseChangedEvent
{
    /// <summary>true=일시정지 진입, false=해제</summary>
    public bool IsPaused;
}