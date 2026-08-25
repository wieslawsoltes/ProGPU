using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Avalonia;
using SkiaSharp;
using Svg.Skia;
using WebScene.Backends.Avalonia.Native;

namespace ProGpuEcosystemCompatibility;

internal static class Program
{
    private const string SkiaToken = "0738eb9f132ed756";
    private const string AvaloniaToken = "c8d484a7012f9a8b";
    private const string Markup = """
        <svg xmlns="http://www.w3.org/2000/svg"
             width="64"
             height="64"
             viewBox="0 0 64 64">
          <defs>
            <linearGradient id="fill" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stop-color="#2563eb" />
              <stop offset="1" stop-color="#7c3aed" />
            </linearGradient>
          </defs>
          <rect x="2" y="2" width="60" height="60" rx="12"
                fill="url(#fill)" />
          <path d="M16 34 L27 45 L49 20"
                fill="none"
                stroke="#ffffff"
                stroke-width="6" />
        </svg>
        """;

    public static async Task Main()
    {
        EcosystemApplication.Build().SetupWithoutStarting();

        AssertIdentity(
            typeof(SKCanvas).Assembly.GetName(),
            "SkiaSharp",
            new Version(4, 151, 0, 0),
            SkiaToken);

        var facade = Assembly.Load(
            "Avalonia.Skia, Version=12.1.1.0, Culture=neutral, " +
            $"PublicKeyToken={AvaloniaToken}");
        AssertIdentity(
            facade.GetName(),
            "Avalonia.Skia",
            new Version(12, 1, 1, 0),
            AvaloniaToken);
        var forwardedLease = facade.GetForwardedTypes().Single(
            static type =>
                type.FullName ==
                "Avalonia.Skia.ISkiaSharpApiLeaseFeature");
        if (forwardedLease.Assembly.GetName().Name != "Avalonia.ProGpu")
        {
            throw new InvalidOperationException(
                "The Avalonia.Skia lease contract did not forward to ProGPU.");
        }

        using (var svg = SKSvg.CreateFromSvg(Markup))
        {
            if (svg.Picture is not { } picture ||
                picture.CullRect.Width != 64f ||
                picture.CullRect.Height != 64f)
            {
                throw new InvalidOperationException(
                    "Svg.Skia 5.2.2 did not record the SVG picture.");
            }
        }

        var view = new EcosystemView();
        view.LoadAndVerify(Markup);
        view.Measure(new Size(128d, 128d));
        view.Arrange(new Rect(0d, 0d, 128d, 128d));
        await view.DisposeWebSceneAsync();

        AssertReference(
            typeof(SKSvg).Assembly,
            "SkiaSharp",
            SkiaToken,
            expectedMajor: 4);
        AssertReference(
            typeof(Avalonia.Svg.Skia.Svg).Assembly,
            "SkiaSharp",
            SkiaToken,
            expectedMajor: 4);
        AssertReference(
            typeof(NativeSceneSurface).Assembly,
            "SkiaSharp",
            SkiaToken,
            expectedMajor: 2);
        AssertReference(
            typeof(NativeSceneSurface).Assembly,
            "Avalonia.Skia",
            AvaloniaToken,
            expectedMajor: 11);

        Console.WriteLine(
            "Latest Svg.Skia, SVG controls, and WebScene packages " +
            "executed against the ProGPU compatibility identities.");
    }

    private static void AssertReference(
        Assembly assembly,
        string expectedName,
        string expectedToken,
        int expectedMajor)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            if (metadata.GetString(reference.Name) != expectedName)
            {
                continue;
            }

            var token = Convert.ToHexString(
                    metadata.GetBlobBytes(reference.PublicKeyOrToken))
                .ToLowerInvariant();
            if (reference.Version.Major != expectedMajor ||
                token != expectedToken)
            {
                throw new InvalidOperationException(
                    $"{assembly.GetName().Name} references " +
                    $"{expectedName} {reference.Version}/{token}; expected " +
                    $"major {expectedMajor} and token {expectedToken}.");
            }

            return;
        }

        throw new InvalidOperationException(
            $"{assembly.GetName().Name} has no {expectedName} AssemblyRef.");
    }

    private static void AssertIdentity(
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
}
