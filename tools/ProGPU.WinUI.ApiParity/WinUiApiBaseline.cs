internal sealed record WinUiApiBaseline
{
    public int SchemaVersion { get; init; }

    public required BaselinePackage[] Packages { get; init; }

    public required string[] NamespacePrefixes { get; init; }

    public required RegressionBudget RegressionBudget { get; init; }
}

internal sealed record BaselinePackage
{
    public required string PackageId { get; init; }

    public required string PackageVersion { get; init; }

    public required string PackageUri { get; init; }

    public required string PackageSha512 { get; init; }

    public required BaselineAsset[] Assets { get; init; }
}

internal sealed record BaselineAsset
{
    public required string PackagePath { get; init; }

    public required string OutputName { get; init; }

    public required string Role { get; init; }
}

internal sealed record RegressionBudget
{
    public int MaximumMissingEntries { get; init; }

    public int MinimumMatchingEntries { get; init; }
}
