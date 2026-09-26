using System.ComponentModel.Design.Serialization;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class FontDescriptorSerializationTests
{
    public static TheoryData<FontStyle, GraphicsUnit, byte, bool, int> ConstructorShapes => new()
    {
        { FontStyle.Regular, GraphicsUnit.Point, 1, false, 2 },
        { FontStyle.Italic, GraphicsUnit.Point, 1, false, 3 },
        { FontStyle.Regular, GraphicsUnit.Inch, 1, false, 4 },
        { FontStyle.Bold | FontStyle.Underline, GraphicsUnit.Pixel, 1, false, 4 },
        { FontStyle.Regular, GraphicsUnit.Point, 0, false, 5 },
        { FontStyle.Regular, GraphicsUnit.Point, 2, false, 5 },
        { FontStyle.Italic | FontStyle.Strikeout, GraphicsUnit.Document, 2, false, 5 },
        { FontStyle.Regular, GraphicsUnit.Point, 1, true, 6 },
        { FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Millimeter, 0, true, 6 },
        { FontStyle.Underline | FontStyle.Strikeout, GraphicsUnit.World, 2, true, 6 }
    };

    [Theory]
    [MemberData(nameof(ConstructorShapes))]
    public void DescriptorUsesMinimalConstructorAndRetainsEveryValue(
        FontStyle style, GraphicsUnit unit, byte characterSet, bool vertical, int argumentCount)
    {
        using FontFamily family = FontFamily.GenericSansSerif;
        using var source = new Font(family.Name, 13.75f, style, unit, characterSet, vertical);
        AssertDescriptor(source, family.Name, argumentCount);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 6)]
    public void DescriptorPreservesRequestedNameWhenTheFamilyFallsBack(bool vertical, int argumentCount)
    {
        const string requestedName = "ProGPU Descriptor Missing Family 639CA17F";
        using var source = new Font(requestedName, 17.25f, FontStyle.Regular, GraphicsUnit.Point, 1, vertical);
        Assert.NotEqual(requestedName, source.Name);
        AssertDescriptor(source, requestedName, argumentCount);
    }

    private static void AssertDescriptor(Font source, string requestedName, int argumentCount)
    {
        var converter = new FontConverter();
        var descriptor = Assert.IsType<InstanceDescriptor>(
            converter.ConvertTo(null, CultureInfo.InvariantCulture, source, typeof(InstanceDescriptor)));
        Type[] allParameterTypes =
            [typeof(string), typeof(float), typeof(FontStyle), typeof(GraphicsUnit), typeof(byte), typeof(bool)];
        object[] allValues =
            [requestedName, source.Size, source.Style, source.Unit, source.GdiCharSet, source.GdiVerticalFont];

        Assert.True(descriptor.IsComplete);
        Assert.Equal(argumentCount, descriptor.Arguments.Count);
        var constructor = Assert.IsAssignableFrom<ConstructorInfo>(descriptor.MemberInfo);
        Assert.Equal(typeof(Font), constructor.DeclaringType);
        Assert.Equal(allParameterTypes.Take(argumentCount), constructor.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(allValues.Take(argumentCount), descriptor.Arguments.Cast<object>());

        string resolvedName = source.Name;
        source.Dispose();
        using var restored = Assert.IsType<Font>(descriptor.Invoke());
        using var second = Assert.IsType<Font>(descriptor.Invoke());
        Assert.NotSame(source, restored);
        Assert.NotSame(restored, second);
        Assert.Equal(resolvedName, restored.Name);
        Assert.Equal(requestedName, restored.OriginalFontName);
        Assert.Equal((float)allValues[1], restored.Size);
        Assert.Equal((FontStyle)allValues[2], restored.Style);
        Assert.Equal((GraphicsUnit)allValues[3], restored.Unit);
        Assert.Equal((byte)allValues[4], restored.GdiCharSet);
        Assert.Equal((bool)allValues[5], restored.GdiVerticalFont);
        Assert.Equal(restored, second);
    }
}
