using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using System;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Numerics;
using Silk.NET.WebGPU;

namespace System.Drawing;

public class Graphics : IDisposable
{
    private readonly DrawingContext _context;
    private readonly Bitmap? _bitmap;
    private Matrix _transform = new();

    public DrawingContext DrawingContext => _context;

    public Matrix Transform
    {
        get => _transform.Clone();
        set
        {
            if (value != null)
            {
                _transform = value.Clone();
            }
        }
    }

    public SmoothingMode SmoothingMode { get; set; } = SmoothingMode.AntiAlias;
    public InterpolationMode InterpolationMode { get; set; } = InterpolationMode.Bilinear;
    public TextRenderingHint TextRenderingHint { get; set; } = TextRenderingHint.ClearTypeGridFit;
    public PixelOffsetMode PixelOffsetMode { get; set; } = PixelOffsetMode.Default;

    public float DpiX => 96f;
    public float DpiY => 96f;

    internal Graphics(DrawingContext context, Bitmap? bitmap = null)
    {
        _context = context;
        _bitmap = bitmap;
    }

    public static Graphics FromImage(Image image)
    {
        if (image is Bitmap bitmap)
        {
            return new Graphics(bitmap.RecordedContext, bitmap);
        }
        throw new NotSupportedException("Only Bitmap image type is supported.");
    }

    public static Graphics FromHwnd(IntPtr hwnd)
    {
        return new Graphics(new DrawingContext());
    }

    public void TranslateTransform(float dx, float dy)
    {
        _transform.Translate(dx, dy);
    }

    public void ScaleTransform(float sx, float sy)
    {
        _transform.Scale(sx, sy);
    }

    public void RotateTransform(float angle)
    {
        _transform.Rotate(angle);
    }

    public void MultiplyTransform(Matrix matrix, MatrixOrder order = MatrixOrder.Prepend)
    {
        if (matrix != null)
        {
            _transform.Multiply(matrix, order);
        }
    }

    public void ResetTransform()
    {
        _transform.Reset();
    }

    private Vector2 Tx(float x, float y)
    {
        return Vector2.Transform(new Vector2(x, y), _transform.Value);
    }

    private Vector2 Tx(PointF pt)
    {
        return Vector2.Transform(new Vector2(pt.X, pt.Y), _transform.Value);
    }

    private Vector2 Tx(Vector2 pt)
    {
        return Vector2.Transform(pt, _transform.Value);
    }

    private bool HasRotationOrShear => Math.Abs(_transform.Value.M12) > 1e-5f || Math.Abs(_transform.Value.M21) > 1e-5f;

