using System.ComponentModel;
using System.Drawing.Imaging;

namespace System.Drawing;

public partial class Graphics
{
    public void AddMetafileComment(byte[] data)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(data);
        if (_metafileRecording is null)
        {
            throw new InvalidOperationException(
                "Metafile comments require an active portable metafile recording session.");
        }

        _metafileRecording.AddComment(data);
    }

    public void EnumerateMetafile(Metafile metafile, PointF destPoint, EnumerateMetafileProc callback) =>
        EnumerateMetafile(metafile, destPoint, callback, IntPtr.Zero);

    public void EnumerateMetafile(Metafile metafile, PointF destPoint, EnumerateMetafileProc callback, IntPtr callbackData) =>
        EnumerateMetafile(metafile, destPoint, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        PointF destPoint,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr) => EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);

    public void EnumerateMetafile(Metafile metafile, Point destPoint, EnumerateMetafileProc callback) =>
        EnumerateMetafile(metafile, destPoint, callback, IntPtr.Zero);

    public void EnumerateMetafile(Metafile metafile, Point destPoint, EnumerateMetafileProc callback, IntPtr callbackData) =>
        EnumerateMetafile(metafile, destPoint, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        Point destPoint,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr) => EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);

    public void EnumerateMetafile(Metafile metafile, RectangleF destRect, EnumerateMetafileProc callback) =>
        EnumerateMetafile(metafile, destRect, callback, IntPtr.Zero);

    public void EnumerateMetafile(Metafile metafile, RectangleF destRect, EnumerateMetafileProc callback, IntPtr callbackData) =>
        EnumerateMetafile(metafile, destRect, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        RectangleF destRect,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr) => EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);

    public void EnumerateMetafile(Metafile metafile, Rectangle destRect, EnumerateMetafileProc callback) =>
        EnumerateMetafile(metafile, destRect, callback, IntPtr.Zero);

    public void EnumerateMetafile(Metafile metafile, Rectangle destRect, EnumerateMetafileProc callback, IntPtr callbackData) =>
        EnumerateMetafile(metafile, destRect, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        Rectangle destRect,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr) => EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);

    public void EnumerateMetafile(Metafile metafile, PointF[] destPoints, EnumerateMetafileProc callback) =>
        EnumerateMetafile(metafile, destPoints, callback, IntPtr.Zero);

    public void EnumerateMetafile(
        Metafile metafile,
        PointF[] destPoints,
        EnumerateMetafileProc callback,
        IntPtr callbackData) => EnumerateMetafile(metafile, destPoints, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        PointF[] destPoints,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr)
    {
        ValidateMetafileDestinationPoints(destPoints);
        EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);
    }

    public void EnumerateMetafile(Metafile metafile, Point[] destPoints, EnumerateMetafileProc callback) =>
        EnumerateMetafile(metafile, destPoints, callback, IntPtr.Zero);

    public void EnumerateMetafile(
        Metafile metafile,
        Point[] destPoints,
        EnumerateMetafileProc callback,
        IntPtr callbackData) => EnumerateMetafile(metafile, destPoints, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        Point[] destPoints,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr)
    {
        ValidateMetafileDestinationPoints(destPoints);
        EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);
    }

    public void EnumerateMetafile(
        Metafile metafile,
        PointF destPoint,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback) => EnumerateMetafile(metafile, destPoint, srcRect, srcUnit, callback, IntPtr.Zero);

    public void EnumerateMetafile(
        Metafile metafile,
        PointF destPoint,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData) => EnumerateMetafile(metafile, destPoint, srcRect, srcUnit, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        PointF destPoint,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr)
    {
        ValidateMetafileUnit(srcUnit);
        EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);
    }

    public void EnumerateMetafile(
        Metafile metafile,
        Point destPoint,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback) => EnumerateMetafile(metafile, destPoint, srcRect, srcUnit, callback, IntPtr.Zero);

    public void EnumerateMetafile(
        Metafile metafile,
        Point destPoint,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData) => EnumerateMetafile(metafile, destPoint, srcRect, srcUnit, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        Point destPoint,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr)
    {
        ValidateMetafileUnit(srcUnit);
        EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);
    }

    public void EnumerateMetafile(
        Metafile metafile,
        RectangleF destRect,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback) => EnumerateMetafile(metafile, destRect, srcRect, srcUnit, callback, IntPtr.Zero);

    public void EnumerateMetafile(
        Metafile metafile,
        RectangleF destRect,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData) => EnumerateMetafile(metafile, destRect, srcRect, srcUnit, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        RectangleF destRect,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr)
    {
        ValidateMetafileUnit(srcUnit);
        EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);
    }

    public void EnumerateMetafile(
        Metafile metafile,
        Rectangle destRect,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback) => EnumerateMetafile(metafile, destRect, srcRect, srcUnit, callback, IntPtr.Zero);

    public void EnumerateMetafile(
        Metafile metafile,
        Rectangle destRect,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData) => EnumerateMetafile(metafile, destRect, srcRect, srcUnit, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        Rectangle destRect,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr)
    {
        ValidateMetafileUnit(srcUnit);
        EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);
    }

    public void EnumerateMetafile(
        Metafile metafile,
        PointF[] destPoints,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback) => EnumerateMetafile(metafile, destPoints, srcRect, srcUnit, callback, IntPtr.Zero);

    public void EnumerateMetafile(
        Metafile metafile,
        PointF[] destPoints,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData) => EnumerateMetafile(metafile, destPoints, srcRect, srcUnit, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        PointF[] destPoints,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr)
    {
        ValidateMetafileDestinationPoints(destPoints);
        ValidateMetafileUnit(srcUnit);
        EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);
    }

    public void EnumerateMetafile(
        Metafile metafile,
        Point[] destPoints,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback) => EnumerateMetafile(metafile, destPoints, srcRect, srcUnit, callback, IntPtr.Zero);

    public void EnumerateMetafile(
        Metafile metafile,
        Point[] destPoints,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData) => EnumerateMetafile(metafile, destPoints, srcRect, srcUnit, callback, callbackData, null);

    public void EnumerateMetafile(
        Metafile metafile,
        Point[] destPoints,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr)
    {
        ValidateMetafileDestinationPoints(destPoints);
        ValidateMetafileUnit(srcUnit);
        EnumerateMetafileCore(metafile, callback, callbackData, imageAttr);
    }

    private unsafe void EnumerateMetafileCore(
        Metafile metafile,
        EnumerateMetafileProc callback,
        IntPtr callbackData,
        ImageAttributes? imageAttr)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(metafile);
        ArgumentNullException.ThrowIfNull(callback);
        metafile.EnsureNotDisposed();
        imageAttr?.EnsureNotDisposed();

        // System.Drawing's native adapter uses callbackData as native state and
        // passes null for the managed PlayRecordCallback parameter. Preserve
        // that observable managed contract while retaining the public ABI.
        _ = callbackData;

        ReadOnlySpan<byte> source = metafile.Source;
        ReadOnlySpan<MetafileRecord> records = metafile.Records;
        fixed (byte* sourcePointer = source)
        {
            foreach (ref readonly MetafileRecord record in records)
            {
                IntPtr data = record.DataLength == 0
                    ? IntPtr.Zero
                    : (IntPtr)(sourcePointer + record.DataOffset);
                if (!callback(record.Type, record.Flags, record.DataLength, data, null))
                {
                    break;
                }
            }
        }
    }

    private static void ValidateMetafileDestinationPoints<T>(T[] destPoints)
    {
        ArgumentNullException.ThrowIfNull(destPoints);
        if (destPoints.Length != 3)
        {
            throw new ArgumentException("Destination points must define a three-point parallelogram.", nameof(destPoints));
        }
    }

    private static void ValidateMetafileUnit(GraphicsUnit unit)
    {
        if (unit is < GraphicsUnit.World or > GraphicsUnit.Millimeter)
        {
            throw new InvalidEnumArgumentException(nameof(unit), (int)unit, typeof(GraphicsUnit));
        }
    }
}
