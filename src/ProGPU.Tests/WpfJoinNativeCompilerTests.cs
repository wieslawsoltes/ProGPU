using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class WpfJoinNativeCompilerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompiledSourcePathKeepsFullPolicyAndSmoothRound(bool curved, bool smooth)
    {
        var pen = WpfJoinInputAndBoundsTests.CreatePen();
        pen.ClipMiterAtLimit = true; // Redundant intent must not create Clip+Round.
        var command = WpfJoinInputAndBoundsTests.Command(
            WpfJoinInputAndBoundsTests.Path(curved: curved, smooth: smooth), pen);
        using var picture = new GpuPicture([command], [], [], [], []);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 991, 1,
            out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using (compiled)
        {
            var geometry = ReadResource<NativeGeometryPrimitive>(compiled, NativeSceneResourceKind.GeometryBatch);
            var join = Assert.Single(geometry.Where(p => p.Kind == NativeGeometryPrimitiveKind.PathJoin));
            Assert.Equal(smooth ? (uint)NativeStrokeJoin.Round : (uint)NativeStrokeJoin.Miter, (uint)join.StartCap);
            Assert.True((join.Flags & NativeGeometryPrimitiveFlags.WpfJoinSemantics) != 0);
            Assert.Equal(!smooth, (join.Flags & NativeGeometryPrimitiveFlags.ClipMiterAtLimit) != 0);
            Assert.All(geometry.Where(p => p.Kind != NativeGeometryPrimitiveKind.PathJoin),
                p => Assert.Equal(0U, (uint)(p.Flags & NativeGeometryPrimitiveFlags.WpfJoinSemantics)));
        }
        pen.UseWpfJoinSemantics = false;
        using var generic = new GpuPicture([command], [], [], [], []);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(generic, 991, 2, out var legacy, out failure), failure.ToString());
        Assert.NotNull(legacy);
        using (legacy)
        {
            var geometry = ReadResource<NativeGeometryPrimitive>(legacy, NativeSceneResourceKind.GeometryBatch);
            Assert.All(geometry, p => Assert.Equal(0U, (uint)(p.Flags & NativeGeometryPrimitiveFlags.WpfJoinSemantics)));
            if (smooth) Assert.DoesNotContain(geometry, p => p.Kind == NativeGeometryPrimitiveKind.PathJoin);
        }
    }

    [Fact]
    public void PolylineTransportRetainsPolicySourceOrderAndDashIdentity()
    {
        var pen = WpfJoinInputAndBoundsTests.CreatePen();
        pen.DashArray = [3.75, 1.25]; pen.DashOffset = -.5;
        var recorder = new GpuPictureRecorder();
        var drawing = recorder.BeginRecording(new Rect(0, 0, 64, 64));
        drawing.DrawPolyline(pen, [new(12.25f, 40.25f), new(32.25f, 40.25f), new(12.25f, 40.25f)]);
        using var picture = recorder.EndRecording();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 992, 1,
            out var compiled, out var failure), failure.ToString());
        Assert.NotNull(compiled);
        using (compiled)
        {
            var stroke = Assert.Single(ReadResource<NativeSceneStroke>(compiled, NativeSceneResourceKind.StrokeBatch));
            Assert.Equal(NativePolylineFlags.WpfJoinSemantics, stroke.Flags);
            Assert.Equal(3UL, stroke.PointCount);
            Assert.Equal(2UL, stroke.DashIntervalCount);
            Assert.Equal(-.5, stroke.DashOffset);
            Assert.Equal(1, compiled.SourceCommandCount);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnsupportedSourceJoinCannotPublishEvenEmptyScene(int invalid)
    {
        var pen = WpfJoinInputAndBoundsTests.CreatePen();
        if (invalid == 0) pen.LineJoin = PenLineJoin.MiterOrBevel;
        if (invalid == 1) pen.StrokeTransformMode = PenStrokeTransformMode.Fixed;
        if (invalid == 2) pen.Thickness = Pen.HairlineThickness;
        using var picture = new GpuPicture(
            [WpfJoinInputAndBoundsTests.Command(new PathGeometry(), pen)], [], [], [], []);
        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(picture, 993, 1, out var compiled, out var failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnsupportedStroke, failure.Error);
    }

    private static T[] ReadResource<T>(NativeCompiledPicture compiled, NativeSceneResourceKind kind) where T : unmanaged
    {
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resources = MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(compiled.Stream.Slice(
            checked((int)header.ResourceOffset), checked((int)header.ResourceCount * Unsafe.SizeOf<NativeMethods.SceneResource>())));
        foreach (var resource in resources)
            if (resource.Kind == kind)
                return MemoryMarshal.Cast<byte, T>(compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset), checked((int)resource.PayloadSize))).ToArray();
        throw new InvalidOperationException($"Missing owned {kind} resource.");
    }
}
