using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using System.Numerics;
using System.Text;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadShxShapeTests
{
    [Fact]
    public void SnapshotRetainsOneScaledShapePathForSceneAndExactSelection()
    {
        (CadDocumentSession session, CadShxFontCatalog catalog, Shape source) =
            CreateShapeSession();

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { ShxFontResolver = catalog });
        CadShxShapePrimitive shape = Assert.Single(snapshot.ShxShapes.ToArray());
        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(CadEntityKind.ShxShape, header.Kind);
        Assert.Equal((ushort)42, shape.Glyph.ShapeNumber);
        AssertPoint(new CadPoint3D(10, 20, 0), shape.Origin);
        AssertPoint(new CadPoint3D(6, 0, 0), shape.XAxis);
        AssertPoint(new CadPoint3D(0, 2, 0), shape.YAxis);
        AssertPoint(new CadPoint3D(10, 20, 0), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(16, 22, 0), snapshot.Bounds.Max);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.Null(command.Brush);
        Assert.NotNull(command.Pen);

        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            source.Handle,
            header.Kind,
            header.Bounds);
        CadPointHitResult edge = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(13, 20, 0),
            0.01);
        CadPointHitResult interior = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(13, 21, 0),
            0.01);
        Assert.Equal(CadPointHitStatus.Hit, edge.Status);
        Assert.Equal(CadPointHitStatus.Miss, interior.Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(
                    new CadPoint3D(9, 19, -1),
                    new CadPoint3D(17, 23, 1)),
                CadBoundsSelectionMode.Window).Status);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);

        using CadPrintPlan printPlan = new CadPrintPlanCompiler().Compile(snapshot);
        using GpuPicture page = printPlan.CreatePagePicture();
        Assert.Equal(1, printPlan.SceneStatistics.RecordedEntityCount);
        Assert.Equal(1, page.GetCommand(1).Picture!.CommandCount);
    }

    [Fact]
    public void RotationAndOcsComposeIntoShapeAxes()
    {
        (CadDocumentSession session, CadShxFontCatalog catalog, _) = CreateShapeSession(
            configure: shape =>
            {
                shape.InsertionPoint = new XYZ(1, 2, 3);
                shape.Normal = XYZ.AxisY;
                shape.Size = 3;
                shape.RelativeXScale = 2;
                shape.Rotation = Math.PI / 2;
                shape.ObliqueAngle = 0;
            });

        CadShxShapePrimitive shape = Assert.Single(new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { ShxFontResolver = catalog }).ShxShapes.ToArray());

        AssertPoint(new CadPoint3D(1, 2, 3), shape.Origin);
        AssertPoint(new CadPoint3D(0, 0, 6), shape.XAxis);
        AssertPoint(new CadPoint3D(3, 0, 0), shape.YAxis);
    }

    [Fact]
    public void ShapeInsideBlockRetainsRootHandleAndComposesItsPathBasis()
    {
        CadShxGlyphCache cache = new(CadShxFont.Parse(BuildStandardShx(
            (42, "BOX", new byte[] { 0x10, 0x14, 0x18, 0x1C, 0 }))));
        var catalog = new CadShxFontCatalog();
        catalog.Register("symbols.shx", cache);
        var document = new CadDocument();
        var style = new TextStyle("symbols")
        {
            Flags = StyleFlags.IsShape,
            Filename = "symbols.shx",
        };
        document.TextStyles.Add(style);
        var block = new BlockRecord("SYMBOL");
        block.Entities.Add(new Shape(style)
        {
            ShapeName = "BOX",
            ShapeNumber = 42,
        });
        var insert = new Insert(block)
        {
            InsertPoint = new XYZ(10, 20, 0),
            XScale = 2,
            YScale = 3,
            Rotation = Math.PI / 2,
        };
        document.Entities.Add(insert);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions { ShxFontResolver = catalog });
        CadShxShapePrimitive shape = Assert.Single(snapshot.ShxShapes.ToArray());

        Assert.Equal(insert.Handle, Assert.Single(snapshot.Entities.ToArray()).Handle);
        AssertPoint(new CadPoint3D(10, 20, 0), shape.Origin);
        AssertPoint(new CadPoint3D(0, 2, 0), shape.XAxis);
        AssertPoint(new CadPoint3D(-3, 0, 0), shape.YAxis);
    }

    [Fact]
    public async Task DxfRoundTripWritesShapeNameAndReloadsItWithoutInventingStyleIdentity()
    {
        (CadDocumentSession session, _, _) = CreateShapeSession();
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            session,
            stream,
            CadDocumentFormat.Dxf,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            CadDocumentFormat.Dxf,
            sourceName: "shape.dxf");
        Shape shape = loaded.Session.Read(document =>
            document.Entities.OfType<Shape>().Single());

        Assert.Equal("BOX", shape.ShapeName);
        Assert.Null(shape.ShapeStyle);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            loaded.Session,
            new CadSnapshotOptions { ShxFontResolver = CreateShapeCatalog() });
        Assert.Equal(CadEntityKind.ShxShape, Assert.Single(snapshot.Entities.ToArray()).Kind);
    }

    [Fact]
    public async Task DwgRoundTripWritesShapeNumberAndShapeFileStyle()
    {
        (CadDocumentSession session, _, _) = CreateShapeSession();
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            session,
            stream,
            CadDocumentFormat.Dwg,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            CadDocumentFormat.Dwg,
            sourceName: "shape.dwg");
        Shape shape = loaded.Session.Read(document =>
            document.Entities.OfType<Shape>().Single());

        Assert.Equal((ushort)42, shape.ShapeNumber);
        Assert.NotNull(shape.ShapeStyle);
        Assert.Equal("symbols.shx", shape.ShapeStyle.Filename);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            loaded.Session,
            new CadSnapshotOptions { ShxFontResolver = CreateShapeCatalog() });
        Assert.Equal(CadEntityKind.ShxShape, Assert.Single(snapshot.Entities.ToArray()).Kind);
    }

    [Fact]
    public void ThicknessAndMissingShapeIdentityRemainExplicitlyUnsupported()
    {
        (CadDocumentSession session, CadShxFontCatalog catalog, Shape source) =
            CreateShapeSession(configure: shape => shape.Thickness = 1);
        CadDocumentSnapshot thick = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { ShxFontResolver = catalog });

        Assert.Empty(thick.Entities.ToArray());
        Assert.Equal(1, thick.Statistics.UnsupportedEntityCount);
        Assert.Contains("thickness", Assert.Single(thick.Diagnostics.ToArray()).Message);

        session.Edit("Remove shape identity", _ =>
        {
            source.Thickness = 0;
            source.ShapeName = "MISSING";
            source.ShapeNumber = 0;
        });
        CadDocumentSnapshot missing = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { ShxFontResolver = catalog });
        Assert.Empty(missing.Entities.ToArray());
        Assert.Equal(1, missing.Statistics.UnsupportedEntityCount);
        Assert.Contains("could not be resolved", Assert.Single(missing.Diagnostics.ToArray()).Message);
    }

    private static (CadDocumentSession Session, CadShxFontCatalog Catalog, Shape Shape)
        CreateShapeSession(Action<Shape>? configure = null)
    {
        CadShxFontCatalog catalog = CreateShapeCatalog();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        Shape? shape = null;
        session.Edit("Add standalone SHX shape", document =>
        {
            var style = new TextStyle("symbols")
            {
                Flags = StyleFlags.IsShape,
                Filename = "symbols.shx",
            };
            document.TextStyles.Add(style);
            var created = new Shape(style)
            {
                ShapeName = "BOX",
                ShapeNumber = 42,
                InsertionPoint = new XYZ(10, 20, 0),
                Size = 2,
                RelativeXScale = 3,
            };
            configure?.Invoke(created);
            document.Entities.Add(created);
            shape = created;
        });
        return (session, catalog, shape!);
    }

    private static CadShxFontCatalog CreateShapeCatalog()
    {
        CadShxGlyphCache cache = new(CadShxFont.Parse(BuildStandardShx(
            (42, "BOX", new byte[] { 0x10, 0x14, 0x18, 0x1C, 0 }))));
        var catalog = new CadShxFontCatalog();
        catalog.Register("symbols.shx", cache);
        return catalog;
    }

    private static byte[] BuildStandardShx(
        params (ushort Number, string Name, byte[] Program)[] shapes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write(shapes.Min(shape => shape.Number));
        writer.Write(shapes.Max(shape => shape.Number));
        writer.Write(checked((ushort)shapes.Length));
        foreach ((ushort number, string name, byte[] program) in shapes)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            writer.Write(number);
            writer.Write(checked((ushort)(nameBytes.Length + 1 + program.Length)));
        }
        foreach ((ushort _, string name, byte[] program) in shapes)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
            writer.Write(program);
        }
        writer.Write("EOF"u8);
        return stream.ToArray();
    }

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.Equal(expected.X, actual.X, 10);
        Assert.Equal(expected.Y, actual.Y, 10);
        Assert.Equal(expected.Z, actual.Z, 10);
    }
}
