namespace PerfMonitor.Desktop;

/// <summary>Retains one latest state and schedules at most one UI callback.</summary>
public sealed class LatestStateRenderer<T> where T : class
{
    private readonly object _gate = new();
    private readonly Action<Action> _schedule;
    private readonly Action<T> _render;
    private T? _latest;
    private bool _scheduled;
    private bool _enabled;
    private bool _closed;
    private bool _dirty;

    public LatestStateRenderer(Action<Action> schedule, Action<T> render)
    {
        _schedule = schedule;
        _render = render;
    }

    public void Publish(T state)
    {
        lock (_gate)
        {
            if (_closed) return;
            _latest = state;
            _dirty = true;
            ScheduleIfNeeded();
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (enabled && !_enabled && _latest is not null) _dirty = true;
            _enabled = enabled;
            ScheduleIfNeeded();
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _latest = null;
        }
    }

    // The scheduler must enqueue the callback; it must not run it inline.
    private void ScheduleIfNeeded()
    {
        if (_closed || !_enabled || _scheduled || !_dirty || _latest is null) return;
        _scheduled = true;
        _schedule(Drain);
    }

    private void Drain()
    {
        T? state;
        lock (_gate)
        {
            state = !_closed && _enabled ? _latest : null;
            if (state is not null) _dirty = false;
        }

        try
        {
            if (state is not null) _render(state);
        }
        finally
        {
            lock (_gate)
            {
                _scheduled = false;
                ScheduleIfNeeded();
            }
        }
    }
}
