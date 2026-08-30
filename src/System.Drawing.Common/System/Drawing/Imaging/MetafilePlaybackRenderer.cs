using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Numerics;

namespace System.Drawing.Imaging;

internal static class MetafilePlaybackRenderer
{
    private const uint StockObjectFlag = 0x8000_0000;
    private const uint WhiteBrush = 0;
    private const uint LightGrayBrush = 1;
    private const uint GrayBrush = 2;
    private const uint DarkGrayBrush = 3;
    private const uint BlackBrush = 4;
    private const uint NullBrush = 5;
    private const uint WhitePen = 6;
    private const uint BlackPen = 7;
    private const uint NullPen = 8;

    internal static void Play(Metafile metafile, Graphics graphics)
    {
        ArgumentNullException.ThrowIfNull(metafile);
        ArgumentNullException.ThrowIfNull(graphics);
        metafile.EnsureNotDisposed();

        MetafileHeader header = metafile.GetMetafileHeader();
        bool isWmf = header.IsWmf();
        int wmfObjectCapacity = isWmf ? (ushort)header.WmfHeader.NoObjects : 0;
        ReadOnlySpan<byte> source = metafile.Source;
        using var state = new PlaybackState(graphics, wmfObjectCapacity);
        foreach (ref readonly MetafileRecord record in metafile.Records)
        {
            ReadOnlySpan<byte> payload = source.Slice(record.DataOffset, record.DataLength);
            if (record.IsEmfPlus)
            {
                PlayEmfPlusRecord(record, payload);
            }
            else if (isWmf)
            {
                PlayWmfRecord(state, record, payload);
            }
            else
            {
                PlayEmfRecord(state, record, payload);
            }
        }
    }

    private static void PlayWmfRecord(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        switch (record.Type)
        {
            case EmfPlusRecordType.WmfRecordBase:
                RequireSize(record, payload, 0);
                return;

            case EmfPlusRecordType.WmfSetBkMode:
                RequireSize(record, payload, 2);
                state.SetBackgroundMode(ReadUInt16(payload, 0), record);
                return;

            case EmfPlusRecordType.WmfSetROP2:
                RequireSize(record, payload, 2);
                state.SetRasterOperation(ReadUInt16(payload, 0), record);
                return;

            case EmfPlusRecordType.WmfSetRelAbs:
                RequireSize(record, payload, 2);
                return;

            case EmfPlusRecordType.WmfSetPolyFillMode:
                RequireSize(record, payload, 2);
                state.FillMode = ReadUInt16(payload, 0) switch
                {
                    1 => FillMode.Alternate,
                    2 => FillMode.Winding,
                    _ => throw Invalid(record)
                };
                return;

            case EmfPlusRecordType.WmfSetTextAlign:
                RequireSize(record, payload, 2);
                state.TextAlignment = ReadUInt16(payload, 0);
                return;

            case EmfPlusRecordType.WmfSetBkColor:
                RequireSize(record, payload, 4);
                state.BackgroundColor = ReadColor(payload, 0);
                return;

            case EmfPlusRecordType.WmfSetWindowOrg:
                RequireSize(record, payload, 4);
                state.WindowOrigin = ReadWmfYxPoint(payload);
                return;

            case EmfPlusRecordType.WmfSetWindowExt:
                RequireSize(record, payload, 4);
                state.SetWindowExtent(ReadWmfYxPoint(payload), record);
                return;

            case EmfPlusRecordType.WmfMoveTo:
                RequireSize(record, payload, 4);
                state.CurrentPoint = ReadWmfYxPoint(payload);
                return;

            case EmfPlusRecordType.WmfSelectObject:
                RequireSize(record, payload, 2);
                state.SelectWmfObject(ReadUInt16(payload, 0), record);
                return;

            case EmfPlusRecordType.WmfDeleteObject:
                RequireSize(record, payload, 2);
                state.DeleteWmfObject(ReadUInt16(payload, 0), record);
                return;

            case EmfPlusRecordType.WmfCreatePenIndirect:
                RequireSize(record, payload, 10);
                state.CreateWmfPen(payload, record);
                return;

            case EmfPlusRecordType.WmfCreateBrushIndirect:
                RequireSize(record, payload, 8);
                state.CreateWmfBrush(payload, record);
                return;

            case EmfPlusRecordType.WmfPolygon:
                DrawWmfPolygon(state, record, payload, close: true);
                return;

            case EmfPlusRecordType.WmfPolyline:
                DrawWmfPolygon(state, record, payload, close: false);
                return;

            case EmfPlusRecordType.WmfArc:
                DrawWmfArc(state, record, payload);
                return;

            case EmfPlusRecordType.WmfEllipse:
                DrawEllipse(state, record, ReadWmfRectangle(record, payload));
                return;

            case EmfPlusRecordType.WmfRectangle:
                DrawRectangle(state, record, ReadWmfRectangle(record, payload));
                return;

            default:
                throw Unsupported(record);
        }
    }

