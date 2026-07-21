using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProGPU.Text.Bidi;
using ProGPU.Text.Shaping;

const string UnicodeVersion = "17.0.0";
const string CharacterTestUrl = $"https://www.unicode.org/Public/{UnicodeVersion}/ucd/BidiCharacterTest.txt";
const string CharacterTestSha256 = "a3e6e905ab5afbe318a96df5401d0372a04cd73ef139ab5e3cf0ae241c255488";
const string TypeTestUrl = $"https://www.unicode.org/Public/{UnicodeVersion}/ucd/BidiTest.txt";
const string TypeTestSha256 = "888bdfc8090652272d1f859cdb00ae659e2dc6c26740be61ef1d03998a687620";

string? characterTestPath = null;
string? typeTestPath = null;
for (int index = 0; index < args.Length; index++)
{
    if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}.");
    string option = args[index];
    string value = args[++index];
    switch (option)
    {
        case "--character-test-file": characterTestPath = value; break;
        case "--bidi-test-file": typeTestPath = value; break;
        default: throw new ArgumentException($"Unknown option: {option}");
    }
}

using var client = new HttpClient();
byte[] characterBytes = await ReadOrDownloadAsync(
    client,
    characterTestPath,
    CharacterTestUrl,
    CharacterTestSha256);
byte[] typeBytes = await ReadOrDownloadAsync(
    client,
    typeTestPath,
    TypeTestUrl,
    TypeTestSha256);

ValidationResult characterResult = ValidateCharacterTests(Encoding.UTF8.GetString(characterBytes));
ValidationResult typeResult = ValidateTypeTests(Encoding.UTF8.GetString(typeBytes));
WriteResult("BidiCharacterTest", characterResult);
WriteResult("BidiTest", typeResult);
return characterResult.Failures == 0 && typeResult.Failures == 0 ? 0 : 1;

static ValidationResult ValidateCharacterTests(string content)
{
    int cases = 0;
    int failures = 0;
    var diagnostics = new List<string>();
    using var reader = new StringReader(content);
    while (reader.ReadLine() is { } sourceLine)
    {
        string line = StripComment(sourceLine);
        if (line.Length == 0) continue;
        cases++;
        string[] fields = line.Split(';', StringSplitOptions.TrimEntries);
        if (fields.Length != 5) throw new InvalidDataException($"Invalid character test row: {line}");
        int[] codePoints = ParseCodePoints(fields[0]);
        ShapingDirection direction = fields[1] switch
        {
            "0" => ShapingDirection.LeftToRight,
            "1" => ShapingDirection.RightToLeft,
            "2" => ShapingDirection.Unspecified,
            _ => throw new InvalidDataException($"Invalid paragraph direction: {fields[1]}")
        };
        sbyte paragraphLevel = sbyte.Parse(fields[2], CultureInfo.InvariantCulture);
        if (!ValidateResolved(
                codePoints,
                direction,
                paragraphLevel,
                fields[3],
                fields[4],
                out string? diagnostic))
        {
            failures++;
            AddDiagnostic(diagnostics, cases, diagnostic, line);
        }
    }
    return new ValidationResult(cases, failures, diagnostics);
}

static ValidationResult ValidateTypeTests(string content)
{
    int cases = 0;
    int failures = 0;
    var diagnostics = new List<string>();
    string levels = string.Empty;
    string reorder = string.Empty;
    using var reader = new StringReader(content);
    while (reader.ReadLine() is { } sourceLine)
    {
        string line = StripComment(sourceLine);
        if (line.Length == 0) continue;
        if (line.StartsWith("@Levels:", StringComparison.Ordinal))
        {
            levels = line["@Levels:".Length..].Trim();
            continue;
        }
        if (line.StartsWith("@Reorder:", StringComparison.Ordinal))
        {
            reorder = line["@Reorder:".Length..].Trim();
            continue;
        }

        string[] fields = line.Split(';', StringSplitOptions.TrimEntries);
        if (fields.Length != 2) throw new InvalidDataException($"Invalid bidi type test row: {line}");
        string[] types = fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int[] codePoints = types.Select(TypeRepresentative).ToArray();
        int modes = int.Parse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        ValidateMode(1, ShapingDirection.Unspecified);
        ValidateMode(2, ShapingDirection.LeftToRight);
        ValidateMode(4, ShapingDirection.RightToLeft);

        void ValidateMode(int bit, ShapingDirection direction)
        {
            if ((modes & bit) == 0) return;
            cases++;
            if (!ValidateResolved(codePoints, direction, null, levels, reorder, out string? diagnostic))
            {
                failures++;
                AddDiagnostic(diagnostics, cases, diagnostic, $"{fields[0]}; mode={direction}");
            }
        }
    }
    return new ValidationResult(cases, failures, diagnostics);
}

