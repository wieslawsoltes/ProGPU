using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadClipboardTests
{
    [Fact]
    public void CopyBaseEnvelopePastesAcrossDocumentsWithExactUndoRedo()
    {
        var source = new CadDocument();
        var sourceLayer = new Layer("CLIP") { Color = ACadSharp.Color.Red };
        source.Layers.Add(sourceLayer);
        var line = new Line(new XYZ(1, 2, 3), new XYZ(4, 2, 3))
        {
            Layer = sourceLayer,
        };
        var block = new BlockRecord("CLIP_BLOCK");
        block.Entities.Add(new Circle
        {
            Center = XYZ.Zero,
            Radius = 2,
            Layer = sourceLayer,
        });
        source.BlockRecords.Add(block);
        var insert = new Insert(block)
        {
            InsertPoint = new XYZ(5, 6, 3),
            Layer = sourceLayer,
        };
        source.Entities.Add(line);
        source.Entities.Add(insert);
        var sourceSession = new CadDocumentSession(source);
        var basePoint = new CadPoint3D(1, 2, 3);

        string text = CadClipboardCodec.Encode(
            sourceSession,
            [line.Handle, insert.Handle, line.Handle],
            basePoint);

        Assert.StartsWith("PROGPU-CAD-CLIPBOARD\t1\t", text, StringComparison.Ordinal);
        Assert.True(CadClipboardCodec.TryDecode(
            text,
            out CadClipboardPayload? payload,
            out string? decodeError),
            decodeError);
        Assert.NotNull(payload);
        Assert.Equal(basePoint, payload.BasePoint);
        Assert.Equal(2, payload.EntityCount);
        Assert.True(payload.EncodedByteCount > 0);

        var destination = new CadDocument();
        var destinationLayer = new Layer("CLIP")
        {
            Color = ACadSharp.Color.Blue,
        };
        destination.Layers.Add(destinationLayer);
        var destinationSession = new CadDocumentSession(destination);
        var history = new CadDocumentHistory(destinationSession);
        var command = new CadPasteModelSpaceEntitiesCommand(
            payload,
            new CadPoint3D(11, 22, 8));

        history.Execute(command);

        Assert.Equal(new CadPoint3D(10, 20, 5), command.Translation);
        Assert.Equal(1UL, destinationSession.ContentGeneration);
        Assert.Equal(2, destination.Entities.Count);
        Assert.All(command.CurrentHandles.ToArray(), handle => Assert.NotEqual(0UL, handle));
        Line pastedLine = Assert.Single(destination.Entities.OfType<Line>());
        Assert.Equal(new XYZ(11, 22, 8), pastedLine.StartPoint);
        Assert.Equal(new XYZ(14, 22, 8), pastedLine.EndPoint);
        Assert.Same(destinationLayer, pastedLine.Layer);
        Insert pastedInsert = Assert.Single(destination.Entities.OfType<Insert>());
        Assert.Equal(new XYZ(15, 26, 8), pastedInsert.InsertPoint);
        Assert.Equal("CLIP_BLOCK", pastedInsert.Block.Name);
        Assert.Same(destination.BlockRecords["CLIP_BLOCK"], pastedInsert.Block);
        Assert.Single(pastedInsert.Block.Entities.OfType<Circle>());

        Entity[] retained = destination.Entities.ToArray();
        Assert.True(history.TryUndo(out ulong undoneGeneration));
        Assert.Equal(2UL, undoneGeneration);
        Assert.Empty(destination.Entities);
        Assert.All(command.CurrentHandles.ToArray(), handle => Assert.Equal(0UL, handle));
        Assert.All(retained, entity =>
        {
            Assert.Null(entity.Owner);
            Assert.Null(entity.Document);
            Assert.Equal(0UL, entity.Handle);
        });

        Assert.True(history.TryRedo(out ulong redoneGeneration));
        Assert.Equal(3UL, redoneGeneration);
        Assert.Equal(retained, destination.Entities.ToArray());

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(destinationSession);
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            snapshot.ContentGeneration,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(picture.CommandCount, native.SourceCommandCount);
    }

    [Fact]
    public void ClipboardEnvelopeRejectsForeignTamperedAndBoundedInput()
    {
        Assert.False(CadClipboardCodec.TryDecode(
            "ordinary text",
            out _,
            out string? foreignError));
        Assert.Contains("does not contain", foreignError);

        var document = new CadDocument();
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        string encoded = CadClipboardCodec.Encode(
            session,
            [line.Handle],
            new CadPoint3D(-0.0, 2.5, -3));
        char replacement = encoded[^1] == 'A' ? 'B' : 'A';
        string tampered = encoded[..^1] + replacement;

        Assert.False(CadClipboardCodec.TryDecode(
            tampered,
            out _,
            out string? checksumError));
        Assert.Contains("checksum", checksumError);
        Assert.False(CadClipboardCodec.TryDecode(
            encoded,
            out _,
            out string? countError,
            maximumEntityCount: 1,
            maximumEncodedByteCount: 16));
        Assert.Contains("bound", countError);
        Assert.Throws<ArgumentException>(() =>
            CadClipboardCodec.Encode(
                session,
                [line.Handle],
                new CadPoint3D(double.NaN, 0, 0)));
        Assert.Throws<InvalidOperationException>(() =>
            CadClipboardCodec.Encode(
                session,
                [line.Handle, line.Handle + 1],
                CadPoint3D.Zero));
    }
}
