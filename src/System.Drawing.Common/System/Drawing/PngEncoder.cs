using System;
using System.IO;

namespace System.Drawing;

internal static class PngEncoder
{
    private static readonly uint[] CrcTable = new uint[256];

    static PngEncoder()
    {
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                if ((c & 1) != 0)
                    c = 0xedb88320u ^ (c >> 1);
                else
                    c = c >> 1;
            }
            CrcTable[i] = c;
        }
    }

    public static void SavePng(string filePath, byte[] rgbaPixels, uint width, uint height)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

        byte[] ihdr = new byte[13];
        ihdr[0] = (byte)((width >> 24) & 0xFF);
        ihdr[1] = (byte)((width >> 16) & 0xFF);
        ihdr[2] = (byte)((width >> 8) & 0xFF);
        ihdr[3] = (byte)(width & 0xFF);
        
        ihdr[4] = (byte)((height >> 24) & 0xFF);
        ihdr[5] = (byte)((height >> 16) & 0xFF);
        ihdr[6] = (byte)((height >> 8) & 0xFF);
        ihdr[7] = (byte)(height & 0xFF);
        
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        
        WriteChunk(fs, "IHDR", ihdr);

        byte[] scanlines = new byte[height * (1 + width * 4)];
        int srcIndex = 0;
        int dstIndex = 0;
        for (int y = 0; y < height; y++)
        {
            scanlines[dstIndex++] = 0;
            Array.Copy(rgbaPixels, srcIndex, scanlines, dstIndex, (int)(width * 4));
            srcIndex += (int)(width * 4);
            dstIndex += (int)(width * 4);
        }

        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);

        using (var deflate = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(scanlines, 0, scanlines.Length);
        }

        uint adler = CalculateAdler32(scanlines);
        ms.WriteByte((byte)((adler >> 24) & 0xFF));
        ms.WriteByte((byte)((adler >> 16) & 0xFF));
        ms.WriteByte((byte)((adler >> 8) & 0xFF));
        ms.WriteByte((byte)(adler & 0xFF));

        WriteChunk(fs, "IDAT", ms.ToArray());

        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        
        uint length = (uint)(data?.Length ?? 0);
        stream.WriteByte((byte)((length >> 24) & 0xFF));
        stream.WriteByte((byte)((length >> 16) & 0xFF));
        stream.WriteByte((byte)((length >> 8) & 0xFF));
        stream.WriteByte((byte)(length & 0xFF));
        
        stream.Write(typeBytes, 0, 4);
        
        if (data != null && data.Length > 0)
        {
            stream.Write(data, 0, data.Length);
        }
        
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < 4; i++)
        {
            crc = CrcTable[(crc ^ typeBytes[i]) & 0xFF] ^ (crc >> 8);
        }
        if (data != null)
        {
            for (int i = 0; i < data.Length; i++)
            {
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }
        }
        crc ^= 0xFFFFFFFFu;
        
        stream.WriteByte((byte)((crc >> 24) & 0xFF));
        stream.WriteByte((byte)((crc >> 16) & 0xFF));
        stream.WriteByte((byte)((crc >> 8) & 0xFF));
        stream.WriteByte((byte)(crc & 0xFF));
    }

    private static uint CalculateAdler32(byte[] data)
    {
        uint s1 = 1;
        uint s2 = 0;
        for (int i = 0; i < data.Length; i++)
        {
            s1 = (s1 + data[i]) % 65521;
            s2 = (s2 + s1) % 65521;
        }
        return (s2 << 16) | s1;
    }
}
