using Xunit;

namespace Avalonia.ProGpu.UnitTests
{
    public class BitmapGlyphCacheTests
    {
        [Fact]
        public void SbixOriginOffsetsPlaceBitmapRelativeToBaseline()
        {
            var metrics = new BitmapGlyphMetrics(
                pixelsPerEm: 20,
                pixelsPerInch: 72,
                originOffsetX: 2,
                originOffsetY: 5,
                pixelWidth: 20,
                pixelHeight: 18);

            var bounds = metrics.GetBounds(new Point(100, 50), emSize: 40);

            Assert.Equal(new Rect(96, 24, 40, 36), bounds);
        }
    }
}
