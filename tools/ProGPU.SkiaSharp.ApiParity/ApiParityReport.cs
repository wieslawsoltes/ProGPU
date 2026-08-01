using System.Text;

internal sealed record ApiParityReport
{
    public int SchemaVersion { get; init; } = 1;

    public required string OfficialPackageId { get; init; }

    public required string OfficialPackageVersion { get; init; }

    public required string OfficialPackageSha512 { get; init; }

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

    public static ApiParityReport Create(
        SkiaSharpApiBaseline baseline,
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
            OfficialPackageId = baseline.Package.PackageId,
            OfficialPackageVersion = baseline.Package.PackageVersion,
            OfficialPackageSha512 = baseline.Package.PackageSha512,
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
                matchingEntries)
        };
    }

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SkiaSharp API parity report");
        builder.AppendLine();
        builder.AppendLine(
            $"Official baseline: `{OfficialPackageId}` " +
            $"`{OfficialPackageVersion}`.");
        builder.AppendLine();
        builder.AppendLine("| Metric | Count |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine($"| Official API entries | {ReferenceEntryCount} |");
        builder.AppendLine($"| ProGPU API entries | {CandidateEntryCount} |");
        builder.AppendLine($"| Exact matches | {MatchingEntryCount} |");
        builder.AppendLine($"| Missing from ProGPU | {MissingEntries.Count} |");
        builder.AppendLine($"| ProGPU-only entries | {ExtraEntries.Count} |");
        builder.AppendLine();
        builder.AppendLine("## By metadata kind");
        builder.AppendLine();
        builder.AppendLine(
            "| Kind | Official | ProGPU | Matching | Missing | ProGPU-only |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var item in KindBreakdown)
        {
            builder.AppendLine(
                $"| `{item.Category}` | {item.Reference} | {item.Candidate} | " +
                $"{item.Matching} | {item.Missing} | {item.Extra} |");
        }
        builder.AppendLine();
        AppendEntries(builder, "Missing from ProGPU", MissingEntries);
        AppendEntries(builder, "ProGPU-only entries", ExtraEntries);
        return builder.ToString();
    }

    private static IReadOnlyList<ApiParityBreakdown> CreateBreakdown(
        IReadOnlySet<string> referenceEntries,
        IReadOnlySet<string> candidateEntries,
        IReadOnlySet<string> matchingEntries)
    {
        static string GetKind(string entry)
        {
            var separator = entry.IndexOf('|');
            return separator < 0 ? "unknown" : entry[..separator];
        }

        return referenceEntries.Concat(candidateEntries)
            .Select(GetKind)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(
                kind => new ApiParityBreakdown(
                    kind,
                    referenceEntries.Count(entry => GetKind(entry) == kind),
                    candidateEntries.Count(entry => GetKind(entry) == kind),
                    matchingEntries.Count(entry => GetKind(entry) == kind),
                    referenceEntries.Count(
                        entry => GetKind(entry) == kind &&
                            !candidateEntries.Contains(entry)),
                    candidateEntries.Count(
                        entry => GetKind(entry) == kind &&
                            !referenceEntries.Contains(entry))))
            .ToArray();
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
