using System.Numerics;
using ProGPU.Scene;

namespace ProGPU.WinUI.Designer;

/// <summary>Finite, quadrant-independent rectangle construction shared by direct authoring tools.</summary>
public static class DesignerDragRectangle
{
    public static Rect Create(Vector2 anchor, Vector2 pointer, float minimumSize = 0, float? aspectRatio = null)
    {
        if (!float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y) || !float.IsFinite(pointer.X) || !float.IsFinite(pointer.Y))
            throw new ArgumentOutOfRangeException(nameof(pointer), "Rectangle endpoints must be finite.");
        if (!float.IsFinite(minimumSize) || minimumSize < 0) throw new ArgumentOutOfRangeException(nameof(minimumSize));
        if (aspectRatio is { } ratio && (!float.IsFinite(ratio) || ratio <= 0)) throw new ArgumentOutOfRangeException(nameof(aspectRatio));
        double dx = (double)pointer.X - anchor.X, dy = (double)pointer.Y - anchor.Y;
        double width = Math.Max(minimumSize, Math.Abs(dx)), height = Math.Max(minimumSize, Math.Abs(dy));
        if (aspectRatio is { } aspect)
        {
            // Expand the shorter relative axis, keeping the pointer's quadrant and the original anchor.
            if (width < height * aspect) width = height * aspect;
            else height = width / aspect;
        }
        double x = dx < 0 ? anchor.X - width : anchor.X, y = dy < 0 ? anchor.Y - height : anchor.Y;
        var result = new Rect((float)x, (float)y, (float)width, (float)height);
        Validate(result);
        return result;
    }

    public static void Validate(Rect bounds)
    {
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y) || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) ||
            bounds.Width < 0 || bounds.Height < 0 || !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom))
            throw new ArgumentOutOfRangeException(nameof(bounds), "Selection bounds must have finite nonnegative dimensions and edges.");
    }
}
