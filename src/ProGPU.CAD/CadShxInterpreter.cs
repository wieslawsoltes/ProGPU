using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.CAD;

public enum CadShxOrientation : byte
{
    Horizontal = 0,
    Vertical = 1,
}

public sealed class CadShxInterpretOptions
{
    public const int DefaultMaxCommands = 100_000;
    public const int DefaultMaxSegments = 100_000;
    public const int DefaultMaxSubshapeDepth = 32;
    public const double DefaultMaxCoordinateMagnitude = 1_000_000_000.0;
    public const double DefaultMaxScaleMagnitude = 1_000_000_000.0;

    public int MaxCommands { get; init; } = DefaultMaxCommands;
    public int MaxSegments { get; init; } = DefaultMaxSegments;
    public int MaxSubshapeDepth { get; init; } = DefaultMaxSubshapeDepth;
    public double MaxCoordinateMagnitude { get; init; } = DefaultMaxCoordinateMagnitude;
    public double MaxScaleMagnitude { get; init; } = DefaultMaxScaleMagnitude;
}

public sealed class CadShxGeometry
{
    public PathGeometry Path { get; }
    public Vector2 EndPoint { get; }
    public int CommandCount { get; }
    public int SegmentCount { get; }

    internal CadShxGeometry(
        PathGeometry path,
        Vector2 endPoint,
        int commandCount,
        int segmentCount)
    {
        Path = path;
        EndPoint = endPoint;
        CommandCount = commandCount;
        SegmentCount = segmentCount;
    }
}

