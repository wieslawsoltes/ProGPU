using ACadSharp.Tables;

namespace ProGPU.CAD;

public sealed class CadShxFontDiscoveryOptions
{
    public const int DefaultMaxSearchDirectories = 256;
    public const int DefaultMaxFontRequests = 4_096;
    public const long DefaultMaxTotalFontBytes = 256L * 1024 * 1024;
    public const int DefaultDiagnosticLimit = 256;

    public string? DrawingDirectory { get; init; }
    public IReadOnlyList<string> SupportDirectories { get; init; } = Array.Empty<string>();
    public int MaxSearchDirectories { get; init; } = DefaultMaxSearchDirectories;
    public int MaxFontRequests { get; init; } = DefaultMaxFontRequests;
    public long MaxTotalFontBytes { get; init; } = DefaultMaxTotalFontBytes;
    public int DiagnosticLimit { get; init; } = DefaultDiagnosticLimit;
    public CadShxParseOptions ParseOptions { get; init; } = new();
    public CadShxInterpretOptions InterpretOptions { get; init; } = new();
}

public sealed class CadShxFontDiscoveryResult
{
    private readonly string[] _loadedFontNames;
    private readonly CadDiagnostic[] _diagnostics;

    public int RequestedFontCount { get; }
    public int AlreadyResolvedFontCount { get; }
    public int MissingFontCount { get; }
    public int InvalidFontCount { get; }
    public ReadOnlyMemory<string> LoadedFontNames => _loadedFontNames;
    public ReadOnlyMemory<CadDiagnostic> Diagnostics => _diagnostics;

    internal CadShxFontDiscoveryResult(
        int requestedFontCount,
        int alreadyResolvedFontCount,
        int missingFontCount,
        int invalidFontCount,
        string[] loadedFontNames,
        CadDiagnostic[] diagnostics)
    {
        RequestedFontCount = requestedFontCount;
        AlreadyResolvedFontCount = alreadyResolvedFontCount;
        MissingFontCount = missingFontCount;
        InvalidFontCount = invalidFontCount;
        _loadedFontNames = loadedFontNames;
        _diagnostics = diagnostics;
    }
}

