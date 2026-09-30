using Xunit;

namespace ProGPU.Tests;

public sealed class DrawingTraceMethodRequirementTests
{
    private const string MetafileType = "System.Drawing.Common.Tests.MetafileParserTests";
    private const string MetafileMethod = "WarmedEnumerationDoesNotAllocatePerRecordPayloads";

    [Fact]
    public void LegacyInvocationStillRequiresTheFontMethod()
    {
        DrawingTraceMethodRequirement method = Assert.Single(DrawingTraceMethodRequirement.Parse([]));
        Assert.Equal("System.Drawing.Tests.FontQualityTests", method.MethodNamespace);
        Assert.Equal("WarmedPrivateMetricReadsAreAllocationFree", method.MethodName);
        Assert.False(method.Found);
        method.Observe(method.MethodNamespace, method.MethodName);
        Assert.True(method.Found);
    }

    [Fact]
    public void AdditionalMetadataCannotReplaceTheFontRequirement()
    {
        DrawingTraceMethodRequirement[] methods = DrawingTraceMethodRequirement.Parse(
            ["--require-method", MetafileType, MetafileMethod]);
        Assert.Equal(2, methods.Length);
        foreach (DrawingTraceMethodRequirement method in methods)
            method.Observe(MetafileType, MetafileMethod);
        Assert.False(methods[0].Found);
        Assert.True(methods[1].Found);
        Assert.Equal(MetafileType, methods[1].MethodNamespace);
        Assert.Equal(MetafileMethod, methods[1].MethodName);
    }

    [Theory]
    [InlineData(null, MetafileMethod)]
    [InlineData(MetafileType, null)]
    [InlineData("System.Drawing.Tests.MetafileParserTests", MetafileMethod)]
    [InlineData("system.Drawing.Common.Tests.MetafileParserTests", MetafileMethod)]
    [InlineData(MetafileType, "warmedEnumerationDoesNotAllocatePerRecordPayloads")]
    [InlineData(MetafileType, "WarmedEnumerationDoesNotAllocatePerRecordPayloadsSuffix")]
    [InlineData(MetafileType, "*")]
    public void MethodMetadataRequiresBothExactOrdinalNames(string? owner, string? name)
    {
        var requirement = new DrawingTraceMethodRequirement(MetafileType, MetafileMethod);
        requirement.Observe(owner, name);
        Assert.False(requirement.Found);
        requirement.Observe(MetafileType, MetafileMethod);
        requirement.Observe(owner, name);
        Assert.True(requirement.Found);
    }

    [Fact]
    public void RepeatedOptionsAreIndependentRequiredMetadata()
    {
        DrawingTraceMethodRequirement[] methods = DrawingTraceMethodRequirement.Parse(
            ["--require-method", MetafileType, MetafileMethod, "--require-method", "Other.Type", "OtherMethod"]);
        Assert.Equal(3, methods.Length);
        foreach (DrawingTraceMethodRequirement method in methods)
        {
            method.Observe(methods[0].MethodNamespace, methods[0].MethodName);
            method.Observe(MetafileType, MetafileMethod);
        }
        Assert.True(methods[0].Found);
        Assert.True(methods[1].Found);
        Assert.False(methods[2].Found);
    }

    [Fact]
    public void InvalidOptionsDoNotRemoveOrReplaceMandatoryAdmission()
    {
        string[][] invalid =
        [
            ["--require-method"],
            ["--require-method", MetafileType],
            ["--replace-method", MetafileType, MetafileMethod],
            ["--require-method", "", MetafileMethod],
            ["--require-method", MetafileType, " "],
            ["--require-method", MetafileType, MetafileMethod, "extra"]
        ];
        foreach (string[] arguments in invalid)
            Assert.Throws<ArgumentException>(() => DrawingTraceMethodRequirement.Parse(arguments));
    }
}
