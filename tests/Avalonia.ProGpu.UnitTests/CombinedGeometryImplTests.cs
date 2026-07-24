using Xunit;
using ProGPU.Vector;
using Avalonia;
using Avalonia.Media;

namespace Avalonia.ProGpu.UnitTests
{
    public class CombinedGeometryImplTests
    {
        [Fact]
        public void Combining_Fill_With_Empty_Stroke_Returns_Fill_Bounds()
        {
            var fill = new ProGPU.Vector.PathGeometry();
            var figure = new ProGPU.Vector.PathFigure { StartPoint = new System.Numerics.Vector2(0, 0) };
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new System.Numerics.Vector2(100, 0)));
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new System.Numerics.Vector2(100, 100)));
            figure.Segments.Add(new ProGPU.Vector.LineSegment(new System.Numerics.Vector2(0, 100)));
            fill.Figures.Add(figure);

            var result = new CombinedGeometryImpl(fill);

            Assert.Equal(new Rect(0, 0, 100, 100), result.Bounds);
        }

        [Theory]
        [InlineData(GeometryCombineMode.Exclude, 0)]
        [InlineData(GeometryCombineMode.Intersect, 1)]
        [InlineData(GeometryCombineMode.Union, 2)]
        [InlineData(GeometryCombineMode.Xor, 3)]
        public void ForceCreate_Preserves_Geometry_Operation(GeometryCombineMode mode, int expectedOperation)
        {
            var first = new RectangleGeometryImpl(new Rect(0, 0, 100, 100));
            var second = new RectangleGeometryImpl(new Rect(10, 10, 80, 80));

            var result = CombinedGeometryImpl.ForceCreate(mode, first, second);

            Assert.True(result.Path.IsCombined);
            Assert.Same(first.Path, result.Path.PathA);
            Assert.Same(second.Path, result.Path.PathB);
            Assert.Equal(expectedOperation, result.Path.Op);
        }

        [Fact]
        public void GeometryGroup_Preserves_EvenOdd_FillRule()
        {
            var child = new RectangleGeometryImpl(new Rect(0, 0, 100, 100));

            var result = new GeometryGroupImpl(Avalonia.Media.FillRule.EvenOdd, new[] { child });

            Assert.Equal(ProGPU.Vector.FillRule.EvenOdd, result.Path.FillRule);
        }
    }
}
