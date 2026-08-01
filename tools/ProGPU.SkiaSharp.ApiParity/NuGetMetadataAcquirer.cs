using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class NuGetMetadataAcquirer
{
    private static readonly HttpClient HttpClient = new();

    public static async Task AcquireAsync(
        SkiaSharpApiBaseline baseline,
        string outputDirectory)
    {
        Validate(baseline);

        var outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);
        var packageDirectory = Path.Combine(outputRoot, "packages");
        Directory.CreateDirectory(packageDirectory);
        var package = baseline.Package;
        var packagePath = Path.Combine(
            packageDirectory,
            $"{package.PackageId}.{package.PackageVersion}.nupkg");
        if (!File.Exists(packagePath) ||
            !await HasExpectedHashAsync(packagePath, package.PackageSha512))
        {
            var packageBytes = await HttpClient.GetByteArrayAsync(package.PackageUri);
            VerifyHash(packageBytes, package.PackageSha512);
            await File.WriteAllBytesAsync(packagePath, packageBytes);
        }

        await using var stream = File.OpenRead(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(package.Asset.PackagePath) ??
            throw new InvalidDataException(
                $"Locked NuGet asset is missing: {package.Asset.PackagePath}");
        var destination = Path.Combine(outputRoot, package.Asset.OutputName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? outputRoot);
        await using (var source = entry.Open())
        await using (var target = File.Create(destination))
            await source.CopyToAsync(target);

        var provenance = new AcquisitionProvenance(
            package.PackageId,
            package.PackageVersion,
            package.PackageUri,
            package.PackageSha512,
            package.Asset.PackagePath,
            package.Asset.OutputName,
            package.Asset.Role,
            await ComputeSha256Async(destination));
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
            $"Verified official {package.PackageId} {package.PackageVersion} " +
            $"and extracted {package.Asset.PackagePath}.");
    }

    private static void Validate(SkiaSharpApiBaseline baseline)
    {
        if (baseline.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported baseline schema.");
        if (baseline.NamespacePrefixes.Length == 0)
            throw new InvalidDataException("No namespace prefixes are locked.");

        var package = baseline.Package;
        if (!Uri.TryCreate(package.PackageUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(
                uri.Host,
                "api.nuget.org",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The official package URI must use HTTPS on api.nuget.org.");
        }

        if (!IsSafeRelativePath(package.Asset.PackagePath) ||
            !IsSafeRelativePath(package.Asset.OutputName))
        {
            throw new InvalidDataException("The locked asset path is unsafe.");
        }
    }

    private static bool IsSafeRelativePath(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !Path.IsPathRooted(value) &&
        !value.Contains("..", StringComparison.Ordinal);

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

    private sealed record AcquisitionProvenance(
        string PackageId,
        string PackageVersion,
        string PackageUri,
        string PackageSha512,
        string PackagePath,
        string OutputName,
        string Role,
        string Sha256);
}