    private Rect TxRect(RectangleF rect)
    {
        var p1 = Tx(rect.X, rect.Y);
        var p2 = Tx(rect.Right, rect.Bottom);
        var x1 = MathF.Min(p1.X, p2.X);
        var y1 = MathF.Min(p1.Y, p2.Y);
        var x2 = MathF.Max(p1.X, p2.X);
        var y2 = MathF.Max(p1.Y, p2.Y);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private Matrix4x4 CurrentTransform4x4()
    {
        var m32 = _transform.Value;
        return new Matrix4x4(
            m32.M11, m32.M12, 0f, 0f,
            m32.M21, m32.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            m32.M31, m32.M32, 0f, 1f);
    }

    private ProGPU.Vector.Pen TransformPen(Pen pen, Vector2 localStart, Vector2 localEnd)
    {
        float widthScale = GetStrokeWidthScale(localStart, localEnd);
        return pen.ToProGpuPen(pen.Width * widthScale);
    }

    private ProGPU.Vector.Pen TransformPen(Pen pen)
    {
        float widthScale = GetFallbackStrokeWidthScale();
        return pen.ToProGpuPen(pen.Width * widthScale);
    }

    private float GetStrokeWidthScale(Vector2 localStart, Vector2 localEnd)
    {
        var delta = localEnd - localStart;
        if (delta.LengthSquared() > 1e-10f)
        {
            var normal = Vector2.Normalize(new Vector2(-delta.Y, delta.X));
            var transformedNormal = Vector2.TransformNormal(normal, _transform.Value);
            float scale = transformedNormal.Length();
            if (float.IsFinite(scale) && scale > 1e-5f)
            {
                return scale;
            }
        }

        return GetFallbackStrokeWidthScale();
    }

    private float GetFallbackStrokeWidthScale()
    {
        float xAxis = Vector2.TransformNormal(Vector2.UnitX, _transform.Value).Length();
        float yAxis = Vector2.TransformNormal(Vector2.UnitY, _transform.Value).Length();
        float fallbackScale = (xAxis + yAxis) * 0.5f;
        return float.IsFinite(fallbackScale) && fallbackScale > 1e-5f
            ? fallbackScale
            : 1f;
    }

    public void Clear(Color color)
    {
        float w = _bitmap != null ? _bitmap.Width : 100000f;
        float h = _bitmap != null ? _bitmap.Height : 100000f;
        var brush = new SolidBrush(color);
        _context.PushBlendMode(GpuBlendMode.Src);
        _context.DrawRectangle(brush.ToProGpuBrush(), null, new Rect(0, 0, w, h));
        _context.PopBlendMode();
    }

    public void DrawLine(Pen pen, PointF p1, PointF p2) => DrawLine(pen, p1.X, p1.Y, p2.X, p2.Y);
    public void DrawLine(Pen pen, Point p1, Point p2) => DrawLine(pen, p1.X, p1.Y, p2.X, p2.Y);
    public void DrawLine(Pen pen, int x1, int y1, int x2, int y2) => DrawLine(pen, (float)x1, y1, x2, y2);

    public void DrawLine(Pen pen, float x1, float y1, float x2, float y2)
    {
        var localStart = new Vector2(x1, y1);
        var localEnd = new Vector2(x2, y2);
        _context.DrawLine(TransformPen(pen, localStart, localEnd), Tx(localStart), Tx(localEnd));
    }

    public void DrawLines(Pen pen, PointF[] points)
    {
        if (points == null || points.Length < 2) return;
        for (int i = 0; i < points.Length - 1; i++)
        {
            DrawLine(pen, points[i], points[i + 1]);
        }
    }

    public void DrawLines(Pen pen, Point[] points)
    {
        if (points == null || points.Length < 2) return;
        for (int i = 0; i < points.Length - 1; i++)
        {
            DrawLine(pen, points[i], points[i + 1]);
        }
    }

    public void DrawRectangle(Pen pen, Rectangle rect) => DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    public void DrawRectangle(Pen pen, int x, int y, int width, int height) => DrawRectangle(pen, (float)x, y, width, height);

    public void DrawRectangle(Pen pen, float x, float y, float width, float height)
    {
        if (HasRotationOrShear)
        {
            using var path = new GraphicsPath();
            path.AddRectangle(new RectangleF(x, y, width, height));
            DrawPath(pen, path);
        }
        else
        {
            var rect = TxRect(new RectangleF(x, y, width, height));
            _context.DrawRectangle(null, TransformPen(pen), rect);
        }
    }

    public void DrawRectangles(Pen pen, Rectangle[] rects)
    {
        foreach (var r in rects) DrawRectangle(pen, r);
    }

    public void DrawRectangles(Pen pen, RectangleF[] rects)
    {
        foreach (var r in rects) DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
    }

    public void FillRectangle(Brush brush, Rectangle rect) => FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);
    public void FillRectangle(Brush brush, int x, int y, int width, int height) => FillRectangle(brush, (float)x, y, width, height);

    public void FillRectangle(Brush brush, float x, float y, float width, float height)
    {
        if (brush is TextureBrush textureBrush)
        {
            FillTextureRectangle(textureBrush, new RectangleF(x, y, width, height));
            return;
        }

        if (HasRotationOrShear)
        {
            using var path = new GraphicsPath();
            path.AddRectangle(new RectangleF(x, y, width, height));
            FillPath(brush, path);
        }
        else
        {
            var rect = TxRect(new RectangleF(x, y, width, height));
            _context.DrawRectangle(brush.ToProGpuBrush(), null, rect);
        }
    }

    private void FillTextureRectangle(TextureBrush brush, RectangleF rect)
    {
        if (rect.Width <= 0f || rect.Height <= 0f)
        {
            return;
        }

        if (brush.Image is not Bitmap bitmap)
        {
            throw new NotSupportedException("Only bitmap-backed TextureBrush fills are supported.");
        }

        var tileWidth = bitmap.Width;
        var tileHeight = bitmap.Height;
        if (tileWidth <= 0 || tileHeight <= 0)
        {
            return;
        }

        var retainedTexture = RetainBitmapTexture(bitmap);
        var transform = CurrentTransform4x4();
        var right = rect.Right;
        var bottom = rect.Bottom;
        var startX = MathF.Floor(rect.X / tileWidth) * tileWidth;
        var startY = MathF.Floor(rect.Y / tileHeight) * tileHeight;

        for (var tileY = startY; tileY < bottom; tileY += tileHeight)
        {
            var destY = MathF.Max(tileY, rect.Y);
            var destBottom = MathF.Min(tileY + tileHeight, bottom);
            var destHeight = destBottom - destY;
            if (destHeight <= 0f)
            {
                continue;
            }

            for (var tileX = startX; tileX < right; tileX += tileWidth)
            {
                var destX = MathF.Max(tileX, rect.X);
                var destRight = MathF.Min(tileX + tileWidth, right);
                var destWidth = destRight - destX;
                if (destWidth <= 0f)
                {
                    continue;
                }

                _context.Commands.Add(new RenderCommand
                {
                    Type = RenderCommandType.DrawTexture,
                    Texture = retainedTexture,
                    Rect = new Rect(destX, destY, destWidth, destHeight),
                    SrcRect = new Rect(destX - tileX, destY - tileY, destWidth, destHeight),
                    Transform = transform,
                    TextureSamplingMode = TextureSamplingMode.Linear
                });
            }
        }
    }

