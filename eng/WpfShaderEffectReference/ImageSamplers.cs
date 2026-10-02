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
    private static void CaptureImageSamplers(string directory, string commit, bool unavailable, Stopwatch timer)
    {
        double startedMilliseconds = timer.Elapsed.TotalMilliseconds;
        int softwareArithmeticControls = SoftwareSamplerOracle.VerifyArithmeticControls();
        var observations = new List<object>();
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var failures = new List<string>();
        int independentColors = 0, equivalentPairs = 0;
        foreach (SamplerCase input in SamplerCases())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Original ImageBrush reference exceeded 60 seconds.");
            var source = CreateSamplerBitmap(input, out byte[] sourcePixels);
            Rect viewbox = input.AbsoluteViewbox
                ? new(input.Viewbox.X * source.Width, input.Viewbox.Y * source.Height,
                    input.Viewbox.Width * source.Width, input.Viewbox.Height * source.Height)
                : input.Viewbox;
            var description = new
            {
                input, PixelWidth = source.PixelWidth, PixelHeight = source.PixelHeight,
                source.DpiX, source.DpiY, SourceDipWidth = source.Width, SourceDipHeight = source.Height,
                ActualViewbox = viewbox, ViewboxUnits = input.AbsoluteViewbox ? "Absolute" : "RelativeToBoundingBox",
                SourcePixelSha256 = Convert.ToHexString(SHA256.HashData(sourcePixels)),
                SourcePixelFormat = "Pbgra32", ShaderWords = SamplerWords,
                SamplerRegister = 0, ShaderRenderMode = "SoftwareOnly", AlignmentX = "Center", AlignmentY = "Center",
                BitmapScalingMode = "NearestNeighbor", ParentBitmapScalingMode = input.ParentNearest ? "NearestNeighbor" : "Unspecified",
                VisualFieldScalingMode = input.VisualNearest ? "NearestNeighbor" : "Unspecified",
                ParentVisualFieldScalingMode = input.ParentVisualNearest ? "NearestNeighbor" : "Unspecified",
                EffectInputBounds = input.Bounds,
                Meaning = "Original source bitmap metadata and complete ImageBrush before shader rendering."
            };
            using (var file = new FileStream(Path.Combine(directory, input.Name + ".input.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(file, description, new JsonSerializerOptions { WriteIndented = true });
            SaveSamplerBitmap(directory, input.Name + ".source", source, sourcePixels);
            ContainerVisual visual = CreateSamplerVisual(input, source, viewbox);
            byte[]? first = null;
            for (int replay = 0; replay < 3; ++replay)
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Original ImageBrush replay exceeded 60 seconds.");
                var bitmap = new RenderTargetBitmap(input.Size, input.Size, 96, 96, PixelFormats.Pbgra32);
                if (replay == 2)
                {
                    var independent = CreateSamplerBitmap(input, out _);
                    visual = CreateSamplerVisual(input, independent, viewbox);
                }
                if (input.ResetVisualOptions && replay != 0)
                {
                    var receivingVisual = (SamplerDrawingVisual)((ContainerVisual)visual.Children[1]).Children[0];
                    receivingVisual.ResetScalingMode();
                    if (receivingVisual.EmittedScalingMode != BitmapScalingMode.Unspecified)
                        throw new InvalidOperationException("Original receiving visual did not reset its actual options.");
                }
                bitmap.Render(visual);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                if (Volatile.Read(ref invalidShaders) != 0) throw new InvalidOperationException($"Original WPF rejected {input.Name}.");
                var pixels = new byte[input.Size * input.Size * 4];
                bitmap.CopyPixels(pixels, input.Size * 4, 0);
                if (replay == 0) SaveSamplerBitmap(directory, input.Name, bitmap, pixels);
                // Retain the entire original inventory after a pixel mismatch,
                // but never publish a successful receipt for those observations.
                try { AssertSamplerPixels(input, pixels, unavailable); }
                catch (InvalidOperationException error) { failures.Add($"Replay {replay}: {error.Message}"); }
                if (first != null && !first.AsSpan().SequenceEqual(pixels))
                    failures.Add($"{input.Name}: cold/warm/independent sampler pixels changed.");
                first ??= pixels;
            }
            if (input.NativeVariant >= 0 || input.ExactViewboxMapping || input.TwoAxisCheckerboard) ++independentColors;
            if (input.EquivalentTo != null)
            {
                if (!originals.TryGetValue(input.EquivalentTo, out var prior) || !prior.AsSpan().SequenceEqual(first))
                    failures.Add($"{input.Name}: equivalent original absolute/relative viewboxes differ.");
                ++equivalentPairs;
            }
            originals.Add(input.Name, first!);
            observations.Add(new
            {
                input.Name, Input = description, Replays = 3, Pixels = first,
                PixelSha256 = Convert.ToHexString(SHA256.HashData(first!)),
                IndependentColorOracle = input.NativeVariant >= 0 || input.ExactViewboxMapping || input.TwoAxisCheckerboard,
                input.EquivalentTo
            });
        }
        if (observations.Count != 28 || independentColors != 22 || equivalentPairs != 5)
            throw new InvalidOperationException("Original ImageBrush inventory is incomplete.");
        var receipt = new
        {
            Schema = 1, SourceCommit = commit, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PresentationIdentity = typeof(ShaderEffect).Assembly.FullName,
            PresentationCore = FileIdentity(typeof(ShaderEffect).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location),
            CaseCount = observations.Count, Cases = observations, Replays = 84,
            CaptureMode = unavailable ? "unsupported-software-control" : "shader-pixels",
            QualifiedShaderCases = unavailable || failures.Count != 0 ? 0 : observations.Count,
            IndependentColorCases = independentColors, EquivalentViewboxPairs = equivalentPairs,
            SoftwareArithmeticControls = softwareArithmeticControls,
            Failures = failures,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds - startedMilliseconds,
            Qualification = unavailable
                ? "Original ARM64 unavailable-software control; no sampler shader pixels qualified."
                : "Original Microsoft WPF ImageBrush observations. Twenty-two independent color controls and five complete-pixel equivalence pairs; failures disqualify the entire capture. Not native/source-host/package parity."
        };
        using (var output = new FileStream(Path.Combine(directory, failures.Count == 0 ? "image-samplers.json" : "image-samplers.failed.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(output, receipt, new JsonSerializerOptions { WriteIndented = true });
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine($"Original ImageBrush: 28 cases / 84 replays / 5 viewbox pairs; {(unavailable ? 0 : 28)} source shader cases qualified.");
    }

    private static readonly uint[] SamplerWords = [0xFFFF0200, 0x0200001F, 0x80000000, 0xB0030000,
        0x0200001F, 0x90000000, 0xA00F0800, 0x03000042, 0x800F0000, 0xB0E40000, 0xA0E40800,
        0x02000001, 0x800F0800, 0x80E40000, 0xFFFF];

    private sealed record SamplerCase(string Name)
    {
        public int Size { get; init; } = 64;
        public int Width { get; init; } = 2;
        public int Height { get; init; } = 1;
        public double DpiX { get; init; } = 144;
        public double DpiY { get; init; } = 192;
        public double Opacity { get; init; } = .5;
        public Rect Bounds { get; init; } = new(8, 10, 32, 24);
        public Rect Clip { get; init; } = new(16, 12, 16, 16);
        public Rect Viewbox { get; init; } = new(0, 0, 1, 1);
        public Rect Viewport { get; init; } = new(0, 0, 1, 1);
        public bool AbsoluteViewbox { get; init; }
        public Stretch Stretch { get; init; } = Stretch.Fill;
        public TileMode Tile { get; init; } = TileMode.None;
        public SamplingMode Sampling { get; init; } = SamplingMode.NearestNeighbor;
        public double TranslationX { get; init; }
        public bool Blue { get; init; }
        public bool TwoAxisCheckerboard { get; init; }
        public int NativeVariant { get; init; } = -1;
        public bool ExactViewboxMapping { get; init; }
        public bool ParentNearest { get; init; }
        public bool VisualNearest { get; init; }
        public bool ParentVisualNearest { get; init; }
        public bool ResetVisualOptions { get; init; }
        public string? EquivalentTo { get; init; }
    }

    private static IEnumerable<SamplerCase> SamplerCases()
    {
        for (int variant = 0; variant < 4; ++variant)
            yield return new($"sampler-native-{variant}")
            {
                NativeVariant = variant, Blue = variant == 3, Opacity = variant == 3 ? 1 : .5,
                Viewport = new(0, 0, variant == 0 ? 1 : .5, 1), Tile = variant == 0 ? TileMode.None : TileMode.Tile,
                TranslationX = variant == 2 ? 8 : 0,
                Sampling = (variant & 1) == 0 ? SamplingMode.NearestNeighbor : SamplingMode.Bilinear
            };
        for (int variant = 0; variant < 2; ++variant)
            yield return new($"sampler-parent-nearest-{variant}")
            {
                NativeVariant = variant, ParentNearest = true,
                Viewport = new(0, 0, variant == 0 ? 1 : .5, 1), Tile = variant == 0 ? TileMode.None : TileMode.Tile,
                Sampling = variant == 0 ? SamplingMode.NearestNeighbor : SamplingMode.Bilinear
            };
        foreach (bool fractional in new[] { false, true })
        foreach (SamplingMode sampling in new[] { SamplingMode.NearestNeighbor, SamplingMode.Bilinear })
        {
            string name = $"sampler-crop-{(fractional ? "fractional" : "integral")}-{sampling}";
            var input = new SamplerCase(name + "-absolute")
            {
                Size = 128, Width = 400, Height = 200, DpiX = fractional ? 123.456789012345 : 192,
                DpiY = fractional ? 183.456789012345 : 384, Opacity = .25,
                Bounds = new(8, 10, 100, 100), Clip = new(8, 10, 100, 100),
                Viewbox = new(.25, .2, .5, .4), AbsoluteViewbox = true, Stretch = Stretch.None,
                Sampling = sampling, ExactViewboxMapping = !fractional
            };
            yield return input;
            yield return input with { Name = name + "-relative", AbsoluteViewbox = false, EquivalentTo = input.Name };
        }
        var mirrored = new SamplerCase("sampler-flip-translated-absolute")
        {
            Size = 128, Width = 400, Height = 200, DpiX = 192, DpiY = 384, Opacity = .25,
            Bounds = new(8, 10, 100, 100), Clip = new(8, 10, 100, 100),
            Viewbox = new(.25, .2, .5, .4), Viewport = new(0, 0, .5, 1), AbsoluteViewbox = true,
            Stretch = Stretch.None, Tile = TileMode.FlipX, TranslationX = 8
        };
        yield return mirrored;
        yield return mirrored with { Name = "sampler-flip-translated-relative", AbsoluteViewbox = false, EquivalentTo = mirrored.Name };
        // Preserve the original sixteen inputs above. These eight additional
        // cases set the actual protected visual state serialized into MIL;
        // attached DPs on bare Visuals are not equivalent emitted options.
        for (int variant = 0; variant < 4; ++variant)
            yield return new($"sampler-visual-options-{variant}")
            {
                NativeVariant = variant, VisualNearest = true, Blue = variant == 3, Opacity = variant == 3 ? 1 : .5,
                Viewport = new(0, 0, variant == 0 ? 1 : .5, 1), Tile = variant == 0 ? TileMode.None : TileMode.Tile,
                TranslationX = variant == 2 ? 8 : 0,
                Sampling = (variant & 1) == 0 ? SamplingMode.NearestNeighbor : SamplingMode.Bilinear
            };
        for (int variant = 0; variant < 2; ++variant)
            yield return new($"sampler-parent-visual-options-{variant}")
            {
                NativeVariant = variant, ParentVisualNearest = true,
                Viewport = new(0, 0, variant == 0 ? 1 : .5, 1), Tile = variant == 0 ? TileMode.None : TileMode.Tile,
                Sampling = variant == 0 ? SamplingMode.NearestNeighbor : SamplingMode.Bilinear
            };
        for (int variant = 0; variant < 2; ++variant)
            yield return new($"sampler-reset-to-inherited-options-{variant}")
            {
                NativeVariant = variant, ParentVisualNearest = true, VisualNearest = true, ResetVisualOptions = true,
                Viewport = new(0, 0, variant == 0 ? 1 : .5, 1), Tile = variant == 0 ? TileMode.None : TileMode.Tile,
                Sampling = variant == 0 ? SamplingMode.NearestNeighbor : SamplingMode.Bilinear
            };
        // Preserve all twenty-four original inputs. These exercise both source
        // axes, including negative source coordinates after translation.
        for (int variant = 0; variant < 4; ++variant)
            yield return new($"sampler-mirror-two-axis-{variant}")
            {
                Height = 2, TwoAxisCheckerboard = true,
                Viewport = new(0, 0, .5, 1), Sampling = SamplingMode.Bilinear,
                Tile = variant switch { 0 => TileMode.FlipX, 1 => TileMode.FlipY, 2 => TileMode.FlipXY, _ => TileMode.FlipX },
                TranslationX = variant == 3 ? 8 : 0
            };
    }

    private static BitmapSource CreateSamplerBitmap(SamplerCase input, out byte[] pixels)
    {
        pixels = new byte[input.Width * input.Height * 4];
        for (int y = 0; y < input.Height; ++y)
        for (int x = 0; x < input.Width; ++x)
        {
            int at = (y * input.Width + x) * 4;
            bool green = (x >= input.Width / 2) != (input.TwoAxisCheckerboard && y >= input.Height / 2);
            pixels[at + (green ? 1 : input.Blue ? 0 : 2)] = 255;
            pixels[at + 3] = 255;
        }
        return BitmapSource.Create(input.Width, input.Height, input.DpiX, input.DpiY, PixelFormats.Pbgra32,
            null, pixels, input.Width * 4);
    }

    private static ContainerVisual CreateSamplerVisual(SamplerCase input, BitmapSource bitmap, Rect viewbox)
    {
        var brush = new ImageBrush(bitmap)
        {
            Opacity = input.Opacity, Viewbox = viewbox,
            ViewboxUnits = input.AbsoluteViewbox ? BrushMappingMode.Absolute : BrushMappingMode.RelativeToBoundingBox,
            Viewport = input.Viewport, ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Stretch = input.Stretch, TileMode = input.Tile, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center,
            Transform = new TranslateTransform(input.TranslationX, 0)
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        var root = new ContainerVisual();
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.Black, null, new(0, 0, input.Size, input.Size));
        root.Children.Add(background);
        var clip = new SamplerContainerVisual(input.ParentVisualNearest) { Clip = new RectangleGeometry(input.Clip) };
        if (input.ParentNearest) RenderOptions.SetBitmapScalingMode(clip, BitmapScalingMode.NearestNeighbor);
        var source = new SamplerDrawingVisual(input.VisualNearest) { Effect = new SamplerEffect(brush, input.Sampling) };
        RenderOptions.SetBitmapScalingMode(source, BitmapScalingMode.NearestNeighbor);
        if (source.EmittedScalingMode != (input.VisualNearest ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Unspecified) ||
            clip.EmittedScalingMode != (input.ParentVisualNearest ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Unspecified))
            throw new InvalidOperationException($"{input.Name}: actual visual state does not match the emitted-option contract.");
        using (var drawing = source.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, input.Bounds);
        clip.Children.Add(source);
        root.Children.Add(clip);
        return root;
    }

    private static void AssertSamplerPixels(SamplerCase input, byte[] pixels, bool unavailable)
    {
        int changed = 0;
        for (int y = 0; y < input.Size; ++y)
        for (int x = 0; x < input.Size; ++x)
        {
            bool inside = input.Clip.Contains(new Point(x + .5, y + .5)) && input.Bounds.Contains(new Point(x + .5, y + .5));
            int at = (y * input.Size + x) * 4;
            if (pixels[at + 3] != 255) throw new InvalidOperationException($"{input.Name}: final opaque alpha changed at {x},{y}.");
            for (int channel = 0; channel < 3; ++channel)
            {
                byte actual = pixels[at + channel];
                if (inside && actual != 0 && actual != 255) ++changed;
                byte expected = 0;
                bool exact = unavailable || !inside || input.NativeVariant >= 0 || input.ExactViewboxMapping || input.TwoAxisCheckerboard;
                // On native ARM64 the unavailable software ImageBrush shader
                // contributes no color, unlike the separate implicit-input
                // controls that retain their original white input. Neither
                // behavior qualifies shader execution on that architecture.
                if (!unavailable && inside && input.TwoAxisCheckerboard)
                {
                    // The original SOFTWARE color source quantizes affine
                    // coefficients and fractions, emits bytes, then applies
                    // fixed-point opacity. This is not a GPU-filter oracle.
                    float sx = (float)(input.Width / (input.Bounds.Width * input.Viewport.Width));
                    float sy = (float)(input.Height / (input.Bounds.Height * input.Viewport.Height));
                    long u = SoftwareSamplerOracle.Coordinate(sx, (float)input.TranslationX, x - (int)input.Bounds.X);
                    long v = SoftwareSamplerOracle.Coordinate(sy, 0, y - (int)input.Bounds.Y);
                    if (channel != 0)
                        expected = SoftwareSamplerOracle.Checkerboard(u, v,
                            input.Tile is TileMode.FlipX or TileMode.FlipXY,
                            input.Tile is TileMode.FlipY or TileMode.FlipXY,
                            channel == 1, SoftwareSamplerOracle.Coefficient((float)input.Opacity));
                }
                else if (!unavailable && inside && input.NativeVariant >= 0 && (input.VisualNearest || input.ParentVisualNearest))
                {
                    int variant = input.NativeVariant;
                    int stripe = ((x - 8 + 32 - (variant == 2 ? 8 : 0)) / (variant == 0 ? 16 : 8)) & 1;
                    int color = stripe == 1 ? 1 : input.Blue ? 0 : 2;
                    if (channel == color) expected = input.Blue ? (byte)255 : (byte)128;
                }
                else if (!unavailable && inside && input.NativeVariant >= 0)
                {
                    int variant = input.NativeVariant;
                    // Independent two-texel linear realization at pixel centers.
                    // The attached nearest DP on a bare Visual does not change
                    // its actual visual field, so these cases emit Unspecified.
                    // Actual visual-option cases retain their nearest oracle.
                    double coordinate = (x - 8 + .5 - input.TranslationX) / (variant == 0 ? 16 : 8) - .5;
                    int left = (int)Math.Floor(coordinate);
                    double fraction = coordinate - left;
                    int first = variant == 0 ? Math.Clamp(left, 0, 1) : ((left % 2) + 2) % 2;
                    int second = variant == 0 ? Math.Clamp(left + 1, 0, 1) : (((left + 1) % 2) + 2) % 2;
                    double green = (first == 1 ? 1 - fraction : 0) + (second == 1 ? fraction : 0);
                    double component = channel == 1 ? green : channel == (input.Blue ? 0 : 2) ? 1 - green : 0;
                    expected = checked((byte)Math.Round(component * 255 * input.Opacity, MidpointRounding.ToEven));
                }
                // Stretch=None maps the selected 100x20 viewbox to (0,40),
                // subtracting its (50,10) origin. TileMode.None preserves the
                // full source 200x50 extent: output y=10+(40-10)..+50.
                else if (!unavailable && inside && input.ExactViewboxMapping && y >= 40 && y < 90)
                    expected = channel == (x < 58 ? 2 : 1) ? (byte)64 : (byte)0;
                if (exact && actual != expected)
                    throw new InvalidOperationException($"{input.Name}: ({x},{y}) BGRA[{channel}]={actual}, expected {expected}.");
            }
        }
        if (!unavailable && input.NativeVariant < 0 && changed == 0)
            throw new InvalidOperationException($"{input.Name}: sampler capture is blank or unmodified, not an opacity-bearing shader result.");
    }

    private static void SaveSamplerBitmap(string directory, string name, BitmapSource bitmap, byte[] pixels)
    {
        using (var raw = new FileStream(Path.Combine(directory, name + ".bgra"), FileMode.CreateNew)) raw.Write(pixels);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var image = new FileStream(Path.Combine(directory, name + ".png"), FileMode.CreateNew);
        encoder.Save(image);
    }

    private sealed class SamplerDrawingVisual : DrawingVisual
    {
        public SamplerDrawingVisual(bool nearest)
        { if (nearest) VisualBitmapScalingMode = BitmapScalingMode.NearestNeighbor; }
        public BitmapScalingMode EmittedScalingMode => VisualBitmapScalingMode;
        public void ResetScalingMode() => VisualBitmapScalingMode = BitmapScalingMode.Unspecified;
    }

    private sealed class SamplerContainerVisual : ContainerVisual
    {
        public SamplerContainerVisual(bool nearest)
        { if (nearest) VisualBitmapScalingMode = BitmapScalingMode.NearestNeighbor; }
        public BitmapScalingMode EmittedScalingMode => VisualBitmapScalingMode;
    }

    private sealed class SamplerEffect : ShaderEffect
    {
        private static readonly DependencyProperty NearestInput = RegisterPixelShaderSamplerProperty(
            "NearestInput", typeof(SamplerEffect), 0, SamplingMode.NearestNeighbor);
        private static readonly DependencyProperty LinearInput = RegisterPixelShaderSamplerProperty(
            "LinearInput", typeof(SamplerEffect), 0, SamplingMode.Bilinear);
        private readonly ImageBrush brush;
        private readonly SamplingMode sampling;

        public SamplerEffect(ImageBrush brush, SamplingMode sampling)
        {
            this.brush = brush; this.sampling = sampling;
            var bytes = new byte[SamplerWords.Length * 4];
            for (int i = 0; i < SamplerWords.Length; ++i) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), SamplerWords[i]);
            var shader = new PixelShader { ShaderRenderMode = ShaderRenderMode.SoftwareOnly };
            using (var stream = new MemoryStream(bytes, writable: false)) shader.SetStreamSource(stream);
            PixelShader = shader;
            var property = sampling == SamplingMode.NearestNeighbor ? NearestInput : LinearInput;
            SetValue(property, brush); UpdateShaderValue(property);
        }

        protected override Freezable CreateInstanceCore() => new SamplerEffect(brush, sampling);
    }
}
