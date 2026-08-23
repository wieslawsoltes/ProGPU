using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

const string SkiaToken = "0738eb9f132ed756";
const string AvaloniaToken = "c8d484a7012f9a8b";
var skiaCeiling = new Version(4, 151, 0, 0);
var avaloniaCeiling = new Version(12, 1, 1, 0);

if (args.Length != 2)
{
    throw new ArgumentException(
        "Usage: ProGpuBinaryCompatibilityHost " +
        "<SkiaSharp|Avalonia.Skia> <consumer.dll>.");
}

var contractKind = args[0];
var consumerPath = Path.GetFullPath(args[1]);
var referenceToken = contractKind switch
{
    "SkiaSharp" => SkiaToken,
    "Avalonia.Skia" => AvaloniaToken,
    _ => throw new ArgumentException($"Unknown contract kind '{contractKind}'.")
};
Version referenceVersion;

using (var stream = File.OpenRead(consumerPath))
using (var peReader = new PEReader(stream))
{
    referenceVersion = ReadAssemblyReference(
        peReader.GetMetadataReader(),
        contractKind,
        referenceToken);
}

var supportedReference = contractKind == "SkiaSharp"
    ? referenceVersion.Major is 2 or 3 or 4 &&
      referenceVersion <= skiaCeiling
    : referenceVersion.Major is 11 or 12 &&
      referenceVersion <= avaloniaCeiling;
if (!supportedReference)
{
    throw new InvalidOperationException(
        $"{contractKind} {referenceVersion} is outside the supported range.");
}

if (contractKind == "Avalonia.Skia")
{
    var facade = Assembly.Load(
        "Avalonia.Skia, Version=12.1.1.0, Culture=neutral, " +
        $"PublicKeyToken={AvaloniaToken}");
    AssertIdentity(
        facade.GetName(),
        "Avalonia.Skia",
        avaloniaCeiling,
        AvaloniaToken);

    var forwardedNames = facade
        .GetForwardedTypes()
        .Select(static type => type.FullName)
        .Order(StringComparer.Ordinal)
        .ToArray();
    var expectedForwardedNames = new[]
    {
        "Avalonia.Skia.ISkiaSharpApiLease",
        "Avalonia.Skia.ISkiaSharpApiLeaseFeature",
        "Avalonia.Skia.ISkiaSharpPlatformGraphicsApiLease"
    };
    if (!forwardedNames.SequenceEqual(
            expectedForwardedNames,
            StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            "Unexpected Avalonia.Skia forwarding contract: " +
            string.Join(", ", forwardedNames));
    }
}

var consumer = AssemblyLoadContext.Default.LoadFromAssemblyPath(consumerPath);
var consumerTypeName = contractKind == "SkiaSharp"
    ? "OfficialBinaryCompatibilityConsumer.SkiaSharpCompatibilityConsumer"
    : "OfficialBinaryCompatibilityConsumer.AvaloniaSkiaCompatibilityConsumer";
var probe = consumer
    .GetType(consumerTypeName, throwOnError: true)!
    .GetMethod("Probe", BindingFlags.Public | BindingFlags.Static)!;
var result = (string?)probe.Invoke(null, null);
var expected = contractKind == "SkiaSharp"
    ? $"SkiaSharp|{SkiaToken}|True"
    : "Avalonia.ProGpu|True";
if (!string.Equals(result, expected, StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        $"Compatibility probe returned '{result}', expected '{expected}'.");
}

if (contractKind == "SkiaSharp")
{
    var skia = Assembly.Load(
        "SkiaSharp, Version=4.151.0.0, Culture=neutral, " +
        $"PublicKeyToken={SkiaToken}");
    AssertIdentity(
        skia.GetName(),
        "SkiaSharp",
        skiaCeiling,
        SkiaToken);
}

Console.WriteLine(
    $"Unmodified {contractKind} {referenceVersion} consumer executed " +
    "against the universal ProGPU compatibility identity.");
return;

static Version ReadAssemblyReference(
    MetadataReader metadata,
    string expectedName,
    string expectedToken)
{
    foreach (var handle in metadata.AssemblyReferences)
    {
        var reference = metadata.GetAssemblyReference(handle);
        if (!string.Equals(
                metadata.GetString(reference.Name),
                expectedName,
                StringComparison.Ordinal))
        {
            continue;
        }

        var token = Convert.ToHexString(
            metadata.GetBlobBytes(reference.PublicKeyOrToken))
            .ToLowerInvariant();
        if (token != expectedToken)
        {
            throw new InvalidOperationException(
                $"{expectedName} AssemblyRef is {reference.Version}/{token}, " +
                $"expected token {expectedToken}.");
        }

        return reference.Version;
    }

    throw new InvalidOperationException(
        $"Consumer has no {expectedName} AssemblyRef.");
}

static void AssertIdentity(
    AssemblyName identity,
    string expectedName,
    Version expectedVersion,
    string expectedToken)
{
    var token = Convert.ToHexString(identity.GetPublicKeyToken() ?? [])
        .ToLowerInvariant();
    if (identity.Name != expectedName ||
        identity.Version != expectedVersion ||
        token != expectedToken)
    {
        throw new InvalidOperationException(
            $"Loaded {identity.FullName}, expected " +
            $"{expectedName}, Version={expectedVersion}, " +
            $"PublicKeyToken={expectedToken}.");
    }
}
