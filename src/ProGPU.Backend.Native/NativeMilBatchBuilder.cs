using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

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

    public void SetVisualEffect(uint handle, uint effectHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetEffect, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, effectHandle);
    }

    /// <summary>Writes canonical MilCmdVisualSetCacheMode state.</summary>
    public void SetVisualCacheMode(uint handle, uint cacheModeHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetCacheMode, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, cacheModeHandle);
    }

    public void SetVisualClip(uint handle, uint clipGeometryHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetClip, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, clipGeometryHandle);
    }

    public void SetVisualScrollableAreaClip(
        uint handle,
        NativeMilRect? clip)
    {
        ValidateHandle(handle);
        if (clip is { } value &&
            (!double.IsFinite(value.X) || !double.IsFinite(value.Y) ||
             !double.IsFinite(value.Width) || !double.IsFinite(value.Height) ||
             value.Width < 0.0 || value.Height < 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(clip));
        }
        NativeMilRect rect = clip ?? default;
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetScrollableAreaClip, 44);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, rect.X);
        WriteDouble(packet, 16, rect.Y);
        WriteDouble(packet, 24, rect.Width);
        WriteDouble(packet, 32, rect.Height);
        WriteUInt32(packet, 40, clip.HasValue ? 1U : 0U);
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

    public void SetVisualRenderOptions(
        uint handle,
        NativeMilRenderOptions options)
    {
        ValidateHandle(handle);
        const NativeMilRenderOptionFlags supported =
            NativeMilRenderOptionFlags.BitmapScalingMode |
            NativeMilRenderOptionFlags.EdgeMode |
            NativeMilRenderOptionFlags.ClearTypeHint |
            NativeMilRenderOptionFlags.TextRenderingMode |
            NativeMilRenderOptionFlags.TextHintingMode;
        if ((options.Flags & ~supported) != 0 ||
            options.EdgeMode > NativeMilEdgeMode.Aliased ||
            options.BitmapScalingMode >
                NativeMilBitmapScalingMode.NearestNeighbor ||
            options.ClearTypeHint > NativeMilClearTypeHint.Enabled ||
            options.TextRenderingMode > NativeMilTextRenderingMode.ClearType ||
            options.TextHintingMode > NativeMilTextHintingMode.Animated ||
            ((options.Flags & NativeMilRenderOptionFlags.EdgeMode) == 0 &&
                options.EdgeMode != NativeMilEdgeMode.Unspecified) ||
            ((options.Flags & NativeMilRenderOptionFlags.BitmapScalingMode) == 0 &&
                options.BitmapScalingMode !=
                    NativeMilBitmapScalingMode.Unspecified) ||
            ((options.Flags & NativeMilRenderOptionFlags.ClearTypeHint) == 0 &&
                options.ClearTypeHint != NativeMilClearTypeHint.Auto) ||
            ((options.Flags & NativeMilRenderOptionFlags.TextRenderingMode) == 0 &&
                options.TextRenderingMode != NativeMilTextRenderingMode.Auto) ||
            ((options.Flags & NativeMilRenderOptionFlags.TextHintingMode) == 0 &&
                options.TextHintingMode != NativeMilTextHintingMode.Auto))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetRenderOptions, 36);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, (uint)options.Flags);
        WriteUInt32(packet, 12, (uint)options.EdgeMode);
        WriteUInt32(packet, 16, 0);
        WriteUInt32(packet, 20, (uint)options.BitmapScalingMode);
        WriteUInt32(packet, 24, (uint)options.ClearTypeHint);
        WriteUInt32(packet, 28, (uint)options.TextRenderingMode);
        WriteUInt32(packet, 32, (uint)options.TextHintingMode);
    }

    public void SetVisualContent(uint handle, uint contentHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetContent, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, contentHandle);
    }

    public void SetVisualOpacityMask(uint handle, uint opacityMaskHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetAlphaMask, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, opacityMaskHandle);
    }

    public void SetVisualGuidelines(
        uint handle,
        ReadOnlySpan<double> guidelinesX,
        ReadOnlySpan<double> guidelinesY)
    {
        ValidateHandle(handle);
        if (guidelinesX.Length > ushort.MaxValue ||
            guidelinesY.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(guidelinesX));
        }
        int count = checked(guidelinesX.Length + guidelinesY.Length);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.VisualSetGuidelineCollection,
            checked(16 + count * sizeof(float)));
        WriteUInt32(packet, 4, handle);
        WriteUInt16(packet, 8, checked((ushort)guidelinesX.Length));
        WriteUInt16(packet, 12, checked((ushort)guidelinesY.Length));
        int offset = 16;
        foreach (double coordinate in guidelinesX)
        {
            float value = (float)coordinate;
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(guidelinesX));
            }
            WriteSingle(packet, offset, value);
            offset += sizeof(float);
        }
        foreach (double coordinate in guidelinesY)
        {
            float value = (float)coordinate;
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(guidelinesY));
            }
            WriteSingle(packet, offset, value);
            offset += sizeof(float);
        }
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

    public void SetBlurEffect(
        uint handle,
        double radius,
        NativeMilEffectRenderingBias renderingBias =
            NativeMilEffectRenderingBias.Performance,
        NativeMilBlurKernelType kernelType =
            NativeMilBlurKernelType.Gaussian)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(radius) ||
            kernelType > NativeMilBlurKernelType.Box ||
            renderingBias > NativeMilEffectRenderingBias.Quality)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.BlurEffect, 28);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, radius);
        WriteUInt32(packet, 16, 0);
        WriteUInt32(packet, 20, (uint)kernelType);
        WriteUInt32(packet, 24, (uint)renderingBias);
    }

    public void SetDropShadowEffect(
        uint handle,
        double shadowDepth,
        NativeMilColor color,
        double direction,
        double opacity,
        double blurRadius,
        NativeMilEffectRenderingBias renderingBias =
            NativeMilEffectRenderingBias.Performance)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(shadowDepth) ||
            !double.IsFinite(direction) ||
            !double.IsFinite(opacity) ||
            !double.IsFinite(blurRadius) ||
            !float.IsFinite(color.Red) || !float.IsFinite(color.Green) ||
            !float.IsFinite(color.Blue) || !float.IsFinite(color.Alpha) ||
            renderingBias > NativeMilEffectRenderingBias.Quality)
        {
            throw new ArgumentOutOfRangeException(nameof(shadowDepth));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DropShadowEffect, 80);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, shadowDepth);
        WriteSingle(packet, 16, color.Red);
        WriteSingle(packet, 20, color.Green);
        WriteSingle(packet, 24, color.Blue);
        WriteSingle(packet, 28, color.Alpha);
        WriteDouble(packet, 32, direction);
        WriteDouble(packet, 40, opacity);
        WriteDouble(packet, 48, blurRadius);
        WriteUInt32(packet, 76, (uint)renderingBias);
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

    public void SetDoubleResource(uint handle, double value)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DoubleResource, 16);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, value);
    }

    /// <summary>Writes canonical MilCmdBitmapCache resource state.</summary>
    public void SetBitmapCache(uint handle, NativeMilBitmapCache cache)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(cache.RenderAtScale))
        {
            throw new ArgumentOutOfRangeException(nameof(cache));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.BitmapCache, 28);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, cache.RenderAtScale);
        WriteUInt32(packet, 16, cache.RenderAtScaleAnimationHandle);
        WriteUInt32(packet, 20, cache.SnapsToDevicePixels ? 1U : 0U);
        WriteUInt32(packet, 24, cache.EnableClearType ? 1U : 0U);
    }

    public void SetPointResource(uint handle, NativeMilPoint point)
    {
        ValidateHandle(handle);
        ValidatePoint(point);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.PointResource, 24);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, point.X);
        WriteDouble(packet, 16, point.Y);
    }

    public void SetLinearGradientBrush(
        uint handle,
        NativeMilLinearGradientBrush brush,
        ReadOnlySpan<NativeMilGradientStop> stops)
    {
        ValidateHandle(handle);
        ValidateGradientState(
            brush.Opacity,
            brush.Interpolation,
            brush.MappingMode,
            brush.SpreadMethod);
        ValidatePoint(brush.StartPoint);
        ValidatePoint(brush.EndPoint);
        int stopsSize = checked(stops.Length * 24);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.LinearGradientBrush,
            checked(84 + stopsSize));
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, brush.Opacity);
        WritePoint(packet, 16, brush.StartPoint);
        WritePoint(packet, 32, brush.EndPoint);
        WriteUInt32(packet, 48, brush.OpacityAnimationHandle);
        WriteUInt32(packet, 52, brush.TransformHandle);
        WriteUInt32(packet, 56, brush.RelativeTransformHandle);
        WriteUInt32(packet, 60, (uint)brush.Interpolation);
        WriteUInt32(packet, 64, (uint)brush.MappingMode);
        WriteUInt32(packet, 68, (uint)brush.SpreadMethod);
        WriteUInt32(packet, 72, checked((uint)stopsSize));
        WriteUInt32(packet, 76, brush.StartPointAnimationHandle);
        WriteUInt32(packet, 80, brush.EndPointAnimationHandle);
        WriteGradientStops(packet[84..], stops);
    }

    public void SetRadialGradientBrush(
        uint handle,
        NativeMilRadialGradientBrush brush,
        ReadOnlySpan<NativeMilGradientStop> stops)
    {
        ValidateHandle(handle);
        ValidateGradientState(
            brush.Opacity,
            brush.Interpolation,
            brush.MappingMode,
            brush.SpreadMethod);
        ValidatePoint(brush.Center);
        ValidatePoint(brush.GradientOrigin);
        if (!double.IsFinite(brush.RadiusX) || brush.RadiusX < 0.0 ||
            !double.IsFinite(brush.RadiusY) || brush.RadiusY < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(brush));
        }
        int stopsSize = checked(stops.Length * 24);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.RadialGradientBrush,
            checked(108 + stopsSize));
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, brush.Opacity);
        WritePoint(packet, 16, brush.Center);
        WriteDouble(packet, 32, brush.RadiusX);
        WriteDouble(packet, 40, brush.RadiusY);
        WritePoint(packet, 48, brush.GradientOrigin);
        WriteUInt32(packet, 64, brush.OpacityAnimationHandle);
        WriteUInt32(packet, 68, brush.TransformHandle);
        WriteUInt32(packet, 72, brush.RelativeTransformHandle);
        WriteUInt32(packet, 76, (uint)brush.Interpolation);
        WriteUInt32(packet, 80, (uint)brush.MappingMode);
        WriteUInt32(packet, 84, (uint)brush.SpreadMethod);
        WriteUInt32(packet, 88, checked((uint)stopsSize));
        WriteUInt32(packet, 92, brush.CenterAnimationHandle);
        WriteUInt32(packet, 96, brush.RadiusXAnimationHandle);
        WriteUInt32(packet, 100, brush.RadiusYAnimationHandle);
        WriteUInt32(packet, 104, brush.GradientOriginAnimationHandle);
        WriteGradientStops(packet[108..], stops);
    }

    public void SetMatrixResource(uint handle, NativeMilMatrix3x2 matrix)
    {
        ValidateHandle(handle);
        ValidateMatrix(matrix);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.MatrixResource, 56);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, matrix.M11);
        WriteDouble(packet, 16, matrix.M12);
        WriteDouble(packet, 24, matrix.M21);
        WriteDouble(packet, 32, matrix.M22);
        WriteDouble(packet, 40, matrix.OffsetX);
        WriteDouble(packet, 48, matrix.OffsetY);
    }

    public void SetMatrixTransform(
        uint handle,
        NativeMilMatrix3x2 matrix,
        uint matrixAnimationHandle = 0)
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
        WriteUInt32(packet, 56, matrixAnimationHandle);
    }

    public void SetTransformGroup(uint handle, ReadOnlySpan<uint> children)
    {
        ValidateHandle(handle);
        int childrenSize = checked(children.Length * sizeof(uint));
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.TransformGroup,
            checked(12 + childrenSize));
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, (uint)childrenSize);
        for (int index = 0; index < children.Length; ++index)
        {
            ValidateHandle(children[index]);
            WriteUInt32(packet, 12 + index * sizeof(uint), children[index]);
        }
    }

    public void SetTranslateTransform(
        uint handle,
        double x,
        double y,
        uint xAnimationHandle = 0,
        uint yAnimationHandle = 0)
    {
        ValidateHandle(handle);
        ValidateTransformValues(x, y);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.TranslateTransform, 32);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, x);
        WriteDouble(packet, 16, y);
        WriteUInt32(packet, 24, xAnimationHandle);
        WriteUInt32(packet, 28, yAnimationHandle);
    }

    public void SetScaleTransform(
        uint handle,
        double scaleX,
        double scaleY,
        double centerX = 0,
        double centerY = 0,
        uint scaleXAnimationHandle = 0,
        uint scaleYAnimationHandle = 0,
        uint centerXAnimationHandle = 0,
        uint centerYAnimationHandle = 0)
    {
        ValidateHandle(handle);
        ValidateTransformValues(scaleX, scaleY, centerX, centerY);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.ScaleTransform, 56);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, scaleX);
        WriteDouble(packet, 16, scaleY);
        WriteDouble(packet, 24, centerX);
        WriteDouble(packet, 32, centerY);
        WriteUInt32(packet, 40, scaleXAnimationHandle);
        WriteUInt32(packet, 44, scaleYAnimationHandle);
        WriteUInt32(packet, 48, centerXAnimationHandle);
        WriteUInt32(packet, 52, centerYAnimationHandle);
    }

    public void SetSkewTransform(
        uint handle,
        double angleX,
        double angleY,
        double centerX = 0,
        double centerY = 0,
        uint angleXAnimationHandle = 0,
        uint angleYAnimationHandle = 0,
        uint centerXAnimationHandle = 0,
        uint centerYAnimationHandle = 0)
    {
        ValidateHandle(handle);
        ValidateTransformValues(angleX, angleY, centerX, centerY);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.SkewTransform, 56);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, angleX);
        WriteDouble(packet, 16, angleY);
        WriteDouble(packet, 24, centerX);
        WriteDouble(packet, 32, centerY);
        WriteUInt32(packet, 40, angleXAnimationHandle);
        WriteUInt32(packet, 44, angleYAnimationHandle);
        WriteUInt32(packet, 48, centerXAnimationHandle);
        WriteUInt32(packet, 52, centerYAnimationHandle);
    }

    public void SetRotateTransform(
        uint handle,
        double angle,
        double centerX = 0,
        double centerY = 0,
        uint angleAnimationHandle = 0,
        uint centerXAnimationHandle = 0,
        uint centerYAnimationHandle = 0)
    {
        ValidateHandle(handle);
        ValidateTransformValues(angle, centerX, centerY);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.RotateTransform, 44);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, angle);
        WriteDouble(packet, 16, centerX);
        WriteDouble(packet, 24, centerY);
        WriteUInt32(packet, 32, angleAnimationHandle);
        WriteUInt32(packet, 36, centerXAnimationHandle);
        WriteUInt32(packet, 40, centerYAnimationHandle);
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

    public void SetGeometryGroup(
        uint handle,
        NativeMilPathFillRule fillRule,
        ReadOnlySpan<uint> childHandles,
        uint transformHandle = 0)
    {
        ValidateHandle(handle);
        if (fillRule > NativeMilPathFillRule.Nonzero)
        {
            throw new ArgumentOutOfRangeException(nameof(fillRule));
        }
        foreach (uint childHandle in childHandles)
        {
            ValidateHandle(childHandle);
        }
        int childrenSize = checked(childHandles.Length * sizeof(uint));
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.GeometryGroup,
            checked(20 + childrenSize));
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, transformHandle);
        WriteUInt32(packet, 12, (uint)fillRule);
        WriteUInt32(packet, 16, checked((uint)childrenSize));
        for (int index = 0; index < childHandles.Length; index++)
        {
            WriteUInt32(packet, 20 + index * sizeof(uint), childHandles[index]);
        }
    }

    public void SetCombinedGeometry(
        uint handle,
        NativeMilGeometryCombineMode combineMode,
        uint geometry1Handle,
        uint geometry2Handle,
        uint transformHandle = 0)
    {
        ValidateHandle(handle);
        if (combineMode > NativeMilGeometryCombineMode.Exclude)
        {
            throw new ArgumentOutOfRangeException(nameof(combineMode));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.CombinedGeometry,
            24);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, transformHandle);
        WriteUInt32(packet, 12, (uint)combineMode);
        WriteUInt32(packet, 16, geometry1Handle);
        WriteUInt32(packet, 20, geometry2Handle);
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

    public void SetGeometryDrawing(
        uint handle,
        uint brushHandle,
        uint penHandle,
        uint geometryHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.GeometryDrawing, 20);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, brushHandle);
        WriteUInt32(packet, 12, penHandle);
        WriteUInt32(packet, 16, geometryHandle);
    }

    /// <summary>
    /// Writes canonical MilCmdGlyphRunCreate state. The embedded DirectWrite
    /// pointer is deliberately zero; bind SFNT bytes with
    /// <see cref="NativeMilChannel.SetGlyphRunFontSfnt"/> before compilation.
    /// </summary>
    public void SetGlyphRun(
        uint handle,
        NativeMilGlyphRun glyphRun,
        ReadOnlySpan<ushort> glyphIndices,
        ReadOnlySpan<float> advances,
        ReadOnlySpan<Vector2> offsets = default)
    {
        ValidateHandle(handle);
        if (glyphIndices.IsEmpty || glyphIndices.Length > ushort.MaxValue ||
            (!advances.IsEmpty && advances.Length != glyphIndices.Length) ||
            (!offsets.IsEmpty && offsets.Length != glyphIndices.Length) ||
            !float.IsFinite(glyphRun.EmSize) || glyphRun.EmSize <= 0 ||
            glyphRun.MeasuringMethod > NativeMilTextMeasuringMethod.GdiNatural ||
            !double.IsFinite(glyphRun.ManagedBounds.X) ||
            !double.IsFinite(glyphRun.ManagedBounds.Y) ||
            !double.IsFinite(glyphRun.ManagedBounds.Width) ||
            !double.IsFinite(glyphRun.ManagedBounds.Height) ||
            glyphRun.ManagedBounds.Width < 0 ||
            glyphRun.ManagedBounds.Height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(glyphRun));
        }
        float originX = ToFiniteSingle(glyphRun.Origin.X, nameof(glyphRun));
        float originY = ToFiniteSingle(glyphRun.Origin.Y, nameof(glyphRun));
        foreach (float advance in advances)
        {
            if (!float.IsFinite(advance))
            {
                throw new ArgumentOutOfRangeException(nameof(advances));
            }
        }
        foreach (Vector2 offset in offsets)
        {
            if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y))
            {
                throw new ArgumentOutOfRangeException(nameof(offsets));
            }
        }
        int payloadSize = checked(
            glyphIndices.Length * sizeof(ushort) +
            glyphIndices.Length * sizeof(float) +
            offsets.Length * sizeof(float) * 2);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.GlyphRunCreate,
            checked(76 + payloadSize));
        WriteUInt32(packet, 4, handle);
        WriteUInt64(packet, 8, 0);
        ushort flags = 0;
        if (glyphRun.IsSideways)
        {
            flags |= 0x0001;
        }
        if (!offsets.IsEmpty)
        {
            flags |= 0x0010;
        }
        WriteUInt16(packet, 16, flags);
        WriteSingle(packet, 20, originX);
        WriteSingle(packet, 24, originY);
        WriteSingle(packet, 28, glyphRun.EmSize);
        WriteDouble(packet, 32, glyphRun.ManagedBounds.X);
        WriteDouble(packet, 40, glyphRun.ManagedBounds.Y);
        WriteDouble(packet, 48, glyphRun.ManagedBounds.Width);
        WriteDouble(packet, 56, glyphRun.ManagedBounds.Height);
        WriteUInt16(packet, 64, checked((ushort)glyphIndices.Length));
        WriteUInt16(packet, 68, glyphRun.BidiLevel);
        WriteUInt16(packet, 72, (ushort)glyphRun.MeasuringMethod);
        int writeOffset = 76;
        foreach (ushort glyphIndex in glyphIndices)
        {
            WriteUInt16(packet, writeOffset, glyphIndex);
            writeOffset += sizeof(ushort);
        }
        if (advances.IsEmpty)
        {
            for (int index = 0; index < glyphIndices.Length; index++)
            {
                WriteSingle(packet, writeOffset, 0);
                writeOffset += sizeof(float);
            }
        }
        else
        {
            foreach (float advance in advances)
            {
                WriteSingle(packet, writeOffset, advance);
                writeOffset += sizeof(float);
            }
        }
        foreach (Vector2 glyphOffset in offsets)
        {
            WriteSingle(packet, writeOffset, glyphOffset.X);
            WriteSingle(
                packet, writeOffset + sizeof(float), glyphOffset.Y);
            writeOffset += sizeof(float) * 2;
        }
    }

    public void SetGlyphRunDrawing(
        uint handle,
        uint glyphRunHandle,
        uint foregroundBrushHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.GlyphRunDrawing, 16);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, glyphRunHandle);
        WriteUInt32(packet, 12, foregroundBrushHandle);
    }

    public void SetImageDrawing(
        uint handle,
        double x,
        double y,
        double width,
        double height,
        uint imageSourceHandle,
        uint rectAnimationHandle = 0)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || width < 0.0 ||
            !double.IsFinite(height) || height < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.ImageDrawing, 48);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, x);
        WriteDouble(packet, 16, y);
        WriteDouble(packet, 24, width);
        WriteDouble(packet, 32, height);
        WriteUInt32(packet, 40, imageSourceHandle);
        WriteUInt32(packet, 44, rectAnimationHandle);
    }

    /// <summary>Writes canonical MilCmdDrawingImage state.</summary>
    public void SetDrawingImage(uint handle, uint drawingHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DrawingImage, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, drawingHandle);
    }

    public void SetDrawingGroup(
        uint handle,
        NativeMilDrawingGroup group,
        ReadOnlySpan<uint> childHandles)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(group.Opacity) || group.Opacity < 0.0 ||
            group.Opacity > 1.0 ||
            group.EdgeMode > NativeMilEdgeMode.Aliased ||
            group.BitmapScalingMode >
                NativeMilBitmapScalingMode.NearestNeighbor ||
            group.ClearTypeHint > NativeMilClearTypeHint.Enabled)
        {
            throw new ArgumentOutOfRangeException(nameof(group));
        }
        foreach (uint childHandle in childHandles)
        {
            ValidateHandle(childHandle);
        }
        int childrenSize = checked(childHandles.Length * sizeof(uint));
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.DrawingGroup,
            checked(52 + childrenSize));
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, group.Opacity);
        WriteUInt32(packet, 16, checked((uint)childrenSize));
        WriteUInt32(packet, 20, group.ClipGeometryHandle);
        WriteUInt32(packet, 24, group.OpacityAnimationHandle);
        WriteUInt32(packet, 28, group.OpacityMaskHandle);
        WriteUInt32(packet, 32, group.TransformHandle);
        WriteUInt32(packet, 36, group.GuidelineSetHandle);
        WriteUInt32(packet, 40, (uint)group.EdgeMode);
        WriteUInt32(packet, 44, (uint)group.BitmapScalingMode);
        WriteUInt32(packet, 48, (uint)group.ClearTypeHint);
        for (int index = 0; index < childHandles.Length; index++)
        {
            WriteUInt32(packet, 52 + index * sizeof(uint), childHandles[index]);
        }
    }

    /// <summary>Writes canonical MilCmdGuidelineSet state.</summary>
    public void SetGuidelineSet(
        uint handle,
        bool isDynamic,
        ReadOnlySpan<double> guidelinesX,
        ReadOnlySpan<double> guidelinesY)
    {
        ValidateHandle(handle);
        if (guidelinesX.Length > ushort.MaxValue ||
            guidelinesY.Length > ushort.MaxValue ||
            (isDynamic &&
                ((guidelinesX.Length & 1) != 0 ||
                 (guidelinesY.Length & 1) != 0)))
        {
            throw new ArgumentOutOfRangeException(nameof(guidelinesX));
        }
        foreach (double coordinate in guidelinesX)
        {
            if (!double.IsFinite(coordinate))
            {
                throw new ArgumentOutOfRangeException(nameof(guidelinesX));
            }
        }
        foreach (double coordinate in guidelinesY)
        {
            if (!double.IsFinite(coordinate))
            {
                throw new ArgumentOutOfRangeException(nameof(guidelinesY));
            }
        }
        int xBytes = checked(guidelinesX.Length * sizeof(double));
        int yBytes = checked(guidelinesY.Length * sizeof(double));
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.GuidelineSet,
            checked(20 + xBytes + yBytes));
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, checked((uint)xBytes));
        WriteUInt32(packet, 12, checked((uint)yBytes));
        WriteUInt32(packet, 16, isDynamic ? 1U : 0U);
        MemoryMarshal.AsBytes(guidelinesX).CopyTo(packet[20..]);
        MemoryMarshal.AsBytes(guidelinesY).CopyTo(packet[(20 + xBytes)..]);
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

    private static void ValidatePoint(NativeMilPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    private static void ValidateGradientState(
        double opacity,
        NativeMilGradientInterpolation interpolation,
        NativeMilBrushMappingMode mappingMode,
        NativeMilGradientSpreadMethod spreadMethod)
    {
        if (!double.IsFinite(opacity) || opacity < 0.0 || opacity > 1.0 ||
            interpolation > NativeMilGradientInterpolation.SRgb ||
            mappingMode > NativeMilBrushMappingMode.RelativeToBoundingBox ||
            spreadMethod > NativeMilGradientSpreadMethod.Repeat)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }
    }

    private static void WriteGradientStops(
        Span<byte> destination,
        ReadOnlySpan<NativeMilGradientStop> stops)
    {
        for (int index = 0; index < stops.Length; ++index)
        {
            NativeMilGradientStop stop = stops[index];
            ValidateColor(stop.Color);
            if (!double.IsFinite(stop.Offset))
            {
                throw new ArgumentOutOfRangeException(nameof(stops));
            }
            int offset = index * 24;
            WriteDouble(destination, offset, stop.Offset);
            WriteSingle(destination, offset + 8, stop.Color.Red);
            WriteSingle(destination, offset + 12, stop.Color.Green);
            WriteSingle(destination, offset + 16, stop.Color.Blue);
            WriteSingle(destination, offset + 20, stop.Color.Alpha);
        }
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

    private static void ValidateTransformValues(
        double first,
        double second,
        double third = 0,
        double fourth = 0)
    {
        if (!double.IsFinite(first) || !double.IsFinite(second) ||
            !double.IsFinite(third) || !double.IsFinite(fourth))
        {
            throw new ArgumentOutOfRangeException(nameof(first));
        }
    }

    internal static void WriteUInt32(Span<byte> packet, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(packet[offset..], value);

    internal static void WriteUInt16(Span<byte> packet, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(packet[offset..], value);

    internal static void WriteUInt64(Span<byte> packet, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(packet[offset..], value);

    internal static void WriteDouble(Span<byte> packet, int offset, double value) =>
        WriteUInt64(packet, offset, BitConverter.DoubleToUInt64Bits(value));

    internal static void WriteSingle(Span<byte> packet, int offset, float value) =>
        WriteUInt32(packet, offset, BitConverter.SingleToUInt32Bits(value));

    private static float ToFiniteSingle(double value, string parameterName)
    {
        float result = (float)value;
        if (!double.IsFinite(value) || !float.IsFinite(result))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return result;
    }
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

    public void PushOpacity(double opacity, uint opacityAnimationHandle)
    {
        if (!double.IsFinite(opacity) || opacity < 0.0 || opacity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.PushOpacityAnimate, 20);
        NativeMilBatchBuilder.WriteDouble(packet, 4, opacity);
        NativeMilBatchBuilder.WriteUInt32(packet, 12, opacityAnimationHandle);
    }

    public void PushClip(uint geometryHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(geometryHandle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.PushClip, 12);
        NativeMilBatchBuilder.WriteUInt32(packet, 4, geometryHandle);
    }

    public void PushOpacityMask(
        NativeMilRect bounds,
        uint opacityMaskHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(opacityMaskHandle);
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || bounds.Width < 0.0 ||
            !double.IsFinite(bounds.Height) || bounds.Height < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }
        float left = (float)bounds.X;
        float top = (float)bounds.Y;
        float right = (float)(bounds.X + bounds.Width);
        float bottom = (float)(bounds.Y + bounds.Height);
        if (!float.IsFinite(left) || !float.IsFinite(top) ||
            !float.IsFinite(right) || !float.IsFinite(bottom))
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.PushOpacityMask, 28);
        NativeMilBatchBuilder.WriteSingle(packet, 4, left);
        NativeMilBatchBuilder.WriteSingle(packet, 8, top);
        NativeMilBatchBuilder.WriteSingle(packet, 12, right);
        NativeMilBatchBuilder.WriteSingle(packet, 16, bottom);
        NativeMilBatchBuilder.WriteUInt32(packet, 20, opacityMaskHandle);
    }

    public void PushTransform(uint transformHandle)
    {
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.PushTransform, 12);
        NativeMilBatchBuilder.WriteUInt32(packet, 4, transformHandle);
    }

    public void PushGuidelineSet(uint guidelineSetHandle)
    {
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.PushGuidelineSet, 12);
        NativeMilBatchBuilder.WriteUInt32(packet, 4, guidelineSetHandle);
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

    public void DrawDrawing(uint drawingHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(drawingHandle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DrawDrawing, 12);
        NativeMilBatchBuilder.WriteUInt32(packet, 4, drawingHandle);
        NativeMilBatchBuilder.WriteUInt32(packet, 8, 0);
    }

    public void DrawGlyphRun(uint foregroundBrushHandle, uint glyphRunHandle)
    {
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DrawGlyphRun, 12);
        NativeMilBatchBuilder.WriteUInt32(packet, 4, foregroundBrushHandle);
        NativeMilBatchBuilder.WriteUInt32(packet, 8, glyphRunHandle);
    }

    public void DrawImage(
        NativeMilRect destination,
        uint imageSourceHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(imageSourceHandle);
        if (!double.IsFinite(destination.X) ||
            !double.IsFinite(destination.Y) ||
            !double.IsFinite(destination.Width) ||
            !double.IsFinite(destination.Height) ||
            destination.Width < 0.0 || destination.Height < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DrawImage, 44);
        NativeMilBatchBuilder.WriteDouble(packet, 4, destination.X);
        NativeMilBatchBuilder.WriteDouble(packet, 12, destination.Y);
        NativeMilBatchBuilder.WriteDouble(packet, 20, destination.Width);
        NativeMilBatchBuilder.WriteDouble(packet, 28, destination.Height);
        NativeMilBatchBuilder.WriteUInt32(packet, 36, imageSourceHandle);
        NativeMilBatchBuilder.WriteUInt32(packet, 40, 0);
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
    internal const uint DoubleResource = 0x0e;
    internal const uint PointResource = 0x10;
    internal const uint MatrixResource = 0x13;
    internal const uint RenderData = 0x18;
    internal const uint VisualCreate = 0x1a;
    internal const uint VisualSetOffset = 0x1b;
    internal const uint VisualSetTransform = 0x1c;
    internal const uint VisualSetEffect = 0x1d;
    internal const uint VisualSetCacheMode = 0x1e;
    internal const uint VisualSetClip = 0x1f;
    internal const uint VisualSetAlpha = 0x20;
    internal const uint VisualSetRenderOptions = 0x21;
    internal const uint VisualSetContent = 0x22;
    internal const uint VisualSetAlphaMask = 0x23;
    internal const uint VisualInsertChildAt = 0x26;
    internal const uint VisualSetGuidelineCollection = 0x27;
    internal const uint VisualSetScrollableAreaClip = 0x28;
    internal const uint GenericTargetCreate = 0x34;
    internal const uint TargetSetRoot = 0x35;
    internal const uint TargetSetClearColor = 0x36;
    internal const uint GlyphRunCreate = 0x3a;
    internal const uint DrawLine = 0x3e;
    internal const uint DrawRectangle = 0x40;
    internal const uint DrawRoundedRectangle = 0x42;
    internal const uint DrawEllipse = 0x44;
    internal const uint DrawGeometry = 0x46;
    internal const uint DrawImage = 0x47;
    internal const uint DrawGlyphRun = 0x49;
    internal const uint DrawDrawing = 0x4a;
    internal const uint PushClip = 0x4d;
    internal const uint PushOpacityMask = 0x4e;
    internal const uint PushOpacity = 0x4f;
    internal const uint PushOpacityAnimate = 0x50;
    internal const uint PushTransform = 0x51;
    internal const uint PushGuidelineSet = 0x52;
    internal const uint Pop = 0x56;
    internal const uint BlurEffect = 0x6e;
    internal const uint DropShadowEffect = 0x6f;
    internal const uint DrawingImage = 0x71;
    internal const uint TransformGroup = 0x72;
    internal const uint TranslateTransform = 0x73;
    internal const uint ScaleTransform = 0x74;
    internal const uint SkewTransform = 0x75;
    internal const uint RotateTransform = 0x76;
    internal const uint MatrixTransform = 0x77;
    internal const uint LineGeometry = 0x78;
    internal const uint RectangleGeometry = 0x79;
    internal const uint EllipseGeometry = 0x7a;
    internal const uint GeometryGroup = 0x7b;
    internal const uint CombinedGeometry = 0x7c;
    internal const uint PathGeometry = 0x7d;
    internal const uint SolidColorBrush = 0x7e;
    internal const uint LinearGradientBrush = 0x7f;
    internal const uint RadialGradientBrush = 0x80;
    internal const uint DashStyle = 0x85;
    internal const uint Pen = 0x86;
    internal const uint GeometryDrawing = 0x87;
    internal const uint GlyphRunDrawing = 0x88;
    internal const uint ImageDrawing = 0x89;
    internal const uint DrawingGroup = 0x8b;
    internal const uint GuidelineSet = 0x8c;
    internal const uint BitmapCache = 0x8d;
}
