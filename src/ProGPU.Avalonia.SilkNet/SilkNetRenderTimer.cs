using System;
using System.Diagnostics;
using Avalonia.Rendering;
using Avalonia.Threading;

namespace Avalonia.SilkNet
{
    /// <summary>
    /// Runs the Avalonia render loop on the Silk.NET UI dispatcher.
    /// </summary>
    internal sealed class SilkNetRenderTimer : DefaultRenderTimer
    {
        private static readonly TimeSpan s_minimumDelay =
            TimeSpan.FromMilliseconds(1);
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public SilkNetRenderTimer(int framesPerSecond)
            : base(framesPerSecond)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
            Interval = TimeSpan.FromSeconds(1.0 / framesPerSecond);
        }

        internal TimeSpan Interval { get; }

        public override bool RunsInBackground => false;

        protected override IDisposable StartCore(Action<TimeSpan> tick)
        {
            return new PhaseLockedSubscription(
                _clock,
                tick,
                Interval);
        }

        private sealed class PhaseLockedSubscription : IDisposable
        {
            private readonly Stopwatch _clock;
            private readonly Action<TimeSpan> _tick;
            private readonly long _periodTicks;
            private readonly DispatcherTimer _timer;
            private long _nextDeadlineTicks;

            public PhaseLockedSubscription(
                Stopwatch clock,
                Action<TimeSpan> tick,
                TimeSpan interval)
            {
                _clock = clock;
                _tick = tick;
                _periodTicks = Math.Max(
                    1,
                    (long)Math.Round(
                        interval.TotalSeconds * Stopwatch.Frequency));
                _nextDeadlineTicks = clock.ElapsedTicks + _periodTicks;
                _timer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = interval
                };
                _timer.Tick += OnTimerTick;
                _timer.Start();
            }

            private void OnTimerTick(object? sender, EventArgs args)
            {
                _tick(_clock.Elapsed);

                long afterTick = _clock.ElapsedTicks;
                do
                {
                    _nextDeadlineTicks += _periodTicks;
                }
                while (_nextDeadlineTicks <= afterTick);

                double remainingSeconds =
                    (double)(_nextDeadlineTicks - afterTick) /
                    Stopwatch.Frequency;
                _timer.Interval = TimeSpan.FromSeconds(
                    Math.Max(
                        s_minimumDelay.TotalSeconds,
                        remainingSeconds));
            }

            public void Dispose()
            {
                _timer.Stop();
                _timer.Tick -= OnTimerTick;
            }
        }
    }
}
