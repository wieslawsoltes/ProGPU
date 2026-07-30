using System.Text;

internal sealed record ApiParityReport
{
    public int SchemaVersion { get; init; } = 1;

    public required IReadOnlyList<OfficialPackageIdentity> OfficialPackages { get; init; }

    public required string ReferenceAssembly { get; init; }

    public required string ReferenceAssemblySha256 { get; init; }

    public required string CandidateAssembly { get; init; }

    public required string CandidateAssemblySha256 { get; init; }

    public int ReferenceEntryCount { get; init; }

    public int CandidateEntryCount { get; init; }

    public int MatchingEntryCount { get; init; }

    public required IReadOnlyList<string> MissingEntries { get; init; }

    public required IReadOnlyList<string> ExtraEntries { get; init; }

    public required IReadOnlyList<ApiParityBreakdown> KindBreakdown { get; init; }

    public required IReadOnlyList<ApiParityBreakdown> AreaBreakdown { get; init; }

    public static ApiParityReport Create(
        WinUiApiBaseline baseline,
        MetadataApiSurface reference,
        MetadataApiSurface candidate)
    {
        var referenceEntries = reference.Entries.ToHashSet(StringComparer.Ordinal);
        var candidateEntries = candidate.Entries.ToHashSet(StringComparer.Ordinal);
        var matchingEntries = referenceEntries.Intersect(
            candidateEntries,
            StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var missingEntries = referenceEntries.Except(
            candidateEntries,
            StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extraEntries = candidateEntries.Except(
            referenceEntries,
            StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new ApiParityReport
        {
            OfficialPackages = baseline.Packages.Select(
                package => new OfficialPackageIdentity(
                    package.PackageId,
                    package.PackageVersion,
                    package.PackageSha512)).ToArray(),
            ReferenceAssembly = reference.AssemblyName,
            ReferenceAssemblySha256 = reference.Sha256,
            CandidateAssembly = candidate.AssemblyName,
            CandidateAssemblySha256 = candidate.Sha256,
            ReferenceEntryCount = referenceEntries.Count,
            CandidateEntryCount = candidateEntries.Count,
            MatchingEntryCount = matchingEntries.Count,
            MissingEntries = missingEntries,
            ExtraEntries = extraEntries,
            KindBreakdown = CreateBreakdown(
                referenceEntries,
                candidateEntries,
                matchingEntries,
                GetKind),
            AreaBreakdown = CreateBreakdown(
                referenceEntries,
                candidateEntries,
                matchingEntries,
                GetArea)
        };
    }

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# WinUI API parity report");
        builder.AppendLine();
        builder.AppendLine("Official baseline:");
        builder.AppendLine();
        foreach (var package in OfficialPackages)
        {
            builder.AppendLine(
                $"- `{package.PackageId}` `{package.PackageVersion}`");
        }
        builder.AppendLine();
        builder.AppendLine("| Metric | Count |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine($"| Official API entries | {ReferenceEntryCount} |");
        builder.AppendLine($"| ProGPU API entries | {CandidateEntryCount} |");
        builder.AppendLine($"| Exact matches | {MatchingEntryCount} |");
        builder.AppendLine($"| Missing from ProGPU | {MissingEntries.Count} |");
        builder.AppendLine($"| ProGPU-only entries | {ExtraEntries.Count} |");
        builder.AppendLine();
        AppendBreakdown(builder, "By metadata kind", KindBreakdown);
        AppendBreakdown(builder, "By Microsoft.UI area", AreaBreakdown);
        AppendEntries(builder, "Missing from ProGPU", MissingEntries);
        AppendEntries(builder, "ProGPU-only entries", ExtraEntries);
        return builder.ToString();
    }

    private static IReadOnlyList<ApiParityBreakdown> CreateBreakdown(
        IReadOnlySet<string> referenceEntries,
        IReadOnlySet<string> candidateEntries,
        IReadOnlySet<string> matchingEntries,
        Func<string, string> classifier)
    {
        var keys = referenceEntries
            .Concat(candidateEntries)
            .Select(classifier)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return keys.Select(
            key => new ApiParityBreakdown(
                key,
                referenceEntries.Count(entry => classifier(entry) == key),
                candidateEntries.Count(entry => classifier(entry) == key),
                matchingEntries.Count(entry => classifier(entry) == key),
                referenceEntries.Count(
                    entry => classifier(entry) == key &&
                        !candidateEntries.Contains(entry)),
                candidateEntries.Count(
                    entry => classifier(entry) == key &&
                        !referenceEntries.Contains(entry))))
            .ToArray();
    }

    private static string GetKind(string entry)
    {
        var separator = entry.IndexOf('|');
        return separator < 0 ? "unknown" : entry[..separator];
    }

    private static string GetArea(string entry)
    {
        var firstSeparator = entry.IndexOf('|');
        if (firstSeparator < 0)
            return "unknown";
        var secondSeparator = entry.IndexOf('|', firstSeparator + 1);
        var owner = secondSeparator < 0
            ? entry[(firstSeparator + 1)..]
            : entry[(firstSeparator + 1)..secondSeparator];
        const string prefix = "Microsoft.UI.";
        if (!owner.StartsWith(prefix, StringComparison.Ordinal))
            return "other";
        var nextSeparator = owner.IndexOf('.', prefix.Length);
        if (nextSeparator < 0)
            return "Microsoft.UI";
        var firstSegment = owner[prefix.Length..nextSeparator];
        return firstSegment is
            "Composition" or
            "Content" or
            "Dispatching" or
            "Input" or
            "System" or
            "Text" or
            "Windowing" or
            "Xaml"
            ? owner[..nextSeparator]
            : "Microsoft.UI";
    }

    private static void AppendBreakdown(
        StringBuilder builder,
        string title,
        IReadOnlyList<ApiParityBreakdown> breakdown)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine(
            "| Category | Official | ProGPU | Matching | Missing | ProGPU-only |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var item in breakdown)
        {
            builder.AppendLine(
                $"| `{item.Category}` | {item.Reference} | {item.Candidate} | " +
                $"{item.Matching} | {item.Missing} | {item.Extra} |");
        }
        builder.AppendLine();
    }

    private static void AppendEntries(
        StringBuilder builder,
        string title,
        IReadOnlyList<string> entries)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        if (entries.Count == 0)
        {
            builder.AppendLine("None.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("```text");
        foreach (var entry in entries)
            builder.AppendLine(entry);
        builder.AppendLine("```");
        builder.AppendLine();
    }
}

internal sealed record ApiParityBreakdown(
    string Category,
    int Reference,
    int Candidate,
    int Matching,
    int Missing,
    int Extra);

internal sealed record OfficialPackageIdentity(
    string PackageId,
    string PackageVersion,
    string PackageSha512);
