using System.Text.Json;
using ProGPU.Hmi.Mqtt;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiMqttCommandSerializationTests
{
    [Theory]
    [InlineData(HmiTagType.Number)]
    [InlineData(HmiTagType.Boolean)]
    [InlineData(HmiTagType.Text)]
    public void CommandScalarsRetainWireTypesWithoutReflection(HmiTagType type)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var timestamp = DateTimeOffset.UnixEpoch.AddHours(1);
        var value = type switch
        {
            HmiTagType.Number => HmiValue.From(123.125),
            HmiTagType.Boolean => HmiValue.From(true),
            _ => HmiValue.From("Zażółć / 控制 / \"quoted\"\nsecond line")
        };
        byte[] payload = HmiMqttPayloadCodec.EncodeCommand(value, timestamp);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.True(Guid.TryParseExact(root.GetProperty("id").GetString(), "N", out _));
        Assert.Equal(timestamp, root.GetProperty("timestamp").GetDateTimeOffset());
        switch (type)
        {
            case HmiTagType.Number: Assert.Equal(value.Number, root.GetProperty("value").GetDouble()); break;
            case HmiTagType.Boolean: Assert.True(root.GetProperty("value").GetBoolean()); break;
            case HmiTagType.Text: Assert.Equal(value.Text, root.GetProperty("value").GetString()); break;
        }
    }

    [Fact]
    public void CommandEncodingRejectsNonfiniteValuesAndOversizedEscapedPayloads()
    {
        Assert.Throws<InvalidDataException>(() => HmiMqttPayloadCodec.EncodeCommand(HmiValue.From(double.NaN), DateTimeOffset.UnixEpoch));
        Assert.Throws<InvalidDataException>(() => HmiMqttPayloadCodec.EncodeCommand(HmiValue.From(new string('\u0001', 4096)), DateTimeOffset.UnixEpoch));
    }
}
