using System.Globalization;
using System.Text;
using ACadSharp.Extensions;

namespace ProGPU.CAD;

public sealed class CadLinParseOptions
{
    public const int DefaultMaxFileBytes = 4 * 1024 * 1024;
    public const int DefaultMaxPhysicalLineLength = 4_096;
    public const int DefaultMaxDefinitionCount = 4_096;
    public const int DefaultMaxElementsPerDefinition = 256;
    public const int DefaultMaxTotalElementCount = 65_536;
    public const int DefaultMaxTextCodeUnits = 1_048_576;

    public int MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    public int MaxPhysicalLineLength { get; init; } =
        DefaultMaxPhysicalLineLength;

    public int MaxDefinitionCount { get; init; } = DefaultMaxDefinitionCount;

    public int MaxElementsPerDefinition { get; init; } =
        DefaultMaxElementsPerDefinition;

    public int MaxTotalElementCount { get; init; } =
        DefaultMaxTotalElementCount;

    public int MaxTextCodeUnits { get; init; } = DefaultMaxTextCodeUnits;
}

public enum CadLinElementKind
{
    Stroke,
    Text,
    Shape,
}

public enum CadLinRotationMode
{
    Relative,
    Absolute,
    Upright,
}

/// <summary>One immutable descriptor from an ASCII AutoCAD LIN definition.</summary>
public readonly record struct CadLinElement(
    CadLinElementKind Kind,
    double Length,
    string Payload,
    string StyleOrFileName,
    double Scale,
    CadLinRotationMode RotationMode,
    double RotationRadians,
    double XOffset,
    double YOffset)
{
    public bool IsImportSupported => RotationMode != CadLinRotationMode.Upright;
}

/// <summary>One immutable A-aligned linetype definition.</summary>
public sealed class CadLinDefinition
{
    private readonly CadLinElement[] _elements;

    public string Name { get; }

    public string Description { get; }

    public int HeaderLineNumber { get; }

    public ReadOnlyMemory<CadLinElement> Elements => _elements;

    public bool IsImportSupported =>
        _elements.All(static element => element.IsImportSupported);

    internal CadLinDefinition(
        string name,
        string description,
        int headerLineNumber,
        CadLinElement[] elements)
    {
        Name = name;
        Description = description;
        HeaderLineNumber = headerLineNumber;
        _elements = elements;
    }
}

/// <summary>
/// A bounded, detached representation of one ASCII AutoCAD LIN library.
/// </summary>
/// <remarks>
/// Parsing is O(B + E) time and O(B + E) owned storage for B input bytes and E
/// descriptors. It performs no document mutation, font IO, or renderer work.
/// </remarks>
public sealed class CadLinFile
{
    private readonly CadLinDefinition[] _definitions;

    public ReadOnlyMemory<CadLinDefinition> Definitions => _definitions;

    public int DefinitionCount => _definitions.Length;

    public int SupportedDefinitionCount =>
        _definitions.Count(static definition => definition.IsImportSupported);

    private CadLinFile(CadLinDefinition[] definitions)
    {
        _definitions = definitions;
    }

    public static CadLinFile Parse(
        ReadOnlySpan<byte> source,
        CadLinParseOptions? options = null)
    {
        options ??= new CadLinParseOptions();
        ValidateOptions(options);
        if (source.Length == 0 || source.Length > options.MaxFileBytes)
        {
            throw new InvalidDataException(
                $"LIN input must contain between 1 and " +
                $"{options.MaxFileBytes:N0} bytes.");
        }
        for (int i = 0; i < source.Length; i++)
        {
            byte value = source[i];
            if (value == 0 || value > 0x7F)
            {
                throw new InvalidDataException(
                    $"LIN input must be plain ASCII; byte {i:N0} is invalid.");
            }
        }

        string[] lines = Encoding.ASCII.GetString(source).Split('\n');
        var definitions = new List<CadLinDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PendingHeader? pending = null;
        int totalElements = 0;
        int totalTextCodeUnits = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string physical = lines[i];
            if (physical.EndsWith('\r'))
            {
                physical = physical[..^1];
            }
            int lineNumber = i + 1;
            if (physical.Length > options.MaxPhysicalLineLength)
            {
                throw Error(
                    lineNumber,
                    $"physical line exceeds {options.MaxPhysicalLineLength:N0} characters");
            }
            string line = physical.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '*')
            {
                if (pending is not null)
                {
                    throw Error(
                        lineNumber,
                        $"definition '{pending.Value.Name}' has no pattern line");
                }
                pending = ParseHeader(line, lineNumber, names);
                if (definitions.Count == options.MaxDefinitionCount)
                {
                    throw Error(
                        lineNumber,
                        $"definition count exceeds {options.MaxDefinitionCount:N0}");
                }
                continue;
            }

