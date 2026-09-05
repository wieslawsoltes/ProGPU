using ProGPU.Scene;

namespace ProGPU.CAD;

/// <summary>Controls how repeated physical page sets are ordered.</summary>
public enum CadPrintCollationMode : byte
{
    /// <summary>Emits the complete source-page sequence once per copy.</summary>
    Collated = 0,

    /// <summary>Emits every copy of one source page before the next page.</summary>
    Uncollated = 1,
}

/// <summary>One caller-ordered retained page supplied to a print job.</summary>
public readonly record struct CadPrintJobPageSource(
    string Name,
    CadPrintPlan Plan);

/// <summary>Detached physical metadata for one source page in a print job.</summary>
public readonly record struct CadPrintJobPageInfo(
    int SourcePageIndex,
    string Name,
    ulong ContentGeneration,
    string? SourcePageSetupName,
    double PaperWidthMillimeters,
    double PaperHeightMillimeters,
    CadPageRotation Rotation,
    float OutputDpi,
    CadPrintPixelSize PageSizePixels,
    CadPrintPixelRect PrintableAreaPixels,
    CadPrintLineWeightMode LineWeightMode,
    CadPrintTransparencyMode TransparencyMode);

/// <summary>Resolved source and copy identity for one output-page index.</summary>
public readonly record struct CadPrintJobOutputPage(
    int OutputPageIndex,
    int SourcePageIndex,
    int CopyIndex,
    CadPrintJobPageInfo SourcePage);

public sealed class CadPrintJobOptions
{
    public const int DefaultMaxSourcePages = 4_096;
    public const int DefaultMaxOutputPages = 65_536;
    public const int DefaultMaxNameCodeUnits = 4_096;
    public const int DefaultMaxTotalNameCodeUnits = 1_048_576;

    public int Copies { get; init; } = 1;

    public CadPrintCollationMode CollationMode { get; init; } =
        CadPrintCollationMode.Collated;

    /// <summary>Reverses the caller-supplied source-page order before copies.</summary>
    public bool ReversePageOrder { get; init; }

    public int MaxSourcePages { get; init; } = DefaultMaxSourcePages;

    public int MaxOutputPages { get; init; } = DefaultMaxOutputPages;

    public int MaxNameCodeUnits { get; init; } = DefaultMaxNameCodeUnits;

    public int MaxTotalNameCodeUnits { get; init; } =
        DefaultMaxTotalNameCodeUnits;
}

/// <summary>
/// Owns one bounded set of retained physical pages and resolves its output order.
/// </summary>
/// <remarks>
/// Source-page command storage is retained exactly once. Collated and uncollated
/// copies use O(1) arithmetic instead of an output-page mapping array, so job
/// storage is O(P + S) for P source pages and S owned name code units regardless
/// of the copy count. Page-picture creation shares immutable command storage and
/// returns an independent resource lease suitable for managed or native replay.
/// </remarks>
public sealed class CadPrintJob : IDisposable
{
    private readonly object _gate = new();
    private readonly CadPrintJobPageInfo[] _sourcePages;
    private GpuPicture[]? _sourcePictures;

    public int SourcePageCount => _sourcePages.Length;

    public int OutputPageCount { get; }

    public int Copies { get; }

    public CadPrintCollationMode CollationMode { get; }

    public bool ReversePageOrder { get; }

    public ReadOnlyMemory<CadPrintJobPageInfo> SourcePages => _sourcePages;

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _sourcePictures is null;
            }
        }
    }

    internal CadPrintJob(
        GpuPicture[] sourcePictures,
        CadPrintJobPageInfo[] sourcePages,
        int outputPageCount,
        CadPrintJobOptions options)
    {
        _sourcePictures = sourcePictures;
        _sourcePages = sourcePages;
        OutputPageCount = outputPageCount;
        Copies = options.Copies;
        CollationMode = options.CollationMode;
        ReversePageOrder = options.ReversePageOrder;
    }

    public CadPrintJobOutputPage GetOutputPage(int outputPageIndex)
    {
        (int sourcePageIndex, int copyIndex) = Resolve(outputPageIndex);
        return new CadPrintJobOutputPage(
            outputPageIndex,
            sourcePageIndex,
            copyIndex,
            _sourcePages[sourcePageIndex]);
    }

    /// <summary>
    /// Creates an independently owned picture for one resolved output page.
    /// </summary>
    public GpuPicture CreatePagePicture(int outputPageIndex)
    {
        (int sourcePageIndex, _) = Resolve(outputPageIndex);
        lock (_gate)
        {
            GpuPicture[] pictures = _sourcePictures ??
                throw new ObjectDisposedException(nameof(CadPrintJob));
            return pictures[sourcePageIndex].Clone();
        }
    }

    public void Dispose()
    {
        GpuPicture[]? pictures;
        lock (_gate)
        {
            pictures = _sourcePictures;
            _sourcePictures = null;
        }

        if (pictures is null)
        {
            return;
        }

        foreach (GpuPicture picture in pictures)
        {
            picture.Dispose();
        }
    }

    private (int SourcePageIndex, int CopyIndex) Resolve(int outputPageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputPageIndex);
        if (outputPageIndex >= OutputPageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(outputPageIndex));
        }

        int sequenceIndex;
        int copyIndex;
        if (CollationMode == CadPrintCollationMode.Collated)
        {
            sequenceIndex = outputPageIndex % SourcePageCount;
            copyIndex = outputPageIndex / SourcePageCount;
        }
        else
        {
            sequenceIndex = outputPageIndex / Copies;
            copyIndex = outputPageIndex % Copies;
        }

        int sourcePageIndex = ReversePageOrder
            ? SourcePageCount - sequenceIndex - 1
            : sequenceIndex;
        return (sourcePageIndex, copyIndex);
    }
}

