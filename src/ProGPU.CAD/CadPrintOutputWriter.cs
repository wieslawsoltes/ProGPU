using System.Globalization;
using System.IO.Compression;
using System.Text;
using ProGPU.Scene;
using SkiaSharp;

namespace ProGPU.CAD;

public enum CadPrintOutputFormat : byte
{
    Pdf = 0,
    Png = 1,
}

public sealed class CadPrintOutputOptions
{
    public const long DefaultMaxPagePixelCount =
        CadPrintPlanOptions.DefaultMaxPagePixelCount;
    public const long DefaultMaxTotalPixelCount = 536_870_912;
    public const long DefaultMaxEncodedBytes = 536_870_912;
    public const int DefaultMaxPixelDimension = 65_535;

    public long MaxPagePixelCount { get; init; } = DefaultMaxPagePixelCount;

    public long MaxTotalPixelCount { get; init; } = DefaultMaxTotalPixelCount;

    public long MaxEncodedBytes { get; init; } = DefaultMaxEncodedBytes;

    public int MaxPixelDimension { get; init; } = DefaultMaxPixelDimension;
}

public readonly record struct CadPrintOutputResult(
    CadPrintOutputFormat Format,
    int PageCount,
    float MinimumRasterDpi,
    float MaximumRasterDpi,
    long RasterPixelCount,
    long EncodedByteCount)
{
    public bool HasUniformRasterDpi => MinimumRasterDpi == MaximumRasterDpi;
}

/// <summary>
/// Writes retained physical CAD pages to bounded, caller-owned output streams.
/// </summary>
/// <remarks>
/// Geometry is not recompiled. Each output page clones one immutable retained
/// picture, maps its integer page-pixel extent to the exact physical media, and
/// replays it through the repository-owned Skia-compatible CPU raster canvas.
/// Work is O(P + C + X), where P is output-page count, C is the replayed command
/// count, and X is the encoded pixel count. Staging storage is O(X + B), bounded
/// by MaxTotalPixelCount and MaxEncodedBytes. The destination is untouched by
/// validation, rendering, encoding, or cancellation failures before commit.
/// </remarks>
public sealed class CadPrintOutputWriter
{
    private const double MillimetersPerInch = 25.4;
    private const float PdfPointsPerInch = 72f;

