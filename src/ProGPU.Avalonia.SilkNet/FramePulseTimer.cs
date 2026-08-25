using System;
using System.Threading;
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
    private long _periodTicks;
    private int _framesPerSecond;
    private long _nextTick;
    private Action<TimeSpan>? _tick;

    public SilkNetRenderTimer(int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        UpdateFramesPerSecond(framesPerSecond);
    }

    public int FramesPerSecond =>
        Volatile.Read(ref _framesPerSecond);

    public TimeSpan Interval =>
        TimeSpan.FromSeconds(1.0 / FramesPerSecond);

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

        long periodTicks = Volatile.Read(ref _periodTicks);
        long next = _nextTick + periodTicks;
        if (next <= nowTicks)
        {
            long missedPeriods =
                (nowTicks - next) / periodTicks + 1;
            next += missedPeriods * periodTicks;
        }

        _nextTick = next;
    }

    internal void UpdateFramesPerSecond(int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            framesPerSecond);
        Volatile.Write(ref _framesPerSecond, framesPerSecond);
        Volatile.Write(
            ref _periodTicks,
            Math.Max(
                1,
                (long)Math.Round(
                    TimeSpan.TicksPerSecond /
                    (double)framesPerSecond)));
        Interlocked.Exchange(ref _nextTick, 0);
    }
}
