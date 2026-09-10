using System.Text.Json;
using System.Xml.Linq;

Dictionary<string, string> options = ParseOptions(args);
string resultsPath = Require(options, "results");
string listPath = Require(options, "list");
string testsRoot = Path.GetFullPath(Require(options, "tests-root"));
string exclusionsPath = Require(options, "excluded-files");
string methodExclusionsPath = Require(options, "excluded-methods");
string knownFailuresPath = Require(options, "known-failures");
string knownSkipsPath = Require(options, "known-skips");
string knownSummaryPath = Require(options, "known-summary");
string artifactsPath = Path.GetFullPath(Require(options, "artifacts"));
bool updateBaseline = options.ContainsKey("update-baseline");

Directory.CreateDirectory(artifactsPath);

XDocument results = XDocument.Load(resultsPath, LoadOptions.None);
XElement assembly = results.Descendants("assembly").Single();
XElement[] errors = assembly.Element("errors")?.Elements("error").ToArray() ?? [];
if (errors.Length != 0)
{
    foreach (XElement error in errors)
    {
        XElement? failure = error.Element("failure");
        Console.Error.WriteLine(
            $"Runner error: {failure?.Attribute("exception-type")?.Value}: {failure?.Element("message")?.Value}");
    }

    return 1;
}

XElement[] tests = assembly.Descendants("test").ToArray();
string[] failures = BuildOutcomeInventory(tests, "Fail", static test =>
    test.Element("failure")?.Attribute("exception-type")?.Value ?? "<missing-exception-type>");
string[] skips = BuildOutcomeInventory(tests, "Skip", static test =>
    Normalize(test.Element("reason")?.Value ?? "<missing-reason>"));

using JsonDocument discoveredJson = JsonDocument.Parse(File.ReadAllText(listPath));
string[] discovered = discoveredJson.RootElement.EnumerateArray()
    .Select(static element => element.GetString() ?? throw new InvalidDataException("A discovered test name was null."))
    .ToArray();
int discoveredMethods = discovered.Length;

Dictionary<string, string> exclusions = ReadReasonManifest(exclusionsPath);
Dictionary<string, string> methodExclusions = ReadReasonManifest(methodExclusionsPath);
string[] sourceFiles = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
    .Select(path => Path.GetRelativePath(testsRoot, path).Replace('\\', '/'))
    .Order(StringComparer.Ordinal)
    .ToArray();

string[] unknownExclusions = exclusions.Keys.Except(sourceFiles, StringComparer.Ordinal).Order().ToArray();
if (unknownExclusions.Length != 0)
{
    Console.Error.WriteLine($"Excluded upstream source files are missing: {string.Join(", ", unknownExclusions)}");
    return 1;
}

string[] unknownMethodExclusions = methodExclusions.Keys
    .Except(discovered, StringComparer.Ordinal)
    .Order()
    .ToArray();
if (unknownMethodExclusions.Length != 0)
{
    Console.Error.WriteLine($"Excluded upstream methods are missing: {string.Join(", ", unknownMethodExclusions)}");
    return 1;
}

int total = tests.Length;
int passed = tests.Count(test => IsResult(test, "Pass"));
int failed = tests.Count(test => IsResult(test, "Fail"));
int skipped = tests.Count(test => IsResult(test, "Skip"));
string[] summary =
[
    $"source-files|{sourceFiles.Length}",
    $"included-source-files|{sourceFiles.Length - exclusions.Count}",
    $"excluded-source-files|{exclusions.Count}",
    $"discovered-methods|{discoveredMethods}",
    $"excluded-methods|{methodExclusions.Count}",
    $"expanded-cases|{total}",
    $"passed|{passed}",
    $"failed|{failed}",
    $"skipped|{skipped}"
];

WriteLines(Path.Combine(artifactsPath, "candidate-failures.txt"), failures);
WriteLines(Path.Combine(artifactsPath, "candidate-skips.txt"), skips);
WriteLines(Path.Combine(artifactsPath, "candidate-summary.txt"), summary);

