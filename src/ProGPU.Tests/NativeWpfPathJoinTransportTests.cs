using System.Numerics;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public class NativeWpfPathJoinTransportTests
{
    [Theory]
    [InlineData(NativeStrokeJoin.Miter)]
    [InlineData(NativeStrokeJoin.Bevel)]
    [InlineData(NativeStrokeJoin.Round)]
    public void NormalPathJoinRetainsSourcePolicyAndJoinKind(NativeStrokeJoin join)
    {
        var value = Create(NativeGeometryPrimitiveKind.PathJoin, join,
            NativeGeometryPrimitiveFlags.WpfJoinSemantics |
            NativeGeometryPrimitiveFlags.EdgeAliased);

        Assert.Equal(1U << 8, (uint)NativeGeometryPrimitiveFlags.WpfJoinSemantics);
        Assert.Equal(NativeGeometryPrimitiveKind.PathJoin, value.Kind);
        Assert.Equal((NativeStrokeCap)join, value.StartCap);
        Assert.Equal(NativeStrokeCap.Flat, value.EndCap);
        Assert.Equal(NativeGeometryPrimitiveFlags.WpfJoinSemantics |
            NativeGeometryPrimitiveFlags.EdgeAliased |
            (NativeGeometryPrimitiveFlags)((uint)join << 3), value.Flags);
        Assert.Equal(new Vector2(4, 5), value.P0);
        Assert.Equal(Vector2.UnitX, value.P1);
        Assert.Equal(Vector2.UnitY, value.P2);
        Assert.Equal(new Vector2(2, 0), value.P3);
        Assert.Equal(4f, value.StrokeThickness);
        Assert.Equal(Matrix3x2.Identity, value.Transform);
    }

    [Theory]
    [InlineData(NativeGeometryPrimitiveKind.Line)]
    [InlineData(NativeGeometryPrimitiveKind.CubicBezier)]
    [InlineData(NativeGeometryPrimitiveKind.PathCap)]
    public void NonJoinSourcePolicyRejectsBeforePublishingValue(NativeGeometryPrimitiveKind kind)
    {
        NativeGeometryPrimitive? candidate = null;
        var error = Assert.Throws<ArgumentException>(() =>
        {
            candidate = Create(kind, NativeStrokeJoin.Miter,
                NativeGeometryPrimitiveFlags.WpfJoinSemantics);
        });

        Assert.Equal("flags", error.ParamName);
        Assert.Null(candidate);
    }

    [Theory]
    [InlineData(NativeStrokeJoin.MiterOrBevel, NativeGeometryPrimitiveFlags.None)]
    [InlineData(NativeStrokeJoin.Miter, NativeGeometryPrimitiveFlags.Hairline)]
    [InlineData(NativeStrokeJoin.Miter, NativeGeometryPrimitiveFlags.FixedDeviceStroke)]
    public void UnsupportedJoinOrWidthRejectsBeforePublishingValue(
        NativeStrokeJoin join, NativeGeometryPrimitiveFlags width)
    {
        NativeGeometryPrimitive? candidate = null;
        var error = Assert.Throws<ArgumentException>(() =>
        {
            candidate = Create(NativeGeometryPrimitiveKind.PathJoin, join,
                NativeGeometryPrimitiveFlags.WpfJoinSemantics | width);
        });

        Assert.Equal("flags", error.ParamName);
        Assert.Null(candidate);
    }

    [Fact]
    public void IndependentClippingDoesNotSelectWpfReversalPolicy()
    {
        var clip = Create(NativeGeometryPrimitiveKind.PathJoin, NativeStrokeJoin.Miter,
            NativeGeometryPrimitiveFlags.ClipMiterAtLimit);
        var source = Create(NativeGeometryPrimitiveKind.PathJoin, NativeStrokeJoin.Miter,
            NativeGeometryPrimitiveFlags.WpfJoinSemantics);
        var legacy = Create(NativeGeometryPrimitiveKind.PathJoin, NativeStrokeJoin.Miter,
            NativeGeometryPrimitiveFlags.None);

        Assert.Equal(NativeGeometryPrimitiveFlags.ClipMiterAtLimit, clip.Flags);
        Assert.Equal(NativeGeometryPrimitiveFlags.WpfJoinSemantics, source.Flags);
        Assert.Equal(NativeGeometryPrimitiveFlags.None, legacy.Flags);
        Assert.Equal(clip.P0, source.P0);
        Assert.Equal(clip.P1, source.P1);
        Assert.Equal(clip.P2, source.P2);
        Assert.Equal(clip.P3, source.P3);
        Assert.Equal(clip.StrokeThickness, source.StrokeThickness);
        Assert.Equal(clip.Transform, source.Transform);
    }

    private static NativeGeometryPrimitive Create(
        NativeGeometryPrimitiveKind kind,
        NativeStrokeJoin join,
        NativeGeometryPrimitiveFlags flags) => new(
            kind, new Vector2(4, 5), Vector2.UnitX, Vector4.One, Matrix3x2.Identity,
            p2: Vector2.UnitY, p3: new Vector2(2, 0), strokeThickness: 4,
            flags: flags, startCap: (NativeStrokeCap)join);
}
