using System.Text.Json;
using System.Text.Json.Nodes;
using HintedDisplayPolicyProbe;
using SourceDisplayPolicyProbe;
using Xunit;

public sealed class SourceDisplayInputTests
{
    private static JsonObject Root() => new()
    {
        ["PresentationIdentity"] = "Microsoft, PublicKeyToken=31bf3856ad364e35",
        ["PresentationCore"] = new JsonObject { ["Sha256"] = "original-assembly" }
    };
    private static JsonObject Source() => new()
    {
        ["Em"] = 13.0, ["Dpi"] = 1.5, ["Mode"] = "Display",
        ["IndependentSourceMetrics"] = new JsonObject
        {
            ["Version"] = 1, ["Mode"] = "Display", ["Em"] = 13.0, ["Dpi"] = 1.5, ["ToReal"] = 1.0,
            ["Baseline"] = 12.0 + 2.0 / 3, ["LineSpacing"] = 16.0,
            ["Font"] = new JsonObject { ["Sha256"] = ReferenceInput.FontHash },
            ["TypefaceIdentity"] = "Microsoft, PublicKeyToken=31bf3856ad364e35",
            ["TypefaceAssembly"] = new JsonObject { ["Sha256"] = "original-assembly" },
            ["BaselineMethod"] = "System.Windows.Media.Typeface.Baseline(Double,Double,Double,TextFormattingMode):Double",
            ["LineSpacingMethod"] = "System.Windows.Media.Typeface.LineSpacing(Double,Double,Double,TextFormattingMode):Double",
            ["CaptureOrder"] = "Before TextFormatter creation and FormatLine; original source request metrics, not line outputs."
        }
    };

    [Fact]
    public void OriginalDoubleInputsAreNotDerivedFromDesignOrLineOutputs()
    {
        var source = Source();
        source["SourceFont"] = new JsonObject { ["Baseline"] = 999, ["LineSpacing"] = 888 };
        source["Lines"] = new JsonArray(new JsonObject { ["Baseline"] = -12, ["Height"] = -16 });
        var metrics = SourceReferenceInput.GetMetrics(JsonSerializer.SerializeToElement(Root()), JsonSerializer.SerializeToElement(source), ReferenceInput.FontHash);
        Assert.Equal(12.0 + 2.0 / 3, metrics.Baseline); Assert.NotEqual((double)(float)metrics.Baseline, metrics.Baseline);
        Assert.Equal(16, metrics.LineSpacing); Assert.Equal(13, metrics.Em); Assert.Equal(1.5, metrics.Dpi);
    }

    [Fact]
    public void MissingOriginalMetricSeamCannotUseExpectedLineOrDesignValues()
    {
        var source = Source(); source.Remove("IndependentSourceMetrics");
        source["SourceFont"] = new JsonObject { ["Baseline"] = 0.96875, ["LineSpacing"] = 1.2 };
        source["Lines"] = new JsonArray(new JsonObject { ["Baseline"] = 13, ["Height"] = 16 });
        Assert.Throws<InvalidDataException>(() => SourceReferenceInput.GetMetrics(JsonSerializer.SerializeToElement(Root()), JsonSerializer.SerializeToElement(source), ReferenceInput.FontHash));
    }

    [Theory]
    [InlineData("Version")] [InlineData("Mode")] [InlineData("Em")] [InlineData("Dpi")] [InlineData("ToReal")]
    [InlineData("Font")] [InlineData("TypefaceIdentity")] [InlineData("TypefaceAssembly")]
    [InlineData("BaselineMethod")] [InlineData("LineSpacingMethod")] [InlineData("CaptureOrder")]
    [InlineData("Baseline")] [InlineData("LineSpacing")]
    public void OriginalMetricIdentityCannotBeBroadened(string field)
    {
        var source = Source(); var metrics = source["IndependentSourceMetrics"]!.AsObject();
        metrics[field] = field switch
        {
            "Version" => JsonValue.Create(2), "Em" => JsonValue.Create(Math.BitIncrement(13.0)),
            "Dpi" => JsonValue.Create(Math.BitIncrement(1.5)), "ToReal" => JsonValue.Create(300.0),
            "Font" or "TypefaceAssembly" => new JsonObject { ["Sha256"] = "different-original-owner" },
            "Baseline" or "LineSpacing" => JsonValue.Create(-1.0), _ => JsonValue.Create("wrong-signature-or-identity")
        };
        Assert.Throws<InvalidDataException>(() => SourceReferenceInput.GetMetrics(JsonSerializer.SerializeToElement(Root()), JsonSerializer.SerializeToElement(source), ReferenceInput.FontHash));
    }

    [Fact]
    public void SyntheticSourceInputsCannotCreateReviewedReceiptProvenance()
    {
        var root = Root(); root["Schema"] = 2; root["Cases"] = new JsonArray(Source());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        Assert.Throws<InvalidDataException>(() => SourceReferenceInput.Parse(bytes, "synthetic-source-inputs"));
    }

    [Fact]
    public void ExactOriginalReceiptRemainsCompleteButMissingMetricsStayExplicit()
    {
        string path = Environment.GetEnvironmentVariable("PROGPU_SOURCE_DISPLAY_REFERENCE")
            ?? throw new InvalidOperationException("Set PROGPU_SOURCE_DISPLAY_REFERENCE to one exact original 192 or 288 receipt.");
        using var receipt = SourceReferenceInput.Read(path);
        Assert.False(receipt.HasIndependentMetrics);
        Assert.Equal(receipt.IsMidpoint ? 288 : 192, receipt.Cases.Count());
        foreach (var item in receipt.Cases)
            Assert.Throws<InvalidDataException>(() => SourceReferenceInput.GetMetrics(receipt.Root, item, ReferenceInput.FontHash));
        byte[] changed = File.ReadAllBytes(path).Concat(new byte[] { (byte)' ' }).ToArray();
        Assert.Throws<InvalidDataException>(() => SourceReferenceInput.Parse(changed, "reserialized"));
    }
}
