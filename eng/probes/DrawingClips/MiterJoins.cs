using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Security.Cryptography;

static class MiterJoins
{
    private const int Size = 64;
    private const int QuerySide = 16;

    // Authored, unexecuted separate-process controls. Microsoft supplies every
    // expected result; no Direct2D transcript or guessed GDI+ corner is an oracle.
    // https://learn.microsoft.com/en-us/windows/win32/api/gdiplusenums/ne-gdiplusenums-linejoin
    public static List<object> Capture(string output)
    {
        var result = new List<object>();
        foreach (string shape in new[] { "rectangle", "acute" })
        foreach (LineJoin join in new[] { LineJoin.Miter, LineJoin.MiterClipped })
        foreach (int limit in new[] { 1, 2, 10 })
        {
            string name = $"{shape}-{join}-{limit}";
            var elapsed = Stopwatch.StartNew();
            Console.WriteLine($"Drawing stroke join {name}: begin");
            using var source = new GraphicsPath();
            if (shape == "rectangle")
                source.AddRectangle(new RectangleF(16, 20, 32, 24));
            else
                source.AddLines(new PointF[] { new(20, 48), new(32, 24), new(44, 48) });
            using var pen = new Pen(Color.FromArgb(160, 36, 80, 184), 8)
            {
                LineJoin = join,
                MiterLimit = limit,
                StartCap = LineCap.Flat,
                EndCap = LineCap.Flat,
                DashStyle = DashStyle.Solid,
                Alignment = PenAlignment.Center
            };
            using Matrix penTransform = pen.Transform;
            var penState = new
            {
                ColorArgb = pen.Color.ToArgb(), pen.Width,
                LineJoin = (int)pen.LineJoin, pen.MiterLimit,
                StartCap = (int)pen.StartCap, EndCap = (int)pen.EndCap,
                DashStyle = (int)pen.DashStyle, Alignment = (int)pen.Alignment,
                Transform = Scalars(penTransform.Elements)
            };
            object sourceBefore = PathSnapshot(source);
            object strokeBounds = Bounds(source.GetBounds(null, pen));
            using var widened = (GraphicsPath)source.Clone();
            widened.Widen(pen);
            object widenedBounds = Bounds(widened.GetBounds());
            // Preserve the original representation diagnostically. Different
            // contour decompositions do not imply different stroke coverage.
            object widenedPath = PathSnapshot(widened);
            bool[] outlineVisible = new bool[QuerySide * QuerySide];
            bool[] widenedVisible = new bool[outlineVisible.Length];
            float[] queryPoints = new float[outlineVisible.Length * 2];
            using var drawn = CreateBitmap();
            using var filled = CreateBitmap();
            object drawState;
            object fillState;
            using (Graphics graphics = Graphics.FromImage(drawn))
            {
                Configure(graphics);
                drawState = GraphicsSnapshot(graphics);
                for (int y = 0; y < QuerySide; y++)
                for (int x = 0; x < QuerySide; x++)
                {
                    int index = y * QuerySide + x;
                    float pointX = x * 4 + 2.5f;
                    float pointY = y * 4 + 2.5f;
                    queryPoints[index * 2] = pointX;
                    queryPoints[index * 2 + 1] = pointY;
                    outlineVisible[index] = source.IsOutlineVisible(pointX, pointY, pen, graphics);
                    widenedVisible[index] = widened.IsVisible(pointX, pointY, graphics);
                }
                graphics.DrawPath(pen, source);
            }
            using (Graphics graphics = Graphics.FromImage(filled))
            {
                Configure(graphics);
                fillState = GraphicsSnapshot(graphics);
                using var brush = new SolidBrush(pen.Color);
                graphics.FillPath(brush, widened);
            }
            string drawHash = CapturePixels(drawn, output, name, "draw");
            string fillHash = CapturePixels(filled, output, name, "widen");
            result.Add(new
            {
                Name = name, Width = Size, Height = Size,
                Pen = penState, SourceBefore = sourceBefore,
                SourceAfter = PathSnapshot(source), StrokeBounds = strokeBounds,
                WidenedBounds = widenedBounds, WidenedPath = widenedPath,
                QueryPoints = queryPoints, OutlineVisible = outlineVisible,
                WidenedVisible = widenedVisible, DrawState = drawState, FillState = fillState,
                DrawPixelsSha256 = drawHash, WidenPixelsSha256 = fillHash
            });
            Console.WriteLine($"Drawing stroke join {name}: completed at {elapsed.ElapsedMilliseconds} ms");
        }
        return result;
    }

    private static Bitmap CreateBitmap()
    {
        var bitmap = new Bitmap(Size, Size);
        bitmap.SetResolution(96, 96);
        return bitmap;
    }

    private static void Configure(Graphics graphics)
    {
        graphics.Clear(Color.White);
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.PageScale = 1;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.None;
        graphics.CompositingMode = CompositingMode.SourceOver;
    }

    private static object GraphicsSnapshot(Graphics graphics)
    {
        using Matrix transform = graphics.Transform;
        return new
        {
            graphics.DpiX, graphics.DpiY,
            PageUnit = (int)graphics.PageUnit, graphics.PageScale,
            SmoothingMode = (int)graphics.SmoothingMode,
            PixelOffsetMode = (int)graphics.PixelOffsetMode,
            CompositingMode = (int)graphics.CompositingMode,
            Transform = Scalars(transform.Elements)
        };
    }

    private static object PathSnapshot(GraphicsPath path)
    {
        PointF[] points = path.PathPoints;
        float[] coordinates = new float[points.Length * 2];
        for (int index = 0; index < points.Length; index++)
        {
            coordinates[index * 2] = points[index].X;
            coordinates[index * 2 + 1] = points[index].Y;
        }
        return new
        {
            FillMode = (int)path.FillMode, path.PointCount,
            Points = Scalars(coordinates), Types = path.PathTypes
        };
    }

    private static object Bounds(RectangleF bounds) =>
        Scalars([bounds.X, bounds.Y, bounds.Width, bounds.Height]);

    private static object Scalars(float[] values) => new
    {
        Values = values, Bits = Array.ConvertAll(values, BitConverter.SingleToInt32Bits)
    };

    private static string CapturePixels(Bitmap bitmap, string output, string name, string route)
    {
        byte[] pixels = new byte[Size * Size * 4];
        int ink = 0;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            Color pixel = bitmap.GetPixel(x, y);
            int index = (y * Size + x) * 4;
            pixels[index] = pixel.R;
            pixels[index + 1] = pixel.G;
            pixels[index + 2] = pixel.B;
            pixels[index + 3] = pixel.A;
            if (pixel.ToArgb() != Color.White.ToArgb()) ink++;
        }
        File.WriteAllBytes(output + $".stroke-joins.{name}.{route}.rgba", pixels);
        if (ink == 0)
            throw new InvalidOperationException($"Drawing stroke join {name}/{route} produced no ink.");
        return Convert.ToHexString(SHA256.HashData(pixels));
    }
}