    private static void PlayEmfPlusRecord(in MetafileRecord record, ReadOnlySpan<byte> payload)
    {
        _ = payload;
        if (record.Type is EmfPlusRecordType.Header or
            EmfPlusRecordType.Comment or
            EmfPlusRecordType.EndOfFile)
        {
            return;
        }

        throw Unsupported(record);
    }

    private static void PlayEmfRecord(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        switch (record.Type)
        {
            case EmfPlusRecordType.EmfHeader:
            case EmfPlusRecordType.EmfEof:
            case EmfPlusRecordType.EmfGdiComment:
                return;

            case EmfPlusRecordType.EmfSetWindowExtEx:
                RequireSize(record, payload, 8);
                state.SetWindowExtent(ReadSize(payload), record);
                return;

            case EmfPlusRecordType.EmfSetWindowOrgEx:
                RequireSize(record, payload, 8);
                state.WindowOrigin = ReadPoint(payload);
                return;

            case EmfPlusRecordType.EmfSetViewportExtEx:
                RequireSize(record, payload, 8);
                state.SetViewportExtent(ReadSize(payload), record);
                return;

            case EmfPlusRecordType.EmfSetViewportOrgEx:
                RequireSize(record, payload, 8);
                state.ViewportOrigin = ReadPoint(payload);
                return;

            case EmfPlusRecordType.EmfScaleViewportExtEx:
                RequireSize(record, payload, 16);
                state.ScaleViewportExtent(payload, record);
                return;

            case EmfPlusRecordType.EmfScaleWindowExtEx:
                RequireSize(record, payload, 16);
                state.ScaleWindowExtent(payload, record);
                return;

            case EmfPlusRecordType.EmfSetMapMode:
                RequireSize(record, payload, 4);
                state.SetMapMode(ReadInt32(payload, 0), record);
                return;

            case EmfPlusRecordType.EmfSetPolyFillMode:
                RequireSize(record, payload, 4);
                state.FillMode = ReadInt32(payload, 0) switch
                {
                    1 => FillMode.Alternate,
                    2 => FillMode.Winding,
                    _ => throw Invalid(record)
                };
                return;

            case EmfPlusRecordType.EmfSetBkMode:
                RequireSize(record, payload, 4);
                state.SetBackgroundMode(ReadInt32(payload, 0), record);
                return;

            case EmfPlusRecordType.EmfSetROP2:
                RequireSize(record, payload, 4);
                state.SetRasterOperation(ReadInt32(payload, 0), record);
                return;

            case EmfPlusRecordType.EmfMoveToEx:
                RequireSize(record, payload, 8);
                state.CurrentPoint = ReadPoint(payload);
                return;

            case EmfPlusRecordType.EmfLineTo:
                RequireSize(record, payload, 8);
                Point next = ReadPoint(payload);
                state.ApplyTransform(record);
                if (state.SelectedPen is Pen linePen)
                {
                    state.Graphics.DrawLine(linePen, state.CurrentPoint, next);
                }
                state.CurrentPoint = next;
                return;

            case EmfPlusRecordType.EmfRectangle:
                RequireSize(record, payload, 16);
                DrawRectangle(state, record, ReadRectangle(record, payload));
                return;

            case EmfPlusRecordType.EmfEllipse:
                RequireSize(record, payload, 16);
                DrawEllipse(state, record, ReadRectangle(record, payload));
                return;

            case EmfPlusRecordType.EmfPolygon:
                DrawPolygon(state, record, payload, close: true);
                return;

            case EmfPlusRecordType.EmfPolyline:
                DrawPolygon(state, record, payload, close: false);
                return;

            case EmfPlusRecordType.EmfPolyPolygon:
                DrawPolyPoly(state, record, payload, close: true);
                return;

            case EmfPlusRecordType.EmfPolyPolyline:
                DrawPolyPoly(state, record, payload, close: false);
                return;

            case EmfPlusRecordType.EmfIntersectClipRect:
                RequireSize(record, payload, 16);
                state.IntersectClip(record, ReadRectangle(record, payload));
                return;

            case EmfPlusRecordType.EmfSaveDC:
                RequireSize(record, payload, 0);
                state.Save();
                return;

            case EmfPlusRecordType.EmfRestoreDC:
                RequireSize(record, payload, 4);
                state.Restore(ReadInt32(payload, 0), record);
                return;

            case EmfPlusRecordType.EmfSetWorldTransform:
                RequireSize(record, payload, 24);
                state.WorldTransform = ReadTransform(record, payload);
                return;

            case EmfPlusRecordType.EmfModifyWorldTransform:
                RequireSize(record, payload, 28);
                state.ModifyWorldTransform(
                    ReadTransform(record, payload),
                    ReadUInt32(payload, 24),
                    record);
                return;

            case EmfPlusRecordType.EmfSelectObject:
                RequireSize(record, payload, 4);
                state.SelectObject(ReadUInt32(payload, 0), record);
                return;

            case EmfPlusRecordType.EmfCreatePen:
                RequireSize(record, payload, 20);
                state.CreatePen(payload, record);
                return;

            case EmfPlusRecordType.EmfCreateBrushIndirect:
                RequireSize(record, payload, 16);
                state.CreateBrush(payload, record);
                return;

            case EmfPlusRecordType.EmfDeleteObject:
                RequireSize(record, payload, 4);
                state.DeleteObject(ReadUInt32(payload, 0), record);
                return;

            default:
                throw Unsupported(record);
        }
    }

