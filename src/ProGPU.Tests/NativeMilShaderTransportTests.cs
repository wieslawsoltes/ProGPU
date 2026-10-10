using System.Buffers.Binary;
using System.Numerics;
using ProGPU.Backend.Native;
using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

// Independent original MIL field offsets: PixelShader 108/20 bytes,
// ImplicitInputBrush 109/28, ShaderEffect 112/80. This is byte transport,
// not bytecode execution, shader compilation, native rendering or UI parity.
public sealed class NativeMilShaderTransportTests
{
    private static byte[] OriginalProgram()
    {
        // Authored ps_2_0: dcl t0.xy; dcl_2d s0; texld r0,t0,s0;
        // mov_sat oC0,r0; end. SAT and all original tokens remain unchanged.
        uint[] words = [0xffff0200, 0x0200001f, 0x80000000, 0xb0030000,
            0x0200001f, 0x90000000, 0xa00f0800,
            0x03000042, 0x800f0000, 0xb0e40000, 0xa0e40800,
            0x02000001, 0x801f0800, 0x80e40000, 0x0000ffff];
        var bytes = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static float F32(byte[] bytes, int offset) => BitConverter.Int32BitsToSingle(unchecked((int)U32(bytes, offset)));
    private static double F64(byte[] bytes, int offset) => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset)));
    private static NativeMilBatchBuilder Seed()
    {
        var result = new NativeMilBatchBuilder(); result.SetImplicitInputBrush(91); return result;
    }

    [Theory]
    [InlineData(0U, false)] [InlineData(0U, true)] [InlineData(2U, false)] [InlineData(2U, true)]
    public void PixelShaderRetainsOriginalTokensAndSoftwareCompilationIntent(uint mode, bool software)
    {
        byte[] bytes = OriginalProgram(), original = bytes.ToArray();
        var writer = new NativeMilBatchBuilder();
        writer.SetPixelShader(17, bytes, (NativeMilShaderRenderMode)mode, software);
        byte[] packet = writer.ToArray();
        Assert.Equal((uint)(24 + bytes.Length), U32(packet, 0)); Assert.Equal(108U, U32(packet, 4));
        Assert.Equal(17U, U32(packet, 8)); Assert.Equal(mode, U32(packet, 12));
        Assert.Equal((uint)bytes.Length, U32(packet, 16)); Assert.Equal(software ? 1U : 0U, U32(packet, 20));
        Assert.Equal(original, packet[24..]); Assert.Equal(original, bytes);
        Assert.Equal(0x801f0800U, U32(packet, 24 + 12 * 4));
        Array.Fill(bytes, (byte)0xa5); Assert.Equal(original, writer.ToArray()[24..]);
    }

    [Fact]
    public void ImplicitInputIsExactlyOpacityOneWithoutTransformOrAnimation()
    {
        var writer = new NativeMilBatchBuilder(); writer.SetImplicitInputBrush(23);
        byte[] packet = writer.ToArray(); Assert.Equal(32, packet.Length);
        Assert.Equal(32U, U32(packet, 0)); Assert.Equal(109U, U32(packet, 4)); Assert.Equal(23U, U32(packet, 8));
        Assert.Equal(1.0, F64(packet, 12)); Assert.All(packet[20..], value => Assert.Equal((byte)0, value));
    }

    [Theory]
    [InlineData(0U)] [InlineData(1U)] [InlineData(2U)]
    public void SamplerUsesOriginalAutoNearestBilinearWireValues(uint mode)
    {
        var writer = new NativeMilBatchBuilder();
        writer.SetShaderEffect(31, 41, [], [], 15, (NativeMilShaderSamplingMode)mode, 51);
        byte[] packet = writer.ToArray(); Assert.Equal(96, packet.Length);
        Assert.Equal(112U, U32(packet, 4)); Assert.Equal(31U, U32(packet, 8));
        Assert.All(packet[12..44], value => Assert.Equal((byte)0, value));
        Assert.Equal(41U, U32(packet, 44)); Assert.Equal(uint.MaxValue, U32(packet, 48));
        Assert.All(packet[52..76], value => Assert.Equal((byte)0, value));
        Assert.Equal(8U, U32(packet, 76)); Assert.Equal(4U, U32(packet, 80));
        Assert.Equal(15U, U32(packet, 84)); Assert.Equal(mode, U32(packet, 88)); Assert.Equal(51U, U32(packet, 92));
    }

    [Fact]
    public void OddInt16RegisterArrayHasNoInteriorAlignmentAndOnlyZeroTailPadding()
    {
        short[] registers = [1, 7, 31]; Vector4[] values = [new(1, 2, 3, 4), new(-1, -2, -3, -4), new(0.25f, -0.0f, 7.5f, 11)];
        var savedRegisters = registers.ToArray(); var savedValues = values.ToArray();
        var writer = new NativeMilBatchBuilder();
        writer.SetShaderEffect(2, 3, registers, values, 4, NativeMilShaderSamplingMode.Bilinear, 5, 31);
        byte[] packet = writer.ToArray(); Assert.Equal(152, packet.Length); Assert.Equal(152U, U32(packet, 0));
        Assert.Equal(31U, U32(packet, 48)); Assert.Equal(6U, U32(packet, 52)); Assert.Equal(48U, U32(packet, 56));
        Assert.Equal((ushort)1, U16(packet, 84)); Assert.Equal((ushort)7, U16(packet, 86)); Assert.Equal((ushort)31, U16(packet, 88));
        for (int i = 0; i < values.Length; i++) CheckVector(packet, 90 + 16 * i, values[i]);
        Assert.Equal(4U, U32(packet, 138)); Assert.Equal(2U, U32(packet, 142)); Assert.Equal(5U, U32(packet, 146));
        Assert.Equal((byte)0, packet[150]); Assert.Equal((byte)0, packet[151]);
        Assert.Equal(savedRegisters, registers); Assert.Equal(savedValues, values);
        registers[0] = 20; values[0] = new(99); Assert.Equal(packet, writer.ToArray());
        writer.SetImplicitInputBrush(6); Assert.Equal(32U, U32(writer.ToArray(), 152));
    }

    [Fact]
    public void AllThirtyTwoFloatRegistersPreserveEveryLaneInOrder()
    {
        short[] registers = Enumerable.Range(0, 32).Select(i => (short)i).ToArray();
        Vector4[] values = Enumerable.Range(0, 32).Select(i => new Vector4(i + 0.25f, -i - 0.5f, i + 0.75f, -i - 1)).ToArray();
        var writer = new NativeMilBatchBuilder(); writer.SetShaderEffect(2, 3, registers, values, 0, NativeMilShaderSamplingMode.Auto, 4);
        byte[] packet = writer.ToArray(); Assert.Equal(672, packet.Length);
        Assert.Equal(64U, U32(packet, 52)); Assert.Equal(512U, U32(packet, 56));
        for (int i = 0; i < 32; i++) { Assert.Equal((ushort)i, U16(packet, 84 + 2 * i)); CheckVector(packet, 148 + 16 * i, values[i]); }
        Assert.Equal(0U, U32(packet, 660)); Assert.Equal(0U, U32(packet, 664)); Assert.Equal(4U, U32(packet, 668));
    }

    [Fact]
    public void CallerSpanTailsAreNeitherValidatedNorSerialized()
    {
        short[] registers = [2, -1]; Vector4[] values = [new(1, 2, 3, 4), new(float.NaN)];
        byte[] bytecode = [0, 2, 255, 255, 255, 255, 0, 0, 0xa5];
        var writer = new NativeMilBatchBuilder(); writer.SetShaderEffect(2, 3, registers.AsSpan(0, 1), values.AsSpan(0, 1), 0, NativeMilShaderSamplingMode.Auto, 4);
        Assert.Equal((short)-1, registers[1]); Assert.True(float.IsNaN(values[1].W));
        var shader = new NativeMilBatchBuilder(); shader.SetPixelShader(1, bytecode.AsSpan(0, 8));
        Assert.Equal(32, shader.Length); Assert.Equal((byte)0xa5, bytecode[8]);
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(9)] [InlineData(65540)]
    public void InvalidBytecodeLengthLeavesEarlierPacketsUntouched(int count)
    {
        var writer = Seed(); byte[] before = writer.ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetPixelShader(2, new byte[count])); Assert.Equal(before, writer.ToArray());
    }

    [Theory]
    [InlineData(1U)] [InlineData(3U)] [InlineData(uint.MaxValue)]
    public void SoftwareOnlyAndUnknownRenderModesRejectAtomically(uint mode)
    {
        var writer = Seed(); byte[] before = writer.ToArray();
        Assert.Throws<NotSupportedException>(() => writer.SetPixelShader(2, OriginalProgram(), (NativeMilShaderRenderMode)mode, true));
        Assert.Equal(before, writer.ToArray());
    }

    [Fact]
    public void MaximumBytecodeSizeRemainsOriginalBytesNotManagedInstructionAdmission()
    {
        var bytes = new byte[65536]; for (int i = 0; i < bytes.Length; i++) bytes[i] = unchecked((byte)i);
        var writer = new NativeMilBatchBuilder(); writer.SetPixelShader(2, bytes);
        Assert.Equal(bytes, writer.ToArray()[24..]);
        // The native translator, not this packet writer, rejects unsupported tokens.
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(31)]
    public void DerivativeRegisterMetadataDoesNotOverwriteUserConstants(int register)
    {
        var writer = new NativeMilBatchBuilder();
        writer.SetShaderEffect(2, 3, [0, 31], [new(7), new(11)], 0, NativeMilShaderSamplingMode.Auto, 4, register);
        byte[] packet = writer.ToArray(); Assert.Equal(unchecked((uint)register), U32(packet, 48));
        CheckVector(packet, 88, new(7)); CheckVector(packet, 104, new(11));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)]
    [InlineData(18)] [InlineData(19)] [InlineData(20)]
    public void LateInvalidEffectOperandCannotAppendAnyPacketBytes(int invalid)
    {
        var writer = Seed(); byte[] before = writer.ToArray(); short[] registers = [0, 31]; Vector4[] values = [new(1), new(2)];
        uint handle = 2, shader = 3, brush = 4, sampler = 0; int derivative = -1; var sampling = NativeMilShaderSamplingMode.Auto;
        switch (invalid)
        {
            case 0: handle = 0; break; case 1: shader = 0; break; case 2: brush = 0; break;
            case 3: registers = [0, -1]; break; case 4: registers = [0, 32]; break;
            case 5: registers = [1, 1]; break; case 6: registers = [2, 1]; break;
            case 7: values = [new(1)]; break;
            case 8: registers = Enumerable.Range(0, 33).Select(i => (short)i).ToArray(); values = new Vector4[33]; break;
            case 9: values[1].X = float.NaN; break; case 10: values[1].Y = float.PositiveInfinity; break;
            case 11: values[1].Z = float.NegativeInfinity; break; case 12: values[1].W = float.NaN; break;
            case 13: sampler = 16; break; case 14: sampler = uint.MaxValue; break;
            case 15: sampling = (NativeMilShaderSamplingMode)3; break; case 16: sampling = (NativeMilShaderSamplingMode)uint.MaxValue; break;
            case 17: derivative = -2; break; case 18: derivative = 32; break;
            case 19: registers = []; break; case 20: values = []; break;
        }
        var originalRegisters = registers.ToArray(); var originalValues = values.ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetShaderEffect(handle, shader, registers, values, sampler, sampling, brush, derivative));
        Assert.Equal(before, writer.ToArray()); Assert.Equal(originalRegisters, registers); Assert.Equal(originalValues, values);
    }

    [Fact]
    public void ZeroShaderAndImplicitHandlesPreserveEarlierPackets()
    {
        var writer = Seed(); byte[] before = writer.ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetPixelShader(0, OriginalProgram()));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetImplicitInputBrush(0)); Assert.Equal(before, writer.ToArray());
    }

    [Fact]
    public void PaddingOverloadPreservesAllOriginalDoubleBitsAndLegacyZeroBytes()
    {
        var padding = new NativeMilShaderPadding(-0.0, Math.BitIncrement(2.0), 0.5, Math.BitDecrement(12.0));
        var writer = new NativeMilBatchBuilder();
        writer.SetShaderEffect(2, 3, [0], [new(0.25f)], 0, NativeMilShaderSamplingMode.Bilinear, 4, padding, 31);
        byte[] packet = writer.ToArray();
        double[] values = [padding.Top, padding.Bottom, padding.Left, padding.Right];
        for (int i = 0; i < values.Length; ++i)
            Assert.Equal(BitConverter.DoubleToInt64Bits(values[i]), BitConverter.DoubleToInt64Bits(F64(packet, 12 + i * 8)));
        Assert.Equal(31U, U32(packet, 48));
        var legacy = new NativeMilBatchBuilder();
        legacy.SetShaderEffect(2, 3, [0], [new(0.25f)], 0, NativeMilShaderSamplingMode.Bilinear, 4, 31);
        var explicitZero = new NativeMilBatchBuilder();
        explicitZero.SetShaderEffect(2, 3, [0], [new(0.25f)], 0, NativeMilShaderSamplingMode.Bilinear, 4,
            default(NativeMilShaderPadding), 31);
        Assert.Equal(legacy.ToArray(), explicitZero.ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void InvalidPaddingAxisOrLateConstantLeavesEarlierPacketsUntouched(int axis)
    {
        foreach (double invalid in new[] { -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue })
        {
            double[] values = [2, 6, 4, 12]; values[axis] = invalid;
            var padding = new NativeMilShaderPadding(values[0], values[1], values[2], values[3]);
            var writer = Seed(); byte[] before = writer.ToArray();
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetShaderEffect(2, 3, [0], [new(1)],
                0, NativeMilShaderSamplingMode.Auto, 4, padding));
            Assert.Equal(before, writer.ToArray());
        }
        var late = Seed(); byte[] original = late.ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => late.SetShaderEffect(2, 3, [0], [new(float.NaN)],
            0, NativeMilShaderSamplingMode.Auto, 4, new NativeMilShaderPadding(2, 6, 4, 12)));
        Assert.Equal(original, late.ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void SourcePaddingTransportCannotRepairInvalidMetadataBeforeNativePacketAdmission(int axis)
    {
        foreach (double invalid in new[] { -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue })
        {
            double[] source = [2, 6, 4, 12]; source[axis] = invalid;
            var effect = new PortableShaderEffect(null, null, null, null, null,
                0, 0, source[0], source[1], source[2], source[3], -1);
            var padding = new NativeMilShaderPadding(effect.PaddingTop, effect.PaddingBottom,
                effect.PaddingLeft, effect.PaddingRight);
            var writer = Seed(); byte[] before = writer.ToArray();
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.SetShaderEffect(2, 3, [], [],
                0, NativeMilShaderSamplingMode.Auto, 4, padding));
            Assert.Equal(before, writer.ToArray());
        }
    }

    [Fact]
    public void SourcePaddingTransportPreservesSignedZeroAndSubFloatPrecisionInNativePacket()
    {
        double[] source = [-0.0, Math.BitIncrement(2.0), double.Epsilon, Math.BitDecrement(12.0)];
        var effect = new PortableShaderEffect(null, null, null, null, null,
            0, 0, source[0], source[1], source[2], source[3], -1);
        var writer = new NativeMilBatchBuilder();
        writer.SetShaderEffect(2, 3, [], [], 0, NativeMilShaderSamplingMode.Auto, 4,
            new NativeMilShaderPadding(effect.PaddingTop, effect.PaddingBottom, effect.PaddingLeft, effect.PaddingRight));
        byte[] packet = writer.ToArray();
        for (int i = 0; i < source.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(source[i]), BitConverter.DoubleToInt64Bits(F64(packet, 12 + i * 8)));
    }

    private static void CheckVector(byte[] bytes, int offset, Vector4 value)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(value.X), BitConverter.SingleToInt32Bits(F32(bytes, offset)));
        Assert.Equal(BitConverter.SingleToInt32Bits(value.Y), BitConverter.SingleToInt32Bits(F32(bytes, offset + 4)));
        Assert.Equal(BitConverter.SingleToInt32Bits(value.Z), BitConverter.SingleToInt32Bits(F32(bytes, offset + 8)));
        Assert.Equal(BitConverter.SingleToInt32Bits(value.W), BitConverter.SingleToInt32Bits(F32(bytes, offset + 12)));
    }
}