            if (pending is null)
            {
                throw Error(lineNumber, "pattern line has no preceding definition header");
            }
            CadLinElement[] elements = ParsePattern(
                line,
                lineNumber,
                options.MaxElementsPerDefinition,
                ref totalTextCodeUnits,
                options.MaxTextCodeUnits);
            totalElements = checked(totalElements + elements.Length);
            if (totalElements > options.MaxTotalElementCount)
            {
                throw Error(
                    lineNumber,
                    $"total descriptor count exceeds " +
                    $"{options.MaxTotalElementCount:N0}");
            }
            definitions.Add(new CadLinDefinition(
                pending.Value.Name,
                pending.Value.Description,
                pending.Value.LineNumber,
                elements));
            pending = null;
        }

        if (pending is not null)
        {
            throw Error(
                pending.Value.LineNumber,
                $"definition '{pending.Value.Name}' has no pattern line");
        }
        if (definitions.Count == 0)
        {
            throw new InvalidDataException("LIN input contains no definitions.");
        }
        return new CadLinFile(definitions.ToArray());
    }

    private static PendingHeader ParseHeader(
        string line,
        int lineNumber,
        HashSet<string> names)
    {
        int comma = line.IndexOf(',');
        string name = (comma < 0 ? line[1..] : line[1..comma]).Trim();
        string description = comma < 0 ? string.Empty : line[(comma + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 ||
            name.IndexOfAny(INamedCadObjectExtensions.InvalidCharacters) >= 0)
        {
            throw Error(lineNumber, $"linetype name '{name}' is invalid");
        }
        if (!names.Add(name))
        {
            throw Error(lineNumber, $"linetype name '{name}' is duplicated");
        }
        if (description.Length > 47)
        {
            throw Error(
                lineNumber,
                $"description for '{name}' exceeds 47 characters");
        }
        return new PendingHeader(name, description, lineNumber);
    }

    private static CadLinElement[] ParsePattern(
        string line,
        int lineNumber,
        int maxElements,
        ref int totalTextCodeUnits,
        int maxTextCodeUnits)
    {
        string[] tokens = SplitTopLevel(line, lineNumber, allowBrackets: true);
        if (tokens.Length < 3 ||
            !tokens[0].Equals("A", StringComparison.OrdinalIgnoreCase))
        {
            throw Error(
                lineNumber,
                "A-aligned LIN patterns require A and at least two descriptors");
        }
        int elementCount = tokens.Length - 1;
        if (elementCount > maxElements)
        {
            throw Error(
                lineNumber,
                $"pattern descriptor count exceeds {maxElements:N0}");
        }

        var elements = new CadLinElement[elementCount];
        double patternLength = 0.0;
        for (int i = 0; i < elementCount; i++)
        {
            string token = tokens[i + 1];
            CadLinElement element = token.StartsWith('[')
                ? ParseComplexDescriptor(token, lineNumber)
                : new CadLinElement(
                    CadLinElementKind.Stroke,
                    ParseDecimal(token, lineNumber, "stroke length"),
                    string.Empty,
                    string.Empty,
                    1.0,
                    CadLinRotationMode.Relative,
                    0.0,
                    0.0,
                    0.0);
            if (i == 0 && element.Length < 0.0)
            {
                throw Error(
                    lineNumber,
                    "the first A-aligned descriptor must be a dash or dot");
            }
            patternLength += Math.Abs(element.Length);
            if (!double.IsFinite(patternLength))
            {
                throw Error(lineNumber, "pattern length exceeds the finite CAD range");
            }
            if (element.Kind == CadLinElementKind.Text)
            {
                totalTextCodeUnits = checked(totalTextCodeUnits + element.Payload.Length);
                if (totalTextCodeUnits > maxTextCodeUnits)
                {
                    throw Error(
                        lineNumber,
                        $"embedded text exceeds {maxTextCodeUnits:N0} code units");
                }
            }
            elements[i] = element;
        }
        if (patternLength <= 0.0)
        {
            throw Error(lineNumber, "pattern must have a positive repeated length");
        }
        return elements;
    }

    private static CadLinElement ParseComplexDescriptor(
        string token,
        int lineNumber)
    {
        if (token.Length < 2 || token[^1] != ']')
        {
            throw Error(lineNumber, "complex descriptor has no closing bracket");
        }
        string[] fields = SplitTopLevel(
            token[1..^1],
            lineNumber,
            allowBrackets: false);
        if (fields.Length < 2)
        {
            throw Error(
                lineNumber,
                "complex descriptor requires a payload and style or SHX filename");
        }

        bool isText = fields[0].StartsWith('"');
        string payload = isText
            ? ParseQuotedText(fields[0], lineNumber)
            : fields[0];
        string styleOrFile = fields[1];
        if (string.IsNullOrWhiteSpace(payload) ||
            string.IsNullOrWhiteSpace(styleOrFile))
        {
            throw Error(lineNumber, "complex descriptor names cannot be empty");
        }
        if (!isText && !styleOrFile.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
        {
            throw Error(
                lineNumber,
                $"shape descriptor file '{styleOrFile}' must use the .shx extension");
        }

        double scale = 1.0;
        double rotation = 0.0;
        double xOffset = 0.0;
        double yOffset = 0.0;
        CadLinRotationMode rotationMode = CadLinRotationMode.Relative;
        bool hasScale = false;
        bool hasRotation = false;
        bool hasX = false;
        bool hasY = false;
        for (int i = 2; i < fields.Length; i++)
        {
            string field = fields[i];
            if (field.Length < 3 || field[1] != '=')
            {
                throw Error(
                    lineNumber,
                    $"complex transform '{field}' is invalid");
            }
            char key = char.ToUpperInvariant(field[0]);
            string value = field[2..].Trim();
            switch (key)
            {
                case 'S':
                    RejectDuplicate(ref hasScale, lineNumber, "scale");
                    scale = ParseDecimal(value, lineNumber, "complex scale");
                    if (scale == 0.0)
                    {
                        throw Error(lineNumber, "complex scale cannot be zero");
                    }
                    break;
                case 'R':
                case 'A':
                case 'U':
                    RejectDuplicate(ref hasRotation, lineNumber, "rotation");
                    rotationMode = key switch
                    {
                        'A' => CadLinRotationMode.Absolute,
                        'U' => CadLinRotationMode.Upright,
                        _ => CadLinRotationMode.Relative,
                    };
                    rotation = ParseRotation(value, lineNumber);
                    break;
                case 'X':
                    RejectDuplicate(ref hasX, lineNumber, "X offset");
                    xOffset = ParseDecimal(value, lineNumber, "X offset");
                    break;
                case 'Y':
                    RejectDuplicate(ref hasY, lineNumber, "Y offset");
                    yOffset = ParseDecimal(value, lineNumber, "Y offset");
                    break;
                default:
                    throw Error(
                        lineNumber,
                        $"complex transform key '{field[0]}' is unsupported");
            }
        }

        return new CadLinElement(
            isText ? CadLinElementKind.Text : CadLinElementKind.Shape,
            0.0,
            payload,
            styleOrFile,
            scale,
            rotationMode,
            rotation,
            xOffset,
            yOffset);
    }

    private static string[] SplitTopLevel(
        string source,
        int lineNumber,
        bool allowBrackets)
    {
        var tokens = new List<string>();
        int start = 0;
        int bracketDepth = 0;
        bool quoted = false;
        for (int i = 0; i < source.Length; i++)
        {
            char value = source[i];
            if (value == '"')
            {
                if (quoted && i + 1 < source.Length && source[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                quoted = !quoted;
                continue;
            }
            if (quoted)
            {
                continue;
            }
            if (value == '[')
            {
                if (!allowBrackets || bracketDepth != 0)
                {
                    throw Error(lineNumber, "nested complex brackets are invalid");
                }
                bracketDepth = 1;
            }
            else if (value == ']')
            {
                if (!allowBrackets || bracketDepth != 1)
                {
                    throw Error(lineNumber, "complex closing bracket is unmatched");
                }
                bracketDepth = 0;
            }
            else if (value == ',' && bracketDepth == 0)
            {
                AddToken(source[start..i], tokens, lineNumber);
                start = i + 1;
            }
        }
        if (quoted || bracketDepth != 0)
        {
            throw Error(lineNumber, "quoted text or complex bracket is unterminated");
        }
        AddToken(source[start..], tokens, lineNumber);
        return tokens.ToArray();
    }

    private static void AddToken(
        string value,
        List<string> destination,
        int lineNumber)
    {
        string token = value.Trim();
        if (token.Length == 0)
        {
            throw Error(lineNumber, "empty LIN descriptor field");
        }
        destination.Add(token);
    }

    private static string ParseQuotedText(string value, int lineNumber)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            throw Error(lineNumber, "embedded text must be enclosed in quotes");
        }
        string source = value[1..^1];
        if (!source.Contains('"'))
        {
            return source;
        }
        var result = new StringBuilder(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != '"')
            {
                result.Append(source[i]);
                continue;
            }
            if (i + 1 >= source.Length || source[i + 1] != '"')
            {
                throw Error(lineNumber, "embedded quote must be doubled");
            }
            result.Append('"');
            i++;
        }
        return result.ToString();
    }

    private static double ParseRotation(string value, int lineNumber)
    {
        double multiplier = Math.PI / 180.0;
        if (value.Length > 0 && char.IsLetter(value[^1]))
        {
            multiplier = char.ToLowerInvariant(value[^1]) switch
            {
                'd' => Math.PI / 180.0,
                'r' => 1.0,
                'g' => Math.PI / 200.0,
                _ => throw Error(
                    lineNumber,
                    $"rotation unit '{value[^1]}' is invalid"),
            };
            value = value[..^1].Trim();
        }
        double angle = ParseDecimal(value, lineNumber, "rotation");
        double radians = angle * multiplier;
        if (!double.IsFinite(radians))
        {
            throw Error(lineNumber, "rotation exceeds the finite CAD range");
        }
        return radians;
    }

    private static double ParseDecimal(
        string value,
        int lineNumber,
        string fieldName)
    {
        const NumberStyles Styles =
            NumberStyles.AllowLeadingSign |
            NumberStyles.AllowDecimalPoint;
        if (!double.TryParse(
                value,
                Styles,
                CultureInfo.InvariantCulture,
                out double result) ||
            !double.IsFinite(result))
        {
            throw Error(lineNumber, $"{fieldName} '{value}' is not a finite decimal");
        }
        return result;
    }

    private static void RejectDuplicate(
        ref bool observed,
        int lineNumber,
        string fieldName)
    {
        if (observed)
        {
            throw Error(lineNumber, $"complex {fieldName} is duplicated");
        }
        observed = true;
    }

    private static InvalidDataException Error(int lineNumber, string message) =>
        new($"LIN line {lineNumber:N0}: {message}.");

    private static void ValidateOptions(CadLinParseOptions options)
    {
        if (options.MaxFileBytes <= 0 ||
            options.MaxPhysicalLineLength <= 0 ||
            options.MaxDefinitionCount <= 0 ||
            options.MaxElementsPerDefinition < 2 ||
            options.MaxTotalElementCount < 2 ||
            options.MaxTextCodeUnits <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Every LIN parser limit must be positive and element limits " +
                "must permit at least two descriptors.");
        }
    }

    private readonly record struct PendingHeader(
        string Name,
        string Description,
        int LineNumber);
}