    private static void DrawRectangle(
        PlaybackState state,
        in MetafileRecord record,
        Rectangle rectangle)
    {
        state.ApplyTransform(record);
        if (state.SelectedBrush is Brush brush)
        {
            state.Graphics.FillRectangle(brush, rectangle);
        }
        if (state.SelectedPen is Pen pen)
        {
            state.Graphics.DrawRectangle(pen, rectangle);
        }
    }

    private static Rectangle ReadWmfRectangle(
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        RequireSize(record, payload, 8);
        int bottom = ReadInt16(payload, 0);
        int right = ReadInt16(payload, 2);
        int top = ReadInt16(payload, 4);
        int left = ReadInt16(payload, 6);
        if (right <= left || bottom <= top)
        {
            throw Invalid(record);
        }

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static void DrawEllipse(
        PlaybackState state,
        in MetafileRecord record,
        Rectangle rectangle)
    {
        state.ApplyTransform(record);
        if (state.SelectedBrush is Brush brush)
        {
            state.Graphics.FillEllipse(brush, rectangle);
        }
        if (state.SelectedPen is Pen pen)
        {
            state.Graphics.DrawEllipse(pen, rectangle);
        }
    }

    private static void DrawPolygon(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        bool close)
    {
        if (payload.Length < 20)
        {
            throw Invalid(record);
        }

        uint countValue = ReadUInt32(payload, 16);
        if (countValue > 1_000_000)
        {
            throw Invalid(record);
        }

        int count = checked((int)countValue);
        int expectedSize = checked(20 + count * 8);
        RequireSize(record, payload, expectedSize);
        if (count < (close ? 3 : 2))
        {
            throw Invalid(record);
        }

        var points = new Point[count];
        int cursor = 20;
        for (int index = 0; index < count; index++)
        {
            points[index] = ReadPoint(payload[cursor..]);
            cursor += 8;
        }

        state.ApplyTransform(record);
        if (close && state.SelectedBrush is Brush brush)
        {
            state.Graphics.FillPolygon(brush, points, state.FillMode);
        }
        if (state.SelectedPen is Pen pen)
        {
            if (close)
            {
                state.Graphics.DrawPolygon(pen, points);
            }
            else
            {
                state.Graphics.DrawLines(pen, points);
            }
        }
    }

    private static void DrawPolyPoly(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        bool close)
    {
        if (payload.Length < 24)
        {
            throw Invalid(record);
        }

        uint polygonCountValue = ReadUInt32(payload, 16);
        uint pointCountValue = ReadUInt32(payload, 20);
        if (polygonCountValue == 0 || polygonCountValue > 1_000_000 ||
            pointCountValue > 1_000_000)
        {
            throw Invalid(record);
        }

        int polygonCount;
        int pointCount;
        int pointsOffset;
        int expectedSize;
        try
        {
            polygonCount = checked((int)polygonCountValue);
            pointCount = checked((int)pointCountValue);
            pointsOffset = checked(24 + polygonCount * 4);
            expectedSize = checked(pointsOffset + pointCount * 8);
        }
        catch (OverflowException exception)
        {
            throw Invalid(record, exception);
        }
        RequireSize(record, payload, expectedSize);

        int consumedPoints = 0;
        for (int polygonIndex = 0; polygonIndex < polygonCount; polygonIndex++)
        {
            uint currentCountValue = ReadUInt32(payload, 24 + polygonIndex * 4);
            int currentCount;
            try
            {
                currentCount = checked((int)currentCountValue);
                consumedPoints = checked(consumedPoints + currentCount);
            }
            catch (OverflowException exception)
            {
                throw Invalid(record, exception);
            }
            if (currentCount < (close ? 3 : 2) || consumedPoints > pointCount)
            {
                throw Invalid(record);
            }
        }
        if (consumedPoints != pointCount)
        {
            throw Invalid(record);
        }

        state.ApplyTransform(record);
        int pointIndex = 0;
        for (int polygonIndex = 0; polygonIndex < polygonCount; polygonIndex++)
        {
            int currentCount = checked((int)ReadUInt32(payload, 24 + polygonIndex * 4));
            var points = new Point[currentCount];
            for (int index = 0; index < currentCount; index++)
            {
                points[index] = ReadPoint(payload[(pointsOffset + pointIndex * 8)..]);
                pointIndex++;
            }

            if (close && state.SelectedBrush is Brush brush)
            {
                state.Graphics.FillPolygon(brush, points, state.FillMode);
            }
            if (state.SelectedPen is Pen pen)
            {
                if (close)
                {
                    state.Graphics.DrawPolygon(pen, points);
                }
                else
                {
                    state.Graphics.DrawLines(pen, points);
                }
            }
        }
    }

    private static void DrawWmfPolygon(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        bool close)
    {
        if (payload.Length < 2)
        {
            throw Invalid(record);
        }

        int count = ReadInt16(payload, 0);
        if (count < 2)
        {
            throw Invalid(record);
        }

        int expectedSize;
        try
        {
            expectedSize = checked(2 + count * 4);
        }
        catch (OverflowException exception)
        {
            throw Invalid(record, exception);
        }
        RequireSize(record, payload, expectedSize);

        var points = new Point[count];
        int cursor = 2;
        for (int index = 0; index < count; index++)
        {
            points[index] = new Point(
                ReadInt16(payload, cursor),
                ReadInt16(payload, cursor + 2));
            cursor += 4;
        }

        state.ApplyTransform(record);
        if (close && count >= 3 && state.SelectedBrush is Brush brush)
        {
            state.Graphics.FillPolygon(brush, points, state.FillMode);
        }
        if (state.SelectedPen is Pen pen)
        {
            if (close && count >= 3)
            {
                state.Graphics.DrawPolygon(pen, points);
            }
            else
            {
                state.Graphics.DrawLines(pen, points);
            }
        }
    }

    private static void DrawWmfArc(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        RequireSize(record, payload, 16);
        int left = ReadInt16(payload, 14);
        int top = ReadInt16(payload, 12);
        int right = ReadInt16(payload, 10);
        int bottom = ReadInt16(payload, 8);
        if (right <= left || bottom <= top)
        {
            throw Invalid(record);
        }

        var rectangle = Rectangle.FromLTRB(left, top, right, bottom);
        var start = new Point(ReadInt16(payload, 6), ReadInt16(payload, 4));
        var end = new Point(ReadInt16(payload, 2), ReadInt16(payload, 0));
        float radiusX = rectangle.Width / 2f;
        float radiusY = rectangle.Height / 2f;
        float centerX = rectangle.Left + radiusX;
        float centerY = rectangle.Top + radiusY;
        float startAngle = MathF.Atan2(
            (start.Y - centerY) / radiusY,
            (start.X - centerX) / radiusX) * (180f / MathF.PI);
        float endAngle = MathF.Atan2(
            (end.Y - centerY) / radiusY,
            (end.X - centerX) / radiusX) * (180f / MathF.PI);
        float sweepAngle = endAngle - startAngle;
        if (sweepAngle >= 0f)
        {
            sweepAngle -= 360f;
        }

        state.ApplyTransform(record);
        if (state.SelectedPen is Pen pen)
        {
            state.Graphics.DrawArc(pen, rectangle, startAngle, sweepAngle);
        }
    }

    private static Point ScaleExtent(
        Point extent,
        ReadOnlySpan<byte> payload,
        in MetafileRecord record)
    {
        int xNumerator = ReadInt32(payload, 0);
        int xDenominator = ReadInt32(payload, 4);
        int yNumerator = ReadInt32(payload, 8);
        int yDenominator = ReadInt32(payload, 12);
        if (xDenominator == 0 || yDenominator == 0)
        {
            throw Invalid(record);
        }

        try
        {
            return new Point(
                checked((int)((long)extent.X * xNumerator / xDenominator)),
                checked((int)((long)extent.Y * yNumerator / yDenominator)));
        }
        catch (OverflowException exception)
        {
            throw Invalid(record, exception);
        }
    }

    private static Matrix3x2 ReadTransform(
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        var transform = new Matrix3x2(
            ReadSingle(payload, 0),
            ReadSingle(payload, 4),
            ReadSingle(payload, 8),
            ReadSingle(payload, 12),
            ReadSingle(payload, 16),
            ReadSingle(payload, 20));
        if (!IsFinite(transform) || !Matrix3x2.Invert(transform, out _))
        {
            throw Invalid(record);
        }
        return transform;
    }

    private static Rectangle ReadRectangle(
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        int left = ReadInt32(payload, 0);
        int top = ReadInt32(payload, 4);
        int right = ReadInt32(payload, 8);
        int bottom = ReadInt32(payload, 12);
        if (right < left || bottom < top)
        {
            throw Invalid(record);
        }
        if ((long)right - left > int.MaxValue || (long)bottom - top > int.MaxValue)
        {
            throw Invalid(record);
        }
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static Point ReadPoint(ReadOnlySpan<byte> payload) =>
        new(ReadInt32(payload, 0), ReadInt32(payload, 4));

    private static Point ReadSize(ReadOnlySpan<byte> payload) => ReadPoint(payload);

    private static Point ReadWmfYxPoint(ReadOnlySpan<byte> payload) =>
        new(ReadInt16(payload, 2), ReadInt16(payload, 0));

    private static Color ReadColor(ReadOnlySpan<byte> payload, int offset)
    {
        uint color = ReadUInt32(payload, offset);
        return Color.FromArgb(
            red: (byte)color,
            green: (byte)(color >> 8),
            blue: (byte)(color >> 16));
    }

    private static void RequireSize(
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        int expectedSize)
    {
        if (payload.Length != expectedSize)
        {
            throw Invalid(record);
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));

    private static ushort ReadUInt16(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2));

    private static short ReadInt16(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(offset, 2));

    private static int ReadInt32(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));

