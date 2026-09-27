using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;

internal static class TextDeviceAdvanceValidation
{
    internal static void Run()
    {
        byte[] bytes = Font();
        using var context = new NativeTextShapingContext(bytes);
        Array.Clear(bytes);
        uint[] glyphs = [2, 0, 1, 2, 1, 0, 2, 2, 0];
        float[] output = new float[12];
        float[] expected = [1, 0, 255];
        for (int count = 0; count <= glyphs.Length; count++)
        {
            Array.Fill(output, -777);
            Check(context.GetDeviceAdvances(0, 12, glyphs.AsSpan(0, count), output, out bool available)
                == NativeRendererStatus.Success && available, "exact record");
            for (int index = 0; index < output.Length; index++)
                Check(output[index] == (index < count ? expected[glyphs[index]] : -777), "order/tail");
        }

        for (uint ppem = 1; ppem <= 40; ppem++)
        {
            Array.Fill(output, -777);
            Check(context.GetDeviceAdvances(0, ppem, glyphs, output, out bool available)
                == NativeRendererStatus.Success && available == (ppem is 12 or 16), "cache eviction/absence");
            for (int index = 0; index < output.Length; index++)
            {
                float value = index >= glyphs.Length || !available ? -777 :
                    ppem == 12 ? expected[glyphs[index]] : glyphs[index] + 2;
                Check(output[index] == value, "ppem identity/tail");
            }
        }

        for (uint face = 1; face <= 20; face++)
        {
            byte[] fallback = Font(checked((byte)(face + 4)));
            Check(context.AddFallbackFont(fallback, out uint fontIndex) == NativeRendererStatus.Success
                && fontIndex == face, "fallback registration");
            Array.Clear(fallback);
            Check(context.GetDeviceAdvances(face, 12, glyphs, output, out bool available)
                == NativeRendererStatus.Success && available && output[0] == face + 4, "owned fallback");
        }

        Check(context.GetDeviceAdvances(0, 12, glyphs, output, out bool primaryAvailable)
            == NativeRendererStatus.Success && primaryAvailable && output[0] == 255, "primary survives growth");
        Array.Fill(output, -777);
        uint[] invalid = [0, 1, 2, 0, uint.MaxValue];
        foreach (uint ppem in new uint[] { 12, 13 })
        {
            Check(context.GetDeviceAdvances(0, ppem, invalid, output, out bool available)
                == NativeRendererStatus.InvalidArgument && !available, "invalid final glyph");
            Check(output.All(value => value == -777), "atomic failure");
        }

        Check(context.GetDeviceAdvances(0, 12, glyphs, output.AsSpan(0, 8), out bool shortAvailable)
            == NativeRendererStatus.InvalidArgument && !shortAvailable, "short output");
        Check(output.All(value => value == -777), "short output unchanged");
        foreach (uint ppem in new uint[] { 0, 65536, uint.MaxValue })
            Check(context.GetDeviceAdvances(0, ppem, glyphs, output, out bool available)
                == NativeRendererStatus.InvalidArgument && !available, "ppem narrowing");
        Check(context.GetDeviceAdvances(21, 12, glyphs, output, out bool unknownAvailable)
            == NativeRendererStatus.InvalidArgument && !unknownAvailable, "unknown face");
        uint[] originalGlyphs = (uint[])glyphs.Clone();
        Check(context.GetDeviceAdvances(0, 12, glyphs, MemoryMarshal.Cast<uint, float>(glyphs.AsSpan()), out bool aliasedAvailable)
            == NativeRendererStatus.InvalidArgument && !aliasedAvailable, "aliased spans");
        Check(glyphs.SequenceEqual(originalGlyphs), "aliased input unchanged");
        byte[] damaged = Font();
        damaged[^1] = 1;
        using (var invalidFont = new NativeTextShapingContext(damaged))
        {
            Check(invalidFont.GetDeviceAdvances(0, 12, glyphs, output, out bool available)
                == NativeRendererStatus.InvalidArgument && !available, "malformed later record");
            Check(output.All(value => value == -777), "malformed record output unchanged");
        }

        using (var absent = new NativeTextShapingContext(Font(withDeviceTable: false)))
        {
            Check(absent.GetDeviceAdvances(0, 12, glyphs, output, out bool available)
                == NativeRendererStatus.Success && !available, "absent table");
            Check(output.All(value => value == -777), "absent table output unchanged");
        }

        context.Dispose();
        bool disposedRejected = false;
        try { context.GetDeviceAdvances(0, 12, glyphs, output, out _); }
        catch (ObjectDisposedException) { disposedRejected = true; }
        Check(disposedRejected, "disposed owner");
        Console.WriteLine("package-consumer: native device advances passed (exact ppem/face, cache, ownership, atomic spans)");
    }

    private static byte[] Font(byte lastWidth = 255, bool withDeviceTable = true)
    {
        byte[] data = new byte[withDeviceTable ? 100 : 60];
        Write32(0, 0x00010000);
        Write16(4, withDeviceTable ? (ushort)2 : (ushort)1);
        Write32(12, 0x6D617870);
        int maxp = withDeviceTable ? 44 : 28;
        Write32(20, (uint)maxp);
        Write32(24, 32);
        Write32(maxp, 0x00010000);
        Write16(maxp + 4, 3);
        if (withDeviceTable)
        {
            Write32(28, 0x68646D78);
            Write32(36, 76);
            Write32(40, 24);
            Write16(78, 2);
            Write32(80, 8);
            data[84] = 12; data[85] = lastWidth;
            data[86] = 1; data[87] = 0; data[88] = lastWidth;
            data[92] = 16; data[93] = 4;
            data[94] = 2; data[95] = 3; data[96] = 4;
        }

        return data;
        void Write16(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
        void Write32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
    }

    private static void Check(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException($"Native device advance contract failed: {contract}.");
    }
}
