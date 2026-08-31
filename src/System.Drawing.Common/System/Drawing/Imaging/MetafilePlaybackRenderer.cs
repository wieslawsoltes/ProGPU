using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Text;

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

    static MetafilePlaybackRenderer() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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

            case EmfPlusRecordType.WmfSetMapMode:
                RequireSize(record, payload, 2);
                state.SetMapMode(ReadUInt16(payload, 0), record);
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

            case EmfPlusRecordType.WmfSetTextCharExtra:
                RequireSize(record, payload, 2);
                state.TextCharacterExtra = ReadUInt16(payload, 0);
                return;

            case EmfPlusRecordType.WmfSetTextJustification:
                RequireSize(record, payload, 4);
                state.SetTextJustification(
                    ReadUInt16(payload, 2),
                    ReadUInt16(payload, 0),
                    record);
                return;

            case EmfPlusRecordType.WmfSetBkColor:
                RequireSize(record, payload, 4);
                state.BackgroundColor = ReadColor(payload, 0);
                return;

            case EmfPlusRecordType.WmfSetTextColor:
                RequireSize(record, payload, 4);
                state.TextColor = ReadColor(payload, 0);
                return;

            case EmfPlusRecordType.WmfSetWindowOrg:
                RequireSize(record, payload, 4);
                state.WindowOrigin = ReadWmfYxPoint(payload);
                return;

            case EmfPlusRecordType.WmfSetWindowExt:
                RequireSize(record, payload, 4);
                state.SetWindowExtent(ReadWmfYxPoint(payload), record);
                return;

            case EmfPlusRecordType.WmfSetViewportOrg:
                RequireSize(record, payload, 4);
                state.ViewportOrigin = ReadWmfYxPoint(payload);
                return;

            case EmfPlusRecordType.WmfSetViewportExt:
                RequireSize(record, payload, 4);
                state.SetViewportExtent(ReadWmfYxPoint(payload), record);
                return;

            case EmfPlusRecordType.WmfOffsetWindowOrg:
                RequireSize(record, payload, 4);
                state.OffsetWindowOrigin(ReadWmfYxPoint(payload), record);
                return;

            case EmfPlusRecordType.WmfOffsetViewportOrg:
                RequireSize(record, payload, 4);
                state.OffsetViewportOrigin(ReadWmfYxPoint(payload), record);
                return;

            case EmfPlusRecordType.WmfScaleWindowExt:
                RequireSize(record, payload, 8);
                state.ScaleWmfWindowExtent(payload, record);
                return;

            case EmfPlusRecordType.WmfScaleViewportExt:
                RequireSize(record, payload, 8);
                state.ScaleWmfViewportExtent(payload, record);
                return;

            case EmfPlusRecordType.WmfMoveTo:
                RequireSize(record, payload, 4);
                state.CurrentPoint = ReadWmfYxPoint(payload);
                return;

            case EmfPlusRecordType.WmfLineTo:
                RequireSize(record, payload, 4);
                Point next = ReadWmfYxPoint(payload);
                state.ApplyTransform(record);
                if (state.SelectedPen is Pen linePen)
                {
                    state.Graphics.DrawLine(linePen, state.CurrentPoint, next);
                }
                state.CurrentPoint = next;
                return;

            case EmfPlusRecordType.WmfSetPixel:
                RequireSize(record, payload, 8);
                state.ApplyTransform(record);
                state.Graphics.SetTransformedPixel(ReadColor(payload, 0), ReadWmfYxPoint(payload[4..]));
                return;

            case EmfPlusRecordType.WmfPatBlt:
                DrawWmfPatBlt(state, record, payload);
                return;

            case EmfPlusRecordType.WmfIntersectClipRect:
                state.IntersectClip(record, ReadWmfRectangle(record, payload));
                return;

            case EmfPlusRecordType.WmfExcludeClipRect:
                state.ExcludeClip(record, ReadWmfRectangle(record, payload));
                return;

            case EmfPlusRecordType.WmfOffsetCilpRgn:
                RequireSize(record, payload, 4);
                state.OffsetClip(record, ReadWmfYxPoint(payload));
                return;

            case EmfPlusRecordType.WmfSaveDC:
                RequireSize(record, payload, 0);
                state.Save();
                return;

            case EmfPlusRecordType.WmfRestoreDC:
                RequireSize(record, payload, 2);
                state.Restore(ReadInt16(payload, 0), record);
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

            case EmfPlusRecordType.WmfCreateFontIndirect:
                RequireSize(record, payload, 50);
                state.CreateWmfFont(payload, record);
                return;

            case EmfPlusRecordType.WmfTextOut:
                DrawWmfTextOut(state, record, payload);
                return;

            case EmfPlusRecordType.WmfExtTextOut:
                DrawWmfExtTextOut(state, record, payload);
                return;

            case EmfPlusRecordType.WmfPolygon:
                DrawWmfPolygon(state, record, payload, close: true);
                return;

            case EmfPlusRecordType.WmfPolyline:
                DrawWmfPolygon(state, record, payload, close: false);
                return;

            case EmfPlusRecordType.WmfPolyPolygon:
                DrawWmfPolyPolygon(state, record, payload);
                return;

            case EmfPlusRecordType.WmfArc:
                DrawWmfArcFamily(state, record, payload, WmfArcClosure.Open);
                return;

            case EmfPlusRecordType.WmfPie:
                DrawWmfArcFamily(state, record, payload, WmfArcClosure.Pie);
                return;

            case EmfPlusRecordType.WmfChord:
                DrawWmfArcFamily(state, record, payload, WmfArcClosure.Chord);
                return;

            case EmfPlusRecordType.WmfEllipse:
                DrawEllipse(state, record, ReadWmfRectangle(record, payload));
                return;

            case EmfPlusRecordType.WmfRectangle:
                DrawRectangle(state, record, ReadWmfRectangle(record, payload));
                return;

            case EmfPlusRecordType.WmfRoundRect:
                DrawWmfRoundRectangle(state, record, payload);
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

            case EmfPlusRecordType.EmfSetTextAlign:
                RequireSize(record, payload, 4);
                state.TextAlignment = ReadInt32(payload, 0);
                return;

            case EmfPlusRecordType.EmfSetTextColor:
                RequireSize(record, payload, 4);
                state.TextColor = ReadColor(payload, 0);
                return;

            case EmfPlusRecordType.EmfSetBkColor:
                RequireSize(record, payload, 4);
                state.BackgroundColor = ReadColor(payload, 0);
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

            case EmfPlusRecordType.EmfExtCreateFontIndirect:
                state.CreateEmfFont(payload, record);
                return;

            case EmfPlusRecordType.EmfExtTextOutW:
                DrawEmfExtTextOut(state, record, payload, unicode: true);
                return;

            case EmfPlusRecordType.EmfExtTextOutA:
                DrawEmfExtTextOut(state, record, payload, unicode: false);
                return;

            case EmfPlusRecordType.EmfPolyTextOutW:
                DrawEmfPolyTextOut(state, record, payload, unicode: true);
                return;

            case EmfPlusRecordType.EmfPolyTextOutA:
                DrawEmfPolyTextOut(state, record, payload, unicode: false);
                return;

            case EmfPlusRecordType.EmfSmallTextOut:
                DrawEmfSmallTextOut(state, record, payload);
                return;

            case EmfPlusRecordType.EmfSetTextJustification:
                RequireSize(record, payload, 8);
                state.SetTextJustification(
                    ReadInt32(payload, 0),
                    ReadInt32(payload, 4),
                    record);
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

    private static void DrawWmfRoundRectangle(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        RequireSize(record, payload, 12);
        int height = ReadInt16(payload, 0);
        int width = ReadInt16(payload, 2);
        int bottom = ReadInt16(payload, 4);
        int right = ReadInt16(payload, 6);
        int top = ReadInt16(payload, 8);
        int left = ReadInt16(payload, 10);
        if (right <= left || bottom <= top)
        {
            throw Invalid(record);
        }

        var rectangle = Rectangle.FromLTRB(left, top, right, bottom);
        var cornerEllipse = new Size(width, height);
        state.ApplyTransform(record);
        if (state.SelectedBrush is Brush brush)
        {
            state.Graphics.FillRoundedRectangle(brush, rectangle, cornerEllipse);
        }
        if (state.SelectedPen is Pen pen)
        {
            state.Graphics.DrawRoundedRectangle(pen, rectangle, cornerEllipse);
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

    private static void DrawWmfPolyPolygon(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            throw Invalid(record);
        }

        int polygonCount = ReadUInt16(payload, 0);
        if (polygonCount == 0)
        {
            throw Invalid(record);
        }

        int pointsOffset;
        try
        {
            pointsOffset = checked(2 + polygonCount * 2);
        }
        catch (OverflowException exception)
        {
            throw Invalid(record, exception);
        }
        if (payload.Length < pointsOffset)
        {
            throw Invalid(record);
        }

        int pointCount = 0;
        for (int polygonIndex = 0; polygonIndex < polygonCount; polygonIndex++)
        {
            int currentCount = ReadUInt16(payload, 2 + polygonIndex * 2);
            if (currentCount < 2)
            {
                throw Invalid(record);
            }

            try
            {
                pointCount = checked(pointCount + currentCount);
            }
            catch (OverflowException exception)
            {
                throw Invalid(record, exception);
            }
        }

        int expectedSize;
        try
        {
            expectedSize = checked(pointsOffset + pointCount * 4);
        }
        catch (OverflowException exception)
        {
            throw Invalid(record, exception);
        }
        RequireSize(record, payload, expectedSize);

        state.ApplyTransform(record);
        int cursor = pointsOffset;
        for (int polygonIndex = 0; polygonIndex < polygonCount; polygonIndex++)
        {
            int currentCount = ReadUInt16(payload, 2 + polygonIndex * 2);
            var points = new Point[currentCount];
            for (int pointIndex = 0; pointIndex < currentCount; pointIndex++)
            {
                points[pointIndex] = new Point(
                    ReadInt16(payload, cursor),
                    ReadInt16(payload, cursor + 2));
                cursor += 4;
            }

            if (currentCount >= 3 && state.SelectedBrush is Brush brush)
            {
                state.Graphics.FillPolygon(brush, points, state.FillMode);
            }
            if (state.SelectedPen is Pen pen)
            {
                if (currentCount >= 3)
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

    private static void DrawWmfPatBlt(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        RequireSize(record, payload, 12);
        uint rasterOperation = ReadUInt32(payload, 0);
        int height = ReadInt16(payload, 4);
        int width = ReadInt16(payload, 6);
        int y = ReadInt16(payload, 8);
        int x = ReadInt16(payload, 10);
        if (width <= 0 || height <= 0)
        {
            throw Invalid(record);
        }

        Brush? brush = rasterOperation switch
        {
            0x0000_0042 => Brushes.Black,
            0x00F0_0021 => state.SelectedBrush,
            0x00FF_0062 => Brushes.White,
            _ => throw Unsupported(
                record,
                $"Ternary raster operation 0x{rasterOperation:X8} requires destination-dependent compositing.")
        };
        if (brush is null)
        {
            return;
        }

        state.ApplyTransform(record);
        state.Graphics.FillRectangle(brush, x, y, width, height);
    }

    private static void DrawWmfTextOut(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6)
        {
            throw Invalid(record);
        }

        int stringLength = ReadInt16(payload, 0);
        if (stringLength < 0)
        {
            throw Invalid(record);
        }

        int paddedLength;
        int expectedSize;
        try
        {
            paddedLength = checked((stringLength + 1) & ~1);
            expectedSize = checked(2 + paddedLength + 4);
        }
        catch (OverflowException exception)
        {
            throw Invalid(record, exception);
        }
        RequireSize(record, payload, expectedSize);

        string text = DecodeWmfText(
            state.SelectedFont.GdiCharSet,
            record,
            payload.Slice(2, stringLength),
            out _);

        var referencePoint = new Point(
            ReadInt16(payload, 2 + paddedLength + 2),
            ReadInt16(payload, 2 + paddedLength));
        state.DrawText(record, text, referencePoint);
    }

    private static void DrawWmfExtTextOut(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        const ushort EtoOpaque = 0x0002;
        const ushort EtoClipped = 0x0004;
        const ushort EtoRtlReading = 0x0080;
        const ushort SupportedOptions = EtoOpaque | EtoClipped | EtoRtlReading;
        if (payload.Length < 8)
        {
            throw Invalid(record);
        }

        int stringLength = ReadInt16(payload, 4);
        ushort options = ReadUInt16(payload, 6);
        if (stringLength < 0)
        {
            throw Invalid(record);
        }
        if ((options & ~SupportedOptions) != 0)
        {
            throw Unsupported(
                record,
                $"EXTTEXTOUT options 0x{options:X4} require glyph-index, numeric-substitution, or two-dimensional advance support.");
        }

        bool hasRectangle = (options & (EtoOpaque | EtoClipped)) != 0;
        int stringOffset = hasRectangle ? 16 : 8;
        int paddedLength;
        int baseSize;
        int dxSize;
        try
        {
            paddedLength = checked((stringLength + 1) & ~1);
            baseSize = checked(stringOffset + paddedLength);
            dxSize = checked(stringLength * 2);
        }
        catch (OverflowException exception)
        {
            throw Invalid(record, exception);
        }
        if (payload.Length != baseSize && payload.Length != checked(baseSize + dxSize))
        {
            throw Invalid(record);
        }

        Rectangle rectangle = Rectangle.Empty;
        if (hasRectangle)
        {
            int left = ReadInt16(payload, 8);
            int top = ReadInt16(payload, 10);
            int right = ReadInt16(payload, 12);
            int bottom = ReadInt16(payload, 14);
            if (right < left || bottom < top)
            {
                throw Invalid(record);
            }
            rectangle = Rectangle.FromLTRB(left, top, right, bottom);
        }

        string text = DecodeWmfText(
            state.SelectedFont.GdiCharSet,
            record,
            payload.Slice(stringOffset, stringLength),
            out Encoding encoding);
        ReadOnlySpan<byte> encodedDx = payload.Length == baseSize
            ? default
            : payload.Slice(baseSize, dxSize);
        if (!encodedDx.IsEmpty && (!encoding.IsSingleByte || text.Length != stringLength))
        {
            throw Unsupported(
                record,
                "Per-character WMF advances currently require a one-byte charset with one UTF-16 code unit per input byte.");
        }

        scoped Span<int> advances = encodedDx.IsEmpty
            ? default
            : stringLength <= 256
                ? stackalloc int[stringLength]
                : new int[stringLength];
        for (int index = 0; index < advances.Length; index++)
        {
            advances[index] = ReadInt16(encodedDx, index * 2);
        }

        state.DrawExtendedText(
            record,
            text,
            new Point(ReadInt16(payload, 2), ReadInt16(payload, 0)),
            rectangle,
            opaque: (options & EtoOpaque) != 0,
            clipped: (options & EtoClipped) != 0,
            rightToLeft: (options & EtoRtlReading) != 0,
            advances);
    }

    private static void DrawEmfExtTextOut(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        bool unicode)
    {
        const int EmrTextOffset = 28;
        const int EmrTextSize = 40;

        ValidateEmfTextHeader(record, payload, EmrTextOffset + EmrTextSize);
        DrawEmfText(
            state,
            record,
            payload,
            EmrTextOffset,
            EmrTextOffset + EmrTextSize,
            unicode);
    }

    private static void DrawEmfPolyTextOut(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        bool unicode)
    {
        const int EmrTextArrayOffset = 32;
        const int EmrTextSize = 40;

        ValidateEmfTextHeader(record, payload, EmrTextArrayOffset);
        uint stringCountValue = ReadUInt32(payload, 28);
        if (stringCountValue > 1_000_000)
        {
            throw Invalid(record);
        }

        int stringCount;
        int dataOffset;
        try
        {
            stringCount = checked((int)stringCountValue);
            dataOffset = checked(EmrTextArrayOffset + stringCount * EmrTextSize);
        }
        catch (OverflowException exception)
        {
            throw Invalid(record, exception);
        }
        if (dataOffset > payload.Length)
        {
            throw Invalid(record);
        }

        for (int index = 0; index < stringCount; index++)
        {
            DrawEmfText(
                state,
                record,
                payload,
                EmrTextArrayOffset + index * EmrTextSize,
                dataOffset,
                unicode);
        }
    }

    private static void ValidateEmfTextHeader(
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        int minimumSize)
    {
        if (payload.Length < minimumSize)
        {
            throw Invalid(record);
        }

        uint graphicsMode = ReadUInt32(payload, 16);
        float xScale = ReadSingle(payload, 20);
        float yScale = ReadSingle(payload, 24);
        if (graphicsMode is not 1 and not 2 ||
            (graphicsMode == 1 &&
             (!float.IsFinite(xScale) || !float.IsFinite(yScale) ||
              xScale <= 0f || yScale <= 0f)))
        {
            throw Invalid(record);
        }
    }

    private static void DrawEmfSmallTextOut(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        const uint EtoOpaque = 0x0000_0002;
        const uint EtoClipped = 0x0000_0004;
        const uint EtoRtlReading = 0x0000_0080;
        const uint EtoNoRect = 0x0000_0100;
        const uint EtoSmallChars = 0x0000_0200;
        const uint SupportedOptions =
            EtoOpaque | EtoClipped | EtoRtlReading | EtoNoRect | EtoSmallChars;
        const int FixedPayloadSize = 28;
        const int BoundsSize = 16;

        ValidateEmfTextHeader(record, payload, FixedPayloadSize);
        uint characterCountValue = ReadUInt32(payload, 8);
        uint options = ReadUInt32(payload, 12);
        if (characterCountValue > 1_000_000 || (options & ~SupportedOptions) != 0)
        {
            if ((options & ~SupportedOptions) != 0)
            {
                throw Unsupported(
                    record,
                    $"SMALLTEXTOUT options 0x{options:X8} require glyph-index, numeric-substitution, language, or two-dimensional text support.");
            }
            throw Invalid(record);
        }

        bool hasRectangle = (options & EtoNoRect) == 0;
        if (!hasRectangle && (options & (EtoOpaque | EtoClipped)) != 0)
        {
            throw Invalid(record);
        }

        int characterCount;
        int textOffset = FixedPayloadSize + (hasRectangle ? BoundsSize : 0);
        int textSize;
        try
        {
            characterCount = checked((int)characterCountValue);
            textSize = checked(characterCount *
                ((options & EtoSmallChars) != 0 ? 1 : 2));
        }
        catch (OverflowException exception)
        {
            throw Invalid(record, exception);
        }
        if (textOffset > payload.Length - textSize)
        {
            throw Invalid(record);
        }

        string text;
        ReadOnlySpan<byte> encodedText = payload.Slice(textOffset, textSize);
        if ((options & EtoSmallChars) != 0)
        {
            text = Encoding.Latin1.GetString(encodedText);
        }
        else
        {
            try
            {
                text = Encoding.GetEncoding(
                    1200,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(encodedText);
            }
            catch (DecoderFallbackException exception)
            {
                throw Invalid(record, exception);
            }
        }

        Rectangle rectangle = Rectangle.Empty;
        if (hasRectangle)
        {
            int left = ReadInt32(payload, FixedPayloadSize);
            int top = ReadInt32(payload, FixedPayloadSize + 4);
            int right = ReadInt32(payload, FixedPayloadSize + 8);
            int bottom = ReadInt32(payload, FixedPayloadSize + 12);
            if (right < left || bottom < top)
            {
                throw Invalid(record);
            }
            rectangle = Rectangle.FromLTRB(left, top, right, bottom);
        }

        state.DrawExtendedText(
            record,
            text,
            new Point(ReadInt32(payload, 0), ReadInt32(payload, 4)),
            rectangle,
            opaque: (options & EtoOpaque) != 0,
            clipped: (options & EtoClipped) != 0,
            rightToLeft: (options & EtoRtlReading) != 0,
            default);
    }

    private static void DrawEmfText(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        int emrTextOffset,
        int minimumDataOffset,
        bool unicode)
    {
        const uint EtoOpaque = 0x0000_0002;
        const uint EtoClipped = 0x0000_0004;
        const uint EtoGlyphIndex = 0x0000_0010;
        const uint EtoRtlReading = 0x0000_0080;
        const uint EtoIgnoreLanguage = 0x0000_1000;
        const uint EtoPdy = 0x0000_2000;
        const uint SupportedOptions =
            EtoOpaque | EtoClipped | EtoGlyphIndex | EtoRtlReading |
            EtoIgnoreLanguage | EtoPdy;
        const int RecordHeaderSize = 8;

        int referenceX = ReadInt32(payload, emrTextOffset);
        int referenceY = ReadInt32(payload, emrTextOffset + 4);
        uint characterCountValue = ReadUInt32(payload, emrTextOffset + 8);
        uint stringOffsetValue = ReadUInt32(payload, emrTextOffset + 12);
        uint options = ReadUInt32(payload, emrTextOffset + 16);
        uint advancesOffsetValue = ReadUInt32(payload, emrTextOffset + 36);
        if (characterCountValue > 1_000_000 || (options & ~SupportedOptions) != 0)
        {
            if ((options & ~SupportedOptions) != 0)
            {
                throw Unsupported(
                    record,
                    $"EXTTEXTOUT options 0x{options:X8} require numeric-substitution, small-character, reverse-index-map, or other unsupported text semantics.");
            }
            throw Invalid(record);
        }

        int characterCount;
        int stringOffset;
        int stringSize;
        try
        {
            characterCount = checked((int)characterCountValue);
            stringOffset = characterCount == 0 && stringOffsetValue == 0
                ? minimumDataOffset
                : checked((int)stringOffsetValue - RecordHeaderSize);
            stringSize = checked(characterCount * (unicode ? 2 : 1));
        }
        catch (OverflowException exception)
        {
            throw Invalid(record, exception);
        }
        if ((unicode && (stringOffsetValue & 1) != 0) ||
            stringOffset < minimumDataOffset ||
            stringOffset > payload.Length - stringSize)
        {
            throw Invalid(record);
        }

        string text = string.Empty;
        Encoding? ansiEncoding = null;
        scoped Span<ushort> glyphIndices = default;
        if ((options & EtoGlyphIndex) != 0)
        {
            if (!unicode)
            {
                throw Unsupported(
                    record,
                    "ANSI EMF glyph-index records require a separately specified 16-bit storage contract.");
            }
            glyphIndices = characterCount <= 256
                ? stackalloc ushort[characterCount]
                : new ushort[characterCount];
            for (int index = 0; index < glyphIndices.Length; index++)
            {
                glyphIndices[index] = ReadUInt16(payload, stringOffset + index * 2);
            }
        }
        else if (unicode)
        {
            try
            {
                text = Encoding.GetEncoding(
                    1200,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(
                        payload.Slice(stringOffset, stringSize));
            }
            catch (DecoderFallbackException exception)
            {
                throw Invalid(record, exception);
            }
        }
        else
        {
            text = DecodeWmfText(
                state.SelectedFont.GdiCharSet,
                record,
                payload.Slice(stringOffset, stringSize),
                out ansiEncoding);
        }

        scoped Span<int> advances = default;
        scoped Span<int> verticalAdvances = default;
        if (advancesOffsetValue != 0)
        {
            if (!unicode &&
                (ansiEncoding is null || !ansiEncoding.IsSingleByte ||
                 text.Length != characterCount))
            {
                throw Unsupported(
                    record,
                    "Per-character ANSI EMF advances currently require a one-byte charset with one UTF-16 code unit per input byte.");
            }

            int advancesOffset;
            int advancesSize;
            try
            {
                advancesOffset = checked((int)advancesOffsetValue - RecordHeaderSize);
                advancesSize = checked(characterCount * 4 *
                    ((options & EtoPdy) != 0 ? 2 : 1));
            }
            catch (OverflowException exception)
            {
                throw Invalid(record, exception);
            }
            if ((advancesOffsetValue & 3) != 0 ||
                advancesOffset < minimumDataOffset ||
                advancesOffset > payload.Length - advancesSize)
            {
                throw Invalid(record);
            }

            advances = characterCount <= 256
                ? stackalloc int[characterCount]
                : new int[characterCount];
            if ((options & EtoPdy) != 0)
            {
                verticalAdvances = characterCount <= 256
                    ? stackalloc int[characterCount]
                    : new int[characterCount];
            }
            for (int index = 0; index < advances.Length; index++)
            {
                int elementOffset = advancesOffset + index *
                    (verticalAdvances.IsEmpty ? 4 : 8);
                uint advance = ReadUInt32(payload, elementOffset);
                if (advance > int.MaxValue)
                {
                    throw Invalid(record);
                }
                advances[index] = (int)advance;
                if (!verticalAdvances.IsEmpty)
                {
                    uint verticalAdvance = ReadUInt32(payload, elementOffset + 4);
                    if (verticalAdvance > int.MaxValue)
                    {
                        throw Invalid(record);
                    }
                    verticalAdvances[index] = (int)verticalAdvance;
                }
            }

            int stringEnd = checked(stringOffset + stringSize);
            int advancesEnd = checked(advancesOffset + advancesSize);
            if (stringOffset < advancesEnd && advancesOffset < stringEnd)
            {
                throw Invalid(record);
            }
        }

        if ((options & EtoIgnoreLanguage) != 0)
        {
            if (glyphIndices.IsEmpty)
            {
                bool hasNonAsciiText = false;
                foreach (char character in text)
                {
                    if (character > 0x7F)
                    {
                        hasNonAsciiText = true;
                        break;
                    }
                }
                if (advances.IsEmpty || hasNonAsciiText)
                {
                    throw Unsupported(
                        record,
                        "ETO_IGNORELANGUAGE requires explicit advances and currently supports ASCII text only.");
                }
            }
        }

        Rectangle rectangle = Rectangle.Empty;
        if ((options & (EtoOpaque | EtoClipped)) != 0)
        {
            int left = ReadInt32(payload, emrTextOffset + 20);
            int top = ReadInt32(payload, emrTextOffset + 24);
            int right = ReadInt32(payload, emrTextOffset + 28);
            int bottom = ReadInt32(payload, emrTextOffset + 32);
            if (right < left || bottom < top)
            {
                throw Invalid(record);
            }
            rectangle = Rectangle.FromLTRB(left, top, right, bottom);
        }

        if (!glyphIndices.IsEmpty)
        {
            state.DrawGlyphIndexText(
                record,
                glyphIndices,
                new Point(referenceX, referenceY),
                rectangle,
                opaque: (options & EtoOpaque) != 0,
                clipped: (options & EtoClipped) != 0,
                advances,
                verticalAdvances);
        }
        else
        {
            state.DrawExtendedText(
                record,
                text,
                new Point(referenceX, referenceY),
                rectangle,
                opaque: (options & EtoOpaque) != 0,
                clipped: (options & EtoClipped) != 0,
                rightToLeft: (options & EtoRtlReading) != 0,
                advances,
                verticalAdvances);
        }
    }

    private static string DecodeWmfText(
        byte charSet,
        in MetafileRecord record,
        ReadOnlySpan<byte> bytes,
        out Encoding encoding)
    {
        encoding = GetWmfEncoding(charSet, record);
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw Invalid(record, exception);
        }
    }

    private static Encoding GetWmfEncoding(byte charSet, in MetafileRecord record)
    {
        int codePage = charSet switch
        {
            0 or 1 => 1252,
            2 => throw Unsupported(record, "SYMBOL_CHARSET needs glyph-index mapping."),
            128 => 932,
            129 => 949,
            130 => 1361,
            134 => 936,
            136 => 950,
            161 => 1253,
            162 => 1254,
            163 => 1258,
            177 => 1255,
            178 => 1256,
            186 => 1257,
            204 => 1251,
            222 => 874,
            238 => 1250,
            255 => 437,
            _ => throw Unsupported(record, $"Font charset {charSet} has no defined WMF code page.")
        };
        return Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private static void DrawWmfArcFamily(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        WmfArcClosure closure)
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
        if (closure == WmfArcClosure.Open)
        {
            if (state.SelectedPen is Pen openPen)
            {
                state.Graphics.DrawArc(openPen, rectangle, startAngle, sweepAngle);
            }
            return;
        }

        if (state.SelectedBrush is null && state.SelectedPen is null)
        {
            return;
        }

        using var path = new GraphicsPath();
        if (closure == WmfArcClosure.Pie)
        {
            path.AddPie(rectangle, startAngle, sweepAngle);
        }
        else
        {
            path.AddArc(rectangle, startAngle, sweepAngle);
            path.CloseFigure();
        }

        if (state.SelectedBrush is Brush brush)
        {
            state.Graphics.FillPath(brush, path);
        }
        if (state.SelectedPen is Pen pen)
        {
            state.Graphics.DrawPath(pen, path);
        }
    }

    private enum WmfArcClosure
    {
        Open,
        Pie,
        Chord
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

    private static Point ScaleWmfExtent(
        Point extent,
        ReadOnlySpan<byte> payload,
        in MetafileRecord record)
    {
        int yDenominator = ReadInt16(payload, 0);
        int yNumerator = ReadInt16(payload, 2);
        int xDenominator = ReadInt16(payload, 4);
        int xNumerator = ReadInt16(payload, 6);
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
        private Font _selectedFont = SystemFonts.DefaultFont;
        private object? _selectedFontObject;
        private int _selectedFontEscapement;
        private SolidBrush? _textBrush;
        private SolidBrush? _backgroundBrush;

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
        internal int TextCharacterExtra { get; set; }
        internal int TextJustificationExtra { get; private set; }
        internal int TextJustificationBreakCount { get; private set; }
        internal int TextJustificationError { get; private set; }
        internal Color BackgroundColor { get; set; } = Color.White;
        internal Color TextColor { get; set; } = Color.Black;
        internal Pen? SelectedPen => _selectedPen;
        internal Brush? SelectedBrush => _selectedBrush;
        internal Font SelectedFont => _selectedFont;

        internal Matrix3x2 ApplyTransform(in MetafileRecord record)
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
            return combined;
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

        internal void OffsetWindowOrigin(Point offset, in MetafileRecord record)
        {
            try
            {
                WindowOrigin = new Point(
                    checked(WindowOrigin.X + offset.X),
                    checked(WindowOrigin.Y + offset.Y));
            }
            catch (OverflowException exception)
            {
                throw Invalid(record, exception);
            }
        }

        internal void OffsetViewportOrigin(Point offset, in MetafileRecord record)
        {
            try
            {
                ViewportOrigin = new Point(
                    checked(ViewportOrigin.X + offset.X),
                    checked(ViewportOrigin.Y + offset.Y));
            }
            catch (OverflowException exception)
            {
                throw Invalid(record, exception);
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

        internal void ScaleWmfWindowExtent(ReadOnlySpan<byte> payload, in MetafileRecord record)
        {
            if (MapMode == 8)
            {
                WindowExtent = ScaleWmfExtent(WindowExtent, payload, record);
                ValidateExtents(record);
            }
        }

        internal void ScaleWmfViewportExtent(ReadOnlySpan<byte> payload, in MetafileRecord record)
        {
            if (MapMode == 8)
            {
                ViewportExtent = ScaleWmfExtent(ViewportExtent, payload, record);
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

        internal void ExcludeClip(in MetafileRecord record, Rectangle rectangle)
        {
            ApplyTransform(record);
            Graphics.ExcludeClip(rectangle);
        }

        internal void OffsetClip(in MetafileRecord record, Point offset)
        {
            ApplyTransform(record);
            Graphics.TranslateClip(offset.X, offset.Y);
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
                TextAlignment,
                TextCharacterExtra,
                TextJustificationExtra,
                TextJustificationBreakCount,
                TextJustificationError,
                BackgroundColor,
                TextColor,
                _selectedPen,
                _selectedBrush,
                _selectedFont,
                _selectedFontObject,
                _selectedFontEscapement,
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
            TextAlignment = saved.TextAlignment;
            TextCharacterExtra = saved.TextCharacterExtra;
            TextJustificationExtra = saved.TextJustificationExtra;
            TextJustificationBreakCount = saved.TextJustificationBreakCount;
            TextJustificationError = saved.TextJustificationError;
            BackgroundColor = saved.BackgroundColor;
            TextColor = saved.TextColor;
            _selectedPen = saved.SelectedPen;
            _selectedBrush = saved.SelectedBrush;
            _selectedFont = saved.SelectedFont;
            _selectedFontObject = saved.SelectedFontObject;
            _selectedFontEscapement = saved.SelectedFontEscapement;
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

        internal void CreateWmfFont(ReadOnlySpan<byte> payload, in MetafileRecord record)
        {
            int height = ReadInt16(payload, 0);
            int width = ReadInt16(payload, 2);
            int escapement = ReadInt16(payload, 4);
            int orientation = ReadInt16(payload, 6);
            int weight = ReadInt16(payload, 8);
            bool italic = payload[10] != 0;
            bool underline = payload[11] != 0;
            bool strikeout = payload[12] != 0;
            byte charSet = payload[13];
            if (height is 0 or short.MinValue || width != 0 ||
                escapement != orientation || weight is < 0 or > 1000)
            {
                throw Unsupported(
                    record,
                    "The typed WMF text path supports nonzero unscaled compatible-mode fonts whose escapement and orientation match.");
            }

            Encoding encoding = GetWmfEncoding(charSet, record);
            int terminator = payload[18..50].IndexOf((byte)0);
            ReadOnlySpan<byte> faceBytes = terminator < 0
                ? payload[18..50]
                : payload.Slice(18, terminator);
            string faceName;
            try
            {
                faceName = encoding.GetString(faceBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw Invalid(record, exception);
            }
            bool vertical = faceName.StartsWith('@');
            if (vertical)
            {
                throw Unsupported(record, "Vertical WMF fonts require a typed vertical-layout path.");
            }
            if (faceName.Length == 0)
            {
                faceName = SystemFonts.DefaultFont.Name;
            }

            FontStyle style = (weight >= 700 ? FontStyle.Bold : FontStyle.Regular) |
                (italic ? FontStyle.Italic : FontStyle.Regular) |
                (underline ? FontStyle.Underline : FontStyle.Regular) |
                (strikeout ? FontStyle.Strikeout : FontStyle.Regular);
            float rawSize = Math.Abs(height);
            Font font = new(faceName, rawSize, style, GraphicsUnit.Pixel, charSet, false);
            if (height > 0)
            {
                int lineUnits = font.TtfFont.Ascender - font.TtfFont.Descender + font.TtfFont.LineGap;
                if (font.TtfFont.UnitsPerEm <= 0 || lineUnits <= 0)
                {
                    font.Dispose();
                    throw Unsupported(record, "The selected font does not expose cell-height metrics.");
                }

                float emSize = rawSize * font.TtfFont.UnitsPerEm / lineUnits;
                font.Dispose();
                font = new Font(faceName, emSize, style, GraphicsUnit.Pixel, charSet, false);
            }
            AddWmfObject(new WmfFontObject(font, escapement), record);
        }

        internal void CreateEmfFont(ReadOnlySpan<byte> payload, in MetafileRecord record)
        {
            const int ObjectIndexSize = 4;
            const int LogFontSize = 92;
            const int LogFontPanoseSize = 320;
            const int LogFontExMinimumSize = 356;
            const int LogFontExMaximumSize = 420;
            int logicalFontSize = payload.Length - ObjectIndexSize;
            if (logicalFontSize != LogFontSize &&
                logicalFontSize != LogFontPanoseSize &&
                (logicalFontSize < LogFontExMinimumSize ||
                 logicalFontSize > LogFontExMaximumSize))
            {
                throw Invalid(record);
            }

            uint index = ReadUInt32(payload, 0);
            ReadOnlySpan<byte> logicalFont = payload[ObjectIndexSize..];
            int height = ReadInt32(logicalFont, 0);
            int width = ReadInt32(logicalFont, 4);
            int escapement = ReadInt32(logicalFont, 8);
            int orientation = ReadInt32(logicalFont, 12);
            int weight = ReadInt32(logicalFont, 16);
            bool italic = logicalFont[20] != 0;
            bool underline = logicalFont[21] != 0;
            bool strikeout = logicalFont[22] != 0;
            byte charSet = logicalFont[23];
            if (height is 0 or int.MinValue || Math.Abs((long)height) > 1_000_000 || width != 0 ||
                escapement != orientation || weight is < 0 or > 1000)
            {
                throw Unsupported(
                    record,
                    "The typed EMF text path supports nonzero logical fonts with computed width whose escapement and orientation match.");
            }

            ReadOnlySpan<byte> faceBytes = logicalFont.Slice(28, 64);
            int terminator = -1;
            for (int offset = 0; offset < faceBytes.Length; offset += 2)
            {
                if (ReadUInt16(faceBytes, offset) == 0)
                {
                    terminator = offset;
                    break;
                }
            }
            if (terminator >= 0)
            {
                faceBytes = faceBytes[..terminator];
            }

            string faceName;
            try
            {
                faceName = Encoding.GetEncoding(
                    1200,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(faceBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw Invalid(record, exception);
            }
            if (faceName.StartsWith('@'))
            {
                throw Unsupported(record, "Vertical EMF fonts require a typed vertical-layout path.");
            }
            if (faceName.Length == 0)
            {
                faceName = SystemFonts.DefaultFont.Name;
            }

            FontStyle style = (weight >= 700 ? FontStyle.Bold : FontStyle.Regular) |
                (italic ? FontStyle.Italic : FontStyle.Regular) |
                (underline ? FontStyle.Underline : FontStyle.Regular) |
                (strikeout ? FontStyle.Strikeout : FontStyle.Regular);
            float rawSize = Math.Abs((float)height);
            Font font = new(faceName, rawSize, style, GraphicsUnit.Pixel, charSet, false);
            if (height > 0)
            {
                int lineUnits = font.TtfFont.Ascender - font.TtfFont.Descender + font.TtfFont.LineGap;
                if (font.TtfFont.UnitsPerEm <= 0 || lineUnits <= 0)
                {
                    font.Dispose();
                    throw Unsupported(record, "The selected font does not expose cell-height metrics.");
                }

                float emSize = rawSize * font.TtfFont.UnitsPerEm / lineUnits;
                font.Dispose();
                font = new Font(faceName, emSize, style, GraphicsUnit.Pixel, charSet, false);
            }
            AddObject(index, new WmfFontObject(font, escapement), record);
        }

        internal void DrawText(in MetafileRecord record, string text, Point recordPoint) =>
            DrawTextCore(
                record,
                text,
                recordPoint,
                Rectangle.Empty,
                opaque: false,
                clipped: false,
                rightToLeft: false,
                default);

        internal void SetTextJustification(
            int extra,
            int breakCount,
            in MetafileRecord record)
        {
            if (breakCount < 0)
            {
                throw Invalid(record);
            }
            TextJustificationExtra = extra;
            TextJustificationBreakCount = breakCount;
            TextJustificationError = 0;
        }

        internal void DrawExtendedText(
            in MetafileRecord record,
            string text,
            Point recordPoint,
            Rectangle rectangle,
            bool opaque,
            bool clipped,
            bool rightToLeft,
            ReadOnlySpan<int> advances,
            ReadOnlySpan<int> verticalAdvances = default) =>
            DrawTextCore(
                record,
                text,
                recordPoint,
                rectangle,
                opaque,
                clipped,
                rightToLeft,
                advances,
                verticalAdvances);

        internal void DrawGlyphIndexText(
            in MetafileRecord record,
            ReadOnlySpan<ushort> glyphIndices,
            Point recordPoint,
            Rectangle rectangle,
            bool opaque,
            bool clipped,
            ReadOnlySpan<int> advances,
            ReadOnlySpan<int> verticalAdvances)
        {
            const int SupportedAlignmentMask = 0x011F;
            if ((TextAlignment & ~SupportedAlignmentMask) != 0 ||
                (TextAlignment & 0x0006) == 0x0004 ||
                (TextAlignment & 0x0018) == 0x0010 ||
                (!advances.IsEmpty && advances.Length != glyphIndices.Length) ||
                (!verticalAdvances.IsEmpty && verticalAdvances.Length != glyphIndices.Length))
            {
                throw Invalid(record);
            }
            if (!verticalAdvances.IsEmpty &&
                (_selectedFont.Style & (FontStyle.Underline | FontStyle.Strikeout)) != 0)
            {
                throw Unsupported(
                    record,
                    "Glyph-index text with two-dimensional cells requires per-cell decoration geometry.");
            }

            float totalX = 0f;
            float totalY = 0f;
            float minimumX = 0f;
            float maximumX = 0f;
            float minimumY = 0f;
            float maximumY = 0f;
            scoped Span<Vector2> vectorAdvances = glyphIndices.Length <= 256
                ? stackalloc Vector2[glyphIndices.Length]
                : new Vector2[glyphIndices.Length];
            if (advances.IsEmpty)
            {
                float fontSize = Graphics.ConvertFontSizeToPixels(
                    _selectedFont.Size,
                    _selectedFont.Unit,
                    Graphics.DpiY);
                for (int index = 0; index < glyphIndices.Length; index++)
                {
                    vectorAdvances[index] = new Vector2(
                        _selectedFont.TtfFont.GetAdvanceWidth(glyphIndices[index], fontSize),
                        0f);
                }
            }
            else
            {
                for (int index = 0; index < advances.Length; index++)
                {
                    vectorAdvances[index] = new Vector2(
                        advances[index],
                        verticalAdvances.IsEmpty ? 0 : verticalAdvances[index]);
                }
            }
            for (int index = 0; index < vectorAdvances.Length; index++)
            {
                totalX += vectorAdvances[index].X;
                totalY += vectorAdvances[index].Y;
                minimumX = Math.Min(minimumX, totalX);
                maximumX = Math.Max(maximumX, totalX);
                minimumY = Math.Min(minimumY, totalY);
                maximumY = Math.Max(maximumY, totalY);
            }
            if (!float.IsFinite(totalX) || !float.IsFinite(totalY) ||
                totalX < int.MinValue || totalX > int.MaxValue ||
                totalY < int.MinValue || totalY > int.MaxValue)
            {
                throw Invalid(record);
            }

            SizeF measuredSize = Graphics.MeasureString("M", _selectedFont);
            PointF reference = (TextAlignment & 0x0001) != 0
                ? CurrentPoint
                : recordPoint;
            Matrix3x2 baseTransform = ApplyTransform(record);
            float x = reference.X - ((TextAlignment & 0x0006) switch
            {
                0x0002 => totalX,
                0x0006 => totalX / 2f,
                _ => 0f
            });
            float y = reference.Y - ((TextAlignment & 0x0018) switch
            {
                0x0008 => measuredSize.Height,
                0x0018 => _selectedFont.TtfFont.UnitsPerEm == 0
                    ? 0f
                    : Graphics.ConvertFontSizeToPixels(
                        _selectedFont.Size,
                        _selectedFont.Unit,
                        Graphics.DpiY) * _selectedFont.TtfFont.Ascender /
                        _selectedFont.TtfFont.UnitsPerEm,
                _ => 0f
            });

            Matrix3x2 textTransform = CreateTextTransform(baseTransform, reference);
            GraphicsState? clippingState = null;
            if (clipped)
            {
                clippingState = Graphics.Save();
                Graphics.IntersectClip(rectangle);
            }
            try
            {
                if (opaque && rectangle.Width > 0 && rectangle.Height > 0)
                {
                    Graphics.FillRectangle(GetBackgroundBrush(), rectangle);
                }
                Graphics.TransformElements = textTransform;
                try
                {
                    if (!opaque && BackgroundMode == 2 &&
                        maximumX > minimumX && measuredSize.Height + maximumY - minimumY > 0)
                    {
                        Graphics.FillRectangle(
                            GetBackgroundBrush(),
                            x + minimumX,
                            y + minimumY,
                            maximumX - minimumX,
                            measuredSize.Height + maximumY - minimumY);
                    }
                    Graphics.DrawGlyphIndicesWithCharacterAdvances(
                        glyphIndices,
                        _selectedFont,
                        GetTextBrush(),
                        x,
                        y,
                        vectorAdvances);
                }
                finally
                {
                    Graphics.TransformElements = baseTransform;
                }
            }
            finally
            {
                if (clippingState is not null)
                {
                    Graphics.Restore(clippingState);
                }
            }

            if ((TextAlignment & 0x0001) != 0)
            {
                Vector2 deviceEnd = Vector2.Transform(
                    new Vector2(reference.X + totalX, reference.Y + totalY),
                    textTransform);
                if (!Matrix3x2.Invert(baseTransform, out Matrix3x2 inverseBase))
                {
                    throw Invalid(record);
                }
                Vector2 logicalEnd = Vector2.Transform(deviceEnd, inverseBase);
                if (!float.IsFinite(logicalEnd.X) || !float.IsFinite(logicalEnd.Y) ||
                    logicalEnd.X < int.MinValue || logicalEnd.X > int.MaxValue ||
                    logicalEnd.Y < int.MinValue || logicalEnd.Y > int.MaxValue)
                {
                    throw Invalid(record);
                }
                CurrentPoint = Point.Round(new PointF(logicalEnd.X, logicalEnd.Y));
            }
        }

        private void DrawTextCore(
            in MetafileRecord record,
            string text,
            Point recordPoint,
            Rectangle rectangle,
            bool opaque,
            bool clipped,
            bool rightToLeft,
            ReadOnlySpan<int> advances,
            ReadOnlySpan<int> verticalAdvances = default)
        {
            const int SupportedAlignmentMask = 0x011F;
            if ((TextAlignment & ~SupportedAlignmentMask) != 0 ||
                (TextAlignment & 0x0006) == 0x0004 ||
                (TextAlignment & 0x0018) == 0x0010)
            {
                throw Unsupported(record, $"Text alignment 0x{TextAlignment:X4} is not valid.");
            }

            bool effectiveRightToLeft = rightToLeft || (TextAlignment & 0x0100) != 0;
            if (!verticalAdvances.IsEmpty && verticalAdvances.Length != advances.Length)
            {
                throw Invalid(record);
            }
            if (!verticalAdvances.IsEmpty &&
                (_selectedFont.Style & (FontStyle.Underline | FontStyle.Strikeout)) != 0)
            {
                throw Unsupported(
                    record,
                    "Two-dimensional character advances with font decorations require per-cell decoration geometry.");
            }
            if (!advances.IsEmpty && effectiveRightToLeft)
            {
                throw Unsupported(
                    record,
                    "Per-character advances combined with right-to-left layout require a bidi glyph-positioning path.");
            }
            if (advances.IsEmpty &&
                (TextCharacterExtra != 0 || TextJustificationExtra != 0) &&
                effectiveRightToLeft)
            {
                throw Unsupported(
                    record,
                    "Inter-character spacing combined with right-to-left layout requires a bidi glyph-positioning path.");
            }

            long totalAdvance = 0;
            long totalVerticalAdvance = 0;
            long minimumAdvance = 0;
            long maximumAdvance = 0;
            long minimumVerticalAdvance = 0;
            long maximumVerticalAdvance = 0;
            for (int index = 0; index < advances.Length; index++)
            {
                totalAdvance += advances[index];
                minimumAdvance = Math.Min(minimumAdvance, totalAdvance);
                maximumAdvance = Math.Max(maximumAdvance, totalAdvance);
                if (!verticalAdvances.IsEmpty)
                {
                    totalVerticalAdvance += verticalAdvances[index];
                    minimumVerticalAdvance = Math.Min(
                        minimumVerticalAdvance,
                        totalVerticalAdvance);
                    maximumVerticalAdvance = Math.Max(
                        maximumVerticalAdvance,
                        totalVerticalAdvance);
                }
            }

            SizeF measuredSize = Graphics.MeasureString(text, _selectedFont);
            PointF reference = (TextAlignment & 0x0001) != 0
                ? CurrentPoint
                : recordPoint;
            Matrix3x2 baseTransform = ApplyTransform(record);
            float characterExtra = advances.IsEmpty
                ? GetEffectiveTextCharacterExtra(baseTransform, record)
                : 0f;
            scoped Span<float> characterSpacing = default;
            float spacingAdvance = 0f;
            bool hasCharacterSpacing = false;
            if (advances.IsEmpty &&
                (characterExtra != 0f ||
                 (TextJustificationExtra != 0 &&
                  TextJustificationBreakCount != 0 &&
                  text.Contains(' '))))
            {
                characterSpacing = text.Length <= 256
                    ? stackalloc float[text.Length]
                    : new float[text.Length];
                for (int index = 0; index < text.Length; index++)
                {
                    float spacing = characterExtra;
                    if (text[index] == ' ')
                    {
                        spacing += GetNextTextJustificationExtra(baseTransform, record);
                    }
                    characterSpacing[index] = spacing;
                    spacingAdvance += spacing;
                    hasCharacterSpacing |= spacing != 0f;
                }
            }
            float horizontalAdvance = advances.IsEmpty
                ? measuredSize.Width + spacingAdvance
                : totalAdvance;
            if (!float.IsFinite(horizontalAdvance))
            {
                throw Invalid(record);
            }
            float x = reference.X;
            float y = reference.Y;
            x -= (TextAlignment & 0x0006) switch
            {
                0x0002 => horizontalAdvance,
                0x0006 => horizontalAdvance / 2f,
                _ => 0f
            };
            y -= (TextAlignment & 0x0018) switch
            {
                0x0008 => measuredSize.Height,
                0x0018 => Graphics.ConvertFontSizeToPixels(
                    _selectedFont.Size,
                    _selectedFont.Unit,
                    Graphics.DpiY) * _selectedFont.TtfFont.Ascender /
                    _selectedFont.TtfFont.UnitsPerEm,
                _ => 0f
            };

            Matrix3x2 textTransform = CreateTextTransform(baseTransform, reference);
            GraphicsState? clippingState = null;
            if (clipped)
            {
                clippingState = Graphics.Save();
                Graphics.IntersectClip(rectangle);
            }
            try
            {
                if (opaque && rectangle.Width > 0 && rectangle.Height > 0)
                {
                    Graphics.FillRectangle(GetBackgroundBrush(), rectangle);
                }

                Graphics.TransformElements = textTransform;
                try
                {
                    float backgroundX = advances.IsEmpty ? x : x + minimumAdvance;
                    float backgroundWidth = advances.IsEmpty
                        ? horizontalAdvance
                        : maximumAdvance - minimumAdvance;
                    float backgroundY = verticalAdvances.IsEmpty
                        ? y
                        : y + minimumVerticalAdvance;
                    float backgroundHeight = measuredSize.Height +
                        (verticalAdvances.IsEmpty
                            ? 0f
                            : maximumVerticalAdvance - minimumVerticalAdvance);
                    if (!opaque && BackgroundMode == 2 &&
                        backgroundWidth > 0f && backgroundHeight > 0f)
                    {
                        Graphics.FillRectangle(
                            GetBackgroundBrush(),
                            backgroundX,
                            backgroundY,
                            backgroundWidth,
                            backgroundHeight);
                    }

                    SolidBrush foreground = GetTextBrush();
                    if (advances.IsEmpty)
                    {
                        if (hasCharacterSpacing)
                        {
                            Graphics.DrawStringWithCharacterSpacing(
                                text,
                                _selectedFont,
                                foreground,
                                x,
                                y,
                                characterSpacing);
                        }
                        else if (effectiveRightToLeft)
                        {
                            using var format = new StringFormat(StringFormat.GenericTypographic)
                            {
                                FormatFlags = StringFormatFlags.DirectionRightToLeft
                            };
                            Graphics.DrawString(text, _selectedFont, foreground, x, y, format);
                        }
                        else
                        {
                            Graphics.DrawString(text, _selectedFont, foreground, x, y);
                        }
                    }
                    else
                    {
                        if (verticalAdvances.IsEmpty)
                        {
                            Graphics.DrawStringWithCharacterAdvances(
                                text,
                                _selectedFont,
                                foreground,
                                x,
                                y,
                                advances);
                        }
                        else
                        {
                            scoped Span<Point> vectorAdvances = advances.Length <= 256
                                ? stackalloc Point[advances.Length]
                                : new Point[advances.Length];
                            for (int index = 0; index < vectorAdvances.Length; index++)
                            {
                                vectorAdvances[index] = new Point(
                                    advances[index],
                                    verticalAdvances[index]);
                            }
                            Graphics.DrawStringWithCharacterAdvances(
                                text,
                                _selectedFont,
                                foreground,
                                x,
                                y,
                                vectorAdvances);
                        }
                    }
                }
                finally
                {
                    Graphics.TransformElements = baseTransform;
                }
            }
            finally
            {
                if (clippingState is not null)
                {
                    Graphics.Restore(clippingState);
                }
            }

            if ((TextAlignment & 0x0001) != 0)
            {
                if (_selectedFontEscapement == 0)
                {
                    float logicalEndX = reference.X + horizontalAdvance;
                    float logicalEndY = reference.Y + totalVerticalAdvance;
                    if (!float.IsFinite(logicalEndX) || !float.IsFinite(logicalEndY) ||
                        logicalEndX < int.MinValue || logicalEndX > int.MaxValue ||
                        logicalEndY < int.MinValue || logicalEndY > int.MaxValue)
                    {
                        throw Invalid(record);
                    }
                    CurrentPoint = Point.Round(new PointF(logicalEndX, logicalEndY));
                }
                else
                {
                    Vector2 deviceEnd = Vector2.Transform(
                        new Vector2(
                            reference.X + horizontalAdvance,
                            reference.Y + totalVerticalAdvance),
                        textTransform);
                    if (!Matrix3x2.Invert(baseTransform, out Matrix3x2 inverseBase))
                    {
                        throw Invalid(record);
                    }
                    Vector2 logicalEnd = Vector2.Transform(deviceEnd, inverseBase);
                    if (!float.IsFinite(logicalEnd.X) || !float.IsFinite(logicalEnd.Y) ||
                        logicalEnd.X < int.MinValue || logicalEnd.X > int.MaxValue ||
                        logicalEnd.Y < int.MinValue || logicalEnd.Y > int.MaxValue)
                    {
                        throw Invalid(record);
                    }
                    CurrentPoint = Point.Round(new PointF(logicalEnd.X, logicalEnd.Y));
                }
            }
        }

        private float GetEffectiveTextCharacterExtra(
            Matrix3x2 baseTransform,
            in MetafileRecord record)
        {
            return GetEffectiveLogicalTextSpacing(
                TextCharacterExtra,
                baseTransform,
                record);
        }

        private float GetNextTextJustificationExtra(
            Matrix3x2 baseTransform,
            in MetafileRecord record)
        {
            if (TextJustificationExtra == 0 || TextJustificationBreakCount == 0)
            {
                return 0f;
            }

            int totalExtra = TextJustificationExtra;
            if (MapMode != 1)
            {
                float deviceTotal = Vector2.TransformNormal(
                    new Vector2(TextJustificationExtra, 0f),
                    baseTransform).X;
                if (!float.IsFinite(deviceTotal) ||
                    deviceTotal < int.MinValue || deviceTotal > int.MaxValue)
                {
                    throw Invalid(record);
                }
                totalExtra = checked((int)MathF.Round(deviceTotal));
            }

            int spacing = Math.DivRem(
                totalExtra,
                TextJustificationBreakCount,
                out int remainder);
            long accumulatedError = (long)TextJustificationError + remainder;
            if (Math.Abs(accumulatedError) >= TextJustificationBreakCount)
            {
                int correction = Math.Sign(accumulatedError);
                spacing += correction;
                accumulatedError -= (long)correction * TextJustificationBreakCount;
            }
            TextJustificationError = checked((int)accumulatedError);

            return MapMode == 1
                ? spacing
                : DeviceTextSpacingToLogical(spacing, baseTransform, record);
        }

        private float GetEffectiveLogicalTextSpacing(
            int logicalSpacing,
            Matrix3x2 baseTransform,
            in MetafileRecord record)
        {
            if (logicalSpacing == 0 || MapMode == 1)
            {
                return logicalSpacing;
            }

            Vector2 deviceExtra = Vector2.TransformNormal(
                new Vector2(logicalSpacing, 0f),
                baseTransform);
            float roundedDeviceX = MathF.Round(deviceExtra.X);
            if (!float.IsFinite(roundedDeviceX))
            {
                throw Invalid(record);
            }
            return DeviceTextSpacingToLogical(
                roundedDeviceX,
                baseTransform,
                record);
        }

        private static float DeviceTextSpacingToLogical(
            float deviceSpacing,
            Matrix3x2 baseTransform,
            in MetafileRecord record)
        {
            if (!Matrix3x2.Invert(baseTransform, out Matrix3x2 inverseBase))
            {
                throw Invalid(record);
            }

            Vector2 logicalExtra = Vector2.TransformNormal(
                new Vector2(deviceSpacing, 0f),
                inverseBase);
            if (!float.IsFinite(logicalExtra.X) || !float.IsFinite(logicalExtra.Y))
            {
                throw Invalid(record);
            }
            return logicalExtra.X;
        }

        private Matrix3x2 CreateTextTransform(Matrix3x2 baseTransform, PointF reference)
        {
            if (_selectedFontEscapement == 0)
            {
                return baseTransform;
            }

            Vector2 deviceReference = Vector2.Transform(
                new Vector2(reference.X, reference.Y),
                baseTransform);
            float radians = -_selectedFontEscapement * (MathF.PI / 1800f);
            return baseTransform * Matrix3x2.CreateRotation(radians, deviceReference);
        }

        private SolidBrush GetTextBrush()
        {
            if (_textBrush is null || _textBrush.Color != TextColor)
            {
                _textBrush?.Dispose();
                _textBrush = new SolidBrush(TextColor);
            }
            return _textBrush;
        }

        private SolidBrush GetBackgroundBrush()
        {
            if (_backgroundBrush is null || _backgroundBrush.Color != BackgroundColor)
            {
                _backgroundBrush?.Dispose();
                _backgroundBrush = new SolidBrush(BackgroundColor);
            }
            return _backgroundBrush;
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
            if (ReferenceEquals(product, _selectedPen) || ReferenceEquals(product, _selectedBrush) ||
                ReferenceEquals(product, _selectedFontObject))
            {
                return true;
            }

            foreach (SavedState savedState in _savedStates)
            {
                if (ReferenceEquals(product, savedState.SelectedPen) ||
                    ReferenceEquals(product, savedState.SelectedBrush) ||
                    ReferenceEquals(product, savedState.SelectedFontObject))
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
                case Font font:
                    _selectedFont = font;
                    _selectedFontObject = font;
                    _selectedFontEscapement = 0;
                    break;
                case WmfFontObject fontObject:
                    _selectedFont = fontObject.Font;
                    _selectedFontObject = fontObject;
                    _selectedFontEscapement = fontObject.Escapement;
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
            _textBrush?.Dispose();
            _backgroundBrush?.Dispose();
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
            int TextAlignment,
            int TextCharacterExtra,
            int TextJustificationExtra,
            int TextJustificationBreakCount,
            int TextJustificationError,
            Color BackgroundColor,
            Color TextColor,
            Pen? SelectedPen,
            Brush? SelectedBrush,
            Font SelectedFont,
            object? SelectedFontObject,
            int SelectedFontEscapement,
            GraphicsState GraphicsState);
    }

    private sealed class WmfFontObject(Font font, int escapement) : IDisposable
    {
        internal Font Font { get; } = font;
        internal int Escapement { get; } = escapement;

        public void Dispose() => Font.Dispose();
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
