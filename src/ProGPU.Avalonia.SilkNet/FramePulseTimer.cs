using System;
using Avalonia.Rendering;

namespace Avalonia.SilkNet;

/// <summary>
/// Converts the native window render cadence into Avalonia render-loop ticks.
/// </summary>
/// <remarks>
/// The callback runs on the UI/native event-loop thread. A pulse costs O(1)
/// time and allocates no managed memory.
/// </remarks>
public sealed class SilkNetRenderTimer : IRenderTimer
{
    private readonly long _periodTicks;
    private long _nextTick;
    private Action<TimeSpan>? _tick;

    public SilkNetRenderTimer(int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        FramesPerSecond = framesPerSecond;
        Interval = TimeSpan.FromSeconds(1.0 / framesPerSecond);
        _periodTicks = Math.Max(
            1,
            (long)Math.Round(
                TimeSpan.TicksPerSecond / (double)framesPerSecond));
    }

    public int FramesPerSecond { get; }

    public TimeSpan Interval { get; }

    public bool RunsInBackground => false;

#if AVALONIA11
    public event Action<TimeSpan> Tick
    {
        add
        {
            _tick += value;
            _nextTick = 0;
        }
        remove => _tick -= value;
    }
#else
    public Action<TimeSpan>? Tick
    {
        get => _tick;
        set
        {
            _tick = value;
            _nextTick = 0;
        }
    }
#endif

    internal void Pulse(long nowTicks)
    {
        Action<TimeSpan>? callback = _tick;
        if (callback is null)
            return;

        if (_nextTick == 0)
            _nextTick = nowTicks;
        if (nowTicks < _nextTick)
            return;

        callback(TimeSpan.FromTicks(nowTicks));

        long next = _nextTick + _periodTicks;
        if (next <= nowTicks)
        {
            long missedPeriods =
                (nowTicks - next) / _periodTicks + 1;
            next += missedPeriods * _periodTicks;
        }

        _nextTick = next;
    }
}
