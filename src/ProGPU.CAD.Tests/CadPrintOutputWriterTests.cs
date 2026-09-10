using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using StbImageSharp;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPrintOutputWriterTests
{
    [Fact]
    public void PngUsesExactRequestedDpiAndLeavesJobReusable()
    {
        using CadPrintPlan plan = CreatePlan(
            "PNG",
            paperWidthMillimeters: 25.4,
            paperHeightMillimeters: 12.7,
            outputDpi: 20);
        using CadPrintJob job = CreateJob(plan);
        using var output = new MemoryStream();

        CadPrintOutputResult result = new CadPrintOutputWriter().WritePng(
            job,
            0,
            output);

        Assert.Equal(CadPrintOutputFormat.Png, result.Format);
        Assert.Equal(1, result.PageCount);
        Assert.True(result.HasUniformRasterDpi);
        Assert.Equal(20, result.MinimumRasterDpi);
        Assert.Equal(20, result.MaximumRasterDpi);
        Assert.Equal(200, result.RasterPixelCount);
        Assert.Equal(output.Length, result.EncodedByteCount);
        Assert.Equal(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            output.GetBuffer().AsSpan(0, 8).ToArray());

        ImageResult decoded = ImageResult.FromMemory(
            output.ToArray(),
            ColorComponents.RedGreenBlueAlpha);
        Assert.Equal(20, decoded.Width);
        Assert.Equal(10, decoded.Height);
        Assert.Contains(
            Enumerable.Range(0, decoded.Width * decoded.Height),
            index => decoded.Data[index * 4] < 250 ||
                decoded.Data[index * 4 + 1] < 250 ||
                decoded.Data[index * 4 + 2] < 250);

        using GpuPicture survivingPage = job.CreatePagePicture(0);
        AssertNativePage(survivingPage, plan.ContentGeneration);
    }

    [Fact]
    public void PdfPreservesResolvedPageOrderCopiesAndPhysicalMedia()
    {
        using CadPrintPlan square = CreatePlan("Square", 25.4, 25.4, 10);
        using CadPrintPlan wide = CreatePlan("Wide", 50.8, 25.4, 20);
        using CadPrintJob job = new CadPrintJobCompiler().Compile(
        [
            new CadPrintJobPageSource("Square", square),
            new CadPrintJobPageSource("Wide", wide),
        ],
            new CadPrintJobOptions
            {
                Copies = 2,
                CollationMode = CadPrintCollationMode.Uncollated,
                ReversePageOrder = true,
            });
        using var output = new MemoryStream();

        CadPrintOutputResult result = new CadPrintOutputWriter().WritePdf(
            job,
            output);

        Assert.Equal(CadPrintOutputFormat.Pdf, result.Format);
        Assert.Equal(4, result.PageCount);
        Assert.False(result.HasUniformRasterDpi);
        Assert.Equal(10, result.MinimumRasterDpi);
        Assert.Equal(20, result.MaximumRasterDpi);
        Assert.Equal(1_800, result.RasterPixelCount);
        Assert.Equal(output.Length, result.EncodedByteCount);
        string pdf = Encoding.Latin1.GetString(output.ToArray());
        Assert.StartsWith("%PDF-1.4", pdf, StringComparison.Ordinal);
        MatchCollection mediaBoxes = Regex.Matches(
            pdf,
            @"/MediaBox \[0 0 ([0-9.]+) ([0-9.]+)\]");
        Assert.Equal(4, mediaBoxes.Count);
        Assert.Equal(
            ["144", "144", "72", "72"],
            mediaBoxes.Select(match => match.Groups[1].Value));
        Assert.All(mediaBoxes, match =>
            Assert.Equal("72", match.Groups[2].Value));
        Assert.Equal(4, Regex.Matches(pdf, @"/Type /Page ").Count);
    }

    [Theory]
    [InlineData(CadPageRotation.CounterClockwise90)]
    [InlineData(CadPageRotation.CounterClockwise270)]
    public void QuarterTurnSwapsRasterAndPhysicalOutputAxes(
        CadPageRotation rotation)
    {
        using CadPrintPlan plan = CreatePlan(
            "Rotated",
            25.4,
            50.8,
            10,
            rotation);
        using CadPrintJob job = CreateJob(plan);
        var writer = new CadPrintOutputWriter();
        using var png = new MemoryStream();
        using var pdf = new MemoryStream();

        writer.WritePng(job, 0, png);
        writer.WritePdf(job, pdf);

        ImageResult decoded = ImageResult.FromMemory(
            png.ToArray(),
            ColorComponents.RedGreenBlueAlpha);
        Assert.Equal(20, decoded.Width);
        Assert.Equal(10, decoded.Height);
        string pdfText = Encoding.Latin1.GetString(pdf.ToArray());
        Assert.Contains(
            "/MediaBox [0 0 144 72]",
            pdfText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PdfAndPngUseTheSameWhitePaperRasterPixels()
    {
        using CadPrintPlan plan = CreatePlan("Parity", 25.4, 25.4, 10);
        using CadPrintJob job = CreateJob(plan);
        var writer = new CadPrintOutputWriter();
        using var png = new MemoryStream();
        using var pdf = new MemoryStream();

        writer.WritePng(job, 0, png);
        writer.WritePdf(job, pdf);

        ImageResult decoded = ImageResult.FromMemory(
            png.ToArray(),
            ColorComponents.RedGreenBlueAlpha);
        byte[] pngRgb = new byte[decoded.Width * decoded.Height * 3];
        for (int source = 0, destination = 0;
             source < decoded.Data.Length;
             source += 4)
        {
            pngRgb[destination++] = decoded.Data[source];
            pngRgb[destination++] = decoded.Data[source + 1];
            pngRgb[destination++] = decoded.Data[source + 2];
        }

        byte[] pdfBytes = pdf.ToArray();
        string pdfText = Encoding.Latin1.GetString(pdfBytes);
        int image = pdfText.IndexOf("/Subtype /Image", StringComparison.Ordinal);
        Assert.True(image >= 0);
        Match length = Regex.Match(
            pdfText.AsSpan(image).ToString(),
            @"/Length ([0-9]+) >>\nstream\n");
        Assert.True(length.Success);
        int streamStart = image + length.Index + length.Length;
        int compressedLength = int.Parse(
            length.Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        using var compressed = new MemoryStream(
            pdfBytes,
            streamStart,
            compressedLength,
            writable: false);
        using var inflater = new ZLibStream(
            compressed,
            CompressionMode.Decompress);
        using var rgb = new MemoryStream();
        inflater.CopyTo(rgb);

        Assert.Equal(pngRgb, rgb.ToArray());
        Assert.Contains(
            Enumerable.Range(0, pngRgb.Length / 3),
            index => pngRgb[index * 3] < 250 ||
                pngRgb[index * 3 + 1] < 250 ||
                pngRgb[index * 3 + 2] < 250);
    }

    [Fact]
    public void PreflightEncodingAndCancellationFailuresDoNotTouchDestination()
    {
        using CadPrintPlan plan = CreatePlan("Bounded", 25.4, 25.4, 10);
        using CadPrintJob job = new CadPrintJobCompiler().Compile(
        [
            new CadPrintJobPageSource("One", plan),
        ],
            new CadPrintJobOptions { Copies = 2 });
        var writer = new CadPrintOutputWriter();

        AssertDestinationUnchanged(destination => Assert.Throws<InvalidDataException>(
            () => writer.WritePdf(
                job,
                destination,
                new CadPrintOutputOptions { MaxPagePixelCount = 99 })));
        AssertDestinationUnchanged(destination => Assert.Throws<InvalidDataException>(
            () => writer.WritePdf(
                job,
                destination,
                new CadPrintOutputOptions { MaxTotalPixelCount = 199 })));
        AssertDestinationUnchanged(destination => Assert.Throws<InvalidDataException>(
            () => writer.WritePng(
                job,
                0,
                destination,
                new CadPrintOutputOptions { MaxTotalPixelCount = 99 })));
        AssertDestinationUnchanged(destination => Assert.Throws<InvalidDataException>(
            () => writer.WritePdf(
                job,
                destination,
                new CadPrintOutputOptions { MaxEncodedBytes = 32 })));
        AssertDestinationUnchanged(destination => Assert.Throws<OperationCanceledException>(
            () => writer.WritePdf(
                job,
                destination,
                cancellationToken: new CancellationToken(canceled: true))));

        using var readOnly = new MemoryStream([1, 2, 3], writable: false);
        Assert.Throws<ArgumentException>(() => writer.WritePdf(job, readOnly));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WritePdf(
            job,
            Stream.Null,
            new CadPrintOutputOptions { MaxPixelDimension = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WritePng(
            job,
            2,
            Stream.Null));

        using GpuPicture survivingPage = job.CreatePagePicture(1);
        AssertNativePage(survivingPage, plan.ContentGeneration);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task RoundTrippedDrawingProducesPngAndPdf(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));
        var store = new CadDocumentStore();
        using var drawing = new MemoryStream();
        await store.SaveAsync(
            new CadDocumentSession(document),
            drawing,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        drawing.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(drawing, format);
        using CadPrintPlan plan = new CadPrintPlanCompiler().Compile(
            new CadSnapshotCompiler().Compile(
                loaded.Session,
                CreatePlottingSnapshotOptions()),
            CreateOptions("Round trip", 25.4, 25.4, 10));
        using CadPrintJob job = CreateJob(plan);
        var writer = new CadPrintOutputWriter();
        using var png = new MemoryStream();
        using var pdf = new MemoryStream();

        CadPrintOutputResult pngResult = writer.WritePng(job, 0, png);
        CadPrintOutputResult pdfResult = writer.WritePdf(job, pdf);

        Assert.True(pngResult.EncodedByteCount > 8);
        Assert.True(pdfResult.EncodedByteCount > 16);
        Assert.Equal(100, pngResult.RasterPixelCount);
        Assert.Equal(100, pdfResult.RasterPixelCount);
    }

    [Fact]
    public void DisposedJobFailsWithoutOutput()
    {
        using CadPrintPlan plan = CreatePlan("Disposed", 25.4, 25.4, 10);
        CadPrintJob job = CreateJob(plan);
        job.Dispose();
        var output = new MemoryStream();
        Assert.Throws<ObjectDisposedException>(() =>
            new CadPrintOutputWriter().WritePdf(job, output));
        Assert.Equal(0, output.Length);
    }

    private static CadPrintJob CreateJob(CadPrintPlan plan) =>
        new CadPrintJobCompiler().Compile(
            [new CadPrintJobPageSource("Page", plan)]);

    private static CadPrintPlan CreatePlan(
        string pageSetupName,
        double paperWidthMillimeters,
        double paperHeightMillimeters,
        float outputDpi,
        CadPageRotation rotation = CadPageRotation.Degrees0)
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 10, 0)));
        var session = new CadDocumentSession(document);
        session.Edit("publish print-output generation", static _ => { });
        return new CadPrintPlanCompiler().Compile(
            new CadSnapshotCompiler().Compile(
                session,
                CreatePlottingSnapshotOptions()),
            CreateOptions(
                pageSetupName,
                paperWidthMillimeters,
                paperHeightMillimeters,
                outputDpi,
                rotation));
    }

    private static CadPrintPlanOptions CreateOptions(
        string pageSetupName,
        double paperWidthMillimeters,
        double paperHeightMillimeters,
        float outputDpi,
        CadPageRotation rotation = CadPageRotation.Degrees0) =>
        new()
        {
            SourcePageSetupName = pageSetupName,
            PaperWidthMillimeters = paperWidthMillimeters,
            PaperHeightMillimeters = paperHeightMillimeters,
            MarginLeftMillimeters = 0,
            MarginTopMillimeters = 0,
            MarginRightMillimeters = 0,
            MarginBottomMillimeters = 0,
            OutputDpi = outputDpi,
            Rotation = rotation,
        };

    private static CadSnapshotOptions CreatePlottingSnapshotOptions() =>
        new()
        {
            DrawOrderPurpose = CadDrawOrderPurpose.Plotting,
            DrawingBackgroundColor = new CadColor32(255, 255, 255),
        };

    private static void AssertDestinationUnchanged(
        Action<MemoryStream> action)
    {
        using var destination = new MemoryStream();
        destination.Write([9, 8, 7]);
        action(destination);
        Assert.Equal(new byte[] { 9, 8, 7 }, destination.ToArray());
    }

    private static void AssertNativePage(
        GpuPicture page,
        ulong contentGeneration)
    {
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            page,
            700,
            contentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }
}
