using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;

namespace SkiaSharp;

public partial class SKPicture
{
    public SKData Serialize() => new(PictureArchive.Serialize(Picture, CullRect));

    public void Serialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(PictureArchive.Serialize(Picture, CullRect));
    }

    public void Serialize(SKWStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = PictureArchive.Serialize(Picture, CullRect);
        stream.Write(bytes, bytes.Length);
    }

    public static SKPicture? Deserialize(ReadOnlySpan<byte> data) =>
        PictureArchive.TryDeserialize(data, out var picture, out var cullRect)
            ? new SKPicture(picture, cullRect)
            : null;

    public static SKPicture? Deserialize(IntPtr data, int length)
    {
        if (data == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(data));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
        {
            return null;
        }

        var bytes = GC.AllocateUninitializedArray<byte>(length);
        Marshal.Copy(data, bytes, 0, length);
        return Deserialize(bytes);
    }

    public static SKPicture? Deserialize(SKData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Deserialize(data.AsSpan());
    }

    public static SKPicture? Deserialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Deserialize(ReadManagedStream(stream));
    }

    public static SKPicture? Deserialize(SKStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.HasLength)
        {
            var remaining = checked(stream.Length - stream.Position);
            if (remaining < 0 || remaining > PictureArchive.MaxArchiveBytes)
            {
                return null;
            }

            var bytes = GC.AllocateUninitializedArray<byte>(remaining);
            var scratch = new byte[Math.Min(8192, Math.Max(1, remaining))];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var requested = Math.Min(scratch.Length, bytes.Length - offset);
                var read = stream.Read(scratch, requested);
                if (read <= 0)
                {
                    return null;
                }
                scratch.AsSpan(0, read).CopyTo(bytes.AsSpan(offset));
                offset += read;
            }

            return Deserialize(bytes);
        }

        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = stream.Read(buffer, buffer.Length);
            if (read <= 0)
            {
                break;
            }
            if (output.Length + read > PictureArchive.MaxArchiveBytes)
            {
                return null;
            }
            output.Write(buffer, 0, read);
        }

        return Deserialize(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
    }

    private static byte[] ReadManagedStream(Stream stream)
    {
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining < 0 || remaining > PictureArchive.MaxArchiveBytes)
            {
                return Array.Empty<byte>();
            }
        }

        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            if (output.Length + read > PictureArchive.MaxArchiveBytes)
            {
                return Array.Empty<byte>();
            }
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}

internal static class PictureArchive
{
    private const ulong Magic = 0x314349504B534750UL;
    private const int Version = 1;
    private const int MaxDepth = 64;
    private const int MaxCommands = 1_000_000;
    private const int MaxArrayElements = 16_000_000;
    private const int MaxStringBytes = 16_000_000;

    internal const int MaxArchiveBytes = 256 * 1024 * 1024;

    private enum BrushKind : byte
    {
        Null,
        Solid,
        Linear,
        Radial,
        TwoPointConical,
        Sweep,
        PerlinNoise,
        Hatch,
        CrossHatch,
        ThemeResource,
        BackdropMaterial,
        SweepAngles,
    }

    private enum SegmentKind : byte
    {
        Line,
        Quadratic,
        Cubic,
        Arc,
    }

    public static byte[] Serialize(GpuPicture picture, SKRect cullRect)
    {
        ArgumentNullException.ThrowIfNull(picture);
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            WriteRect(writer, cullRect);
            WritePicture(writer, picture, 0);
        }

