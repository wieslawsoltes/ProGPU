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

        ReadOnlySpan<byte> source = metafile.Source;
        using var state = new PlaybackState(graphics);
        foreach (ref readonly MetafileRecord record in metafile.Records)
        {
            ReadOnlySpan<byte> payload = source.Slice(record.DataOffset, record.DataLength);
            if (record.IsEmfPlus)
            {
                PlayEmfPlusRecord(record, payload);
            }
            else
            {
                PlayEmfRecord(state, record, payload);
            }
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
        private Pen? _selectedPen = Pens.Black;
        private Brush? _selectedBrush = Brushes.White;

        internal PlaybackState(Graphics graphics)
        {
            Graphics = graphics;
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

        internal void Save() => _savedStates.Add(new SavedState(
            WindowOrigin,
            WindowExtent,
            ViewportOrigin,
            ViewportExtent,
            CurrentPoint,
            WorldTransform,
            FillMode,
            MapMode,
            _selectedPen,
            _selectedBrush));

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
            _selectedPen = saved.SelectedPen;
            _selectedBrush = saved.SelectedBrush;
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
            Pen? SelectedPen,
            Brush? SelectedBrush);
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
