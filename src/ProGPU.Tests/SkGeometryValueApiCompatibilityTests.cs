using System.Numerics;
using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkGeometryValueApiCompatibilityTests
{
    [Fact]
    public void PointVectorConversionsRoundTripWithoutAllocation()
    {
        var vector = new Vector2(12.5f, -7.25f);

        SKPoint point = vector;
        Vector2 roundTrip = point;

        Assert.Equal(new SKPoint(12.5f, -7.25f), point);
        Assert.Equal(vector, roundTrip);
    }

    [Fact]
    public void GeometryFamiliesExposeNativeParameterNames()
    {
        AssertParameterNames(
            typeof(SKPoint).GetMethod(nameof(SKPoint.Add), [typeof(SKPoint), typeof(SKSize)]),
            "pt",
            "sz");
        AssertParameterNames(
            typeof(SKPointI).GetConstructor([typeof(SKSizeI)]),
            "sz");
        AssertParameterNames(
            typeof(SKPoint3).GetMethod(nameof(SKPoint3.Subtract)),
            "pt",
            "sz");
        AssertParameterNames(
            typeof(SKSize).GetConstructor([typeof(SKPoint)]),
            "pt");
        AssertParameterNames(
            typeof(SKSizeI).GetMethod(nameof(SKSizeI.Add)),
            "sz1",
            "sz2");
        AssertParameterNames(
            typeof(SKRect).GetMethod(
                nameof(SKRect.Intersect),
                BindingFlags.Public | BindingFlags.Static,
                [typeof(SKRect), typeof(SKRect)]),
            "a",
            "b");
        AssertParameterNames(
            typeof(SKRectI).GetMethod(
                nameof(SKRectI.Inflate),
                BindingFlags.Public | BindingFlags.Static,
                [typeof(SKRectI), typeof(int), typeof(int)]),
            "rect",
            "x",
            "y");
        AssertParameterNames(typeof(SKPoint).GetMethod(nameof(SKPoint.Offset), [typeof(SKPoint)]), "p");
        AssertParameterNames(typeof(SKPoint).GetMethod(nameof(SKPoint.Subtract), [typeof(SKPoint), typeof(SKSizeI)]), "pt", "sz");
        AssertParameterNames(typeof(SKSize).GetMethod(nameof(SKSize.Subtract)), "sz1", "sz2");
        AssertParameterNames(typeof(SKSizeI).GetConstructor([typeof(SKPointI)]), "pt");
        AssertParameterNames(typeof(SKRect).GetMethod(nameof(SKRect.Contains), [typeof(SKPoint)]), "pt");
        AssertParameterNames(typeof(SKRect).GetMethod(nameof(SKRect.Offset), [typeof(SKPoint)]), "pos");
        AssertParameterNames(typeof(SKRectI).GetMethod(nameof(SKRectI.Contains), [typeof(SKPointI)]), "pt");
        AssertParameterNames(typeof(SKRectI).GetMethod(nameof(SKRectI.Offset), [typeof(SKPointI)]), "pos");
    }

    private static void AssertParameterNames(MethodBase? method, params string[] expected)
    {
        Assert.NotNull(method);
        Assert.Equal(expected, method!.GetParameters().Select(parameter => parameter.Name));
    }
}
