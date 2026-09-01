using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Text;
using ProGPU.Scene;
using ProGPU.SystemDrawing;
using TilePatternBrush = ProGPU.Vector.TilePatternBrush;

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
    private const uint DefaultPalette = 15;
    private const uint DibRgbColors = 0;
    private const uint DibPalColors = 1;
    private const uint DibPalIndices = 2;
    private const uint BiRgb = 0;
    private const uint BiRle8 = 1;
    private const uint BiRle4 = 2;
    private const uint BiBitFields = 3;
    private const uint BiJpeg = 4;
    private const uint BiPng = 5;
    private const uint BiCmyk = 11;
    private const uint BiCmykRle8 = 12;
    private const uint BiCmykRle4 = 13;
    private const uint Blackness = 0x0000_0042;
    private const uint NotSourceCopy = 0x0033_0008;
    private const uint SrcCopy = 0x00CC_0020;
    private const uint PatCopy = 0x00F0_0021;
    private const uint Whiteness = 0x00FF_0062;

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

            case EmfPlusRecordType.WmfSetStretchBltMode:
                RequireSize(record, payload, 2);
                state.SetStretchMode(ReadUInt16(payload, 0), record);
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
                state.MoveTo(ReadWmfYxPoint(payload));
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

            case EmfPlusRecordType.WmfDibBitBlt:
                DrawWmfDibBitBlt(state, record, payload);
                return;

            case EmfPlusRecordType.WmfDibStretchBlt:
                DrawWmfDibStretchBlt(state, record, payload);
                return;

            case EmfPlusRecordType.WmfBitBlt:
                DrawWmfBitmap16Blt(state, record, payload, stretch: false);
                return;

            case EmfPlusRecordType.WmfStretchBlt:
                DrawWmfBitmap16Blt(state, record, payload, stretch: true);
                return;

            case EmfPlusRecordType.WmfStretchDib:
                DrawWmfStretchDib(state, record, payload);
                return;

            case EmfPlusRecordType.WmfSetDibToDev:
                DrawWmfSetDibToDevice(state, record, payload);
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

            case EmfPlusRecordType.WmfDibCreatePatternBrush:
                state.CreateWmfDibPatternBrush(payload, record);
                return;

            case EmfPlusRecordType.WmfCreatePatternBrush:
                state.CreateWmfBitmap16PatternBrush(payload, record);
                return;

            case EmfPlusRecordType.WmfCreateFontIndirect:
                RequireSize(record, payload, 50);
                state.CreateWmfFont(payload, record);
                return;

            case EmfPlusRecordType.WmfCreatePalette:
                state.CreateWmfPalette(payload, record);
                return;

            case EmfPlusRecordType.WmfSelectPalette:
                RequireSize(record, payload, 2);
                state.SelectWmfPalette(ReadUInt16(payload, 0), record);
                return;

            case EmfPlusRecordType.WmfSetPalEntries:
                state.SetWmfPaletteEntries(payload, record);
                return;

            case EmfPlusRecordType.WmfAnimatePalette:
                state.AnimateWmfPalette(payload, record);
                return;

            case EmfPlusRecordType.WmfResizePalette:
                RequireSize(record, payload, 2);
                state.ResizeSelectedPalette(ReadUInt16(payload, 0), record);
                return;

            case EmfPlusRecordType.WmfRealizePalette:
                RequireSize(record, payload, 0);
                state.RealizePalette();
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
                DrawWmfArcFamily(state, record, payload, ArcClosure.Open);
                return;

            case EmfPlusRecordType.WmfPie:
                DrawWmfArcFamily(state, record, payload, ArcClosure.Pie);
                return;

            case EmfPlusRecordType.WmfChord:
                DrawWmfArcFamily(state, record, payload, ArcClosure.Chord);
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

            case EmfPlusRecordType.EmfSetPixelV:
                RequireSize(record, payload, 12);
                state.EnsurePathCaptureSupported(record, "SetPixelV");
                state.ApplyTransform(record);
                state.Graphics.SetTransformedPixel(
                    ReadColor(payload, 8),
                    ReadPoint(payload));
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

            case EmfPlusRecordType.EmfSetBrushOrgEx:
                RequireSize(record, payload, 8);
                state.Graphics.RenderingOrigin = ReadPoint(payload);
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

            case EmfPlusRecordType.EmfSetStretchBltMode:
                RequireSize(record, payload, 4);
                state.SetStretchMode(ReadInt32(payload, 0), record);
                return;

            case EmfPlusRecordType.EmfMoveToEx:
                RequireSize(record, payload, 8);
                state.MoveTo(ReadPoint(payload), record);
                return;

            case EmfPlusRecordType.EmfLineTo:
                RequireSize(record, payload, 8);
                Point next = ReadPoint(payload);
                if (state.IsPathBracketOpen)
                {
                    using var path = new GraphicsPath();
                    path.AddLine(state.CurrentPoint, next);
                    state.CapturePath(
                        record,
                        path,
                        connect: true,
                        continueFigure: true);
                }
                else
                {
                    state.ApplyTransform(record);
                    if (state.SelectedPen is Pen linePen)
                    {
                        state.Graphics.DrawLine(linePen, state.CurrentPoint, next);
                    }
                }
                state.CurrentPoint = next;
                return;

            case EmfPlusRecordType.EmfPolyBezier:
                DrawEmfBezier(state, record, payload, fromCurrentPosition: false, points16: false);
                return;

            case EmfPlusRecordType.EmfPolyBezier16:
                DrawEmfBezier(state, record, payload, fromCurrentPosition: false, points16: true);
                return;

            case EmfPlusRecordType.EmfPolyBezierTo:
                DrawEmfBezier(state, record, payload, fromCurrentPosition: true, points16: false);
                return;

            case EmfPlusRecordType.EmfPolyBezierTo16:
                DrawEmfBezier(state, record, payload, fromCurrentPosition: true, points16: true);
                return;

            case EmfPlusRecordType.EmfPolyLineTo:
                DrawEmfPolylineTo(state, record, payload, points16: false);
                return;

            case EmfPlusRecordType.EmfPolylineTo16:
                DrawEmfPolylineTo(state, record, payload, points16: true);
                return;

            case EmfPlusRecordType.EmfPolyDraw:
                DrawEmfPolyDraw(state, record, payload, points16: false);
                return;

            case EmfPlusRecordType.EmfPolyDraw16:
                DrawEmfPolyDraw(state, record, payload, points16: true);
                return;

            case EmfPlusRecordType.EmfRectangle:
                RequireSize(record, payload, 16);
                DrawRectangle(state, record, ReadRectangle(record, payload));
                return;

            case EmfPlusRecordType.EmfRoundRect:
                DrawEmfRoundRectangle(state, record, payload);
                return;

            case EmfPlusRecordType.EmfRoundArc:
                DrawEmfArcFamily(state, record, payload, ArcClosure.Open);
                return;

            case EmfPlusRecordType.EmfArcTo:
                DrawEmfArcTo(state, record, payload);
                return;

            case EmfPlusRecordType.EmfAngleArc:
                DrawEmfAngleArc(state, record, payload);
                return;

            case EmfPlusRecordType.EmfChord:
                DrawEmfArcFamily(state, record, payload, ArcClosure.Chord);
                return;

            case EmfPlusRecordType.EmfPie:
                DrawEmfArcFamily(state, record, payload, ArcClosure.Pie);
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

            case EmfPlusRecordType.EmfPolygon16:
                DrawPolygon(state, record, payload, close: true, points16: true);
                return;

            case EmfPlusRecordType.EmfPolyline16:
                DrawPolygon(state, record, payload, close: false, points16: true);
                return;

            case EmfPlusRecordType.EmfPolyPolygon:
                DrawPolyPoly(state, record, payload, close: true);
                return;

            case EmfPlusRecordType.EmfPolyPolyline:
                DrawPolyPoly(state, record, payload, close: false);
                return;

            case EmfPlusRecordType.EmfPolyPolygon16:
                DrawPolyPoly(state, record, payload, close: true, points16: true);
                return;

            case EmfPlusRecordType.EmfPolyPolyline16:
                DrawPolyPoly(state, record, payload, close: false, points16: true);
                return;

            case EmfPlusRecordType.EmfOffsetClipRgn:
                RequireSize(record, payload, 8);
                state.OffsetClip(record, ReadPoint(payload));
                return;

            case EmfPlusRecordType.EmfSetMetaRgn:
                RequireSize(record, payload, 0);
                state.SetMetaRegion();
                return;

            case EmfPlusRecordType.EmfExcludeClipRect:
                RequireSize(record, payload, 16);
                state.ExcludeClip(record, ReadRectangle(record, payload));
                return;

            case EmfPlusRecordType.EmfIntersectClipRect:
                RequireSize(record, payload, 16);
                state.IntersectClip(record, ReadRectangle(record, payload));
                return;

            case EmfPlusRecordType.EmfExtSelectClipRgn:
                SelectEmfClipRegion(state, record, payload);
                return;

            case EmfPlusRecordType.EmfSetDIBitsToDevice:
                DrawEmfSetDibitsToDevice(state, record, payload);
                return;

            case EmfPlusRecordType.EmfStretchDIBits:
                DrawEmfStretchDibits(state, record, payload);
                return;

            case EmfPlusRecordType.EmfSaveDC:
                RequireSize(record, payload, 0);
                state.Save();
                return;

            case EmfPlusRecordType.EmfRestoreDC:
                RequireSize(record, payload, 4);
                state.Restore(ReadInt32(payload, 0), record);
                return;

            case EmfPlusRecordType.EmfSetArcDirection:
                RequireSize(record, payload, 4);
                state.SetArcDirection(ReadInt32(payload, 0), record);
                return;

            case EmfPlusRecordType.EmfSetMiterLimit:
                RequireSize(record, payload, 4);
                state.SetMiterLimit(ReadSingle(payload, 0), record);
                return;

            case EmfPlusRecordType.EmfBeginPath:
                RequireSize(record, payload, 0);
                state.BeginPath(record);
                return;

            case EmfPlusRecordType.EmfEndPath:
                RequireSize(record, payload, 0);
                state.EndPath(record);
                return;

            case EmfPlusRecordType.EmfCloseFigure:
                RequireSize(record, payload, 0);
                state.CloseFigure(record);
                return;

            case EmfPlusRecordType.EmfFillPath:
                RequireSize(record, payload, 16);
                state.RenderPath(record, fill: true, stroke: false);
                return;

            case EmfPlusRecordType.EmfStrokeAndFillPath:
                RequireSize(record, payload, 16);
                state.RenderPath(record, fill: true, stroke: true);
                return;

            case EmfPlusRecordType.EmfStrokePath:
                RequireSize(record, payload, 16);
                state.RenderPath(record, fill: false, stroke: true);
                return;

            case EmfPlusRecordType.EmfFlattenPath:
                RequireSize(record, payload, 0);
                state.FlattenPath(record);
                return;

            case EmfPlusRecordType.EmfWidenPath:
                RequireSize(record, payload, 0);
                state.WidenPath(record);
                return;

            case EmfPlusRecordType.EmfSelectClipPath:
                RequireSize(record, payload, 4);
                state.SelectClipPath(ReadInt32(payload, 0), record);
                return;

            case EmfPlusRecordType.EmfAbortPath:
                RequireSize(record, payload, 0);
                state.AbortPath();
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

            case EmfPlusRecordType.EmfCreateMonoBrush:
                state.CreateEmfDibPatternBrush(payload, record, requireMonochrome: true);
                return;

            case EmfPlusRecordType.EmfCreateDibPatternBrushPt:
                state.CreateEmfDibPatternBrush(payload, record, requireMonochrome: false);
                return;

            case EmfPlusRecordType.EmfCreatePalette:
                state.CreateEmfPalette(payload, record);
                return;

            case EmfPlusRecordType.EmfSelectPalette:
                RequireSize(record, payload, 4);
                state.SelectPalette(ReadUInt32(payload, 0), record);
                return;

            case EmfPlusRecordType.EmfSetPaletteEntries:
                state.SetEmfPaletteEntries(payload, record);
                return;

            case EmfPlusRecordType.EmfResizePalette:
                RequireSize(record, payload, 8);
                state.ResizePalette(
                    ReadUInt32(payload, 0),
                    ReadUInt32(payload, 4),
                    record);
                return;

            case EmfPlusRecordType.EmfRealizePalette:
                RequireSize(record, payload, 0);
                state.RealizePalette();
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

    private static void DrawEmfStretchDibits(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        const int fixedPayloadSize = 72;
        if (payload.Length < fixedPayloadSize)
        {
            throw Invalid(record);
        }

        state.EnsurePathCaptureSupported(record, "StretchDIBits");
        uint usage = ReadUInt32(payload, 56);
        uint rasterOperation = ReadUInt32(payload, 60);
        ValidateDibRasterOperation(record, rasterOperation);

        ReadOnlySpan<byte> bitmapInfo = ReadEmfBuffer(
            record,
            payload,
            ReadUInt32(payload, 40),
            ReadUInt32(payload, 44),
            fixedPayloadSize);
        ReadOnlySpan<byte> bitmapBits = ReadEmfBuffer(
            record,
            payload,
            ReadUInt32(payload, 48),
            ReadUInt32(payload, 52),
            fixedPayloadSize);
        EnsureDisjointEmfBuffers(
            record,
            ReadUInt32(payload, 40),
            ReadUInt32(payload, 44),
            ReadUInt32(payload, 48),
            ReadUInt32(payload, 52));

        DibInfo dib = ReadDibInfo(record, bitmapInfo, usage, state.SelectedPalette);
        using Bitmap bitmap = DecodeDibRows(record, dib, bitmapInfo, bitmapBits, dib.Height);
        DrawMappedDib(
            state,
            record,
            bitmap,
            dib,
            ReadInt32(payload, 24),
            ReadInt32(payload, 28),
            ReadInt32(payload, 32),
            ReadInt32(payload, 36),
            ReadInt32(payload, 16),
            ReadInt32(payload, 20),
            ReadInt32(payload, 64),
            ReadInt32(payload, 68),
            rasterOperation);
    }

    private static void DrawEmfSetDibitsToDevice(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        const int fixedPayloadSize = 68;
        if (payload.Length < fixedPayloadSize)
        {
            throw Invalid(record);
        }

        state.EnsurePathCaptureSupported(record, "SetDIBitsToDevice");
        uint usage = ReadUInt32(payload, 56);

        ReadOnlySpan<byte> bitmapInfo = ReadEmfBuffer(
            record,
            payload,
            ReadUInt32(payload, 40),
            ReadUInt32(payload, 44),
            fixedPayloadSize);
        ReadOnlySpan<byte> bitmapBits = ReadEmfBuffer(
            record,
            payload,
            ReadUInt32(payload, 48),
            ReadUInt32(payload, 52),
            fixedPayloadSize);
        EnsureDisjointEmfBuffers(
            record,
            ReadUInt32(payload, 40),
            ReadUInt32(payload, 44),
            ReadUInt32(payload, 48),
            ReadUInt32(payload, 52));

        DibInfo dib = ReadDibInfo(record, bitmapInfo, usage, state.SelectedPalette);
        DrawSetDibitsBand(
            state,
            record,
            dib,
            bitmapInfo,
            bitmapBits,
            ReadInt32(payload, 24),
            ReadInt32(payload, 28),
            ReadInt32(payload, 32),
            ReadInt32(payload, 36),
            ReadInt32(payload, 16),
            ReadInt32(payload, 20),
            ReadUInt32(payload, 60),
            ReadUInt32(payload, 64));
    }

    private static void DrawWmfDibBitBlt(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        const int fixedPayloadSize = 16;
        if (TryDrawWmfBitmapWithoutSource(state, record, payload, stretch: false))
        {
            return;
        }
        if (payload.Length < fixedPayloadSize)
        {
            throw Invalid(record);
        }
        uint rasterOperation = ReadUInt32(payload, 0);
        ValidateDibRasterOperation(record, rasterOperation);

        ReadOnlySpan<byte> packedDib = payload[fixedPayloadSize..];
        DibInfo dib = ReadDibInfo(record, packedDib, DibRgbColors, state.SelectedPalette);
        ReadOnlySpan<byte> bitmapBits = ReadWmfDibBits(record, dib, packedDib);
        using Bitmap bitmap = DecodeDibRows(
            record,
            dib,
            packedDib[..dib.BitmapInfoSize],
            bitmapBits,
            dib.Height);
        int width = ReadInt16(payload, 10);
        int height = ReadInt16(payload, 8);
        DrawMappedDib(
            state,
            record,
            bitmap,
            dib,
            ReadInt16(payload, 6),
            ReadInt16(payload, 4),
            width,
            height,
            ReadInt16(payload, 14),
            ReadInt16(payload, 12),
            width,
            height,
            rasterOperation);
    }

    private static void DrawWmfDibStretchBlt(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        const int fixedPayloadSize = 20;
        if (TryDrawWmfBitmapWithoutSource(state, record, payload, stretch: true))
        {
            return;
        }
        if (payload.Length < fixedPayloadSize)
        {
            throw Invalid(record);
        }
        uint rasterOperation = ReadUInt32(payload, 0);
        ValidateDibRasterOperation(record, rasterOperation);

        ReadOnlySpan<byte> packedDib = payload[fixedPayloadSize..];
        DibInfo dib = ReadDibInfo(record, packedDib, DibRgbColors, state.SelectedPalette);
        ReadOnlySpan<byte> bitmapBits = ReadWmfDibBits(record, dib, packedDib);
        using Bitmap bitmap = DecodeDibRows(
            record,
            dib,
            packedDib[..dib.BitmapInfoSize],
            bitmapBits,
            dib.Height);
        DrawMappedDib(
            state,
            record,
            bitmap,
            dib,
            ReadInt16(payload, 10),
            ReadInt16(payload, 8),
            ReadInt16(payload, 6),
            ReadInt16(payload, 4),
            ReadInt16(payload, 18),
            ReadInt16(payload, 16),
            ReadInt16(payload, 14),
            ReadInt16(payload, 12),
            rasterOperation);
    }

    private static void DrawWmfStretchDib(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        const int fixedPayloadSize = 22;
        if (payload.Length < fixedPayloadSize)
        {
            throw Invalid(record);
        }
        uint rasterOperation = ReadUInt32(payload, 0);
        ValidateDibRasterOperation(record, rasterOperation);
        uint usage = ReadUInt16(payload, 4);

        ReadOnlySpan<byte> packedDib = payload[fixedPayloadSize..];
        DibInfo dib = ReadDibInfo(record, packedDib, usage, state.SelectedPalette);
        ReadOnlySpan<byte> bitmapBits = ReadWmfDibBits(record, dib, packedDib);
        using Bitmap bitmap = DecodeDibRows(
            record,
            dib,
            packedDib[..dib.BitmapInfoSize],
            bitmapBits,
            dib.Height);
        DrawMappedDib(
            state,
            record,
            bitmap,
            dib,
            ReadInt16(payload, 12),
            ReadInt16(payload, 10),
            ReadInt16(payload, 8),
            ReadInt16(payload, 6),
            ReadInt16(payload, 20),
            ReadInt16(payload, 18),
            ReadInt16(payload, 16),
            ReadInt16(payload, 14),
            rasterOperation);
    }

    private static void DrawWmfSetDibToDevice(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        const int fixedPayloadSize = 18;
        if (payload.Length < fixedPayloadSize)
        {
            throw Invalid(record);
        }
        uint usage = ReadUInt16(payload, 0);

        ReadOnlySpan<byte> packedDib = payload[fixedPayloadSize..];
        DibInfo dib = ReadDibInfo(record, packedDib, usage, state.SelectedPalette);
        ReadOnlySpan<byte> bitmapBits = ReadWmfDibBits(record, dib, packedDib);
        DrawSetDibitsBand(
            state,
            record,
            dib,
            packedDib[..dib.BitmapInfoSize],
            bitmapBits,
            ReadUInt16(payload, 8),
            ReadUInt16(payload, 6),
            ReadUInt16(payload, 12),
            ReadUInt16(payload, 10),
            ReadUInt16(payload, 16),
            ReadUInt16(payload, 14),
            ReadUInt16(payload, 4),
            ReadUInt16(payload, 2));
    }

    private static ReadOnlySpan<byte> ReadWmfDibBits(
        in MetafileRecord record,
        in DibInfo dib,
        ReadOnlySpan<byte> packedDib)
    {
        ReadOnlySpan<byte> bitmapBits = packedDib[dib.BitmapInfoSize..];
        if (!IsRleCompression(dib.Compression) &&
            dib.Compression is not BiJpeg and not BiPng)
        {
            return bitmapBits;
        }
        if (bitmapBits.Length == dib.CompressedSize)
        {
            return bitmapBits;
        }
        if (dib.CompressedSize < int.MaxValue && bitmapBits.Length == dib.CompressedSize + 1)
        {
            return bitmapBits[..dib.CompressedSize];
        }
        throw Invalid(record);
    }

    private static void DrawWmfBitmap16Blt(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        bool stretch)
    {
        if (TryDrawWmfBitmapWithoutSource(state, record, payload, stretch))
        {
            return;
        }

        int fixedPayloadSize = stretch ? 20 : 16;
        if (payload.IsEmpty)
        {
            throw Unsupported(record, "Bitmap16 source playback is not available for an empty record.");
        }
        if (payload.Length < fixedPayloadSize + 10)
        {
            throw Invalid(record);
        }
        ReadOnlySpan<byte> bitmap16 = payload[fixedPayloadSize..];
        WmfBitmap16Info bitmapInfo = ReadBitmap16Info(record, bitmap16);

        uint rasterOperation = ReadUInt32(payload, 0);
        int destinationHeight = ReadInt16(payload, stretch ? 12 : 8);
        int destinationWidth = ReadInt16(payload, stretch ? 14 : 10);
        int destinationY = ReadInt16(payload, stretch ? 16 : 12);
        int destinationX = ReadInt16(payload, stretch ? 18 : 14);
        ValidateDibRasterOperation(record, rasterOperation);
        if (TryDrawSourceIndependentRasterOperation(
            state,
            record,
            rasterOperation,
            destinationX,
            destinationY,
            destinationWidth,
            destinationHeight))
        {
            return;
        }

        int sourceHeight = stretch ? ReadInt16(payload, 4) : destinationHeight;
        int sourceWidth = stretch ? ReadInt16(payload, 6) : destinationWidth;
        int sourceY = ReadInt16(payload, stretch ? 8 : 4);
        int sourceX = ReadInt16(payload, stretch ? 10 : 6);
        using Bitmap bitmap = WmfBitmap16DecodeServices.Decode(bitmapInfo, bitmap16[10..]);
        DrawMappedBitmap(
            state,
            record,
            bitmap,
            bitmapInfo.Width,
            bitmapInfo.Height,
            topDown: true,
            sourceX,
            sourceY,
            sourceWidth,
            sourceHeight,
            destinationX,
            destinationY,
            destinationWidth,
            destinationHeight,
            rasterOperation);
    }

    private static bool TryDrawWmfBitmapWithoutSource(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        bool stretch)
    {
        int expectedSize = stretch ? 22 : 18;
        if (payload.Length != expectedSize)
        {
            return false;
        }

        uint rasterOperation = ReadUInt32(payload, 0);
        int destinationHeight = ReadInt16(payload, stretch ? 14 : 10);
        int destinationWidth = ReadInt16(payload, stretch ? 16 : 12);
        int destinationY = ReadInt16(payload, stretch ? 18 : 14);
        int destinationX = ReadInt16(payload, stretch ? 20 : 16);
        ValidateDibRasterOperation(record, rasterOperation);
        if (!TryDrawSourceIndependentRasterOperation(
            state,
            record,
            rasterOperation,
            destinationX,
            destinationY,
            destinationWidth,
            destinationHeight))
        {
            throw Unsupported(
                record,
                $"Ternary raster operation 0x{rasterOperation:X8} requires an embedded bitmap source; " +
                "the source-omitted WMF record must fail instead of sampling the playback device context.");
        }
        return true;
    }

    private static WmfBitmap16Info ReadBitmap16Info(
        in MetafileRecord record,
        ReadOnlySpan<byte> bitmap,
        int bitsOffset = 10)
    {
        if (bitsOffset < 10 || bitmap.Length < bitsOffset)
        {
            throw Invalid(record);
        }

        short type = ReadInt16(bitmap, 0);
        int width = ReadInt16(bitmap, 2);
        int height = ReadInt16(bitmap, 4);
        int widthBytes = ReadInt16(bitmap, 6);
        int planes = bitmap[8];
        int bitsPerPixel = bitmap[9];
        if (width <= 0 || height <= 0 || widthBytes <= 0 || planes != 1 || bitsPerPixel == 0)
        {
            throw Invalid(record);
        }

        long computedWidthBytes = (((long)width * bitsPerPixel + 15) >> 4) << 1;
        long expectedSize = bitsOffset + computedWidthBytes * height;
        if (computedWidthBytes != widthBytes || expectedSize != bitmap.Length)
        {
            throw Invalid(record);
        }

        return new WmfBitmap16Info(
            type,
            width,
            height,
            widthBytes,
            (byte)planes,
            (byte)bitsPerPixel);
    }

    private static void ValidateDibRasterOperation(
        in MetafileRecord record,
        uint rasterOperation)
    {
        if ((rasterOperation & 0xFF00_0000u) != 0)
        {
            throw Invalid(record);
        }
    }

    private static void DrawSetDibitsBand(
        PlaybackState state,
        in MetafileRecord record,
        in DibInfo dib,
        ReadOnlySpan<byte> bitmapInfo,
        ReadOnlySpan<byte> bitmapBits,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        int destinationX,
        int destinationY,
        uint startScan,
        uint scanCount)
    {
        if (startScan > (uint)dib.Height || scanCount > (uint)dib.Height - startScan)
        {
            throw Invalid(record);
        }
        if (scanCount == 0)
        {
            if (bitmapBits.Length != 0)
            {
                throw Invalid(record);
            }
            return;
        }

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw Invalid(record);
        }

        bool encodedFile = dib.Compression is BiJpeg or BiPng;
        using Bitmap decoded = DecodeDibRows(
            record,
            dib,
            bitmapInfo,
            bitmapBits,
            encodedFile ? dib.Height : checked((int)scanCount));
        int bandTop = dib.TopDown
            ? checked((int)startScan)
            : checked(dib.Height - (int)(startScan + scanCount));
        Rectangle requestedSource = GetSetDibSourceRectangle(
            record,
            dib,
            sourceX,
            sourceY,
            sourceWidth,
            sourceHeight);
        Rectangle availableSource = Rectangle.Intersect(
            requestedSource,
            new Rectangle(0, bandTop, dib.Width, checked((int)scanCount)));
        if (availableSource.IsEmpty)
        {
            return;
        }

        Rectangle bandSource = new(
            availableSource.X,
            encodedFile ? availableSource.Y : availableSource.Y - bandTop,
            availableSource.Width,
            availableSource.Height);
        Rectangle destination = new(
            AddCoordinate(
                record,
                destinationX,
                availableSource.X - requestedSource.X),
            AddCoordinate(
                record,
                destinationY,
                availableSource.Y - requestedSource.Y),
            availableSource.Width,
            availableSource.Height);
        state.ApplyTransform(record);
        state.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        state.Graphics.DrawImage(decoded, destination, bandSource, GraphicsUnit.Pixel);
    }

    private static Rectangle GetSetDibSourceRectangle(
        in MetafileRecord record,
        in DibInfo dib,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight)
    {
        long top = dib.TopDown
            ? sourceY
            : (long)dib.Height - sourceY - sourceHeight;
        long right = (long)sourceX + sourceWidth;
        long bottom = top + sourceHeight;
        if (right is < int.MinValue or > int.MaxValue ||
            top is < int.MinValue or > int.MaxValue ||
            bottom is < int.MinValue or > int.MaxValue)
        {
            throw Invalid(record);
        }
        return new Rectangle(sourceX, (int)top, sourceWidth, sourceHeight);
    }

    private static void DrawMappedDib(
        PlaybackState state,
        in MetafileRecord record,
        Bitmap bitmap,
        in DibInfo dib,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        uint rasterOperation)
        => DrawMappedBitmap(
            state,
            record,
            bitmap,
            dib.Width,
            dib.Height,
            dib.TopDown,
            sourceX,
            sourceY,
            sourceWidth,
            sourceHeight,
            destinationX,
            destinationY,
            destinationWidth,
            destinationHeight,
            rasterOperation);

    private static void DrawMappedBitmap(
        PlaybackState state,
        in MetafileRecord record,
        Bitmap bitmap,
        int bitmapWidth,
        int bitmapHeight,
        bool topDown,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        uint rasterOperation)
    {
        if (destinationWidth == 0 || destinationHeight == 0)
        {
            return;
        }

        ValidateDibRasterOperation(record, rasterOperation);
        if (TryDrawSourceIndependentRasterOperation(
            state,
            record,
            rasterOperation,
            destinationX,
            destinationY,
            destinationWidth,
            destinationHeight))
        {
            return;
        }
        if (sourceWidth == 0 || sourceHeight == 0)
        {
            return;
        }

        long sourceXEnd = (long)sourceX + sourceWidth;
        long sourceVisualY0 = topDown ? sourceY : (long)bitmapHeight - sourceY;
        long sourceVisualY1 = topDown
            ? (long)sourceY + sourceHeight
            : (long)bitmapHeight - sourceY - sourceHeight;
        long left = Math.Min(sourceX, sourceXEnd);
        long right = Math.Max(sourceX, sourceXEnd);
        long top = Math.Min(sourceVisualY0, sourceVisualY1);
        long bottom = Math.Max(sourceVisualY0, sourceVisualY1);
        if (right == left || bottom == top)
        {
            throw Invalid(record);
        }

        PointF topLeft = new(
            sourceWidth > 0 ? destinationX : AddCoordinate(record, destinationX, destinationWidth),
            sourceHeight > 0 ? destinationY : AddCoordinate(record, destinationY, destinationHeight));
        PointF topRight = new(
            sourceWidth > 0 ? AddCoordinate(record, destinationX, destinationWidth) : destinationX,
            topLeft.Y);
        PointF bottomLeft = new(
            topLeft.X,
            sourceHeight > 0 ? AddCoordinate(record, destinationY, destinationHeight) : destinationY);

        long clippedLeft = Math.Max(0, left);
        long clippedTop = Math.Max(0, top);
        long clippedRight = Math.Min(bitmapWidth, right);
        long clippedBottom = Math.Min(bitmapHeight, bottom);
        if (clippedRight <= clippedLeft || clippedBottom <= clippedTop)
        {
            return;
        }

        float u0 = (float)((double)(clippedLeft - left) / (right - left));
        float u1 = (float)((double)(clippedRight - left) / (right - left));
        float v0 = (float)((double)(clippedTop - top) / (bottom - top));
        float v1 = (float)((double)(clippedBottom - top) / (bottom - top));
        PointF horizontal = new(topRight.X - topLeft.X, topRight.Y - topLeft.Y);
        PointF vertical = new(bottomLeft.X - topLeft.X, bottomLeft.Y - topLeft.Y);
        PointF clippedDestinationTopLeft = InterpolateDestination(topLeft, horizontal, vertical, u0, v0);
        PointF clippedDestinationTopRight = InterpolateDestination(topLeft, horizontal, vertical, u1, v0);
        PointF clippedDestinationBottomLeft = InterpolateDestination(topLeft, horizontal, vertical, u0, v1);

        state.ApplyTransform(record);
        state.Graphics.InterpolationMode = state.DibInterpolationMode;
        RectangleF clippedSource = new(
            clippedLeft,
            clippedTop,
            clippedRight - clippedLeft,
            clippedBottom - clippedTop);
        if (rasterOperation is SrcCopy or NotSourceCopy)
        {
            using Bitmap? inverted = rasterOperation == NotSourceCopy
                ? bitmap.CreateBitwiseInvertedRgb()
                : null;
            state.Graphics.DrawImage(
                inverted ?? bitmap,
                [clippedDestinationTopLeft, clippedDestinationTopRight, clippedDestinationBottomLeft],
                clippedSource,
                GraphicsUnit.Pixel);
            return;
        }

        state.Graphics.DrawImageRasterOperation(
            bitmap,
            clippedDestinationTopLeft,
            clippedDestinationTopRight,
            clippedDestinationBottomLeft,
            clippedSource,
            CreateRasterOperation(state, record, rasterOperation));
    }

    private static GpuRasterOperation CreateRasterOperation(
        PlaybackState state,
        in MetafileRecord record,
        uint rasterOperation)
    {
        byte code = checked((byte)(rasterOperation >> 16));
        if (!RasterOperationUsesPattern(code))
        {
            return new GpuRasterOperation(code, Vector4.Zero);
        }

        return state.SelectedBrush switch
        {
            SolidBrush solidBrush => new GpuRasterOperation(
                code,
                ToVector(solidBrush.Color)),
            HatchBrush hatchBrush => new GpuRasterOperation(
                code,
                state.ResolveRasterOperationPattern(hatchBrush)),
            TextureBrush textureBrush => new GpuRasterOperation(
                code,
                state.ResolveRasterOperationPattern(textureBrush)),
            _ => throw Unsupported(
                record,
                $"Ternary raster operation 0x{rasterOperation:X8} requires a selected solid, hatch, or texture brush pattern.")
        };
    }

    private static Vector4 ToVector(Color color) =>
        new(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);

    private static bool RasterOperationUsesPattern(byte code)
    {
        for (int sourceDestination = 0; sourceDestination < 4; sourceDestination++)
        {
            int withoutPattern = (code >> sourceDestination) & 1;
            int withPattern = (code >> (sourceDestination | 4)) & 1;
            if (withoutPattern != withPattern)
            {
                return true;
            }
        }
        return false;
    }

    private static bool RasterOperationUsesSource(byte code)
    {
        for (int pattern = 0; pattern < 2; pattern++)
        {
            for (int destination = 0; destination < 2; destination++)
            {
                int withoutSource = (code >> ((pattern << 2) | destination)) & 1;
                int withSource = (code >> ((pattern << 2) | 2 | destination)) & 1;
                if (withoutSource != withSource)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryDrawSourceIndependentRasterOperation(
        PlaybackState state,
        in MetafileRecord record,
        uint rasterOperation,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight)
    {
        if (TryDrawCommonSourceIndependentRasterOperation(
            state,
            record,
            rasterOperation,
            destinationX,
            destinationY,
            destinationWidth,
            destinationHeight))
        {
            return true;
        }

        byte code = checked((byte)(rasterOperation >> 16));
        if (RasterOperationUsesSource(code))
        {
            return false;
        }
        if (destinationWidth == 0 || destinationHeight == 0)
        {
            return true;
        }

        PointF destinationTopLeft = new(destinationX, destinationY);
        PointF destinationTopRight = new(
            AddCoordinate(record, destinationX, destinationWidth),
            destinationY);
        PointF destinationBottomLeft = new(
            destinationX,
            AddCoordinate(record, destinationY, destinationHeight));
        state.ApplyTransform(record);
        state.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        state.Graphics.DrawImageRasterOperation(
            state.RasterOperationCoverageBitmap,
            destinationTopLeft,
            destinationTopRight,
            destinationBottomLeft,
            new RectangleF(0, 0, 1, 1),
            CreateRasterOperation(state, record, rasterOperation));
        return true;
    }

    private static bool TryDrawCommonSourceIndependentRasterOperation(
        PlaybackState state,
        in MetafileRecord record,
        uint rasterOperation,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight)
    {
        Brush? brush = rasterOperation switch
        {
            Blackness => Brushes.Black,
            PatCopy => state.SelectedBrush,
            Whiteness => Brushes.White,
            _ => null
        };
        if (rasterOperation is not Blackness and not PatCopy and not Whiteness)
        {
            return false;
        }
        if (brush is null || destinationWidth == 0 || destinationHeight == 0)
        {
            return true;
        }

        PointF destinationTopLeft = new(destinationX, destinationY);
        PointF destinationTopRight = new(
            AddCoordinate(record, destinationX, destinationWidth),
            destinationY);
        PointF destinationBottomLeft = new(
            destinationX,
            AddCoordinate(record, destinationY, destinationHeight));
        PointF destinationBottomRight = new(
            destinationTopRight.X + destinationBottomLeft.X - destinationTopLeft.X,
            destinationTopRight.Y + destinationBottomLeft.Y - destinationTopLeft.Y);
        state.ApplyTransform(record);
        state.Graphics.FillPolygon(
            brush,
            [
                destinationTopLeft,
                destinationTopRight,
                destinationBottomRight,
                destinationBottomLeft
            ]);
        return true;
    }

    private static PointF InterpolateDestination(
        PointF origin,
        PointF horizontal,
        PointF vertical,
        float u,
        float v) =>
        new(
            origin.X + horizontal.X * u + vertical.X * v,
            origin.Y + horizontal.Y * u + vertical.Y * v);

    private static int AddCoordinate(
        in MetafileRecord record,
        int coordinate,
        int extent)
    {
        long result = (long)coordinate + extent;
        if (result is < int.MinValue or > int.MaxValue)
        {
            throw Invalid(record);
        }
        return (int)result;
    }

    private static ReadOnlySpan<byte> ReadEmfBuffer(
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        uint recordOffset,
        uint size,
        int fixedPayloadSize)
    {
        const int recordHeaderSize = 8;
        if (recordOffset < recordHeaderSize + fixedPayloadSize ||
            recordOffset > int.MaxValue || size > int.MaxValue)
        {
            throw Invalid(record);
        }

        int payloadOffset = (int)recordOffset - recordHeaderSize;
        int length = (int)size;
        if (payloadOffset > payload.Length || length > payload.Length - payloadOffset)
        {
            throw Invalid(record);
        }
        return payload.Slice(payloadOffset, length);
    }

    private static void EnsureDisjointEmfBuffers(
        in MetafileRecord record,
        uint firstOffset,
        uint firstSize,
        uint secondOffset,
        uint secondSize)
    {
        ulong firstEnd = (ulong)firstOffset + firstSize;
        ulong secondEnd = (ulong)secondOffset + secondSize;
        if (firstSize != 0 && secondSize != 0 &&
            firstOffset < secondEnd && secondOffset < firstEnd)
        {
            throw Invalid(record);
        }
    }

    private static DibInfo ReadDibInfo(
        in MetafileRecord record,
        ReadOnlySpan<byte> bitmapInfo,
        uint usage,
        LogicalPalette selectedPalette)
    {
        const int bitmapInfoHeaderSize = 40;
        if (usage is not DibRgbColors and not DibPalColors and not DibPalIndices ||
            bitmapInfo.Length < bitmapInfoHeaderSize)
        {
            throw Invalid(record);
        }

        uint headerSize = ReadUInt32(bitmapInfo, 0);
        if (headerSize is not 40 and not 108 and not 124 || headerSize > (uint)bitmapInfo.Length)
        {
            throw Unsupported(record, "Only BITMAPINFOHEADER, BITMAPV4HEADER, and BITMAPV5HEADER DIBs are supported.");
        }

        int width = ReadInt32(bitmapInfo, 4);
        int signedHeight = ReadInt32(bitmapInfo, 8);
        if (width <= 0 || signedHeight == 0 || signedHeight == int.MinValue ||
            ReadUInt16(bitmapInfo, 12) != 1)
        {
            throw Invalid(record);
        }
        int height = Math.Abs(signedHeight);
        ushort bitCount = ReadUInt16(bitmapInfo, 14);
        uint compression = ReadUInt32(bitmapInfo, 16);
        if (compression is not BiRgb and not BiRle8 and not BiRle4 and
            not BiBitFields and not BiJpeg and not BiPng and not BiCmyk and
            not BiCmykRle8 and not BiCmykRle4)
        {
            throw Unsupported(
                record,
                "Only BI_RGB, BI_RLE8, BI_RLE4, BI_BITFIELDS, BI_JPEG, BI_PNG, BI_CMYK, BI_CMYKRLE8, and BI_CMYKRLE4 DIBs are supported.");
        }
        bool usesEncodedFile = compression is BiJpeg or BiPng;
        if (usesEncodedFile)
        {
            if (usage != DibRgbColors || bitCount != 0 || signedHeight < 0)
            {
                throw Invalid(record);
            }
        }
        else if (bitCount is not 1 and not 4 and not 8 and not 16 and not 24 and not 32)
        {
            throw Unsupported(record, $"DIB bit depth {bitCount} is not supported.");
        }
        bool usesCmyk = compression == BiCmyk;
        if (usesCmyk && bitCount != 32)
        {
            throw Invalid(record);
        }
        bool usesBitFields = compression == BiBitFields;
        if (usesBitFields && bitCount is not 16 and not 32)
        {
            throw Invalid(record);
        }
        bool usesRle8 = IsRle8Compression(compression);
        bool usesRle4 = IsRle4Compression(compression);
        bool usesRle = usesRle8 || usesRle4;
        if (usesRle &&
            (signedHeight < 0 ||
             usesRle8 && bitCount != 8 ||
             usesRle4 && bitCount != 4))
        {
            throw Invalid(record);
        }
        uint imageSize = ReadUInt32(bitmapInfo, 20);
        bool usesCompressedBuffer = usesRle || usesEncodedFile;
        if (usesCompressedBuffer && (imageSize == 0 || imageSize > int.MaxValue))
        {
            throw Invalid(record);
        }

        long rowBits = (long)width * bitCount;
        long rowStride = ((rowBits + 31) / 32) * 4;
        if (rowStride > int.MaxValue || (long)width * height * 4 > int.MaxValue)
        {
            throw Invalid(record);
        }

        uint colorsUsed = ReadUInt32(bitmapInfo, 32);
        int paletteCount = 0;
        int colorTableCount;
        if (usesEncodedFile)
        {
            if (colorsUsed != 0)
            {
                throw Invalid(record);
            }
            colorTableCount = 0;
        }
        else if (usage == DibPalIndices)
        {
            if (bitCount > 8 || colorsUsed != 0)
            {
                throw Invalid(record);
            }
            paletteCount = Math.Min(1 << bitCount, selectedPalette.Count);
            colorTableCount = 0;
        }
        else if (bitCount <= 8)
        {
            int maximumPaletteCount = 1 << bitCount;
            if (colorsUsed > (uint)maximumPaletteCount)
            {
                throw Invalid(record);
            }
            paletteCount = colorsUsed == 0
                ? maximumPaletteCount
                : (int)colorsUsed;
            colorTableCount = paletteCount;
        }
        else
        {
            if (colorsUsed > int.MaxValue)
            {
                throw Invalid(record);
            }
            colorTableCount = (int)colorsUsed;
        }

        int externalMaskBytes = usesBitFields && headerSize == bitmapInfoHeaderSize ? 12 : 0;
        int colorTableEntrySize = usage == DibPalColors ? 2 : 4;
        long requiredInfoSize =
            (long)headerSize + externalMaskBytes + colorTableCount * (long)colorTableEntrySize;
        if (requiredInfoSize > bitmapInfo.Length || requiredInfoSize > int.MaxValue)
        {
            throw Invalid(record);
        }

        if (usage == DibPalColors)
        {
            int colorTableOffset = checked((int)headerSize + externalMaskBytes);
            for (int index = 0; index < colorTableCount; index++)
            {
                ushort paletteIndex = ReadUInt16(
                    bitmapInfo,
                    checked(colorTableOffset + index * 2));
                if (paletteIndex >= selectedPalette.Count)
                {
                    throw Invalid(record);
                }
            }
        }

        uint redMask = 0;
        uint greenMask = 0;
        uint blueMask = 0;
        uint alphaMask = 0;
        if (usesBitFields)
        {
            int maskOffset = headerSize == bitmapInfoHeaderSize
                ? bitmapInfoHeaderSize
                : 40;
            redMask = ReadUInt32(bitmapInfo, maskOffset);
            greenMask = ReadUInt32(bitmapInfo, maskOffset + 4);
            blueMask = ReadUInt32(bitmapInfo, maskOffset + 8);
            alphaMask = headerSize >= 108 ? ReadUInt32(bitmapInfo, 52) : 0;
            ValidateDibMasks(record, bitCount, redMask, greenMask, blueMask, alphaMask);
        }

        return new DibInfo(
            width,
            height,
            signedHeight < 0,
            bitCount,
            (int)rowStride,
            (int)headerSize,
            paletteCount,
            (int)requiredInfoSize,
            usesBitFields,
            redMask,
            greenMask,
            blueMask,
            alphaMask,
            compression,
            usesCompressedBuffer ? (int)imageSize : 0,
            usage,
            selectedPalette);
    }

    private static void ValidateDibMasks(
        in MetafileRecord record,
        ushort bitCount,
        uint redMask,
        uint greenMask,
        uint blueMask,
        uint alphaMask)
    {
        uint pixelMask = bitCount == 32 ? uint.MaxValue : (1u << bitCount) - 1;
        if (!IsContiguousDibMask(redMask, pixelMask) ||
            !IsContiguousDibMask(greenMask, pixelMask) ||
            !IsContiguousDibMask(blueMask, pixelMask) ||
            alphaMask != 0 && !IsContiguousDibMask(alphaMask, pixelMask) ||
            (redMask & greenMask) != 0 ||
            (redMask & blueMask) != 0 ||
            (greenMask & blueMask) != 0 ||
            (alphaMask & (redMask | greenMask | blueMask)) != 0)
        {
            throw Invalid(record);
        }
    }

    private static bool IsContiguousDibMask(uint mask, uint pixelMask)
    {
        if (mask == 0 || (mask & ~pixelMask) != 0)
        {
            return false;
        }

        uint shifted = mask >> BitOperations.TrailingZeroCount(mask);
        return shifted == uint.MaxValue || (shifted & (shifted + 1)) == 0;
    }

    private static Bitmap DecodeDibRows(
        in MetafileRecord record,
        in DibInfo dib,
        ReadOnlySpan<byte> bitmapInfo,
        ReadOnlySpan<byte> bitmapBits,
        int rowCount)
    {
        if (rowCount <= 0 || rowCount > dib.Height)
        {
            throw Invalid(record);
        }
        if (IsRleCompression(dib.Compression))
        {
            return DecodeRleDib(record, dib, bitmapInfo, bitmapBits, rowCount);
        }
        if (dib.Compression is BiJpeg or BiPng)
        {
            return DecodeEncodedDib(record, dib, bitmapBits, rowCount);
        }
        if (bitmapBits.Length != checked(dib.RowStride * rowCount))
        {
            throw Invalid(record);
        }

        byte[] rgba = new byte[checked(dib.Width * rowCount * 4)];
        for (int storedRow = 0; storedRow < rowCount; storedRow++)
        {
            int outputRow = dib.TopDown ? storedRow : rowCount - storedRow - 1;
            ReadOnlySpan<byte> source = bitmapBits.Slice(storedRow * dib.RowStride, dib.RowStride);
            Span<byte> destination = rgba.AsSpan(outputRow * dib.Width * 4, dib.Width * 4);
            DecodeDibRow(record, dib, bitmapInfo, source, destination);
        }
        return Bitmap.CreateOwnedRgba(dib.Width, rowCount, rgba);
    }

    private static Bitmap DecodeEncodedDib(
        in MetafileRecord record,
        in DibInfo dib,
        ReadOnlySpan<byte> bitmapBits,
        int rowCount)
    {
        if (rowCount != dib.Height || bitmapBits.Length != dib.CompressedSize ||
            dib.Compression == BiJpeg && !HasJpegSignature(bitmapBits) ||
            dib.Compression == BiPng && !HasPngSignature(bitmapBits))
        {
            throw Invalid(record);
        }

        try
        {
            return Bitmap.CreateFromEncodedImage(bitmapBits, dib.Width, dib.Height);
        }
        catch (ArgumentException)
        {
            throw Invalid(record);
        }
    }

    private static bool HasJpegSignature(ReadOnlySpan<byte> bitmapBits) =>
        bitmapBits.Length >= 3 &&
        bitmapBits[0] == 0xFF &&
        bitmapBits[1] == 0xD8 &&
        bitmapBits[2] == 0xFF;

    private static bool HasPngSignature(ReadOnlySpan<byte> bitmapBits) =>
        bitmapBits.Length >= 8 &&
        bitmapBits[0] == 0x89 &&
        bitmapBits[1] == 0x50 &&
        bitmapBits[2] == 0x4E &&
        bitmapBits[3] == 0x47 &&
        bitmapBits[4] == 0x0D &&
        bitmapBits[5] == 0x0A &&
        bitmapBits[6] == 0x1A &&
        bitmapBits[7] == 0x0A;

    private static Bitmap DecodeRleDib(
        in MetafileRecord record,
        in DibInfo dib,
        ReadOnlySpan<byte> bitmapInfo,
        ReadOnlySpan<byte> bitmapBits,
        int rowCount)
    {
        if (bitmapBits.Length != dib.CompressedSize)
        {
            throw Invalid(record);
        }

        byte[] indices = new byte[checked(dib.Width * rowCount)];
        bool rle8 = IsRle8Compression(dib.Compression);
        int cursor = 0;
        int x = 0;
        int y = 0;
        bool ended = false;
        while (cursor < bitmapBits.Length)
        {
            if (bitmapBits.Length - cursor < 2)
            {
                throw Invalid(record);
            }
            int count = bitmapBits[cursor++];
            byte value = bitmapBits[cursor++];
            if (count != 0)
            {
                EnsureRleRunFits(record, dib, rowCount, x, y, count);
                for (int index = 0; index < count; index++)
                {
                    indices[y * dib.Width + x + index] = rle8
                        ? value
                        : (byte)((index & 1) == 0 ? value >> 4 : value & 0x0F);
                }
                x += count;
                continue;
            }

            switch (value)
            {
                case 0:
                    if (y >= rowCount)
                    {
                        throw Invalid(record);
                    }
                    x = 0;
                    y++;
                    break;
                case 1:
                    ended = true;
                    break;
                case 2:
                    if (bitmapBits.Length - cursor < 2 || y >= rowCount)
                    {
                        throw Invalid(record);
                    }
                    int deltaX = bitmapBits[cursor++];
                    int deltaY = bitmapBits[cursor++];
                    if (deltaX > dib.Width - x || deltaY >= rowCount - y)
                    {
                        throw Invalid(record);
                    }
                    x += deltaX;
                    y += deltaY;
                    break;
                default:
                    int absoluteCount = value;
                    EnsureRleRunFits(record, dib, rowCount, x, y, absoluteCount);
                    int dataBytes = rle8
                        ? absoluteCount
                        : (absoluteCount + 1) / 2;
                    int alignedBytes = (dataBytes + 1) & ~1;
                    if (alignedBytes > bitmapBits.Length - cursor)
                    {
                        throw Invalid(record);
                    }
                    for (int index = 0; index < absoluteCount; index++)
                    {
                        byte packed = bitmapBits[cursor + (rle8 ? index : index / 2)];
                        indices[y * dib.Width + x + index] = rle8
                            ? packed
                            : (byte)((index & 1) == 0 ? packed >> 4 : packed & 0x0F);
                    }
                    for (int padding = dataBytes; padding < alignedBytes; padding++)
                    {
                        if (bitmapBits[cursor + padding] != 0)
                        {
                            throw Invalid(record);
                        }
                    }
                    cursor += alignedBytes;
                    x += absoluteCount;
                    break;
            }
            if (ended)
            {
                break;
            }
        }
        if (!ended || cursor != bitmapBits.Length)
        {
            throw Invalid(record);
        }

        byte[] rgba = new byte[checked(dib.Width * rowCount * 4)];
        for (int storedY = 0; storedY < rowCount; storedY++)
        {
            int outputY = rowCount - storedY - 1;
            for (int pixelX = 0; pixelX < dib.Width; pixelX++)
            {
                ReadPaletteColor(
                    record,
                    dib,
                    bitmapInfo,
                    indices[storedY * dib.Width + pixelX],
                    out byte red,
                    out byte green,
                    out byte blue);
                int destination = (outputY * dib.Width + pixelX) * 4;
                rgba[destination] = red;
                rgba[destination + 1] = green;
                rgba[destination + 2] = blue;
                rgba[destination + 3] = byte.MaxValue;
            }
        }
        return Bitmap.CreateOwnedRgba(dib.Width, rowCount, rgba);
    }

    private static void EnsureRleRunFits(
        in MetafileRecord record,
        in DibInfo dib,
        int rowCount,
        int x,
        int y,
        int count)
    {
        if (y >= rowCount || count > dib.Width - x)
        {
            throw Invalid(record);
        }
    }

    private static void DecodeDibRow(
        in MetafileRecord record,
        in DibInfo dib,
        ReadOnlySpan<byte> bitmapInfo,
        ReadOnlySpan<byte> source,
        Span<byte> destination)
    {
        for (int x = 0; x < dib.Width; x++)
        {
            int destinationOffset = x * 4;
            byte red;
            byte green;
            byte blue;
            byte alpha = byte.MaxValue;
            switch (dib.BitCount)
            {
                case 1:
                    ReadPaletteColor(
                        record,
                        dib,
                        bitmapInfo,
                        (source[x >> 3] >> (7 - (x & 7))) & 1,
                        out red,
                        out green,
                        out blue);
                    break;
                case 4:
                    byte packed = source[x >> 1];
                    int index = (x & 1) == 0 ? packed >> 4 : packed & 0x0F;
                    ReadPaletteColor(record, dib, bitmapInfo, index, out red, out green, out blue);
                    break;
                case 8:
                    ReadPaletteColor(record, dib, bitmapInfo, source[x], out red, out green, out blue);
                    break;
                case 16:
                    ushort pixel16 = ReadUInt16(source, x * 2);
                    if (dib.UsesBitFields)
                    {
                        red = ReadMaskedColor(pixel16, dib.RedMask);
                        green = ReadMaskedColor(pixel16, dib.GreenMask);
                        blue = ReadMaskedColor(pixel16, dib.BlueMask);
                        alpha = ReadMaskedAlpha(pixel16, dib.AlphaMask);
                    }
                    else
                    {
                        red = ExpandFiveBits((pixel16 >> 10) & 0x1F);
                        green = ExpandFiveBits((pixel16 >> 5) & 0x1F);
                        blue = ExpandFiveBits(pixel16 & 0x1F);
                    }
                    break;
                case 24:
                    blue = source[x * 3];
                    green = source[x * 3 + 1];
                    red = source[x * 3 + 2];
                    break;
                case 32:
                    uint pixel32 = ReadUInt32(source, x * 4);
                    if (dib.Compression == BiCmyk)
                    {
                        byte cyan = (byte)pixel32;
                        byte magenta = (byte)(pixel32 >> 8);
                        byte yellow = (byte)(pixel32 >> 16);
                        byte black = (byte)(pixel32 >> 24);
                        red = ConvertCmykChannel(cyan, black);
                        green = ConvertCmykChannel(magenta, black);
                        blue = ConvertCmykChannel(yellow, black);
                    }
                    else if (dib.UsesBitFields)
                    {
                        red = ReadMaskedColor(pixel32, dib.RedMask);
                        green = ReadMaskedColor(pixel32, dib.GreenMask);
                        blue = ReadMaskedColor(pixel32, dib.BlueMask);
                        alpha = ReadMaskedAlpha(pixel32, dib.AlphaMask);
                    }
                    else
                    {
                        blue = (byte)pixel32;
                        green = (byte)(pixel32 >> 8);
                        red = (byte)(pixel32 >> 16);
                    }
                    break;
                default:
                    throw Unsupported(record);
            }

            destination[destinationOffset] = red;
            destination[destinationOffset + 1] = green;
            destination[destinationOffset + 2] = blue;
            destination[destinationOffset + 3] = alpha;
        }
    }

    private static byte ReadMaskedColor(uint pixel, uint mask)
    {
        int shift = BitOperations.TrailingZeroCount(mask);
        uint maximum = mask >> shift;
        uint value = (pixel & mask) >> shift;
        return (byte)(((ulong)value * byte.MaxValue + maximum / 2) / maximum);
    }

    private static byte ReadMaskedAlpha(uint pixel, uint mask) =>
        mask == 0 ? byte.MaxValue : ReadMaskedColor(pixel, mask);

    private static byte ConvertCmykChannel(byte colorant, byte black) =>
        (byte)(((byte.MaxValue - colorant) * (byte.MaxValue - black) + 127) /
            byte.MaxValue);

    private static bool IsRleCompression(uint compression) =>
        IsRle8Compression(compression) || IsRle4Compression(compression);

    private static bool IsRle8Compression(uint compression) =>
        compression is BiRle8 or BiCmykRle8;

    private static bool IsRle4Compression(uint compression) =>
        compression is BiRle4 or BiCmykRle4;

    private static void ReadPaletteColor(
        in MetafileRecord record,
        in DibInfo dib,
        ReadOnlySpan<byte> bitmapInfo,
        int index,
        out byte red,
        out byte green,
        out byte blue)
    {
        if ((uint)index >= (uint)dib.PaletteCount)
        {
            throw Invalid(record);
        }
        if (dib.ColorUsage == DibPalIndices)
        {
            Color color = dib.LogicalPalette.GetColor(index);
            red = color.R;
            green = color.G;
            blue = color.B;
            return;
        }
        if (dib.ColorUsage == DibPalColors)
        {
            int offset = checked(dib.HeaderSize + index * 2);
            Color color = dib.LogicalPalette.GetColor(ReadUInt16(bitmapInfo, offset));
            red = color.R;
            green = color.G;
            blue = color.B;
            return;
        }

        int rgbOffset = checked(dib.HeaderSize + index * 4);
        blue = bitmapInfo[rgbOffset];
        green = bitmapInfo[rgbOffset + 1];
        red = bitmapInfo[rgbOffset + 2];
    }

    private static byte ExpandFiveBits(int value) =>
        (byte)((value * 255 + 15) / 31);

    private readonly record struct DibInfo(
        int Width,
        int Height,
        bool TopDown,
        ushort BitCount,
        int RowStride,
        int HeaderSize,
        int PaletteCount,
        int BitmapInfoSize,
        bool UsesBitFields,
        uint RedMask,
        uint GreenMask,
        uint BlueMask,
        uint AlphaMask,
        uint Compression,
        int CompressedSize,
        uint ColorUsage,
        LogicalPalette LogicalPalette);

    private static void DrawRectangle(
        PlaybackState state,
        in MetafileRecord record,
        Rectangle rectangle)
    {
        if (state.IsPathBracketOpen)
        {
            using var path = new GraphicsPath();
            path.AddRectangle(rectangle);
            state.CapturePath(
                record,
                path,
                connect: false,
                continueFigure: false);
            return;
        }

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

    private static void SelectEmfClipRegion(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        const int FixedPayloadSize = 8;
        const int RegionDataHeaderSize = 32;
        const int RectangleSize = 16;
        if (payload.Length < FixedPayloadSize)
        {
            throw Invalid(record);
        }

        uint dataSize = ReadUInt32(payload, 0);
        int mode = ReadInt32(payload, 4);
        if (dataSize > int.MaxValue || payload.Length != FixedPayloadSize + (int)dataSize)
        {
            throw Invalid(record);
        }

        if (dataSize == 0)
        {
            state.SelectClipRegion(region: null, mode, record);
            return;
        }
        if (dataSize < RegionDataHeaderSize)
        {
            throw Invalid(record);
        }

        ReadOnlySpan<byte> data = payload[FixedPayloadSize..];
        uint headerSize = ReadUInt32(data, 0);
        uint type = ReadUInt32(data, 4);
        uint rectangleCount = ReadUInt32(data, 8);
        uint rectangleBytes = ReadUInt32(data, 12);
        if (headerSize != RegionDataHeaderSize || type != 1 ||
            rectangleCount > int.MaxValue / RectangleSize ||
            rectangleBytes != rectangleCount * RectangleSize ||
            data.Length != RegionDataHeaderSize + (int)rectangleBytes)
        {
            throw Invalid(record);
        }

        Rectangle bounds = ReadRectangle(record, data[16..32]);
        if (rectangleCount == 0)
        {
            if (bounds != Rectangle.Empty)
            {
                throw Invalid(record);
            }
            using var empty = new Region();
            empty.MakeEmpty();
            state.SelectClipRegion(empty, mode, record);
            return;
        }

        int minimumX = int.MaxValue;
        int minimumY = int.MaxValue;
        int maximumX = int.MinValue;
        int maximumY = int.MinValue;
        for (int index = 0; index < rectangleCount; index++)
        {
            int offset = RegionDataHeaderSize + index * RectangleSize;
            Rectangle rectangle = ReadRectangle(record, data.Slice(offset, RectangleSize));
            if (rectangle.Left < bounds.Left || rectangle.Top < bounds.Top ||
                rectangle.Right > bounds.Right || rectangle.Bottom > bounds.Bottom)
            {
                throw Invalid(record);
            }

            minimumX = Math.Min(minimumX, rectangle.Left);
            minimumY = Math.Min(minimumY, rectangle.Top);
            maximumX = Math.Max(maximumX, rectangle.Right);
            maximumY = Math.Max(maximumY, rectangle.Bottom);
        }
        if (minimumX != bounds.Left || minimumY != bounds.Top ||
            maximumX != bounds.Right || maximumY != bounds.Bottom)
        {
            throw Invalid(record);
        }

        using Region region = CreateRectangleRegion(data, (int)rectangleCount);
        state.SelectClipRegion(region, mode, record);
    }

    private static Region CreateRectangleRegion(
        ReadOnlySpan<byte> data,
        int rectangleCount)
    {
        const int RegionDataHeaderSize = 32;
        const int RectangleSize = 16;
        var levels = new Region?[32];
        try
        {
            for (int index = 0; index < rectangleCount; index++)
            {
                int offset = RegionDataHeaderSize + index * RectangleSize;
                var rectangle = Rectangle.FromLTRB(
                    ReadInt32(data, offset),
                    ReadInt32(data, offset + 4),
                    ReadInt32(data, offset + 8),
                    ReadInt32(data, offset + 12));
                Region current = new(rectangle);
                for (int level = 0; ; level++)
                {
                    if (levels[level] is null)
                    {
                        levels[level] = current;
                        break;
                    }

                    Region lower = levels[level]!;
                    levels[level] = null;
                    lower.Union(current);
                    current.Dispose();
                    current = lower;
                }
            }

            Region? result = null;
            for (int level = 0; level < levels.Length; level++)
            {
                Region? next = levels[level];
                if (next is null)
                {
                    continue;
                }
                levels[level] = null;
                if (result is null)
                {
                    result = next;
                }
                else
                {
                    result.Union(next);
                    next.Dispose();
                }
            }
            return result ?? throw new InvalidOperationException("An EMF rectangle region was unexpectedly empty.");
        }
        finally
        {
            foreach (Region? region in levels)
            {
                region?.Dispose();
            }
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
        if (state.IsPathBracketOpen)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(rectangle);
            state.CapturePath(
                record,
                path,
                connect: false,
                continueFigure: false);
            return;
        }

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
        if (state.IsPathBracketOpen)
        {
            using var path = new GraphicsPath();
            path.AddRoundedRectangle(rectangle, cornerEllipse);
            state.CapturePath(
                record,
                path,
                connect: false,
                continueFigure: false);
            return;
        }

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

    private static void DrawEmfRoundRectangle(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        RequireSize(record, payload, 24);
        Rectangle rectangle = ReadRectangle(record, payload);
        Size cornerEllipse = new(
            ReadInt32(payload, 16),
            ReadInt32(payload, 20));
        if (state.IsPathBracketOpen)
        {
            using var path = new GraphicsPath();
            path.AddRoundedRectangle(rectangle, cornerEllipse);
            state.CapturePath(
                record,
                path,
                connect: false,
                continueFigure: false);
            return;
        }

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
        bool close,
        bool points16 = false)
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
        int pointSize = points16 ? 4 : 8;
        int expectedSize = checked(20 + count * pointSize);
        RequireSize(record, payload, expectedSize);
        if (count < (close ? 3 : 2))
        {
            throw Invalid(record);
        }

        var points = new Point[count];
        int cursor = 20;
        for (int index = 0; index < count; index++)
        {
            points[index] = ReadEmfPoint(payload[cursor..], points16);
            cursor += pointSize;
        }

        if (state.IsPathBracketOpen)
        {
            using var path = new GraphicsPath();
            if (close)
            {
                path.AddPolygon(points);
            }
            else
            {
                path.AddLines(points);
            }
            state.CapturePath(
                record,
                path,
                connect: false,
                continueFigure: false);
            return;
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
        bool close,
        bool points16 = false)
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
            expectedSize = checked(pointsOffset + pointCount * (points16 ? 4 : 8));
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

        GraphicsPath? capturedPath = state.IsPathBracketOpen ? new GraphicsPath() : null;
        if (capturedPath is null)
        {
            state.ApplyTransform(record);
        }
        int pointSize = points16 ? 4 : 8;
        int pointIndex = 0;
        try
        {
            for (int polygonIndex = 0; polygonIndex < polygonCount; polygonIndex++)
            {
                int currentCount = checked((int)ReadUInt32(payload, 24 + polygonIndex * 4));
                var points = new Point[currentCount];
                for (int index = 0; index < currentCount; index++)
                {
                    points[index] = ReadEmfPoint(
                        payload[(pointsOffset + pointIndex * pointSize)..],
                        points16);
                    pointIndex++;
                }

                if (capturedPath is not null)
                {
                    if (close)
                    {
                        capturedPath.AddPolygon(points);
                    }
                    else
                    {
                        capturedPath.StartFigure();
                        capturedPath.AddLines(points);
                    }
                    continue;
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

            if (capturedPath is not null)
            {
                state.CapturePath(
                    record,
                    capturedPath,
                    connect: false,
                    continueFigure: false);
            }
        }
        finally
        {
            capturedPath?.Dispose();
        }
    }

    private static void DrawEmfBezier(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        bool fromCurrentPosition,
        bool points16)
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
        if (fromCurrentPosition
            ? count < 3 || count % 3 != 0
            : count < 4 || (count - 1) % 3 != 0)
        {
            throw Invalid(record);
        }

        int pointSize = points16 ? 4 : 8;
        RequireSize(record, payload, checked(20 + count * pointSize));
        int destinationOffset = fromCurrentPosition ? 1 : 0;
        var points = new Point[count + destinationOffset];
        if (fromCurrentPosition)
        {
            points[0] = state.CurrentPoint;
        }
        for (int index = 0; index < count; index++)
        {
            points[index + destinationOffset] = ReadEmfPoint(
                payload[(20 + index * pointSize)..],
                points16);
        }

        using var path = new GraphicsPath();
        path.AddBeziers(points);
        if (!state.CapturePath(
                record,
                path,
                connect: fromCurrentPosition,
                continueFigure: fromCurrentPosition))
        {
            state.ApplyTransform(record);
            if (state.SelectedPen is Pen pen)
            {
                state.Graphics.DrawBeziers(pen, points);
            }
        }
        if (fromCurrentPosition)
        {
            state.CurrentPoint = points[^1];
        }
    }

    private static void DrawEmfPolylineTo(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        bool points16)
    {
        if (payload.Length < 20)
        {
            throw Invalid(record);
        }

        uint countValue = ReadUInt32(payload, 16);
        if (countValue is 0 or > 1_000_000)
        {
            throw Invalid(record);
        }

        int count = checked((int)countValue);
        int pointSize = points16 ? 4 : 8;
        RequireSize(record, payload, checked(20 + count * pointSize));
        var points = new Point[count + 1];
        points[0] = state.CurrentPoint;
        for (int index = 0; index < count; index++)
        {
            points[index + 1] = ReadEmfPoint(
                payload[(20 + index * pointSize)..],
                points16);
        }

        using var path = new GraphicsPath();
        path.AddLines(points);
        if (!state.CapturePath(
                record,
                path,
                connect: true,
                continueFigure: true))
        {
            state.ApplyTransform(record);
            if (state.SelectedPen is Pen pen)
            {
                state.Graphics.DrawLines(pen, points);
            }
        }
        state.CurrentPoint = points[^1];
    }

    private static void DrawEmfPolyDraw(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        bool points16)
    {
        if (payload.Length < 20)
        {
            throw Invalid(record);
        }

        uint countValue = ReadUInt32(payload, 16);
        if (countValue is 0 or > 1_000_000)
        {
            throw Invalid(record);
        }

        int count = checked((int)countValue);
        int pointSize = points16 ? 4 : 8;
        int typesOffset = checked(20 + count * pointSize);
        int expectedSize = checked((typesOffset + count + 3) & ~3);
        RequireSize(record, payload, expectedSize);

        var points = new Point[count];
        for (int index = 0; index < count; index++)
        {
            points[index] = ReadEmfPoint(
                payload[(20 + index * pointSize)..],
                points16);
        }

        Point current = state.CurrentPoint;
        Point figureStart = state.FigureStart;
        Point pathFigureStart = current;
        bool hasDrawnSegment = false;
        bool figureClosedAtEnd = false;
        using var path = new GraphicsPath();
        for (int index = 0; index < count; index++)
        {
            byte type = payload[typesOffset + index];
            bool closeFigure = (type & 0x01) != 0;
            switch (type & 0xFE)
            {
                case 0x06:
                    if (closeFigure)
                    {
                        throw Invalid(record);
                    }
                    path.StartFigure();
                    current = points[index];
                    figureStart = current;
                    pathFigureStart = current;
                    figureClosedAtEnd = false;
                    break;

                case 0x02:
                    path.AddLine(current, points[index]);
                    current = points[index];
                    hasDrawnSegment = true;
                    if (closeFigure)
                    {
                        ClosePolyDrawFigure(path, current, figureStart, pathFigureStart);
                        current = figureStart;
                        pathFigureStart = current;
                        figureClosedAtEnd = true;
                    }
                    else
                    {
                        figureClosedAtEnd = false;
                    }
                    break;

                case 0x04:
                    if (index + 2 >= count || closeFigure ||
                        (payload[typesOffset + index + 1] & 0xFF) != 0x04 ||
                        (payload[typesOffset + index + 2] & 0xFE) != 0x04)
                    {
                        throw Invalid(record);
                    }
                    path.AddBezier(
                        current,
                        points[index],
                        points[index + 1],
                        points[index + 2]);
                    hasDrawnSegment = true;
                    bool closeBezier = (payload[typesOffset + index + 2] & 0x01) != 0;
                    current = points[index + 2];
                    if (closeBezier)
                    {
                        ClosePolyDrawFigure(path, current, figureStart, pathFigureStart);
                        current = figureStart;
                        pathFigureStart = current;
                        figureClosedAtEnd = true;
                    }
                    else
                    {
                        figureClosedAtEnd = false;
                    }
                    index += 2;
                    break;

                default:
                    throw Invalid(record);
            }
        }

        bool captured = state.CapturePath(
            record,
            path,
            connect: (payload[typesOffset] & 0xFE) != 0x06,
            continueFigure: hasDrawnSegment && !figureClosedAtEnd);
        if (!captured)
        {
            state.ApplyTransform(record);
            if (hasDrawnSegment && state.SelectedPen is Pen pen)
            {
                state.Graphics.DrawPath(pen, path);
            }
        }
        state.CompletePolyDraw(current, figureStart);
    }

    private static void ClosePolyDrawFigure(
        GraphicsPath path,
        Point current,
        Point figureStart,
        Point pathFigureStart)
    {
        if (pathFigureStart == figureStart)
        {
            path.CloseFigure();
        }
        else
        {
            path.AddLine(current, figureStart);
            path.StartFigure();
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

        if (TryDrawSourceIndependentRasterOperation(
            state,
            record,
            rasterOperation,
            x,
            y,
            width,
            height))
        {
            return;
        }

        throw Unsupported(
            record,
            $"Ternary raster operation 0x{rasterOperation:X8} requires an unavailable source bitmap.");
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
        ArcClosure closure)
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
        DrawArcFamily(
            state,
            record,
            rectangle,
            start,
            end,
            closure,
            arcDirection: 1);
    }

    private static void DrawEmfArcFamily(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload,
        ArcClosure closure)
    {
        RequireSize(record, payload, 32);
        Rectangle rectangle = ReadRectangle(record, payload);
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            throw Invalid(record);
        }

        DrawArcFamily(
            state,
            record,
            rectangle,
            ReadPoint(payload[16..]),
            ReadPoint(payload[24..]),
            closure,
            state.ArcDirection);
    }

    private static void DrawEmfArcTo(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        RequireSize(record, payload, 32);
        Rectangle rectangle = ReadRectangle(record, payload);
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            throw Invalid(record);
        }

        ArcGeometry geometry = GetArcGeometry(
            rectangle,
            ReadPoint(payload[16..]),
            ReadPoint(payload[24..]),
            state.ArcDirection);
        Point next = RoundLogicalPoint(record, geometry.EndPoint);
        using (var path = new GraphicsPath())
        {
            path.AddLine(
                state.CurrentPoint.X,
                state.CurrentPoint.Y,
                geometry.StartPoint.X,
                geometry.StartPoint.Y);
            path.AddArc(rectangle, geometry.StartAngle, geometry.SweepAngle);
            if (!state.CapturePath(
                    record,
                    path,
                    connect: true,
                    continueFigure: true))
            {
                state.ApplyTransform(record);
                if (state.SelectedPen is Pen pen)
                {
                    state.Graphics.DrawPath(pen, path);
                }
            }
        }
        state.CurrentPoint = next;
    }

    private static void DrawEmfAngleArc(
        PlaybackState state,
        in MetafileRecord record,
        ReadOnlySpan<byte> payload)
    {
        RequireSize(record, payload, 20);
        Point center = ReadPoint(payload);
        uint radiusValue = ReadUInt32(payload, 8);
        float startAngle = ReadSingle(payload, 12);
        float sweepAngle = ReadSingle(payload, 16);
        if (radiusValue == 0 || !float.IsFinite(startAngle) || !float.IsFinite(sweepAngle))
        {
            throw Invalid(record);
        }

        float radius = radiusValue;
        var rectangle = new RectangleF(
            center.X - radius,
            center.Y - radius,
            radius * 2f,
            radius * 2f);
        if (!float.IsFinite(rectangle.X) || !float.IsFinite(rectangle.Y) ||
            !float.IsFinite(rectangle.Width) || !float.IsFinite(rectangle.Height))
        {
            throw Invalid(record);
        }

        // GDI AngleArc angles are counterclockwise; managed drawing angles are
        // clockwise in the usual downward-positive device coordinate system.
        float managedStart = -MathF.IEEERemainder(startAngle, 360f);
        float managedSweep = -sweepAngle;
        float renderSweep = MathF.Abs(managedSweep) >= 360f
            ? MathF.CopySign(360f, managedSweep)
            : managedSweep;
        PointF arcStart = PointOnEllipse(rectangle, managedStart);
        double endAngle = Math.IEEERemainder(
            -(double)startAngle - sweepAngle,
            360d);
        PointF arcEnd = PointOnEllipse(rectangle, (float)endAngle);
        Point next = RoundLogicalPoint(record, arcEnd);

        using (var path = new GraphicsPath())
        {
            path.AddLine(state.CurrentPoint.X, state.CurrentPoint.Y, arcStart.X, arcStart.Y);
            if (renderSweep != 0f)
            {
                path.AddArc(rectangle, managedStart, renderSweep);
            }
            if (!state.CapturePath(
                    record,
                    path,
                    connect: true,
                    continueFigure: true))
            {
                state.ApplyTransform(record);
                if (state.SelectedPen is Pen pen)
                {
                    state.Graphics.DrawPath(pen, path);
                }
            }
        }
        state.CurrentPoint = next;
    }

    private static void DrawArcFamily(
        PlaybackState state,
        in MetafileRecord record,
        Rectangle rectangle,
        Point start,
        Point end,
        ArcClosure closure,
        int arcDirection)
    {
        ArcGeometry geometry = GetArcGeometry(rectangle, start, end, arcDirection);
        float startAngle = geometry.StartAngle;
        float sweepAngle = geometry.SweepAngle;

        if (closure == ArcClosure.Open)
        {
            if (state.IsPathBracketOpen)
            {
                using var openPath = new GraphicsPath();
                openPath.AddArc(rectangle, startAngle, sweepAngle);
                state.CapturePath(
                    record,
                    openPath,
                    connect: false,
                    continueFigure: false);
                return;
            }

            state.ApplyTransform(record);
            if (state.SelectedPen is Pen openPen)
            {
                state.Graphics.DrawArc(openPen, rectangle, startAngle, sweepAngle);
            }
            return;
        }

        using var path = new GraphicsPath();
        if (closure == ArcClosure.Pie)
        {
            path.AddPie(rectangle, startAngle, sweepAngle);
        }
        else
        {
            path.AddArc(rectangle, startAngle, sweepAngle);
            path.CloseFigure();
        }

        if (state.CapturePath(
                record,
                path,
                connect: false,
                continueFigure: false))
        {
            return;
        }

        state.ApplyTransform(record);
        if (state.SelectedBrush is null && state.SelectedPen is null)
        {
            return;
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

    private static ArcGeometry GetArcGeometry(
        Rectangle rectangle,
        Point start,
        Point end,
        int arcDirection)
    {
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
        if (arcDirection == 1)
        {
            if (sweepAngle >= 0f)
            {
                sweepAngle -= 360f;
            }
        }
        else if (sweepAngle <= 0f)
        {
            sweepAngle += 360f;
        }

        return new ArcGeometry(
            startAngle,
            sweepAngle,
            new PointF(
                centerX + radiusX * MathF.Cos(startAngle * (MathF.PI / 180f)),
                centerY + radiusY * MathF.Sin(startAngle * (MathF.PI / 180f))),
            new PointF(
                centerX + radiusX * MathF.Cos((startAngle + sweepAngle) * (MathF.PI / 180f)),
                centerY + radiusY * MathF.Sin((startAngle + sweepAngle) * (MathF.PI / 180f))));
    }

    private static PointF PointOnEllipse(RectangleF rectangle, float angle)
    {
        double radians = angle * Math.PI / 180d;
        return new PointF(
            rectangle.Left + rectangle.Width / 2f + rectangle.Width / 2f * (float)Math.Cos(radians),
            rectangle.Top + rectangle.Height / 2f + rectangle.Height / 2f * (float)Math.Sin(radians));
    }

    private static Point RoundLogicalPoint(in MetafileRecord record, PointF point)
    {
        float x = MathF.Round(point.X);
        float y = MathF.Round(point.Y);
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            x < int.MinValue || x > int.MaxValue ||
            y < int.MinValue || y > int.MaxValue)
        {
            throw Invalid(record);
        }
        return new Point((int)x, (int)y);
    }

    private readonly record struct ArcGeometry(
        float StartAngle,
        float SweepAngle,
        PointF StartPoint,
        PointF EndPoint);

    private enum ArcClosure
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

    private static Point ReadEmfPoint(ReadOnlySpan<byte> payload, bool point16) =>
        point16
            ? new Point(ReadInt16(payload, 0), ReadInt16(payload, 2))
            : ReadPoint(payload);

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
        private object? _selectedBrushObject = Brushes.White;
        private Font _selectedFont = SystemFonts.DefaultFont;
        private object? _selectedFontObject;
        private int _selectedFontEscapement;
        private LogicalPalette _selectedPalette = LogicalPalette.Default;
        private SolidBrush? _textBrush;
        private SolidBrush? _backgroundBrush;
        private HatchBrush? _resolvedHatchBrush;
        private GdiHatchBrushObject? _resolvedHatchBrushObject;
        private Color _resolvedHatchBackground;
        private TextureBrush? _resolvedPatternBrush;
        private GdiPatternBrushObject? _resolvedPatternBrushObject;
        private Point _resolvedPatternBrushOrigin;
        private HatchBrush? _resolvedRasterOperationHatchBrush;
        private Point _resolvedRasterOperationOrigin;
        private TilePatternBrush? _resolvedRasterOperationPattern;
        private TextureBrush? _resolvedRasterOperationTextureBrush;
        private GpuRasterTexturePattern? _resolvedRasterOperationTexturePattern;
        private Bitmap? _rasterOperationCoverageBitmap;
        private GraphicsPath? _buildingPath;
        private GraphicsPath? _selectedPath;
        private PointF? _pathMoveDevicePoint;
        private bool _pathBracketOpen;
        private bool _pathConnectNext;
        private Region _metaClip;
        private Region? _applicationClip;
        private bool _metaClipRequiresMaterialization;
        private bool _applicationClipRequiresMaterialization;

        internal PlaybackState(Graphics graphics, int wmfObjectCapacity)
        {
            Graphics = graphics;
            _wmfObjectCapacity = wmfObjectCapacity;
            _metaClip = graphics.Clip;
        }

        internal Graphics Graphics { get; }
        internal Bitmap RasterOperationCoverageBitmap =>
            _rasterOperationCoverageBitmap ??=
                Bitmap.CreateOwnedRgba(1, 1, [0, 0, 0, byte.MaxValue]);
        internal Point WindowOrigin { get; set; }
        internal Point WindowExtent { get; set; } = new(1, 1);
        internal Point ViewportOrigin { get; set; }
        internal Point ViewportExtent { get; set; } = new(1, 1);
        internal Point CurrentPoint { get; set; }
        internal Point FigureStart { get; private set; }
        internal Matrix3x2 WorldTransform { get; set; } = Matrix3x2.Identity;
        internal FillMode FillMode { get; set; } = FillMode.Alternate;
        internal int MapMode { get; set; } = 1;
        internal int BackgroundMode { get; private set; } = 2;
        internal int RasterOperation { get; set; } = 13;
        internal InterpolationMode DibInterpolationMode { get; private set; } =
            InterpolationMode.NearestNeighbor;
        internal int ArcDirection { get; private set; } = 1;
        internal float MiterLimit { get; private set; } = 10f;
        internal int TextAlignment { get; set; }
        internal int TextCharacterExtra { get; set; }
        internal int TextJustificationExtra { get; private set; }
        internal int TextJustificationBreakCount { get; private set; }
        internal int TextJustificationError { get; private set; }
        private Color _backgroundColor = Color.White;
        internal Color BackgroundColor
        {
            get => _backgroundColor;
            set
            {
                if (_backgroundColor != value)
                {
                    _backgroundColor = value;
                    InvalidateResolvedBrushes();
                }
            }
        }
        internal Color TextColor { get; set; } = Color.Black;
        internal Pen? SelectedPen => _selectedPen;
        internal Brush? SelectedBrush => ResolveSelectedBrush();
        internal Font SelectedFont => _selectedFont;
        internal LogicalPalette SelectedPalette => _selectedPalette;
        internal bool IsPathBracketOpen => _pathBracketOpen;

        private Brush? ResolveSelectedBrush()
        {
            if (_selectedBrushObject is GdiPatternBrushObject patternBrush)
            {
                Point origin = Graphics.RenderingOrigin;
                if (!ReferenceEquals(_resolvedPatternBrushObject, patternBrush) ||
                    _resolvedPatternBrushOrigin != origin)
                {
                    _resolvedPatternBrush?.Dispose();
                    _resolvedPatternBrush = new TextureBrush(patternBrush.Bitmap, WrapMode.Tile);
                    _resolvedPatternBrush.TranslateTransform(origin.X, origin.Y);
                    _resolvedPatternBrushObject = patternBrush;
                    _resolvedPatternBrushOrigin = origin;
                }
                return _resolvedPatternBrush;
            }

            if (_selectedBrushObject is not GdiHatchBrushObject hatchBrush)
            {
                return _selectedBrushObject as Brush;
            }

            Color background = BackgroundMode == 1
                ? Color.Transparent
                : BackgroundColor;
            if (!ReferenceEquals(_resolvedHatchBrushObject, hatchBrush) ||
                _resolvedHatchBackground != background)
            {
                InvalidateResolvedBrushes();
                _resolvedHatchBrush = new HatchBrush(
                    hatchBrush.Style,
                    hatchBrush.ForegroundColor,
                    background);
                _resolvedHatchBrushObject = hatchBrush;
                _resolvedHatchBackground = background;
            }
            return _resolvedHatchBrush;
        }

        private void InvalidateResolvedBrushes()
        {
            _resolvedHatchBrush?.Dispose();
            _resolvedHatchBrush = null;
            _resolvedHatchBrushObject = null;
            _resolvedHatchBackground = default;
            _resolvedRasterOperationHatchBrush = null;
            _resolvedRasterOperationPattern = null;
            _resolvedRasterOperationTextureBrush = null;
            _resolvedRasterOperationTexturePattern = null;
            _resolvedPatternBrush?.Dispose();
            _resolvedPatternBrush = null;
            _resolvedPatternBrushObject = null;
            _resolvedPatternBrushOrigin = default;
        }

        internal TilePatternBrush ResolveRasterOperationPattern(HatchBrush hatchBrush)
        {
            Point origin = Graphics.RenderingOrigin;
            if (!ReferenceEquals(_resolvedRasterOperationHatchBrush, hatchBrush) ||
                _resolvedRasterOperationOrigin != origin)
            {
                _resolvedRasterOperationPattern = new TilePatternBrush(
                    HatchPatternMasks.Get(hatchBrush.HatchStyle),
                    ToVector(hatchBrush.ForegroundColor),
                    ToVector(hatchBrush.BackgroundColor),
                    new Vector2(origin.X, origin.Y));
                _resolvedRasterOperationHatchBrush = hatchBrush;
                _resolvedRasterOperationOrigin = origin;
            }
            return _resolvedRasterOperationPattern!;
        }

        internal GpuRasterTexturePattern ResolveRasterOperationPattern(
            TextureBrush textureBrush)
        {
            if (!ReferenceEquals(_resolvedRasterOperationTextureBrush, textureBrush))
            {
                _resolvedRasterOperationTexturePattern =
                    Graphics.RetainRasterOperationPattern(textureBrush);
                _resolvedRasterOperationTextureBrush = textureBrush;
            }
            return _resolvedRasterOperationTexturePattern!;
        }

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

        internal void MoveTo(Point point)
        {
            CurrentPoint = point;
            FigureStart = point;
            if (_pathBracketOpen)
            {
                _buildingPath!.StartFigure();
                _pathConnectNext = false;
            }
        }

        internal void MoveTo(Point point, in MetafileRecord record)
        {
            MoveTo(point);
            if (_pathBracketOpen)
            {
                Vector2 devicePoint = Vector2.Transform(
                    new Vector2(point.X, point.Y),
                    ApplyTransform(record));
                _pathMoveDevicePoint = new PointF(devicePoint.X, devicePoint.Y);
            }
        }

        internal void CompletePolyDraw(Point currentPoint, Point figureStart)
        {
            CurrentPoint = currentPoint;
            FigureStart = figureStart;
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
            if (BackgroundMode != mode)
            {
                BackgroundMode = mode;
                InvalidateResolvedBrushes();
            }
        }

        internal void SetRasterOperation(int operation, in MetafileRecord record)
        {
            if (operation != 13)
            {
                throw Unsupported(record, "The initial vector player supports R2_COPYPEN only.");
            }
            RasterOperation = operation;
        }

        internal void SetStretchMode(int mode, in MetafileRecord record)
        {
            DibInterpolationMode = mode switch
            {
                1 or 2 or 3 => InterpolationMode.NearestNeighbor,
                4 => InterpolationMode.HighQualityBilinear,
                _ => throw Invalid(record)
            };
        }

        internal void SetArcDirection(int direction, in MetafileRecord record)
        {
            if (direction is not 1 and not 2)
            {
                throw Invalid(record);
            }
            ArcDirection = direction;
        }

        internal void SetMiterLimit(float limit, in MetafileRecord record)
        {
            if (!float.IsFinite(limit) || limit < 1f)
            {
                throw Invalid(record);
            }
            MiterLimit = limit;
        }

        internal void BeginPath(in MetafileRecord record)
        {
            if (_pathBracketOpen)
            {
                throw Invalid(record);
            }

            _selectedPath?.Dispose();
            _selectedPath = null;
            _buildingPath?.Dispose();
            _buildingPath = new GraphicsPath();
            _pathBracketOpen = true;
            _pathMoveDevicePoint = null;
            _pathConnectNext = false;
        }

        internal void EndPath(in MetafileRecord record)
        {
            if (!_pathBracketOpen || _buildingPath is null)
            {
                throw Invalid(record);
            }

            _selectedPath?.Dispose();
            _selectedPath = _buildingPath;
            _buildingPath = null;
            _pathBracketOpen = false;
            _pathMoveDevicePoint = null;
            _pathConnectNext = false;
        }

        internal void CloseFigure(in MetafileRecord record)
        {
            if (!_pathBracketOpen || _buildingPath is null)
            {
                throw Invalid(record);
            }

            _buildingPath.CloseFigure();
            _pathConnectNext = false;
        }

        internal bool CapturePath(
            in MetafileRecord record,
            GraphicsPath path,
            bool connect,
            bool continueFigure)
        {
            if (!_pathBracketOpen)
            {
                return false;
            }

            Matrix3x2 transform = ApplyTransform(record);
            using (var matrix = new Matrix(transform))
            {
                path.Transform(matrix);
            }

            if (path.PointCount != 0)
            {
                GraphicsPath? adjustedPath = null;
                try
                {
                    PointF? deviceStart = connect
                        ? _pathConnectNext && _buildingPath!.PointCount != 0
                            ? _buildingPath.GetLastPoint()
                            : _pathMoveDevicePoint
                        : null;
                    if (deviceStart is PointF start)
                    {
                        PointF[] points = path.PathPoints;
                        byte[] types = path.PathTypes;
                        points[0] = start;
                        adjustedPath = new GraphicsPath(points, types, path.FillMode);
                    }

                    _buildingPath!.AddPath(
                        adjustedPath ?? path,
                        connect && _pathConnectNext);
                }
                finally
                {
                    adjustedPath?.Dispose();
                }
                _pathMoveDevicePoint = null;
                _pathConnectNext = continueFigure;
            }
            return true;
        }

        internal void EnsurePathCaptureSupported(
            in MetafileRecord record,
            string operation)
        {
            if (_pathBracketOpen)
            {
                throw Unsupported(
                    record,
                    $"{operation} inside an EMF path bracket requires typed outline capture.");
            }
        }

        internal void RenderPath(in MetafileRecord record, bool fill, bool stroke)
        {
            GraphicsPath path = TakeSelectedPath(record);
            try
            {
                path.FillMode = FillMode;
                if (fill)
                {
                    path.CloseAllFigures();
                }

                Graphics.TransformElements = Matrix3x2.Identity;
                if (fill && SelectedBrush is Brush brush)
                {
                    Graphics.FillPath(brush, path);
                }
                if (stroke && _selectedPen is Pen pen)
                {
                    using Pen effectivePen = CreateEffectivePathPen(pen);
                    Graphics.DrawPath(effectivePen, path);
                }
            }
            finally
            {
                path.Dispose();
            }
        }

        internal void FlattenPath(in MetafileRecord record)
        {
            GraphicsPath path = GetSelectedPath(record);
            path.Flatten();
        }

        internal void WidenPath(in MetafileRecord record)
        {
            GraphicsPath path = GetSelectedPath(record);
            if (_selectedPen is not Pen pen || pen.Width <= 1f)
            {
                throw Unsupported(
                    record,
                    "WidenPath requires a selected pen wider than one device unit.");
            }

            using Pen effectivePen = CreateEffectivePathPen(pen);
            path.Widen(effectivePen);
        }

        internal void SelectClipPath(int mode, in MetafileRecord record)
        {
            GraphicsPath path = TakeSelectedPath(record);
            try
            {
                path.FillMode = FillMode;
                path.CloseAllFigures();
                using var region = new Region(path);
                CombineMode combineMode = ReadCombineMode(mode, record);
                CombineApplicationClip(
                    region,
                    combineMode,
                    requiresMaterialization:
                        combineMode is CombineMode.Xor or CombineMode.Exclude);
            }
            finally
            {
                path.Dispose();
            }
        }

        internal void AbortPath()
        {
            _buildingPath?.Dispose();
            _buildingPath = null;
            _selectedPath?.Dispose();
            _selectedPath = null;
            _pathBracketOpen = false;
            _pathMoveDevicePoint = null;
            _pathConnectNext = false;
        }

        private GraphicsPath GetSelectedPath(in MetafileRecord record)
        {
            if (_pathBracketOpen || _selectedPath is null)
            {
                throw Invalid(record);
            }
            return _selectedPath;
        }

        private GraphicsPath TakeSelectedPath(in MetafileRecord record)
        {
            GraphicsPath path = GetSelectedPath(record);
            _selectedPath = null;
            return path;
        }

        private Pen CreateEffectivePathPen(Pen pen)
        {
            var clone = (Pen)pen.Clone();
            clone.MiterLimit = MiterLimit;
            return clone;
        }

        internal void IntersectClip(in MetafileRecord record, Rectangle rectangle)
        {
            using Region region = CreateTransformedRegion(record, rectangle);
            CombineApplicationClip(region, CombineMode.Intersect);
        }

        internal void ExcludeClip(in MetafileRecord record, Rectangle rectangle)
        {
            using Region region = CreateTransformedRegion(record, rectangle);
            CombineApplicationClip(region, CombineMode.Exclude);
        }

        internal void OffsetClip(in MetafileRecord record, Point offset)
        {
            Matrix3x2 transform = ApplyTransform(record);
            if (_applicationClip is null)
            {
                return;
            }

            Vector2 translated = Vector2.TransformNormal(
                new Vector2(offset.X, offset.Y),
                transform);
            if (!float.IsFinite(translated.X) || !float.IsFinite(translated.Y))
            {
                throw Invalid(record);
            }
            _applicationClip.Translate(translated.X, translated.Y);
            ApplyEffectiveClip();
        }

        internal void SelectClipRegion(
            Region? region,
            int mode,
            in MetafileRecord record)
        {
            CombineMode combineMode = ReadCombineMode(mode, record);
            if (region is null)
            {
                if (combineMode != CombineMode.Replace)
                {
                    throw Invalid(record);
                }
                _applicationClip?.Dispose();
                _applicationClip = null;
                _applicationClipRequiresMaterialization = false;
                ApplyEffectiveClip();
                return;
            }

            Region transformed = region.Clone();
            try
            {
                Matrix3x2 transform = ApplyTransform(record);
                using var matrix = new Matrix(transform);
                transformed.Transform(matrix);
                CombineApplicationClip(
                    transformed,
                    combineMode,
                    requiresMaterialization:
                        combineMode is CombineMode.Xor or CombineMode.Exclude);
            }
            finally
            {
                transformed.Dispose();
            }
        }

        internal void SetMetaRegion()
        {
            if (_applicationClip is not null)
            {
                _metaClip.Intersect(_applicationClip);
                _metaClipRequiresMaterialization |=
                    _applicationClipRequiresMaterialization;
                _applicationClip.Dispose();
                _applicationClip = null;
                _applicationClipRequiresMaterialization = false;
            }
            ApplyEffectiveClip();
        }

        private Region CreateTransformedRegion(
            in MetafileRecord record,
            Rectangle rectangle)
        {
            var region = new Region(rectangle);
            try
            {
                Matrix3x2 transform = ApplyTransform(record);
                using var matrix = new Matrix(transform);
                region.Transform(matrix);
                return region;
            }
            catch
            {
                region.Dispose();
                throw;
            }
        }

        private void CombineApplicationClip(
            Region region,
            CombineMode mode,
            bool requiresMaterialization = false)
        {
            if (mode == CombineMode.Replace)
            {
                _applicationClip?.Dispose();
                _applicationClip = region.Clone();
                _applicationClipRequiresMaterialization = requiresMaterialization;
                ApplyEffectiveClip();
                return;
            }

            _applicationClip ??= new Region();
            _applicationClipRequiresMaterialization |= requiresMaterialization;
            switch (mode)
            {
                case CombineMode.Intersect:
                    _applicationClip.Intersect(region);
                    break;
                case CombineMode.Union:
                    _applicationClip.Union(region);
                    break;
                case CombineMode.Xor:
                    _applicationClip.Xor(region);
                    break;
                case CombineMode.Exclude:
                    _applicationClip.Exclude(region);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected EMF region combine mode.");
            }
            ApplyEffectiveClip();
        }

        private void ApplyEffectiveClip()
        {
            Region effective = _metaClip.Clone();
            Region? materialized = null;
            try
            {
                if (_applicationClip is not null)
                {
                    effective.Intersect(_applicationClip);
                }
                if (_metaClipRequiresMaterialization ||
                    _applicationClipRequiresMaterialization)
                {
                    try
                    {
                        using var identity = new Matrix();
                        RectangleF[] scans = effective.GetRegionScans(identity);
                        if (scans.Length == 0)
                        {
                            materialized = new Region();
                            materialized.MakeEmpty();
                        }
                        else
                        {
                            using var path = new GraphicsPath(FillMode.Winding);
                            path.AddRectangles(scans);
                            materialized = new Region(path);
                        }
                    }
                    catch (NotSupportedException)
                    {
                        // Curved or rotated clips retain the exact deferred-vector
                        // path when rectangular scan materialization is unavailable.
                    }
                }
                Graphics.TransformElements = Matrix3x2.Identity;
                Graphics.SetClip(materialized ?? effective, CombineMode.Replace);
            }
            finally
            {
                materialized?.Dispose();
                effective.Dispose();
            }
        }

        private static CombineMode ReadCombineMode(
            int mode,
            in MetafileRecord record) => mode switch
        {
            1 => CombineMode.Intersect,
            2 => CombineMode.Union,
            3 => CombineMode.Xor,
            4 => CombineMode.Exclude,
            5 => CombineMode.Replace,
            _ => throw Invalid(record)
        };

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
                FigureStart,
                WorldTransform,
                FillMode,
                MapMode,
                BackgroundMode,
                RasterOperation,
                DibInterpolationMode,
                ArcDirection,
                MiterLimit,
                TextAlignment,
                TextCharacterExtra,
                TextJustificationExtra,
                TextJustificationBreakCount,
                TextJustificationError,
                BackgroundColor,
                TextColor,
                _selectedPen,
                _selectedBrushObject,
                _selectedFont,
                _selectedFontObject,
                _selectedFontEscapement,
                _selectedPalette,
                _metaClip.Clone(),
                _applicationClip?.Clone(),
                _metaClipRequiresMaterialization,
                _applicationClipRequiresMaterialization,
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
            Region restoredMetaClip = saved.MetaClip.Clone();
            Region? restoredApplicationClip = saved.ApplicationClip?.Clone();
            for (int index = stateIndex; index < _savedStates.Count; index++)
            {
                _savedStates[index].MetaClip.Dispose();
                _savedStates[index].ApplicationClip?.Dispose();
            }
            _savedStates.RemoveRange(stateIndex, _savedStates.Count - stateIndex);
            WindowOrigin = saved.WindowOrigin;
            WindowExtent = saved.WindowExtent;
            ViewportOrigin = saved.ViewportOrigin;
            ViewportExtent = saved.ViewportExtent;
            CurrentPoint = saved.CurrentPoint;
            FigureStart = saved.FigureStart;
            WorldTransform = saved.WorldTransform;
            FillMode = saved.FillMode;
            MapMode = saved.MapMode;
            BackgroundMode = saved.BackgroundMode;
            RasterOperation = saved.RasterOperation;
            DibInterpolationMode = saved.DibInterpolationMode;
            ArcDirection = saved.ArcDirection;
            MiterLimit = saved.MiterLimit;
            TextAlignment = saved.TextAlignment;
            TextCharacterExtra = saved.TextCharacterExtra;
            TextJustificationExtra = saved.TextJustificationExtra;
            TextJustificationBreakCount = saved.TextJustificationBreakCount;
            TextJustificationError = saved.TextJustificationError;
            BackgroundColor = saved.BackgroundColor;
            TextColor = saved.TextColor;
            _selectedPen = saved.SelectedPen;
            _selectedBrushObject = saved.SelectedBrushObject;
            InvalidateResolvedBrushes();
            _selectedFont = saved.SelectedFont;
            _selectedFontObject = saved.SelectedFontObject;
            _selectedFontEscapement = saved.SelectedFontEscapement;
            _selectedPalette = saved.SelectedPalette;
            _metaClip.Dispose();
            _metaClip = restoredMetaClip;
            _applicationClip?.Dispose();
            _applicationClip = restoredApplicationClip;
            _metaClipRequiresMaterialization = saved.MetaClipRequiresMaterialization;
            _applicationClipRequiresMaterialization =
                saved.ApplicationClipRequiresMaterialization;
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
                2 => CreateHatchBrushObject(
                    ReadUInt32(payload, 12),
                    ReadColor(payload, 8),
                    record),
                _ => throw Unsupported(record, "The typed player supports solid, null, or hatched brushes only.")
            };
            AddObject(index, product, record);
        }

        internal void CreateEmfDibPatternBrush(
            ReadOnlySpan<byte> payload,
            in MetafileRecord record,
            bool requireMonochrome)
        {
            const int fixedPayloadSize = 24;
            if (payload.Length < fixedPayloadSize)
            {
                throw Invalid(record);
            }

            uint bitmapInfoOffset = ReadUInt32(payload, 8);
            uint bitmapInfoSize = ReadUInt32(payload, 12);
            uint bitmapBitsOffset = ReadUInt32(payload, 16);
            uint bitmapBitsSize = ReadUInt32(payload, 20);
            ReadOnlySpan<byte> bitmapInfo = ReadEmfBuffer(
                record,
                payload,
                bitmapInfoOffset,
                bitmapInfoSize,
                fixedPayloadSize);
            ReadOnlySpan<byte> bitmapBits = ReadEmfBuffer(
                record,
                payload,
                bitmapBitsOffset,
                bitmapBitsSize,
                fixedPayloadSize);
            EnsureDisjointEmfBuffers(
                record,
                bitmapInfoOffset,
                bitmapInfoSize,
                bitmapBitsOffset,
                bitmapBitsSize);

            DibInfo dib = ReadDibInfo(
                record,
                bitmapInfo,
                ReadUInt32(payload, 4),
                SelectedPalette);
            if (requireMonochrome && dib.BitCount != 1)
            {
                throw Invalid(record);
            }
            Bitmap bitmap = DecodeDibRows(
                record,
                dib,
                bitmapInfo,
                bitmapBits,
                dib.Height);
            AddObject(
                ReadUInt32(payload, 0),
                new GdiPatternBrushObject(bitmap),
                record);
        }

        internal void CreateEmfPalette(ReadOnlySpan<byte> payload, in MetafileRecord record)
        {
            if (payload.Length < 8)
            {
                throw Invalid(record);
            }
            uint index = ReadUInt32(payload, 0);
            LogicalPalette palette = ReadLogicalPalette(
                payload[4..],
                requireVersion: true,
                requireEntries: true,
                validateFlags: false,
                record);
            AddObject(index, palette, record);
        }

        internal void CreateWmfPalette(ReadOnlySpan<byte> payload, in MetafileRecord record) =>
            AddWmfObject(
                ReadLogicalPalette(
                    payload,
                    requireVersion: true,
                    requireEntries: true,
                    validateFlags: true,
                    record),
                record);

        internal void SelectPalette(uint index, in MetafileRecord record)
        {
            if (index == (StockObjectFlag | DefaultPalette))
            {
                _selectedPalette = LogicalPalette.Default;
                return;
            }
            if (index == 0 || (index & StockObjectFlag) != 0 ||
                !_objects.TryGetValue(index, out object? product) ||
                product is not LogicalPalette palette)
            {
                throw Invalid(record);
            }
            _selectedPalette = palette;
        }

        internal void SelectWmfPalette(ushort index, in MetafileRecord record)
        {
            if (!_objects.TryGetValue(index, out object? product) ||
                product is not LogicalPalette palette)
            {
                throw Invalid(record);
            }
            _selectedPalette = palette;
        }

        internal void SetEmfPaletteEntries(
            ReadOnlySpan<byte> payload,
            in MetafileRecord record)
        {
            if (payload.Length < 12)
            {
                throw Invalid(record);
            }
            LogicalPalette palette = GetPalette(ReadUInt32(payload, 0), record);
            uint start = ReadUInt32(payload, 4);
            uint count = ReadUInt32(payload, 8);
            SetPaletteEntries(
                palette,
                start,
                count,
                payload[12..],
                validateFlags: false,
                record);
        }

        internal void SetWmfPaletteEntries(
            ReadOnlySpan<byte> payload,
            in MetafileRecord record)
        {
            LogicalPalette palette = GetMutableSelectedPalette(record);
            ReadPaletteUpdate(payload, palette, animate: false, record);
        }

        internal void AnimateWmfPalette(
            ReadOnlySpan<byte> payload,
            in MetafileRecord record)
        {
            LogicalPalette palette = GetMutableSelectedPalette(record);
            ReadPaletteUpdate(payload, palette, animate: true, record);
        }

        internal void ResizePalette(
            uint index,
            uint count,
            in MetafileRecord record)
        {
            if (count is 0 or > 0x400)
            {
                throw Invalid(record);
            }
            GetPalette(index, record).Resize((int)count);
        }

        internal void ResizeSelectedPalette(ushort count, in MetafileRecord record)
        {
            if (count == 0)
            {
                throw Invalid(record);
            }
            GetMutableSelectedPalette(record).Resize(count);
        }

        internal void RealizePalette()
        {
            // Retained playback resolves logical colors directly, so no device palette
            // realization is required at this typed cross-platform boundary.
        }

        private LogicalPalette GetPalette(uint index, in MetafileRecord record)
        {
            if (index == 0 || (index & StockObjectFlag) != 0 ||
                !_objects.TryGetValue(index, out object? product) ||
                product is not LogicalPalette palette)
            {
                throw Invalid(record);
            }
            return palette;
        }

        private LogicalPalette GetMutableSelectedPalette(in MetafileRecord record)
        {
            if (ReferenceEquals(_selectedPalette, LogicalPalette.Default))
            {
                throw Invalid(record);
            }
            return _selectedPalette;
        }

        private static LogicalPalette ReadLogicalPalette(
            ReadOnlySpan<byte> payload,
            bool requireVersion,
            bool requireEntries,
            bool validateFlags,
            in MetafileRecord record)
        {
            if (payload.Length < 4 || requireVersion && ReadUInt16(payload, 0) != 0x0300)
            {
                throw Invalid(record);
            }
            int count = ReadUInt16(payload, 2);
            if (requireEntries && count == 0 || payload.Length != checked(4 + count * 4))
            {
                throw Invalid(record);
            }
            return new LogicalPalette(ReadPaletteEntries(
                payload[4..],
                count,
                validateFlags,
                record));
        }

        private static void ReadPaletteUpdate(
            ReadOnlySpan<byte> payload,
            LogicalPalette palette,
            bool animate,
            in MetafileRecord record)
        {
            if (payload.Length < 4)
            {
                throw Invalid(record);
            }
            ushort start = ReadUInt16(payload, 0);
            ushort count = ReadUInt16(payload, 2);
            SetPaletteEntries(
                palette,
                start,
                count,
                payload[4..],
                validateFlags: true,
                record,
                animate);
        }

        private static void SetPaletteEntries(
            LogicalPalette palette,
            uint start,
            uint count,
            ReadOnlySpan<byte> payload,
            bool validateFlags,
            in MetafileRecord record,
            bool animate = false)
        {
            if (start > int.MaxValue || count > int.MaxValue ||
                count > (uint)(int.MaxValue / 4) ||
                payload.Length != (int)count * 4 ||
                start + (ulong)count > (uint)palette.Count)
            {
                throw Invalid(record);
            }
            PaletteEntry[] entries = ReadPaletteEntries(
                payload,
                (int)count,
                validateFlags,
                record);
            if (animate)
            {
                palette.Animate((int)start, entries);
            }
            else
            {
                palette.SetEntries((int)start, entries);
            }
        }

        private static PaletteEntry[] ReadPaletteEntries(
            ReadOnlySpan<byte> payload,
            int count,
            bool validateFlags,
            in MetafileRecord record)
        {
            var entries = new PaletteEntry[count];
            for (int index = 0; index < count; index++)
            {
                int offset = index * 4;
                byte flags = validateFlags ? payload[offset + 3] : (byte)0;
                if (validateFlags && flags is not 0 and not 1 and not 2 and not 4)
                {
                    throw Invalid(record);
                }
                entries[index] = new PaletteEntry(
                    Color.FromArgb(payload[offset], payload[offset + 1], payload[offset + 2]),
                    flags);
            }
            return entries;
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
                2 => CreateHatchBrushObject(
                    ReadUInt16(payload, 6),
                    ReadColor(payload, 2),
                    record),
                _ => throw Unsupported(record, "The typed WMF player supports solid, null, or hatched brushes only.")
            };
            AddWmfObject(product, record);
        }

        internal void CreateWmfDibPatternBrush(
            ReadOnlySpan<byte> payload,
            in MetafileRecord record)
        {
            const int fixedPayloadSize = 4;
            if (payload.Length < fixedPayloadSize)
            {
                throw Invalid(record);
            }

            ushort style = ReadUInt16(payload, 0);
            uint usage = style == 3
                ? DibRgbColors
                : ReadUInt16(payload, 2);
            ReadOnlySpan<byte> packedDib = payload[fixedPayloadSize..];
            DibInfo dib = ReadDibInfo(record, packedDib, usage, SelectedPalette);
            ReadOnlySpan<byte> bitmapBits = ReadWmfDibBits(record, dib, packedDib);
            Bitmap bitmap = DecodeDibRows(
                record,
                dib,
                packedDib[..dib.BitmapInfoSize],
                bitmapBits,
                dib.Height);
            AddWmfObject(new GdiPatternBrushObject(bitmap), record);
        }

        internal void CreateWmfBitmap16PatternBrush(
            ReadOnlySpan<byte> payload,
            in MetafileRecord record)
        {
            const int patternBitsOffset = 32;
            if (payload.Length < patternBitsOffset)
            {
                throw Invalid(record);
            }

            WmfBitmap16Info bitmapInfo = ReadBitmap16Info(
                record,
                payload,
                patternBitsOffset);
            Bitmap bitmap = WmfBitmap16DecodeServices.Decode(
                bitmapInfo,
                payload[patternBitsOffset..]);
            AddWmfObject(new GdiPatternBrushObject(bitmap), record);
        }

        private static GdiHatchBrushObject CreateHatchBrushObject(
            uint hatch,
            Color foregroundColor,
            in MetafileRecord record)
        {
            if (hatch > (uint)HatchStyle.DiagonalCross)
            {
                throw Invalid(record);
            }
            return new GdiHatchBrushObject((HatchStyle)hatch, foregroundColor);
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
            EnsurePathCaptureSupported(record, "Glyph-index text");
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
            EnsurePathCaptureSupported(record, "Text output");
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
            if (ReferenceEquals(product, _selectedPen) || ReferenceEquals(product, _selectedBrushObject) ||
                ReferenceEquals(product, _selectedFontObject) ||
                ReferenceEquals(product, _selectedPalette))
            {
                return true;
            }

            foreach (SavedState savedState in _savedStates)
            {
                if (ReferenceEquals(product, savedState.SelectedPen) ||
                    ReferenceEquals(product, savedState.SelectedBrushObject) ||
                    ReferenceEquals(product, savedState.SelectedFontObject) ||
                    ReferenceEquals(product, savedState.SelectedPalette))
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
                    _selectedBrushObject = brush;
                    InvalidateResolvedBrushes();
                    break;
                case GdiHatchBrushObject hatchBrush:
                    _selectedBrushObject = hatchBrush;
                    InvalidateResolvedBrushes();
                    break;
                case GdiPatternBrushObject patternBrush:
                    _selectedBrushObject = patternBrush;
                    InvalidateResolvedBrushes();
                    break;
                case NullBrushMarker:
                    _selectedBrushObject = null;
                    InvalidateResolvedBrushes();
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
            _resolvedHatchBrush?.Dispose();
            _resolvedPatternBrush?.Dispose();
            _rasterOperationCoverageBitmap?.Dispose();
            _buildingPath?.Dispose();
            _selectedPath?.Dispose();
            _metaClip.Dispose();
            _applicationClip?.Dispose();
            foreach (SavedState state in _savedStates)
            {
                state.MetaClip.Dispose();
                state.ApplicationClip?.Dispose();
            }
            _savedStates.Clear();
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
            Point FigureStart,
            Matrix3x2 WorldTransform,
            FillMode FillMode,
            int MapMode,
            int BackgroundMode,
            int RasterOperation,
            InterpolationMode DibInterpolationMode,
            int ArcDirection,
            float MiterLimit,
            int TextAlignment,
            int TextCharacterExtra,
            int TextJustificationExtra,
            int TextJustificationBreakCount,
            int TextJustificationError,
            Color BackgroundColor,
            Color TextColor,
            Pen? SelectedPen,
            object? SelectedBrushObject,
            Font SelectedFont,
            object? SelectedFontObject,
            int SelectedFontEscapement,
            LogicalPalette SelectedPalette,
            Region MetaClip,
            Region? ApplicationClip,
            bool MetaClipRequiresMaterialization,
            bool ApplicationClipRequiresMaterialization,
            GraphicsState GraphicsState);
    }

    private sealed class WmfFontObject(Font font, int escapement) : IDisposable
    {
        internal Font Font { get; } = font;
        internal int Escapement { get; } = escapement;

        public void Dispose() => Font.Dispose();
    }

    private sealed record GdiHatchBrushObject(
        HatchStyle Style,
        Color ForegroundColor);

    private sealed class GdiPatternBrushObject(Bitmap bitmap) : IDisposable
    {
        internal Bitmap Bitmap { get; } = bitmap;

        public void Dispose() => Bitmap.Dispose();
    }

    private sealed class LogicalPalette
    {
        internal static readonly LogicalPalette Default = CreateDefault();
        private PaletteEntry[] _entries;

        internal LogicalPalette(PaletteEntry[] entries) => _entries = entries;

        internal int Count => _entries.Length;

        internal Color GetColor(int index) => _entries[index].Color;

        internal void SetEntries(int start, PaletteEntry[] entries) =>
            entries.CopyTo(_entries, start);

        internal void Animate(int start, PaletteEntry[] entries)
        {
            for (int index = 0; index < entries.Length; index++)
            {
                int destinationIndex = start + index;
                PaletteEntry destination = _entries[destinationIndex];
                if ((destination.Flags & 1) != 0)
                {
                    _entries[destinationIndex] = new PaletteEntry(
                        entries[index].Color,
                        destination.Flags);
                }
            }
        }

        internal void Resize(int count) => Array.Resize(ref _entries, count);

        private static LogicalPalette CreateDefault()
        {
            Color[] colors = new ColorPalette(PaletteType.FixedHalftone256).Entries;
            var entries = new PaletteEntry[colors.Length];
            for (int index = 0; index < entries.Length; index++)
            {
                entries[index] = new PaletteEntry(colors[index], 0);
            }
            return new LogicalPalette(entries);
        }
    }

    private readonly record struct PaletteEntry(Color Color, byte Flags);

    private sealed class NullPenMarker
    {
        internal static readonly NullPenMarker Instance = new();
    }

    private sealed class NullBrushMarker
    {
        internal static readonly NullBrushMarker Instance = new();
    }
}
