using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Svg;

// Diagnostic capture only: preserve each actual outcome, including a worker
// crash/timeout. This program never turns one exception type into another.
if (args.Length != 3 && args.Length != 4)
    throw new ArgumentException("Expected corpus root, fresh output directory, architecture, and optional worker index.");
string corpus = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
if (!StringComparer.Ordinal.Equals(args[2], RuntimeInformation.ProcessArchitecture.ToString()))
    throw new InvalidOperationException("SVG reference process architecture mismatch.");
var fixtures = new[]
{
    new Fixture("resvg|tests/structure/image/recursive-2",
        "externals/resvg/crates/resvg/tests/tests/structure/image/recursive-2.svg",
        "externals/resvg/crates/resvg/tests/tests/structure/image/recursive-2.png",
        "tests/Svg.Skia.UnitTests/ChromeReference/resvg/tests/structure/image/recursive-2.png"),
    new Fixture("w3c|struct-image-12-b",
        "externals/W3C_SVG_11_TestSuite/W3C_SVG_11_TestSuite/svg/struct-image-12-b.svg",
        "externals/W3C_SVG_11_TestSuite/W3C_SVG_11_TestSuite/png/struct-image-12-b.png",
        "tests/Svg.Skia.UnitTests/ChromeReference/W3C/struct-image-12-b.png"),
    new Fixture("w3c|struct-use-08-b",
        "externals/W3C_SVG_11_TestSuite/W3C_SVG_11_TestSuite/svg/struct-use-08-b.svg",
        "externals/W3C_SVG_11_TestSuite/W3C_SVG_11_TestSuite/png/struct-use-08-b.png",
        "tests/Svg.Skia.UnitTests/ChromeReference/W3C/struct-use-08-b.png")
};

if (args.Length == 4)
{
    int index = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    Fixture fixture = fixtures[index];
    string source = Path.Combine(corpus, fixture.Svg);
    string reference = Path.Combine(corpus, fixture.Chrome);
    if (!File.Exists(reference)) reference = Path.Combine(corpus, fixture.Png);
    (int width, int height) = ReadPngDimensions(reference);
    Assembly drawing = typeof(Graphics).Assembly;
#if MICROSOFT_DRAWING_REFERENCE
    string drawingPath = drawing.Location.Replace('\\', '/');
    if (!OperatingSystem.IsWindows() ||
        !drawingPath.Contains("/shared/Microsoft.WindowsDesktop.App/", StringComparison.Ordinal) ||
        drawing.GetName().Name != "System.Drawing.Common" || drawing.GetName().Version?.Major != 10 ||
        Convert.ToHexString(drawing.GetName().GetPublicKeyToken()!) != "CC7B13FFCD2DDD51")
        throw new InvalidOperationException($"The reference did not load Microsoft Windows Desktop System.Drawing: {drawing.FullName}, {drawing.Location}.");
    object fontEvidence = "Microsoft Windows Desktop font resolver";
#else
    byte[] drawingKey = drawing.GetName().GetPublicKey() ?? [];
    byte[] providerKey = typeof(ProGPU.Backend.WgpuContext).Assembly.GetName().GetPublicKey() ?? [];
    if (drawingKey.Length == 0 || !drawingKey.AsSpan().SequenceEqual(providerKey))
        throw new InvalidOperationException("The portable probe did not load ProGPU System.Drawing.");
    object fontEvidence = RegisterPortableFonts(corpus, includeW3c: index != 0);
#endif
    WriteNew($"{index}-identity.json", new
    {
        fixture.Key, Width = width, Height = height,
        FixtureSha256 = HashFile(source), ReferenceSha256 = HashFile(reference),
        SourceTextSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            File.ReadAllText(source).Replace("\r\n", "\n", StringComparison.Ordinal)))),
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Runtime = RuntimeInformation.FrameworkDescription,
        Drawing = Describe(drawing), Svg = Describe(typeof(SvgDocument).Assembly), Fonts = fontEvidence
    });
    SvgDocument.SkipGdiPlusCapabilityCheck = true;
    bool captured = false, capturedDrawing = false, recording = false;
    Exception? diagnosticError = null;
    void CaptureFirstException(object? sender, FirstChanceExceptionEventArgs eventArgs)
    {
        if (recording || diagnosticError != null || (captured && capturedDrawing)) return;
        recording = true; // Diagnostic I/O cannot recursively enter this callback.
        try
        {
            if (!captured)
            {
                captured = true;
                WriteNew($"{index}-first-exception.json", DescribeException(eventArgs.Exception));
            }
            if (!capturedDrawing && new StackTrace(eventArgs.Exception).GetFrames().Any(
                frame => frame.GetMethod()?.DeclaringType?.Assembly == drawing))
            {
                capturedDrawing = true;
                WriteNew($"{index}-first-drawing-exception.json", DescribeException(eventArgs.Exception));
            }
        }
        catch (Exception error)
        {
            diagnosticError = error; // Never replace the renderer's exception during unwinding.
        }
        finally { recording = false; }
    }
    AppDomain.CurrentDomain.FirstChanceException += CaptureFirstException;
    object result;
    try
    {
        var document = SvgDocument.Open<SvgDocument>(source);
        using var bitmap = document.Draw(width, height)
            ?? throw new InvalidOperationException("SVG rendered an empty bitmap.");
        result = new { Outcome = "Rendered", bitmap.Width, bitmap.Height };
    }
    catch (Exception exception)
    {
        result = new { Outcome = "Exception", Exception = DescribeException(exception) };
    }
    finally
    {
        AppDomain.CurrentDomain.FirstChanceException -= CaptureFirstException;
    }
    if (diagnosticError != null) throw new IOException("Exception evidence could not be recorded.", diagnosticError);
    WriteNew($"{index}-result.json", result);
    return;
}

