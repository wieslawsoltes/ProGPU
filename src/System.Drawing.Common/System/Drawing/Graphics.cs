using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Numerics;
using System.Text;

namespace System.Drawing;

public class Graphics : IDisposable
{
    private readonly DrawingContext _context;
    private readonly Bitmap? _bitmap;
    // Device/host state is immutable; public Transform APIs mutate only _transform.
    private readonly Matrix3x2 _baseTransform;
    private Matrix _transform = new();
    private readonly List<SavedGraphicsState> _savedStates = new();
    private int _nextStateId;
    private float _pageScale = 1f;
    private GraphicsUnit _pageUnit = GraphicsUnit.Display;
    private CompositingQuality _compositingQuality = CompositingQuality.Default;

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

    public float PageScale
    {
        get => _pageScale;
        set
        {
            if (value <= 0f || value > 1_000_000_032f || float.IsInfinity(value))
            {
                throw new ArgumentException("Page scale is outside the supported GDI+ range.", nameof(value));
            }

            _pageScale = value;
        }
    }

    public GraphicsUnit PageUnit
    {
        get => _pageUnit;
        set
        {
            if (value < GraphicsUnit.World || value > GraphicsUnit.Millimeter)
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(GraphicsUnit));
            }

            if (value == GraphicsUnit.World)
            {
                throw new ArgumentException("GraphicsUnit.World is not a valid page unit.", nameof(value));
            }

