using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;

namespace Avalonia.SilkNet;

internal interface ISilkNetLoopParticipant
{
    bool IsLoopVisible { get; }
    bool IsLoopInitialized { get; }
    void PollNativeEvents();
    void UpdateNativeWindow();
    void RenderNativeWindow();
}

/// <summary>
/// Owns one UI-thread event loop for every Silk window in an Avalonia
/// application.
/// </summary>
/// <remarks>
/// Each loop iteration is O(W), where W is the number of live windows, and
/// uses a reusable snapshot array. No per-frame managed collection is
/// allocated.
/// </remarks>
internal sealed class SilkNetEventLoop : IControlledDispatcherImpl
{
    private readonly object _gate = new();
    private readonly Thread _thread = Thread.CurrentThread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly List<ISilkNetLoopParticipant> _windows = [];
    private ISilkNetLoopParticipant[] _snapshot = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long _framePeriodTicks;
    private bool _signalPending;
    private long? _timerDueMilliseconds;

    internal SilkNetEventLoop(int framesPerSecond)
    {
        _framePeriodTicks = Math.Max(
            1,
            Stopwatch.Frequency / framesPerSecond);
    }

    public bool CurrentThreadIsLoopThread =>
        Thread.CurrentThread == _thread;

    public bool CanQueryPendingInput => true;

    public bool HasPendingInput => false;

    public long Now => _clock.ElapsedMilliseconds;

    public event Action? Signaled;
    public event Action? Timer;

    public void Signal()
    {
        lock (_gate)
            _signalPending = true;
        _wake.Set();
    }

    public void UpdateTimer(long? dueTimeInMs)
    {
        lock (_gate)
            _timerDueMilliseconds = dueTimeInMs;
        _wake.Set();
    }

    public void RunLoop(CancellationToken token)
    {
        if (!CurrentThreadIsLoopThread)
        {
            throw new InvalidOperationException(
                "The Silk.NET event loop must run on its creating thread.");
        }

        using CancellationTokenRegistration cancellation =
            token.Register(static state => ((AutoResetEvent)state!).Set(), _wake);
        long nextFrame = Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested)
        {
            DispatchAvaloniaWork();
            DispatchAvaloniaTimer();

            int windowCount = CaptureWindows();
            if (windowCount == 0)
            {
                _wake.WaitOne(GetWaitMilliseconds(nextFrame));
                nextFrame = Stopwatch.GetTimestamp();
                continue;
            }

            ISilkNetLoopParticipant first = _snapshot[0];
            if (first.IsLoopInitialized)
                first.PollNativeEvents();

            long now = Stopwatch.GetTimestamp();
            if (now >= nextFrame)
            {
                for (int index = 0; index < windowCount; index++)
                {
                    ISilkNetLoopParticipant window = _snapshot[index];
                    if (!window.IsLoopInitialized)
                        continue;
                    window.UpdateNativeWindow();
                    if (window.IsLoopVisible)
                        window.RenderNativeWindow();
                }

                do
                    nextFrame += _framePeriodTicks;
                while (nextFrame <= now);
            }

            _wake.WaitOne(GetWaitMilliseconds(nextFrame));
        }
    }

    internal void Register(ISilkNetLoopParticipant window)
    {
        lock (_gate)
        {
            if (!_windows.Contains(window))
                _windows.Add(window);
        }
        _wake.Set();
    }

    internal void Unregister(ISilkNetLoopParticipant window)
    {
        lock (_gate)
            _windows.Remove(window);
        _wake.Set();
    }

    internal void Wake() => _wake.Set();

    private int CaptureWindows()
    {
        lock (_gate)
        {
            int count = _windows.Count;
            if (_snapshot.Length < count)
                _snapshot = new ISilkNetLoopParticipant[count];
            _windows.CopyTo(_snapshot);
            return count;
        }
    }

    private void DispatchAvaloniaWork()
    {
        bool signal;
        lock (_gate)
        {
            signal = _signalPending;
            _signalPending = false;
        }

        if (signal)
            Signaled?.Invoke();
    }

    private void DispatchAvaloniaTimer()
    {
        bool due;
        lock (_gate)
        {
            due = _timerDueMilliseconds is { } timer &&
                  timer <= _clock.ElapsedMilliseconds;
            if (due)
                _timerDueMilliseconds = null;
        }

        if (due)
            Timer?.Invoke();
    }

    private int GetWaitMilliseconds(long nextFrame)
    {
        long now = Stopwatch.GetTimestamp();
        long frameTicks = Math.Max(0, nextFrame - now);
        int wait = (int)Math.Clamp(
            frameTicks * 1000 / Stopwatch.Frequency,
            0,
            16);
        lock (_gate)
        {
            if (_timerDueMilliseconds is { } timer)
            {
                wait = Math.Min(
                    wait,
                    (int)Math.Clamp(
                        timer - _clock.ElapsedMilliseconds,
                        0,
                        16));
            }
        }

        return wait;
    }
}
