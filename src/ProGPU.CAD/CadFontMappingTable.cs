using System.Text;

namespace ProGPU.CAD;

public sealed class CadFontMappingParseOptions
{
    public const int DefaultMaxFileBytes = 1 * 1024 * 1024;
    public const int DefaultMaxMappings = 16_384;
    public const int DefaultMaxLineBytes = 1_024;

    public int MaxFileBytes { get; init; } = DefaultMaxFileBytes;
    public int MaxMappings { get; init; } = DefaultMaxMappings;
    public int MaxLineBytes { get; init; } = DefaultMaxLineBytes;
}

public readonly record struct CadFontMapping(
    string RequestedFontName,
    string ReplacementFontFilename);

/// <summary>An immutable bounded AutoCAD ASCII font-mapping table.</summary>
/// <remarks>
/// Parsing is O(B) time and O(M + T) storage for B source bytes, M mappings,
/// and T retained filename characters. The parser implements the documented
/// one original/substitute pair per line contract and rejects ambiguous input.
/// </remarks>
public sealed class CadFontMappingTable
{
    private readonly CadFontMapping[] _mappings;

    public ReadOnlyMemory<CadFontMapping> Mappings => _mappings;

    private CadFontMappingTable(CadFontMapping[] mappings)
    {
        _mappings = mappings;
    }

    public static CadFontMappingTable Parse(
        ReadOnlySpan<byte> source,
        CadFontMappingParseOptions? options = null)
    {
        options ??= new CadFontMappingParseOptions();
        ValidateOptions(options);
        if (source.Length == 0 || source.Length > options.MaxFileBytes)
        {
            throw new InvalidDataException(
                $"Font mapping input must contain between 1 and {options.MaxFileBytes} bytes.");
        }

        var mappings = new List<CadFontMapping>();
        var requestedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int lineStart = 0;
        int lineNumber = 1;
        while (lineStart < source.Length)
        {
            int lineEnd = lineStart;
            while (lineEnd < source.Length && source[lineEnd] is not ((byte)'\r' or (byte)'\n'))
            {
                if (source[lineEnd] > 0x7F)
                {
                    throw Invalid(lineNumber, "contains non-ASCII data");
                }
                lineEnd++;
            }

            if (lineEnd - lineStart > options.MaxLineBytes)
            {
                throw Invalid(lineNumber, $"exceeds the configured {options.MaxLineBytes}-byte line limit");
            }
            ReadOnlySpan<byte> line = TrimAsciiWhitespace(source[lineStart..lineEnd]);
            if (!line.IsEmpty)
            {
                int separator = line.IndexOf((byte)';');
                if (separator <= 0 || separator != line.LastIndexOf((byte)';'))
                {
                    throw Invalid(lineNumber, "must contain exactly one separating semicolon");
                }

                ReadOnlySpan<byte> requestedBytes = TrimAsciiWhitespace(line[..separator]);
                ReadOnlySpan<byte> replacementBytes = TrimAsciiWhitespace(line[(separator + 1)..]);
                ValidateFilename(requestedBytes, lineNumber, "requested font");
                ValidateFilename(replacementBytes, lineNumber, "replacement font");
                int extensionSeparator = replacementBytes.LastIndexOf((byte)'.');
                if (extensionSeparator <= 0 || extensionSeparator == replacementBytes.Length - 1)
                {
                    throw Invalid(lineNumber, "replacement font must include a file extension");
                }
                if (mappings.Count == options.MaxMappings)
                {
                    throw new InvalidDataException(
                        $"Font mapping count exceeds the configured limit of {options.MaxMappings}.");
                }

                string requested = Encoding.ASCII.GetString(requestedBytes);
                if (!requestedNames.Add(requested))
                {
                    throw Invalid(lineNumber, $"duplicates requested font '{requested}'");
                }
                mappings.Add(new CadFontMapping(
                    requested,
                    Encoding.ASCII.GetString(replacementBytes)));
            }

            if (lineEnd == source.Length)
            {
                break;
            }
            if (source[lineEnd] == '\r' && lineEnd + 1 < source.Length &&
                source[lineEnd + 1] == '\n')
            {
                lineEnd++;
            }
            lineStart = lineEnd + 1;
            lineNumber++;
        }

        if (mappings.Count == 0)
        {
            throw new InvalidDataException("Font mapping input contains no mappings.");
        }
        return new CadFontMappingTable(mappings.ToArray());
    }

    private static void ValidateFilename(
        ReadOnlySpan<byte> value,
        int lineNumber,
        string field)
    {
        if (value.IsEmpty)
        {
            throw Invalid(lineNumber, $"has an empty {field}");
        }
        for (int i = 0; i < value.Length; i++)
        {
            byte item = value[i];
            if (item < 0x20 || item >= 0x7F || item is (byte)'/' or (byte)'\\')
            {
                throw Invalid(lineNumber, $"{field} must be a filename without a path");
            }
        }
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
    {
        int start = 0;
        while (start < value.Length && value[start] is (byte)' ' or (byte)'\t')
        {
            start++;
        }
        int end = value.Length;
        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t')
        {
            end--;
        }
        return value[start..end];
    }

    private static InvalidDataException Invalid(int lineNumber, string message) =>
        new($"Font mapping line {lineNumber} {message}.");

    private static void ValidateOptions(CadFontMappingParseOptions options)
    {
        if (options.MaxFileBytes <= 0 || options.MaxMappings <= 0 ||
            options.MaxLineBytes <= 0 || options.MaxLineBytes > options.MaxFileBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Font mapping parser limits must be positive and internally bounded.");
        }
    }
}
