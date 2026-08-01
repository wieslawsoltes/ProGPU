using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class GrVulkanDescriptorCompatibilityTests
{
    [Fact]
    public void AllocationUsesOfficialSequentialStorageAndValueBehavior()
    {
        AssertFieldTypes<GRVkAlloc>(
            typeof(ulong),
            typeof(ulong),
            typeof(ulong),
            typeof(uint),
            typeof(IntPtr),
            typeof(byte));

        var allocation = new GRVkAlloc
        {
            Memory = 11,
            Size = 4096,
            Offset = 256,
            Flags = 3,
            BackendMemory = (IntPtr)0x1234,
        };
        var same = allocation;

        Assert.Equal(11ul, allocation.Memory);
        Assert.Equal(4096ul, allocation.Size);
        Assert.Equal(256ul, allocation.Offset);
        Assert.Equal(3u, allocation.Flags);
        Assert.Equal((IntPtr)0x1234, allocation.BackendMemory);
        Assert.True(allocation.Equals(same));
        Assert.True(allocation == same);
        Assert.Equal(allocation.GetHashCode(), same.GetHashCode());

        same.Offset++;
        Assert.True(allocation != same);
    }

    [Fact]
    public void YcbcrDescriptorsUseOfficialSequentialStorageAndValueBehavior()
    {
        AssertFieldTypes<GRVkYcbcrComponents>(
            typeof(uint), typeof(uint), typeof(uint), typeof(uint));
        AssertFieldTypes<GRVkYcbcrConversionInfo>(
            typeof(uint),
            typeof(ulong),
            typeof(uint),
            typeof(uint),
            typeof(uint),
            typeof(uint),
            typeof(uint),
            typeof(uint),
            typeof(GRVkYcbcrComponents),
            typeof(byte),
            typeof(byte));

        var components = new GRVkYcbcrComponents { R = 1, G = 2, B = 3, A = 4 };
        var conversion = new GRVkYcbcrConversionInfo
        {
            Format = 1000156003,
            ExternalFormat = 17,
            YcbcrModel = 1,
            YcbcrRange = 2,
            XChromaOffset = 3,
            YChromaOffset = 4,
            ChromaFilter = 5,
            ForceExplicitReconstruction = 6,
            Components = components,
            SupportsLinearFilter = true,
            SamplerFilterMustMatchChromaFilter = true,
        };
        var same = conversion;

        Assert.Equal(components, conversion.Components);
        Assert.True(conversion.SupportsLinearFilter);
        Assert.True(conversion.SamplerFilterMustMatchChromaFilter);
        Assert.True(conversion == same);
        Assert.Equal(conversion.GetHashCode(), same.GetHashCode());

        same.SupportsLinearFilter = false;
        Assert.True(conversion != same);
    }

    [Fact]
    public void ImageInfoUsesOfficialSequentialStorageAndValueBehavior()
    {
        AssertFieldTypes<GRVkImageInfo>(
            typeof(ulong),
            typeof(GRVkAlloc),
            typeof(uint),
            typeof(uint),
            typeof(uint),
            typeof(uint),
            typeof(uint),
            typeof(uint),
            typeof(uint),
            typeof(byte),
            typeof(GRVkYcbcrConversionInfo),
            typeof(uint));

        var info = new GRVkImageInfo
        {
            Image = 23,
            Alloc = new GRVkAlloc { Memory = 29, Size = 8192, Offset = 512 },
            ImageTiling = 1,
            ImageLayout = 2,
            Format = 3,
            ImageUsageFlags = 4,
            SampleCount = 8,
            LevelCount = 5,
            CurrentQueueFamily = 6,
            Protected = true,
            YcbcrConversionInfo = new GRVkYcbcrConversionInfo { Format = 7 },
            SharingMode = 8,
        };
        var same = info;

        Assert.True(info.Protected);
        Assert.Equal(8u, info.SharingMode);
        Assert.Equal(7u, info.YcbcrConversionInfo.Format);
        Assert.True(info.Equals(same));
        Assert.True(info == same);
        Assert.Equal(info.GetHashCode(), same.GetHashCode());

        same.ImageLayout++;
        Assert.True(info != same);
    }

    [Fact]
    public void LegacyConversionAliasIsOneFieldAndRoundTrips()
    {
#pragma warning disable CS0618
        AssertFieldTypes<GrVkYcbcrConversionInfo>(typeof(GRVkYcbcrConversionInfo));
        var legacy = new GrVkYcbcrConversionInfo
        {
            Format = 17,
            ExternalFormat = 19,
            Components = new GRVkYcbcrComponents { R = 1, G = 2, B = 3, A = 4 },
            SupportsLinearFilter = true,
            SamplerFilterMustMatchChromaFilter = true,
            FormatFeatures = uint.MaxValue,
        };
        GRVkYcbcrConversionInfo current = legacy;
        GrVkYcbcrConversionInfo roundTrip = current;

        Assert.Equal(17u, current.Format);
        Assert.Equal(19ul, current.ExternalFormat);
        Assert.True(roundTrip.SupportsLinearFilter);
        Assert.True(roundTrip.SamplerFilterMustMatchChromaFilter);
        Assert.Equal(0u, roundTrip.FormatFeatures);
#pragma warning restore CS0618
    }

    private static void AssertFieldTypes<T>(params Type[] expected)
    {
        var fieldTypes = typeof(T)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .OrderBy(static field => field.MetadataToken)
            .Select(static field => field.FieldType);
        Assert.Equal(expected, fieldTypes);
    }
}
