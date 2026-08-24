using System.Buffers;
using System.Buffers.Binary;

namespace ProGPU.Backend.Native;

/// <summary>
/// Writes canonical, DWORD-aligned WPF DUCE/MIL channel batches.
/// </summary>
public sealed class NativeMilBatchBuilder
{
    private readonly ArrayBufferWriter<byte> _writer;

    public NativeMilBatchBuilder(int initialCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        _writer = new ArrayBufferWriter<byte>(initialCapacity);
    }

    public int Length => _writer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _writer.WrittenSpan;

    public void Clear() => _writer.Clear();

    public byte[] ToArray() => _writer.WrittenSpan.ToArray();

    public void CreateResource(uint handle, NativeMilResourceType resourceType)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.CreateResource, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, (uint)resourceType);
    }

    public void DeleteResource(uint handle, NativeMilResourceType resourceType)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DeleteResource, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, (uint)resourceType);
    }

    public void CreateVisual(uint handle)
    {
        WriteHandleCommand(NativeMilCommand.VisualCreate, handle);
    }

    public void SetVisualOffset(uint handle, double x, double y)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetOffset, 24);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, x);
        WriteDouble(packet, 16, y);
    }

    public void SetVisualTransform(uint handle, uint transformHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetTransform, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, transformHandle);
    }

    public void SetVisualOpacity(uint handle, double opacity)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(opacity) || opacity < 0.0 || opacity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetAlpha, 16);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, opacity);
    }

    public void SetVisualContent(uint handle, uint contentHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetContent, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, contentHandle);
    }

    public void InsertVisualChild(uint handle, uint childHandle, uint index)
    {
        ValidateHandle(handle);
        ValidateHandle(childHandle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualInsertChildAt, 16);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, childHandle);
        WriteUInt32(packet, 12, index);
    }

    public void CreateGenericTarget(
        uint handle,
        uint pixelWidth,
        uint pixelHeight,
        uint flags = 0,
        ulong platformRenderTarget = 0,
        ulong section = 0)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.GenericTargetCreate, 36);
        WriteUInt32(packet, 4, handle);
        WriteUInt64(packet, 8, platformRenderTarget);
        WriteUInt64(packet, 16, section);
        WriteUInt32(packet, 24, pixelWidth);
        WriteUInt32(packet, 28, pixelHeight);
        WriteUInt32(packet, 32, flags);
    }

    public void SetTargetRoot(uint handle, uint rootHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.TargetSetRoot, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, rootHandle);
    }

    public void SetTargetClearColor(uint handle, NativeMilColor color)
    {
        ValidateHandle(handle);
        ValidateColor(color);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.TargetSetClearColor, 24);
        WriteUInt32(packet, 4, handle);
        WriteSingle(packet, 8, color.Red);
        WriteSingle(packet, 12, color.Green);
        WriteSingle(packet, 16, color.Blue);
        WriteSingle(packet, 20, color.Alpha);
    }

    public void SetSolidColorBrush(
        uint handle,
        NativeMilColor color,
        double opacity = 1.0)
    {
        ValidateHandle(handle);
        ValidateColor(color);
        if (!double.IsFinite(opacity) || opacity < 0.0 || opacity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.SolidColorBrush, 48);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, opacity);
        WriteSingle(packet, 16, color.Red);
        WriteSingle(packet, 20, color.Green);
        WriteSingle(packet, 24, color.Blue);
        WriteSingle(packet, 28, color.Alpha);
    }

    public void SetMatrixTransform(
        uint handle,
        NativeMilMatrix3x2 matrix)
    {
        ValidateHandle(handle);
        ValidateMatrix(matrix);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.MatrixTransform, 60);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, matrix.M11);
        WriteDouble(packet, 16, matrix.M12);
        WriteDouble(packet, 24, matrix.M21);
        WriteDouble(packet, 32, matrix.M22);
        WriteDouble(packet, 40, matrix.OffsetX);
        WriteDouble(packet, 48, matrix.OffsetY);
        WriteUInt32(packet, 56, 0);
    }

    public void SetLineGeometry(
        uint handle,
        double startX,
        double startY,
        double endX,
        double endY,
        uint transformHandle = 0)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(startX) || !double.IsFinite(startY) ||
            !double.IsFinite(endX) || !double.IsFinite(endY))
        {
            throw new ArgumentOutOfRangeException(nameof(startX));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.LineGeometry, 52);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, startX);
        WriteDouble(packet, 16, startY);
        WriteDouble(packet, 24, endX);
        WriteDouble(packet, 32, endY);
        WriteUInt32(packet, 40, transformHandle);
        WriteUInt32(packet, 44, 0);
        WriteUInt32(packet, 48, 0);
    }

    public void SetRectangleGeometry(
        uint handle,
        double x,
        double y,
        double width,
        double height,
        double radiusX = 0,
        double radiusY = 0,
        uint transformHandle = 0)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || width < 0.0 ||
            !double.IsFinite(height) || height < 0.0 ||
            !double.IsFinite(radiusX) || radiusX < 0.0 ||
            !double.IsFinite(radiusY) || radiusY < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.RectangleGeometry, 72);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, radiusX);
        WriteDouble(packet, 16, radiusY);
        WriteDouble(packet, 24, x);
        WriteDouble(packet, 32, y);
        WriteDouble(packet, 40, width);
        WriteDouble(packet, 48, height);
        WriteUInt32(packet, 56, transformHandle);
        WriteUInt32(packet, 60, 0);
        WriteUInt32(packet, 64, 0);
        WriteUInt32(packet, 68, 0);
    }

    public void SetEllipseGeometry(
        uint handle,
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        uint transformHandle = 0)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY) ||
            !double.IsFinite(radiusX) || radiusX < 0.0 ||
            !double.IsFinite(radiusY) || radiusY < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusX));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.EllipseGeometry, 56);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, radiusX);
        WriteDouble(packet, 16, radiusY);
        WriteDouble(packet, 24, centerX);
        WriteDouble(packet, 32, centerY);
        WriteUInt32(packet, 40, transformHandle);
        WriteUInt32(packet, 44, 0);
        WriteUInt32(packet, 48, 0);
        WriteUInt32(packet, 52, 0);
    }

    public void SetPathGeometry(
        uint handle,
        NativeMilPathGeometry geometry,
        uint transformHandle = 0)
    {
        ValidateHandle(handle);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(geometry.Figures);
        double right = geometry.X + geometry.Width;
        double bottom = geometry.Y + geometry.Height;
        if (geometry.FillRule > NativeMilPathFillRule.Nonzero ||
            !double.IsFinite(geometry.X) ||
            !double.IsFinite(geometry.Y) ||
            !double.IsFinite(geometry.Width) || geometry.Width < 0.0 ||
            !double.IsFinite(geometry.Height) || geometry.Height < 0.0 ||
            !double.IsFinite(right) ||
            !double.IsFinite(bottom))
        {
            throw new ArgumentOutOfRangeException(nameof(geometry));
        }

        int figuresSize = 48;
        foreach (NativeMilPathFigure figure in geometry.Figures)
        {
            ArgumentNullException.ThrowIfNull(figure);
            ArgumentNullException.ThrowIfNull(figure.Segments);
            ValidatePoint(figure.StartPoint, nameof(geometry));
            figuresSize = checked(figuresSize + 40);
            foreach (NativeMilPathSegment segment in figure.Segments)
            {
                ValidatePathSegment(segment, nameof(geometry));
                figuresSize = checked(figuresSize + PathSegmentSize(segment));
            }
        }

        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.PathGeometry,
            checked(20 + figuresSize));
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, transformHandle);
        WriteUInt32(packet, 12, (uint)geometry.FillRule);
        WriteUInt32(packet, 16, checked((uint)figuresSize));

        const uint PathHasCurves = 0x01;
        const uint PathBoundsValid = 0x02;
        const uint PathHasGaps = 0x04;
        const uint PathHasHollows = 0x08;
        uint pathFlags = PathBoundsValid;
        foreach (NativeMilPathFigure figure in geometry.Figures)
        {
            if (!figure.IsFilled)
            {
                pathFlags |= PathHasHollows;
            }
            foreach (NativeMilPathSegment segment in figure.Segments)
            {
                if (segment.Kind != NativeMilPathSegmentKind.Line)
                {
                    pathFlags |= PathHasCurves;
                }
                if (!segment.IsStroked)
                {
                    pathFlags |= PathHasGaps;
                }
            }
        }

        int offset = 20;
        WriteUInt32(packet, offset, checked((uint)figuresSize));
        WriteUInt32(packet, offset + 4, pathFlags);
        WriteDouble(packet, offset + 8, geometry.X);
        WriteDouble(packet, offset + 16, geometry.Y);
        WriteDouble(packet, offset + 24, right);
        WriteDouble(packet, offset + 32, bottom);
        WriteUInt32(packet, offset + 40, checked((uint)geometry.Figures.Count));
        WriteUInt32(packet, offset + 44, 0);
        offset += 48;

        uint previousFigureSize = 0;
        foreach (NativeMilPathFigure figure in geometry.Figures)
        {
            int figureOffset = offset;
            int figureSize = 40;
            foreach (NativeMilPathSegment segment in figure.Segments)
            {
                figureSize = checked(figureSize + PathSegmentSize(segment));
            }
            uint figureFlags = 0;
            if (figure.Segments.Any(static segment => !segment.IsStroked))
            {
                figureFlags |= 0x01;
            }
            if (figure.Segments.Any(
                    static segment =>
                        segment.Kind != NativeMilPathSegmentKind.Line))
            {
                figureFlags |= 0x02;
            }
            if (figure.IsClosed)
            {
                figureFlags |= 0x04;
            }
            if (figure.IsFilled)
            {
                figureFlags |= 0x08;
            }
            WriteUInt32(packet, offset, previousFigureSize);
            WriteUInt32(packet, offset + 4, figureFlags);
            WriteUInt32(
                packet,
                offset + 8,
                checked((uint)figure.Segments.Count));
            WriteUInt32(packet, offset + 12, checked((uint)figureSize));
            WritePoint(packet, offset + 16, figure.StartPoint);
            int lastSegmentOffset = 0;
            int segmentOffset = offset + 40;
            uint previousSegmentSize = 0;
            foreach (NativeMilPathSegment segment in figure.Segments)
            {
                lastSegmentOffset = segmentOffset - figureOffset;
                int segmentSize = PathSegmentSize(segment);
                uint segmentFlags = 0;
                if (!segment.IsStroked)
                {
                    segmentFlags |= 0x04;
                }
                if (segment.IsSmoothJoin)
                {
                    segmentFlags |= 0x08;
                }
                if (segment.Kind != NativeMilPathSegmentKind.Line)
                {
                    segmentFlags |= 0x20;
                }
                WriteUInt32(packet, segmentOffset, (uint)segment.Kind);
                WriteUInt32(packet, segmentOffset + 4, segmentFlags);
                WriteUInt32(packet, segmentOffset + 8, previousSegmentSize);
                switch (segment.Kind)
                {
                    case NativeMilPathSegmentKind.Line:
                        WriteUInt32(packet, segmentOffset + 12, 0);
                        WritePoint(packet, segmentOffset + 16, segment.Point1);
                        break;
                    case NativeMilPathSegmentKind.QuadraticBezier:
                        WriteUInt32(packet, segmentOffset + 12, 0);
                        WritePoint(packet, segmentOffset + 16, segment.Point1);
                        WritePoint(packet, segmentOffset + 32, segment.Point2);
                        break;
                    case NativeMilPathSegmentKind.CubicBezier:
                        WriteUInt32(packet, segmentOffset + 12, 0);
                        WritePoint(packet, segmentOffset + 16, segment.Point1);
                        WritePoint(packet, segmentOffset + 32, segment.Point2);
                        WritePoint(packet, segmentOffset + 48, segment.Point3);
                        break;
                    case NativeMilPathSegmentKind.Arc:
                        WriteUInt32(
                            packet,
                            segmentOffset + 12,
                            segment.IsLargeArc ? 1U : 0U);
                        WritePoint(packet, segmentOffset + 16, segment.Point1);
                        WriteDouble(packet, segmentOffset + 32, segment.RadiusX);
                        WriteDouble(packet, segmentOffset + 40, segment.RadiusY);
                        WriteDouble(
                            packet,
                            segmentOffset + 48,
                            segment.RotationAngle);
                        WriteUInt32(
                            packet,
                            segmentOffset + 56,
                            segment.IsClockwise ? 1U : 0U);
                        WriteUInt32(packet, segmentOffset + 60, 0);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(geometry));
                }
                previousSegmentSize = checked((uint)segmentSize);
                segmentOffset += segmentSize;
            }
            WriteUInt32(
                packet,
                offset + 32,
                checked((uint)lastSegmentOffset));
            WriteUInt32(packet, offset + 36, 0);
            offset += figureSize;
            previousFigureSize = checked((uint)figureSize);
        }
    }

    private static int PathSegmentSize(NativeMilPathSegment segment) =>
        segment.Kind switch
        {
            NativeMilPathSegmentKind.Line => 32,
            NativeMilPathSegmentKind.QuadraticBezier => 48,
            NativeMilPathSegmentKind.CubicBezier => 64,
            NativeMilPathSegmentKind.Arc => 64,
            _ => throw new ArgumentOutOfRangeException(nameof(segment))
        };

    private static void ValidatePathSegment(
        NativeMilPathSegment segment,
        string parameterName)
    {
        if (segment.Kind < NativeMilPathSegmentKind.Line ||
            segment.Kind > NativeMilPathSegmentKind.Arc)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        ValidatePoint(segment.Point1, parameterName);
        if (segment.Kind == NativeMilPathSegmentKind.QuadraticBezier ||
            segment.Kind == NativeMilPathSegmentKind.CubicBezier)
        {
            ValidatePoint(segment.Point2, parameterName);
        }
        if (segment.Kind == NativeMilPathSegmentKind.CubicBezier)
        {
            ValidatePoint(segment.Point3, parameterName);
        }
        if (segment.Kind == NativeMilPathSegmentKind.Arc &&
            (!double.IsFinite(segment.RadiusX) || segment.RadiusX < 0.0 ||
             !double.IsFinite(segment.RadiusY) || segment.RadiusY < 0.0 ||
             !double.IsFinite(segment.RotationAngle)))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidatePoint(
        NativeMilPoint point,
        string parameterName)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void WritePoint(
        Span<byte> destination,
        int offset,
        NativeMilPoint point)
    {
        WriteDouble(destination, offset, point.X);
        WriteDouble(destination, offset + 8, point.Y);
    }

    public void SetPen(uint handle, NativeMilPen pen)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(pen.Thickness) || pen.Thickness < 0.0 ||
            !double.IsFinite(pen.MiterLimit) || pen.MiterLimit < 0.0 ||
            pen.StartLineCap > NativeMilPenLineCap.Triangle ||
            pen.EndLineCap > NativeMilPenLineCap.Triangle ||
            pen.DashCap > NativeMilPenLineCap.Triangle ||
            pen.LineJoin > NativeMilPenLineJoin.Round)
        {
            throw new ArgumentOutOfRangeException(nameof(pen));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.Pen, 52);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, pen.Thickness);
        WriteDouble(packet, 16, pen.MiterLimit);
        WriteUInt32(packet, 24, pen.BrushHandle);
        WriteUInt32(packet, 32, (uint)pen.StartLineCap);
        WriteUInt32(packet, 36, (uint)pen.EndLineCap);
        WriteUInt32(packet, 40, (uint)pen.DashCap);
        WriteUInt32(packet, 44, (uint)pen.LineJoin);
        WriteUInt32(packet, 48, pen.DashStyleHandle);
    }

    public void SetDashStyle(
        uint handle,
        double offset,
        ReadOnlySpan<double> intervals)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(offset))
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        foreach (double interval in intervals)
        {
            if (!double.IsFinite(interval) || interval < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(intervals));
            }
        }
        int intervalsSize = checked(intervals.Length * sizeof(double));
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.DashStyle,
            checked(24 + intervalsSize));
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, offset);
        WriteUInt32(packet, 20, (uint)intervalsSize);
        for (int index = 0; index < intervals.Length; ++index)
        {
            WriteDouble(
                packet,
                24 + index * sizeof(double),
                intervals[index]);
        }
    }

    public void SetRenderData(uint handle, NativeMilRenderDataBuilder renderData)
    {
        ValidateHandle(handle);
        ArgumentNullException.ThrowIfNull(renderData);
        ReadOnlySpan<byte> nested = renderData.WrittenSpan;
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.RenderData,
            checked(12 + nested.Length));
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, checked((uint)nested.Length));
        nested.CopyTo(packet[12..]);
    }

    private void WriteHandleCommand(uint command, uint handle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, command, 8);
        WriteUInt32(packet, 4, handle);
    }

    private static void ValidateHandle(uint handle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(handle);
    }

    internal static void ValidateColor(NativeMilColor color)
    {
        if (!float.IsFinite(color.Red) || !float.IsFinite(color.Green) ||
            !float.IsFinite(color.Blue) || !float.IsFinite(color.Alpha))
        {
            throw new ArgumentOutOfRangeException(nameof(color));
        }
    }

    internal static void ValidateMatrix(NativeMilMatrix3x2 matrix)
    {
        if (!double.IsFinite(matrix.M11) || !double.IsFinite(matrix.M12) ||
            !double.IsFinite(matrix.M21) || !double.IsFinite(matrix.M22) ||
            !double.IsFinite(matrix.OffsetX) ||
            !double.IsFinite(matrix.OffsetY))
        {
            throw new ArgumentOutOfRangeException(nameof(matrix));
        }
    }

    internal static void WriteUInt32(Span<byte> packet, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(packet[offset..], value);

    internal static void WriteUInt64(Span<byte> packet, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(packet[offset..], value);

    internal static void WriteDouble(Span<byte> packet, int offset, double value) =>
        WriteUInt64(packet, offset, BitConverter.DoubleToUInt64Bits(value));

    internal static void WriteSingle(Span<byte> packet, int offset, float value) =>
        WriteUInt32(packet, offset, BitConverter.SingleToUInt32Bits(value));
}

/// <summary>
/// Writes the nested instruction stream carried by a MIL render-data resource.
/// </summary>
public sealed class NativeMilRenderDataBuilder
{
    private readonly ArrayBufferWriter<byte> _writer;

    public NativeMilRenderDataBuilder(int initialCapacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        _writer = new ArrayBufferWriter<byte>(initialCapacity);
    }

    public int Length => _writer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _writer.WrittenSpan;

    public void Clear() => _writer.Clear();

    public void PushOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || opacity < 0.0 || opacity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.PushOpacity, 12);
        NativeMilBatchBuilder.WriteDouble(packet, 4, opacity);
    }

    public void PushTransform(uint transformHandle)
    {
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.PushTransform, 12);
        NativeMilBatchBuilder.WriteUInt32(packet, 4, transformHandle);
    }

    public void Pop()
    {
        _ = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.Pop, 4);
    }

    public void DrawLine(
        double x0,
        double y0,
        double x1,
        double y1,
        uint penHandle)
    {
        if (!double.IsFinite(x0) || !double.IsFinite(y0) ||
            !double.IsFinite(x1) || !double.IsFinite(y1))
        {
            throw new ArgumentOutOfRangeException(nameof(x0));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DrawLine, 44);
        NativeMilBatchBuilder.WriteDouble(packet, 4, x0);
        NativeMilBatchBuilder.WriteDouble(packet, 12, y0);
        NativeMilBatchBuilder.WriteDouble(packet, 20, x1);
        NativeMilBatchBuilder.WriteDouble(packet, 28, y1);
        NativeMilBatchBuilder.WriteUInt32(packet, 36, penHandle);
    }

    public void DrawGeometry(
        uint brushHandle,
        uint penHandle,
        uint geometryHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(geometryHandle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DrawGeometry, 20);
        NativeMilBatchBuilder.WriteUInt32(packet, 4, brushHandle);
        NativeMilBatchBuilder.WriteUInt32(packet, 8, penHandle);
        NativeMilBatchBuilder.WriteUInt32(packet, 12, geometryHandle);
        NativeMilBatchBuilder.WriteUInt32(packet, 16, 0);
    }

    public void DrawRectangle(
        double x,
        double y,
        double width,
        double height,
        uint brushHandle,
        uint penHandle = 0)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            width < 0.0 || height < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DrawRectangle, 44);
        NativeMilBatchBuilder.WriteDouble(packet, 4, x);
        NativeMilBatchBuilder.WriteDouble(packet, 12, y);
        NativeMilBatchBuilder.WriteDouble(packet, 20, width);
        NativeMilBatchBuilder.WriteDouble(packet, 28, height);
        NativeMilBatchBuilder.WriteUInt32(packet, 36, brushHandle);
        NativeMilBatchBuilder.WriteUInt32(packet, 40, penHandle);
    }

    public void DrawEllipse(
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        uint brushHandle,
        uint penHandle = 0)
    {
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY) ||
            !double.IsFinite(radiusX) || !double.IsFinite(radiusY) ||
            radiusX < 0.0 || radiusY < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusX));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DrawEllipse, 44);
        NativeMilBatchBuilder.WriteDouble(packet, 4, centerX);
        NativeMilBatchBuilder.WriteDouble(packet, 12, centerY);
        NativeMilBatchBuilder.WriteDouble(packet, 20, radiusX);
        NativeMilBatchBuilder.WriteDouble(packet, 28, radiusY);
        NativeMilBatchBuilder.WriteUInt32(packet, 36, brushHandle);
        NativeMilBatchBuilder.WriteUInt32(packet, 40, penHandle);
    }

    public void DrawRoundedRectangle(
        double x,
        double y,
        double width,
        double height,
        double radiusX,
        double radiusY,
        uint brushHandle,
        uint penHandle = 0)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(radiusX) || !double.IsFinite(radiusY) ||
            width < 0.0 || height < 0.0 || radiusX < 0.0 || radiusY < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DrawRoundedRectangle, 60);
        NativeMilBatchBuilder.WriteDouble(packet, 4, x);
        NativeMilBatchBuilder.WriteDouble(packet, 12, y);
        NativeMilBatchBuilder.WriteDouble(packet, 20, width);
        NativeMilBatchBuilder.WriteDouble(packet, 28, height);
        NativeMilBatchBuilder.WriteDouble(packet, 36, radiusX);
        NativeMilBatchBuilder.WriteDouble(packet, 44, radiusY);
        NativeMilBatchBuilder.WriteUInt32(packet, 52, brushHandle);
        NativeMilBatchBuilder.WriteUInt32(packet, 56, penHandle);
    }
}

