using System.Numerics;

namespace ProGPU.Backend.Native;

/// <summary>Original MIL shader execution intent, not a renderer fallback.</summary>
public enum NativeMilShaderRenderMode : uint
{
    Auto = 0,
    SoftwareOnly = 1,
    HardwareOnly = 2
}

/// <summary>Original WPF sampler values; not the scene texture-sampling enum.</summary>
public enum NativeMilShaderSamplingMode : uint
{
    Auto = 0,
    NearestNeighbor = 1,
    Bilinear = 2
}

/// <summary>
/// Original local-space source padding, before float inflation and native capture
/// validation. A valid packet does not admit fractional or cropped GPU captures.
/// </summary>
public readonly record struct NativeMilShaderPadding(double Top, double Bottom, double Left, double Right);

public sealed partial class NativeMilBatchBuilder
{
    /// <summary>
    /// Retains original shader-model bytecode in one canonical packet. Native
    /// compilation remains authoritative for instruction and register admission.
    /// </summary>
    public void SetPixelShader(
        uint handle,
        ReadOnlySpan<byte> bytecode,
        NativeMilShaderRenderMode mode = NativeMilShaderRenderMode.Auto,
        bool compileSoftwareShader = false)
    {
        ValidateHandle(handle);
        if (bytecode.Length is < 8 or > 65536 || (bytecode.Length & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(bytecode));
        if (mode is not (NativeMilShaderRenderMode.Auto or NativeMilShaderRenderMode.HardwareOnly))
            throw new NotSupportedException("Native MIL shader effects require GPU execution intent.");

        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.PixelShader, checked(20 + bytecode.Length));
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, (uint)mode);
        WriteUInt32(packet, 12, (uint)bytecode.Length);
        WriteUInt32(packet, 16, compileSoftwareShader ? 1U : 0U);
        bytecode.CopyTo(packet[20..]);
    }

    /// <summary>Records the untransformed, opacity-one original implicit input.</summary>
    public void SetImplicitInputBrush(uint handle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.ImplicitInputBrush, 28);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, 1.0);
    }

    /// <summary>
    /// Records original zero-padding effect state with ordered Int16 float
    /// registers and one source brush. Register arrays are packed without
    /// interior alignment; only the complete packet receives DWORD padding.
    /// All caller data is validated before any bytes are appended.
    /// </summary>
    public void SetShaderEffect(
        uint handle,
        uint pixelShaderHandle,
        ReadOnlySpan<short> floatRegisters,
        ReadOnlySpan<Vector4> floatValues,
        uint samplerRegister,
        NativeMilShaderSamplingMode samplingMode,
        uint brushHandle,
        int ddxUvDdyUvRegisterIndex = -1)
        => SetShaderEffect(handle, pixelShaderHandle, floatRegisters, floatValues,
            samplerRegister, samplingMode, brushHandle, default(NativeMilShaderPadding), ddxUvDdyUvRegisterIndex);

    /// <summary>
    /// Records the four original padding doubles without rounding or normalization.
    /// Native scene/capture validation remains authoritative for actual admission.
    /// </summary>
    public void SetShaderEffect(
        uint handle,
        uint pixelShaderHandle,
        ReadOnlySpan<short> floatRegisters,
        ReadOnlySpan<Vector4> floatValues,
        uint samplerRegister,
        NativeMilShaderSamplingMode samplingMode,
        uint brushHandle,
        NativeMilShaderPadding padding,
        int ddxUvDdyUvRegisterIndex = -1)
    {
        ValidateHandle(handle);
        ValidateHandle(pixelShaderHandle);
        ValidateHandle(brushHandle);
        if (!ValidPadding(padding.Top) || !ValidPadding(padding.Bottom) ||
            !ValidPadding(padding.Left) || !ValidPadding(padding.Right))
            throw new ArgumentOutOfRangeException(nameof(padding));
        if (floatRegisters.Length > 32 || floatRegisters.Length != floatValues.Length)
            throw new ArgumentOutOfRangeException(nameof(floatRegisters));
        if (samplerRegister >= 16)
            throw new ArgumentOutOfRangeException(nameof(samplerRegister));
        if (samplingMode is not (NativeMilShaderSamplingMode.Auto or
            NativeMilShaderSamplingMode.NearestNeighbor or NativeMilShaderSamplingMode.Bilinear))
            throw new ArgumentOutOfRangeException(nameof(samplingMode));
        if (ddxUvDdyUvRegisterIndex is < -1 or >= 32)
            throw new ArgumentOutOfRangeException(nameof(ddxUvDdyUvRegisterIndex));
        for (int index = 0; index < floatRegisters.Length; ++index)
        {
            short register = floatRegisters[index];
            Vector4 value = floatValues[index];
            if (register is < 0 or >= 32 || (index != 0 && register <= floatRegisters[index - 1]))
                throw new ArgumentOutOfRangeException(nameof(floatRegisters));
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
                !float.IsFinite(value.Z) || !float.IsFinite(value.W))
                throw new ArgumentOutOfRangeException(nameof(floatValues));
        }

        int registerBytes = floatRegisters.Length * sizeof(short);
        int valueBytes = floatValues.Length * 4 * sizeof(float);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.ShaderEffect, 80 + registerBytes + valueBytes + 12);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, padding.Top);
        WriteDouble(packet, 16, padding.Bottom);
        WriteDouble(packet, 24, padding.Left);
        WriteDouble(packet, 32, padding.Right);
        WriteUInt32(packet, 40, pixelShaderHandle);
        WriteUInt32(packet, 44, unchecked((uint)ddxUvDdyUvRegisterIndex));
        WriteUInt32(packet, 48, (uint)registerBytes);
        WriteUInt32(packet, 52, (uint)valueBytes);
        WriteUInt32(packet, 72, 8);
        WriteUInt32(packet, 76, 4);
        for (int index = 0; index < floatRegisters.Length; ++index)
        {
            WriteUInt16(packet, 80 + index * sizeof(short), (ushort)floatRegisters[index]);
            int offset = 80 + registerBytes + index * 4 * sizeof(float);
            Vector4 value = floatValues[index];
            WriteSingle(packet, offset, value.X);
            WriteSingle(packet, offset + 4, value.Y);
            WriteSingle(packet, offset + 8, value.Z);
            WriteSingle(packet, offset + 12, value.W);
        }
        int samplerOffset = 80 + registerBytes + valueBytes;
        WriteUInt32(packet, samplerOffset, samplerRegister);
        WriteUInt32(packet, samplerOffset + 4, (uint)samplingMode);
        WriteUInt32(packet, samplerOffset + 8, brushHandle);
    }

    private static bool ValidPadding(double value) => value >= 0 && double.IsFinite(value) && float.IsFinite((float)value);
}
