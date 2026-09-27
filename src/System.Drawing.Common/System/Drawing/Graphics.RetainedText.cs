using System.Drawing.Text;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.SystemDrawing;

namespace System.Drawing;

public partial class Graphics
{
    internal DrawingTextLayout CreateRetainedTextLayout(string text, Font font,
        SizeF layoutSize, StringFormat? format)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        if (!float.IsFinite(layoutSize.Width) || !float.IsFinite(layoutSize.Height) ||
            layoutSize.Width < 0 || layoutSize.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(layoutSize));
        using StringFormat selected = format is null ? new StringFormat() : new StringFormat(format);
        if (format is null)
        {
            selected.Trimming = StringTrimming.None;
            selected.SetDigitSubstitution(0, StringDigitSubstitute.None);
        }
        if (selected.HotkeyPrefix != HotkeyPrefix.None ||
            selected.DigitSubstitutionMethod != StringDigitSubstitute.None ||
            selected.Trimming != StringTrimming.None ||
            (selected.FormatFlags & (StringFormatFlags.DirectionVertical | StringFormatFlags.LineLimit)) != 0)
            throw new NotSupportedException(
                "Retained interaction requires complete horizontal source text without mnemonic, digit or trimming rewrites.");
        const StringFormatFlags supported = StringFormatFlags.DirectionRightToLeft | StringFormatFlags.FitBlackBox |
            StringFormatFlags.DisplayFormatControl | StringFormatFlags.NoFontFallback | StringFormatFlags.MeasureTrailingSpaces |
            StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        if ((selected.FormatFlags & ~supported) != 0)
            throw new NotSupportedException("Unknown retained text-format flags.");
        _ = font.GetHeight(this); // Reject disposed caller fonts before borrowing their immutable face.
        FormattedTextLayout formatted = CreateFormattedTextLayout(text, font, layoutSize, selected,
            retainCompleteParagraph: true);
        var offset = new Vector2(
            GetNoWrapAlignmentOffset(formatted.Layout.ContentSize.X, layoutSize.Width,
                selected.Alignment, selected.FormatFlags),
            GetRectangleAlignmentOffset(formatted.Layout.ContentSize.Y, layoutSize.Height, selected.LineAlignment));
        var decorations = new List<PreparedDrawingTextDecoration>();
        DrawFontDecorations(formatted.Layout, font, null!, Vector2.Zero, Matrix4x4.Identity, decorations);
        return new DrawingTextLayout(formatted.Layout, layoutSize, offset,
            GetLineHeight(font, formatted.Layout.FontSize), DpiX, DpiY, font.Style,
            (selected.FormatFlags & StringFormatFlags.NoClip) == 0,
            PrepareDrawingGlyphRuns(formatted.Layout), decorations.ToArray());
    }

    internal void DrawRetainedTextLayout(DrawingTextLayout layout, Brush brush, PointF location)
    {
        EnsureNotDisposed();
        if (DpiX != layout.DpiX || DpiY != layout.DpiY)
            throw new InvalidOperationException("Recreate the retained text layout for the current target DPI.");
        if (!float.IsFinite(location.X) || !float.IsFinite(location.Y))
            throw new ArgumentOutOfRangeException(nameof(location));
        Vector2 origin = new Vector2(location.X, location.Y) + layout.Offset;
        Matrix4x4 transform = CurrentTransform4x4();
        if (layout.Clip)
            _context.PushClip(new Rect(location.X, location.Y, layout.LayoutSize.Width, layout.LayoutSize.Height), transform);
        try
        {
            DrawPreparedGlyphRuns(layout.Runs, layout.Style, layout.FontSize, brush, origin, transform);
            if (layout.Decorations.Length != 0)
            {
                var nativeBrush = TransformBrush(brush);
                foreach (PreparedDrawingTextDecoration decoration in layout.Decorations)
                    _context.DrawRectangle(nativeBrush, null, decoration.Place(origin), transform);
            }
        }
        finally
        {
            if (layout.Clip) _context.PopClip();
        }
    }
}
