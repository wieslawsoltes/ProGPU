using System.Text.Json;
using System.Text.Json.Nodes;
using HintedDisplayPolicyProbe;
using Xunit;

public sealed class ReferenceInputTests
{
    private static JsonObject Original()
    {
        string path = Environment.GetEnvironmentVariable("PROGPU_WPF_DISPLAY_REFERENCE")
            ?? throw new InvalidOperationException("Point PROGPU_WPF_DISPLAY_REFERENCE at an unchanged successful Microsoft receipt.");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static void Validate(JsonObject node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        ReferenceInput.Validate(document.RootElement);
    }

    [Fact]
    public void OriginalReceiptIsAcceptedWithoutNativeExecution() => Validate(Original());

    [Theory]
    [InlineData("commit")] [InlineData("font")] [InlineData("assembly")]
    [InlineData("duplicate")] [InlineData("dpi")] [InlineData("glyph")]
    [InlineData("offset")] [InlineData("utf16")] [InlineData("cluster")]
    public void ChangedSourceInputsAreRejected(string mutation)
    {
        var original = Original();
        var cases = original["Cases"]!.AsArray();
        var item = cases[0]!;
        var run = item["Lines"]![0]!["Runs"]![0]!;
        switch (mutation)
        {
            case "commit": original["SourceCommit"] = new string('0', 40); break;
            case "font": original["Font"]!["Sha256"] = new string('0', 64); break;
            case "assembly": original["PresentationIdentity"] = "Portable PresentationCore"; break;
            case "duplicate": cases[1] = item.DeepClone(); break;
            case "dpi": run["PixelsPerDip"] = 3; break;
            case "glyph": run["GlyphIds"]![0] = 65536; break;
            case "offset": run["Offsets"]![0] = new JsonArray(1); break;
            case "utf16": run["Utf16"]![0] = 0; break;
            case "cluster": run["Clusters"]![0] = 65535; break;
        }
        Assert.Throws<InvalidDataException>(() => Validate(original));
    }

    [Theory]
    [InlineData(768, 1.25, 9.6)] [InlineData(-768, 1.25, -9.6)]
    [InlineData(320, 1.5, 3.3333333333333335)] [InlineData(1, 2, 0.0078125)]
    public void DiagnosticDivisionPreservesRawSignedFixedPoint(long value, double dpi, double expected)
        => Assert.Equal(expected, ReferenceInput.DeviceToDip(value, dpi));

    [Fact]
    public void DiagnosticDivisionDoesNotMasqueradeFloatProjectionAsSourceDouble()
    {
        Assert.Equal(9.6, ReferenceInput.DeviceToDip(768, 1.25));
        Assert.NotEqual((double)(768 * (0.8f / 64)), ReferenceInput.DeviceToDip(768, 1.25));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidDpiIsNotReplacedWithDefault(double dpi)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ReferenceInput.DeviceToDip(768, dpi));
}
