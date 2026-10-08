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
    private sealed record DrawingImageSamplerState(int Index, string Name)
    {
        public Rect FirstRectangle { get; init; } = new(10, 20, 4, 6);
        public Rect SecondRectangle { get; init; } = new(14, 20, 4, 6);
        public bool Blue { get; init; }
        public double GroupOpacity { get; init; } = 1;
        public double BrushOpacity { get; init; } = 1;
        public BrushMappingMode ViewportUnits { get; init; } = BrushMappingMode.RelativeToBoundingBox;
        public BrushMappingMode ViewboxUnits { get; init; } = BrushMappingMode.RelativeToBoundingBox;
        public Rect Viewport { get; init; } = new(0, 0, 1, 1);
        public Rect Viewbox { get; init; } = new(0, 0, 1, 1);
        public TileMode Tile { get; init; } = TileMode.None;
        public bool Attached { get; init; } = true;
        public bool HasChildren { get; init; } = true;
        public string? EquivalentTo { get; init; }
    }

    private static IEnumerable<DrawingImageSamplerState> DrawingImageSamplerStates()
    {
        yield return new(0, "drawing-sampler-original-origin");
        yield return new(1, "drawing-sampler-overlap-opacity")
        {
            FirstRectangle = new(10, 20, 6, 6), GroupOpacity = .5, BrushOpacity = .5
        };
        yield return new(2, "drawing-sampler-absolute-viewbox")
        {
            ViewportUnits = BrushMappingMode.Absolute, ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new(8, 0, 16, 24), Viewbox = new(14, 20, 4, 6)
        };
        yield return new(3, "drawing-sampler-tiled-relative") { Viewport = new(0, 0, .5, 1), Tile = TileMode.Tile };
        yield return new(4, "drawing-sampler-retained-color") { Blue = true };
        yield return new(5, "drawing-sampler-detached-drawing") { Blue = true, Attached = false };
        yield return new(6, "drawing-sampler-reattached-empty")
        {
            Blue = true, HasChildren = false, EquivalentTo = "drawing-sampler-detached-drawing"
        };
        yield return new(7, "drawing-sampler-refilled-origin")
        {
            Blue = true, FirstRectangle = new(-6, 9, 4, 6), SecondRectangle = new(-2, 9, 4, 6),
            EquivalentTo = "drawing-sampler-retained-color"
        };
        yield return new(8, "drawing-sampler-absolute-origin-miss")
        {
            Blue = true, ViewportUnits = BrushMappingMode.Absolute, ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new(8, 0, 16, 24), Viewbox = new(4, 0, 4, 6),
            EquivalentTo = "drawing-sampler-detached-drawing"
        };
    }

    private static void CaptureDrawingImageSamplers(string directory, string commit, bool unavailable, Stopwatch timer)
    {
        var observations = new List<object>();
        var failures = new List<string>();
        var prior = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        OriginalDrawingImageSamplerScene? retained = null;
        foreach (DrawingImageSamplerState state in DrawingImageSamplerStates())
        {
            CheckSamplerAnimationDeadline(timer);
            if (retained == null) retained = new OriginalDrawingImageSamplerScene(state);
            else retained.Advance(state);
            var input = new
            {
                State = state, Original = retained.Describe(state), ShaderWords = SamplerWords,
                ShaderRenderMode = "SoftwareOnly", SamplerRegister = 0, SamplingMode = "NearestNeighbor",
                ActualVisualBitmapScalingMode = "NearestNeighbor", ImageSourceKind = "DrawingImage",
                SourceOrder = new[] { "first-geometry", "second-geometry" }, Pens = "none",
                Bounds = new Rect(8, 10, 32, 24), Clip = new Rect(8, 10, 32, 24), TargetDpi = 96,
                ExpectedNaturalWidth = state.Attached && state.HasChildren ? 8 : 0,
                ExpectedNaturalHeight = state.Attached && state.HasChildren ? 6 : 0,
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
                OriginalDrawingImageSamplerScene current = replay < 2 ? retained : new(state);
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
                try { AssertDrawingImageSamplerPixels(state, pixels, unavailable); }
                catch (InvalidOperationException error) { failures.Add($"Replay {replay}: {error.Message}"); }
                if (first != null && !first.AsSpan().SequenceEqual(pixels))
                    failures.Add($"{state.Name}: same-source/warm/independent-literal pixels differ.");
                first ??= pixels;
                replays.Add(new
                {
                    Replay = replay, SameSourceObjects = replay < 2, IndependentLiteralInstance = replay == 2,
                    Before = before, After = after, RetainedBefore = retainedBefore, RetainedAfter = retainedAfter,
                    Pixels = pixels, PixelSha256 = Convert.ToHexString(SHA256.HashData(pixels))
                });
            }
            if (state.EquivalentTo != null && !prior[state.EquivalentTo].AsSpan().SequenceEqual(first))
                failures.Add($"{state.Name}: retained source restoration differs from {state.EquivalentTo}.");
            prior.Add(state.Name, first!);
            observations.Add(new { state.Name, Input = input, Replays = replays });
        }
        if (observations.Count != 9) throw new InvalidOperationException("Original DrawingImage sampler inventory is incomplete.");
        CheckSamplerAnimationDeadline(timer);
        var receipt = new
        {
            Schema = 1, SourceCommit = commit, CaseFamily = "owned-drawing-image-shader-sampler",
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PresentationCore = FileIdentity(typeof(DrawingImage).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location),
            CaseCount = observations.Count, Replays = 27, Cases = observations, Failures = failures,
            CaptureMode = unavailable ? "unsupported-software-control" : "shader-pixels",
            QualifiedShaderCases = unavailable || failures.Count != 0 ? 0 : observations.Count,
            Qualification = "Original Microsoft WPF SoftwareOnly DrawingImage controls only; no hardware, native provider, package or source-host parity."
        };
        using (var file = new FileStream(Path.Combine(directory, failures.Count == 0 ?
            "drawing-image-samplers.json" : "drawing-image-samplers.failed.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine($"Original DrawingImage samplers: 9 states / 27 replays; {(unavailable ? 0 : 9)} source shader cases qualified.");
    }

    private static void AssertDrawingImageSamplerPixels(DrawingImageSamplerState state, byte[] pixels, bool unavailable)
    {
        // Literal complete-frame oracle: no product bounds, graph evaluator,
        // matrix inverse, image realization or original output supplies a color.
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
                    // DrawingImage requires an intermediate. Its None source
                    // clip is the viewport, not the bitmap-only full-source
                    // realization contract covered by ImageSamplers.cs.
                    2 => column >= 8 && column < 24 ? 1 : -1,
                    3 => (column / 8) % 2 == 0 ? 2 : 1,
                    4 or 7 => column < 16 ? 0 : 1,
                    5 or 6 or 8 => -1,
                    _ => throw new InvalidOperationException("Unknown independent DrawingImage sampler oracle.")
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

    private static void CaptureOrdinaryDrawingImageBrushes(string directory, string commit, Stopwatch timer)
    {
        var observations = new List<object>();
        var failures = new List<string>();
        OriginalDrawingImageSamplerScene? retained = null;
        foreach (DrawingImageSamplerState state in DrawingImageSamplerStates())
        {
            if (state.Index > 7) break;
            CheckSamplerAnimationDeadline(timer);
            if (retained == null) retained = new(state, ordinary: true);
            else retained.Advance(state);
            if (state.Index is not (0 or 2 or 7)) continue;
            string name = "ordinary-" + state.Name;
            var input = new
            {
                Name = name, State = state, Original = retained.Describe(state), ShaderEffectAttached = false,
                LocalPaint = new Rect(0, 0, 32, 24), VisualOffset = new Vector(8, 10),
                FinalClip = new Rect(8, 10, 32, 24), TargetDpi = 96,
                SourceKind = "DrawingImage containing original retained geometry; not a bitmap replacement"
            };
            using (var file = new FileStream(Path.Combine(directory, name + ".input.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(file, input, new JsonSerializerOptions { WriteIndented = true });
            byte[]? first = null;
            var replays = new List<object>();
            for (int replay = 0; replay < 3; ++replay)
            {
                CheckSamplerAnimationDeadline(timer);
                object retainedBefore = retained.Describe(state);
                OriginalDrawingImageSamplerScene current = replay < 2 ? retained : new(state, ordinary: true);
                object before = current.Describe(state);
                var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(current.Root);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                object after = current.Describe(state);
                object retainedAfter = retained.Describe(state);
                var pixels = new byte[64 * 64 * 4];
                bitmap.CopyPixels(pixels, 64 * 4, 0);
                SaveSamplerBitmap(directory, name + $".replay-{replay}", bitmap, pixels);
                // This ordinary source has no PixelShader and is a positive
                // drawing control on both architectures, including native ARM64.
                try { AssertDrawingImageSamplerPixels(state, pixels, unavailable: false); }
                catch (InvalidOperationException error) { failures.Add($"{name}, replay {replay}: {error.Message}"); }
                if (first != null && !first.AsSpan().SequenceEqual(pixels))
                    failures.Add($"{name}: ordinary retained/warm/independent-literal pixels differ.");
                first ??= pixels;
                replays.Add(new
                {
                    Replay = replay, SameSourceObjects = replay < 2, IndependentLiteralInstance = replay == 2,
                    Before = before, After = after, RetainedBefore = retainedBefore, RetainedAfter = retainedAfter,
                    Pixels = pixels, PixelSha256 = Convert.ToHexString(SHA256.HashData(pixels))
                });
            }
            observations.Add(new { Name = name, Input = input, Replays = replays });
        }
        if (observations.Count != 3) throw new InvalidOperationException("Original ordinary DrawingImage inventory changed.");
        CheckSamplerAnimationDeadline(timer);
        var receipt = new
        {
            Schema = 1, SourceCommit = commit, CaseFamily = "ordinary-drawing-image-brush",
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PresentationCore = FileIdentity(typeof(DrawingImage).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location),
            CaseCount = observations.Count, Replays = 9, Cases = observations, Failures = failures,
            QualifiedDrawingCases = failures.Count == 0 ? observations.Count : 0,
            Qualification = "Original ordinary Microsoft WPF drawing only; no ShaderEffect, native provider, package or source-host parity."
        };
        using (var file = new FileStream(Path.Combine(directory, failures.Count == 0 ?
            "ordinary-drawing-image-brushes.json" : "ordinary-drawing-image-brushes.failed.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("Original ordinary DrawingImage brushes: 3 states / 9 replays; no ShaderEffect attached.");
    }

    private sealed class OriginalDrawingImageSamplerScene
    {
        private readonly bool ordinary;
        private readonly SolidColorBrush firstBrush;
        private readonly SolidColorBrush secondBrush;
        private readonly RectangleGeometry firstGeometry;
        private readonly RectangleGeometry secondGeometry;
        private readonly GeometryDrawing firstDrawing;
        private readonly GeometryDrawing secondDrawing;
        private readonly DrawingGroup group;
        private readonly DrawingImage image;
        private readonly ImageBrush brush;
        private readonly AnimatedSamplerEffect? effect;
        private readonly PixelShader? shader;
        private readonly SamplerDrawingVisual source;
        private readonly SamplerContainerVisual clip;
        private readonly RectangleGeometry clipGeometry = new(new(8, 10, 32, 24));
        private int index;
        public ContainerVisual Root { get; }

        public OriginalDrawingImageSamplerScene(DrawingImageSamplerState state, bool ordinary = false)
        {
            this.ordinary = ordinary;
            index = state.Index;
            firstBrush = new(state.Blue ? Colors.Blue : Colors.Red);
            secondBrush = new(Colors.Lime);
            firstGeometry = new(state.FirstRectangle);
            secondGeometry = new(state.SecondRectangle);
            firstDrawing = new(firstBrush, null, firstGeometry);
            secondDrawing = new(secondBrush, null, secondGeometry);
            group = new() { Opacity = state.GroupOpacity };
            if (state.HasChildren) { group.Children.Add(firstDrawing); group.Children.Add(secondDrawing); }
            image = new() { Drawing = state.Attached ? group : null };
            brush = new(image)
            {
                Opacity = state.BrushOpacity, ViewportUnits = state.ViewportUnits, ViewboxUnits = state.ViewboxUnits,
                Viewport = state.Viewport, Viewbox = state.Viewbox, TileMode = state.Tile,
                Stretch = Stretch.Fill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center
            };
            effect = ordinary ? null : new(brush);
            shader = effect?.Shader;
            Root = new();
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.Black, null, new(0, 0, 64, 64));
            Root.Children.Add(background);
            clip = new(nearest: true) { Clip = clipGeometry };
            source = new(nearest: true) { Effect = effect, Offset = ordinary ? new Vector(8, 10) : default };
            using (var drawing = source.RenderOpen())
                drawing.DrawRectangle(ordinary ? brush : Brushes.White, null,
                    ordinary ? new Rect(0, 0, 32, 24) : new Rect(8, 10, 32, 24));
            clip.Children.Add(source);
            Root.Children.Add(clip);
        }

        public void Advance(DrawingImageSamplerState state)
        {
            if (state.Index != index + 1) throw new InvalidOperationException("Original DrawingImage mutation order changed.");
            switch (state.Index)
            {
                case 1:
                    firstGeometry.Rect = new(10, 20, 6, 6);
                    group.Opacity = .5;
                    brush.Opacity = .5;
                    break;
                case 2:
                    firstGeometry.Rect = new(10, 20, 4, 6);
                    group.Opacity = 1;
                    brush.Opacity = 1;
                    brush.ViewportUnits = BrushMappingMode.Absolute;
                    brush.ViewboxUnits = BrushMappingMode.Absolute;
                    brush.Viewport = new(8, 0, 16, 24);
                    brush.Viewbox = new(14, 20, 4, 6);
                    break;
                case 3:
                    brush.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
                    brush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
                    brush.Viewport = new(0, 0, .5, 1);
                    brush.Viewbox = new(0, 0, 1, 1);
                    brush.TileMode = TileMode.Tile;
                    break;
                case 4:
                    brush.Viewport = new(0, 0, 1, 1);
                    brush.TileMode = TileMode.None;
                    firstBrush.Color = Colors.Blue;
                    break;
                case 5:
                    image.Drawing = null;
                    break;
                case 6:
                    group.Children.Clear();
                    image.Drawing = group;
                    break;
                case 7:
                    firstGeometry.Rect = new(-6, 9, 4, 6);
                    secondGeometry.Rect = new(-2, 9, 4, 6);
                    group.Children.Add(firstDrawing);
                    group.Children.Add(secondDrawing);
                    break;
                case 8:
                    firstGeometry.Rect = new(10, 20, 4, 6);
                    secondGeometry.Rect = new(14, 20, 4, 6);
                    brush.ViewportUnits = BrushMappingMode.Absolute;
                    brush.ViewboxUnits = BrushMappingMode.Absolute;
                    brush.Viewport = new(8, 0, 16, 24);
                    brush.Viewbox = new(4, 0, 4, 6);
                    break;
                default: throw new InvalidOperationException("Unknown original DrawingImage mutation.");
            }
            index = state.Index;
        }

        public object Describe(DrawingImageSamplerState state)
        {
            bool attached = ReferenceEquals(image.Drawing, group);
            bool children = group.Children.Count == 2 && ReferenceEquals(group.Children[0], firstDrawing) &&
                ReferenceEquals(group.Children[1], secondDrawing);
            bool effectIdentity = ordinary ? effect == null && shader == null :
                effect != null && shader != null && ReferenceEquals(effect.InputBrush, brush) &&
                ReferenceEquals(effect.Shader, shader) && shader.ShaderRenderMode == ShaderRenderMode.SoftwareOnly;
            Rect expectedBounds = state.HasChildren ? state.Index == 7 ? new(-6, 9, 8, 6) : new(10, 20, 8, 6) : Rect.Empty;
            if (index != state.Index || attached != state.Attached || (!attached && image.Drawing != null) ||
                children != state.HasChildren || (!children && group.Children.Count != 0) ||
                !ReferenceEquals(brush.ImageSource, image) || !ReferenceEquals(source.Effect, effect) ||
                !effectIdentity || source.Offset != (ordinary ? new Vector(8, 10) : default) ||
                !ReferenceEquals(firstDrawing.Brush, firstBrush) || !ReferenceEquals(secondDrawing.Brush, secondBrush) ||
                !ReferenceEquals(firstDrawing.Geometry, firstGeometry) || !ReferenceEquals(secondDrawing.Geometry, secondGeometry) ||
                firstDrawing.Pen != null || secondDrawing.Pen != null ||
                firstGeometry.Rect != state.FirstRectangle || secondGeometry.Rect != state.SecondRectangle ||
                firstBrush.Color != (state.Blue ? Colors.Blue : Colors.Red) || secondBrush.Color != Colors.Lime ||
                firstBrush.Opacity != 1 || secondBrush.Opacity != 1 || group.Opacity != state.GroupOpacity ||
                brush.Opacity != state.BrushOpacity || brush.ViewportUnits != state.ViewportUnits ||
                brush.ViewboxUnits != state.ViewboxUnits || brush.Viewport != state.Viewport || brush.Viewbox != state.Viewbox ||
                brush.TileMode != state.Tile || brush.Stretch != Stretch.Fill || brush.AlignmentX != AlignmentX.Center ||
                brush.AlignmentY != AlignmentY.Center || !brush.Transform.Value.IsIdentity ||
                !brush.RelativeTransform.Value.IsIdentity || group.ClipGeometry != null || group.OpacityMask != null ||
                group.Transform != null || group.Bounds != expectedBounds ||
                image.Width != (state.Attached && state.HasChildren ? 8 : 0) ||
                image.Height != (state.Attached && state.HasChildren ? 6 : 0) ||
                !ReferenceEquals(clip.Clip, clipGeometry) || clipGeometry.Rect != new Rect(8, 10, 32, 24) ||
                source.EmittedScalingMode != BitmapScalingMode.NearestNeighbor ||
                brush.HasAnimatedProperties || group.HasAnimatedProperties || image.HasAnimatedProperties)
                throw new InvalidOperationException($"{state.Name}: original DrawingImage identity, bounds, source order or mapping changed.");
            return new
            {
                CurrentState = index, OrdinaryBrush = ordinary, ShaderEffectAttached = source.Effect != null,
                source.Offset, AttachedOriginalGroup = attached, OriginalOrderedChildren = children,
                ChildCount = group.Children.Count, GroupBounds = RectDescription(group.Bounds),
                ImageDrawingBounds = RectDescription(image.Drawing?.Bounds ?? Rect.Empty), image.Width, image.Height,
                FirstRectangle = firstGeometry.Rect, SecondRectangle = secondGeometry.Rect,
                FirstArgb = firstBrush.Color.ToString(), SecondArgb = secondBrush.Color.ToString(),
                GroupOpacityBits = Bits(group.Opacity), BrushOpacityBits = Bits(brush.Opacity),
                Viewport = brush.Viewport, Viewbox = brush.Viewbox,
                ViewportUnits = brush.ViewportUnits.ToString(), ViewboxUnits = brush.ViewboxUnits.ToString(),
                TileMode = brush.TileMode.ToString(),
                ContentCoordinates = "original Drawing bounds; Width/Height are extents, not a zero-origin rebase"
            };
        }

        private static string Bits(double value) => BitConverter.DoubleToUInt64Bits(value).ToString("X16");
        private static object RectDescription(Rect rectangle) => new
        {
            rectangle.IsEmpty,
            Bits = new[] { Bits(rectangle.X), Bits(rectangle.Y), Bits(rectangle.Width), Bits(rectangle.Height) }
        };
    }
}
