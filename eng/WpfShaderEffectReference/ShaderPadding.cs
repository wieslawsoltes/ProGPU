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
using System.Windows.Media.Media3D;
using System.Windows.Threading;

internal static partial class Program
{
    private static void CaptureShaderPadding(string directory, string commit, bool unavailable, Stopwatch timer)
    {
        double startedMilliseconds = timer.Elapsed.TotalMilliseconds;
        int arithmeticControls = ShaderPaddingOracle.VerifyArithmeticControls();
        var retained = new PaddingVisualState();
        var observations = new List<object>();
        var failures = new List<string>();
        byte[]? constantBefore = null, constantExpanded = null;
        OriginalPaddingEffect? constantOwner = null;
        int mutationChecks = 0;
        foreach (PaddingCase input in ShaderPaddingOracle.Cases())
        {
            CheckPaddingDeadline(timer);
            retained.Prepare(input);
            if (input.Name == "padding-constant-generation-zero") constantOwner = retained.Effect;
            if (input.Name is "padding-constant-generation-expand" or "padding-constant-generation-reset")
            {
                if (!ReferenceEquals(constantOwner, retained.Effect))
                    throw new InvalidOperationException("Padding generation transition replaced its original effect.");
                ++mutationChecks;
            }
            var description = new
            {
                input.Name, input.NativeVariant, Output = input.Output.ToString(),
                OriginalPadding = new[] { input.Top, input.Bottom, input.Left, input.Right },
                PaddingOrder = "Top,Bottom,Left,Right", OriginalDoubleBits = retained.Effect.PaddingBits(),
                ShaderWords = PaddingWords(input.Output), ShaderRenderMode = "SoftwareOnly", SamplerRegister = 0,
                ShaderSampling = "NearestNeighbor", ConstantRegister = 0, Constant = new[] { .25, .5, .75, 1 },
                DerivativeRegister = input.Output == PaddingOutput.Derivatives ? 0 : -1,
                SourceDpi = input.Dpi, TargetDpi = 96 * input.Dpi, PixelWidth = 64, PixelHeight = 64,
                SourceContentBounds = VisualTreeHelper.GetContentBounds(retained.Source),
                FinalSourceClip = retained.Clip.Clip!.Bounds,
                ActualVisualBitmapScalingMode = retained.Source.EmittedScalingMode.ToString(),
                ExpectedPhysicalFrame = input.Frame,
                ImageBrush = input.Output == PaddingOutput.Image ? new
                {
                    PixelWidth = 2, PixelHeight = 1, DpiX = 144, DpiY = 192, PixelFormat = "Pbgra32",
                    Viewbox = new[] { 0, 0, 1, 1 }, Viewport = new[] { 0, 0, 1, 1 },
                    ViewboxUnits = "RelativeToBoundingBox", ViewportUnits = "RelativeToBoundingBox",
                    Stretch = "Fill", TileMode = "None", Opacity = 1,
                    SourcePixelSha256 = Convert.ToHexString(SHA256.HashData(PaddingImagePixels))
                } : null,
                SourceMutationOrdinal = retained.Generation,
                Meaning = "Original public ShaderEffect padding, actual visual options and drawing; no ProGPU runtime loaded."
            };
            using (var file = new FileStream(Path.Combine(directory, input.Name + ".input.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(file, description, new JsonSerializerOptions { WriteIndented = true });
            if (input.Output == PaddingOutput.Image)
                SaveSamplerBitmap(directory, input.Name + ".source", retained.Effect.SourceBitmap!, PaddingImagePixels);
            byte[]? first = null;
            var replayHashes = new List<string>();
            for (int replay = 0; replay < 3; ++replay)
            {
                CheckPaddingDeadline(timer);
                PaddingVisualState visual = retained;
                if (replay == 2) { visual = new PaddingVisualState(); visual.Prepare(input); }
                var bitmap = new RenderTargetBitmap(64, 64, 96 * input.Dpi, 96 * input.Dpi, PixelFormats.Pbgra32);
                bitmap.Render(visual.Root);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                if (Volatile.Read(ref invalidShaders) != 0) throw new InvalidOperationException($"Original WPF rejected {input.Name}.");
                var pixels = new byte[64 * 64 * 4];
                bitmap.CopyPixels(pixels, 64 * 4, 0);
                SaveSamplerBitmap(directory, input.Name + "-replay-" + replay, bitmap, pixels);
                try { AssertPaddingPixels(input, pixels, unavailable); }
                catch (InvalidOperationException error) { failures.Add($"Replay {replay}: {error.Message}"); }
                if (first != null && !first.AsSpan().SequenceEqual(pixels))
                    failures.Add($"{input.Name}: retained/warm/independent padding pixels differ.");
                first ??= pixels;
                replayHashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
            }
            if (input.Name == "padding-constant-generation-zero") constantBefore = first;
            if (input.Name == "padding-constant-generation-expand") constantExpanded = first;
            if (input.Name == "padding-constant-generation-reset")
            {
                if (constantBefore == null || constantExpanded == null || !constantBefore.AsSpan().SequenceEqual(first))
                    failures.Add("Original constant padding reset did not restore every original pixel.");
                if (!unavailable && (constantBefore == null || constantBefore.AsSpan().SequenceEqual(constantExpanded)))
                    failures.Add("Original same-effect padding expansion produced no border change.");
                mutationChecks += 2;
            }
            observations.Add(new { Input = description, Replays = 3, Pixels = first, PixelSha256 = replayHashes[0], ReplaySha256 = replayHashes });
        }
        if (observations.Count != 13 || mutationChecks != 4 || arithmeticControls != 31)
            throw new InvalidOperationException("Original padding inventory or generation controls are incomplete.");
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(module => string.Equals(module.ModuleName, "wpfgfx_cor3.dll", StringComparison.OrdinalIgnoreCase))
            .Select(module => FileIdentity(module.FileName)).ToArray();
        if (modules.Length != 1) throw new InvalidOperationException("Original padding renderer identity is missing or ambiguous.");
        var receipt = new
        {
            Schema = 1, SourceCommit = commit, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PresentationIdentity = typeof(ShaderEffect).Assembly.FullName,
            PresentationCore = FileIdentity(typeof(ShaderEffect).Assembly.Location),
            Producer = FileIdentity(Assembly.GetExecutingAssembly().Location), NativeModules = modules,
            CaseCount = observations.Count, Cases = observations, Replays = 39, NativeAdmittedCases = 10,
            ArithmeticControls = arithmeticControls, MutationChecks = mutationChecks, InvalidShaders = invalidShaders,
            CaptureMode = unavailable ? "unsupported-software-control" : "shader-pixels",
            QualifiedShaderCases = unavailable || failures.Count != 0 ? 0 : observations.Count,
            Failures = failures, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds - startedMilliseconds,
            Qualification = unavailable
                ? "Original ARM64 unavailable-software control only; zero padding shader cases qualified."
                : "Original Microsoft WPF SOFTWARE padding reference only; no fractional/cropped source, native hardware, provider, package or application admission."
        };
        using (var output = new FileStream(Path.Combine(directory, failures.Count == 0 ? "shader-padding.json" : "shader-padding.failed.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(output, receipt, new JsonSerializerOptions { WriteIndented = true });
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine($"Original padding: 13 cases / 39 replays / 4 generation checks; {(unavailable ? 0 : 13)} source shader cases qualified.");
    }

    private static void CheckPaddingDeadline(Stopwatch timer)
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Original padding reference exceeded the shared 60 seconds.");
    }

    private static void AssertPaddingPixels(PaddingCase input, byte[] pixels, bool unavailable)
    {
        for (int y = 0; y < 64; ++y)
        for (int x = 0; x < 64; ++x)
        for (int channel = 0; channel < 4; ++channel)
        {
            byte expected = ShaderPaddingOracle.Expected(input, x, y, channel, unavailable);
            byte actual = pixels[(y * 64 + x) * 4 + channel];
            if (actual != expected)
                throw new InvalidOperationException($"{input.Name}: ({x},{y}) BGRA[{channel}]={actual}, expected {expected}.");
        }
    }

    private static readonly byte[] PaddingImagePixels = [0, 0, 255, 255, 0, 255, 0, 255];

    private static uint[] PaddingWords(PaddingOutput output)
    {
        var words = new List<uint>
        {
            0xffff0200, 0x0200001f, 0x80000000, 0xb0030000,
            0x0200001f, 0x90000000, 0xa00f0800,
            0x05000051, 0xa00f0002, 0, 0, 0, 0x3f800000,
            0x03000042, 0x800f0000, 0xb0e40000, 0xa0e40800
        };
        if (output == PaddingOutput.Uv)
            words.AddRange([0x02000001, 0x80030800, 0xb0e40000, 0x02000001, 0x800c0800, 0xa0e40002]);
        else if (output == PaddingOutput.Derivatives)
            words.AddRange([0x02000001, 0x80070800, 0xa05c0000, 0x02000001, 0x80080800, 0xa0ff0002]);
        else words.AddRange([0x02000001, 0x800f0800, output == PaddingOutput.Constant ? 0xa0e40000U : 0x80e40000U]);
        words.Add(0xffff);
        return words.ToArray();
    }

    private sealed class PaddingVisualState
    {
        internal readonly ContainerVisual Root = new();
        internal readonly ContainerVisual Clip = new();
        internal readonly PaddingDrawingVisual Source = new();
        private readonly DrawingVisual background = new();
        private readonly Dictionary<PaddingOutput, OriginalPaddingEffect> effects = new();
        private double dpi;
        internal OriginalPaddingEffect Effect { get; private set; } = null!;
        internal int Generation { get; private set; }

        internal PaddingVisualState()
        {
            Root.Children.Add(background); Root.Children.Add(Clip); Clip.Children.Add(Source);
        }

        internal void Prepare(PaddingCase input)
        {
            if (dpi != input.Dpi)
            {
                dpi = input.Dpi;
                using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.Black, null, new(0, 0, 64 / dpi, 64 / dpi));
                using (var drawing = Source.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new(16 / dpi, 16 / dpi, 16 / dpi, 8 / dpi));
                Clip.Clip = new RectangleGeometry(new(14 / dpi, 12 / dpi, 28 / dpi, 20 / dpi));
            }
            if (!effects.TryGetValue(input.Output, out var effect)) effects.Add(input.Output, effect = new OriginalPaddingEffect(input.Output));
            Effect = effect;
            effect.SetPadding(input);
            Source.Effect = effect;
            if (Source.EmittedScalingMode != BitmapScalingMode.NearestNeighbor)
                throw new InvalidOperationException("Original padding visual did not emit nearest bitmap scaling.");
            ++Generation;
        }
    }

    private sealed class PaddingDrawingVisual : DrawingVisual
    {
        internal PaddingDrawingVisual() { VisualBitmapScalingMode = BitmapScalingMode.NearestNeighbor; VisualEdgeMode = EdgeMode.Aliased; }
        internal BitmapScalingMode EmittedScalingMode => VisualBitmapScalingMode;
    }

    private sealed class OriginalPaddingEffect : ShaderEffect
    {
        private static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
            "Input", typeof(OriginalPaddingEffect), 0, SamplingMode.NearestNeighbor);
        private static readonly DependencyProperty ConstantProperty = DependencyProperty.Register(
            "Constant", typeof(Point4D), typeof(OriginalPaddingEffect), new UIPropertyMetadata(default(Point4D), PixelShaderConstantCallback(0)));
        private readonly PaddingOutput output;
        internal BitmapSource? SourceBitmap { get; }

        internal OriginalPaddingEffect(PaddingOutput output)
        {
            this.output = output;
            uint[] words = PaddingWords(output);
            var bytes = new byte[words.Length * 4];
            for (int i = 0; i < words.Length; ++i) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), words[i]);
            var shader = new PixelShader { ShaderRenderMode = ShaderRenderMode.SoftwareOnly };
            using (var stream = new MemoryStream(bytes, writable: false)) shader.SetStreamSource(stream);
            PixelShader = shader;
            if (output == PaddingOutput.Derivatives) DdxUvDdyUvRegisterIndex = 0;
            Brush input = ImplicitInput;
            if (output == PaddingOutput.Image)
            {
                SourceBitmap = BitmapSource.Create(2, 1, 144, 192, PixelFormats.Pbgra32, null, PaddingImagePixels, 8);
                input = new ImageBrush(SourceBitmap)
                {
                    Viewbox = new(0, 0, 1, 1), Viewport = new(0, 0, 1, 1),
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                    Stretch = Stretch.Fill, TileMode = TileMode.None, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center
                };
            }
            SetValue(InputProperty, input); UpdateShaderValue(InputProperty);
            SetValue(ConstantProperty, new Point4D(.25, .5, .75, 1)); UpdateShaderValue(ConstantProperty);
        }

        internal void SetPadding(PaddingCase input)
        {
            PaddingTop = input.Top; PaddingBottom = input.Bottom; PaddingLeft = input.Left; PaddingRight = input.Right;
            if (PaddingTop != input.Top || PaddingBottom != input.Bottom || PaddingLeft != input.Left || PaddingRight != input.Right)
                throw new InvalidOperationException("Original source padding identity changed before capture.");
        }

        internal string[] PaddingBits() => new[] { PaddingTop, PaddingBottom, PaddingLeft, PaddingRight }
            .Select(value => BitConverter.DoubleToInt64Bits(value).ToString("X16")).ToArray();
        protected override Freezable CreateInstanceCore() => new OriginalPaddingEffect(output);
    }
}