/// <summary>Compiles caller-ordered retained pages into a bounded print job.</summary>
public sealed class CadPrintJobCompiler
{
    public CadPrintJob Compile(
        ReadOnlySpan<CadPrintJobPageSource> pages,
        CadPrintJobOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new CadPrintJobOptions();
        ValidateOptions(options);
        if (pages.IsEmpty)
        {
            throw new ArgumentException(
                "A print job requires at least one source page.",
                nameof(pages));
        }
        if (pages.Length > options.MaxSourcePages)
        {
            throw new InvalidDataException(
                "The print job exceeds the configured source-page budget.");
        }

        long outputPageCount = checked((long)pages.Length * options.Copies);
        if (outputPageCount > options.MaxOutputPages)
        {
            throw new InvalidDataException(
                "The print job exceeds the configured output-page budget.");
        }

        int totalNameCodeUnits = 0;
        for (int index = 0; index < pages.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CadPrintJobPageSource page = pages[index];
            ArgumentNullException.ThrowIfNull(page.Plan);
            if (string.IsNullOrWhiteSpace(page.Name))
            {
                throw new ArgumentException(
                    "Every print-job source page requires a nonempty name.",
                    nameof(pages));
            }
            string? sourcePageSetupName = page.Plan.SourcePageSetupName;
            int sourcePageSetupNameLength = sourcePageSetupName?.Length ?? 0;
            if (page.Name.Length > options.MaxNameCodeUnits ||
                sourcePageSetupNameLength > options.MaxNameCodeUnits ||
                page.Name.Length >
                    options.MaxTotalNameCodeUnits - totalNameCodeUnits ||
                sourcePageSetupNameLength >
                    options.MaxTotalNameCodeUnits -
                    totalNameCodeUnits - page.Name.Length)
            {
                throw new InvalidDataException(
                    "Print-job page names exceed the configured ownership budget.");
            }
            if (page.Plan.IsDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(pages),
                    $"Print-job source page {index} has a disposed plan.");
            }

            totalNameCodeUnits += page.Name.Length + sourcePageSetupNameLength;
        }

        var pictures = new GpuPicture[pages.Length];
        var pageInfos = new CadPrintJobPageInfo[pages.Length];
        int retainedPictureCount = 0;
        try
        {
            for (int index = 0; index < pages.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CadPrintJobPageSource page = pages[index];
                CadPrintPlan plan = page.Plan;
                pictures[index] = plan.CreatePagePicture();
                retainedPictureCount++;
                pageInfos[index] = new CadPrintJobPageInfo(
                    index,
                    new string(page.Name.AsSpan()),
                    plan.ContentGeneration,
                    plan.SourcePageSetupName is null
                        ? null
                        : new string(plan.SourcePageSetupName.AsSpan()),
                    plan.PaperWidthMillimeters,
                    plan.PaperHeightMillimeters,
                    plan.Rotation,
                    plan.OutputDpi,
                    plan.PageSizePixels,
                    plan.PrintableAreaPixels,
                    plan.LineWeightMode,
                    plan.TransparencyMode);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new CadPrintJob(
                pictures,
                pageInfos,
                checked((int)outputPageCount),
                options);
        }
        catch
        {
            for (int index = 0; index < retainedPictureCount; index++)
            {
                pictures[index].Dispose();
            }

            throw;
        }
    }

    internal static void ValidateOptions(CadPrintJobOptions options)
    {
        if (options.Copies <= 0 ||
            !Enum.IsDefined(options.CollationMode) ||
            options.MaxSourcePages <= 0 ||
            options.MaxOutputPages <= 0 ||
            options.MaxNameCodeUnits <= 0 ||
            options.MaxTotalNameCodeUnits <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Print-job copies, collation, page, and name budgets must be valid.");
        }
    }
}