if (Directory.Exists(output)) throw new IOException("Evidence directory already exists.");
Directory.CreateDirectory(output);
var outcomes = new List<object>();
for (int index = 0; index < fixtures.Length; index++)
{
    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(corpus);
    start.ArgumentList.Add(output);
    start.ArgumentList.Add(args[2]);
    start.ArgumentList.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    using var process = Process.Start(start) ?? throw new IOException("SVG worker did not start.");
    await using var stdout = new FileStream(Path.Combine(output, $"{index}-stdout.log"), FileMode.CreateNew);
    await using var stderr = new FileStream(Path.Combine(output, $"{index}-stderr.log"), FileMode.CreateNew);
    Task copyOutput = process.StandardOutput.BaseStream.CopyToAsync(stdout);
    Task copyError = process.StandardError.BaseStream.CopyToAsync(stderr);
    bool timedOut = !process.WaitForExit(30_000);
    if (timedOut)
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }
    await Task.WhenAll(copyOutput, copyError);
    if (!File.Exists(Path.Combine(output, $"{index}-identity.json")))
        throw new InvalidOperationException($"SVG worker {index} failed before identity capture; see logs.");
    bool hasResult = File.Exists(Path.Combine(output, $"{index}-result.json"));
    if ((process.ExitCode == 0 && !timedOut) != hasResult)
        throw new InvalidOperationException($"SVG worker {index} has an inconsistent completion receipt.");
    outcomes.Add(new { fixtures[index].Key, process.ExitCode, TimedOut = timedOut, HasResult = hasResult });
    Console.WriteLine($"{fixtures[index].Key}: exit={process.ExitCode}, timeout={timedOut}, result={hasResult}");
}
WriteNew("workers.json", outcomes);

void WriteNew(string name, object value)
{
    using var stream = new FileStream(Path.Combine(output, name), FileMode.CreateNew);
    JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
}

static object DescribeException(Exception exception) => new
{
    Type = exception.GetType().FullName, exception.Message,
    Parameter = (exception as ArgumentException)?.ParamName, Detail = exception.ToString()
};

static object Describe(Assembly assembly) => new
{
    Name = assembly.FullName, Path = assembly.Location, Sha256 = HashFile(assembly.Location)
};

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static (int Width, int Height) ReadPngDimensions(string path)
{
    using var stream = File.OpenRead(path);
    Span<byte> header = stackalloc byte[24];
    stream.ReadExactly(header);
    ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
    if (!header[..8].SequenceEqual(signature) || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
        throw new InvalidDataException("Expected a PNG reference.");
    int width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
    int height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
    if (width <= 0 || height <= 0) throw new InvalidDataException("Invalid PNG reference dimensions.");
    return (width, height);
}

#if !MICROSOFT_DRAWING_REFERENCE
static object RegisterPortableFonts(string corpusRoot, bool includeW3c)
{
    // Match CorpusFonts.Register in eng/SystemDrawing.SvgCorpus/Program.cs:
    // the same pinned files, source ordering, face enumeration and strict count.
    var directories = new List<string>
    {
        Path.Combine(corpusRoot, "externals/resvg/crates/resvg/tests/fonts")
    };
    if (includeW3c)
        directories.Add(Path.Combine(corpusRoot, "externals/W3C_SVG_11_TestSuite/W3C_SVG_11_TestSuite/resources"));
    string[] files = directories.SelectMany(path => Directory.EnumerateFiles(path))
        .Where(path => Path.GetExtension(path).Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                       Path.GetExtension(path).Equals(".otf", StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.Ordinal).ToArray();
    int expected = includeW3c ? 22 : 21;
    if (files.Length != expected) throw new InvalidDataException("Pinned SVG font inventory changed.");
    int faces = 0;
    var hashes = new List<string>();
    foreach (string path in files)
    {
        byte[] bytes = File.ReadAllBytes(path);
        hashes.Add($"{Path.GetRelativePath(corpusRoot, path).Replace('\\', '/')}|{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
        for (int face = 0; face < 64; face++)
        {
            try
            {
                ProGPU.Text.FontApi.Manager.RegisterFont(new ProGPU.Text.TtfFont(bytes, face));
                faces++;
            }
            catch (Exception error) when (error is InvalidDataException or FormatException or ArgumentOutOfRangeException)
            {
                if (face == 0) throw new InvalidDataException($"Pinned SVG font failed: {path}", error);
                break;
            }
        }
    }
    if (faces != expected) throw new InvalidDataException("Pinned SVG font face count changed.");
    return new { SourceFileCount = files.Length, LoadedFaceCount = faces,
        InventorySha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', hashes)))).ToLowerInvariant() };
}
#endif

sealed record Fixture(string Key, string Svg, string Png, string Chrome);
