using System.Text.Json;
using Xunit;

public sealed class MidpointCasesTests
{
    [Fact]
    public void InventoryRetainsEveryIndependentAxisAndNoDuplicate()
    {
        MidpointInput[] cases = MidpointCases.Create().ToArray();
        Assert.Equal(288, cases.Length);
        Assert.Equal(Enumerable.Range(0, 288), cases.Select(value => value.Ordinal));
        Assert.Equal(288, cases.Select(value => value with { Ordinal = 0 }).Distinct().Count());
        Assert.Equal(["Display", "Ideal"], cases.Select(value => value.Mode).Distinct().Order().ToArray());
        Assert.Equal(["LeftToRight", "RightToLeft"], cases.Select(value => value.Direction).Distinct().Order().ToArray());
        Assert.Equal([1d, 2d], cases.Select(value => value.Dpi).Distinct().Order().ToArray());
        Assert.Equal([25d, 1000d], cases.Select(value => value.Width).Distinct().Order().ToArray());
    }

    [Fact]
    public void PhysicalMidpointsDistinguishEvenFromHalfUpAndRetainAdjacentDoubles()
    {
        foreach (MidpointInput input in MidpointCases.Create())
        {
            Assert.Contains(input.MidpointPhysicalEm, new[] { 18.5, 20.5 });
            Assert.NotEqual(Math.Round(input.MidpointPhysicalEm, MidpointRounding.ToEven),
                Math.Round(input.MidpointPhysicalEm, MidpointRounding.AwayFromZero));
            double expected = input.Side switch
            {
                "Below" => Math.BitDecrement(input.MidpointPhysicalEm),
                "Above" => Math.BitIncrement(input.MidpointPhysicalEm),
                _ => input.MidpointPhysicalEm,
            };
            Assert.Equal(MidpointCases.Bits(expected), input.PhysicalEmBits);
            Assert.Equal(input.PhysicalEmBits, input.RecomputedPhysicalEmBits);
            Assert.Equal(input.EmBits, MidpointCases.Bits(JsonSerializer.SerializeToElement(input).GetProperty("Em").GetDouble()));
        }
    }

    [Fact]
    public void RtlOverrideHasOriginalNoControlCounterpartForEveryInput()
    {
        // Keep the exact unsuccessful v2 inputs testable; they are not v3 RTL
        // evidence and are never admitted by relaxing Capture's font guard.
        MidpointInput[] cases = MidpointCases.CreateLegacy().ToArray();
        foreach (MidpointInput input in cases.Where(value => value.TextKind == "RtlOverride"))
        {
            MidpointInput paired = Assert.Single(cases, value => value.TextKind == "Positioned"
                && value.Mode == input.Mode && value.Direction == input.Direction && value.Dpi == input.Dpi
                && value.MidpointPhysicalEm == input.MidpointPhysicalEm && value.Side == input.Side && value.Width == input.Width);
            Assert.Equal("\u202E" + paired.Text + "\u202C", input.Text);
            Assert.Contains('\u0301', paired.Text);
            Assert.Contains('\u0302', paired.Text);
        }
    }

    [Fact]
    public void CoverageRequiresObservedOffsetsAndRunDirectionNotLabels()
    {
        MidpointInput input = Input("RtlOverride");
        MidpointCoverage coverage = MidpointCases.Observe(input, Observation(input, [[-0.25, 0.5]], 1));
        Assert.Equal(new MidpointCoverage(1, 1, 1, 1, 1), coverage);
        Assert.Throws<InvalidOperationException>(() => MidpointCases.Observe(input, Observation(input, [[0, 0]], 1)));
        Assert.Throws<InvalidOperationException>(() => MidpointCases.Observe(input, Observation(input, [[-0.25, 0.5]], 0)));
    }

    [Fact]
    public void PlainObservationPreservesAbsentOffsetsInsteadOfInventingZeros()
    {
        MidpointInput input = Input("Plain");
        JsonElement original = Observation(input, null, 0);
        Assert.Equal(new MidpointCoverage(1, 1, 0, 0), MidpointCases.Observe(input, original));
        Assert.Equal(JsonValueKind.Null, original.GetProperty("Lines")[0].GetProperty("Runs")[0].GetProperty("Offsets").ValueKind);
    }

    [Fact]
    public void ExactOriginalDoubleOffsetAndNominalAdvanceRemainUnchanged()
    {
        MidpointInput input = Input("Positioned");
        double offset = Math.BitIncrement(0.5);
        JsonElement original = Observation(input, [[offset, -9.6]], 0);
        MidpointCases.Observe(input, original);
        JsonElement run = original.GetProperty("Lines")[0].GetProperty("Runs")[0];
        Assert.Equal(MidpointCases.Bits(offset), MidpointCases.Bits(run.GetProperty("Offsets")[0][0].GetDouble()));
        Assert.Equal(MidpointCases.Bits(-9.6), MidpointCases.Bits(run.GetProperty("Offsets")[0][1].GetDouble()));
        Assert.Equal(0.625, run.GetProperty("NominalDesignAdvances")[0].GetDouble());
    }

    [Theory]
    [InlineData("Em")]
    [InlineData("Dpi")]
    [InlineData("Text")]
    [InlineData("Direction")]
    public void ChangedOriginalSourceIdentityRejects(string field)
    {
        MidpointInput input = Input("Plain");
        var original = JsonSerializer.SerializeToNode(Observation(input, null, 0))!;
        if (field is "Em" or "Dpi") original[field] = Math.BitIncrement(original[field]!.GetValue<double>());
        else original[field] = "changed";
        Assert.Throws<InvalidOperationException>(() => MidpointCases.Observe(input, JsonSerializer.SerializeToElement(original)));
    }

