using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMTextColumnRoundTripTests
{
    [Theory]
    [InlineData(false, 0x3EC)]
    [InlineData(true, 0x3EC)]
    [InlineData(false, 0x3F6)]
    [InlineData(true, 0x3F6)]
    [InlineData(false, 0x779)]
    [InlineData(true, 0x779)]
    public void RepresentativeFinalColumnMatchesUnboundedReference(bool dwg, int handle)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
            root = root.Parent;
        Assert.NotNull(root);
        string samples = Path.Combine(root.FullName, "external", "ACadSharp", "samples");
        CadDocument input = dwg ? DwgReader.Read(Path.Combine(samples, "sample_AC1032.dwg"))
            : DxfReader.Read(Path.Combine(samples, "sample_AC1032_ascii.dxf"));
        MText source = Assert.Single(input.Entities.OfType<MText>(), text => text.Handle == (ulong)handle);
        double[] original = source.ColumnData.Heights.ToArray();
        Assert.Single(original);
        Assert.True(original[0] <= 0);
        CadDocumentSnapshot actual = Compile((MText)source.Clone());
        var reference = (MText)source.Clone();
        reference.ColumnData.Heights[0] = 10000;
        CadDocumentSnapshot expected = Compile(reference);
        CadMTextPrimitive text = Assert.Single(actual.MTexts.ToArray());
        Assert.True(double.IsFinite(text.ContentHeight) && text.ContentHeight > 0);
        Assert.NotEmpty(actual.TextGlyphPositions.ToArray());
        Assert.Equal(expected.TextGlyphPositions.ToArray(), actual.TextGlyphPositions.ToArray());
        using var actualScene = new CadPlanSceneCompiler().Compile(actual);
        using var expectedScene = new CadPlanSceneCompiler().Compile(expected);
        using GpuPicture actualPicture = actualScene.CreatePicture();
        using GpuPicture expectedPicture = expectedScene.CreatePicture();
        Assert.Equal(NativeStream(expectedPicture), NativeStream(actualPicture));
        using var actualPrint = new CadPrintPlanCompiler().Compile(actual);
        using var expectedPrint = new CadPrintPlanCompiler().Compile(expected);
        using GpuPicture actualPage = actualPrint.CreatePagePicture();
        using GpuPicture expectedPage = expectedPrint.CreatePagePicture();
        Assert.Equal(NativeStream(expectedPage), NativeStream(actualPage));
        Assert.Equal(original, source.ColumnData.Heights);
    }

    private static CadDocumentSnapshot Compile(MText text)
    {
        var document = new CadDocument();
        document.Entities.Add(text);
        return new CadSnapshotCompiler().Compile(new CadDocumentSession(document),
            new CadSnapshotOptions { TextFontResolver = new CadFontManagerTextResolver(InterFontFamily.Regular) });
    }

    private static byte[] NativeStream(GpuPicture picture)
    {
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 96U, 1U,
            out NativeCompiledPicture? native, out NativePictureCompileFailure failure), failure.ToString());
        return native!.Stream.ToArray();
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf, 0)]
    [InlineData(CadDocumentFormat.Dwg, 0)]
    [InlineData(CadDocumentFormat.Dxf, -3)]
    [InlineData(CadDocumentFormat.Dwg, -3)]
    [InlineData(CadDocumentFormat.Dxf, 0.01)]
    [InlineData(CadDocumentFormat.Dwg, 0.01)]
    public async Task FinalManualHeightRoundTripsWithoutChangingMetadataOrGlyphPlacement(
        CadDocumentFormat format, double finalHeight)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        var source = new MText
        {
            Value = @"AA\PBB\PAA\PBB", Height = 10,
            AttachmentPoint = AttachmentPointType.TopLeft,
        };
        source.ColumnData.ColumnType = ColumnType.DynamicColumns;
        source.ColumnData.ColumnCount = 2;
        source.ColumnData.Width = 40;
        source.ColumnData.Gutter = 5;
        source.ColumnData.Heights.AddRange([20, finalHeight]);
        document.Entities.Add(source);
        var session = new CadDocumentSession(document);
        var options = new CadSnapshotOptions
        {
            TextFontResolver = new CadFontManagerTextResolver(InterFontFamily.Regular),
        };
        var compiler = new CadSnapshotCompiler();
        CadDocumentSnapshot before = compiler.Compile(session, options);
        Assert.Single(before.MTexts.ToArray());
        using var output = new MemoryStream();
        var store = new CadDocumentStore();
        await store.SaveAsync(session, output, format, new CadSaveOptions { AllowUncertifiedWrite = true });
        output.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(output, format);
        CadDocumentSnapshot after = compiler.Compile(loaded.Session, options);
        Assert.Single(after.MTexts.ToArray());
        Assert.Equal(before.TextGlyphPositions.ToArray(), after.TextGlyphPositions.ToArray());
        Assert.Equal(before.MTexts.Span[0].ContentHeight, after.MTexts.Span[0].ContentHeight);
        Assert.Equal(new double[] { 20, finalHeight }, loaded.Session.Read(value =>
            value.Entities.OfType<MText>().Single().ColumnData.Heights.ToArray()));
        Assert.Equal(new double[] { 20, finalHeight }, source.ColumnData.Heights);
    }

    [Theory]
    [InlineData(ColumnType.StaticColumns, 0, 1)]
    [InlineData(ColumnType.StaticColumns, -3, 1)]
    [InlineData(ColumnType.DynamicColumns, 0, 0)]
    [InlineData(ColumnType.DynamicColumns, -3, 0)]
    [InlineData(ColumnType.DynamicColumns, double.NaN, 1)]
    [InlineData(ColumnType.DynamicColumns, double.PositiveInfinity, 1)]
    [InlineData(ColumnType.DynamicColumns, double.MaxValue, 0)]
    public void InvalidActiveColumnLimitsRemainRejected(ColumnType type, double height, int index)
    {
        var document = new CadDocument();
        var source = new MText { Value = "TEXT", Height = 10 };
        source.ColumnData.ColumnType = type;
        source.ColumnData.ColumnCount = 2;
        source.ColumnData.Width = 40;
        source.ColumnData.Heights.AddRange([20, 20]);
        source.ColumnData.Heights[index] = height;
        document.Entities.Add(source);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(new CadDocumentSession(document),
            new CadSnapshotOptions { TextFontResolver = new CadFontManagerTextResolver(InterFontFamily.Regular) });
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
        Assert.Empty(snapshot.MTexts.ToArray());
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf, ColumnType.StaticColumns, false)]
    [InlineData(CadDocumentFormat.Dwg, ColumnType.StaticColumns, false)]
    [InlineData(CadDocumentFormat.Dxf, ColumnType.DynamicColumns, false)]
    [InlineData(CadDocumentFormat.Dwg, ColumnType.DynamicColumns, false)]
    [InlineData(CadDocumentFormat.Dxf, ColumnType.DynamicColumns, true)]
    [InlineData(CadDocumentFormat.Dwg, ColumnType.DynamicColumns, true)]
    public async Task LoadedColumnsRetainPositionedGlyphsAndNativePicture(
        CadDocumentFormat format, ColumnType type, bool autoHeight)
    {
        var document = new CadDocument();
        document.Header.Version = ACadVersion.AC1032;
        var text = new MText
        {
            Value = @"FIRST\NSECOND",
            Height = 4,
            RectangleWidth = 40,
            RectangleHeight = 50,
            AttachmentPoint = AttachmentPointType.TopLeft,
        };
        text.ColumnData.ColumnType = type;
        text.ColumnData.ColumnCount = 2;
        text.ColumnData.Width = 40;
        text.ColumnData.Gutter = 5;
        text.ColumnData.FlowReversed = true;
        text.ColumnData.AutoHeight = autoHeight;
        if (type == ColumnType.DynamicColumns && !autoHeight)
            text.ColumnData.Heights.AddRange([50, 60]);
        document.Entities.Add(text);
        var options = new CadSnapshotOptions
        {
            TextFontResolver = new CadFontManagerTextResolver(InterFontFamily.Regular),
        };
        var compiler = new CadSnapshotCompiler();
        var session = new CadDocumentSession(document);
        CadDocumentSnapshot before = compiler.Compile(session, options);
        Assert.Single(before.MTexts.ToArray());

        using var output = new MemoryStream();
        var store = new CadDocumentStore();
        await store.SaveAsync(session, output, format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        output.Position = 0;
        var loaded = await store.LoadAsync(output, format);
        MText restored = loaded.Session.Read(value => value.Entities.OfType<MText>().Single());
        Assert.Equal(2, restored.ColumnData.ColumnCount);
        Assert.Equal(text.ColumnData.Heights, restored.ColumnData.Heights);
        CadDocumentSnapshot after = compiler.Compile(loaded.Session, options);
        CadMTextPrimitive primitive = Assert.Single(after.MTexts.ToArray());
        Assert.Equal(2, primitive.ColumnCount);
        Assert.Equal(before.TextGlyphPositions.ToArray(), after.TextGlyphPositions.ToArray());
        Assert.Equal(before.MTexts.Span[0].ContentWidth, primitive.ContentWidth);
        Assert.Equal(before.MTexts.Span[0].ContentHeight, primitive.ContentHeight);
        using var scene = new CadPlanSceneCompiler().Compile(after);
        Assert.Contains(scene.DrawingContext.Commands.ToArray(), command =>
            command.Type == RenderCommandType.DrawGlyphRun);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 96U, 1U,
            out NativeCompiledPicture? native, out NativePictureCompileFailure failure), failure.ToString());
        Assert.NotNull(native);
        Assert.True(native.NativeDrawCount > 0);
    }

    [Fact]
    public void CopiedColumnHeightEditsDoNotMutateOriginal()
    {
        var text = new MText();
        text.ColumnData.ColumnType = ColumnType.DynamicColumns;
        text.ColumnData.ColumnCount = 2;
        text.ColumnData.Heights.AddRange([10, 20]);
        var clone = (MText)text.Clone();
        Assert.NotSame(text.ColumnData, clone.ColumnData);
        Assert.NotSame(text.ColumnData.Heights, clone.ColumnData.Heights);
        clone.ColumnData.Heights[0] = 100;
        clone.ColumnData.Heights.Add(30);
        Assert.Equal(new double[] { 10, 20 }, text.ColumnData.Heights);
    }
}