    private static float ReadSingle(ReadOnlySpan<byte> payload, int offset) =>
        BitConverter.Int32BitsToSingle(ReadInt32(payload, offset));

    private static bool IsFinite(Matrix3x2 matrix) =>
        float.IsFinite(matrix.M11) &&
        float.IsFinite(matrix.M12) &&
        float.IsFinite(matrix.M21) &&
        float.IsFinite(matrix.M22) &&
        float.IsFinite(matrix.M31) &&
        float.IsFinite(matrix.M32);

    private static ArgumentException Invalid(
        in MetafileRecord record,
        Exception? inner = null) =>
        new($"Metafile record {record.Type} at byte offset {record.Offset} is invalid.", inner);

    private static NotSupportedException Unsupported(
        in MetafileRecord record,
        string? detail = null) =>
        new(
            $"Metafile record {record.Type} at byte offset {record.Offset} is not supported" +
            (detail is null ? "." : $": {detail}"));

    private sealed class PlaybackState : IDisposable
    {
        private readonly Dictionary<uint, object> _objects = [];
        private readonly List<SavedState> _savedStates = [];
        private readonly int _wmfObjectCapacity;
        private Pen? _selectedPen = Pens.Black;
        private Brush? _selectedBrush = Brushes.White;

        internal PlaybackState(Graphics graphics, int wmfObjectCapacity)
        {
            Graphics = graphics;
            _wmfObjectCapacity = wmfObjectCapacity;
        }