/// <summary>
/// Interprets standard AutoCAD-86 SHX programs into retained analytic paths.
/// </summary>
/// <remarks>
/// Interpretation is O(C + A) time and O(S + D) storage for executed command
/// bytes C, emitted analytic segments A, retained segments S, and active
/// subshape depth D. All work is bounded by <see cref="CadShxInterpretOptions"/>.
/// </remarks>
public static class CadShxInterpreter
{
    public static CadShxGeometry Interpret(
        CadShxFont font,
        ushort shapeNumber,
        CadShxOrientation orientation = CadShxOrientation.Horizontal,
        CadShxInterpretOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(font);
        options ??= new CadShxInterpretOptions();
        ValidateOptions(options);
        if (!Enum.IsDefined(orientation))
        {
            throw new ArgumentOutOfRangeException(nameof(orientation));
        }
        if (orientation == CadShxOrientation.Vertical &&
            !font.SupportsVerticalOrientation)
        {
            throw new NotSupportedException(
                "Vertical SHX interpretation requires a standard text font with vertical mode enabled.");
        }
        if (!font.TryGetShape(shapeNumber, out CadShxShape? shape))
        {
            throw new KeyNotFoundException(
                $"SHX shape {shapeNumber} is not present in the font.");
        }
        if (shapeNumber == 0 && font.IsTextFont)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shapeNumber),
                "Standard SHX shape zero is font metadata and has no drawable program.");
        }

        var executor = new Executor(font, orientation, options);
        executor.ExecuteShape(shape!, depth: 0);
        return executor.CreateResult();
    }

    private static void ValidateOptions(CadShxInterpretOptions options)
    {
        if (options.MaxCommands <= 0 || options.MaxSegments <= 0 ||
            options.MaxSubshapeDepth <= 0 ||
            !double.IsFinite(options.MaxCoordinateMagnitude) ||
            options.MaxCoordinateMagnitude <= 0.0 ||
            !double.IsFinite(options.MaxScaleMagnitude) ||
            options.MaxScaleMagnitude <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "SHX interpreter limits must be finite positive bounded values.");
        }
    }

    private sealed class Executor
    {
        private const double EighthTurn = Math.PI / 4.0;
        private const double FullTurn = Math.PI * 2.0;
        private readonly CadShxFont _font;
        private readonly CadShxOrientation _orientation;
        private readonly CadShxInterpretOptions _options;
        private readonly PathGeometry _path = new();
        private readonly PointD[] _positionStack = new PointD[4];
        private readonly HashSet<ushort> _activeShapes = new();
        private PointD _position;
        private PathFigure? _figure;
        private double _scale = 1.0;
        private int _positionStackCount;
        private int _commandCount;
        private int _segmentCount;
        private bool _draw = true;

        public Executor(
            CadShxFont font,
            CadShxOrientation orientation,
            CadShxInterpretOptions options)
        {
            _font = font;
            _orientation = orientation;
            _options = options;
        }

        public CadShxGeometry CreateResult() =>
            new(
                _path,
                ToVector(_position),
                _commandCount,
                _segmentCount);

        public void ExecuteShape(CadShxShape shape, int depth)
        {
            if (depth >= _options.MaxSubshapeDepth)
            {
                throw Invalid(shape.Number, "subshape recursion exceeds the configured depth limit");
            }
            if (!_activeShapes.Add(shape.Number))
            {
                throw Invalid(shape.Number, "subshape recursion contains a cycle");
            }

            int entryStackCount = _positionStackCount;
            try
            {
                ReadOnlySpan<byte> program = shape.Program.Span;
                int offset = 0;
                bool ended = false;
                while (offset < program.Length)
                {
                    byte command = program[offset++];
                    CountCommand(shape.Number);
                    if (command == 0)
                    {
                        if (offset != program.Length)
                        {
                            throw Invalid(shape.Number, "contains bytes after its end command");
                        }
                        ended = true;
                        break;
                    }

                    ExecuteCommand(shape.Number, command, program, ref offset, depth, execute: true);
                }

                if (!ended)
                {
                    throw Invalid(shape.Number, "has no reachable end command");
                }
                if (_positionStackCount != entryStackCount)
                {
                    throw Invalid(shape.Number, "does not balance its position-stack operations");
                }
            }
            finally
            {
                _activeShapes.Remove(shape.Number);
            }
        }

        private void ExecuteCommand(
            ushort shapeNumber,
            byte command,
            ReadOnlySpan<byte> program,
            ref int offset,
            int depth,
            bool execute)
        {
            if ((command & 0xF0) != 0)
            {
                if (execute)
                {
                    int length = command >> 4;
                    GetDirection(command & 0x0F, out double x, out double y);
                    AddDisplacement(shapeNumber, x * length, y * length);
                }
                return;
            }

            switch (command)
            {
                case 1:
                    if (execute)
                    {
                        _draw = true;
                    }
                    return;
                case 2:
                    if (execute)
                    {
                        _draw = false;
                        _figure = null;
                    }
                    return;
                case 3:
                case 4:
                    {
                        byte factor = ReadByte(shapeNumber, program, ref offset, "scale factor");
                        if (factor == 0)
                        {
                            throw Invalid(shapeNumber, "contains a zero scale factor");
                        }
                        if (execute)
                        {
                            _scale = command == 3 ? _scale / factor : _scale * factor;
                            if (!double.IsFinite(_scale) || _scale <= 0.0 ||
                                _scale > _options.MaxScaleMagnitude)
                            {
                                throw Invalid(shapeNumber, "exceeds the configured scale limit");
                            }
                        }
                        return;
                    }
                case 5:
                    if (execute)
                    {
                        if (_positionStackCount == _positionStack.Length)
                        {
                            throw Invalid(shapeNumber, "overflows the four-entry position stack");
                        }
                        _positionStack[_positionStackCount++] = _position;
                    }
                    return;
                case 6:
                    if (execute)
                    {
                        if (_positionStackCount == 0)
                        {
                            throw Invalid(shapeNumber, "underflows the position stack");
                        }
                        _position = _positionStack[--_positionStackCount];
                        _figure = null;
                    }
                    return;
                case 7:
                    {
                        byte subshapeNumber = ReadByte(shapeNumber, program, ref offset, "subshape number");
                        if (subshapeNumber == 0)
                        {
                            throw Invalid(shapeNumber, "references reserved subshape zero");
                        }
                        if (execute)
                        {
                            if (!_font.TryGetShape(subshapeNumber, out CadShxShape? subshape))
                            {
                                throw Invalid(shapeNumber, $"references missing subshape {subshapeNumber}");
                            }
                            ExecuteShape(subshape!, checked(depth + 1));
                        }
                        return;
                    }
                case 8:
                    {
                        sbyte x = ReadSignedByte(shapeNumber, program, ref offset, "X displacement");
                        sbyte y = ReadSignedByte(shapeNumber, program, ref offset, "Y displacement");
                        if (execute)
                        {
                            AddDisplacement(shapeNumber, x, y);
                        }
                        return;
                    }
                case 9:
                    ExecuteDisplacementSequence(shapeNumber, program, ref offset, execute);
                    return;
                case 10:
                    ExecuteOctantArc(shapeNumber, program, ref offset, execute);
                    return;
                case 11:
                    ExecuteFractionalArc(shapeNumber, program, ref offset, execute);
                    return;
                case 12:
                    ExecuteBulgeArc(shapeNumber, program, ref offset, execute);
                    return;
                case 13:
                    ExecutePolyArc(shapeNumber, program, ref offset, execute);
                    return;
                case 14:
                    if (!execute)
                    {
                        return;
                    }
                    if (!_font.SupportsVerticalOrientation)
                    {
                        throw Invalid(
                            shapeNumber,
                            "uses vertical command 14 without dual-orientation font mode");
                    }
                    byte conditional = ReadByte(
                        shapeNumber,
                        program,
                        ref offset,
                        "vertical conditional command");
                    CountCommand(shapeNumber);
                    if (conditional == 0)
                    {
                        throw Invalid(shapeNumber, "conditionally wraps an end command");
                    }
                    ExecuteCommand(
                        shapeNumber,
                        conditional,
                        program,
                        ref offset,
                        depth,
                        execute: _orientation == CadShxOrientation.Vertical);
                    return;
                default:
                    throw Invalid(shapeNumber, $"contains unknown special command {command}");
            }
        }

        private void ExecuteDisplacementSequence(
            ushort shapeNumber,
            ReadOnlySpan<byte> program,
            ref int offset,
            bool execute)
        {
            while (true)
            {
                sbyte x = ReadSignedByte(shapeNumber, program, ref offset, "X displacement");
                sbyte y = ReadSignedByte(shapeNumber, program, ref offset, "Y displacement");
                if (x == 0 && y == 0)
                {
                    return;
                }
                if (execute)
                {
                    AddDisplacement(shapeNumber, x, y);
                }
            }
        }

        private void ExecuteOctantArc(
            ushort shapeNumber,
            ReadOnlySpan<byte> program,
            ref int offset,
            bool execute)
        {
            byte radius = ReadByte(shapeNumber, program, ref offset, "octant-arc radius");
            sbyte packed = ReadSignedByte(shapeNumber, program, ref offset, "octant-arc descriptor");
            DecodeSignedDescriptor(shapeNumber, packed, out int sign, out int startOctant, out int count);
            if (radius == 0)
            {
                throw Invalid(shapeNumber, "contains a zero octant-arc radius");
            }
            if (execute)
            {
                double start = startOctant * EighthTurn;
                double sweep = sign * (count == 0 ? FullTurn : count * EighthTurn);
                AddCenterArc(shapeNumber, radius * _scale, start, sweep);
            }
        }

        private void ExecuteFractionalArc(
            ushort shapeNumber,
            ReadOnlySpan<byte> program,
            ref int offset,
            bool execute)
        {
            byte startOffset = ReadByte(shapeNumber, program, ref offset, "fractional-arc start offset");
            byte endOffset = ReadByte(shapeNumber, program, ref offset, "fractional-arc end offset");
            byte highRadius = ReadByte(shapeNumber, program, ref offset, "fractional-arc high radius");
            byte lowRadius = ReadByte(shapeNumber, program, ref offset, "fractional-arc low radius");
            sbyte packed = ReadSignedByte(shapeNumber, program, ref offset, "fractional-arc descriptor");
            DecodeSignedDescriptor(shapeNumber, packed, out int sign, out int startOctant, out int endOctant);
            int radius = (highRadius << 8) | lowRadius;
            if (radius == 0)
            {
                throw Invalid(shapeNumber, "contains a zero fractional-arc radius");
            }
            if (!execute)
            {
                return;
            }

            double start = (startOctant + startOffset / 256.0) * EighthTurn;
            double end = (endOctant + endOffset / 256.0) * EighthTurn;
            double sweep;
            if (sign > 0)
            {
                sweep = end - start;
                if (sweep <= 0.0)
                {
                    sweep += FullTurn;
                }
            }
            else
            {
                sweep = end - start;
                if (sweep >= 0.0)
                {
                    sweep -= FullTurn;
                }
            }
            AddCenterArc(shapeNumber, radius * _scale, start, sweep);
        }

        private void ExecuteBulgeArc(
            ushort shapeNumber,
            ReadOnlySpan<byte> program,
            ref int offset,
            bool execute)
        {
            sbyte x = ReadBulgeByte(shapeNumber, program, ref offset, "bulge-arc X displacement");
            sbyte y = ReadBulgeByte(shapeNumber, program, ref offset, "bulge-arc Y displacement");
            sbyte bulge = ReadBulgeByte(shapeNumber, program, ref offset, "bulge-arc factor");
            if (x == 0 && y == 0)
            {
                throw Invalid(shapeNumber, "contains a zero bulge-arc displacement");
            }
            if (execute)
            {
                AddBulgeArc(shapeNumber, x, y, bulge);
            }
        }

        private void ExecutePolyArc(
            ushort shapeNumber,
            ReadOnlySpan<byte> program,
            ref int offset,
            bool execute)
        {
            while (true)
            {
                sbyte x = ReadBulgeByte(shapeNumber, program, ref offset, "polyarc X displacement");
                sbyte y = ReadBulgeByte(shapeNumber, program, ref offset, "polyarc Y displacement");
                if (x == 0 && y == 0)
                {
                    return;
                }
                sbyte bulge = ReadBulgeByte(shapeNumber, program, ref offset, "polyarc factor");
                if (execute)
                {
                    AddBulgeArc(shapeNumber, x, y, bulge);
                }
            }
        }

        private void AddDisplacement(ushort shapeNumber, double x, double y)
        {
            PointD end = CheckedPoint(
                shapeNumber,
                _position.X + x * _scale,
                _position.Y + y * _scale);
            if (_draw)
            {
                EnsureFigure();
                CountSegment(shapeNumber);
                _figure!.Segments.Add(new LineSegment(ToVector(end)));
            }
            else
            {
                _figure = null;
            }
            _position = end;
        }

        private void AddBulgeArc(ushort shapeNumber, double x, double y, int packedBulge)
        {
            PointD end = CheckedPoint(
                shapeNumber,
                _position.X + x * _scale,
                _position.Y + y * _scale);
            if (!_draw)
            {
                _position = end;
                _figure = null;
                return;
            }
            if (packedBulge == 0)
            {
                EnsureFigure();
                CountSegment(shapeNumber);
                _figure!.Segments.Add(new LineSegment(ToVector(end)));
                _position = end;
                return;
            }

            double deltaX = end.X - _position.X;
            double deltaY = end.Y - _position.Y;
            double chord = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            double bulge = packedBulge / 127.0;
            double radius = chord * (1.0 + bulge * bulge) / (4.0 * Math.Abs(bulge));
            CheckedMagnitude(shapeNumber, radius, "bulge-arc radius");
            EnsureFigure();
            CountSegment(shapeNumber);
            _figure!.Segments.Add(new ArcSegment(
                ToVector(end),
                new Vector2(ToFloat(radius), ToFloat(radius)),
                rotationAngle: 0.0f,
                isLargeArc: false,
                sweepDirection: packedBulge > 0
                    ? SweepDirection.Counterclockwise
                    : SweepDirection.Clockwise));
            _position = end;
        }

        private void AddCenterArc(
            ushort shapeNumber,
            double radius,
            double startAngle,
            double sweep)
        {
            CheckedMagnitude(shapeNumber, radius, "arc radius");
            PointD center = CheckedPoint(
                shapeNumber,
                _position.X - radius * Math.Cos(startAngle),
                _position.Y - radius * Math.Sin(startAngle));
            PointD end = CheckedPoint(
                shapeNumber,
                center.X + radius * Math.Cos(startAngle + sweep),
                center.Y + radius * Math.Sin(startAngle + sweep));
            if (!_draw)
            {
                _position = end;
                _figure = null;
                return;
            }

            EnsureFigure();
            if (Math.Abs(sweep) >= FullTurn - 1e-12)
            {
                double middleAngle = startAngle + Math.CopySign(Math.PI, sweep);
                PointD middle = CheckedPoint(
                    shapeNumber,
                    center.X + radius * Math.Cos(middleAngle),
                    center.Y + radius * Math.Sin(middleAngle));
                AddArcSegment(shapeNumber, middle, radius, Math.CopySign(Math.PI, sweep));
                AddArcSegment(shapeNumber, end, radius, Math.CopySign(Math.PI, sweep));
            }
            else
            {
                AddArcSegment(shapeNumber, end, radius, sweep);
            }
            _position = end;
        }

        private void AddArcSegment(
            ushort shapeNumber,
            PointD end,
            double radius,
            double sweep)
        {
            CountSegment(shapeNumber);
            _figure!.Segments.Add(new ArcSegment(
                ToVector(end),
                new Vector2(ToFloat(radius), ToFloat(radius)),
                rotationAngle: 0.0f,
                isLargeArc: Math.Abs(sweep) > Math.PI,
                sweepDirection: sweep > 0.0
                    ? SweepDirection.Counterclockwise
                    : SweepDirection.Clockwise));
        }

        private void EnsureFigure()
        {
            if (_figure is not null)
            {
                return;
            }
            _figure = new PathFigure(ToVector(_position))
            {
                IsClosed = false,
                IsFilled = false,
            };
            _path.Figures.Add(_figure);
        }

        private PointD CheckedPoint(ushort shapeNumber, double x, double y)
        {
            CheckedMagnitude(shapeNumber, x, "X coordinate");
            CheckedMagnitude(shapeNumber, y, "Y coordinate");
            return new PointD(x, y);
        }

        private void CheckedMagnitude(ushort shapeNumber, double value, string field)
        {
            if (!double.IsFinite(value) || Math.Abs(value) > _options.MaxCoordinateMagnitude)
            {
                throw Invalid(shapeNumber, $"{field} exceeds the configured coordinate limit");
            }
        }

        private void CountCommand(ushort shapeNumber)
        {
            if (++_commandCount > _options.MaxCommands)
            {
                throw Invalid(shapeNumber, "exceeds the configured command limit");
            }
        }

        private void CountSegment(ushort shapeNumber)
        {
            if (++_segmentCount > _options.MaxSegments)
            {
                throw Invalid(shapeNumber, "exceeds the configured segment limit");
            }
        }

        private static void DecodeSignedDescriptor(
            ushort shapeNumber,
            sbyte packed,
            out int sign,
            out int high,
            out int low)
        {
            if (packed == sbyte.MinValue)
            {
                throw Invalid(shapeNumber, "contains an invalid signed arc descriptor");
            }
            sign = packed < 0 ? -1 : 1;
            int value = Math.Abs(packed);
            high = value >> 4;
            low = value & 0x0F;
            if (high > 7 || low > 7)
            {
                throw Invalid(shapeNumber, "contains an out-of-range arc octant");
            }
        }

        private static void GetDirection(int direction, out double x, out double y)
        {
            (x, y) = direction switch
            {
                0 => (1.0, 0.0),
                1 => (1.0, 0.5),
                2 => (1.0, 1.0),
                3 => (0.5, 1.0),
                4 => (0.0, 1.0),
                5 => (-0.5, 1.0),
                6 => (-1.0, 1.0),
                7 => (-1.0, 0.5),
                8 => (-1.0, 0.0),
                9 => (-1.0, -0.5),
                10 => (-1.0, -1.0),
                11 => (-0.5, -1.0),
                12 => (0.0, -1.0),
                13 => (0.5, -1.0),
                14 => (1.0, -1.0),
                _ => (1.0, -0.5),
            };
        }

        private static byte ReadByte(
            ushort shapeNumber,
            ReadOnlySpan<byte> program,
            ref int offset,
            string field)
        {
            if ((uint)offset >= (uint)program.Length)
            {
                throw Invalid(shapeNumber, $"is truncated while reading {field}");
            }
            return program[offset++];
        }

        private static sbyte ReadSignedByte(
            ushort shapeNumber,
            ReadOnlySpan<byte> program,
            ref int offset,
            string field) =>
            unchecked((sbyte)ReadByte(shapeNumber, program, ref offset, field));

        private static sbyte ReadBulgeByte(
            ushort shapeNumber,
            ReadOnlySpan<byte> program,
            ref int offset,
            string field)
        {
            sbyte value = ReadSignedByte(shapeNumber, program, ref offset, field);
            if (value == sbyte.MinValue)
            {
                throw Invalid(shapeNumber, $"contains out-of-range {field}");
            }
            return value;
        }

        private static Vector2 ToVector(PointD point) =>
            new(ToFloat(point.X), ToFloat(point.Y));

        private static float ToFloat(double value)
        {
            float converted = (float)value;
            if (!float.IsFinite(converted))
            {
                throw new InvalidDataException("SHX geometry cannot be represented as retained float coordinates.");
            }
            return converted;
        }

        private static InvalidDataException Invalid(ushort shapeNumber, string message) =>
            new($"SHX shape {shapeNumber} {message}.");
    }

    private readonly record struct PointD(double X, double Y);
}
