using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static partial class Program
{
    private sealed record BitmapCacheSamplerState(int Index, string Name)
    {
        public bool TargetCache => Index >= 1;
        public bool ExplicitCache => Index >= 2;
        public bool IgnoredRootState => Index >= 3;
        public bool Attached => Index != 7;
        public bool Blue => Index >= 8;
        public bool HasGroup => Index != 10;
        public bool HasLeaves => Index != 9 && Index != 10;
        public bool EmptyTarget => !HasLeaves;
        public double ExplicitScale => Index == 6 ? 0 : 1;
        public double InnerOpacity => Index == 4 ? .5 : 1;
        public double BrushOpacity => Index == 4 ? .5 : 1;
        public Rect FirstRectangle => Index >= 8 ? new(-4, 4, 8, 12) : new(4, 6, 8, 12);
        public Rect SecondRectangle => Index >= 8 ? new(4, 4, 8, 12) : new(12, 6, 8, 12);
        public Rect? InnerClip => Index == 4 ? new Rect(12, 6, 8, 12) : null;
        public double RelativeX => Index == 5 ? .25 : 0;
        public double AbsoluteY => Index == 5 ? 2 : 0;
    }

    private static IEnumerable<BitmapCacheSamplerState> BitmapCacheSamplerStates()
    {
        yield return new(0, "cache-sampler-default-cache");
        yield return new(1, "cache-sampler-target-cache");
        yield return new(2, "cache-sampler-explicit-cache");
        yield return new(3, "cache-sampler-ignored-root-state");
        yield return new(4, "cache-sampler-descendant-clip-opacity");
        yield return new(5, "cache-sampler-relative-absolute-mapping");
        yield return new(6, "cache-sampler-zero-scale");
        yield return new(7, "cache-sampler-null-target");
        yield return new(8, "cache-sampler-restored-negative-origin");
        yield return new(9, "cache-sampler-attached-empty-group");
        yield return new(10, "cache-sampler-attached-empty-root");
        yield return new(11, "cache-sampler-refilled-same-owners");
    }

    private static void CaptureBitmapCacheBrushSamplers(string directory, string commit, bool unavailable, Stopwatch timer)
    {
        uint systemDpiObservation = ObserveCacheSamplerSystemDpi();
        if (systemDpiObservation == 0)
            throw new InvalidOperationException("Original system DPI observation failed.");
        var observations = new List<object>();
        var failures = new List<string>();
        OriginalBitmapCacheSamplerScene? retained = null;
        byte[]? defaultPixels = null;
        byte[]? zeroScalePixels = null;
        byte[]? nullTargetPixels = null;
        byte[]? filledPixels = null;
        foreach (BitmapCacheSamplerState state in BitmapCacheSamplerStates())
        {
            CheckSamplerAnimationDeadline(timer);
            if (retained == null) retained = new(state);
            else retained.Advance(state);
            var input = new
            {
                State = state, Original = retained.Describe(state), ShaderWords = SamplerWords,
                ShaderRenderMode = "SoftwareOnly", SamplerRegister = 0, SamplingMode = "NearestNeighbor",
                SourceTree = "unparented ContainerVisual / inner ContainerVisual / two retained DrawingVisual leaves",
                SourceFrameworkElements = false, SourceReplacedByBitmap = false,
                ReceivingLocalBounds = new Rect(0, 0, 32, 24), ReceivingOffset = new Vector(8, 10),
                OutputClip = new Rect(8, 10, 32, 24), TargetDpi = 96,
                SamplerRealization = "selected raw cache texture sampled over normalized shader coordinates",
                ConsumerBrushOpacityAndTransforms = "ignored by the BitmapCacheBrush shader sampler",
                SystemDpiObservation = systemDpiObservation,
                DpiObservationScope = "current UI-thread GetDpiForSystem; not proof of WPF's historically cached primary DPI",
                ExpectedFirstArgb = state.Blue ? "FF0000FF" : "FFFF0000", ExpectedSecondArgb = "FF00FF00"
            };
            using (var file = new FileStream(Path.Combine(directory, state.Name + ".input.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(file, input, new JsonSerializerOptions { WriteIndented = true });
            byte[]? first = null;
            var replays = new List<object>();
            for (int replay = 0; replay < 3; ++replay)
            {
                CheckSamplerAnimationDeadline(timer);
                object retainedBefore = retained.Describe(state);
                OriginalBitmapCacheSamplerScene current = replay < 2 ? retained : new(state);
                object before = current.Describe(state);
                var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(current.Root);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                if (Volatile.Read(ref invalidShaders) != 0)
                    throw new InvalidOperationException($"Original WPF rejected {state.Name}.");
                object after = current.Describe(state);
                object retainedAfter = retained.Describe(state);
                var pixels = new byte[64 * 64 * 4];
                bitmap.CopyPixels(pixels, 64 * 4, 0);
                SaveSamplerBitmap(directory, state.Name + $".replay-{replay}", bitmap, pixels);
                try { AssertBitmapCacheSamplerPixels(state, pixels, unavailable); }
                catch (InvalidOperationException error) { failures.Add($"Replay {replay}: {error.Message}"); }
                if (first != null && !first.AsSpan().SequenceEqual(pixels))
                    failures.Add($"{state.Name}: retained/warm/independent-literal pixels differ.");
                first ??= pixels;
                replays.Add(new
                {
                    Replay = replay, SameSourceObjects = replay < 2, IndependentLiteralInstance = replay == 2,
                    Before = before, After = after, RetainedBefore = retainedBefore, RetainedAfter = retainedAfter,
                    Pixels = pixels, PixelSha256 = Convert.ToHexString(SHA256.HashData(pixels))
                });
            }
            if (state.Index == 0) defaultPixels = first;
            if (state.Index is >= 1 and <= 3 && (defaultPixels == null || !defaultPixels.AsSpan().SequenceEqual(first)))
                failures.Add($"{state.Name}: cache policy or excluded root state changed logical source output.");
            if (state.Index == 5 && (defaultPixels == null || !defaultPixels.AsSpan().SequenceEqual(first)))
                failures.Add("Consumer brush transforms changed the selected raw cache sampler output.");
            if (state.Index == 6) zeroScalePixels = first;
            if (state.Index == 7 && (zeroScalePixels == null || !zeroScalePixels.AsSpan().SequenceEqual(first)))
                failures.Add("The genuine null target differs from the independently retained zero-scale frame.");
            if (state.Index == 7) nullTargetPixels = first;
            if (state.Index == 8) filledPixels = first;
            if (state.EmptyTarget && (nullTargetPixels == null || !nullTargetPixels.AsSpan().SequenceEqual(first)))
                failures.Add($"{state.Name}: the genuine attached empty source differs from the independent null frame.");
            if (state.Index == 11 && (filledPixels == null || !filledPixels.AsSpan().SequenceEqual(first)))
                failures.Add("Refilling the same source owners did not restore their original nonempty frame.");
            observations.Add(new { state.Name, Input = input, Replays = replays });
        }
        if (observations.Count != 12) throw new InvalidOperationException("Original BitmapCacheBrush sampler inventory changed.");
        CheckSamplerAnimationDeadline(timer);
        var receipt = new
        {
            Schema = 2, SourceCommit = commit, CaseFamily = "owned-bitmap-cache-brush-shader-sampler",
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription,
            PresentationCore = FileIdentity(typeof(BitmapCacheBrush).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location),
            CaseCount = observations.Count, Replays = 36, Cases = observations, Failures = failures,
            SystemDpiObservation = systemDpiObservation,
            SamplerContract = "raw selected cache texture; consumer brush opacity and transforms excluded",
            RasterProfile = "literal integral-cache corpus; no fractional/near-integer, device-clamp or historical primary-DPI qualification",
            CaptureMode = unavailable ? "unsupported-software-control" : "shader-pixels",
            QualifiedShaderCases = unavailable || failures.Count != 0 ? 0 : observations.Count,
            Qualification = "Original Microsoft WPF SoftwareOnly BitmapCacheBrush controls only; no native/provider/package, generic-cache allocation, UIElement wrapper, cyclic-source or source-host qualification."
        };
        using (var file = new FileStream(Path.Combine(directory, failures.Count == 0 ?
            "bitmap-cache-brush-samplers.json" : "bitmap-cache-brush-samplers.failed.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine($"Original BitmapCacheBrush samplers: 12 states / 36 replays; {(unavailable ? 0 : 12)} shader cases qualified.");
    }

    private static void AssertBitmapCacheSamplerPixels(BitmapCacheSamplerState state, byte[] pixels, bool unavailable)
    {
        // Independent literal bands for the integral-cache profile. The raw
        // cache texture spans normalized shader coordinates; ordinary brush
        // placement and opacity do not apply. No product math or observed
        // original pixels supply the expected frame.
        for (int y = 0; y < 64; ++y) for (int x = 0; x < 64; ++x)
        {
            int selected = -1;
            byte component = state.Index == 4 ? (byte)128 : (byte)255;
            if (!unavailable && x >= 8 && x < 40 && y >= 10 && y < 34)
            {
                int column = x - 8;
                selected = state.Index switch
                {
                    0 or 1 or 2 or 3 or 5 => column < 16 ? 2 : 1,
                    4 => 1,
                    8 or 11 => column < 16 ? 0 : 1,
                    >= 0 and <= 11 => -1,
                    _ => throw new InvalidOperationException("Unknown independent BitmapCacheBrush sampler oracle.")
                };
            }
            for (int channel = 0; channel < 4; ++channel)
            {
                byte expected = channel == 3 ? (byte)255 : channel == selected ? component : (byte)0;
                byte actual = pixels[(y * 64 + x) * 4 + channel];
                if (actual != expected)
                    throw new InvalidOperationException($"{state.Name}: ({x},{y}) BGRA[{channel}]={actual}, expected {expected}.");
            }
        }
    }

    private sealed class OriginalBitmapCacheSamplerScene
    {
        private readonly ContainerVisual sampledRoot = new();
        private readonly ContainerVisual group = new();
        private readonly VisualSamplerDrawingVisual first = new();
        private readonly VisualSamplerDrawingVisual second = new();
        private readonly RectangleGeometry firstGeometry = new();
        private readonly RectangleGeometry secondGeometry = new();
        private readonly SolidColorBrush firstBrush = new(Colors.Red);
        private readonly SolidColorBrush secondBrush = new(Colors.Lime);
        private readonly BitmapCache targetCache = new(2);
        private readonly BitmapCache explicitCache = new(1) { SnapsToDevicePixels = true };
        private readonly BitmapCacheBrush brush = new();
        private readonly TranslateTransform relativeTransform = new();
        private readonly TranslateTransform absoluteTransform = new();
        private readonly TranslateTransform rootTransform = new(29, 31);
        private readonly RectangleGeometry rootClip = new(new Rect(0, 0, 1, 1));
        private readonly BlurEffect rootEffect = new() { Radius = 3 };
        private readonly SolidColorBrush rootMask = new(Colors.Transparent);
        private readonly RectangleGeometry groupClip = new();
        private readonly OriginalBitmapCacheSamplerEffect effect;
        private readonly PixelShader shader;
        private readonly VisualSamplerDrawingVisual receiver = new() { Offset = new(8, 10) };
        private readonly SamplerContainerVisual outputClip = new(nearest: true);
        private readonly RectangleGeometry outputClipGeometry = new(new Rect(8, 10, 32, 24));
        private int index;
        public ContainerVisual Root { get; } = new();

        internal OriginalBitmapCacheSamplerScene(BitmapCacheSamplerState state)
        {
            using (DrawingContext drawing = first.RenderOpen()) drawing.DrawGeometry(firstBrush, null, firstGeometry);
            using (DrawingContext drawing = second.RenderOpen()) drawing.DrawGeometry(secondBrush, null, secondGeometry);
            group.Children.Add(first);
            group.Children.Add(second);
            sampledRoot.Children.Add(group);
            brush.RelativeTransform = relativeTransform;
            brush.Transform = absoluteTransform;
            Apply(state);
            effect = new(brush);
            shader = effect.Shader;
            receiver.Effect = effect;
            using (DrawingContext drawing = receiver.RenderOpen())
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 32, 24));
            var background = new DrawingVisual();
            using (DrawingContext drawing = background.RenderOpen())
                drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 64, 64));
            Root.Children.Add(background);
            outputClip.Clip = outputClipGeometry;
            outputClip.Children.Add(receiver);
            Root.Children.Add(outputClip);
        }

        internal void Advance(BitmapCacheSamplerState state)
        {
            if (state.Index != index + 1)
                throw new InvalidOperationException("Original BitmapCacheBrush source mutation order changed.");
            Apply(state);
        }

        private void Apply(BitmapCacheSamplerState state)
        {
            index = state.Index;
            if (!state.HasLeaves && group.Children.Count != 0) group.Children.Clear();
            else if (state.HasLeaves && group.Children.Count == 0)
            {
                group.Children.Add(first);
                group.Children.Add(second);
            }
            if (!state.HasGroup && sampledRoot.Children.Count != 0) sampledRoot.Children.Clear();
            else if (state.HasGroup && sampledRoot.Children.Count == 0) sampledRoot.Children.Add(group);
            firstGeometry.Rect = state.FirstRectangle;
            secondGeometry.Rect = state.SecondRectangle;
            firstBrush.Color = state.Blue ? Colors.Blue : Colors.Red;
            sampledRoot.CacheMode = state.TargetCache ? targetCache : null;
            brush.BitmapCache = state.ExplicitCache ? explicitCache : null;
            explicitCache.RenderAtScale = state.ExplicitScale;
            sampledRoot.Offset = state.IgnoredRootState ? new(101, 103) : default;
            sampledRoot.Transform = state.IgnoredRootState ? rootTransform : null;
            sampledRoot.Clip = state.IgnoredRootState ? rootClip : null;
            sampledRoot.Effect = state.IgnoredRootState ? rootEffect : null;
            sampledRoot.Opacity = state.IgnoredRootState ? .25 : 1;
            sampledRoot.OpacityMask = state.IgnoredRootState ? rootMask : null;
            group.Opacity = state.InnerOpacity;
            if (state.InnerClip is Rect clip)
            {
                groupClip.Rect = clip;
                group.Clip = groupClip;
            }
            else group.Clip = null;
            brush.Target = state.Attached ? sampledRoot : null;
            brush.Opacity = state.BrushOpacity;
            relativeTransform.X = state.RelativeX;
            absoluteTransform.Y = state.AbsoluteY;
        }

        internal object Describe(BitmapCacheSamplerState state)
        {
            Rect observedBounds = VisualTreeHelper.GetDescendantBounds(sampledRoot);
            if (index != state.Index || !ReferenceEquals(brush.Target, state.Attached ? sampledRoot : null) ||
                VisualTreeHelper.GetParent(sampledRoot) != null || sampledRoot.Children.Count != (state.HasGroup ? 1 : 0) ||
                (state.HasGroup && !ReferenceEquals(sampledRoot.Children[0], group)) ||
                !ReferenceEquals(VisualTreeHelper.GetParent(group), state.HasGroup ? sampledRoot : null) ||
                group.Children.Count != (state.HasLeaves ? 2 : 0) ||
                (state.HasLeaves && (!ReferenceEquals(group.Children[0], first) || !ReferenceEquals(group.Children[1], second))) ||
                !ReferenceEquals(VisualTreeHelper.GetParent(first), state.HasLeaves ? group : null) ||
                !ReferenceEquals(VisualTreeHelper.GetParent(second), state.HasLeaves ? group : null) ||
                (state.EmptyTarget && !observedBounds.IsEmpty) ||
                !ReferenceEquals(sampledRoot.CacheMode, state.TargetCache ? targetCache : null) ||
                !ReferenceEquals(brush.BitmapCache, state.ExplicitCache ? explicitCache : null) ||
                targetCache.RenderAtScale != 2 || targetCache.EnableClearType || targetCache.SnapsToDevicePixels ||
                explicitCache.RenderAtScale != state.ExplicitScale || explicitCache.EnableClearType || !explicitCache.SnapsToDevicePixels ||
                sampledRoot.Offset != (state.IgnoredRootState ? new Vector(101, 103) : default) ||
                !ReferenceEquals(sampledRoot.Transform, state.IgnoredRootState ? rootTransform : null) ||
                !ReferenceEquals(sampledRoot.Clip, state.IgnoredRootState ? rootClip : null) ||
                !ReferenceEquals(sampledRoot.Effect, state.IgnoredRootState ? rootEffect : null) ||
                sampledRoot.Opacity != (state.IgnoredRootState ? .25 : 1) ||
                !ReferenceEquals(sampledRoot.OpacityMask, state.IgnoredRootState ? rootMask : null) ||
                rootTransform.X != 29 || rootTransform.Y != 31 || rootClip.Rect != new Rect(0, 0, 1, 1) ||
                rootEffect.Radius != 3 || rootMask.Color != Colors.Transparent || rootMask.Opacity != 1 ||
                group.Opacity != state.InnerOpacity || !ReferenceEquals(group.Clip, state.InnerClip.HasValue ? groupClip : null) ||
                (state.InnerClip is Rect expectedClip && groupClip.Rect != expectedClip) ||
                group.Offset != default || (group.Transform != null && !group.Transform.Value.IsIdentity) ||
                group.Effect != null || group.OpacityMask != null || group.CacheMode != null ||
                firstGeometry.Rect != state.FirstRectangle || secondGeometry.Rect != state.SecondRectangle ||
                firstBrush.Color != (state.Blue ? Colors.Blue : Colors.Red) || secondBrush.Color != Colors.Lime ||
                firstBrush.Opacity != 1 || secondBrush.Opacity != 1 || brush.Opacity != state.BrushOpacity ||
                !ReferenceEquals(brush.RelativeTransform, relativeTransform) || !ReferenceEquals(brush.Transform, absoluteTransform) ||
                relativeTransform.X != state.RelativeX || relativeTransform.Y != 0 ||
                absoluteTransform.X != 0 || absoluteTransform.Y != state.AbsoluteY || brush.HasAnimatedProperties ||
                !ReferenceEquals(receiver.Effect, effect) || !ReferenceEquals(effect.InputBrush, brush) ||
                !ReferenceEquals(effect.Shader, shader) || shader.ShaderRenderMode != ShaderRenderMode.SoftwareOnly ||
                receiver.Offset != new Vector(8, 10) || !ReferenceEquals(outputClip.Clip, outputClipGeometry) ||
                outputClipGeometry.Rect != new Rect(8, 10, 32, 24) ||
                receiver.EmittedScalingMode != BitmapScalingMode.NearestNeighbor ||
                first.EmittedEdgeMode != EdgeMode.Aliased || second.EmittedEdgeMode != EdgeMode.Aliased)
                throw new InvalidOperationException($"{state.Name}: original cache/source identity or selected state changed.");
            return new
            {
                CurrentState = index, AttachedOriginalTarget = state.Attached, SourceRootChildCount = sampledRoot.Children.Count,
                InnerChildOrder = state.HasLeaves ? new[] { "first", "second" } : Array.Empty<string>(),
                state.HasGroup, state.HasLeaves, state.EmptyTarget,
                // Observations, not inputs to the literal pixel oracle. In
                // particular, outer root effects are excluded from cache paint.
                ObservedDescendantBounds = VisualSamplerBoundsDescription(observedBounds),
                FirstContentBounds = VisualSamplerBoundsDescription(VisualTreeHelper.GetContentBounds(first)),
                SecondContentBounds = VisualSamplerBoundsDescription(VisualTreeHelper.GetContentBounds(second)),
                FirstRectangle = firstGeometry.Rect, SecondRectangle = secondGeometry.Rect,
                FirstArgb = firstBrush.Color.ToString(), SecondArgb = secondBrush.Color.ToString(),
                RootOffset = sampledRoot.Offset, RootTransform = sampledRoot.Transform?.Value,
                RootClip = sampledRoot.Clip == null ? null : VisualSamplerBoundsDescription(rootClip.Rect),
                RootEffect = sampledRoot.Effect == null ? "none" : "BlurEffect(radius=3)",
                RootOpacity = sampledRoot.Opacity, RootMask = sampledRoot.OpacityMask == null ? "none" : rootMask.Color.ToString(),
                InnerClip = state.InnerClip, InnerOpacity = group.Opacity, BrushOpacity = brush.Opacity,
                RelativeTransform = relativeTransform.Value, AbsoluteTransform = absoluteTransform.Value,
                TargetCacheAttached = state.TargetCache, TargetScale = targetCache.RenderAtScale,
                ExplicitCacheAttached = state.ExplicitCache, ExplicitScale = explicitCache.RenderAtScale,
                ExplicitSnapping = explicitCache.SnapsToDevicePixels,
                SelectedPolicy = state.ExplicitCache ? "explicit-brush" : state.TargetCache ? "target" : "default",
                SamplerPlacement = "raw cache texture over normalized shader coordinates; no ordinary brush mapping"
            };
        }
    }

    private sealed class OriginalBitmapCacheSamplerEffect : ShaderEffect
    {
        private static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
            "Input", typeof(OriginalBitmapCacheSamplerEffect), 0, SamplingMode.NearestNeighbor);
        internal BitmapCacheBrush InputBrush => (BitmapCacheBrush)GetValue(InputProperty);
        internal PixelShader Shader => PixelShader;

        internal OriginalBitmapCacheSamplerEffect(BitmapCacheBrush brush)
        {
            // Existing ProGPU-owned identity shader, unchanged instruction bytes.
            byte[] bytes = new byte[SamplerWords.Length * 4];
            for (int index = 0; index < SamplerWords.Length; ++index)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), SamplerWords[index]);
            var shader = new PixelShader { ShaderRenderMode = ShaderRenderMode.SoftwareOnly };
            using (var stream = new MemoryStream(bytes, writable: false)) shader.SetStreamSource(stream);
            PixelShader = shader;
            SetValue(InputProperty, brush);
            UpdateShaderValue(InputProperty);
        }

        protected override Freezable CreateInstanceCore() => new OriginalBitmapCacheSamplerEffect(InputBrush);
    }

    [DllImport("user32.dll", EntryPoint = "GetDpiForSystem", ExactSpelling = true)]
    private static extern uint ObserveCacheSamplerSystemDpi();
}