        internal Graphics Graphics { get; }
        internal Point WindowOrigin { get; set; }
        internal Point WindowExtent { get; set; } = new(1, 1);
        internal Point ViewportOrigin { get; set; }
        internal Point ViewportExtent { get; set; } = new(1, 1);
        internal Point CurrentPoint { get; set; }
        internal Matrix3x2 WorldTransform { get; set; } = Matrix3x2.Identity;
        internal FillMode FillMode { get; set; } = FillMode.Alternate;
        internal int MapMode { get; set; } = 1;
        internal int BackgroundMode { get; set; } = 2;
        internal int RasterOperation { get; set; } = 13;
        internal int TextAlignment { get; set; }
        internal Color BackgroundColor { get; set; } = Color.White;
        internal Pen? SelectedPen => _selectedPen;
        internal Brush? SelectedBrush => _selectedBrush;

        internal void ApplyTransform(in MetafileRecord record)
        {
            ValidateExtents(record);
            float scaleX = MapMode == 1 ? 1f : (float)ViewportExtent.X / WindowExtent.X;
            float scaleY = MapMode == 1 ? 1f : (float)ViewportExtent.Y / WindowExtent.Y;
            var page = new Matrix3x2(
                scaleX,
                0f,
                0f,
                scaleY,
                ViewportOrigin.X - WindowOrigin.X * scaleX,
                ViewportOrigin.Y - WindowOrigin.Y * scaleY);
            Matrix3x2 combined = WorldTransform * page;
            if (!IsFinite(combined) || !Matrix3x2.Invert(combined, out _))
            {
                throw Invalid(record);
            }

            Graphics.TransformElements = combined;
        }

