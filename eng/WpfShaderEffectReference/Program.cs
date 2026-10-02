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
    private const int ExpectedCases = 25;
    private static int invalidShaders;

    [STAThread]
    private static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 4 ||
            !string.Equals(args[1], RuntimeInformation.ProcessArchitecture.ToString(), StringComparison.OrdinalIgnoreCase) ||
            args[2].Length != 40 || !args[2].All(Uri.IsHexDigit))
            throw new ArgumentException("Expected receipt path, actual Windows architecture, exact source commit and explicit capture mode.");
        bool unavailableControl = args[3] switch
        {
            "shader-pixels" => false,
            "unsupported-software-control" => true,
            _ => throw new ArgumentException("Unknown original capture mode.")
        };
        Assembly presentation = typeof(ShaderEffect).Assembly;
        if (Convert.ToHexString(presentation.GetName().GetPublicKeyToken() ?? []) != "31BF3856AD364E35")
            throw new InvalidOperationException("Only original Microsoft PresentationCore is a reference.");
        string output = Path.GetFullPath(args[0]);
        string directory = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(directory) || File.Exists(output))
            throw new InvalidOperationException("Expected an existing evidence directory and a new receipt.");
        var capabilities = new
        {
            SourceCommit = args[2], Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription,
            PresentationIdentity = presentation.FullName, PresentationCore = FileIdentity(presentation.Location),
            SoftwarePixelShader20 = RenderCapability.IsPixelShaderVersionSupportedInSoftware(2, 0),
            HardwarePixelShader20 = RenderCapability.IsPixelShaderVersionSupported(2, 0),
            RenderTier = RenderCapability.Tier >> 16,
            Sse2 = System.Runtime.Intrinsics.X86.Sse2.IsSupported
        };
        using (var capabilityOutput = new FileStream(Path.Combine(directory, "capabilities.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(capabilityOutput, capabilities, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(JsonSerializer.Serialize(capabilities));
        if (unavailableControl)
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64 || capabilities.SoftwarePixelShader20 || capabilities.Sse2)
                throw new InvalidOperationException("The explicit native ARM64 unavailable-software control no longer applies.");
        }
        else if (!capabilities.SoftwarePixelShader20)
            throw new InvalidOperationException("Original software shader pixels are unavailable; no reference may be qualified.");
        PixelShader.InvalidPixelShaderEncountered += OnInvalidShader;
        var timer = Stopwatch.StartNew();
        var observations = new List<object>();
        int unmatchedShaderCases = 0;
        try
        {
            foreach (OriginalCase input in Cases())
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Original shader reference exceeded 60 seconds.");
                Console.WriteLine($"Original ps_2_0 input {observations.Count}: {input.Name}");
                using (var evidence = new FileStream(Path.Combine(directory, input.Name + ".input.json"), FileMode.CreateNew))
                    JsonSerializer.Serialize(evidence, input, new JsonSerializerOptions { WriteIndented = true });
                var visual = CreateVisual(input);
                byte[]? first = null;
                for (int replay = 0; replay < 3; ++replay)
                {
                    var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(replay == 2 ? CreateVisual(input) : visual);
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    if (Volatile.Read(ref invalidShaders) != 0)
                        throw new InvalidOperationException($"Original WPF rejected {input.Name}.");
                    var pixels = new byte[64 * 64 * 4];
                    bitmap.CopyPixels(pixels, 64 * 4, 0);
                    if (replay == 0)
                    {
                        // Preserve actual source pixels even when the strict
                        // color assertion fails. This is not a passing receipt.
                        using var raw = new FileStream(Path.Combine(directory, input.Name + ".bgra"), FileMode.CreateNew);
                        raw.Write(pixels);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var image = new FileStream(Path.Combine(directory, input.Name + ".png"), FileMode.CreateNew);
                        encoder.Save(image);
                    }
                    if (unavailableControl) AssertUnavailableSoftwarePixels(input.Name, pixels);
                    else AssertPixels(input, pixels);
                    if (first != null && !first.AsSpan().SequenceEqual(pixels))
                        throw new InvalidOperationException($"{input.Name}: cold/warm/independent pixels changed.");
                    if (first == null)
                    {
                        first = pixels;
                    }
                }
                bool matchesShader = MatchesShaderPixels(input, first!);
                if (!matchesShader) ++unmatchedShaderCases;
                observations.Add(new { input.Name, input.Words, input.Constants, input.ExpectedRgb,
                    Replays = 3, PixelWidth = 64, PixelHeight = 64, PixelFormat = "Pbgra32", Pixels = first,
                    MatchesShaderPixels = matchesShader, PixelSha256 = Convert.ToHexString(SHA256.HashData(first!)) });
            }
            if (observations.Count != ExpectedCases) throw new InvalidOperationException("Original shader inventory is incomplete.");
            if (unmatchedShaderCases != (unavailableControl ? 24 : 0))
                throw new InvalidOperationException("Original reference/control outcome inventory changed.");
            var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Where(module => string.Equals(module.ModuleName, "wpfgfx_cor3.dll", StringComparison.OrdinalIgnoreCase))
                .Select(module => FileIdentity(module.FileName)).ToArray();
            if (modules.Length != 1) throw new InvalidOperationException("Original native WPF renderer identity is missing or ambiguous.");
            CaptureImageSamplers(directory, args[2], unavailableControl, timer);
            CaptureShaderPadding(directory, args[2], unavailableControl, timer);
            CaptureShaderLocalFrames(directory, args[2], unavailableControl, timer);
            CaptureShaderAxisDecomposition(directory, args[2], unavailableControl, timer);
            CaptureShaderAffineMath(directory, args[2], timer);
            CaptureTransformPrimitiveMath(directory, args[2], timer);
            var receipt = new
            {
                Schema = 1, SourceCommit = args[2], CaseCount = observations.Count, Cases = observations,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription, OperatingSystem = RuntimeInformation.OSDescription,
                PresentationIdentity = presentation.FullName, PresentationCore = FileIdentity(presentation.Location),
                Producer = FileIdentity(Assembly.GetExecutingAssembly().Location), NativeModules = modules,
                ShaderModel = "ps_2_0", ShaderRenderMode = "SoftwareOnly", InvalidShaders = invalidShaders,
                CaptureMode = args[3], QualifiedShaderCases = unavailableControl ? 0 : observations.Count,
                UnmatchedShaderCases = unmatchedShaderCases,
                Capabilities = capabilities,
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                Qualification = unavailableControl
                    ? "Original ARM64 software shader unavailability control only; zero shader pixel cases qualified. Native ARM64 GPU pixel gates remain required."
                    : "Original Microsoft WPF software-reference pixels only; not ps_3_0, native provider, package or application qualification."
            };
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, receipt, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine($"Original WPF {args[3]}: {observations.Count} cases, 75 replays, 0 skipped, {(unavailableControl ? 0 : observations.Count)} shader cases qualified; {args[1]}.");
        }
        finally { PixelShader.InvalidPixelShaderEncountered -= OnInvalidShader; }
    }

    private static void OnInvalidShader(object? sender, EventArgs args) => Interlocked.Increment(ref invalidShaders);

    private static object FileIdentity(string path)
    {
        using var file = File.OpenRead(path);
        return new { Path = Path.GetFullPath(path), Bytes = file.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(file)), Version = FileVersionInfo.GetVersionInfo(path).FileVersion };
    }

    private static ContainerVisual CreateVisual(OriginalCase input)
    {
        var root = new ContainerVisual();
        var background = new DrawingVisual();
        using (DrawingContext drawing = background.RenderOpen())
            drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 64, 64));
        root.Children.Add(background);
        var clip = new ContainerVisual { Clip = new RectangleGeometry(new Rect(16, 12, 16, 16)) };
        var source = new DrawingVisual { Effect = new OriginalEffect(input) };
        using (DrawingContext drawing = source.RenderOpen())
            drawing.DrawRectangle(Brushes.White, null, new Rect(8, 10, 32, 24));
        clip.Children.Add(source);
        root.Children.Add(clip);
        return root;
    }

    private static void AssertPixels(OriginalCase input, byte[] pixels)
    {
        for (int y = 0; y < 64; ++y)
        for (int x = 0; x < 64; ++x)
        {
            bool inside = x >= 16 && x < 32 && y >= 12 && y < 28;
            for (int channel = 0; channel < 4; ++channel)
            {
                byte expected = channel == 3 ? (byte)255 : inside ? input.ExpectedRgb[2 - channel] : (byte)0;
                byte actual = pixels[(y * 64 + x) * 4 + channel];
                if (actual != expected)
                    throw new InvalidOperationException($"{input.Name}: ({x},{y}) BGRA[{channel}]={actual}, expected {expected}.");
            }
        }
    }

    private static bool MatchesShaderPixels(OriginalCase input, byte[] pixels)
    {
        for (int y = 0; y < 64; ++y)
        for (int x = 0; x < 64; ++x)
        for (int channel = 0; channel < 4; ++channel)
        {
            bool inside = x >= 16 && x < 32 && y >= 12 && y < 28;
            byte expected = channel == 3 ? (byte)255 : inside ? input.ExpectedRgb[2 - channel] : (byte)0;
            if (pixels[(y * 64 + x) * 4 + channel] != expected) return false;
        }
        return true;
    }

    private static void AssertUnavailableSoftwarePixels(string name, byte[] pixels)
    {
        // This is a separate negative source-capability control, never an
        // alternative successful shader oracle. Every original case remains.
        for (int y = 0; y < 64; ++y)
        for (int x = 0; x < 64; ++x)
        for (int channel = 0; channel < 4; ++channel)
        {
            byte expected = channel == 3 || (x >= 16 && x < 32 && y >= 12 && y < 28) ? (byte)255 : (byte)0;
            if (pixels[(y * 64 + x) * 4 + channel] != expected)
                throw new InvalidOperationException($"{name}: unavailable software path did not preserve original input at ({x},{y}) BGRA[{channel}].");
        }
    }

    private static List<uint> Prefix() => [0xFFFF0200, 0x0200001F, 0x80000000, 0xB0030000,
        0x0200001F, 0x90000000, 0xA00F0800, 0x03000042, 0x800F0000, 0xB0E40000, 0xA0E40800];

    private static IEnumerable<OriginalCase> Cases()
    {
        foreach (float value in new[] { 1.0F, 0.5F })
        {
            var words = Prefix();
            words.AddRange([0x03000005, 0x800F0800, 0x80E40000, 0xA0E40000, 0xFFFF]);
            var constants = new float[20];
            Array.Fill(constants, value, 0, 4);
            byte expected = value == 1 ? (byte)255 : (byte)128;
            yield return new(value == 1 ? "multiply-one" : "multiply-half", words.ToArray(), constants, [expected, expected, expected]);
        }
        for (uint axis = 0; axis < 3; ++axis)
        {
            var words = Prefix();
            words.AddRange([0x02000001, 0x800F0001, 0x80E40000,
                0x03000005, 0x800F0000, 0x80E40000, 0xA0E40000,
                0x03000021, 0x80070001, 0x80E40000, 0xA0E40001,
                0x02000001, 0x800F0800, 0x80E40001, 0xFFFF]);
            var constants = new float[20];
            constants[(axis + 1) % 3] = constants[3] = 1;
            constants[4 + (axis + 2) % 3] = 1;
            var expected = new byte[3];
            expected[axis] = 255;
            yield return new($"cross-{axis}", words.ToArray(), constants, expected);
        }
        foreach (uint opcode in new uint[] { 20, 21, 22, 23, 24 })
        foreach (bool swapped in new[] { false, true })
        foreach (bool negative in new[] { false, true })
        {
            int columns = opcode <= 21 ? 4 : 3;
            int rows = opcode is 20 or 22 ? 4 : opcode == 24 ? 2 : 3;
            uint mask = (1U << rows) - 1;
            uint vector = 0x80000000U | ((swapped ? 0xE1U : 0xE4U) << 16) | (negative ? 0x01000000U : 0);
            var words = Prefix();
            words.AddRange([0x02000001, 0x800F0001, 0x80E40000,
                0x03000005, 0x800F0000, 0x80E40000, 0xA0E40000,
                0x03000000U | opcode, 0x80000001U | (mask << 16) | (negative ? 0x00100000U : 0), vector, 0xA0E40001,
                0x02000001, 0x800F0800, 0x80E40001, 0xFFFF]);
            var constants = new float[20];
            constants[0] = constants[3] = negative ? -1 : 1;
            int component = swapped ? 1 : 0;
            for (int row = 0; row < rows; ++row)
            {
                bool zero = row == 1;
                constants[4 * (row + 1) + component] = columns == 3 && zero ? 0 : 1;
                constants[4 * (row + 1) + 3] = columns == 3 ? (zero ? 1 : -1) : (zero ? -1 : 0);
            }
            yield return new($"matrix-{opcode}-{(swapped ? "yx" : "xy")}-{(negative ? "neg-sat" : "plain")}",
                words.ToArray(), constants, [255, 0, 255]);
        }
    }

    private sealed record OriginalCase(string Name, uint[] Words, float[] Constants, byte[] ExpectedRgb);

    private sealed class OriginalEffect : ShaderEffect
    {
        private readonly OriginalCase input;
        private static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
            "Input", typeof(OriginalEffect), 0, SamplingMode.NearestNeighbor);
        private static readonly DependencyProperty[] ConstantProperties = RegisterConstants();

        public OriginalEffect(OriginalCase input)
        {
            this.input = input;
            var bytes = new byte[input.Words.Length * 4];
            for (int i = 0; i < input.Words.Length; ++i)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), input.Words[i]);
            var shader = new PixelShader { ShaderRenderMode = ShaderRenderMode.SoftwareOnly };
            using (var stream = new MemoryStream(bytes, writable: false)) shader.SetStreamSource(stream);
            PixelShader = shader;
            SetValue(InputProperty, ImplicitInput);
            UpdateShaderValue(InputProperty);
            for (int i = 0; i < ConstantProperties.Length; ++i)
            {
                int at = 4 * i;
                SetValue(ConstantProperties[i], new Point4D(input.Constants[at], input.Constants[at + 1],
                    input.Constants[at + 2], input.Constants[at + 3]));
                UpdateShaderValue(ConstantProperties[i]);
            }
        }

        protected override Freezable CreateInstanceCore() => new OriginalEffect(input);

        private static DependencyProperty[] RegisterConstants()
        {
            var properties = new DependencyProperty[5];
            for (int i = 0; i < properties.Length; ++i)
                properties[i] = DependencyProperty.Register($"Constant{i}", typeof(Point4D), typeof(OriginalEffect),
                    new UIPropertyMetadata(default(Point4D), PixelShaderConstantCallback(i)));
            return properties;
        }
    }
}
