using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPrintJobTests
{
    [Fact]
    public void CollatedCopiesShareEachSourcePageAndPreserveInputOrder()
    {
        using CadPrintPlan first = CreatePlan(
            "A4 model",
            lineCount: 1,
            paperWidthMillimeters: 210,
            paperHeightMillimeters: 297,
            outputDpi: 100);
        using CadPrintPlan second = CreatePlan(
            "A3 model",
            lineCount: 2,
            paperWidthMillimeters: 420,
            paperHeightMillimeters: 297,
            outputDpi: 100);
        CadPrintJobPageSource[] sources =
        [
            new("Sheet A", first),
            new("Sheet B", second),
        ];

        using CadPrintJob job = new CadPrintJobCompiler().Compile(
            sources,
            new CadPrintJobOptions
            {
                Copies = 3,
                CollationMode = CadPrintCollationMode.Collated,
            });

        Assert.Equal(2, job.SourcePageCount);
        Assert.Equal(6, job.OutputPageCount);
        Assert.Equal(3, job.Copies);
        Assert.Equal(
            [0, 1, 0, 1, 0, 1],
            Enumerable.Range(0, job.OutputPageCount)
                .Select(index => job.GetOutputPage(index).SourcePageIndex));
        Assert.Equal(
            [0, 0, 1, 1, 2, 2],
            Enumerable.Range(0, job.OutputPageCount)
                .Select(index => job.GetOutputPage(index).CopyIndex));
        Assert.Equal("Sheet A", job.SourcePages.Span[0].Name);
        Assert.Equal("A4 model", job.SourcePages.Span[0].SourcePageSetupName);
        Assert.Equal(new CadPrintPixelSize(827, 1169),
            job.SourcePages.Span[0].PageSizePixels);
        Assert.Equal(new CadPrintPixelSize(1654, 1169),
            job.SourcePages.Span[1].PageSizePixels);

        using GpuPicture firstCopy = job.CreatePagePicture(0);
        using GpuPicture secondCopy = job.CreatePagePicture(2);
        using GpuPicture otherPage = job.CreatePagePicture(1);
        Assert.True(firstCopy.SharesRetainedCommandStorageWith(secondCopy));
        Assert.False(firstCopy.SharesRetainedCommandStorageWith(otherPage));
        Assert.Equal(1, GetContentPicture(firstCopy).CommandCount);
        Assert.Equal(2, GetContentPicture(otherPage).CommandCount);
        AssertNativePage(firstCopy, first.ContentGeneration, 401U);
        AssertNativePage(otherPage, second.ContentGeneration, 402U);
    }

    [Fact]
    public void UncollatedReverseOrderUsesExactCopyAndMixedMediaMapping()
    {
        using CadPrintPlan first = CreatePlan(
            "First",
            lineCount: 1,
            paperWidthMillimeters: 100,
            paperHeightMillimeters: 50,
            outputDpi: 254);
        using CadPrintPlan second = CreatePlan(
            "Second",
            lineCount: 2,
            paperWidthMillimeters: 200,
            paperHeightMillimeters: 100,
            outputDpi: 127);
        using CadPrintPlan third = CreatePlan(
            "Third",
            lineCount: 3,
            paperWidthMillimeters: 80,
            paperHeightMillimeters: 120,
            outputDpi: 254);
        CadPrintJobPageSource[] sources =
        [
            new("One", first),
            new("Two", second),
            new("Three", third),
        ];

        using CadPrintJob job = new CadPrintJobCompiler().Compile(
            sources,
            new CadPrintJobOptions
            {
                Copies = 2,
                CollationMode = CadPrintCollationMode.Uncollated,
                ReversePageOrder = true,
            });

        Assert.True(job.ReversePageOrder);
        Assert.Equal(
            [2, 2, 1, 1, 0, 0],
            Enumerable.Range(0, job.OutputPageCount)
                .Select(index => job.GetOutputPage(index).SourcePageIndex));
        Assert.Equal(
            [0, 1, 0, 1, 0, 1],
            Enumerable.Range(0, job.OutputPageCount)
                .Select(index => job.GetOutputPage(index).CopyIndex));
        Assert.Equal("Three", job.GetOutputPage(0).SourcePage.Name);
        Assert.Equal(new CadPrintPixelSize(800, 1200),
            job.GetOutputPage(0).SourcePage.PageSizePixels);
        Assert.Equal(new CadPrintPixelSize(1000, 500),
            job.GetOutputPage(2).SourcePage.PageSizePixels);
        Assert.Equal(200,
            job.GetOutputPage(2).SourcePage.PaperWidthMillimeters);
        Assert.Equal(127,
            job.GetOutputPage(2).SourcePage.OutputDpi);
        Assert.Equal(new CadPrintPixelSize(1000, 500),
            job.GetOutputPage(4).SourcePage.PageSizePixels);
        Assert.Equal(100,
            job.GetOutputPage(4).SourcePage.PaperWidthMillimeters);
        Assert.Equal(254,
            job.GetOutputPage(4).SourcePage.OutputDpi);
    }

    [Fact]
    public void JobOwnsPagesIndependentlyFromPlansAndReturnedPictures()
    {
        CadPrintPlan first = CreatePlan("One", 1, 100, 100, 25.4f);
        CadPrintPlan second = CreatePlan("Two", 2, 100, 100, 25.4f);
        using CadPrintJob job = new CadPrintJobCompiler().Compile(
        [
            new CadPrintJobPageSource("One", first),
            new CadPrintJobPageSource("Two", second),
        ]);

        first.Dispose();
        second.Dispose();
        using GpuPicture survivingPage = job.CreatePagePicture(1);
        Assert.Equal(2, GetContentPicture(survivingPage).CommandCount);

        job.Dispose();
        Assert.True(job.IsDisposed);
        Assert.Equal("One", job.GetOutputPage(0).SourcePage.Name);
        Assert.Throws<ObjectDisposedException>(() => job.CreatePagePicture(0));
        using GpuPicture survivingClone = survivingPage.Clone();
        Assert.Equal(2, GetContentPicture(survivingClone).CommandCount);
    }

    [Fact]
    public void LargeCopyCountRetainsOnlySourcePageCommandStorage()
    {
        using CadPrintPlan first = CreatePlan("First", 1, 100, 100, 25.4f);
        using CadPrintPlan second = CreatePlan("Second", 2, 100, 100, 25.4f);
        using CadPrintJob job = new CadPrintJobCompiler().Compile(
        [
            new CadPrintJobPageSource("First", first),
            new CadPrintJobPageSource("Second", second),
        ],
            new CadPrintJobOptions
            {
                Copies = 10_000,
                MaxOutputPages = 20_000,
            });

        Assert.Equal(2, job.SourcePages.Length);
        Assert.Equal(20_000, job.OutputPageCount);
        Assert.Equal(0, job.GetOutputPage(19_998).SourcePageIndex);
        Assert.Equal(9_999, job.GetOutputPage(19_998).CopyIndex);
        Assert.Equal(1, job.GetOutputPage(19_999).SourcePageIndex);
        Assert.Equal(9_999, job.GetOutputPage(19_999).CopyIndex);
        using GpuPicture firstCopy = job.CreatePagePicture(0);
        using GpuPicture lastFirstCopy = job.CreatePagePicture(19_998);
        Assert.True(firstCopy.SharesRetainedCommandStorageWith(lastFirstCopy));
    }

    [Fact]
    public void InvalidJobsBudgetsAndCancellationFailBeforeSourceConsumption()
    {
        using CadPrintPlan plan = CreatePlan("Setup", 1, 100, 100, 25.4f);
        var compiler = new CadPrintJobCompiler();
        CadPrintJobPageSource[] source = [new("Page", plan)];

        Assert.Throws<ArgumentException>(() => compiler.Compile([]));
        Assert.Throws<ArgumentNullException>(() => compiler.Compile([default]));
        Assert.Throws<ArgumentOutOfRangeException>(() => compiler.Compile(
            source,
            new CadPrintJobOptions { Copies = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => compiler.Compile(
            source,
            new CadPrintJobOptions
            {
                CollationMode = (CadPrintCollationMode)byte.MaxValue,
            }));
        Assert.Throws<InvalidDataException>(() => compiler.Compile(
            source,
            new CadPrintJobOptions { MaxOutputPages = 1, Copies = 2 }));
        Assert.Throws<InvalidDataException>(() => compiler.Compile(
            [source[0], source[0]],
            new CadPrintJobOptions { MaxSourcePages = 1 }));
        Assert.Throws<ArgumentException>(() => compiler.Compile(
            [new CadPrintJobPageSource(" ", plan)]));
        Assert.Throws<InvalidDataException>(() => compiler.Compile(
            source,
            new CadPrintJobOptions { MaxNameCodeUnits = 4 }));
        Assert.Throws<InvalidDataException>(() => compiler.Compile(
            source,
            new CadPrintJobOptions
            {
                MaxNameCodeUnits = 10,
                MaxTotalNameCodeUnits = 8,
            }));
        Assert.Throws<OperationCanceledException>(() => compiler.Compile(
            source,
            cancellationToken: new CancellationToken(canceled: true)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using CadPrintJob job = compiler.Compile(source);
            job.GetOutputPage(1);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using CadPrintJob job = compiler.Compile(source);
            job.CreatePagePicture(-1).Dispose();
        });

        using GpuPicture sourceStillOwned = plan.CreatePagePicture();
        Assert.Equal(1, GetContentPicture(sourceStillOwned).CommandCount);

        using CadPrintPlan disposed = CreatePlan("Disposed", 1, 100, 100, 25.4f);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => compiler.Compile(
            [new CadPrintJobPageSource("Disposed", disposed)]));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task RoundTrippedDrawingsRetainOrderedNativeJobPages(
        CadDocumentFormat format)
    {
        using CadPrintPlan first = await RoundTripPlanAsync(format, 1, 11);
        using CadPrintPlan second = await RoundTripPlanAsync(format, 3, 22);
        using CadPrintJob job = new CadPrintJobCompiler().Compile(
        [
            new CadPrintJobPageSource("Drawing A", first),
            new CadPrintJobPageSource("Drawing B", second),
        ],
            new CadPrintJobOptions
            {
                Copies = 2,
                ReversePageOrder = true,
            });

        Assert.Equal([1, 0, 1, 0],
            Enumerable.Range(0, job.OutputPageCount)
                .Select(index => job.GetOutputPage(index).SourcePageIndex));
        Assert.Equal(22UL, job.SourcePages.Span[1].ContentGeneration);
        for (int outputPageIndex = 0;
             outputPageIndex < job.OutputPageCount;
             outputPageIndex++)
        {
            CadPrintJobOutputPage output = job.GetOutputPage(outputPageIndex);
            using GpuPicture picture = job.CreatePagePicture(outputPageIndex);
            Assert.Equal(
                output.SourcePageIndex == 0 ? 1 : 3,
                GetContentPicture(picture).CommandCount);
            AssertNativePage(
                picture,
                output.SourcePage.ContentGeneration,
                checked((uint)(410 + outputPageIndex)));
        }
    }

    private static CadPrintPlan CreatePlan(
        string pageSetupName,
        int lineCount,
        double paperWidthMillimeters,
        double paperHeightMillimeters,
        float outputDpi)
    {
        var document = new CadDocument();
        for (int index = 0; index < lineCount; index++)
        {
            document.Entities.Add(new Line(
                new XYZ(0, index, 0),
                new XYZ(10, index, 0)));
        }

        var session = new CadDocumentSession(document);
        session.Edit("publish print-job generation", static _ => { });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        return new CadPrintPlanCompiler().Compile(
            snapshot,
            new CadPrintPlanOptions
            {
                SourcePageSetupName = pageSetupName,
                PaperWidthMillimeters = paperWidthMillimeters,
                PaperHeightMillimeters = paperHeightMillimeters,
                MarginLeftMillimeters = 0,
                MarginTopMillimeters = 0,
                MarginRightMillimeters = 0,
                MarginBottomMillimeters = 0,
                OutputDpi = outputDpi,
            });
    }

    private static async Task<CadPrintPlan> RoundTripPlanAsync(
        CadDocumentFormat format,
        int lineCount,
        int generation)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        for (int index = 0; index < lineCount; index++)
        {
            document.Entities.Add(new Line(
                new XYZ(index, 0, 0),
                new XYZ(index, 10, 0)));
        }

        var store = new CadDocumentStore();
        using var stream = new MemoryStream();
        await store.SaveAsync(
            new CadDocumentSession(document),
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"job-{lineCount}.{format.ToString().ToLowerInvariant()}");
        for (int index = 0; index < generation; index++)
        {
            loaded.Session.Edit("advance print-job generation", static _ => { });
        }

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            loaded.Session,
            new CadSnapshotOptions
            {
                DrawOrderPurpose = CadDrawOrderPurpose.Plotting,
            });
        return new CadPrintPlanCompiler().Compile(
            snapshot,
            new CadPrintPlanOptions
            {
                SourcePageSetupName = $"Generation {generation}",
                PaperWidthMillimeters = 100,
                PaperHeightMillimeters = 100,
                MarginLeftMillimeters = 0,
                MarginTopMillimeters = 0,
                MarginRightMillimeters = 0,
                MarginBottomMillimeters = 0,
                OutputDpi = 25.4f,
            });
    }

    private static GpuPicture GetContentPicture(GpuPicture page) =>
        page.GetCommand(1).Picture!;

    private static void AssertNativePage(
        GpuPicture page,
        ulong contentGeneration,
        uint deviceId)
    {
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            page,
            deviceId,
            contentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }
}