        internal void ValidateExtents(in MetafileRecord record)
        {
            if (MapMode == 8 &&
                (WindowExtent.X == 0 || WindowExtent.Y == 0 ||
                 ViewportExtent.X == 0 || ViewportExtent.Y == 0))
            {
                throw Invalid(record);
            }
        }

        internal void SetMapMode(int mapMode, in MetafileRecord record)
        {
            if (mapMode is not 1 and not 8)
            {
                throw Unsupported(
                    record,
                    "The initial player supports MM_TEXT and MM_ANISOTROPIC map modes only.");
            }
            MapMode = mapMode;
            ValidateExtents(record);
        }

        internal void SetWindowExtent(Point extent, in MetafileRecord record)
        {
            if (MapMode == 8)
            {
                WindowExtent = extent;
                ValidateExtents(record);
            }
        }

        internal void SetViewportExtent(Point extent, in MetafileRecord record)
        {
            if (MapMode == 8)
            {
                ViewportExtent = extent;
                ValidateExtents(record);
            }
        }

        internal void ScaleWindowExtent(ReadOnlySpan<byte> payload, in MetafileRecord record)
        {
            if (MapMode == 8)
            {
                WindowExtent = ScaleExtent(WindowExtent, payload, record);
                ValidateExtents(record);
            }
        }

        internal void ScaleViewportExtent(ReadOnlySpan<byte> payload, in MetafileRecord record)
        {
            if (MapMode == 8)
            {
                ViewportExtent = ScaleExtent(ViewportExtent, payload, record);
                ValidateExtents(record);
            }
        }

        internal void SetBackgroundMode(int mode, in MetafileRecord record)
        {
            if (mode is not 1 and not 2)
            {
                throw Unsupported(record, "Only TRANSPARENT and OPAQUE background modes are valid.");
            }
            BackgroundMode = mode;
        }

        internal void SetRasterOperation(int operation, in MetafileRecord record)
        {
            if (operation != 13)
            {
                throw Unsupported(record, "The initial vector player supports R2_COPYPEN only.");
            }
            RasterOperation = operation;
        }

        internal void IntersectClip(in MetafileRecord record, Rectangle rectangle)
        {
            ApplyTransform(record);
            Graphics.IntersectClip(rectangle);
        }