    [Theory]
    [InlineData("Advances")]
    [InlineData("NominalDesignAdvances")]
    [InlineData("Offsets")]
    public void IncompleteOccurrenceArraysReject(string field)
    {
        MidpointInput input = Input("Positioned");
        var original = JsonSerializer.SerializeToNode(Observation(input, [[1, 0]], 0))!;
        original["Lines"]![0]!["Runs"]![0]![field] = new System.Text.Json.Nodes.JsonArray();
        Assert.Throws<InvalidOperationException>(() => MidpointCases.Observe(input, JsonSerializer.SerializeToElement(original)));
    }

    [Fact]
    public void GenuineRtlInputsKeepAllAxesAndInterInputsOfTheUnqualifiedInventory()
    {
        Assert.NotEqual(MidpointCases.LegacyFamily, MidpointCases.Family);
        MidpointInput[] old = MidpointCases.CreateLegacy().ToArray();
        foreach (MidpointInput input in MidpointCases.Create())
        {
            MidpointInput prior = old[input.Ordinal];
            if (input.TextKind != "RtlPositioned")
            {
                Assert.Equal(prior, input);
                Assert.Equal("InterRegular", input.FontKey);
                continue;
            }
            Assert.Equal(prior with { TextKind = "RtlPositioned", Text = MidpointCases.RtlPositionedText }, input);
            Assert.Equal("NotoSansHebrewRegular", input.FontKey);
            Assert.Contains('\u05E9', input.Text);
            Assert.Contains('\u05B8', input.Text);
            Assert.Contains('\u05C1', input.Text);
            Assert.DoesNotContain('\u202E', input.Text);
            Assert.DoesNotContain('\u202C', input.Text);
        }
    }

    [Fact]
    public void RtlRequiresNonzeroPositioningInTheActualOddRun()
    {
        MidpointInput input = Input("RtlPositioned");
        Assert.Equal(new MidpointCoverage(1, 1, 1, 1, 1),
            MidpointCases.Observe(input, Observation(input, [[-0.25, 0.5]], 1)));
        Assert.Throws<InvalidOperationException>(() => MidpointCases.Observe(input, Observation(input, [[0, 0]], 1)));
        Assert.Throws<InvalidOperationException>(() => MidpointCases.Observe(input, Observation(input, [[-0.25, 0.5]], 0)));
        var split = JsonSerializer.SerializeToNode(Observation(input, [[-0.25, 0.5]], 0))!;
        var unpositionedOdd = JsonSerializer.SerializeToNode(Observation(input, [[0, 0]], 1))!;
        split["Lines"]![0]!["Runs"]!.AsArray().Add(unpositionedOdd["Lines"]![0]!["Runs"]![0]!.DeepClone());
        Assert.Throws<InvalidOperationException>(() => MidpointCases.Observe(input, JsonSerializer.SerializeToElement(split)));
    }

    [Fact]
    public void FontAndNoticePinOwnTheExactImmutableUpstreamPayloads()
    {
        ReferenceFontPin pin = ReferenceFontPin.Load();
        Assert.Equal("https://github.com/notofonts/noto-fonts", pin.Repository);
        Assert.Equal("ffebf8c1ee449e544955a7e813c54f9b73848eac", pin.Commit);
        Assert.Equal("Noto Sans Hebrew", pin.FamilyName);
        Assert.Equal("hinted/ttf/NotoSansHebrew/NotoSansHebrew-Regular.ttf", pin.FontPath);
        Assert.Equal("NotoSansHebrew-Regular.ttf", pin.FontFileName);
        Assert.Equal(26900, pin.FontLength);
        Assert.Equal("A7FA16FFFB27BEDB060A0866267C29E9859AEB9C21CC33F5B3AAF6EB062ECA85", pin.FontSha256);
        Assert.Equal("LICENSE", pin.LicensePath);
        Assert.Equal("OFL.txt", pin.LicenseFileName);
        Assert.Equal(4377, pin.LicenseLength);
        Assert.Equal("0DAB92D0544F7B233403F14B84A663BDBFA746982EDA629E7F4F9FFE1B036FEB", pin.LicenseSha256);
    }

    [Fact]
    public void PayloadAdmissionRejectsChangedBytesLengthAndMissingLicense()
    {
        byte[] payload = [1, 2, 3];
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        ReferenceFontPin.VerifyPayload(payload, 3, hash);
        Assert.Throws<InvalidOperationException>(() => ReferenceFontPin.VerifyPayload([1, 2, 4], 3, hash));
        Assert.Throws<InvalidOperationException>(() => ReferenceFontPin.VerifyPayload(payload, 2, hash));
        Assert.Throws<InvalidOperationException>(() => ReferenceFontPin.VerifyPayload([], 4377, ReferenceFontPin.Load().LicenseSha256));
    }

    private static MidpointInput Input(string kind) => (kind == "RtlOverride" ? MidpointCases.CreateLegacy() : MidpointCases.Create())
        .First(value => value.TextKind == kind);

    // Synthetic parser/coverage input only. These values are never an oracle
    // receipt, font observation, portable metric baseline or runtime evidence.
    private static JsonElement Observation(MidpointInput input, double[][]? offsets, int bidi)
        => JsonSerializer.SerializeToElement(new
        {
            input.Text, input.Mode, input.Direction, input.Dpi, input.Em, input.Width,
            Lines = new[] { new { Runs = new[] { new
            {
                GlyphIds = new[] { 1 }, Advances = new[] { 9.6 }, NominalDesignAdvances = new[] { 0.625 },
                BidiLevel = bidi, Offsets = offsets,
            } } } },
        });
}
