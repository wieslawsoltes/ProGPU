using System.Buffers;
using System.Text;

#nullable disable

namespace SkiaSharp;

/// <summary>
/// Provides bounded UTF text conversion helpers for SkiaSharp compatibility.
/// </summary>
public static class StringUtilities
{
    public static byte[] GetEncodedText(string text, SKTextEncoding encoding) =>
        GetEncodedText((text ?? string.Empty).AsSpan(), encoding);

    public static byte[] GetEncodedText(
        ReadOnlySpan<char> text,
        SKTextEncoding encoding)
    {
        var codec = GetEncoding(encoding);
        if (text.IsEmpty)
            return [];

        var bytes = GC.AllocateUninitializedArray<byte>(codec.GetByteCount(text));
        codec.GetBytes(text, bytes);
        return bytes;
    }

    public static string GetString(byte[] data, SKTextEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(data);
        return GetEncoding(encoding).GetString(data);
    }

    public static string GetString(
        byte[] data,
        int index,
        int count,
        SKTextEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(data);
        return GetEncoding(encoding).GetString(data, index, count);
    }

    public static string GetString(
        ReadOnlySpan<byte> data,
        SKTextEncoding encoding) =>
        GetEncoding(encoding).GetString(data);

    public static string GetString(
        ReadOnlySpan<byte> data,
        int index,
        int count,
        SKTextEncoding encoding) =>
        GetEncoding(encoding).GetString(data.Slice(index, count));

    public static unsafe string GetString(
        IntPtr data,
        int dataLength,
        SKTextEncoding encoding)
    {
        var codec = GetEncoding(encoding);
        return dataLength == 0
            ? string.Empty
            : codec.GetString((byte*)data, dataLength);
    }

    public static int GetUnicodeCharacterCode(
        string character,
        SKTextEncoding encoding)
    {
        _ = GetEncoding(encoding);
        ArgumentNullException.ThrowIfNull(character);

        var status = Rune.DecodeFromUtf16(
            character.AsSpan(),
            out var rune,
            out var consumed);
        if (status != OperationStatus.Done || consumed != character.Length)
        {
            throw new ArgumentException(
                "Only a single character can be specified.",
                nameof(character));
        }

        return rune.Value;
    }

    private static Encoding GetEncoding(SKTextEncoding encoding) => encoding switch
    {
        SKTextEncoding.Utf8 => Encoding.UTF8,
        SKTextEncoding.Utf16 => Encoding.Unicode,
        SKTextEncoding.Utf32 => Encoding.UTF32,
        _ => throw new ArgumentOutOfRangeException(
            nameof(encoding),
            $"Encoding {encoding} is not supported."),
    };

    // The official type retains ExtensionAttribute metadata even though its
    // current public methods are ordinary static methods.
    private static void PreserveExtensionTypeMetadata(this object value)
    {
    }
}