static bool ValidateResolved(
    int[] codePoints,
    ShapingDirection direction,
    sbyte? expectedParagraphLevel,
    string expectedLevelsText,
    string expectedOrderText,
    out string? diagnostic)
{
    string text = string.Concat(codePoints.Select(char.ConvertFromUtf32));
    string[] expectedLevelTokens = expectedLevelsText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    int[] expectedOrder = expectedOrderText.Length == 0
        ? Array.Empty<int>()
        : expectedOrderText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => int.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
    if (expectedLevelTokens.Length != codePoints.Length)
        throw new InvalidDataException("Expected level count does not match input count.");

    BidiParagraph paragraph = BidiParagraph.Resolve(text, direction);
    if (expectedParagraphLevel is { } expectedParagraph && paragraph.ParagraphLevel != expectedParagraph)
    {
        diagnostic = $"paragraph level expected {expectedParagraph}, got {paragraph.ParagraphLevel}";
        return false;
    }

    var visibleLevels = new List<sbyte>();
    var visibleScalarIndices = new List<int>();
    int utf16Index = 0;
    for (int scalarIndex = 0; scalarIndex < codePoints.Length; scalarIndex++)
    {
        string token = expectedLevelTokens[scalarIndex];
        if (token != "x")
        {
            sbyte expected = sbyte.Parse(token, CultureInfo.InvariantCulture);
            sbyte actual = paragraph.Utf16Levels[utf16Index];
            if (actual != expected)
            {
                diagnostic = $"scalar {scalarIndex} U+{codePoints[scalarIndex]:X}: expected level {expected}, got {actual}";
                return false;
            }
            visibleLevels.Add(actual);
            visibleScalarIndices.Add(scalarIndex);
        }
        utf16Index += codePoints[scalarIndex] > 0xFFFF ? 2 : 1;
    }

    int[] actualVisibleOrder = BidiParagraph.GetVisualOrder(visibleLevels.ToArray());
    int[] actualOrder = actualVisibleOrder.Select(index => visibleScalarIndices[index]).ToArray();
    if (!actualOrder.SequenceEqual(expectedOrder))
    {
        diagnostic = $"order expected [{string.Join(' ', expectedOrder)}], got [{string.Join(' ', actualOrder)}]";
        return false;
    }

    diagnostic = null;
    return true;
}

static int TypeRepresentative(string type) => type switch
{
    "L" => 0x0061,
    "R" => 0x05D0,
    "AL" => 0x0627,
    "EN" => 0x0031,
    "ES" => 0x002B,
    "ET" => 0x0024,
    "AN" => 0x0661,
    "CS" => 0x002C,
    "NSM" => 0x0300,
    "BN" => 0x00AD,
    "B" => 0x2029,
    "S" => 0x0009,
    "WS" => 0x0020,
    "ON" => 0x0021,
    "LRE" => 0x202A,
    "LRO" => 0x202D,
    "RLE" => 0x202B,
    "RLO" => 0x202E,
    "PDF" => 0x202C,
    "LRI" => 0x2066,
    "RLI" => 0x2067,
    "FSI" => 0x2068,
    "PDI" => 0x2069,
    _ => throw new InvalidDataException($"Unknown bidi test type: {type}")
};

static int[] ParseCodePoints(string text) => text
    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
    .Select(static value => int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
    .ToArray();

static async Task<byte[]> ReadOrDownloadAsync(
    HttpClient client,
    string? path,
    string url,
    string expectedSha256)
{
    byte[] bytes = path is null
        ? await client.GetByteArrayAsync(url)
        : await File.ReadAllBytesAsync(path);
    string actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
    if (!actual.Equals(expectedSha256, StringComparison.Ordinal))
        throw new InvalidDataException($"SHA-256 mismatch for {path ?? url}: {actual}");
    return bytes;
}

static void AddDiagnostic(List<string> diagnostics, int number, string? diagnostic, string line)
{
    if (diagnostics.Count < 20) diagnostics.Add($"case {number}: {diagnostic}\n  {line}");
}

static void WriteResult(string name, ValidationResult result)
{
    foreach (string diagnostic in result.Diagnostics) Console.Error.WriteLine(diagnostic);
    Console.WriteLine(
        $"Unicode {UnicodeVersion} {name}: {result.Cases:N0} cases, {result.Failures:N0} failures.");
}

static string StripComment(string line)
{
    int comment = line.IndexOf('#');
    return (comment < 0 ? line : line[..comment]).Trim();
}

internal sealed record ValidationResult(int Cases, int Failures, List<string> Diagnostics);
