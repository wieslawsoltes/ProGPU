using System.Buffers.Binary;
using ProGPU.Backend.Native;
using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableTransformResourceTests
{
    [Fact]
    public void PrimitiveParametersRemainOriginalDoublesWithoutModuloOrNarrowing()
    {
        double value = double.BitIncrement(360000000.25);
        var rotate = new PortableRotateTransform(value, -0.0, double.BitIncrement(1));
        Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(rotate.Angle));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(rotate.CenterX));
        Assert.Equal(BitConverter.DoubleToInt64Bits(double.BitIncrement(1)), BitConverter.DoubleToInt64Bits(rotate.CenterY));
        var skew = new PortableSkewTransform(double.NaN, double.PositiveInfinity, 3, 4);
        Assert.True(double.IsNaN(skew.AngleX)); Assert.True(double.IsPositiveInfinity(skew.AngleY));
        // DTO capture is not consumer admission; invalid values stay invalid.
    }

    [Fact]
    public void GroupOwnsOrderedListButRetainsEachOriginalSourceIdentity()
    {
        object a = new(), b = new(); object[] caller = [a, b, a];
        var group = new PortableTransformGroup(caller);
        caller[0] = new(); Array.Reverse(caller);
        Assert.Equal(3, group.Children.Count);
        Assert.Same(a, group.Children[0]); Assert.Same(b, group.Children[1]); Assert.Same(a, group.Children[2]);
        Assert.Throws<NotSupportedException>(() => ((IList<object>)group.Children)[1] = a);
        Assert.Empty(new PortableTransformGroup([]).Children);
        Assert.Throws<ArgumentNullException>(() => new PortableTransformGroup([a, null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortableTransformGroup(new object[PortableTransformGroup.MaximumChildCount + 1]));
    }

    [Fact]
    public void GroupWriterRejectsLateInvalidChildWithoutChangingPriorBytes()
    {
        var writer = new NativeMilBatchBuilder(); writer.SetTranslateTransform(17, 2, 3);
        byte[] before = writer.ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetTransformGroup(19, [17, 23, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetTransformGroup(19, new uint[(1 << 20) + 1]));
        Assert.Equal(before, writer.ToArray());
        writer.SetTransformGroup(19, [17, 23, 17]);
        byte[] bytes = writer.ToArray(); int offset = before.Length;
        Assert.Equal(28, bytes.Length - offset);
        Assert.Equal(12U, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12)));
        Assert.Equal(new uint[] { 17, 23, 17 }, Enumerable.Range(0, 3)
            .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 16 + i * 4))).ToArray());
    }

    [Fact]
    public void AllPrimitivePacketsPreserveOriginalCurrentValuesAndZeroAnimationHandles()
    {
        var writer = new NativeMilBatchBuilder();
        double angle = double.BitIncrement(450), center = double.BitIncrement(-1);
        writer.SetRotateTransform(2, angle, center, -0.0);
        byte[] rotate = writer.ToArray();
        Assert.Equal(48, rotate.Length);
        Assert.Equal(BitConverter.DoubleToInt64Bits(angle), BinaryPrimitives.ReadInt64LittleEndian(rotate.AsSpan(12)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(center), BinaryPrimitives.ReadInt64LittleEndian(rotate.AsSpan(20)));
        Assert.Equal(long.MinValue, BinaryPrimitives.ReadInt64LittleEndian(rotate.AsSpan(28)));
        Assert.All(rotate[36..], value => Assert.Equal((byte)0, value));
        writer.SetSkewTransform(3, -721.25, 360000000.125, center, 4);
        byte[] both = writer.ToArray(); Assert.Equal(108, both.Length);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-721.25), BinaryPrimitives.ReadInt64LittleEndian(both.AsSpan(60)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(360000000.125), BinaryPrimitives.ReadInt64LittleEndian(both.AsSpan(68)));
        Assert.All(both[92..], value => Assert.Equal((byte)0, value));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetScaleTransform(4, 1, double.NaN));
        Assert.Equal(both, writer.ToArray());
    }
}
