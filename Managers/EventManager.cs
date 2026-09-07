// =================================================================
// [스크립트 목적]  타입 기반 PubSub 이벤트 버스. 글로벌 느슨한 결합용
// [주요 변수]      - _handlers : 이벤트 타입 → 핸들러 델리게이트 (Action<T> 멀티캐스트)
//                  - _publishDepth : 연쇄 발행 깊이 (무한 루프 방어)
// [의존 관계]      - ManagerBase<EventManager>, IDisposableEvent, GameLogger
// [InitOrder]      100
// =================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

public class EventManager : ManagerBase<EventManager>
{
    public override int InitOrder => 100;

    // 연쇄 발행 깊이 한계. 넘으면 무한 트리거로 간주하고 중단.
    private const int MAX_PUBLISH_DEPTH = 16;

    private readonly Dictionary<Type, Delegate> _handlers = new(32);
    private int _publishDepth;

    protected override UniTask OnInitializeAsync(CancellationToken token)
        => UniTask.CompletedTask;

    protected override UniTask OnShutdownInternalAsync()
    {
        _handlers.Clear();
        _publishDepth = 0;
        return UniTask.CompletedTask;
    }

    // ─────────────────────────────────────────────
    // 구독 / 해제
    // ─────────────────────────────────────────────

    public IDisposableEvent Subscribe<T>(Action<T> handler) where T : struct
    {
        if (handler == null) return null;

        var type = typeof(T);
        if (_handlers.TryGetValue(type, out var existing))
        {
            var current = (Action<T>)existing;
            if (Array.IndexOf(current.GetInvocationList(), (Delegate)handler) >= 0)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                GameLogger.LogWarning(ELogCategory.System,
                    $"[EventManager] 중복 Subscribe 무시: {type.Name} / {handler.Method.Name}");
#endif
                return new Subscription<T>(this, handler);
            }

            _handlers[type] = current + handler;
        }
        else
        {
            _handlers[type] = handler;
        }

        return new Subscription<T>(this, handler);
    }

    public IDisposableEvent SubscribeOnce<T>(Action<T> handler) where T : struct
    {
        if (handler == null) return null;

        Action<T> wrapper = null;
        wrapper = (evt) =>
        {
            Unsubscribe(wrapper);
            handler(evt);
        };

        return Subscribe(wrapper);
    }

    public void Unsubscribe<T>(Action<T> handler) where T : struct
    {
        if (handler == null) return;

        var type = typeof(T);
        if (!_handlers.TryGetValue(type, out var existing)) return;

        var updated = (Action<T>)existing - handler;
        if (updated == null)
            _handlers.Remove(type);
        else
            _handlers[type] = updated;
    }

    // ─────────────────────────────────────────────
    // 발행 (핫패스)
    // ─────────────────────────────────────────────

    public void Publish<T>(T evt) where T : struct
    {
        // 무한 연쇄 방어: 핸들러 안에서 다시 Publish가 누적되면 깊이 증가
        if (_publishDepth >= MAX_PUBLISH_DEPTH)
        {
            GameLogger.LogError(ELogCategory.System,
                $"[EventManager] 발행 깊이 {MAX_PUBLISH_DEPTH} 초과 → 무한 연쇄 의심. " +
                $"{typeof(T).Name} 발행 중단. (이벤트 핸들러가 서로를 트리거하는지 확인)");
            return;
        }

        if (!_handlers.TryGetValue(typeof(T), out var del)) return;

        var action = del as Action<T>;
        if (action == null) return;

        _publishDepth++;
        try
        {
            var list = action.GetInvocationList();
            if (list.Length == 1)
            {
                InvokeSafe(action, evt);
            }
            else
            {
                // 스냅샷 순회 → 핸들러 안에서 구독 변경돼도 이번 발행은 안전
                for (int i = 0; i < list.Length; i++)
                    InvokeSafe((Action<T>)list[i], evt);
            }
        }
        finally
        {
            _publishDepth--;
        }
    }

    private static void InvokeSafe<T>(Action<T> handler, T evt) where T : struct
    {
        try
        {
            handler(evt);
        }
        catch (Exception e)
        {
            GameLogger.LogError(ELogCategory.System,
                $"[EventManager] {typeof(T).Name} 핸들러 예외({handler.Method?.Name}): {e}");
        }
    }

    public bool HasSubscribers<T>() where T : struct
        => _handlers.ContainsKey(typeof(T));

    public void ClearAll()
    {
        _handlers.Clear();
        _publishDepth = 0;
    }

    // ─────────────────────────────────────────────
    // 구독 핸들
    // ─────────────────────────────────────────────

    private class Subscription<T> : IDisposableEvent where T : struct
    {
        private EventManager _manager;
        private Action<T> _handler;

        public Subscription(EventManager manager, Action<T> handler)
        {
            _manager = manager;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_manager != null && _handler != null)
                _manager.Unsubscribe(_handler);
            _manager = null;
            _handler = null;
        }
    }
}