        internal void ModifyWorldTransform(
            Matrix3x2 transform,
            uint mode,
            in MetafileRecord record)
        {
            WorldTransform = mode switch
            {
                1 => Matrix3x2.Identity,
                2 => WorldTransform * transform,
                3 => transform * WorldTransform,
                4 => transform,
                _ => throw Invalid(record)
            };
            if (!IsFinite(WorldTransform) || !Matrix3x2.Invert(WorldTransform, out _))
            {
                throw Invalid(record);
            }
        }

        internal void Save()
        {
            GraphicsState graphicsState = Graphics.Save();
            _savedStates.Add(new SavedState(
                WindowOrigin,
                WindowExtent,
                ViewportOrigin,
                ViewportExtent,
                CurrentPoint,
                WorldTransform,
                FillMode,
                MapMode,
                BackgroundMode,
                RasterOperation,
                _selectedPen,
                _selectedBrush,
                graphicsState));
        }

        internal void Restore(int savedDc, in MetafileRecord record)
        {
            if (savedDc >= 0 || savedDc == int.MinValue)
            {
                throw Unsupported(record, "Only relative negative RestoreDC levels are supported.");
            }

            int restoreCount = -savedDc;
            if (restoreCount > _savedStates.Count)
            {
                throw Invalid(record);
            }

            int stateIndex = _savedStates.Count - restoreCount;
            SavedState saved = _savedStates[stateIndex];
            _savedStates.RemoveRange(stateIndex, _savedStates.Count - stateIndex);
            WindowOrigin = saved.WindowOrigin;
            WindowExtent = saved.WindowExtent;
            ViewportOrigin = saved.ViewportOrigin;
            ViewportExtent = saved.ViewportExtent;
            CurrentPoint = saved.CurrentPoint;
            WorldTransform = saved.WorldTransform;
            FillMode = saved.FillMode;
            MapMode = saved.MapMode;
            BackgroundMode = saved.BackgroundMode;
            RasterOperation = saved.RasterOperation;
            _selectedPen = saved.SelectedPen;
            _selectedBrush = saved.SelectedBrush;
            Graphics.Restore(saved.GraphicsState);
        }

        internal void CreatePen(ReadOnlySpan<byte> payload, in MetafileRecord record)
        {
            uint index = ReadUInt32(payload, 0);
            uint style = ReadUInt32(payload, 4);
            int rawWidth = ReadInt32(payload, 8);
            if (rawWidth == int.MinValue ||
                ReadInt32(payload, 12) != 0 ||
                (style & 0xFFFF_FFF0) != 0)
            {
                throw Unsupported(record, "The initial player supports cosmetic solid or null pens only.");
            }
            int width = Math.Abs(rawWidth);

            object product = (style & 0xF) switch
            {
                0 => new Pen(ReadColor(payload, 16), Math.Max(width, 1)),
                5 => NullPenMarker.Instance,
                _ => throw Unsupported(record, "The initial player supports cosmetic solid or null pens only.")
            };
            AddObject(index, product, record);
        }

        internal void CreateBrush(ReadOnlySpan<byte> payload, in MetafileRecord record)
        {
            uint index = ReadUInt32(payload, 0);
            uint style = ReadUInt32(payload, 4);
            object product = style switch
            {
                0 => new SolidBrush(ReadColor(payload, 8)),
                1 => NullBrushMarker.Instance,
                _ => throw Unsupported(record, "The initial player supports solid or null brushes only.")
            };
            AddObject(index, product, record);
        }

        internal void CreateWmfPen(ReadOnlySpan<byte> payload, in MetafileRecord record)
        {
            uint style = ReadUInt16(payload, 0);
            int rawWidth = ReadInt16(payload, 2);
            if (rawWidth == short.MinValue ||
                (style & 0xFFF0) != 0)
            {
                throw Unsupported(record, "The initial WMF player supports cosmetic solid or null pens only.");
            }

            object product = (style & 0xF) switch
            {
                0 => new Pen(ReadColor(payload, 6), Math.Max(Math.Abs(rawWidth), 1)),
                5 => NullPenMarker.Instance,
                _ => throw Unsupported(record, "The initial WMF player supports cosmetic solid or null pens only.")
            };
            AddWmfObject(product, record);
        }

