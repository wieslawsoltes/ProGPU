using System.ComponentModel;
using System.Drawing;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class FontBrowsingMetadataTests
{
    private static readonly string[] s_browsableNames =
    [
        nameof(Font.Name), nameof(Font.Size), nameof(Font.Unit), nameof(Font.Bold),
        nameof(Font.GdiCharSet), nameof(Font.GdiVerticalFont), nameof(Font.Italic),
        nameof(Font.Strikeout), nameof(Font.Underline)
    ];

    private static readonly string[] s_hiddenNames =
    [
        nameof(Font.FontFamily), nameof(Font.Height), nameof(Font.IsSystemFont),
        nameof(Font.OriginalFontName), nameof(Font.OriginalUnit), nameof(Font.SizeInPoints),
        nameof(Font.Style), nameof(Font.SystemFontName)
    ];

    [Theory]
    [InlineData(nameof(Font.FontFamily))]
    [InlineData(nameof(Font.Height))]
    [InlineData(nameof(Font.IsSystemFont))]
    [InlineData(nameof(Font.OriginalFontName))]
    [InlineData(nameof(Font.OriginalUnit))]
    [InlineData(nameof(Font.SizeInPoints))]
    [InlineData(nameof(Font.Style))]
    [InlineData(nameof(Font.SystemFontName))]
    public void NonDesignerPropertiesRetainTheirPublicDescriptorWithHiddenMetadata(string name)
    {
        PropertyDescriptor property = Assert.IsAssignableFrom<PropertyDescriptor>(
            TypeDescriptor.GetProperties(typeof(Font))[name]);

        Assert.Equal(typeof(Font), property.ComponentType);
        Assert.True(property.IsReadOnly);
        Assert.False(property.IsBrowsable);
        Assert.False(Assert.IsType<BrowsableAttribute>(property.Attributes[typeof(BrowsableAttribute)]).Browsable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DesignerPropertiesHaveTheCompleteCanonicalOrder(int overload)
    {
        using FontFamily family = FontFamily.GenericSansSerif;
        using var font = new Font(family, 13.25f, FontStyle.Bold | FontStyle.Italic);
        var converter = new FontConverter();

        PropertyDescriptorCollection properties = overload switch
        {
            0 => converter.GetProperties(font)!,
            1 => converter.GetProperties(null, font)!,
            _ => converter.GetProperties(null, font, [BrowsableAttribute.Yes])
        };

        Assert.Equal(s_browsableNames, properties.Cast<PropertyDescriptor>().Select(property => property.Name));
        Assert.Equal(s_browsableNames, properties.Cast<PropertyDescriptor>().Select(property => property.DisplayName));
        Assert.All(properties.Cast<PropertyDescriptor>(), property => Assert.True(property.IsBrowsable));
        Assert.IsType<FontConverter.FontNameConverter>(properties[nameof(Font.Name)]!.Converter);
        Assert.IsType<FontConverter.FontUnitConverter>(properties[nameof(Font.Unit)]!.Converter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitUnfilteredQueriesRetainEveryPublicProperty(bool useEmptyFilter)
    {
        using FontFamily family = FontFamily.GenericSansSerif;
        using var font = new Font(family, 11.5f, FontStyle.Underline, GraphicsUnit.Pixel, 2, true);
        var converter = new FontConverter();

        PropertyDescriptorCollection properties = converter.GetProperties(
            null, font, useEmptyFilter ? [] : null);

        AssertNames(s_browsableNames.Concat(s_hiddenNames), properties);
        Assert.Equal(font.OriginalUnit, properties[nameof(Font.OriginalUnit)]!.GetValue(font));
        Assert.Equal(font.Style, properties[nameof(Font.Style)]!.GetValue(font));
        Assert.Equal(font.GdiCharSet, properties[nameof(Font.GdiCharSet)]!.GetValue(font));
        Assert.Equal(font.GdiVerticalFont, properties[nameof(Font.GdiVerticalFont)]!.GetValue(font));
    }

    [Fact]
    public void ExplicitHiddenFilterReturnsOnlyNonDesignerProperties()
    {
        using FontFamily family = FontFamily.GenericSansSerif;
        using var font = new Font(family, 9.75f);

        PropertyDescriptorCollection properties = new FontConverter().GetProperties(
            null, font, [BrowsableAttribute.No]);

        AssertNames(s_hiddenNames, properties);
        Assert.All(properties.Cast<PropertyDescriptor>(), property => Assert.False(property.IsBrowsable));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypeDescriptorHonorsTheSameMetadataWithoutTheFontConverter(bool browsable)
    {
        PropertyDescriptorCollection properties = TypeDescriptor.GetProperties(
            typeof(Font), [new BrowsableAttribute(browsable)]);

        AssertNames(browsable ? s_browsableNames : s_hiddenNames, properties);
    }

    private static void AssertNames(IEnumerable<string> expected, PropertyDescriptorCollection actual)
    {
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal),
            actual.Cast<PropertyDescriptor>().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
    }
}
