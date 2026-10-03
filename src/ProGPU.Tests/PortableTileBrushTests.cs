using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableTileBrushTests
{
    private static readonly PortableRect Viewport = new(-0.0, 2.5, 30.25, 40.5);
    private static readonly PortableRect Viewbox = new(-6.75, 9.25, 8.5, 6.125);
    private static readonly PortableMatrix3x2 Transform = new(2, -.5, .25, 3, -0.0, 7.5);
    private static readonly PortableMatrix3x2 RelativeTransform = new(-1, 0, -0.0, .5, .75, -.25);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void LegacyConstructorStillRejectsNullForEveryKind(int kind)
    {
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() => Legacy((PortableTileBrushKind)kind, null!));
        Assert.Equal("content", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void LegacyConstructorKeepsOriginalKindAndSourceIdentity(int kind)
    {
        var source = new Source();
        PortableTileBrush brush = Legacy((PortableTileBrushKind)kind, source);
        Assert.Equal((PortableTileBrushKind)kind, brush.Kind);
        Assert.Same(source, brush.Content);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public void ExplicitNullVisualRetainsCompleteMappingAndHasNoSyntheticSource()
    {
        PortableTileBrush brush = Visual(null);
        Assert.Equal(PortableTileBrushKind.Visual, brush.Kind);
        Assert.Null(brush.Content);
        AssertSnapshotEqual(Legacy(PortableTileBrushKind.Visual, new object()), brush);
        AssertDoubleBits(-0.0, brush.Viewport.X);
        AssertDoubleBits(-0.0, brush.Transform.OffsetX);
        AssertDoubleBits(-0.0, brush.RelativeTransform.M21);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void VisualFactoryRetainsLiveIdentityAndOriginalTransformFlagPolicy(bool hasTransform, bool hasRelativeTransform)
    {
        var source = new Source();
        PortableTileBrush brush = Visual(source, hasTransform: hasTransform, hasRelativeTransform: hasRelativeTransform);
        Assert.Same(source, brush.Content);
        AssertSnapshotEqual(Legacy(PortableTileBrushKind.Visual, source,
            hasTransform: hasTransform, hasRelativeTransform: hasRelativeTransform), brush);
        AssertMatrixBits(hasTransform ? Transform : PortableMatrix3x2.Identity, brush.Transform);
        AssertMatrixBits(hasRelativeTransform ? RelativeTransform : PortableMatrix3x2.Identity, brush.RelativeTransform);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.0)]
    [InlineData(.25)]
    [InlineData(-.5)]
    [InlineData(1.25)]
    public void VisualFactoryReusesLegacyOpacitySnapshotPolicy(double opacity)
    {
        PortableTileBrush expected = Legacy(PortableTileBrushKind.Visual, new object(), opacity);
        PortableTileBrush actual = Visual(null, opacity);
        AssertSnapshotEqual(expected, actual);
        AssertDoubleBits(double.IsFinite(opacity) ? opacity : 1, actual.Opacity);
    }

    [Fact]
    public void NullVisualDoesNotRewriteInvalidMappingIntoAnAdmittedCapture()
    {
        // Transport is not consumer admission: preserve original flags/values.
        var viewport = new PortableRect(double.NaN, double.PositiveInfinity, -2, -0.0);
        var transform = new PortableMatrix3x2(double.NegativeInfinity, -0.0, 0, 1, double.NaN, 0);
        PortableTileBrush brush = PortableTileBrush.Visual(null, .75, viewport, PortableRect.Empty,
            (PortableBrushMappingMode)91, (PortableBrushMappingMode)92, (PortableTileMode)93,
            (PortableStretch)94, (PortableAlignmentX)95, (PortableAlignmentY)96,
            true, transform, false, transform);
        Assert.Null(brush.Content);
        AssertRectBits(viewport, brush.Viewport);
        Assert.True(brush.Viewbox.IsEmpty);
        Assert.Equal((PortableBrushMappingMode)91, brush.ViewportUnits);
        Assert.Equal((PortableBrushMappingMode)92, brush.ViewboxUnits);
        Assert.Equal((PortableTileMode)93, brush.TileMode);
        Assert.Equal((PortableStretch)94, brush.Stretch);
        Assert.Equal((PortableAlignmentX)95, brush.AlignmentX);
        Assert.Equal((PortableAlignmentY)96, brush.AlignmentY);
        AssertMatrixBits(transform, brush.Transform);
        Assert.False(brush.HasRelativeTransform);
        AssertMatrixBits(PortableMatrix3x2.Identity, brush.RelativeTransform);
    }

    private static PortableTileBrush Legacy(PortableTileBrushKind kind, object content,
        double opacity = .375, bool hasTransform = true, bool hasRelativeTransform = true)
        => new(kind, content, opacity, Viewport, Viewbox,
            PortableBrushMappingMode.Absolute, PortableBrushMappingMode.RelativeToBoundingBox,
            PortableTileMode.FlipXY, PortableStretch.UniformToFill, PortableAlignmentX.Right, PortableAlignmentY.Bottom,
            hasTransform, Transform, hasRelativeTransform, RelativeTransform);

    private static PortableTileBrush Visual(object? content, double opacity = .375,
        bool hasTransform = true, bool hasRelativeTransform = true)
        => PortableTileBrush.Visual(content, opacity, Viewport, Viewbox,
            PortableBrushMappingMode.Absolute, PortableBrushMappingMode.RelativeToBoundingBox,
            PortableTileMode.FlipXY, PortableStretch.UniformToFill, PortableAlignmentX.Right, PortableAlignmentY.Bottom,
            hasTransform, Transform, hasRelativeTransform, RelativeTransform);

    private static void AssertSnapshotEqual(PortableTileBrush expected, PortableTileBrush actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        AssertDoubleBits(expected.Opacity, actual.Opacity);
        AssertRectBits(expected.Viewport, actual.Viewport);
        AssertRectBits(expected.Viewbox, actual.Viewbox);
        Assert.Equal(expected.ViewportUnits, actual.ViewportUnits);
        Assert.Equal(expected.ViewboxUnits, actual.ViewboxUnits);
        Assert.Equal(expected.TileMode, actual.TileMode);
        Assert.Equal(expected.Stretch, actual.Stretch);
        Assert.Equal(expected.AlignmentX, actual.AlignmentX);
        Assert.Equal(expected.AlignmentY, actual.AlignmentY);
        Assert.Equal(expected.HasTransform, actual.HasTransform);
        Assert.Equal(expected.HasRelativeTransform, actual.HasRelativeTransform);
        AssertMatrixBits(expected.Transform, actual.Transform);
        AssertMatrixBits(expected.RelativeTransform, actual.RelativeTransform);
    }

    private static void AssertRectBits(PortableRect expected, PortableRect actual)
    {
        AssertDoubleBits(expected.X, actual.X); AssertDoubleBits(expected.Y, actual.Y);
        AssertDoubleBits(expected.Width, actual.Width); AssertDoubleBits(expected.Height, actual.Height);
        Assert.Equal(expected.IsEmpty, actual.IsEmpty);
    }

    private static void AssertMatrixBits(PortableMatrix3x2 expected, PortableMatrix3x2 actual)
    {
        AssertDoubleBits(expected.M11, actual.M11); AssertDoubleBits(expected.M12, actual.M12);
        AssertDoubleBits(expected.M21, actual.M21); AssertDoubleBits(expected.M22, actual.M22);
        AssertDoubleBits(expected.OffsetX, actual.OffsetX); AssertDoubleBits(expected.OffsetY, actual.OffsetY);
    }

    private static void AssertDoubleBits(double expected, double actual)
        => Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));

    private sealed class Source : IPortableVisualStateSource
    {
        internal int Calls;
        public bool TryGetPortableVisualState(out PortableVisualState state)
        {
            ++Calls;
            throw new InvalidOperationException("Neutral brush capture cannot inspect or replace the source visual.");
        }
    }
}
