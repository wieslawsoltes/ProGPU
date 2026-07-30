using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

internal static class NuGetMetadataAcquirer
{
    private static readonly HttpClient HttpClient = new();

    public static async Task AcquireAsync(
        WinUiApiBaseline baseline,
        string outputDirectory)
    {
        Validate(baseline);

        var outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);
        var packageDirectory = Path.Combine(outputRoot, "packages");
        Directory.CreateDirectory(packageDirectory);
        var packages = new List<AcquiredPackage>(baseline.Packages.Length);
        foreach (var package in baseline.Packages)
        {
            var packagePath = Path.Combine(
                packageDirectory,
                $"{package.PackageId}.{package.PackageVersion}.nupkg");
            if (!File.Exists(packagePath) ||
                !await HasExpectedHashAsync(
                    packagePath,
                    package.PackageSha512))
            {
                var packageBytes = await HttpClient.GetByteArrayAsync(
                    package.PackageUri);
                VerifyHash(packageBytes, package.PackageSha512);
                await File.WriteAllBytesAsync(packagePath, packageBytes);
            }

            await using var stream = File.OpenRead(packagePath);
            using var archive = new ZipArchive(
                stream,
                ZipArchiveMode.Read,
                leaveOpen: false);
            var assets = new List<AcquiredAsset>(package.Assets.Length);
            foreach (var asset in package.Assets)
            {
                var entry = archive.GetEntry(asset.PackagePath) ??
                    throw new InvalidDataException(
                        $"Locked NuGet asset is missing: {asset.PackagePath}");
                var destination = Path.Combine(outputRoot, asset.OutputName);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination) ?? outputRoot);

                await using (var source = entry.Open())
                await using (var target = File.Create(destination))
                    await source.CopyToAsync(target);

                assets.Add(new AcquiredAsset(
                    asset.PackagePath,
                    asset.OutputName,
                    asset.Role,
                    await ComputeSha256Async(destination)));
            }

            packages.Add(new AcquiredPackage(
                package.PackageId,
                package.PackageVersion,
                package.PackageUri,
                package.PackageSha512,
                assets));
        }

        VerifyComponentDependencyGraph(outputRoot, baseline);
        var provenance = new AcquisitionProvenance(packages);
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "provenance.json"),
            JsonSerializer.Serialize(
                provenance,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }) + "\n");

        Console.WriteLine(
            $"Verified {packages.Count} official NuGet packages and extracted " +
            $"{packages.Sum(package => package.Assets.Count)} public metadata assets.");
    }

    private static void Validate(WinUiApiBaseline baseline)
    {
        if (baseline.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported baseline schema.");
        if (baseline.Packages.Length == 0)
            throw new InvalidDataException("No official packages are locked.");

        var outputNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in baseline.Packages)
        {
            if (!Uri.TryCreate(
                    package.PackageUri,
                    UriKind.Absolute,
                    out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(
                    uri.Host,
                    "api.nuget.org",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Every official package URI must use HTTPS on api.nuget.org.");
            }

            if (package.Assets.Length == 0)
            {
                throw new InvalidDataException(
                    $"No public metadata assets are locked for {package.PackageId}.");
            }

            foreach (var asset in package.Assets)
            {
                if (Path.IsPathRooted(asset.PackagePath) ||
                    Path.IsPathRooted(asset.OutputName) ||
                    asset.PackagePath.Contains("..", StringComparison.Ordinal) ||
                    asset.OutputName.Contains("..", StringComparison.Ordinal) ||
                    !outputNames.Add(asset.OutputName))
                {
                    throw new InvalidDataException(
                        $"Unsafe or duplicate locked asset: {asset.OutputName}");
                }
            }
        }
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        string expectedBase64)
    {
        await using var stream = File.OpenRead(path);
        var actual = await SHA512.HashDataAsync(stream);
        return CryptographicOperations.FixedTimeEquals(
            actual,
            Convert.FromBase64String(expectedBase64));
    }

    private static void VerifyComponentDependencyGraph(
        string outputRoot,
        WinUiApiBaseline baseline)
    {
        var umbrella = baseline.Packages.SingleOrDefault(
            package => string.Equals(
                package.PackageId,
                "Microsoft.WindowsAppSDK",
                StringComparison.OrdinalIgnoreCase));
        if (umbrella is null)
            throw new InvalidDataException(
                "The Microsoft.WindowsAppSDK umbrella package is not locked.");

        var nuspecAsset = umbrella.Assets.SingleOrDefault(
            asset => asset.PackagePath.EndsWith(
                ".nuspec",
                StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidDataException(
                "The official umbrella package nuspec is not locked.");
        var document = XDocument.Load(
            Path.Combine(outputRoot, nuspecAsset.OutputName),
            LoadOptions.None);
        var dependencies = document.Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .Select(
                element => new
                {
                    Id = (string?)element.Attribute("id"),
                    Version = (string?)element.Attribute("version")
                })
            .Where(item => item.Id is not null && item.Version is not null)
            .ToDictionary(
                item => item.Id!,
                item => item.Version!,
                StringComparer.OrdinalIgnoreCase);

        foreach (var package in baseline.Packages)
        {
            if (ReferenceEquals(package, umbrella))
                continue;
            if (!dependencies.TryGetValue(
                    package.PackageId,
                    out var dependencyVersion) ||
                !string.Equals(
                    dependencyVersion.Trim('[', ']', '(', ')'),
                    package.PackageVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The umbrella dependency for {package.PackageId} does not " +
                    $"match locked version {package.PackageVersion}.");
            }
        }
    }

    private static void VerifyHash(
        ReadOnlySpan<byte> bytes,
        string expectedBase64)
    {
        Span<byte> actual = stackalloc byte[SHA512.HashSizeInBytes];
        SHA512.HashData(bytes, actual);
        if (!CryptographicOperations.FixedTimeEquals(
                actual,
                Convert.FromBase64String(expectedBase64)))
        {
            throw new InvalidDataException(
                "Official NuGet package SHA-512 verification failed.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream))
            .ToLowerInvariant();
    }

    private sealed record AcquiredAsset(
        string PackagePath,
        string OutputName,
        string Role,
        string Sha256);

    private sealed record AcquiredPackage(
        string PackageId,
        string PackageVersion,
        string PackageUri,
        string PackageSha512,
        IReadOnlyList<AcquiredAsset> Assets);

    private sealed record AcquisitionProvenance(
        IReadOnlyList<AcquiredPackage> Packages);
}
