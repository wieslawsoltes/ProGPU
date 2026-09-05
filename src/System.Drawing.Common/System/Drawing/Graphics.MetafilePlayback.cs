using ProGPU.Scene;
using System.Drawing.Imaging;
using System.Numerics;

namespace System.Drawing;

public partial class Graphics
{
    private void DrawMetafile(
        Metafile metafile,
        PointF destination0,
        PointF destination1,
        PointF destination2,
        RectangleF sourceRectangle,
        ImageAttributes? imageAttributes)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(metafile);
        metafile.EnsureNotDisposed();
        imageAttributes?.EnsureNotDisposed();
        if (imageAttributes is not null)
        {
            throw new NotSupportedException(
                "Metafile image-attribute playback requires the typed image/object record tranche.");
        }
        if (!IsFinite(sourceRectangle) || sourceRectangle.Width <= 0f || sourceRectangle.Height <= 0f)
        {
            throw new ArgumentException("The metafile source rectangle must be finite and non-empty.", "srcRect");
        }

        Vector2 topLeft = new(destination0.X, destination0.Y);
        Vector2 topRight = new(destination1.X, destination1.Y);
        Vector2 bottomLeft = new(destination2.X, destination2.Y);
        if (!IsFinite(topLeft) || !IsFinite(topRight) || !IsFinite(bottomLeft))
        {
            throw new ArgumentException("Metafile destination points must be finite.", "destPoints");
        }

        float m11 = (topRight.X - topLeft.X) / sourceRectangle.Width;
        float m12 = (topRight.Y - topLeft.Y) / sourceRectangle.Width;
        float m21 = (bottomLeft.X - topLeft.X) / sourceRectangle.Height;
        float m22 = (bottomLeft.Y - topLeft.Y) / sourceRectangle.Height;
        var sourceToDestination = new Matrix3x2(
            m11,
            m12,
            m21,
            m22,
            topLeft.X - sourceRectangle.X * m11 - sourceRectangle.Y * m21,
            topLeft.Y - sourceRectangle.X * m12 - sourceRectangle.Y * m22);
        if (!IsFinite2DAffineTransform(ToMatrix4x4(sourceToDestination)) ||
            !Matrix3x2.Invert(sourceToDestination, out _))
        {
            throw new ArgumentException(
                "Metafile destination points must define a non-degenerate parallelogram.",
                "destPoints");
        }

        Matrix3x2 playbackBase = sourceToDestination * CombinedTransform;
        var recording = new DrawingContext();
        using (Graphics playback = FromProGpuDrawingContext(recording, ToMatrix4x4(playbackBase)))
        {
            playback.SetClip(sourceRectangle);
            MetafilePlaybackRenderer.Play(metafile, playback);
        }

        _context.Append(recording);
    }

    private void DrawMetafile(Metafile metafile, RectangleF destinationRectangle)
    {
        Rectangle bounds = metafile.GetMetafileHeader().Bounds;
        RectangleF source = bounds;
        DrawMetafile(
            metafile,
            destinationRectangle.Location,
            new PointF(destinationRectangle.Right, destinationRectangle.Top),
            new PointF(destinationRectangle.Left, destinationRectangle.Bottom),
            source,
            imageAttributes: null);
    }
}
