using System.Text.RegularExpressions;

namespace ProGPU.DirectX;

internal static class ProGpuDirectXFrontFacingEmulation
{
    private static readonly Regex s_frontFacingDeclarationRegex = new(
        @"@builtin\(front_facing\)\s+(?<name>[A-Za-z_]\w*)\s*:\s*bool\s*,?",
        RegexOptions.Compiled);

    private static readonly Regex s_emptyStructRegex = new(
        @"struct\s+(?<name>[A-Za-z_]\w*)\s*\{\s*\}\s*",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static bool TryCreateOverrideSource(string source, bool isFrontFacing, out string overrideSource)
    {
        overrideSource = string.Empty;
        var matches = s_frontFacingDeclarationRegex.Matches(source);
        if (matches.Count == 0)
        {
            return false;
        }

        var names = matches
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var constant = isFrontFacing ? "true" : "false";
        overrideSource = s_frontFacingDeclarationRegex.Replace(source, string.Empty);
        overrideSource = RemoveEmptyStructParameters(overrideSource);

        foreach (var name in names)
        {
            overrideSource = Regex.Replace(
                overrideSource,
                $@"\b[A-Za-z_]\w*\.{Regex.Escape(name)}\b",
                constant);
            overrideSource = Regex.Replace(
                overrideSource,
                $@"\b{Regex.Escape(name)}\b",
                constant);
        }

        overrideSource = Regex.Replace(overrideSource, @"\(\s*,", "(");
        overrideSource = Regex.Replace(overrideSource, @",\s*,", ",");
        overrideSource = Regex.Replace(overrideSource, @",\s*\)", ")");
        return !overrideSource.Contains("@builtin(front_facing)", StringComparison.Ordinal);
    }

    private static string RemoveEmptyStructParameters(string source)
    {
        var emptyStructNames = s_emptyStructRegex
            .Matches(source)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (emptyStructNames.Length == 0)
        {
            return source;
        }

        var result = s_emptyStructRegex.Replace(source, string.Empty);
        foreach (var structName in emptyStructNames)
        {
            result = Regex.Replace(
                result,
                $@"(?<prefix>,\s*)?[A-Za-z_]\w*\s*:\s*{Regex.Escape(structName)}\s*(?<suffix>,\s*)?",
                match => match.Groups["prefix"].Success && match.Groups["suffix"].Success ? ", " : string.Empty);
        }

        return result;
    }
}
