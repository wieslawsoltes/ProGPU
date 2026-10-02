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
        MidpointInput[] cases = MidpointCases.Create().ToArray();
        foreach (MidpointInput input in cases.Where(value => value.TextKind == "RtlOverride"))
        {
            MidpointInput paired = Assert.Single(cases.Where(value => value.TextKind == "Positioned"
                && value.Mode == input.Mode && value.Direction == input.Direction && value.Dpi == input.Dpi
                && value.MidpointPhysicalEm == input.MidpointPhysicalEm && value.Side == input.Side && value.Width == input.Width));
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
        Assert.Equal(new MidpointCoverage(1, 1, 1, 1), coverage);
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

    private static MidpointInput Input(string kind) => MidpointCases.Create().First(value => value.TextKind == kind);

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