    public CadPrintOutputResult WritePdf(
        CadPrintJob job,
        Stream destination,
        CadPrintOutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateDestination(destination);
        options ??= new CadPrintOutputOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        OutputPageLayout[] pages = PreflightJob(
            job,
            options,
            cancellationToken,
            out long rasterPixelCount,
            out float minimumRasterDpi,
            out float maximumRasterDpi);

        using var staging = new BoundedMemoryStream(options.MaxEncodedBytes);
        WritePdfDocument(
            job,
            pages,
            staging,
            options.MaxEncodedBytes,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        Commit(staging, destination);
        return new CadPrintOutputResult(
            CadPrintOutputFormat.Pdf,
            pages.Length,
            minimumRasterDpi,
            maximumRasterDpi,
            rasterPixelCount,
            staging.Length);
    }

    public CadPrintOutputResult WritePng(
        CadPrintJob job,
        int outputPageIndex,
        Stream destination,
        CadPrintOutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateDestination(destination);
        options ??= new CadPrintOutputOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        CadPrintJobOutputPage outputPage = job.GetOutputPage(outputPageIndex);
        OutputPageLayout page = CreateLayout(
            outputPage.SourcePage,
            options);
        if (page.PixelCount > options.MaxTotalPixelCount)
        {
            throw new InvalidDataException(
                "The print output exceeds the configured total raster-pixel budget.");
        }

        using var staging = new BoundedMemoryStream(options.MaxEncodedBytes);
        using (SKBitmap bitmap = RenderPage(
                   job.CreatePagePicture(outputPageIndex),
                   page))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bitmap.Encode(staging, SKEncodedImageFormat.Png, quality: 100))
            {
                throw new InvalidDataException("The retained CAD page could not be encoded as PNG.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Commit(staging, destination);
        return new CadPrintOutputResult(
            CadPrintOutputFormat.Png,
            1,
            page.RasterDpi,
            page.RasterDpi,
            page.PixelCount,
            staging.Length);
    }

    private static SKBitmap RenderPage(
        GpuPicture picture,
        OutputPageLayout page)
    {
        var bitmap = new SKBitmap(new SKImageInfo(
            page.WidthPixels,
            page.HeightPixels,
            SKColorType.Rgba8888,
            SKAlphaType.Opaque));
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            using var skPicture = new SKPicture(
                picture,
                new SKRect(
                    0,
                    0,
                    page.SourceWidthPixels,
                    page.SourceHeightPixels));
            canvas.Scale(
                (float)page.WidthPixels / page.SourceWidthPixels,
                (float)page.HeightPixels / page.SourceHeightPixels);
            canvas.DrawPicture(skPicture);
            canvas.Flush();
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void WritePdfDocument(
        CadPrintJob job,
        ReadOnlySpan<OutputPageLayout> pages,
        BoundedMemoryStream output,
        long maxEncodedBytes,
        CancellationToken cancellationToken)
    {
        int objectCount = checked(2 + pages.Length * 3);
        var offsets = new long[objectCount + 1];
        WriteAscii(output, "%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");
        WriteObject(
            output,
            offsets,
            1,
            "<< /Type /Catalog /Pages 2 0 R >>");

        var kids = new StringBuilder(checked(pages.Length * 12));
        for (int index = 0; index < pages.Length; index++)
        {
            kids.Append(3 + index * 3).Append(" 0 R ");
        }
        WriteObject(
            output,
            offsets,
            2,
            $"<< /Type /Pages /Count {pages.Length} /Kids [{kids}] >>");

        for (int index = 0; index < pages.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OutputPageLayout page = pages[index];
            int pageId = 3 + index * 3;
            int imageId = pageId + 1;
            int contentId = pageId + 2;
            string width = page.WidthPoints.ToString("0.###", CultureInfo.InvariantCulture);
            string height = page.HeightPoints.ToString("0.###", CultureInfo.InvariantCulture);
            WriteObject(
                output,
                offsets,
                pageId,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im{index + 1} {imageId} 0 R >> >> /Contents {contentId} 0 R >>");

            using SKBitmap bitmap = RenderPage(job.CreatePagePicture(index), page);
            using BoundedMemoryStream compressed = CompressRgb(
                bitmap.GetPixelSpan(),
                maxEncodedBytes);
            offsets[imageId] = output.Position;
            WriteAscii(output, $"{imageId} 0 obj\n");
            WriteAscii(
                output,
                $"<< /Type /XObject /Subtype /Image /Width {page.WidthPixels} /Height {page.HeightPixels} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
            compressed.Position = 0;
            compressed.CopyTo(output);
            WriteAscii(output, "\nendstream\nendobj\n");

            string commands =
                $"q {width} 0 0 -{height} 0 {height} cm /Im{index + 1} Do Q\n";
            offsets[contentId] = output.Position;
            WriteAscii(output, $"{contentId} 0 obj\n");
            WriteAscii(
                output,
                $"<< /Length {Encoding.Latin1.GetByteCount(commands)} >>\nstream\n");
            WriteAscii(output, commands);
            WriteAscii(output, "endstream\nendobj\n");
        }

        cancellationToken.ThrowIfCancellationRequested();
        long xref = output.Position;
        WriteAscii(output, $"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        for (int id = 1; id <= objectCount; id++)
        {
            WriteAscii(output, $"{offsets[id]:D10} 00000 n \n");
        }
        WriteAscii(
            output,
            $"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private static BoundedMemoryStream CompressRgb(
        ReadOnlySpan<byte> rgba,
        long maxEncodedBytes)
    {
        if ((rgba.Length & 3) != 0)
        {
            throw new InvalidDataException(
                "The raster page does not contain complete RGBA pixels.");
        }
        var output = new BoundedMemoryStream(maxEncodedBytes);
        try
        {
            using (var compressor = new ZLibStream(
                       output,
                       CompressionLevel.SmallestSize,
                       leaveOpen: true))
            {
                Span<byte> rgb = stackalloc byte[3 * 1_024];
                int source = 0;
                while (source < rgba.Length)
                {
                    int pixelCount = Math.Min(
                        rgb.Length / 3,
                        (rgba.Length - source) / 4);
                    int destination = 0;
                    for (int pixel = 0; pixel < pixelCount; pixel++)
                    {
                        rgb[destination++] = rgba[source++];
                        rgb[destination++] = rgba[source++];
                        rgb[destination++] = rgba[source++];
                        source++;
                    }
                    compressor.Write(rgb[..destination]);
                }
            }
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void WriteObject(
        BoundedMemoryStream output,
        long[] offsets,
        int id,
        string body)
    {
        offsets[id] = output.Position;
        WriteAscii(output, $"{id} 0 obj\n{body}\nendobj\n");
    }

    private static void WriteAscii(Stream output, string value)
    {
        int byteCount = Encoding.Latin1.GetByteCount(value);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(byteCount);
        Encoding.Latin1.GetBytes(value, bytes);
        output.Write(bytes);
    }

    private static OutputPageLayout[] PreflightJob(
        CadPrintJob job,
        CadPrintOutputOptions options,
        CancellationToken cancellationToken,
        out long rasterPixelCount,
        out float minimumRasterDpi,
        out float maximumRasterDpi)
    {
        if (job.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(job));
        }

        var pages = new OutputPageLayout[job.OutputPageCount];
        long total = 0;
        float minimumDpi = float.MaxValue;
        float maximumDpi = 0f;
        for (int index = 0; index < pages.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages[index] = CreateLayout(
                job.GetOutputPage(index).SourcePage,
                options);
            minimumDpi = MathF.Min(minimumDpi, pages[index].RasterDpi);
            maximumDpi = MathF.Max(maximumDpi, pages[index].RasterDpi);
            total = checked(total + pages[index].PixelCount);
            if (total > options.MaxTotalPixelCount)
            {
                throw new InvalidDataException(
                    "The print output exceeds the configured total raster-pixel budget.");
            }
        }

        rasterPixelCount = total;
        minimumRasterDpi = minimumDpi;
        maximumRasterDpi = maximumDpi;
        return pages;
    }

    private static OutputPageLayout CreateLayout(
        CadPrintJobPageInfo source,
        CadPrintOutputOptions options)
    {
        float rasterDpi = source.OutputDpi;
        if (!(rasterDpi > 0f) || !float.IsFinite(rasterDpi))
        {
            throw new InvalidDataException(
                "The retained print page has an invalid output DPI.");
        }
        bool quarterTurn = source.Rotation is
            CadPageRotation.CounterClockwise90 or
            CadPageRotation.CounterClockwise270;
        double widthMillimeters = quarterTurn
            ? source.PaperHeightMillimeters
            : source.PaperWidthMillimeters;
        double heightMillimeters = quarterTurn
            ? source.PaperWidthMillimeters
            : source.PaperHeightMillimeters;
        float widthPoints = checked((float)(
            widthMillimeters / MillimetersPerInch * PdfPointsPerInch));
        float heightPoints = checked((float)(
            heightMillimeters / MillimetersPerInch * PdfPointsPerInch));
        if (!(widthPoints > 0f) || !(heightPoints > 0f) ||
            !float.IsFinite(widthPoints) || !float.IsFinite(heightPoints))
        {
            throw new InvalidDataException(
                "The print page has invalid physical media dimensions.");
        }

        int widthPixels = ToPixelDimension(
            widthMillimeters,
            rasterDpi,
            options.MaxPixelDimension);
        int heightPixels = ToPixelDimension(
            heightMillimeters,
            rasterDpi,
            options.MaxPixelDimension);
        long pixelCount = checked((long)widthPixels * heightPixels);
        if (pixelCount > options.MaxPagePixelCount)
        {
            throw new InvalidDataException(
                "The print page exceeds the configured raster-pixel budget.");
        }
        if (source.PageSizePixels.Width <= 0 || source.PageSizePixels.Height <= 0)
        {
            throw new InvalidDataException(
                "The retained print page has invalid source pixel dimensions.");
        }

        return new OutputPageLayout(
            source.PageSizePixels.Width,
            source.PageSizePixels.Height,
            widthPixels,
            heightPixels,
            widthPoints,
            heightPoints,
            rasterDpi,
            pixelCount);
    }

    private static int ToPixelDimension(
        double millimeters,
        float rasterDpi,
        int maxDimension)
    {
        double exact = millimeters / MillimetersPerInch * rasterDpi;
        if (!(exact > 0.0) || !double.IsFinite(exact) || exact > int.MaxValue)
        {
            throw new InvalidDataException(
                "The print page has an invalid raster dimension.");
        }

        int dimension = Math.Max(1, checked((int)Math.Ceiling(exact)));
        if (dimension > maxDimension)
        {
            throw new InvalidDataException(
                "The print page exceeds the configured raster-dimension budget.");
        }
        return dimension;
    }

    private static void ValidateOptions(CadPrintOutputOptions options)
    {
        if (options.MaxPagePixelCount <= 0 ||
            options.MaxTotalPixelCount <= 0 ||
            options.MaxEncodedBytes <= 0 ||
            options.MaxEncodedBytes > int.MaxValue ||
            options.MaxPixelDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Output byte, pixel, and dimension budgets must be valid.");
        }
    }

    private static void ValidateDestination(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "The output destination must be writable.",
                nameof(destination));
        }
    }

    private static void Commit(BoundedMemoryStream staging, Stream destination)
    {
        if (!staging.TryGetBuffer(out ArraySegment<byte> buffer))
        {
            throw new InvalidOperationException("The bounded output staging buffer is unavailable.");
        }
        destination.Write(buffer.AsSpan(0, checked((int)staging.Length)));
    }

    private readonly record struct OutputPageLayout(
        int SourceWidthPixels,
        int SourceHeightPixels,
        int WidthPixels,
        int HeightPixels,
        float WidthPoints,
        float HeightPoints,
        float RasterDpi,
        long PixelCount)
    {
    }

    private sealed class BoundedMemoryStream : MemoryStream
    {
        private readonly long _maximumLength;

        public BoundedMemoryStream(long maximumLength)
        {
            _maximumLength = maximumLength;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityFor(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityFor(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacityFor(1);
            base.WriteByte(value);
        }

        public override void SetLength(long value)
        {
            if (value < 0 || value > _maximumLength)
            {
                throw new InvalidDataException(
                    "The encoded print output exceeds its configured byte budget.");
            }
            base.SetLength(value);
        }

        private void EnsureCapacityFor(int count)
        {
            if (count < 0 || Position > _maximumLength - count)
            {
                throw new InvalidDataException(
                    "The encoded print output exceeds its configured byte budget.");
            }
        }
    }
}