var evidence = new
{
    generatedUtc = DateTimeOffset.UtcNow,
    platform = Require(options, "platform"),
    winFormsCommit = Require(options, "winforms-commit"),
    runtimeAssetsCommit = Require(options, "runtime-assets-commit"),
    sourceFiles = sourceFiles.Length,
    includedSourceFiles = sourceFiles.Length - exclusions.Count,
    excludedSourceFiles = exclusions.Count,
    discoveredMethods,
    excludedMethods = methodExclusions.Count,
    expandedCases = total,
    passed,
    failed,
    skipped,
    failureGroups = failures.Length,
    skipGroups = skips.Length
};
File.WriteAllText(
    Path.Combine(artifactsPath, "summary.json"),
    JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

bool valid = true;
valid &= VerifyInventory("failure", knownFailuresPath, failures, updateBaseline);
valid &= VerifyInventory("skip", knownSkipsPath, skips, updateBaseline);
valid &= VerifyInventory("summary", knownSummaryPath, summary, updateBaseline);

Console.WriteLine(
    $"Official System.Drawing corpus: {passed} passed, {failed} failed, {skipped} skipped " +
    $"across {total} expanded cases ({discoveredMethods} discovered methods; " +
    $"{sourceFiles.Length - exclusions.Count}/{sourceFiles.Length} source files enabled)." );
return valid ? 0 : 1;

static string[] BuildOutcomeInventory(
    IEnumerable<XElement> tests,
    string result,
    Func<XElement, string> detailSelector) => tests
    .Where(test => IsResult(test, result))
    .Select(test => $"{test.Attribute("type")?.Value}.{test.Attribute("method")?.Value}|{detailSelector(test)}")
    .GroupBy(value => value, StringComparer.Ordinal)
    .Select(group => $"{group.Count()}|{group.Key}")
    .Order(StringComparer.Ordinal)
    .ToArray();

static bool IsResult(XElement test, string expected) =>
    string.Equals(test.Attribute("result")?.Value, expected, StringComparison.Ordinal);

static bool VerifyInventory(string name, string baselinePath, string[] candidate, bool update)
{
    if (update)
    {
        WriteLines(baselinePath, candidate);
        Console.WriteLine($"Updated {name} baseline: {baselinePath}");
        return true;
    }

    if (!File.Exists(baselinePath))
    {
        Console.Error.WriteLine($"Missing {name} baseline: {baselinePath}");
        return false;
    }

    string[] expected = ReadInventory(baselinePath);
    string[] added = candidate.Except(expected, StringComparer.Ordinal).ToArray();
    string[] removed = expected.Except(candidate, StringComparer.Ordinal).ToArray();
    if (added.Length == 0 && removed.Length == 0)
    {
        return true;
    }

    Console.Error.WriteLine($"Official corpus {name} inventory changed.");
    foreach (string value in added)
    {
        Console.Error.WriteLine($"+ {value}");
    }

    foreach (string value in removed)
    {
        Console.Error.WriteLine($"- {value}");
    }

    return false;
}

static Dictionary<string, string> ReadReasonManifest(string path)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (string line in ReadInventory(path))
    {
        int separator = line.IndexOf('|');
        if (separator <= 0 || separator == line.Length - 1)
        {
            throw new InvalidDataException($"Expected '<key>|<reason>' in {path}: {line}");
        }

        if (!result.TryAdd(line[..separator], line[(separator + 1)..]))
        {
            throw new InvalidDataException($"Duplicate key in {path}: {line[..separator]}");
        }
    }

    return result;
}

static string[] ReadInventory(string path) => File.ReadLines(path)
    .Select(static line => line.Trim())
    .Where(static line => line.Length != 0 && !line.StartsWith('#'))
    .Order(StringComparer.Ordinal)
    .ToArray();

static void WriteLines(string path, IEnumerable<string> lines)
{
    string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
    if (directory is not null)
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
}

static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

static string Require(IReadOnlyDictionary<string, string> options, string key) =>
    options.TryGetValue(key, out string? value) && value.Length != 0
        ? value
        : throw new ArgumentException($"Missing --{key} <value>.");

static Dictionary<string, string> ParseOptions(string[] arguments)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int index = 0; index < arguments.Length; index++)
    {
        string argument = arguments[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument: {argument}");
        }

        string key = argument[2..];
        if (key == "update-baseline")
        {
            result[key] = "true";
            continue;
        }

        if (++index >= arguments.Length)
        {
            throw new ArgumentException($"Missing value for {argument}.");
        }

        result[key] = arguments[index];
    }

    return result;
}
