using System;
using Avalonia.Media;
using ProGPU.Vector;
using Xunit;
using ArcSegment = ProGPU.Vector.ArcSegment;
using VectorFillRule = ProGPU.Vector.FillRule;

namespace Avalonia.ProGpu.UnitTests
{
    public class StreamGeometryImplTests
    {
        [Fact]
        public void Defaults_To_EvenOdd_FillRule()
        {
            var geometry = new StreamGeometryImpl();

            Assert.Equal(VectorFillRule.EvenOdd, geometry.Path.FillRule);
        }

        [Fact]
        public void Clone_Preserves_Arc_And_Figure_Metadata()
        {
            var geometry = CreateArcGeometry();

            var clone = Assert.IsType<StreamGeometryImpl>(geometry.Clone());
            var figure = Assert.Single(clone.Path.Figures);
            var arc = Assert.IsType<ArcSegment>(Assert.Single(figure.Segments));

            Assert.Equal(VectorFillRule.EvenOdd, clone.Path.FillRule);
            Assert.False(figure.IsFilled);
            Assert.True(figure.IsClosed);
            Assert.False(arc.IsStroked);
            Assert.False(arc.IsSmoothJoin);
            Assert.Equal(new System.Numerics.Vector2(20, 10), arc.Point);
            Assert.Equal(new System.Numerics.Vector2(10, 5), arc.Size);
            Assert.Equal(25, arc.RotationAngle);
            Assert.True(arc.IsLargeArc);
            Assert.Equal(ProGPU.Vector.SweepDirection.Clockwise, arc.SweepDirection);
        }

        [Fact]
        public void WithTransform_Preserves_Arc_And_FillRule()
        {
            var geometry = CreateArcGeometry();
            var transform = Matrix.CreateRotation(Math.PI / 6) * Matrix.CreateTranslation(8, 4);

            var transformed = Assert.IsType<TransformedGeometryImpl>(geometry.WithTransform(transform));
            var figure = Assert.Single(transformed.Path.Figures);

            Assert.Equal(VectorFillRule.EvenOdd, transformed.Path.FillRule);
            Assert.False(figure.IsFilled);
            Assert.True(figure.IsClosed);
            Assert.IsType<ArcSegment>(Assert.Single(figure.Segments));
        }

        private static StreamGeometryImpl CreateArcGeometry()
        {
            var geometry = new StreamGeometryImpl();
            using (var context = geometry.Open())
            {
                context.SetFillRule(Avalonia.Media.FillRule.EvenOdd);
                context.BeginFigure(new Point(0, 10), isFilled: false);
                context.ArcTo(
                    new Point(20, 10),
                    new Size(10, 5),
                    rotationAngle: 25,
                    isLargeArc: true,
                    Avalonia.Media.SweepDirection.Clockwise,
                    isStroked: false);
                context.EndFigure(isClosed: true);
            }

            return geometry;
        }
    }
}
