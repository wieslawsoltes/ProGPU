using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkCodecFrameInfoLayoutCompatibilityTests
{
    [Fact]
    public void BooleanStorageUsesOneByteFieldsInTheSequentialAbi()
    {
        var fieldTypes = typeof(SKCodecFrameInfo)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(static field => field.FieldType)
            .ToArray();

        Assert.Equal(8, fieldTypes.Length);
        Assert.Equal(2, fieldTypes.Count(static type => type == typeof(byte)));
        Assert.DoesNotContain(typeof(bool), fieldTypes);
    }

    [Fact]
    public void BooleanPropertiesNormalizeValuesAndParticipateInEquality()
    {
        var value = new SKCodecFrameInfo
        {
            RequiredFrame = -1,
            Duration = 125,
            FullyRecieved = true,
            AlphaType = SKAlphaType.Premul,
            HasAlphaWithinBounds = true,
            DisposalMethod = SKCodecAnimationDisposalMethod.RestoreBackgroundColor,
            Blend = SKCodecAnimationBlend.SrcOver,
            FrameRect = new SKRectI(1, 2, 31, 42),
        };
        var equal = value;

        Assert.True(value.FullyRecieved);
        Assert.True(value.HasAlphaWithinBounds);
        Assert.True(value == equal);
        Assert.False(value != equal);
        Assert.Equal(value.GetHashCode(), equal.GetHashCode());

        equal.HasAlphaWithinBounds = false;
        Assert.NotEqual(value, equal);
        Assert.False(equal.HasAlphaWithinBounds);
    }
}