/// <summary>
/// Performs bounded style-driven desktop SHX discovery outside snapshot and
/// rendering hot paths.
/// </summary>
public static class CadShxFontDiscovery
{
    public static async Task<CadShxFontDiscoveryResult> DiscoverAsync(
        CadDocumentSession session,
        CadShxFontCatalog catalog,
        CadShxFontDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(catalog);
        options ??= new CadShxFontDiscoveryOptions();
        ValidateOptions(options);
        string[] directories = GetSearchDirectories(options);
        string[] requestedFonts = GetRequestedFonts(session, options.MaxFontRequests);
        var candidates = new List<Candidate>(requestedFonts.Length);
        var scheduledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<CadDiagnostic>(Math.Min(options.DiagnosticLimit, 16));
        int alreadyResolved = 0;
        int missing = 0;
        int invalid = 0;
        long totalBytes = 0;

        foreach (string fontName in requestedFonts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string registeredName = fontName;
            if (catalog.TryGetMapping(fontName, out string replacement))
            {
                if (catalog.ContainsRegisteredName(replacement))
                {
                    alreadyResolved++;
                    continue;
                }
                registeredName = replacement;
            }
            else if (catalog.ContainsRegisteredName(fontName))
            {
                alreadyResolved++;
                continue;
            }

            FindCandidate(
                directories,
                registeredName,
                out string? foundPath,
                out long foundLength);
            if (foundPath is null &&
                !registeredName.Equals(fontName, StringComparison.OrdinalIgnoreCase))
            {
                if (catalog.ContainsRegisteredName(fontName))
                {
                    alreadyResolved++;
                    continue;
                }
                registeredName = fontName;
                FindCandidate(
                    directories,
                    registeredName,
                    out foundPath,
                    out foundLength);
            }

            if (foundPath is null)
            {
                missing++;
                AddDiagnostic(
                    diagnostics,
                    options.DiagnosticLimit,
                    new CadDiagnostic(
                        CadDiagnosticSeverity.Information,
                        "CADSHX001",
                        $"Standard SHX font '{registeredName}' requested by '{fontName}' was not found in the ordered host search paths."));
                continue;
            }
            if (!scheduledNames.Add(registeredName))
            {
                continue;
            }
            if (foundLength <= 0 || foundLength > options.ParseOptions.MaxFileBytes ||
                foundLength > int.MaxValue)
            {
                invalid++;
                AddDiagnostic(
                    diagnostics,
                    options.DiagnosticLimit,
                    new CadDiagnostic(
                        CadDiagnosticSeverity.Warning,
                        "CADSHX002",
                        $"Standard SHX font '{foundPath}' has an invalid bounded length of {foundLength} bytes."));
                continue;
            }

            totalBytes = checked(totalBytes + foundLength);
            if (totalBytes > options.MaxTotalFontBytes)
            {
                throw new InvalidDataException(
                    $"Discovered SHX font bytes exceed the configured total limit of {options.MaxTotalFontBytes}.");
            }
            candidates.Add(new Candidate(registeredName, foundPath, foundLength));
        }

        var parsed = new List<ParsedFont>(candidates.Count);
        long actualTotalBytes = 0;
        foreach (Candidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] source = await File.ReadAllBytesAsync(candidate.Path, cancellationToken)
                .ConfigureAwait(false);
            actualTotalBytes = checked(actualTotalBytes + source.Length);
            if (source.LongLength != candidate.Length ||
                source.Length == 0 || source.Length > options.ParseOptions.MaxFileBytes ||
                actualTotalBytes > options.MaxTotalFontBytes)
            {
                throw new InvalidDataException(
                    "An SHX file changed size while discovery was reading the bounded font set.");
            }

            try
            {
                CadShxFont font = CadShxFont.Parse(source, options.ParseOptions);
                parsed.Add(new ParsedFont(
                    candidate.FontName,
                    new CadShxGlyphCache(font, options.InterpretOptions)));
            }
            catch (Exception exception) when (
                exception is InvalidDataException or NotSupportedException or
                    ArgumentException or ArithmeticException)
            {
                invalid++;
                AddDiagnostic(
                    diagnostics,
                    options.DiagnosticLimit,
                    new CadDiagnostic(
                        CadDiagnosticSeverity.Warning,
                        "CADSHX003",
                        $"Standard SHX font '{candidate.Path}' was rejected: {exception.Message}"));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var loaded = new List<string>(parsed.Count);
        foreach (ParsedFont font in parsed)
        {
            try
            {
                catalog.Register(font.FontName, font.Cache);
                loaded.Add(font.FontName);
            }
            catch (InvalidOperationException exception)
            {
                if (catalog.ContainsRegisteredName(font.FontName))
                {
                    alreadyResolved++;
                    continue;
                }
                invalid++;
                AddDiagnostic(
                    diagnostics,
                    options.DiagnosticLimit,
                    new CadDiagnostic(
                        CadDiagnosticSeverity.Warning,
                        "CADSHX004",
                        $"Standard SHX font '{font.FontName}' could not be registered: {exception.Message}"));
            }
        }

        return new CadShxFontDiscoveryResult(
            requestedFonts.Length,
            alreadyResolved,
            missing,
            invalid,
            loaded.ToArray(),
            diagnostics.ToArray());
    }

    private static void FindCandidate(
        IReadOnlyList<string> directories,
        string fontName,
        out string? path,
        out long length)
    {
        foreach (string directory in directories)
        {
            string candidatePath = Path.Combine(directory, fontName);
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            path = candidatePath;
            length = new FileInfo(candidatePath).Length;
            return;
        }
        path = null;
        length = 0;
    }

    private static string[] GetRequestedFonts(CadDocumentSession session, int maxFontRequests) =>
        session.Read(document =>
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TextStyle style in document.TextStyles)
            {
                string filename = ExtractFilename(style.Filename);
                if (filename.Length == 0 ||
                    !filename.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!seen.Add(filename))
                {
                    continue;
                }
                result.Add(filename);
                if (result.Count > maxFontRequests)
                {
                    throw new InvalidDataException(
                        $"Document SHX font requests exceed the configured limit of {maxFontRequests}.");
                }
            }
            return result.ToArray();
        });

    private static string[] GetSearchDirectories(CadShxFontDiscoveryOptions options)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        Add(options.DrawingDirectory);
        foreach (string directory in options.SupportDirectories)
        {
            Add(directory);
        }
        return result.ToArray();

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }
            if (!Path.IsPathFullyQualified(directory))
            {
                throw new ArgumentException(
                    $"SHX search directory '{directory}' must be fully qualified.",
                    nameof(options));
            }
            string fullPath = Path.GetFullPath(directory);
            if (seen.Add(fullPath))
            {
                result.Add(fullPath);
                if (result.Count > options.MaxSearchDirectories)
                {
                    throw new InvalidDataException(
                        $"SHX search directories exceed the configured limit of {options.MaxSearchDirectories}.");
                }
            }
        }
    }

    private static string ExtractFilename(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        string trimmed = value.Trim();
        int separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        string filename = separator < 0 ? trimmed : trimmed[(separator + 1)..];
        return filename is "." or ".." ? string.Empty : filename;
    }

    private static void AddDiagnostic(
        List<CadDiagnostic> destination,
        int limit,
        CadDiagnostic diagnostic)
    {
        if (destination.Count < limit)
        {
            destination.Add(diagnostic);
        }
    }

    private static void ValidateOptions(CadShxFontDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.SupportDirectories);
        ArgumentNullException.ThrowIfNull(options.ParseOptions);
        ArgumentNullException.ThrowIfNull(options.InterpretOptions);
        if (options.MaxSearchDirectories <= 0 || options.MaxFontRequests <= 0 ||
            options.MaxTotalFontBytes <= 0 || options.DiagnosticLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "SHX discovery limits must be positive bounded values.");
        }
        if (options.ParseOptions.MaxFileBytes <= 0 ||
            options.ParseOptions.MaxShapeCount <= 0 ||
            options.ParseOptions.MaxShapeCount > ushort.MaxValue ||
            options.ParseOptions.MaxShapeBytes <= 0 ||
            options.ParseOptions.MaxShapeBytes > ushort.MaxValue - 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "SHX parser limits must be finite positive bounded values.");
        }
        if (options.InterpretOptions.MaxCommands <= 0 ||
            options.InterpretOptions.MaxSegments <= 0 ||
            options.InterpretOptions.MaxSubshapeDepth <= 0 ||
            !double.IsFinite(options.InterpretOptions.MaxCoordinateMagnitude) ||
            options.InterpretOptions.MaxCoordinateMagnitude <= 0.0 ||
            !double.IsFinite(options.InterpretOptions.MaxScaleMagnitude) ||
            options.InterpretOptions.MaxScaleMagnitude <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "SHX interpreter limits must be finite positive bounded values.");
        }
    }

    private readonly record struct Candidate(string FontName, string Path, long Length);

    private readonly record struct ParsedFont(string FontName, CadShxGlyphCache Cache);
}