internal static class NativeMilBatchEncoding
{
    internal static Span<byte> Allocate(
        ArrayBufferWriter<byte> writer,
        uint command,
        int packetSize)
    {
        int itemSize = checked((packetSize + 4 + 3) & ~3);
        Span<byte> item = writer.GetSpan(itemSize)[..itemSize];
        item.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(item, (uint)itemSize);
        BinaryPrimitives.WriteUInt32LittleEndian(item[4..], command);
        writer.Advance(itemSize);
        return item.Slice(4, packetSize);
    }
}

internal static class NativeMilCommand
{
    internal const uint CreateResource = 0x07;
    internal const uint DeleteResource = 0x08;
    internal const uint RenderData = 0x18;
    internal const uint VisualCreate = 0x1a;
    internal const uint VisualSetOffset = 0x1b;
    internal const uint VisualSetTransform = 0x1c;
    internal const uint VisualSetAlpha = 0x20;
    internal const uint VisualSetContent = 0x22;
    internal const uint VisualInsertChildAt = 0x26;
    internal const uint GenericTargetCreate = 0x34;
    internal const uint TargetSetRoot = 0x35;
    internal const uint TargetSetClearColor = 0x36;
    internal const uint DrawLine = 0x3e;
    internal const uint DrawRectangle = 0x40;
    internal const uint DrawRoundedRectangle = 0x42;
    internal const uint DrawEllipse = 0x44;
    internal const uint DrawGeometry = 0x46;
    internal const uint PushOpacity = 0x4f;
    internal const uint PushTransform = 0x51;
    internal const uint Pop = 0x56;
    internal const uint MatrixTransform = 0x77;
    internal const uint LineGeometry = 0x78;
    internal const uint RectangleGeometry = 0x79;
    internal const uint EllipseGeometry = 0x7a;
    internal const uint PathGeometry = 0x7d;
    internal const uint SolidColorBrush = 0x7e;
    internal const uint DashStyle = 0x85;
    internal const uint Pen = 0x86;
}
