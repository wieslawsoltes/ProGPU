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
        var observations = new List<object>();
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
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
                BitmapScalingMode = "NearestNeighbor", EffectInputBounds = input.Bounds,
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
                bitmap.Render(visual);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                if (Volatile.Read(ref invalidShaders) != 0) throw new InvalidOperationException($"Original WPF rejected {input.Name}.");
                var pixels = new byte[input.Size * input.Size * 4];
                bitmap.CopyPixels(pixels, input.Size * 4, 0);
                if (replay == 0) SaveSamplerBitmap(directory, input.Name, bitmap, pixels);
                AssertSamplerPixels(input, pixels, unavailable);
                if (first != null && !first.AsSpan().SequenceEqual(pixels))
                    throw new InvalidOperationException($"{input.Name}: cold/warm/independent sampler pixels changed.");
                first ??= pixels;
            }
            if (input.NativeVariant >= 0 || input.ExactCenteredCrop) ++independentColors;
            if (input.EquivalentTo != null)
            {
                if (!originals.TryGetValue(input.EquivalentTo, out var prior) || !prior.AsSpan().SequenceEqual(first))
                    throw new InvalidOperationException($"{input.Name}: equivalent original absolute/relative viewboxes differ.");
                ++equivalentPairs;
            }
            originals.Add(input.Name, first!);
            observations.Add(new
            {
                input.Name, Input = description, Replays = 3, Pixels = first,
                PixelSha256 = Convert.ToHexString(SHA256.HashData(first!)),
                IndependentColorOracle = input.NativeVariant >= 0 || input.ExactCenteredCrop,
                input.EquivalentTo
            });
        }
        if (observations.Count != 14 || independentColors != 8 || equivalentPairs != 5)
            throw new InvalidOperationException("Original ImageBrush inventory is incomplete.");
        var receipt = new
        {
            Schema = 1, SourceCommit = commit, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PresentationIdentity = typeof(ShaderEffect).Assembly.FullName,
            PresentationCore = FileIdentity(typeof(ShaderEffect).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location),
            CaseCount = observations.Count, Cases = observations, Replays = 42,
            CaptureMode = unavailable ? "unsupported-software-control" : "shader-pixels",
            QualifiedShaderCases = unavailable ? 0 : observations.Count,
            IndependentColorCases = independentColors, EquivalentViewboxPairs = equivalentPairs,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds - startedMilliseconds,
            Qualification = unavailable
                ? "Original ARM64 unavailable-software control; no sampler shader pixels qualified."
                : "Original Microsoft WPF ImageBrush pixels. Eight independent color cases and five complete-pixel equivalence pairs; not native/source-host/package parity."
        };
        using var output = new FileStream(Path.Combine(directory, "image-samplers.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(output, receipt, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Original ImageBrush: 14 cases / 42 replays / 5 viewbox pairs; {(unavailable ? 0 : 14)} source shader cases qualified.");
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
        public int NativeVariant { get; init; } = -1;
        public bool ExactCenteredCrop { get; init; }
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
                Sampling = sampling, ExactCenteredCrop = !fractional
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
    }

    private static BitmapSource CreateSamplerBitmap(SamplerCase input, out byte[] pixels)
    {
        pixels = new byte[input.Width * input.Height * 4];
        for (int y = 0; y < input.Height; ++y)
        for (int x = 0; x < input.Width; ++x)
        {
            int at = (y * input.Width + x) * 4;
            pixels[at + (x >= input.Width / 2 ? 1 : input.Blue ? 0 : 2)] = 255;
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
        var clip = new ContainerVisual { Clip = new RectangleGeometry(input.Clip) };
        var source = new DrawingVisual { Effect = new SamplerEffect(brush, input.Sampling) };
        RenderOptions.SetBitmapScalingMode(source, BitmapScalingMode.NearestNeighbor);
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
                bool exact = unavailable || !inside || input.NativeVariant >= 0 || input.ExactCenteredCrop;
                if (inside && unavailable) expected = 255;
                else if (inside && input.NativeVariant >= 0)
                {
                    int variant = input.NativeVariant;
                    int stripe = ((x - 8 + 32 - (variant == 2 ? 8 : 0)) / (variant == 0 ? 16 : 8)) & 1;
                    int color = stripe == 1 ? 1 : variant == 3 ? 0 : 2;
                    if (channel == color) expected = variant == 3 ? (byte)255 : (byte)128;
                }
                else if (inside && input.ExactCenteredCrop && y >= 50 && y < 70)
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