        internal void CreateWmfBrush(ReadOnlySpan<byte> payload, in MetafileRecord record)
        {
            uint style = ReadUInt16(payload, 0);
            object product = style switch
            {
                0 => new SolidBrush(ReadColor(payload, 2)),
                1 => NullBrushMarker.Instance,
                _ => throw Unsupported(record, "The initial WMF player supports solid or null brushes only.")
            };
            AddWmfObject(product, record);
        }

        internal void SelectObject(uint index, in MetafileRecord record)
        {
            object product;
            if ((index & StockObjectFlag) != 0)
            {
                product = GetStockObject(index & ~StockObjectFlag, record);
            }
            else if (!_objects.TryGetValue(index, out product!))
            {
                throw Invalid(record);
            }

            SelectProduct(product, record);
        }

        internal void SelectWmfObject(ushort index, in MetafileRecord record)
        {
            if (!_objects.TryGetValue(index, out object? product))
            {
                throw Invalid(record);
            }
            SelectProduct(product, record);
        }

        internal void DeleteWmfObject(ushort index, in MetafileRecord record) =>
            DeleteObject(index, record);

        internal void DeleteObject(uint index, in MetafileRecord record)
        {
            if ((index & StockObjectFlag) != 0 || !_objects.Remove(index, out object? product))
            {
                throw Invalid(record);
            }
            if (IsSelected(product))
            {
                _objects.Add(index, product);
                throw Invalid(record);
            }
            (product as IDisposable)?.Dispose();
        }

        private bool IsSelected(object product)
        {
            if (ReferenceEquals(product, _selectedPen) || ReferenceEquals(product, _selectedBrush))
            {
                return true;
            }

            foreach (SavedState savedState in _savedStates)
            {
                if (ReferenceEquals(product, savedState.SelectedPen) ||
                    ReferenceEquals(product, savedState.SelectedBrush))
                {
                    return true;
                }
            }
            return false;
        }

        private void AddObject(uint index, object product, in MetafileRecord record)
        {
            if (index == 0 || index > ushort.MaxValue || !_objects.TryAdd(index, product))
            {
                (product as IDisposable)?.Dispose();
                throw Invalid(record);
            }
        }

        private void AddWmfObject(object product, in MetafileRecord record)
        {
            for (uint index = 0; index < _wmfObjectCapacity; index++)
            {
                if (_objects.TryAdd(index, product))
                {
                    return;
                }
            }

            (product as IDisposable)?.Dispose();
            throw Invalid(record);
        }

        private void SelectProduct(object product, in MetafileRecord record)
        {
            switch (product)
            {
                case Pen pen:
                    _selectedPen = pen;
                    break;
                case NullPenMarker:
                    _selectedPen = null;
                    break;
                case Brush brush:
                    _selectedBrush = brush;
                    break;
                case NullBrushMarker:
                    _selectedBrush = null;
                    break;
                default:
                    throw Unsupported(record, "The selected GDI object kind is not supported.");
            }
        }

        private static object GetStockObject(uint index, in MetafileRecord record) => index switch
        {
            WhiteBrush => Brushes.White,
            LightGrayBrush => Brushes.LightGray,
            GrayBrush => Brushes.Gray,
            DarkGrayBrush => Brushes.DarkGray,
            BlackBrush => Brushes.Black,
            NullBrush => NullBrushMarker.Instance,
            WhitePen => Pens.White,
            BlackPen => Pens.Black,
            NullPen => NullPenMarker.Instance,
            _ => throw Unsupported(record, "The selected stock object kind is not supported.")
        };

        public void Dispose()
        {
            foreach (object product in _objects.Values)
            {
                (product as IDisposable)?.Dispose();
            }
            _objects.Clear();
        }

        private readonly record struct SavedState(
            Point WindowOrigin,
            Point WindowExtent,
            Point ViewportOrigin,
            Point ViewportExtent,
            Point CurrentPoint,
            Matrix3x2 WorldTransform,
            FillMode FillMode,
            int MapMode,
            int BackgroundMode,
            int RasterOperation,
            Pen? SelectedPen,
            Brush? SelectedBrush,
            GraphicsState GraphicsState);
    }

    private sealed class NullPenMarker
    {
        internal static readonly NullPenMarker Instance = new();
    }

    private sealed class NullBrushMarker
    {
        internal static readonly NullBrushMarker Instance = new();
    }
}