    public void FillRectangles(Brush brush, Rectangle[] rects)
    {
        foreach (var r in rects) FillRectangle(brush, r);
    }

    public void FillRectangles(Brush brush, RectangleF[] rects)
    {
        foreach (var r in rects) FillRectangle(brush, r.X, r.Y, r.Width, r.Height);
    }

    public void DrawEllipse(Pen pen, Rectangle rect) => DrawEllipse(pen, rect.X, rect.Y, rect.Width, rect.Height);
    public void DrawEllipse(Pen pen, RectangleF rect) => DrawEllipse(pen, rect.X, rect.Y, rect.Width, rect.Height);
    public void DrawEllipse(Pen pen, int x, int y, int width, int height) => DrawEllipse(pen, (float)x, y, width, height);

    public void DrawEllipse(Pen pen, float x, float y, float width, float height)
    {
        if (HasRotationOrShear)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(x, y, width, height);
            DrawPath(pen, path);
        }
        else
        {
            float rx = width / 2f;
            float ry = height / 2f;
            var center = Tx(x + rx, y + ry);
            var scale = new Vector2(
                Vector2.TransformNormal(Vector2.UnitX, _transform.Value).Length(),
                Vector2.TransformNormal(Vector2.UnitY, _transform.Value).Length()
            );
            _context.DrawEllipse(null, TransformPen(pen), center, rx * scale.X, ry * scale.Y);
        }
    }

    public void FillEllipse(Brush brush, Rectangle rect) => FillEllipse(brush, rect.X, rect.Y, rect.Width, rect.Height);
    public void FillEllipse(Brush brush, RectangleF rect) => FillEllipse(brush, rect.X, rect.Y, rect.Width, rect.Height);
    public void FillEllipse(Brush brush, int x, int y, int width, int height) => FillEllipse(brush, (float)x, y, width, height);

    public void FillEllipse(Brush brush, float x, float y, float width, float height)
    {
        if (HasRotationOrShear)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(x, y, width, height);
            FillPath(brush, path);
        }
        else
        {
            float rx = width / 2f;
            float ry = height / 2f;
            var center = Tx(x + rx, y + ry);
            var scale = new Vector2(
                Vector2.TransformNormal(Vector2.UnitX, _transform.Value).Length(),
                Vector2.TransformNormal(Vector2.UnitY, _transform.Value).Length()
            );
            _context.DrawEllipse(brush.ToProGpuBrush(), null, center, rx * scale.X, ry * scale.Y);
        }
    }

    public void DrawPolygon(Pen pen, PointF[] points)
    {
        if (points == null || points.Length < 2) return;
        using var path = new GraphicsPath();
        path.AddPolygon(points);
        DrawPath(pen, path);
    }

    public void DrawPolygon(Pen pen, Point[] points)
    {
        if (points == null || points.Length < 2) return;
        using var path = new GraphicsPath();
        path.AddPolygon(points);
        DrawPath(pen, path);
    }

    public void FillPolygon(Brush brush, PointF[] points)
    {
        if (points == null || points.Length < 2) return;
        using var path = new GraphicsPath();
        path.AddPolygon(points);
        FillPath(brush, path);
    }

    public void FillPolygon(Brush brush, Point[] points)
    {
        if (points == null || points.Length < 2) return;
        using var path = new GraphicsPath();
        path.AddPolygon(points);
        FillPath(brush, path);
    }

    public void DrawPath(Pen pen, GraphicsPath path)
    {
        if (path == null) return;
        _context.DrawPath(null, TransformPen(pen), path.Geometry, CurrentTransform4x4());
    }

    public void FillPath(Brush brush, GraphicsPath path)
    {
        if (path == null) return;
        _context.DrawPath(brush.ToProGpuBrush(), null, path.Geometry, CurrentTransform4x4());
    }

    public void DrawString(string s, Font font, Brush brush, PointF point) => DrawString(s, font, brush, point.X, point.Y);
    public void DrawString(string s, Font font, Brush brush, float x, float y)
    {
        var isBold = (font.Style & FontStyle.Bold) != 0;
        var isItalic = (font.Style & FontStyle.Italic) != 0;
        _context.DrawText(
            s,
            font.TtfFont,
            GetFontPixelSize(font),
            brush.ToProGpuBrush(),
            new Vector2(x, y),
            CurrentTransform4x4(),
            isBold,
            isItalic);
    }

    public void DrawString(string s, Font font, Brush brush, RectangleF layoutRectangle)
    {
        DrawString(s, font, brush, layoutRectangle.X, layoutRectangle.Y);
    }

    public SizeF MeasureString(string text, Font font)
    {
        var layout = new ProGPU.Text.TextLayout(text, font.TtfFont, GetFontPixelSize(font));
        return new SizeF(layout.MeasuredSize.X, layout.MeasuredSize.Y);
    }

    public SizeF MeasureString(string text, Font font, SizeF layoutArea)
    {
        return MeasureString(text, font);
    }

    public SizeF MeasureString(string text, Font font, int width)
    {
        return MeasureString(text, font, new SizeF(width, float.MaxValue));
    }

    private float GetFontPixelSize(Font font)
    {
        return ConvertFontSizeToPixels(font.Size, font.Unit, DpiY);
    }

    internal static float ConvertFontSizeToPixels(float size, GraphicsUnit unit, float dpi)
    {
        return unit switch
        {
            GraphicsUnit.Point => size * dpi / 72f,
            GraphicsUnit.Inch => size * dpi,
            GraphicsUnit.Document => size * dpi / 300f,
            GraphicsUnit.Millimeter => size * dpi / 25.4f,
            _ => size
        };
    }

    internal static float ConvertFontSizeToPoints(float size, GraphicsUnit unit, float dpi)
    {
        return unit switch
        {
            GraphicsUnit.Pixel or GraphicsUnit.Display or GraphicsUnit.World => size * 72f / dpi,
            GraphicsUnit.Inch => size * 72f,
            GraphicsUnit.Document => size * 72f / 300f,
            GraphicsUnit.Millimeter => size * 72f / 25.4f,
            _ => size
        };
    }

    public void DrawImage(Image image, PointF point) => DrawImage(image, point.X, point.Y);
    public void DrawImage(Image image, float x, float y)
    {
        if (image is Bitmap bmp)
        {
            DrawBitmap(bmp, new RectangleF(x, y, bmp.Width, bmp.Height));
        }
    }

    public void DrawImage(Image image, int x, int y, int width, int height)
    {
        DrawImage(image, new Rectangle(x, y, width, height));
    }

    public void DrawImage(Image image, float x, float y, float width, float height)
    {
        DrawImage(image, new RectangleF(x, y, width, height));
    }

    public void DrawImage(Image image, RectangleF rect)
    {
        if (image is Bitmap bmp)
        {
            DrawBitmap(bmp, rect, default, null);
        }
    }

    public void DrawImage(Image image, Rectangle rect) => DrawImage(image, (RectangleF)rect);

    public void DrawImage(Image image, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit)
    {
        DrawImage(image, (RectangleF)destRect, (RectangleF)srcRect, srcUnit);
    }

    public void DrawImage(Image image, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit)
    {
        if (image is Bitmap bmp)
        {
            DrawBitmap(bmp, destRect, ConvertSourceRect(srcRect, srcUnit), null);
        }
    }

    public void DrawIcon(Icon icon, Rectangle targetRect)
    {
        ArgumentNullException.ThrowIfNull(icon);
        using var bitmap = icon.ToBitmap();
        DrawImage(bitmap, targetRect);
    }

    public void DrawImageUnscaled(Image image, int x, int y)
    {
        DrawImage(image, x, y);
    }

    public void DrawImageUnscaled(Image image, Point point)
    {
        DrawImageUnscaled(image, point.X, point.Y);
    }

    public void DrawImage(
        Image image,
        Rectangle destRect,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr)
    {
        if (image is Bitmap bmp)
        {
            var srcRect = ConvertSourceRect(
                new RectangleF(srcX, srcY, srcWidth, srcHeight),
                srcUnit);
            DrawBitmap(bmp, destRect, srcRect, imageAttr);
        }
    }

    private void DrawBitmap(Bitmap bitmap, RectangleF rect)
    {
        DrawBitmap(bitmap, rect, default, null);
    }

    private void DrawBitmap(Bitmap bitmap, RectangleF rect, RectangleF sourceRect, ImageAttributes? imageAttributes)
    {
        var retainedTexture = RetainBitmapTexture(bitmap);
        var srcRect = sourceRect.Width > 0f && sourceRect.Height > 0f
            ? new Rect(sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height)
            : Rect.Empty;

        var colorMatrix = TryCreateImageEffectColorMatrix(imageAttributes?.ColorMatrix);
        if (colorMatrix.HasValue)
        {
            _context.DrawImageWithEffect(
                retainedTexture,
                new Rect(rect.X, rect.Y, rect.Width, rect.Height),
                sourceRect: srcRect,
                samplingMode: GetTextureSamplingMode(),
                colorMatrix: colorMatrix,
                transform: CurrentTransform4x4());
            return;
        }

        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Texture = retainedTexture,
            Rect = new Rect(rect.X, rect.Y, rect.Width, rect.Height),
            SrcRect = srcRect,
            Transform = CurrentTransform4x4(),
            TextureSamplingMode = GetTextureSamplingMode()
        });
    }

    private RectangleF ConvertSourceRect(RectangleF sourceRect, GraphicsUnit unit)
    {
        if (unit == GraphicsUnit.Pixel || unit == GraphicsUnit.Display || unit == GraphicsUnit.World)
        {
            return sourceRect;
        }

        var scaleX = UnitToPixelScale(unit, DpiX);
        var scaleY = UnitToPixelScale(unit, DpiY);
        return new RectangleF(
            sourceRect.X * scaleX,
            sourceRect.Y * scaleY,
            sourceRect.Width * scaleX,
            sourceRect.Height * scaleY);
    }

    private static float UnitToPixelScale(GraphicsUnit unit, float dpi)
    {
        return unit switch
        {
            GraphicsUnit.Point => dpi / 72f,
            GraphicsUnit.Inch => dpi,
            GraphicsUnit.Document => dpi / 300f,
            GraphicsUnit.Millimeter => dpi / 25.4f,
            _ => 1f
        };
    }

    private static ImageEffectColorMatrix? TryCreateImageEffectColorMatrix(ColorMatrix? colorMatrix)
    {
        if (colorMatrix == null)
        {
            return null;
        }

        var matrix = colorMatrix.Matrix;
        return new ImageEffectColorMatrix(
            new Vector4(Read(matrix, 0, 0), Read(matrix, 1, 0), Read(matrix, 2, 0), Read(matrix, 3, 0)),
            new Vector4(Read(matrix, 0, 1), Read(matrix, 1, 1), Read(matrix, 2, 1), Read(matrix, 3, 1)),
            new Vector4(Read(matrix, 0, 2), Read(matrix, 1, 2), Read(matrix, 2, 2), Read(matrix, 3, 2)),
            new Vector4(Read(matrix, 0, 3), Read(matrix, 1, 3), Read(matrix, 2, 3), Read(matrix, 3, 3)),
            new Vector4(Read(matrix, 4, 0), Read(matrix, 4, 1), Read(matrix, 4, 2), Read(matrix, 4, 3)));
    }

    private static float Read(float[][] matrix, int row, int column)
    {
        if ((uint)row >= (uint)matrix.Length)
        {
            return 0f;
        }

        var rowValues = matrix[row];
        if (rowValues == null || (uint)column >= (uint)rowValues.Length)
        {
            return 0f;
        }

        return rowValues[column];
    }

    private TextureSamplingMode GetTextureSamplingMode()
    {
        return InterpolationMode switch
        {
            InterpolationMode.NearestNeighbor => TextureSamplingMode.Nearest,
            InterpolationMode.Bicubic or InterpolationMode.HighQualityBicubic => TextureSamplingMode.Cubic,
            _ => TextureSamplingMode.Linear
        };
    }

    private GpuTexture RetainBitmapTexture(Bitmap bitmap)
    {
        bitmap.Flush();
        var source = bitmap.GpuTexture;
        var targetContext = _bitmap?.GpuTexture.Context ?? GpuProvider.Context;
        if (!ReferenceEquals(source.Context, targetContext))
        {
            throw new InvalidOperationException("Cannot draw a GDI Bitmap from a different WebGPU context into this Graphics target.");
        }

        var retainedTexture = new GpuTexture(
            targetContext,
            source.Width,
            source.Height,
            source.Format,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "GDI DrawImage Retained Source Texture",
            alphaMode: source.AlphaMode);
        retainedTexture.CopyFrom(source);
        _context.RetainResource(retainedTexture);
        return retainedTexture;
    }

    public void Dispose()
    {
        _transform.Dispose();
        _bitmap?.Flush();
    }
}
