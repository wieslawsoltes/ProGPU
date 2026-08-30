using System.Globalization;
using System.Text;

namespace ProGPU.CAD;

/// <summary>
/// Resolves the persisted DWGCODEPAGE contract to a strict, reusable encoding.
/// This work belongs to immutable snapshot preparation, never retained replay.
/// </summary>
internal static class CadDrawingCodePage
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Encoding> Encodings =
        new(StringComparer.OrdinalIgnoreCase);

    static CadDrawingCodePage() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new NotSupportedException(
                "Drawing-code-page SHX text requires a persisted DWGCODEPAGE value.");
        }

        string key = name.Trim();
        lock (Gate)
        {
            if (Encodings.TryGetValue(key, out Encoding? cached))
            {
                return cached;
            }

            Encoding encoding = Create(key);
            Encodings.Add(key, encoding);
            return encoding;
        }
    }

    private static Encoding Create(string name)
    {
        try
        {
            return Encoding.GetEncoding(
                name,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            int codePage = ResolveNumericAlias(name);
            if (codePage <= 0)
            {
                throw new NotSupportedException(
                    $"Drawing code page '{name}' is not available for strict SHX character mapping.");
            }
            try
            {
                return Encoding.GetEncoding(
                    codePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException exception)
            {
                throw new NotSupportedException(
                    $"Drawing code page '{name}' ({codePage}) is not available for strict SHX character mapping.",
                    exception);
            }
        }
    }

    private static int ResolveNumericAlias(string name)
    {
        string normalized = name.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized switch
        {
            "gb2312" => 936,
            "kcs5601" => 949,
            "big5" => 950,
            "johab" => 1361,
            "ascii" => 20127,
            "macroman" => 10000,
            _ when TryReadPrefixedNumber(normalized, "ansi", out int ansi) => ansi,
            _ when TryReadPrefixedNumber(normalized, "dos", out int dos) => dos,
            _ => 0,
        };
    }

    private static bool TryReadPrefixedNumber(
        string value,
        string prefix,
        out int codePage)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            codePage = 0;
            return false;
        }
        ReadOnlySpan<char> suffix = value.AsSpan(prefix.Length);
        codePage = 0;
        return suffix.Length is >= 3 and <= 5 &&
            int.TryParse(
                suffix,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out codePage);
    }
}
