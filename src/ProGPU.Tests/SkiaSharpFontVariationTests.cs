using System.Reflection;
using ProGPU.Fonts.Inter;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkiaSharpFontVariationTests
{
    [Fact]
    public void ValueContractsPreserveOfficialFieldOrderAndEquality()
    {
        Assert.Equal(
            new[] { typeof(ushort), typeof(uint) },
            DeclaredFieldTypes<SKFontPaletteOverride>());
        Assert.Equal(
            new[] { typeof(SKFourByteTag), typeof(float), typeof(float), typeof(float), typeof(byte) },
            DeclaredFieldTypes<SKFontVariationAxis>());
        Assert.Equal(
            new[] { typeof(SKFourByteTag), typeof(float) },
            DeclaredFieldTypes<SKFontVariationPositionCoordinate>());

        var palette = new SKFontPaletteOverride { Index = 7, Color = 0x80402010 };
        Assert.Equal(palette, new SKFontPaletteOverride { Index = 7, Color = 0x80402010 });
        Assert.NotEqual(palette, new SKFontPaletteOverride { Index = 8, Color = 0x80402010 });

        var axis = new SKFontVariationAxis
        {
            Tag = SKFourByteTag.Parse("wght"),
            Min = 100,
            Default = 400,
            Max = 900,
            IsHidden = true
        };
        Assert.True(axis.IsHidden);
        Assert.Equal(axis, axis);

        var coordinate = new SKFontVariationPositionCoordinate
        {
            Axis = axis.Tag,
            Value = 537
        };
        Assert.Equal(coordinate, coordinate);
        Assert.NotEqual(
            coordinate,
            new SKFontVariationPositionCoordinate { Axis = axis.Tag, Value = 538 });
    }

    [Fact]
    public void VariableTypefaceReportsAxesAndDefaultPosition()
    {
        using var typeface = new SKTypeface(InterFontFamily.Variable, "Inter");

        Assert.Equal(2, typeface.VariationDesignParameterCount);
        Assert.Equal(2, typeface.VariationDesignPositionCount);

        Span<SKFontVariationAxis> axes = stackalloc SKFontVariationAxis[2];
        Assert.Equal(2, typeface.GetVariationDesignParameters(axes));
        Assert.Equal("opsz", axes[0].Tag.ToString());
        Assert.Equal((14f, 14f, 32f), (axes[0].Min, axes[0].Default, axes[0].Max));
        Assert.Equal("wght", axes[1].Tag.ToString());
        Assert.Equal((100f, 400f, 900f), (axes[1].Min, axes[1].Default, axes[1].Max));

        Span<SKFontVariationPositionCoordinate> position =
            stackalloc SKFontVariationPositionCoordinate[2];
        Assert.Equal(2, typeface.GetVariationDesignPosition(position));
        Assert.Equal(("opsz", 14f), (position[0].Axis.ToString(), position[0].Value));
        Assert.Equal(("wght", 400f), (position[1].Axis.ToString(), position[1].Value));

        Span<SKFontVariationAxis> shortAxes = stackalloc SKFontVariationAxis[1];
        Assert.Equal(1, typeface.GetVariationDesignParameters(shortAxes));
        Assert.Equal(0, typeface.GetVariationDesignParameters(Span<SKFontVariationAxis>.Empty));
    }

    [Fact]
    public void VariationSpanQueriesAllocateNothingAfterLazyInitialization()
    {
        using var typeface = new SKTypeface(InterFontFamily.Variable, "Inter");
        Span<SKFontVariationAxis> axes = stackalloc SKFontVariationAxis[2];
        Span<SKFontVariationPositionCoordinate> coordinates =
            stackalloc SKFontVariationPositionCoordinate[2];
        _ = typeface.GetVariationDesignParameters(axes);
        _ = typeface.GetVariationDesignPosition(coordinates);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100_000; index++)
        {
            _ = typeface.GetVariationDesignParameters(axes);
            _ = typeface.GetVariationDesignPosition(coordinates);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void CloneCreatesImmutableClampedVariationInstance()
    {
        using var typeface = new SKTypeface(InterFontFamily.Variable, "Inter");
        Span<SKFontVariationPositionCoordinate> requested =
            stackalloc SKFontVariationPositionCoordinate[3]
            {
                new() { Axis = SKFourByteTag.Parse("wght"), Value = 537 },
                new() { Axis = SKFourByteTag.Parse("opsz"), Value = 100 },
                new() { Axis = SKFourByteTag.Parse("NOPE"), Value = 12 }
            };

        using SKTypeface clone = typeface.Clone(requested);

        Assert.NotSame(typeface, clone);
        Assert.Equal(537, clone.FontWeight);
        Assert.Equal(2, clone.VariationDesignPositionCount);
        SKFontVariationPositionCoordinate[] position = clone.VariationDesignPosition;
        Assert.Equal(("opsz", 32f), (position[0].Axis.ToString(), position[0].Value));
        Assert.Equal(("wght", 537f), (position[1].Axis.ToString(), position[1].Value));

        Assert.Equal(400, typeface.FontWeight);
        Assert.Equal(14f, typeface.VariationDesignPosition[0].Value);
    }

    [Fact]
    public void RepeatedVariationCloneReusesFontInstanceAndAllocatesOnlyWrapper()
    {
        using var typeface = new SKTypeface(InterFontFamily.Variable, "Inter");
        Span<SKFontVariationPositionCoordinate> requested =
            stackalloc SKFontVariationPositionCoordinate[2]
            {
                new() { Axis = SKFourByteTag.Parse("opsz"), Value = 23 },
                new() { Axis = SKFourByteTag.Parse("wght"), Value = 537 }
            };
        using (SKTypeface warmup = typeface.Clone(requested))
        {
            Assert.Equal(537, warmup.FontWeight);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        using SKTypeface first = typeface.Clone(requested);
        long firstAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        using SKTypeface second = typeface.Clone(requested);
        long secondAllocation = GC.GetAllocatedBytesForCurrentThread() - before - firstAllocation;

        Assert.Same(first.Font, second.Font);
        Assert.InRange(firstAllocation, 1, 256);
        Assert.Equal(firstAllocation, secondAllocation);
    }

    [Fact]
    public void EmptyTypefaceHasNoVariationSurfaceAndRemainsSharedAfterDispose()
    {
        SKTypeface empty = SKTypeface.Empty;
        empty.Dispose();

        Assert.Same(empty, SKTypeface.Empty);
        Assert.True(empty.IsEmpty);
        Assert.Equal(0, empty.VariationDesignParameterCount);
        Assert.Empty(empty.VariationDesignParameters);
        Assert.Empty(empty.VariationDesignPosition);
    }

    private static Type[] DeclaredFieldTypes<T>() =>
        typeof(T)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .OrderBy(static field => field.MetadataToken)
            .Select(static field => field.FieldType)
            .ToArray();
}
