using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static partial class Program
{
    private static void CaptureImageSamplerAnimations(string directory, string commit, bool unavailable, Stopwatch timer)
    {
        double started = timer.Elapsed.TotalMilliseconds;
        // Original authored bitmap and existing ProGPU-owned identity bytecode;
        // neither changes during the entire same-owner sequence.
        byte[] sourcePixels = [0, 0, 255, 255, 0, 255, 0, 255];
        var scene = new AnimatedSamplerScene(sourcePixels, 1, new(0, 0, 1, 1), new(0, 0, 1, 1));
        SaveSamplerBitmap(directory, "sampler-animation.source", scene.Bitmap, sourcePixels);
        using var clocks = new SamplerAnimationClocks(timer);
        var observations = new List<object>();
        var failures = new List<string>();
        var prior = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (SamplerAnimationState state in SamplerAnimationStates())
            {
                CheckSamplerAnimationDeadline(timer);
                switch (state.Name)
                {
                    case "sampler-animation-opacity":
                        clocks.Seek(2);
                        scene.Brush.ApplyAnimationClock(Brush.OpacityProperty, clocks.Opacity);
                        break;
                    case "sampler-animation-viewport":
                        scene.Brush.ApplyAnimationClock(TileBrush.ViewportProperty, clocks.Viewport);
                        break;
                    case "sampler-animation-viewbox":
                        scene.Brush.ApplyAnimationClock(TileBrush.ViewboxProperty, clocks.Viewbox);
                        break;
                    case "sampler-animation-seek-origin":
                        clocks.Seek(0);
                        break;
                    case "sampler-animation-hidden-base-update":
                        scene.Brush.Opacity = .25;
                        break;
                    case "sampler-animation-detached-base":
                    case "sampler-animation-detached-again":
                        scene.RemoveAnimations();
                        break;
                    case "sampler-animation-reattached":
                        scene.Brush.ApplyAnimationClock(Brush.OpacityProperty, clocks.Opacity);
                        scene.Brush.ApplyAnimationClock(TileBrush.ViewportProperty, clocks.Viewport);
                        scene.Brush.ApplyAnimationClock(TileBrush.ViewboxProperty, clocks.Viewbox);
                        clocks.Seek(2);
                        break;
                }

                var input = new
                {
                    state, Current = scene.Describe(state, clocks),
                    ShaderWords = SamplerWords, ShaderRenderMode = "SoftwareOnly", SamplerRegister = 0,
                    SamplingMode = "NearestNeighbor", ActualVisualBitmapScalingMode = "NearestNeighbor",
                    ImagePixelWidth = 2, ImagePixelHeight = 1, ImageDpiX = 96, ImageDpiY = 96,
                    ImagePixelFormat = "Pbgra32", SourcePixels = sourcePixels,
                    SourcePixelSha256 = Convert.ToHexString(SHA256.HashData(sourcePixels)),
                    Bounds = new Rect(8, 10, 32, 24), Clip = new Rect(8, 10, 32, 24),
                    ViewportUnits = "RelativeToBoundingBox", ViewboxUnits = "RelativeToBoundingBox",
                    Stretch = "Fill", TileMode = "None", AlignmentX = "Center", AlignmentY = "Center"
                };
                using (var file = new FileStream(Path.Combine(directory, state.Name + ".input.json"), FileMode.CreateNew))
                    JsonSerializer.Serialize(file, input, new JsonSerializerOptions { WriteIndented = true });
                byte[]? first = null;
                var replays = new List<object>();
                for (int replay = 0; replay < 3; ++replay)
                {
                    CheckSamplerAnimationDeadline(timer);
                    object before = scene.Describe(state, clocks);
                    AnimatedSamplerScene current = replay < 2 ? scene :
                        new AnimatedSamplerScene(sourcePixels, state.Opacity, state.Viewport, state.Viewbox);
                    current.AssertIdentity(sourcePixels);
                    var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(current.Root);
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    if (Volatile.Read(ref invalidShaders) != 0)
                        throw new InvalidOperationException($"Original WPF rejected {state.Name}.");
                    object after = scene.Describe(state, clocks);
                    current.AssertIdentity(sourcePixels);
                    var pixels = new byte[64 * 64 * 4];
                    bitmap.CopyPixels(pixels, 64 * 4, 0);
                    SaveSamplerBitmap(directory, state.Name + $".replay-{replay}", bitmap, pixels);
                    try { AssertSamplerAnimationPixels(state, pixels, unavailable); }
                    catch (InvalidOperationException error) { failures.Add($"Replay {replay}: {error.Message}"); }
                    if (first != null && !first.AsSpan().SequenceEqual(pixels))
                        failures.Add($"{state.Name}: same-owner/warm/independent-literal pixels differ.");
                    first ??= pixels;
                    replays.Add(new
                    {
                        Replay = replay, SameSourceObjects = replay < 2, IndependentLiteralControl = replay == 2,
                        Before = before, After = after, Pixels = pixels,
                        PixelSha256 = Convert.ToHexString(SHA256.HashData(pixels))
                    });
                }
                if (state.EquivalentTo != null && !prior[state.EquivalentTo].AsSpan().SequenceEqual(first))
                    failures.Add($"{state.Name}: restoration differs from {state.EquivalentTo}.");
                prior.Add(state.Name, first!);
                observations.Add(new { state.Name, Input = input, Replays = replays });
            }
        }
        finally { scene.RemoveAnimations(); }

        if (observations.Count != 9) throw new InvalidOperationException("Original animated sampler inventory is incomplete.");
        CheckSamplerAnimationDeadline(timer);
        var receipt = new
        {
            Schema = 1, SourceCommit = commit, CaseFamily = "image-brush-animation-current-values",
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PresentationCore = FileIdentity(typeof(ShaderEffect).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location),
            CaseCount = observations.Count, Replays = 27, Cases = observations, Failures = failures,
            CaptureMode = unavailable ? "unsupported-software-control" : "shader-pixels",
            QualifiedShaderCases = unavailable || failures.Count != 0 ? 0 : observations.Count,
            ClockContract = new
            {
                DurationTicks = TimeSpan.FromSeconds(4).Ticks, ControllableRoot = "ParallelTimeline",
                OpacityFrom = 1, OpacityTo = 0,
                ViewportFrom = new Rect(0, 0, 1, 1), ViewportTo = new Rect(.5, 0, 0, 1),
                ViewboxFrom = new Rect(0, 0, 1, 1), ViewboxTo = new Rect(1, 0, 0, 1),
                FillBehavior = "HoldEnd", HandoffBehavior = "SnapshotAndReplace",
                TimeControl = "Observe paused active root; SeekAlignedToLastTick; exact before/after time and current-value assertions."
            },
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds - started,
            Qualification = unavailable
                ? "Original ARM64 unavailable-software control only; zero animated sampler shader cases qualified."
                : "Original Microsoft WPF SoftwareOnly animation/current-value controls only. Not hardware, native provider, source-host or package qualification."
        };
        using (var file = new FileStream(Path.Combine(directory, failures.Count == 0 ?
            "image-sampler-animations.json" : "image-sampler-animations.failed.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine($"Original animated ImageBrush: 9 states / 27 replays; {(unavailable ? 0 : 9)} source shader cases qualified.");
    }

    private sealed record SamplerAnimationState(string Name, double Opacity, Rect Viewport, Rect Viewbox,
        int AnimatedProperties, int ClockSeconds, double BaseOpacity, string? EquivalentTo = null);

    private static IEnumerable<SamplerAnimationState> SamplerAnimationStates()
    {
        // Literal current-value oracles are independent of WPF animation evaluation.
        yield return new("sampler-animation-baseline", 1, new(0, 0, 1, 1), new(0, 0, 1, 1), 0, 0, 1);
        yield return new("sampler-animation-opacity", .5, new(0, 0, 1, 1), new(0, 0, 1, 1), 1, 2, 1);
        yield return new("sampler-animation-viewport", .5, new(.25, 0, .5, 1), new(0, 0, 1, 1), 3, 2, 1);
        yield return new("sampler-animation-viewbox", .5, new(.25, 0, .5, 1), new(.5, 0, .5, 1), 7, 2, 1);
        yield return new("sampler-animation-seek-origin", 1, new(0, 0, 1, 1), new(0, 0, 1, 1), 7, 0, 1,
            "sampler-animation-baseline");
        yield return new("sampler-animation-hidden-base-update", 1, new(0, 0, 1, 1), new(0, 0, 1, 1), 7, 0, .25,
            "sampler-animation-baseline");
        yield return new("sampler-animation-detached-base", .25, new(0, 0, 1, 1), new(0, 0, 1, 1), 0, 0, .25);
        yield return new("sampler-animation-reattached", .5, new(.25, 0, .5, 1), new(.5, 0, .5, 1), 7, 2, .25,
            "sampler-animation-viewbox");
        yield return new("sampler-animation-detached-again", .25, new(0, 0, 1, 1), new(0, 0, 1, 1), 0, 2, .25,
            "sampler-animation-detached-base");
    }

    private static void AssertSamplerAnimationPixels(SamplerAnimationState state, byte[] pixels, bool unavailable)
    {
        // Bounded independent nearest-neighbor mapping: one two-texel source,
        // integral viewport edges, no repeated filtering or effect-phase inference.
        double left = 8 + 32 * state.Viewport.X, right = left + 32 * state.Viewport.Width;
        byte opacity = state.Opacity switch { 1 => 255, .5 => 128, .25 => 64,
            _ => throw new InvalidOperationException("Unspecified independent opacity oracle.") };
        for (int y = 0; y < 64; ++y)
        for (int x = 0; x < 64; ++x)
        {
            bool inside = x >= 8 && x < 40 && y >= 10 && y < 34 && x + .5 >= left && x + .5 < right;
            double u = state.Viewbox.X + (x + .5 - left) / (right - left) * state.Viewbox.Width;
            int selected = u < .5 ? 2 : 1;
            for (int channel = 0; channel < 4; ++channel)
            {
                byte expected = channel == 3 ? (byte)255 :
                    !unavailable && inside && channel == selected ? opacity : (byte)0;
                byte actual = pixels[(y * 64 + x) * 4 + channel];
                if (actual != expected)
                    throw new InvalidOperationException($"{state.Name}: ({x},{y}) BGRA[{channel}]={actual}, expected {expected}.");
            }
        }
    }

    private static void CheckSamplerAnimationDeadline(Stopwatch timer)
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(60))
            throw new TimeoutException("Original animated sampler reference exceeded the shared 60-second deadline.");
    }

    private sealed class SamplerAnimationClocks : IDisposable
    {
        private readonly ClockGroup root;
        private readonly ClockController controller;
        private int timingNotifications;
        public AnimationClock Opacity => (AnimationClock)root.Children[0];
        public AnimationClock Viewport => (AnimationClock)root.Children[1];
        public AnimationClock Viewbox => (AnimationClock)root.Children[2];

        public SamplerAnimationClocks(Stopwatch timer)
        {
            var duration = new Duration(TimeSpan.FromSeconds(4));
            var timeline = new ParallelTimeline { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
            timeline.Children.Add(new DoubleAnimation(1, 0, duration));
            timeline.Children.Add(new RectAnimation(new(0, 0, 1, 1), new(.5, 0, 0, 1), duration));
            timeline.Children.Add(new RectAnimation(new(0, 0, 1, 1), new(1, 0, 0, 1), duration));
            root = (ClockGroup)timeline.CreateClock(hasControllableRoot: true);
            controller = root.Controller ?? throw new InvalidOperationException("Original clock is not controllable.");
            // Until the first animation is applied, the public timing event is
            // the clock's only consumer. An unattached, unobserved clock does not
            // progress merely because a DispatcherTimer reads its properties.
            root.CurrentTimeInvalidated += OnTimeInvalidated;
            try
            {
                controller.Begin();
                controller.Pause();
                // Pause is processed by a real timing tick. Observe its public state,
                // never assume a sleep/drain implies that it happened. The watchdog
                // cannot extend the caller's original 60-second overall deadline.
                var frame = new DispatcherFrame();
                Exception? failure = null;
                var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
                EventHandler tick = (_, _) =>
                {
                    try
                    {
                        CheckSamplerAnimationDeadline(timer);
                        if (root.IsPaused && root.CurrentState == ClockState.Active) frame.Continue = false;
                    }
                    catch (Exception error) { failure = error; frame.Continue = false; }
                };
                poll.Tick += tick;
                try { poll.Start(); Dispatcher.PushFrame(frame); }
                finally { poll.Stop(); poll.Tick -= tick; }
                if (failure != null) throw failure;
                Seek(0);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Seek(int seconds) => controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(seconds), TimeSeekOrigin.BeginTime);

        public object Describe(int seconds)
        {
            TimeSpan expected = TimeSpan.FromSeconds(seconds);
            if (timingNotifications == 0 || !root.IsPaused || root.CurrentState != ClockState.Active || root.CurrentTime != expected ||
                root.CurrentProgress != seconds / 4.0 || root.CurrentGlobalSpeed != 0 ||
                Opacity.CurrentTime != expected || Viewport.CurrentTime != expected || Viewbox.CurrentTime != expected)
                throw new InvalidOperationException("Original clock time moved or was not the requested paused source time.");
            return new { TimingNotifications = timingNotifications,
                root.IsPaused, State = root.CurrentState.ToString(), CurrentTimeTicks = root.CurrentTime?.Ticks,
                root.CurrentProgress, root.CurrentGlobalSpeed, OpacityTicks = Opacity.CurrentTime?.Ticks,
                ViewportTicks = Viewport.CurrentTime?.Ticks, ViewboxTicks = Viewbox.CurrentTime?.Ticks };
        }

        private void OnTimeInvalidated(object? sender, EventArgs args) => ++timingNotifications;

        public void Dispose()
        {
            root.CurrentTimeInvalidated -= OnTimeInvalidated;
            controller.Remove();
        }
    }

    private sealed class AnimatedSamplerScene
    {
        public BitmapSource Bitmap { get; }
        public ImageBrush Brush { get; }
        public ContainerVisual Root { get; }
        private readonly SamplerDrawingVisual source;
        private readonly AnimatedSamplerEffect effect;
        private readonly PixelShader shader;

        public AnimatedSamplerScene(byte[] pixels, double opacity, Rect viewport, Rect viewbox)
        {
            Bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Pbgra32, null, pixels, 8);
            Bitmap.Freeze();
            Brush = new ImageBrush(Bitmap)
            {
                Opacity = opacity, Viewport = viewport, Viewbox = viewbox,
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Stretch = Stretch.Fill, TileMode = TileMode.None, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center
            };
            effect = new AnimatedSamplerEffect(Brush);
            shader = effect.Shader;
            Root = new ContainerVisual();
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.Black, null, new(0, 0, 64, 64));
            Root.Children.Add(background);
            var clip = new SamplerContainerVisual(nearest: true) { Clip = new RectangleGeometry(new(8, 10, 32, 24)) };
            source = new SamplerDrawingVisual(nearest: true) { Effect = effect };
            using (var drawing = source.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new(8, 10, 32, 24));
            clip.Children.Add(source);
            Root.Children.Add(clip);
        }

        public void RemoveAnimations()
        {
            Brush.ApplyAnimationClock(System.Windows.Media.Brush.OpacityProperty, null);
            Brush.ApplyAnimationClock(TileBrush.ViewportProperty, null);
            Brush.ApplyAnimationClock(TileBrush.ViewboxProperty, null);
        }

        public void AssertIdentity(byte[] expectedPixels)
        {
            var pixels = new byte[8];
            Bitmap.CopyPixels(pixels, 8, 0);
            if (!ReferenceEquals(Brush.ImageSource, Bitmap) || !ReferenceEquals(source.Effect, effect) ||
                !ReferenceEquals(effect.InputBrush, Brush) || !ReferenceEquals(effect.Shader, shader) ||
                shader.ShaderRenderMode != ShaderRenderMode.SoftwareOnly || !Bitmap.IsFrozen ||
                Bitmap.PixelWidth != 2 || Bitmap.PixelHeight != 1 || Bitmap.DpiX != 96 || Bitmap.DpiY != 96 ||
                !pixels.AsSpan().SequenceEqual(expectedPixels) || source.EmittedScalingMode != BitmapScalingMode.NearestNeighbor)
                throw new InvalidOperationException("Original animated sampler changed source bitmap/shader/brush/visual identity or policy.");
        }

        public object Describe(SamplerAnimationState state, SamplerAnimationClocks clocks)
        {
            object timing = clocks.Describe(state.ClockSeconds);
            int animated = (DependencyPropertyHelper.GetValueSource(Brush, System.Windows.Media.Brush.OpacityProperty).IsAnimated ? 1 : 0) |
                (DependencyPropertyHelper.GetValueSource(Brush, TileBrush.ViewportProperty).IsAnimated ? 2 : 0) |
                (DependencyPropertyHelper.GetValueSource(Brush, TileBrush.ViewboxProperty).IsAnimated ? 4 : 0);
            double baseOpacity = (double)Brush.GetAnimationBaseValue(System.Windows.Media.Brush.OpacityProperty);
            Rect baseViewport = (Rect)Brush.GetAnimationBaseValue(TileBrush.ViewportProperty);
            Rect baseViewbox = (Rect)Brush.GetAnimationBaseValue(TileBrush.ViewboxProperty);
            if (animated != state.AnimatedProperties || Brush.HasAnimatedProperties != (animated != 0) ||
                Bits(Brush.Opacity) != Bits(state.Opacity) || Bits(baseOpacity) != Bits(state.BaseOpacity) ||
                !RectBits(Brush.Viewport).SequenceEqual(RectBits(state.Viewport)) ||
                !RectBits(Brush.Viewbox).SequenceEqual(RectBits(state.Viewbox)) ||
                !RectBits(baseViewport).SequenceEqual(RectBits(new(0, 0, 1, 1))) ||
                !RectBits(baseViewbox).SequenceEqual(RectBits(new(0, 0, 1, 1))))
                throw new InvalidOperationException($"{state.Name}: original current/base values or actual animation attachment differ.");
            return new { Clock = timing, AnimatedProperties = animated, Brush.HasAnimatedProperties,
                OpacityBits = Bits(Brush.Opacity), ViewportBits = RectBits(Brush.Viewport), ViewboxBits = RectBits(Brush.Viewbox),
                BaseOpacityBits = Bits(baseOpacity), BaseViewportBits = RectBits(baseViewport), BaseViewboxBits = RectBits(baseViewbox) };
        }

        private static string Bits(double value) => BitConverter.DoubleToUInt64Bits(value).ToString("X16");
        private static string[] RectBits(Rect value) => [Bits(value.X), Bits(value.Y), Bits(value.Width), Bits(value.Height)];
    }

    private sealed class AnimatedSamplerEffect : ShaderEffect
    {
        private static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
            "Input", typeof(AnimatedSamplerEffect), 0, SamplingMode.NearestNeighbor);
        public ImageBrush InputBrush => (ImageBrush)GetValue(InputProperty);
        public PixelShader Shader => PixelShader;

        public AnimatedSamplerEffect(ImageBrush brush)
        {
            // Reuse the original ProGPU-owned sampler bytecode from ImageSamplers.cs.
            byte[] bytes = new byte[SamplerWords.Length * 4];
            for (int i = 0; i < SamplerWords.Length; ++i)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), SamplerWords[i]);
            var shader = new PixelShader { ShaderRenderMode = ShaderRenderMode.SoftwareOnly };
            using (var stream = new MemoryStream(bytes, writable: false)) shader.SetStreamSource(stream);
            PixelShader = shader;
            SetValue(InputProperty, brush);
            UpdateShaderValue(InputProperty);
        }

        protected override Freezable CreateInstanceCore() => new AnimatedSamplerEffect(InputBrush);
    }
}
