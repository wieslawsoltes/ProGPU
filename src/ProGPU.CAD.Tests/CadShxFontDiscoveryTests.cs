using System.Text;
using ACadSharp.Tables;
using ProGPU.CAD;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadShxFontDiscoveryTests
{
    [Fact]
    public async Task DiscoveryUsesDrawingThenSupportOrderAndReportsEachOutcome()
    {
        using var files = new TemporaryDirectory();
        string drawing = files.CreateDirectory("drawing");
        string support = files.CreateDirectory("support");
        File.WriteAllBytes(
            Path.Combine(drawing, "ordered.shx"),
            BuildTextShx("DRAWING", 3));
        File.WriteAllBytes(
            Path.Combine(support, "ordered.shx"),
            BuildTextShx("SUPPORT", 7));
        File.WriteAllBytes(
            Path.Combine(support, "support-only.shx"),
            BuildTextShx("SUPPORTONLY", 5));
        File.WriteAllBytes(
            Path.Combine(support, "mapped-target.shx"),
            BuildTextShx("MAPPEDTARGET", 6));
        File.WriteAllBytes(
            Path.Combine(support, "fallback-original.shx"),
            BuildTextShx("FALLBACKORIGINAL", 8));
        File.WriteAllBytes(Path.Combine(drawing, "corrupt.shx"), "not-shx"u8.ToArray());
        File.WriteAllBytes(
            Path.Combine(support, "corrupt.shx"),
            BuildTextShx("SHOULDNOTLOAD", 9));

        CadDocumentSession session = CreateSession(
            "ordered.shx",
            "support-only.shx",
            "existing.shx",
            "mapped.shx",
            "mapped-on-disk.shx",
            "fallback-original.shx",
            "missing.shx",
            "corrupt.shx");
        var catalog = new CadShxFontCatalog();
        catalog.ParseAndRegister("existing.shx", BuildTextShx("EXISTING", 1));
        catalog.ParseAndRegister("replacement.shx", BuildTextShx("REPLACEMENT", 1));
        catalog.ParseAndRegister(
            "mapped-on-disk.shx",
            BuildTextShx("ORIGINALMAPPED", 1));
        catalog.SetMapping("mapped", "replacement.shx");
        catalog.SetMapping("mapped-on-disk", "mapped-target.shx");
        catalog.SetMapping("fallback-original", "absent-target.shx");

        CadShxFontDiscoveryResult result = await CadShxFontDiscovery.DiscoverAsync(
            session,
            catalog,
            new CadShxFontDiscoveryOptions
            {
                DrawingDirectory = drawing,
                SupportDirectories = new[] { support },
            });

        Assert.Equal(8, result.RequestedFontCount);
        Assert.Equal(2, result.AlreadyResolvedFontCount);
        Assert.Equal(1, result.MissingFontCount);
        Assert.Equal(1, result.InvalidFontCount);
        Assert.Equal(
            new[]
            {
                "ordered.shx",
                "support-only.shx",
                "mapped-target.shx",
                "fallback-original.shx",
            },
            result.LoadedFontNames.ToArray());
        Assert.Contains(result.Diagnostics.ToArray(), item => item.Code == "CADSHX001");
        Assert.Contains(result.Diagnostics.ToArray(), item => item.Code == "CADSHX003");

        CadShxFontResolution ordered = catalog.Resolve(
            new CadShxFontRequest("ORDERED", "ordered.shx", string.Empty));
        CadShxGlyphCache orderedCache = Assert.IsType<CadShxGlyphCache>(ordered.GlyphCache);
        Assert.Equal("DRAWING", orderedCache.Font.Name);
        Assert.Equal(3, orderedCache.GetGlyph(65).Advance.X);
        CadShxFontResolution mapped = catalog.Resolve(
            new CadShxFontRequest("MAPPED", "mapped-on-disk.shx", string.Empty));
        Assert.Equal(
            "MAPPEDTARGET",
            Assert.IsType<CadShxGlyphCache>(mapped.GlyphCache).Font.Name);
        CadShxFontResolution fallback = catalog.Resolve(
            new CadShxFontRequest("FALLBACK", "fallback-original.shx", string.Empty));
        Assert.Equal(
            "FALLBACKORIGINAL",
            Assert.IsType<CadShxGlyphCache>(fallback.GlyphCache).Font.Name);
        Assert.False(catalog.ContainsRegisteredName("corrupt.shx"));
    }

    [Fact]
    public async Task DiscoveryNormalizesStyleFilenamesAndBoundsConfiguration()
    {
        using var files = new TemporaryDirectory();
        string drawing = files.CreateDirectory("drawing");
        File.WriteAllBytes(
            Path.Combine(drawing, "nested.shx"),
            BuildTextShx("NESTED", 4));
        CadDocumentSession nested = CreateSession(@"C:\fonts\nested.shx");

        var catalog = new CadShxFontCatalog();
        CadShxFontDiscoveryResult result = await CadShxFontDiscovery.DiscoverAsync(
            nested,
            catalog,
            new CadShxFontDiscoveryOptions
            {
                DrawingDirectory = drawing,
                SupportDirectories = new[] { drawing },
                MaxSearchDirectories = 1,
            });

        Assert.Equal(new[] { "nested.shx" }, result.LoadedFontNames.ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CadShxFontDiscovery.DiscoverAsync(
                nested,
                new CadShxFontCatalog(),
                new CadShxFontDiscoveryOptions { DrawingDirectory = "relative" }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CadShxFontDiscovery.DiscoverAsync(
                CreateSession("one.shx", "two.shx"),
                new CadShxFontCatalog(),
                new CadShxFontDiscoveryOptions { MaxFontRequests = 1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CadShxFontDiscovery.DiscoverAsync(
                nested,
                new CadShxFontCatalog(),
                new CadShxFontDiscoveryOptions
                {
                    ParseOptions = new CadShxParseOptions { MaxFileBytes = 0 },
                }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CadShxFontDiscovery.DiscoverAsync(
                nested,
                new CadShxFontCatalog(),
                new CadShxFontDiscoveryOptions
                {
                    InterpretOptions = new CadShxInterpretOptions
                    {
                        MaxCoordinateMagnitude = double.PositiveInfinity,
                    },
                }));
    }

    [Fact]
    public async Task TotalBytePreflightDoesNotPartiallyRegisterFonts()
    {
        using var files = new TemporaryDirectory();
        string drawing = files.CreateDirectory("drawing");
        byte[] first = BuildTextShx("FIRST", 1);
        byte[] second = BuildTextShx("SECOND", 2);
        File.WriteAllBytes(Path.Combine(drawing, "first.shx"), first);
        File.WriteAllBytes(Path.Combine(drawing, "second.shx"), second);
        var catalog = new CadShxFontCatalog();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CadShxFontDiscovery.DiscoverAsync(
                CreateSession("first.shx", "second.shx"),
                catalog,
                new CadShxFontDiscoveryOptions
                {
                    DrawingDirectory = drawing,
                    MaxTotalFontBytes = Math.Max(first.Length, second.Length),
                }));

        Assert.Equal(0, catalog.RegisteredFontCount);
        Assert.Equal(0, catalog.RegisteredNameCount);
    }

    private static CadDocumentSession CreateSession(params string[] filenames)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add SHX styles", document =>
        {
            for (int i = 0; i < filenames.Length; i++)
            {
                document.TextStyles.Add(new TextStyle($"SHX{i}")
                {
                    Filename = filenames[i],
                });
            }
        });
        return session;
    }

    private static byte[] BuildTextShx(string fontName, byte advance)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write((ushort)0);
        writer.Write((ushort)65);
        writer.Write((ushort)2);
        writer.Write((ushort)0);
        writer.Write(checked((ushort)(fontName.Length + 5)));
        writer.Write((ushort)65);
        writer.Write((ushort)7);
        writer.Write(Encoding.ASCII.GetBytes(fontName));
        writer.Write((byte)0);
        writer.Write(new byte[] { 10, 2, 0, 0 });
        writer.Write("A"u8);
        writer.Write((byte)0);
        writer.Write(new byte[] { 2, 8, advance, 0, 0 });
        writer.Write("EOF"u8);
        return stream.ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"progpu-cad-shx-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_path);

        public string CreateDirectory(string name)
        {
            string path = Path.Combine(_path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
