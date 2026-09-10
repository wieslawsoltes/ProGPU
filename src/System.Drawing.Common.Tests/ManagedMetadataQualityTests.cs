using System.Collections;
using System.Drawing.Design;
using System.Drawing.Imaging;
using System.Reflection;
using Xunit;

namespace System.Drawing.Tests;

public sealed class ManagedMetadataQualityTests
{
    [Fact]
    public void BitmapSuffixAttributesHaveOfficialAssemblyOnlyShape()
    {
        Assert.False(typeof(BitmapSuffixInSameAssemblyAttribute).IsSealed);
        Assert.False(typeof(BitmapSuffixInSatelliteAssemblyAttribute).IsSealed);

        AssertAssemblyOnlyUsage(typeof(BitmapSuffixInSameAssemblyAttribute));
        AssertAssemblyOnlyUsage(typeof(BitmapSuffixInSatelliteAssemblyAttribute));
    }

    [Fact]
    public void CategoryNamesSnapshotAndExposeReadOnlyCollectionBehavior()
    {
        string[] source = ["Appearance", "Layout"];
        var names = new CategoryNameCollection(source);
        source[0] = "Changed";

        Assert.IsAssignableFrom<ReadOnlyCollectionBase>(names);
        Assert.Equal(2, names.Count);
        Assert.Equal("Appearance", names[0]);
        Assert.True(names.Contains("Layout"));
        Assert.Equal(1, names.IndexOf("Layout"));
        Assert.False(names.Contains("Missing"));
        Assert.Equal(-1, names.IndexOf("Missing"));

        var copy = new string[3];
        names.CopyTo(copy, 1);
        Assert.Null(copy[0]);
        Assert.Equal(["Appearance", "Layout"], copy[1..]);

        var clonedNames = new CategoryNameCollection(names);
        Assert.Equal(["Appearance", "Layout"], clonedNames.Cast<string>());
    }

    [Fact]
    public void CategoryNamesPreserveCollectionValidation()
    {
        Assert.Throws<ArgumentNullException>(() => new CategoryNameCollection((string[])null!));
        Assert.Throws<ArgumentNullException>(() => new CategoryNameCollection((CategoryNameCollection)null!));

        var names = new CategoryNameCollection(["Appearance"]);
        Assert.Throws<ArgumentNullException>(() => names.CopyTo(null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => names.CopyTo(new string[1], -1));
        Assert.Throws<ArgumentException>(() => names.CopyTo(new string[1], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = names[-1]);
    }

    [Fact]
    public void ColorModeHasOfficialValues()
    {
        Assert.Equal(0, (int)ColorMode.Argb32Mode);
        Assert.Equal(1, (int)ColorMode.Argb64Mode);
    }

    private static void AssertAssemblyOnlyUsage(Type attributeType)
    {
        AttributeUsageAttribute usage = attributeType.GetCustomAttribute<AttributeUsageAttribute>()!;

        Assert.Equal(AttributeTargets.Assembly, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
        Assert.True(usage.Inherited);
    }
}