            _pageUnit = value;
        }
    }

    public CompositingQuality CompositingQuality
    {
        get => _compositingQuality;
        set
        {
            if (value < CompositingQuality.Invalid || value > CompositingQuality.AssumeLinear)
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(CompositingQuality));
            }

            _compositingQuality = value;
        }
    }

    public float DpiX => 96f;
    public float DpiY => 96f;

    public RectangleF VisibleClipBounds
    {
        get
        {
            if (_bitmap == null)
            {
                return RectangleF.Empty;
            }

            if (!Matrix3x2.Invert(CombinedTransform, out Matrix3x2 deviceToWorld))
            {
                return RectangleF.Empty;
            }

            Vector2 topLeft = Vector2.Transform(Vector2.Zero, deviceToWorld);
            Vector2 topRight = Vector2.Transform(new Vector2(_bitmap.Width, 0f), deviceToWorld);
            Vector2 bottomLeft = Vector2.Transform(new Vector2(0f, _bitmap.Height), deviceToWorld);
            Vector2 bottomRight = Vector2.Transform(new Vector2(_bitmap.Width, _bitmap.Height), deviceToWorld);
            float left = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomLeft.X, bottomRight.X));
            float top = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomLeft.Y, bottomRight.Y));
            float right = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomLeft.X, bottomRight.X));
            float bottom = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomLeft.Y, bottomRight.Y));

            return new RectangleF(left, top, right - left, bottom - top);
        }
    }

    internal Graphics(DrawingContext context, Bitmap? bitmap = null)
        : this(context, bitmap, Matrix3x2.Identity)
    {
    }

    private Graphics(DrawingContext context, Bitmap? bitmap, Matrix3x2 baseTransform)
    {
        _context = context;
        _bitmap = bitmap;
        _baseTransform = baseTransform;
    }

    public static Graphics FromProGpuDrawingContext(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        return new Graphics(drawingContext);
    }

    public static Graphics FromProGpuDrawingContext(
        DrawingContext drawingContext,
        Matrix4x4 outerTransform)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (!IsFinite2DAffineTransform(outerTransform))
        {
            throw new ArgumentException(
                "The native drawing-context transform must be a finite 2D affine matrix.",
                nameof(outerTransform));
        }

        return new Graphics(drawingContext, bitmap: null, baseTransform: new Matrix3x2(
            outerTransform.M11,
            outerTransform.M12,
            outerTransform.M21,
            outerTransform.M22,
            outerTransform.M41,
            outerTransform.M42));
    }

    private static bool IsFinite2DAffineTransform(Matrix4x4 transform)
    {
        const float epsilon = 0.00001f;
        return float.IsFinite(transform.M11)
            && float.IsFinite(transform.M12)
            && float.IsFinite(transform.M21)
            && float.IsFinite(transform.M22)
            && float.IsFinite(transform.M41)
            && float.IsFinite(transform.M42)
            && MathF.Abs(transform.M13) <= epsilon
            && MathF.Abs(transform.M14) <= epsilon
            && MathF.Abs(transform.M23) <= epsilon
            && MathF.Abs(transform.M24) <= epsilon
            && MathF.Abs(transform.M31) <= epsilon
            && MathF.Abs(transform.M32) <= epsilon
            && MathF.Abs(transform.M34) <= epsilon
            && MathF.Abs(transform.M43) <= epsilon
            && MathF.Abs(transform.M33 - 1f) <= epsilon
            && MathF.Abs(transform.M44 - 1f) <= epsilon;
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

    private Matrix3x2 CombinedTransform => _transform.Value * GetPageTransform() * _baseTransform;

    private Matrix3x2 GetPageTransform()
    {
        float unitScaleX = UnitToPixelScale(PageUnit, DpiX);
        float unitScaleY = UnitToPixelScale(PageUnit, DpiY);
        return Matrix3x2.CreateScale(unitScaleX * PageScale, unitScaleY * PageScale);
    }

    public GraphicsState Save()
    {
        var state = new GraphicsState(++_nextStateId);
        _savedStates.Add(new SavedGraphicsState(
            state,
            _transform.Value,
            SmoothingMode,
            InterpolationMode,
            TextRenderingHint,
            PixelOffsetMode,
            PageScale,
            PageUnit,
            CompositingQuality));
        return state;
    }

    public void Restore(GraphicsState gstate)
    {
        ArgumentNullException.ThrowIfNull(gstate);

        int stateIndex = -1;
        for (int i = _savedStates.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_savedStates[i].State, gstate))
            {
                stateIndex = i;
                break;
            }
        }

        if (stateIndex < 0)
        {
            throw new ArgumentException("The graphics state does not belong to this Graphics instance or has already been restored.", nameof(gstate));
        }

        SavedGraphicsState saved = _savedStates[stateIndex];
        _transform.Dispose();
        _transform = new Matrix(saved.Transform);
        SmoothingMode = saved.SmoothingMode;
        InterpolationMode = saved.InterpolationMode;
        TextRenderingHint = saved.TextRenderingHint;
        PixelOffsetMode = saved.PixelOffsetMode;
        _pageScale = saved.PageScale;
        _pageUnit = saved.PageUnit;
        _compositingQuality = saved.CompositingQuality;

        _savedStates.RemoveRange(stateIndex, _savedStates.Count - stateIndex);
    }

    private readonly record struct SavedGraphicsState(
        GraphicsState State,
        Matrix3x2 Transform,
        SmoothingMode SmoothingMode,
        InterpolationMode InterpolationMode,
        TextRenderingHint TextRenderingHint,
        PixelOffsetMode PixelOffsetMode,
        float PageScale,
        GraphicsUnit PageUnit,
        CompositingQuality CompositingQuality);

    private Vector2 Tx(float x, float y)
    {
        return Vector2.Transform(new Vector2(x, y), CombinedTransform);
    }

    private Vector2 Tx(PointF pt)
    {
        return Vector2.Transform(new Vector2(pt.X, pt.Y), CombinedTransform);
    }

    private Vector2 Tx(Vector2 pt)
    {
        return Vector2.Transform(pt, CombinedTransform);
    }

    private bool HasRotationOrShear =>
        Math.Abs(CombinedTransform.M12) > 1e-5f
        || Math.Abs(CombinedTransform.M21) > 1e-5f;

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
        var m32 = CombinedTransform;
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
            var transformedNormal = Vector2.TransformNormal(normal, CombinedTransform);
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
        float xAxis = Vector2.TransformNormal(Vector2.UnitX, CombinedTransform).Length();
        float yAxis = Vector2.TransformNormal(Vector2.UnitY, CombinedTransform).Length();
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
            var nativePen = TransformPen(pen);
            float roundedThickness = MathF.Round(nativePen.Thickness);
            if (roundedThickness > 0f
                && MathF.Abs(nativePen.Thickness - roundedThickness) <= 1e-5f
                && ((int)roundedThickness & 1) != 0)
            {
                // The vector shader samples at pixel centers. Align odd-width
                // GDI strokes to those centers so a one-pixel focus rectangle
                // covers its declared integer boundary instead of falling
                // exactly between adjacent samples.
                rect = new Rect(rect.X + 0.5f, rect.Y + 0.5f, rect.Width, rect.Height);
            }

            _context.DrawRectangle(null, nativePen, rect);
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
                Vector2.TransformNormal(Vector2.UnitX, CombinedTransform).Length(),
                Vector2.TransformNormal(Vector2.UnitY, CombinedTransform).Length()
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
                Vector2.TransformNormal(Vector2.UnitX, CombinedTransform).Length(),
                Vector2.TransformNormal(Vector2.UnitY, CombinedTransform).Length()
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
    public void DrawString(string s, Font font, Brush brush, PointF point, StringFormat? format) =>
        DrawString(s, font, brush, point.X, point.Y, format);

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

    public void DrawString(string s, Font font, Brush brush, float x, float y, StringFormat? format)
    {
        if (format == null)
        {
            DrawString(s, font, brush, x, y);
            return;
        }

        DrawFormattedString(
            s,
            font,
            brush,
            new RectangleF(x, y, float.PositiveInfinity, float.PositiveInfinity),
            format,
            pointAnchor: true);
    }

    public void DrawString(string s, Font font, Brush brush, RectangleF layoutRectangle)
    {
        if (layoutRectangle.Width <= 0f
            || layoutRectangle.Height <= 0f
            || !float.IsFinite(layoutRectangle.Width)
            || !float.IsFinite(layoutRectangle.Height))
        {
            DrawString(s, font, brush, layoutRectangle.X, layoutRectangle.Y);
            return;
        }

        var isBold = (font.Style & FontStyle.Bold) != 0;
        var isItalic = (font.Style & FontStyle.Italic) != 0;
        var layoutBounds = new Rect(
            layoutRectangle.X,
            layoutRectangle.Y,
            layoutRectangle.Width,
            layoutRectangle.Height);
        var transform = CurrentTransform4x4();

        _context.PushClip(layoutBounds, transform);
        _context.DrawText(
            s,
            font.TtfFont,
            GetFontPixelSize(font),
            brush.ToProGpuBrush(),
            new Vector2(layoutRectangle.X, layoutRectangle.Y),
            transform,
            layoutBounds,
            isBold,
            isItalic);
        _context.PopClip();
    }

    public void DrawString(string s, Font font, Brush brush, Rectangle layoutRectangle, StringFormat? format) =>
        DrawString(s, font, brush, (RectangleF)layoutRectangle, format);

    public void DrawString(string s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat? format)
    {
        if (format == null)
        {
            DrawString(s, font, brush, layoutRectangle);
            return;
        }

        if (layoutRectangle.Width <= 0f
            || layoutRectangle.Height <= 0f
            || !float.IsFinite(layoutRectangle.Width)
            || !float.IsFinite(layoutRectangle.Height))
        {
            DrawString(s, font, brush, layoutRectangle.X, layoutRectangle.Y, format);
            return;
        }

        DrawFormattedString(s, font, brush, layoutRectangle, format, pointAnchor: false);
    }

    public SizeF MeasureString(string text, Font font)
    {
        var layout = new ProGPU.Text.TextLayout(text, font.TtfFont, GetFontPixelSize(font));
        return new SizeF(layout.MeasuredSize.X, layout.MeasuredSize.Y);
    }

    public SizeF MeasureString(string text, Font font, SizeF layoutArea)
    {
        float maxWidth = layoutArea.Width > 0f && float.IsFinite(layoutArea.Width)
            ? layoutArea.Width
            : float.PositiveInfinity;
        var layout = new ProGPU.Text.TextLayout(
            text,
            font.TtfFont,
            GetFontPixelSize(font),
            maxWidth);

        float measuredWidth = layout.ContentSize.X;
        float measuredHeight = layout.ContentSize.Y;
        if (float.IsFinite(maxWidth))
        {
            measuredWidth = MathF.Min(measuredWidth, maxWidth);
        }

        if (layoutArea.Height > 0f && float.IsFinite(layoutArea.Height))
        {
            measuredHeight = MathF.Min(measuredHeight, layoutArea.Height);
        }

        return new SizeF(measuredWidth, measuredHeight);
    }

    public SizeF MeasureString(string text, Font font, SizeF layoutArea, StringFormat? stringFormat)
    {
        return MeasureString(text, font, layoutArea, stringFormat, out _, out _);
    }

    public SizeF MeasureString(
        string text,
        Font font,
        SizeF layoutArea,
        StringFormat? stringFormat,
        out int charactersFitted,
        out int linesFilled)
    {
        if (stringFormat == null)
        {
            SizeF measured = MeasureString(text, font, layoutArea);
            charactersFitted = text?.Length ?? 0;
            linesFilled = CountLines(text);
            return measured;
        }

        FormattedTextLayout formatted = CreateFormattedTextLayout(text, font, layoutArea, stringFormat);
        charactersFitted = formatted.CharactersFitted;
        linesFilled = formatted.LinesFilled;

        float measuredWidth = formatted.Layout.ContentSize.X;
        float measuredHeight = formatted.Layout.ContentSize.Y;
        if (layoutArea.Width > 0f && float.IsFinite(layoutArea.Width))
        {
            measuredWidth = MathF.Min(measuredWidth, layoutArea.Width);
        }

        if (layoutArea.Height > 0f && float.IsFinite(layoutArea.Height))
        {
            measuredHeight = MathF.Min(measuredHeight, layoutArea.Height);
        }

        return new SizeF(measuredWidth, measuredHeight);
    }

    public SizeF MeasureString(string text, Font font, int width)
    {
        return MeasureString(text, font, new SizeF(width, float.MaxValue));
    }

    public SizeF MeasureString(string text, Font font, int width, StringFormat? format)
    {
        return MeasureString(text, font, new SizeF(width, float.MaxValue), format);
    }

    private void DrawFormattedString(
        string text,
        Font font,
        Brush brush,
        RectangleF layoutRectangle,
        StringFormat format,
        bool pointAnchor)
    {
        var layoutArea = new SizeF(layoutRectangle.Width, layoutRectangle.Height);
        FormattedTextLayout formatted = CreateFormattedTextLayout(text, font, layoutArea, format);
        if (formatted.Layout.Glyphs.Count == 0)
        {
            return;
        }

        StringFormatFlags flags = format.FormatFlags;
        bool rightToLeft = (flags & StringFormatFlags.DirectionRightToLeft) != 0;
        float offsetX = pointAnchor
            ? GetPointAlignmentOffset(formatted.Layout.ContentSize.X, format.Alignment, rightToLeft)
            : GetNoWrapAlignmentOffset(formatted.Layout.ContentSize.X, layoutRectangle.Width, format.Alignment, flags);
        float offsetY = pointAnchor
            ? GetPointAlignmentOffset(formatted.Layout.ContentSize.Y, format.LineAlignment, rightToLeft: false)
            : GetRectangleAlignmentOffset(formatted.Layout.ContentSize.Y, layoutRectangle.Height, format.LineAlignment);
        var origin = new Vector2(layoutRectangle.X + offsetX, layoutRectangle.Y + offsetY);
        var transform = CurrentTransform4x4();
        bool useClip = !pointAnchor && (flags & StringFormatFlags.NoClip) == 0;
        if (useClip)
        {
            _context.PushClip(
                new Rect(layoutRectangle.X, layoutRectangle.Y, layoutRectangle.Width, layoutRectangle.Height),
                transform);
        }

        DrawFormattedGlyphRuns(formatted.Layout, font, brush, origin, transform);

        if (useClip)
        {
            _context.PopClip();
        }
    }

    private void DrawFormattedGlyphRuns(
        ProGPU.Text.TextLayout layout,
        Font font,
        Brush brush,
        Vector2 origin,
        Matrix4x4 transform)
    {
        var isBold = (font.Style & FontStyle.Bold) != 0;
        var isItalic = (font.Style & FontStyle.Italic) != 0;
        var nativeBrush = brush.ToProGpuBrush();
        GlyphRunBuilder? run = null;

        for (int i = 0; i < layout.Glyphs.Count; i++)
        {
            ProGPU.Text.TextRunGlyph glyph = layout.Glyphs[i];
            if (run == null || !ReferenceEquals(run.Font, glyph.Font))
            {
                if (run != null)
                {
                    RecordGlyphRun(run);
                }

                run = new GlyphRunBuilder(glyph.Font);
            }

            run.GlyphIndices.Add(glyph.GlyphIndex);
            run.GlyphPositions.Add(glyph.Position);
        }

        if (run != null)
        {
            RecordGlyphRun(run);
        }

        void RecordGlyphRun(GlyphRunBuilder glyphRun)
        {
            _context.DrawGlyphRun(
                glyphRun.GlyphIndices.ToArray(),
                glyphRun.GlyphPositions.ToArray(),
                glyphRun.Font,
                GetFontPixelSize(font),
                nativeBrush,
                origin,
                transform,
                isBold,
                isItalic);
        }
    }

    private FormattedTextLayout CreateFormattedTextLayout(
        string text,
        Font font,
        SizeF layoutArea,
        StringFormat format)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(format);
        format.EnsureNotDisposed();

        StringFormatFlags flags = format.FormatFlags;
        string displayText = ApplyHotkeyPrefix(text, format.HotkeyPrefix);
        float maxWidth = layoutArea.Width > 0f && float.IsFinite(layoutArea.Width)
            ? layoutArea.Width
            : float.PositiveInfinity;
        float maxHeight = layoutArea.Height > 0f && float.IsFinite(layoutArea.Height)
            ? layoutArea.Height
            : float.PositiveInfinity;
        bool noWrap = (flags & StringFormatFlags.NoWrap) != 0;
        ProGPU.Text.TextAlignment alignment = noWrap
            ? ProGPU.Text.TextAlignment.Left
            : GetTextAlignment(format.Alignment, flags);
        float textLayoutWidth = noWrap ? float.PositiveInfinity : maxWidth;
        float fontSize = GetFontPixelSize(font);
        var layout = new ProGPU.Text.TextLayout(displayText, font.TtfFont, fontSize, textLayoutWidth, alignment);

        bool exceedsWidth = float.IsFinite(maxWidth) && layout.ContentSize.X > maxWidth + 0.001f;
        bool exceedsHeight = float.IsFinite(maxHeight) && layout.ContentSize.Y > maxHeight + 0.001f;
        bool lineLimit = (flags & StringFormatFlags.LineLimit) != 0;
        int charactersFitted = text.Length;

        if ((exceedsWidth || exceedsHeight)
            && (format.Trimming != StringTrimming.None || lineLimit))
        {
            StringTrimming trimming = format.Trimming == StringTrimming.None
                ? StringTrimming.Character
                : format.Trimming;
            displayText = TrimTextToLayout(
                displayText,
                font,
                fontSize,
                maxWidth,
                maxHeight,
                noWrap,
                alignment,
                trimming,
                out charactersFitted);
            layout = new ProGPU.Text.TextLayout(displayText, font.TtfFont, fontSize, textLayoutWidth, alignment);
        }

        int linesFilled = GetLineCount(layout, font, fontSize);
        return new FormattedTextLayout(layout, Math.Min(charactersFitted, text.Length), linesFilled);
    }

    private static string TrimTextToLayout(
        string text,
        Font font,
        float fontSize,
        float maxWidth,
        float maxHeight,
        bool noWrap,
        ProGPU.Text.TextAlignment alignment,
        StringTrimming trimming,
        out int charactersFitted)
    {
        string suffix = trimming is StringTrimming.EllipsisCharacter
            or StringTrimming.EllipsisWord
            or StringTrimming.EllipsisPath
                ? "\u2026"
                : string.Empty;
        if (!Fits(text, suffix: string.Empty))
        {
            var prefixLengths = new List<int>(text.Length + 1) { 0 };
            for (int textIndex = 0; textIndex < text.Length; textIndex++)
            {
                if (char.IsHighSurrogate(text[textIndex])
                    && textIndex + 1 < text.Length
                    && char.IsLowSurrogate(text[textIndex + 1]))
                {
                    textIndex++;
                }

                prefixLengths.Add(textIndex + 1);
            }

            int low = 0;
            int high = prefixLengths.Count - 1;
            int best = 0;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                int prefixLength = prefixLengths[middle];
                if (Fits(text[..prefixLength], suffix))
                {
                    best = prefixLength;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (trimming is StringTrimming.Word or StringTrimming.EllipsisWord)
            {
                best = FindWordBoundary(text, best);
            }

            string prefix = text[..best].TrimEnd();
            while (prefix.Length > 0 && !Fits(prefix, suffix))
            {
                int nextLength = NormalizePrefixLength(prefix, prefix.Length - 1);
                prefix = prefix[..nextLength].TrimEnd();
            }

            charactersFitted = prefix.Length;
            return Fits(prefix, suffix) ? prefix + suffix : string.Empty;
        }

        charactersFitted = text.Length;
        return text;

        bool Fits(string prefix, string suffix)
        {
            string candidate = prefix + suffix;
            var candidateLayout = new ProGPU.Text.TextLayout(
                candidate,
                font.TtfFont,
                fontSize,
                noWrap ? float.PositiveInfinity : maxWidth,
                alignment);
            bool widthFits = !float.IsFinite(maxWidth) || candidateLayout.ContentSize.X <= maxWidth + 0.001f;
            bool heightFits = !float.IsFinite(maxHeight) || candidateLayout.ContentSize.Y <= maxHeight + 0.001f;
            return widthFits && heightFits;
        }
    }

    private static int NormalizePrefixLength(string text, int length)
    {
        length = Math.Clamp(length, 0, text.Length);
        if (length > 0
            && length < text.Length
            && char.IsHighSurrogate(text[length - 1])
            && char.IsLowSurrogate(text[length]))
        {
            length--;
        }

        return length;
    }

    private static int FindWordBoundary(string text, int length)
    {
        length = NormalizePrefixLength(text, length);
        while (length > 0 && char.IsWhiteSpace(text[length - 1]))
        {
            length--;
        }

        if (length == text.Length || (length < text.Length && char.IsWhiteSpace(text[length])))
        {
            return length;
        }

        int boundary = length;
        while (boundary > 0 && !char.IsWhiteSpace(text[boundary - 1]))
        {
            boundary--;
        }

        return boundary == 0 ? length : boundary;
    }

    private static string ApplyHotkeyPrefix(string text, HotkeyPrefix hotkeyPrefix)
    {
        if (hotkeyPrefix == HotkeyPrefix.None || text.IndexOf('&') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                builder.Append(text[i]);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '&')
            {
                builder.Append('&');
                i++;
            }
        }

        return builder.ToString();
    }

    private static ProGPU.Text.TextAlignment GetTextAlignment(
        StringAlignment alignment,
        StringFormatFlags flags)
    {
        if ((flags & StringFormatFlags.DirectionRightToLeft) != 0)
        {
            alignment = alignment switch
            {
                StringAlignment.Near => StringAlignment.Far,
                StringAlignment.Far => StringAlignment.Near,
                _ => alignment
            };
        }

        return alignment switch
        {
            StringAlignment.Center => ProGPU.Text.TextAlignment.Center,
            StringAlignment.Far => ProGPU.Text.TextAlignment.Right,
            _ => ProGPU.Text.TextAlignment.Left
        };
    }

    private static float GetNoWrapAlignmentOffset(
        float contentSize,
        float availableSize,
        StringAlignment alignment,
        StringFormatFlags flags)
    {
        if ((flags & StringFormatFlags.NoWrap) == 0)
        {
            return 0f;
        }

        bool rightToLeft = (flags & StringFormatFlags.DirectionRightToLeft) != 0;
        return GetRectangleAlignmentOffset(contentSize, availableSize, SwapNearAndFar(alignment, rightToLeft));
    }

    private static float GetPointAlignmentOffset(float contentSize, StringAlignment alignment, bool rightToLeft)
    {
        alignment = SwapNearAndFar(alignment, rightToLeft);
        return alignment switch
        {
            StringAlignment.Center => -contentSize / 2f,
            StringAlignment.Far => -contentSize,
            _ => 0f
        };
    }

    private static float GetRectangleAlignmentOffset(
        float contentSize,
        float availableSize,
        StringAlignment alignment)
    {
        float remaining = MathF.Max(0f, availableSize - contentSize);
        return alignment switch
        {
            StringAlignment.Center => remaining / 2f,
            StringAlignment.Far => remaining,
            _ => 0f
        };
    }

    private static StringAlignment SwapNearAndFar(StringAlignment alignment, bool swap)
    {
        if (!swap)
        {
            return alignment;
        }

        return alignment switch
        {
            StringAlignment.Near => StringAlignment.Far,
            StringAlignment.Far => StringAlignment.Near,
            _ => alignment
        };
    }

    private static int GetLineCount(ProGPU.Text.TextLayout layout, Font font, float fontSize)
    {
        if (layout.Text.Length == 0)
        {
            return 0;
        }

        var singleLine = new ProGPU.Text.TextLayout("M", font.TtfFont, fontSize);
        float lineHeight = singleLine.ContentSize.Y;
        return lineHeight > 0f
            ? Math.Max(1, (int)MathF.Round(layout.ContentSize.Y / lineHeight))
            : CountLines(layout.Text);
    }

    private static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private readonly record struct FormattedTextLayout(
        ProGPU.Text.TextLayout Layout,
        int CharactersFitted,
        int LinesFilled);

    private sealed class GlyphRunBuilder
    {
        public GlyphRunBuilder(ProGPU.Text.TtfFont font)
        {
            Font = font;
        }

        public ProGPU.Text.TtfFont Font { get; }
        public List<ushort> GlyphIndices { get; } = [];
        public List<Vector2> GlyphPositions { get; } = [];
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
        if (ReferenceEquals(bitmap, _bitmap))
        {
            throw new InvalidOperationException(
                "Drawing a Bitmap into itself requires an explicit snapshot texture and is not supported by the deferred GPU path.");
        }

        var targetContext = _bitmap?.GetDrawingContext() ?? GpuProvider.Context;
        if (!_context.TryRetainTexture(bitmap, targetContext, out var retainedTexture))
        {
            throw new ObjectDisposedException(nameof(bitmap), "Cannot draw a disposed GDI Bitmap.");
        }

        return retainedTexture;
    }

    public void Dispose()
    {
        _savedStates.Clear();
        _transform.Dispose();
        _bitmap?.Flush();
    }
}