        if (output.Length > MaxArchiveBytes)
        {
            throw new NotSupportedException($"Serialized pictures are limited to {MaxArchiveBytes} bytes.");
        }
        return output.ToArray();
    }

    public static bool TryDeserialize(
        ReadOnlySpan<byte> data,
        out GpuPicture picture,
        out SKRect cullRect)
    {
        picture = null!;
        cullRect = SKRect.Empty;
        if (data.IsEmpty || data.Length > MaxArchiveBytes)
        {
            return false;
        }

        try
        {
            using var input = new MemoryStream(data.ToArray(), writable: false);
            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt64() != Magic || reader.ReadInt32() != Version)
            {
                return false;
            }

            cullRect = ReadRect(reader);
            picture = ReadPicture(reader, 0);
            if (input.Position != input.Length)
            {
                picture.Dispose();
                picture = null!;
                cullRect = SKRect.Empty;
                return false;
            }
            return true;
        }
        catch (Exception exception) when (
            exception is EndOfStreamException or
                InvalidDataException or
                IOException or
                OverflowException or
                ArgumentException or
                FormatException or
                IndexOutOfRangeException or
                NotSupportedException)
        {
            picture?.Dispose();
            picture = null!;
            cullRect = SKRect.Empty;
            return false;
        }
    }

    private static void WritePicture(BinaryWriter writer, GpuPicture picture, int depth)
    {
        EnsureDepth(depth);
        WriteVector2Array(writer, picture.PointBuffer);
        WriteDoubleArray(writer, picture.DoubleBuffer);
        WriteLine3DArray(writer, picture.Line3DBuffer);
        WriteFloatArray(writer, picture.FloatBuffer);
        WriteCount(writer, picture.RetainedCommands.Length, MaxCommands, "commands");
        foreach (var command in picture.RetainedCommands)
        {
            WriteCommand(writer, command, depth);
        }
    }

    private static GpuPicture ReadPicture(BinaryReader reader, int depth)
    {
        EnsureDepth(depth);
        var points = ReadVector2Array(reader);
        var doubles = ReadDoubleArray(reader);
        var lines = ReadLine3DArray(reader);
        var floats = ReadFloatArray(reader);
        var count = ReadCount(reader, MaxCommands, "commands");
        var commands = new RenderCommand[count];
        for (var index = 0; index < commands.Length; index++)
        {
            commands[index] = ReadCommand(reader, depth);
        }
        return new GpuPicture(commands, points, doubles, lines, floats);
    }

    private static void WriteCommand(BinaryWriter writer, RenderCommand command, int depth)
    {
        if (command.Texture is not null ||
            command.StaticBuffer is not null ||
            command.SeriesCacheKey is not null ||
            command.DataParam is not null)
        {
            throw new NotSupportedException(
                "Picture serialization does not support GPU textures, static buffers, cache keys, or custom extension payloads.");
        }

        writer.Write((int)command.Type);
        writer.Write(command.HitTestId);
        WriteRect(writer, command.Rect);
        WriteBrush(writer, command.Brush);
        WritePen(writer, command.Pen);
        WritePath(writer, command.Path, depth);
        WriteString(writer, command.Text);
        WriteFont(writer, command.Font);
        writer.Write(command.FontSize);
        WriteVector2(writer, command.Position);
        writer.Write(command.IsBold);
        writer.Write(command.IsItalic);
        WriteVector2(writer, command.FontTransform);
        writer.Write(command.HasFontTransform);
        writer.Write(command.Rotation);
        writer.Write((int)command.TextRenderingMode);
        writer.Write((int)command.TextHintingMode);
        writer.Write(command.UseVectorGlyphRendering);
        WriteRect(writer, command.SrcRect);
        WriteTexturePatches(writer, command.TexturePatches);
        writer.Write((int)command.TextureSamplingMode);
        writer.Write(command.TextureMaxAnisotropy);
        WriteVector2(writer, command.TextureCubicCoefficients);
        writer.Write(command.HasTextureCubicCoefficients);
        writer.Write(command.IsEdgeAliased);
        writer.Write(command.IsPenThicknessLocal);
        writer.Write(command.PathSampleGrid);
        writer.Write(command.PathCoverageGamma);
        WriteVector2(writer, command.Position2);
        WriteVector2(writer, command.Position3);
        WriteVector2(writer, command.Position4);
        writer.Write(command.RadiusX);
        writer.Write(command.RadiusY);
        writer.Write(command.CornerRadius);
        WriteVector2Array(writer, command.PolylinePoints);
        writer.Write(command.IsClosed);
        WriteDoubleArray(writer, command.SplineKnots);
        WriteDoubleArray(writer, command.SplineWeights);
        writer.Write(command.SplineDegree);
        WriteVector3(writer, command.Position3D1);
        WriteVector3(writer, command.Position3D2);
        WriteLine3DList(writer, command.Edges3D);
        WriteMatrix(writer, command.Transform);
        WriteFloatArray(writer, command.GpuPoints);
        writer.Write(command.GpuPointsCount);
        writer.Write(command.UseGpuTransforms);
        WriteMatrix(writer, command.CameraView);
        WriteVector2(writer, command.Scale);
        WriteVector2(writer, command.Translate);
        writer.Write(command.PointBufferOffset);
        writer.Write(command.PointBufferCount);
        writer.Write(command.DoubleBufferOffset);
        writer.Write(command.DoubleBufferCount);
        writer.Write(command.Line3DBufferOffset);
        writer.Write(command.Line3DBufferCount);
        writer.Write(command.WeightBufferOffset);
        writer.Write(command.WeightBufferCount);
        writer.Write(command.FloatBufferOffset);
        writer.Write(command.FloatBufferCount);
        writer.Write(command.Picture is not null);
        if (command.Picture is not null)
        {
            WritePicture(writer, command.Picture, depth + 1);
        }
        WriteUShortArray(writer, command.GlyphIndices);
        WriteVector2Array(writer, command.GlyphPositions);
        WriteVertexMesh(writer, command.VertexMesh);
        writer.Write((int)command.VertexColorBlendMode);
        writer.Write(command.ExtensionId);
        writer.Write(command.IntParam);
        writer.Write(command.FloatParam);
    }

    private static RenderCommand ReadCommand(BinaryReader reader, int depth)
    {
        var command = new RenderCommand
        {
            Type = ReadEnum<RenderCommandType>(reader),
            HitTestId = reader.ReadInt32(),
            Rect = ReadSceneRect(reader),
            Brush = ReadBrush(reader),
            Pen = ReadPen(reader),
            Path = ReadPath(reader, depth),
            Text = ReadString(reader),
            Font = ReadFont(reader),
            FontSize = reader.ReadSingle(),
            Position = ReadVector2(reader),
            IsBold = reader.ReadBoolean(),
            IsItalic = reader.ReadBoolean(),
            FontTransform = ReadVector2(reader),
            HasFontTransform = reader.ReadBoolean(),
            Rotation = reader.ReadSingle(),
            TextRenderingMode = ReadEnum<TextRenderingMode>(reader),
            TextHintingMode = ReadEnum<TextHintingMode>(reader),
            UseVectorGlyphRendering = reader.ReadBoolean(),
            SrcRect = ReadSceneRect(reader),
            TexturePatches = ReadTexturePatches(reader),
            TextureSamplingMode = ReadEnum<TextureSamplingMode>(reader),
            TextureMaxAnisotropy = reader.ReadByte(),
            TextureCubicCoefficients = ReadVector2(reader),
            HasTextureCubicCoefficients = reader.ReadBoolean(),
            IsEdgeAliased = reader.ReadBoolean(),
            IsPenThicknessLocal = reader.ReadBoolean(),
            PathSampleGrid = reader.ReadUInt32(),
            PathCoverageGamma = reader.ReadSingle(),
            Position2 = ReadVector2(reader),
            Position3 = ReadVector2(reader),
            Position4 = ReadVector2(reader),
            RadiusX = reader.ReadSingle(),
            RadiusY = reader.ReadSingle(),
            CornerRadius = reader.ReadSingle(),
            PolylinePoints = ReadNullableVector2Array(reader),
            IsClosed = reader.ReadBoolean(),
            SplineKnots = ReadNullableDoubleArray(reader),
            SplineWeights = ReadNullableDoubleArray(reader),
            SplineDegree = reader.ReadInt32(),
            Position3D1 = ReadVector3(reader),
            Position3D2 = ReadVector3(reader),
            Edges3D = ReadLine3DList(reader),
            Transform = ReadMatrix(reader),
            GpuPoints = ReadNullableFloatArray(reader),
            GpuPointsCount = reader.ReadInt32(),
            UseGpuTransforms = reader.ReadBoolean(),
            CameraView = ReadMatrix(reader),
            Scale = ReadVector2(reader),
            Translate = ReadVector2(reader),
            PointBufferOffset = reader.ReadInt32(),
            PointBufferCount = reader.ReadInt32(),
            DoubleBufferOffset = reader.ReadInt32(),
            DoubleBufferCount = reader.ReadInt32(),
            Line3DBufferOffset = reader.ReadInt32(),
            Line3DBufferCount = reader.ReadInt32(),
            WeightBufferOffset = reader.ReadInt32(),
            WeightBufferCount = reader.ReadInt32(),
            FloatBufferOffset = reader.ReadInt32(),
            FloatBufferCount = reader.ReadInt32(),
        };
        if (reader.ReadBoolean())
        {
            command.Picture = ReadPicture(reader, depth + 1);
        }
        command.GlyphIndices = ReadNullableUShortArray(reader);
        command.GlyphPositions = ReadNullableVector2Array(reader);
        command.VertexMesh = ReadVertexMesh(reader);
        command.VertexColorBlendMode = ReadEnum<VertexColorBlendMode>(reader);
        command.ExtensionId = reader.ReadInt32();
        command.IntParam = reader.ReadInt32();
        command.FloatParam = reader.ReadSingle();
        return command;
    }

    private static void WriteBrush(BinaryWriter writer, Brush? brush)
    {
        var kind = brush switch
        {
            null => BrushKind.Null,
            SolidColorBrush => BrushKind.Solid,
            LinearGradientBrush => BrushKind.Linear,
            RadialGradientBrush => BrushKind.Radial,
            TwoPointConicalGradientBrush => BrushKind.TwoPointConical,
            SweepGradientBrush sweep when sweep.StartAngle == 0f && sweep.EndAngle == 360f => BrushKind.Sweep,
            SweepGradientBrush => BrushKind.SweepAngles,
            PerlinNoiseBrush => BrushKind.PerlinNoise,
            HatchPatternBrush => BrushKind.Hatch,
            CrossHatchBrush => BrushKind.CrossHatch,
            ThemeResourceBrush => BrushKind.ThemeResource,
            BackdropMaterialBrush => BrushKind.BackdropMaterial,
            _ => throw new NotSupportedException($"Brush type '{brush.GetType().FullName}' is not serializable."),
        };
        writer.Write((byte)kind);
        if (brush is null)
        {
            return;
        }
        writer.Write(brush.Opacity);
        switch (brush)
        {
            case SolidColorBrush solid:
                WriteVector4(writer, solid.Color);
                break;
            case LinearGradientBrush linear:
                WriteVector2(writer, linear.StartPoint);
                WriteVector2(writer, linear.EndPoint);
                WriteMatrix(writer, linear.CoordinateTransform);
                writer.Write((int)linear.SpreadMethod);
                writer.Write((int)linear.ColorInterpolationMode);
                WriteGradientStops(writer, linear.Stops);
                break;
            case RadialGradientBrush radial:
                WriteVector2(writer, radial.Center);
                WriteVector2(writer, radial.GradientOrigin);
                writer.Write(radial.RadiusX);
                writer.Write(radial.RadiusY);
                WriteMatrix(writer, radial.CoordinateTransform);
                writer.Write((int)radial.SpreadMethod);
                writer.Write((int)radial.ColorInterpolationMode);
                WriteGradientStops(writer, radial.Stops);
                break;
            case TwoPointConicalGradientBrush conical:
                WriteVector2(writer, conical.StartCenter);
                writer.Write(conical.StartRadius);
                WriteVector2(writer, conical.EndCenter);
                writer.Write(conical.EndRadius);
                WriteMatrix(writer, conical.CoordinateTransform);
                writer.Write((int)conical.SpreadMethod);
                writer.Write((int)conical.ColorInterpolationMode);
                WriteGradientStops(writer, conical.Stops);
                writer.Write(conical.OutsideColor.HasValue);
                if (conical.OutsideColor is { } outsideColor)
                {
                    WriteVector4(writer, outsideColor);
                }
                break;
            case SweepGradientBrush sweep:
                WriteVector2(writer, sweep.Center);
                WriteMatrix(writer, sweep.CoordinateTransform);
                writer.Write((int)sweep.SpreadMethod);
                writer.Write((int)sweep.ColorInterpolationMode);
                if (kind == BrushKind.SweepAngles)
                {
                    writer.Write(sweep.StartAngle);
                    writer.Write(sweep.EndAngle);
                }
                WriteGradientStops(writer, sweep.Stops);
                break;
            case PerlinNoiseBrush noise:
                writer.Write(noise.IsTurbulence);
                WriteVector2(writer, noise.BaseFrequency);
                writer.Write(noise.NumOctaves);
                writer.Write(noise.Seed);
                WriteVector2(writer, noise.TileSize);
                WriteMatrix(writer, noise.CoordinateTransform);
                break;
            case HatchPatternBrush hatch:
                writer.Write(hatch.Angle);
                writer.Write(hatch.Spacing);
                writer.Write(hatch.Thickness);
                WriteVector4(writer, hatch.Color);
                break;
            case CrossHatchBrush crossHatch:
                writer.Write(crossHatch.Angle);
                writer.Write(crossHatch.Spacing);
                writer.Write(crossHatch.Thickness);
                WriteVector4(writer, crossHatch.Color);
                break;
            case ThemeResourceBrush theme:
                WriteString(writer, theme.ResourceKey as string ?? throw new NotSupportedException(
                    $"Picture serialization supports string theme-resource keys; key type '{theme.ResourceKey.GetType().FullName}' is not serializable."));
                break;
            case BackdropMaterialBrush backdrop:
                writer.Write((int)backdrop.Kind);
                writer.Write((int)backdrop.Source);
                WriteVector4(writer, backdrop.TintColor);
                WriteVector4(writer, backdrop.LuminosityColor);
                WriteVector4(writer, backdrop.FallbackColor);
                WriteVector4(writer, backdrop.NoiseColor);
                writer.Write(backdrop.TintOpacity);
                writer.Write(backdrop.LuminosityOpacity);
                writer.Write(backdrop.MaterialOpacity);
                writer.Write(backdrop.NoiseOpacity);
                writer.Write(backdrop.BlurRadius);
                writer.Write(backdrop.Saturation);
                writer.Write(backdrop.UseFallback);
                break;
        }
    }

    private static Brush? ReadBrush(BinaryReader reader)
    {
        var kind = (BrushKind)reader.ReadByte();
        if (kind == BrushKind.Null)
        {
            return null;
        }
        var opacity = reader.ReadSingle();
        Brush brush = kind switch
        {
            BrushKind.Solid => new SolidColorBrush(ReadVector4(reader)),
            BrushKind.Linear => ReadLinearGradient(reader),
            BrushKind.Radial => ReadRadialGradient(reader),
            BrushKind.TwoPointConical => ReadConicalGradient(reader),
            BrushKind.Sweep => ReadSweepGradient(reader),
            BrushKind.SweepAngles => ReadSweepGradient(reader, hasAngles: true),
            BrushKind.PerlinNoise => ReadPerlinNoise(reader),
            BrushKind.Hatch => new HatchPatternBrush(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                ReadVector4(reader)),
            BrushKind.CrossHatch => new CrossHatchBrush(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                ReadVector4(reader)),
            BrushKind.ThemeResource => new ThemeResourceBrush(
                ReadString(reader) ?? throw new InvalidDataException("Theme resource keys cannot be null.")),
            BrushKind.BackdropMaterial => ReadBackdropMaterial(reader),
            _ => throw new InvalidDataException("Unknown picture brush kind."),
        };
        brush.Opacity = opacity;
        return brush;
    }

    private static LinearGradientBrush ReadLinearGradient(BinaryReader reader)
    {
        var start = ReadVector2(reader);
        var end = ReadVector2(reader);
        var transform = ReadMatrix(reader);
        var spread = ReadEnum<GradientSpreadMethod>(reader);
        var colorMode = ReadEnum<GradientColorInterpolationMode>(reader);
        return new LinearGradientBrush(start, end, ReadGradientStops(reader))
        {
            CoordinateTransform = transform,
            SpreadMethod = spread,
            ColorInterpolationMode = colorMode,
        };
    }

    private static RadialGradientBrush ReadRadialGradient(BinaryReader reader)
    {
        var center = ReadVector2(reader);
        var origin = ReadVector2(reader);
        var radiusX = reader.ReadSingle();
        var radiusY = reader.ReadSingle();
        var transform = ReadMatrix(reader);
        var spread = ReadEnum<GradientSpreadMethod>(reader);
        var colorMode = ReadEnum<GradientColorInterpolationMode>(reader);
        return new RadialGradientBrush(center, origin, radiusX, radiusY, ReadGradientStops(reader))
        {
            CoordinateTransform = transform,
            SpreadMethod = spread,
            ColorInterpolationMode = colorMode,
        };
    }

    private static TwoPointConicalGradientBrush ReadConicalGradient(BinaryReader reader)
    {
        var startCenter = ReadVector2(reader);
        var startRadius = reader.ReadSingle();
        var endCenter = ReadVector2(reader);
        var endRadius = reader.ReadSingle();
        var transform = ReadMatrix(reader);
        var spread = ReadEnum<GradientSpreadMethod>(reader);
        var colorMode = ReadEnum<GradientColorInterpolationMode>(reader);
        var brush = new TwoPointConicalGradientBrush(
            startCenter,
            startRadius,
            endCenter,
            endRadius,
            ReadGradientStops(reader))
        {
            CoordinateTransform = transform,
            SpreadMethod = spread,
            ColorInterpolationMode = colorMode,
        };
        if (reader.ReadBoolean())
        {
            brush.OutsideColor = ReadVector4(reader);
        }
        return brush;
    }

    private static SweepGradientBrush ReadSweepGradient(BinaryReader reader, bool hasAngles = false)
    {
        var center = ReadVector2(reader);
        var transform = ReadMatrix(reader);
        var spread = ReadEnum<GradientSpreadMethod>(reader);
        var colorMode = ReadEnum<GradientColorInterpolationMode>(reader);
        var startAngle = hasAngles ? reader.ReadSingle() : 0f;
        var endAngle = hasAngles ? reader.ReadSingle() : 360f;
        return new SweepGradientBrush(center, ReadGradientStops(reader))
        {
            StartAngle = startAngle,
            EndAngle = endAngle,
            CoordinateTransform = transform,
            SpreadMethod = spread,
            ColorInterpolationMode = colorMode,
        };
    }

    private static PerlinNoiseBrush ReadPerlinNoise(BinaryReader reader)
    {
        var brush = new PerlinNoiseBrush(
            reader.ReadBoolean(),
            ReadVector2(reader),
            reader.ReadInt32(),
            reader.ReadSingle(),
            ReadVector2(reader))
        {
            CoordinateTransform = ReadMatrix(reader),
        };
        return brush;
    }

    private static BackdropMaterialBrush ReadBackdropMaterial(BinaryReader reader) =>
        new()
        {
            Kind = ReadEnum<BackdropMaterialKind>(reader),
            Source = ReadEnum<BackdropMaterialSource>(reader),
            TintColor = ReadVector4(reader),
            LuminosityColor = ReadVector4(reader),
            FallbackColor = ReadVector4(reader),
            NoiseColor = ReadVector4(reader),
            TintOpacity = reader.ReadSingle(),
            LuminosityOpacity = reader.ReadSingle(),
            MaterialOpacity = reader.ReadSingle(),
            NoiseOpacity = reader.ReadSingle(),
            BlurRadius = reader.ReadSingle(),
            Saturation = reader.ReadSingle(),
            UseFallback = reader.ReadBoolean(),
        };

    private static void WritePen(BinaryWriter writer, Pen? pen)
    {
        writer.Write(pen is not null);
        if (pen is null)
        {
            return;
        }
        WriteBrush(writer, pen.Brush);
        writer.Write(pen.Thickness);
        writer.Write((int)pen.LineJoin);
        writer.Write(pen.MiterLimit);
        writer.Write((int)pen.StartLineCap);
        writer.Write((int)pen.EndLineCap);
        writer.Write((int)pen.DashCap);
        WriteDoubleArray(writer, pen.DashArray);
        writer.Write(pen.DashOffset);
    }

    private static Pen? ReadPen(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }
        var brush = ReadBrush(reader) ?? throw new InvalidDataException("Pens require a brush.");
        return new Pen(brush)
        {
            Thickness = reader.ReadSingle(),
            LineJoin = ReadEnum<PenLineJoin>(reader),
            MiterLimit = reader.ReadSingle(),
            StartLineCap = ReadEnum<PenLineCap>(reader),
            EndLineCap = ReadEnum<PenLineCap>(reader),
            DashCap = ReadEnum<PenLineCap>(reader),
            DashArray = ReadNullableDoubleArray(reader),
            DashOffset = reader.ReadDouble(),
        };
    }

    private static void WritePath(BinaryWriter writer, PathGeometry? path, int depth)
    {
        writer.Write(path is not null);
        if (path is null)
        {
            return;
        }
        EnsureDepth(depth);
        writer.Write((int)path.FillRule);
        writer.Write(path.IsCombined);
        writer.Write(path.Op);
        if (path.IsCombined)
        {
            WritePath(writer, path.PathA, depth + 1);
            WritePath(writer, path.PathB, depth + 1);
        }
        WriteCount(writer, path.Figures.Count, MaxArrayElements, "path figures");
        foreach (var figure in path.Figures)
        {
            WriteVector2(writer, figure.StartPoint);
            writer.Write(figure.IsClosed);
            writer.Write(figure.IsFilled);
            WriteCount(writer, figure.Segments.Count, MaxArrayElements, "path segments");
            foreach (var segment in figure.Segments)
            {
                var kind = segment switch
                {
                    LineSegment => SegmentKind.Line,
                    QuadraticBezierSegment => SegmentKind.Quadratic,
                    CubicBezierSegment => SegmentKind.Cubic,
                    ArcSegment => SegmentKind.Arc,
                    _ => throw new NotSupportedException(
                        $"Path segment type '{segment.GetType().FullName}' is not serializable."),
                };
                writer.Write((byte)kind);
                writer.Write(segment.IsSmoothJoin);
                writer.Write(segment.IsStroked);
                switch (segment)
                {
                    case LineSegment line:
                        WriteVector2(writer, line.Point);
                        break;
                    case QuadraticBezierSegment quadratic:
                        WriteVector2(writer, quadratic.ControlPoint);
                        WriteVector2(writer, quadratic.Point);
                        break;
                    case CubicBezierSegment cubic:
                        WriteVector2(writer, cubic.ControlPoint1);
                        WriteVector2(writer, cubic.ControlPoint2);
                        WriteVector2(writer, cubic.Point);
                        break;
                    case ArcSegment arc:
                        WriteVector2(writer, arc.Point);
                        WriteVector2(writer, arc.Size);
                        writer.Write(arc.RotationAngle);
                        writer.Write(arc.IsLargeArc);
                        writer.Write((int)arc.SweepDirection);
                        break;
                }
            }
        }
    }

    private static PathGeometry? ReadPath(BinaryReader reader, int depth)
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }
        EnsureDepth(depth);
        var path = new PathGeometry
        {
            FillRule = ReadEnum<FillRule>(reader),
            IsCombined = reader.ReadBoolean(),
            Op = reader.ReadInt32(),
        };
        if (path.IsCombined)
        {
            path.PathA = ReadPath(reader, depth + 1);
            path.PathB = ReadPath(reader, depth + 1);
        }
        var figureCount = ReadCount(reader, MaxArrayElements, "path figures");
        for (var figureIndex = 0; figureIndex < figureCount; figureIndex++)
        {
            var figure = new PathFigure(ReadVector2(reader), reader.ReadBoolean())
            {
                IsFilled = reader.ReadBoolean(),
            };
            var segmentCount = ReadCount(reader, MaxArrayElements, "path segments");
            for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                var kind = (SegmentKind)reader.ReadByte();
                var smooth = reader.ReadBoolean();
                var stroked = reader.ReadBoolean();
                PathSegment segment = kind switch
                {
                    SegmentKind.Line => new LineSegment(ReadVector2(reader), smooth, stroked),
                    SegmentKind.Quadratic => new QuadraticBezierSegment(
                        ReadVector2(reader),
                        ReadVector2(reader),
                        smooth,
                        stroked),
                    SegmentKind.Cubic => new CubicBezierSegment(
                        ReadVector2(reader),
                        ReadVector2(reader),
                        ReadVector2(reader),
                        smooth,
                        stroked),
                    SegmentKind.Arc => new ArcSegment(
                        ReadVector2(reader),
                        ReadVector2(reader),
                        reader.ReadSingle(),
                        reader.ReadBoolean(),
                        ReadEnum<SweepDirection>(reader),
                        smooth,
                        stroked),
                    _ => throw new InvalidDataException("Unknown picture path segment kind."),
                };
                figure.Segments.Add(segment);
            }
            path.Figures.Add(figure);
        }
        return path;
    }

    private static void WriteFont(BinaryWriter writer, TtfFont? font)
    {
        writer.Write(font is not null);
        if (font is not null)
        {
            WriteByteArray(writer, font.FontData.Span);
        }
    }

    private static TtfFont? ReadFont(BinaryReader reader) =>
        reader.ReadBoolean() ? new TtfFont(ReadByteArray(reader)) : null;

    private static void WriteTexturePatches(BinaryWriter writer, TexturePatch[]? patches)
    {
        WriteNullableCount(writer, patches?.Length, "texture patches");
        if (patches is null)
        {
            return;
        }
        foreach (var patch in patches)
        {
            WriteSceneRect(writer, patch.Source);
            WriteSceneRect(writer, patch.Destination);
            WriteVector4(writer, patch.Color);
            writer.Write((byte)patch.Kind);
            WriteMatrix3x2(writer, patch.DestinationTransform);
            writer.Write(patch.HasDestinationTransform);
            writer.Write((int)patch.ColorBlendMode);
        }
    }

    private static TexturePatch[]? ReadTexturePatches(BinaryReader reader)
    {
        var count = ReadNullableCount(reader, "texture patches");
        if (count < 0)
        {
            return null;
        }
        var patches = new TexturePatch[count];
        for (var index = 0; index < patches.Length; index++)
        {
            var source = ReadSceneRect(reader);
            var destination = ReadSceneRect(reader);
            var color = ReadVector4(reader);
            var kind = (TexturePatchKind)reader.ReadByte();
            var transform = ReadMatrix3x2(reader);
            var hasTransform = reader.ReadBoolean();
            var blend = ReadEnum<VertexColorBlendMode>(reader);
            patches[index] = kind switch
            {
                TexturePatchKind.FixedColor => new TexturePatch(destination, color),
                TexturePatchKind.AtlasColor => new TexturePatch(source, destination, transform, color, blend),
                _ when hasTransform => new TexturePatch(source, destination, transform),
                _ => new TexturePatch(source, destination),
            };
        }
        return patches;
    }

    private static void WriteVertexMesh(BinaryWriter writer, VertexMesh2D? mesh)
    {
        writer.Write(mesh is not null);
        if (mesh is null)
        {
            return;
        }
        writer.Write((int)mesh.Topology);
        WriteVector2Array(writer, mesh.Positions.Span);
        WriteVector2Array(writer, mesh.TextureCoordinates.Span);
        WriteVector4Array(writer, mesh.Colors.Span);
        WriteUShortArray(writer, mesh.Indices.Span);
    }

    private static VertexMesh2D? ReadVertexMesh(BinaryReader reader) =>
        !reader.ReadBoolean()
            ? null
            : new VertexMesh2D(
                ReadEnum<VertexMeshTopology>(reader),
                ReadVector2Array(reader),
                ReadVector2Array(reader),
                ReadVector4Array(reader),
                ReadUShortArray(reader));

    private static void WriteGradientStops(BinaryWriter writer, GradientStop[] stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        WriteCount(writer, stops.Length, MaxArrayElements, "gradient stops");
        foreach (var stop in stops)
        {
            WriteVector4(writer, stop.Color);
            writer.Write(stop.Offset);
        }
    }

    private static GradientStop[] ReadGradientStops(BinaryReader reader)
    {
        var count = ReadCount(reader, MaxArrayElements, "gradient stops");
        var stops = new GradientStop[count];
        for (var index = 0; index < stops.Length; index++)
        {
            stops[index] = new GradientStop(ReadVector4(reader), reader.ReadSingle());
        }
        return stops;
    }

    private static void WriteString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteCount(writer, byteCount, MaxStringBytes, "string bytes");
        if (byteCount == 0)
        {
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes);
    }

    private static string? ReadString(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count == -1)
        {
            return null;
        }
        ValidateCount(count, MaxStringBytes, "string bytes");
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new EndOfStreamException();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteByteArray(BinaryWriter writer, ReadOnlySpan<byte> values)
    {
        WriteCount(writer, values.Length, MaxArrayElements, "bytes");
        writer.Write(values);
    }

    private static byte[] ReadByteArray(BinaryReader reader)
    {
        var count = ReadCount(reader, MaxArrayElements, "bytes");
        var values = reader.ReadBytes(count);
        if (values.Length != count)
        {
            throw new EndOfStreamException();
        }
        return values;
    }

    private static void WriteVector2Array(BinaryWriter writer, Vector2[]? values)
    {
        WriteNullableCount(writer, values?.Length, "Vector2 array");
        if (values is not null)
        {
            WriteVector2ArrayValues(writer, values);
        }
    }

    private static void WriteVector2Array(BinaryWriter writer, ReadOnlySpan<Vector2> values)
    {
        WriteCount(writer, values.Length, MaxArrayElements, "Vector2 array");
        WriteVector2ArrayValues(writer, values);
    }

    private static void WriteVector2ArrayValues(BinaryWriter writer, ReadOnlySpan<Vector2> values)
    {
        foreach (var value in values)
        {
            WriteVector2(writer, value);
        }
    }

    private static Vector2[] ReadVector2Array(BinaryReader reader)
    {
        var count = ReadCount(reader, MaxArrayElements, "Vector2 array");
        return ReadVector2ArrayValues(reader, count);
    }

    private static Vector2[]? ReadNullableVector2Array(BinaryReader reader)
    {
        var count = ReadNullableCount(reader, "Vector2 array");
        return count < 0 ? null : ReadVector2ArrayValues(reader, count);
    }

    private static Vector2[] ReadVector2ArrayValues(BinaryReader reader, int count)
    {
        var values = new Vector2[count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = ReadVector2(reader);
        }
        return values;
    }

    private static void WriteVector4Array(BinaryWriter writer, ReadOnlySpan<Vector4> values)
    {
        WriteCount(writer, values.Length, MaxArrayElements, "Vector4 array");
        foreach (var value in values)
        {
            WriteVector4(writer, value);
        }
    }

    private static Vector4[] ReadVector4Array(BinaryReader reader)
    {
        var count = ReadCount(reader, MaxArrayElements, "Vector4 array");
        var values = new Vector4[count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = ReadVector4(reader);
        }
        return values;
    }

    private static void WriteDoubleArray(BinaryWriter writer, double[]? values)
    {
        WriteNullableCount(writer, values?.Length, "double array");
        if (values is not null)
        {
            foreach (var value in values)
            {
                writer.Write(value);
            }
        }
    }

    private static double[] ReadDoubleArray(BinaryReader reader)
    {
        var count = ReadCount(reader, MaxArrayElements, "double array");
        return ReadDoubleArrayValues(reader, count);
    }

    private static double[]? ReadNullableDoubleArray(BinaryReader reader)
    {
        var count = ReadNullableCount(reader, "double array");
        return count < 0 ? null : ReadDoubleArrayValues(reader, count);
    }

    private static double[] ReadDoubleArrayValues(BinaryReader reader, int count)
    {
        var values = new double[count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = reader.ReadDouble();
        }
        return values;
    }

    private static void WriteFloatArray(BinaryWriter writer, float[]? values)
    {
        WriteNullableCount(writer, values?.Length, "float array");
        if (values is not null)
        {
            foreach (var value in values)
            {
                writer.Write(value);
            }
        }
    }

    private static float[] ReadFloatArray(BinaryReader reader)
    {
        var count = ReadCount(reader, MaxArrayElements, "float array");
        return ReadFloatArrayValues(reader, count);
    }

    private static float[]? ReadNullableFloatArray(BinaryReader reader)
    {
        var count = ReadNullableCount(reader, "float array");
        return count < 0 ? null : ReadFloatArrayValues(reader, count);
    }

    private static float[] ReadFloatArrayValues(BinaryReader reader, int count)
    {
        var values = new float[count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = reader.ReadSingle();
        }
        return values;
    }

    private static void WriteUShortArray(BinaryWriter writer, ushort[]? values)
    {
        WriteNullableCount(writer, values?.Length, "ushort array");
        if (values is not null)
        {
            WriteUShortArrayValues(writer, values);
        }
    }

    private static void WriteUShortArray(BinaryWriter writer, ReadOnlySpan<ushort> values)
    {
        WriteCount(writer, values.Length, MaxArrayElements, "ushort array");
        WriteUShortArrayValues(writer, values);
    }

    private static void WriteUShortArrayValues(BinaryWriter writer, ReadOnlySpan<ushort> values)
    {
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static ushort[] ReadUShortArray(BinaryReader reader)
    {
        var count = ReadCount(reader, MaxArrayElements, "ushort array");
        return ReadUShortArrayValues(reader, count);
    }

    private static ushort[]? ReadNullableUShortArray(BinaryReader reader)
    {
        var count = ReadNullableCount(reader, "ushort array");
        return count < 0 ? null : ReadUShortArrayValues(reader, count);
    }

    private static ushort[] ReadUShortArrayValues(BinaryReader reader, int count)
    {
        var values = new ushort[count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = reader.ReadUInt16();
        }
        return values;
    }

    private static void WriteLine3DArray(BinaryWriter writer, Line3D[] values)
    {
        WriteCount(writer, values.Length, MaxArrayElements, "Line3D array");
        foreach (var value in values)
        {
            WriteLine3D(writer, value);
        }
    }

    private static Line3D[] ReadLine3DArray(BinaryReader reader)
    {
        var count = ReadCount(reader, MaxArrayElements, "Line3D array");
        var values = new Line3D[count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = ReadLine3D(reader);
        }
        return values;
    }

    private static void WriteLine3DList(BinaryWriter writer, List<Line3D>? values)
    {
        WriteNullableCount(writer, values?.Count, "Line3D list");
        if (values is not null)
        {
            foreach (var value in values)
            {
                WriteLine3D(writer, value);
            }
        }
    }

    private static List<Line3D>? ReadLine3DList(BinaryReader reader)
    {
        var count = ReadNullableCount(reader, "Line3D list");
        if (count < 0)
        {
            return null;
        }
        var values = new List<Line3D>(count);
        for (var index = 0; index < count; index++)
        {
            values.Add(ReadLine3D(reader));
        }
        return values;
    }

    private static void WriteLine3D(BinaryWriter writer, Line3D value)
    {
        WriteVector3(writer, value.Start);
        WriteVector3(writer, value.End);
    }

    private static Line3D ReadLine3D(BinaryReader reader) =>
        new(ReadVector3(reader), ReadVector3(reader));

    private static void WriteCount(BinaryWriter writer, int count, int maximum, string name)
    {
        ValidateCount(count, maximum, name);
        writer.Write(count);
    }

    private static int ReadCount(BinaryReader reader, int maximum, string name)
    {
        var count = reader.ReadInt32();
        ValidateCount(count, maximum, name);
        return count;
    }

    private static void WriteNullableCount(BinaryWriter writer, int? count, string name)
    {
        if (!count.HasValue)
        {
            writer.Write(-1);
            return;
        }
        WriteCount(writer, count.Value, MaxArrayElements, name);
    }

    private static int ReadNullableCount(BinaryReader reader, string name)
    {
        var count = reader.ReadInt32();
        if (count != -1)
        {
            ValidateCount(count, MaxArrayElements, name);
        }
        return count;
    }

    private static void ValidateCount(int count, int maximum, string name)
    {
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException($"Invalid {name} count '{count}'.");
        }
    }

    private static void EnsureDepth(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("Picture nesting exceeds the supported depth.");
        }
    }

    private static TEnum ReadEnum<TEnum>(BinaryReader reader)
        where TEnum : struct, Enum
    {
        var raw = reader.ReadInt32();
        if (!Enum.IsDefined(typeof(TEnum), raw))
        {
            throw new InvalidDataException($"Invalid {typeof(TEnum).Name} value '{raw}'.");
        }
        return (TEnum)Enum.ToObject(typeof(TEnum), raw);
    }

    private static void WriteRect(BinaryWriter writer, SKRect value)
    {
        writer.Write(value.Left);
        writer.Write(value.Top);
        writer.Write(value.Right);
        writer.Write(value.Bottom);
    }

    private static SKRect ReadRect(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteSceneRect(BinaryWriter writer, Rect value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Width);
        writer.Write(value.Height);
    }

    private static Rect ReadSceneRect(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteRect(BinaryWriter writer, Rect value) => WriteSceneRect(writer, value);

    private static void WriteVector2(BinaryWriter writer, Vector2 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    private static Vector2 ReadVector2(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle());

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteVector4(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }

    private static Vector4 ReadVector4(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteMatrix(BinaryWriter writer, Matrix4x4 value)
    {
        writer.Write(value.M11); writer.Write(value.M12); writer.Write(value.M13); writer.Write(value.M14);
        writer.Write(value.M21); writer.Write(value.M22); writer.Write(value.M23); writer.Write(value.M24);
        writer.Write(value.M31); writer.Write(value.M32); writer.Write(value.M33); writer.Write(value.M34);
        writer.Write(value.M41); writer.Write(value.M42); writer.Write(value.M43); writer.Write(value.M44);
    }

    private static Matrix4x4 ReadMatrix(BinaryReader reader) =>
        new(
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteMatrix3x2(BinaryWriter writer, Matrix3x2 value)
    {
        writer.Write(value.M11); writer.Write(value.M12);
        writer.Write(value.M21); writer.Write(value.M22);
        writer.Write(value.M31); writer.Write(value.M32);
    }

    private static Matrix3x2 ReadMatrix3x2(BinaryReader reader) =>
        new(
            reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle());
}
