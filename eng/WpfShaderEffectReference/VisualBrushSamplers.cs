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
    private sealed record VisualBrushSamplerState(int Index, string Name)
    {
        public Rect FirstRectangle { get; init; } = new(10, 20, 4, 6);
        public Rect SecondRectangle { get; init; } = new(14, 20, 4, 6);
        public bool Blue { get; init; }
        public double GroupOpacity { get; init; } = 1;
        public double BrushOpacity { get; init; } = 1;
        public Rect? GroupClip { get; init; }
        public Vector GroupTranslation { get; init; }
        public bool ReverseChildren { get; init; }
        public bool Attached { get; init; } = true;
        public bool HasGroup { get; init; } = true;
        public BrushMappingMode ViewportUnits { get; init; } = BrushMappingMode.RelativeToBoundingBox;
        public BrushMappingMode ViewboxUnits { get; init; } = BrushMappingMode.RelativeToBoundingBox;
        public Rect Viewport { get; init; } = new(0, 0, 1, 1);
        public Rect Viewbox { get; init; } = new(0, 0, 1, 1);
        public TileMode Tile { get; init; } = TileMode.None;
    }

    private static IEnumerable<VisualBrushSamplerState> VisualBrushSamplerStates()
    {
        yield return new(0, "visual-sampler-original-bounds");
        yield return new(1, "visual-sampler-overlap-opacity")
        { FirstRectangle = new(10, 20, 6, 6), GroupOpacity = .5, BrushOpacity = .5 };
        yield return new(2, "visual-sampler-absolute-viewbox")
        {
            ViewportUnits = BrushMappingMode.Absolute, ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new(8, 0, 16, 24), Viewbox = new(14, 20, 4, 6)
        };
        yield return new(3, "visual-sampler-child-clip")
        {
            ViewportUnits = BrushMappingMode.Absolute, ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new(0, 0, 32, 24), Viewbox = new(10, 20, 8, 6), GroupClip = new(10, 20, 6, 6)
        };
        yield return new(4, "visual-sampler-retained-color-tiles")
        { Blue = true, Viewport = new(0, 0, .5, 1), Tile = TileMode.Tile };
        yield return new(5, "visual-sampler-null-source") { Blue = true, Attached = false };
        yield return new(6, "visual-sampler-attached-empty") { Blue = true, HasGroup = false };
        yield return new(7, "visual-sampler-refilled-origin")
        { Blue = true, FirstRectangle = new(-6, 9, 4, 6), SecondRectangle = new(-2, 9, 4, 6) };
        yield return new(8, "visual-sampler-reordered-translated")
        {
            Blue = true, FirstRectangle = new(10, 20, 6, 6), ReverseChildren = true,
            GroupTranslation = new(3, -2)
        };
    }

    private static void CaptureVisualBrushSamplers(string directory, string commit, bool unavailable, Stopwatch timer)
    {
        var observations = new List<object>();
        var failures = new List<string>();
        OriginalVisualBrushSamplerScene? retained = null;
        byte[]? nullSourcePixels = null;
        foreach (VisualBrushSamplerState state in VisualBrushSamplerStates())
        {
            CheckSamplerAnimationDeadline(timer);
            if (retained == null) retained = new(state);
            else retained.Advance(state);
            var input = new
            {
                State = state, Original = retained.Describe(state), ShaderWords = SamplerWords,
                ShaderRenderMode = "SoftwareOnly", SamplerRegister = 0, SamplingMode = "NearestNeighbor",
                SourceTree = "identity sampled-root / inner group / first and second original DrawingVisual leaves",
                InitialChildOrder = new[] { "first", "second" }, RootTransform = "identity", RootOpacity = 1,
                AutoLayoutContent = false, SourceFrameworkElements = false,
                Bounds = new Rect(8, 10, 32, 24), Clip = new Rect(8, 10, 32, 24), TargetDpi = 96,
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
                OriginalVisualBrushSamplerScene current = replay < 2 ? retained : new(state);
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
                try { AssertVisualBrushSamplerPixels(state, pixels, unavailable); }
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
            if (state.Index == 5) nullSourcePixels = first;
            if (state.Index == 6 && (nullSourcePixels == null || !nullSourcePixels.AsSpan().SequenceEqual(first)))
                failures.Add("An attached genuinely empty visual differs from the independent null-source frame.");
            observations.Add(new { state.Name, Input = input, Replays = replays });
        }
        if (observations.Count != 9) throw new InvalidOperationException("Original VisualBrush sampler inventory changed.");
        CheckSamplerAnimationDeadline(timer);
        var receipt = new
        {
            Schema = 1, SourceCommit = commit, CaseFamily = "owned-visual-brush-shader-sampler",
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription,
            PresentationCore = FileIdentity(typeof(VisualBrush).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location),
            CaseCount = observations.Count, Replays = 27, Cases = observations, Failures = failures,
            CaptureMode = unavailable ? "unsupported-software-control" : "shader-pixels",
            QualifiedShaderCases = unavailable || failures.Count != 0 ? 0 : observations.Count,
            Qualification = "Original Microsoft WPF SoftwareOnly VisualBrush controls only; no native provider, package, source-host, self-reference or UIElement layout qualification."
        };
        using (var file = new FileStream(Path.Combine(directory, failures.Count == 0 ?
            "visual-brush-samplers.json" : "visual-brush-samplers.failed.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine($"Original VisualBrush samplers: 9 states / 27 replays; {(unavailable ? 0 : 9)} shader cases qualified.");
    }

    private static void AssertVisualBrushSamplerPixels(VisualBrushSamplerState state, byte[] pixels, bool unavailable)
    {
        // Independent literal coverage and premultiplied-over-black colors.
        // Neither the native renderer nor original output derives these bands.
        for (int y = 0; y < 64; ++y) for (int x = 0; x < 64; ++x)
        {
            int selected = -1;
            byte component = state.Index == 1 ? (byte)64 : (byte)255;
            if (!unavailable && y >= 10 && y < 34 && x >= 8 && x < 40)
            {
                int column = x - 8;
                selected = state.Index switch
                {
                    0 or 1 => column < 16 ? 2 : 1,
                    2 => column >= 8 && column < 24 ? 1 : -1,
                    3 => column < 16 ? 2 : column < 24 ? 1 : -1,
                    4 => (column / 8) % 2 == 0 ? 0 : 1,
                    5 or 6 => -1,
                    7 => column < 16 ? 0 : 1,
                    8 => column < 24 ? 0 : 1,
                    _ => throw new InvalidOperationException("Unknown independent VisualBrush sampler oracle.")
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

    private sealed class OriginalVisualBrushSamplerScene
    {
        private readonly ContainerVisual sampledRoot = new();
        private readonly ContainerVisual group = new();
        private readonly VisualSamplerDrawingVisual first = new();
        private readonly VisualSamplerDrawingVisual second = new();
        private readonly SolidColorBrush firstBrush = new(Colors.Red);
        private readonly SolidColorBrush secondBrush = new(Colors.Lime);
        private readonly RectangleGeometry firstGeometry = new();
        private readonly RectangleGeometry secondGeometry = new();
        private readonly TranslateTransform groupTransform = new();
        private readonly RectangleGeometry groupClip = new();
        private readonly VisualBrush brush = new() { AutoLayoutContent = false };
        private readonly OriginalVisualSamplerEffect effect;
        private readonly PixelShader shader;
        private readonly VisualSamplerDrawingVisual source = new();
        private readonly SamplerContainerVisual outputClip = new(nearest: true);
        private readonly RectangleGeometry outputClipGeometry = new(new Rect(8, 10, 32, 24));
        private int index;
        public ContainerVisual Root { get; } = new();

        internal OriginalVisualBrushSamplerScene(VisualBrushSamplerState state)
        {
            using (DrawingContext drawing = first.RenderOpen()) drawing.DrawGeometry(firstBrush, null, firstGeometry);
            using (DrawingContext drawing = second.RenderOpen()) drawing.DrawGeometry(secondBrush, null, secondGeometry);
            group.Transform = groupTransform;
            group.Children.Add(first);
            group.Children.Add(second);
            sampledRoot.Children.Add(group);
            Apply(state);
            effect = new(brush);
            shader = effect.Shader;
            source.Effect = effect;
            using (DrawingContext drawing = source.RenderOpen())
                drawing.DrawRectangle(Brushes.White, null, new Rect(8, 10, 32, 24));
            var background = new DrawingVisual();
            using (DrawingContext drawing = background.RenderOpen())
                drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 64, 64));
            Root.Children.Add(background);
            outputClip.Clip = outputClipGeometry;
            outputClip.Children.Add(source);
            Root.Children.Add(outputClip);
        }

        internal void Advance(VisualBrushSamplerState state)
        {
            if (state.Index != index + 1)
                throw new InvalidOperationException("Original VisualBrush source mutation order changed.");
            Apply(state);
        }

        private void Apply(VisualBrushSamplerState state)
        {
            index = state.Index;
            firstGeometry.Rect = state.FirstRectangle;
            secondGeometry.Rect = state.SecondRectangle;
            firstBrush.Color = state.Blue ? Colors.Blue : Colors.Red;
            group.Opacity = state.GroupOpacity;
            groupTransform.X = state.GroupTranslation.X;
            groupTransform.Y = state.GroupTranslation.Y;
            if (state.GroupClip is Rect clip)
            {
                groupClip.Rect = clip;
                group.Clip = groupClip;
            }
            else group.Clip = null;
            bool reversed = ReferenceEquals(group.Children[0], second);
            if (reversed != state.ReverseChildren)
            {
                group.Children.Clear();
                group.Children.Add(state.ReverseChildren ? second : first);
                group.Children.Add(state.ReverseChildren ? first : second);
            }
            if (!state.HasGroup && sampledRoot.Children.Count != 0) sampledRoot.Children.Clear();
            else if (state.HasGroup && sampledRoot.Children.Count == 0) sampledRoot.Children.Add(group);
            brush.Visual = state.Attached ? sampledRoot : null;
            brush.Opacity = state.BrushOpacity;
            brush.ViewportUnits = state.ViewportUnits;
            brush.ViewboxUnits = state.ViewboxUnits;
            brush.Viewport = state.Viewport;
            brush.Viewbox = state.Viewbox;
            brush.TileMode = state.Tile;
            brush.Stretch = Stretch.Fill;
            brush.AlignmentX = AlignmentX.Center;
            brush.AlignmentY = AlignmentY.Center;
        }

        internal object Describe(VisualBrushSamplerState state)
        {
            Rect bounds = VisualTreeHelper.GetDescendantBounds(sampledRoot);
            Rect expectedBounds = state.Index switch
            {
                3 => new(10, 20, 6, 6), 6 => Rect.Empty,
                7 => new(-6, 9, 8, 6), 8 => new(13, 18, 8, 6), _ => new(10, 20, 8, 6)
            };
            if (index != state.Index || !ReferenceEquals(brush.Visual, state.Attached ? sampledRoot : null) ||
                VisualTreeHelper.GetParent(sampledRoot) != null || sampledRoot.Children.Count != (state.HasGroup ? 1 : 0) ||
                (state.HasGroup && !ReferenceEquals(sampledRoot.Children[0], group)) ||
                !ReferenceEquals(VisualTreeHelper.GetParent(group), state.HasGroup ? sampledRoot : null) ||
                group.Children.Count != 2 || !ReferenceEquals(group.Children[0], state.ReverseChildren ? second : first) ||
                !ReferenceEquals(group.Children[1], state.ReverseChildren ? first : second) ||
                !ReferenceEquals(VisualTreeHelper.GetParent(first), group) || !ReferenceEquals(VisualTreeHelper.GetParent(second), group) ||
                !ReferenceEquals(source.Effect, effect) || !ReferenceEquals(effect.InputBrush, brush) ||
                !ReferenceEquals(effect.Shader, shader) || shader.ShaderRenderMode != ShaderRenderMode.SoftwareOnly ||
                !ReferenceEquals(group.Transform, groupTransform) || groupTransform.X != state.GroupTranslation.X ||
                groupTransform.Y != state.GroupTranslation.Y || !ReferenceEquals(group.Clip, state.GroupClip.HasValue ? groupClip : null) ||
                (state.GroupClip is Rect expectedClip && groupClip.Rect != expectedClip) ||
                firstGeometry.Rect != state.FirstRectangle || secondGeometry.Rect != state.SecondRectangle ||
                firstBrush.Color != (state.Blue ? Colors.Blue : Colors.Red) || secondBrush.Color != Colors.Lime ||
                firstBrush.Opacity != 1 || secondBrush.Opacity != 1 || group.Opacity != state.GroupOpacity ||
                sampledRoot.Opacity != 1 || sampledRoot.Clip != null || sampledRoot.Effect != null || sampledRoot.OpacityMask != null ||
                sampledRoot.Offset != default || (sampledRoot.Transform != null && !sampledRoot.Transform.Value.IsIdentity) ||
                group.Effect != null || group.OpacityMask != null || group.Offset != default ||
                brush.Opacity != state.BrushOpacity || brush.ViewportUnits != state.ViewportUnits ||
                brush.ViewboxUnits != state.ViewboxUnits || brush.Viewport != state.Viewport || brush.Viewbox != state.Viewbox ||
                brush.TileMode != state.Tile || brush.Stretch != Stretch.Fill || brush.AlignmentX != AlignmentX.Center ||
                brush.AlignmentY != AlignmentY.Center || brush.AutoLayoutContent || !brush.Transform.Value.IsIdentity ||
                !brush.RelativeTransform.Value.IsIdentity || brush.HasAnimatedProperties || bounds != expectedBounds ||
                !ReferenceEquals(outputClip.Clip, outputClipGeometry) || outputClipGeometry.Rect != new Rect(8, 10, 32, 24) ||
                source.EmittedScalingMode != BitmapScalingMode.NearestNeighbor || first.EmittedEdgeMode != EdgeMode.Aliased ||
                second.EmittedEdgeMode != EdgeMode.Aliased)
                throw new InvalidOperationException($"{state.Name}: original VisualBrush identity, bounds, child order or source mapping changed.");
            return new
            {
                CurrentState = index, AttachedOriginalVisual = state.Attached, SourceRootChildCount = sampledRoot.Children.Count,
                InnerChildOrder = state.ReverseChildren ? new[] { "second", "first" } : new[] { "first", "second" },
                DescendantBounds = VisualSamplerBoundsDescription(bounds),
                FirstContentBounds = VisualSamplerBoundsDescription(VisualTreeHelper.GetContentBounds(first)),
                SecondContentBounds = VisualSamplerBoundsDescription(VisualTreeHelper.GetContentBounds(second)),
                GroupClip = state.GroupClip, GroupTranslation = group.Transform.Value, group.Opacity,
                FirstRectangle = firstGeometry.Rect, SecondRectangle = secondGeometry.Rect,
                FirstArgb = firstBrush.Color.ToString(), SecondArgb = secondBrush.Color.ToString(),
                BrushOpacity = brush.Opacity, brush.Viewport, brush.Viewbox,
                ViewportUnits = brush.ViewportUnits.ToString(), ViewboxUnits = brush.ViewboxUnits.ToString(),
                TileMode = brush.TileMode.ToString(), brush.AutoLayoutContent,
                ContentCoordinates = "actual retained visual descendant bounds; not a viewport or replacement bitmap"
            };
        }
    }

    private static object VisualSamplerBoundsDescription(Rect bounds) => bounds.IsEmpty
        ? new { Empty = true, X = 0.0, Y = 0.0, Width = 0.0, Height = 0.0 }
        : new { Empty = false, bounds.X, bounds.Y, bounds.Width, bounds.Height };

    private sealed class VisualSamplerDrawingVisual : DrawingVisual
    {
        internal VisualSamplerDrawingVisual()
        {
            VisualBitmapScalingMode = BitmapScalingMode.NearestNeighbor;
            VisualEdgeMode = EdgeMode.Aliased;
        }
        internal BitmapScalingMode EmittedScalingMode => VisualBitmapScalingMode;
        internal EdgeMode EmittedEdgeMode => VisualEdgeMode;
    }

    private sealed class OriginalVisualSamplerEffect : ShaderEffect
    {
        private static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
            "Input", typeof(OriginalVisualSamplerEffect), 0, SamplingMode.NearestNeighbor);
        internal VisualBrush InputBrush => (VisualBrush)GetValue(InputProperty);
        internal PixelShader Shader => PixelShader;

        internal OriginalVisualSamplerEffect(VisualBrush brush)
        {
            // Existing original ProGPU identity sampler, unchanged instruction bytes.
            byte[] bytes = new byte[SamplerWords.Length * 4];
            for (int index = 0; index < SamplerWords.Length; ++index)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), SamplerWords[index]);
            var shader = new PixelShader { ShaderRenderMode = ShaderRenderMode.SoftwareOnly };
            using (var stream = new MemoryStream(bytes, writable: false)) shader.SetStreamSource(stream);
            PixelShader = shader;
            SetValue(InputProperty, brush);
            UpdateShaderValue(InputProperty);
        }

        protected override Freezable CreateInstanceCore() => new OriginalVisualSamplerEffect(InputBrush);
    }
}
