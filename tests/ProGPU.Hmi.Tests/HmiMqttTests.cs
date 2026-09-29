using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Server;
using ProGPU.Hmi.Mqtt;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiMqttTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    [Theory]
    [InlineData("12.5", HmiTagType.Number)] [InlineData("true", HmiTagType.Boolean)] [InlineData("\"ready\"", HmiTagType.Text)]
    public void TypedScalarsDecode(string json, HmiTagType type)
    {
        var sample = HmiMqttPayloadCodec.Decode(Encoding.UTF8.GetBytes(json), type, false, Now);
        Assert.Equal(type, sample.Value.Type); Assert.Equal(Now, sample.Timestamp);
    }
    [Fact]
    public void RetainedValuesRequireOriginalSourceTime()
    {
        Assert.Throws<InvalidDataException>(() => HmiMqttPayloadCodec.Decode("12"u8.ToArray(), HmiTagType.Number, true, Now));
        var sample = HmiMqttPayloadCodec.Decode("{\"value\":12,\"timestamp\":\"2025-12-31T23:00:00Z\",\"quality\":\"Good\"}"u8.ToArray(), HmiTagType.Number, true, Now);
        Assert.Equal(Now.AddHours(-1), sample.Timestamp);
    }
    [Theory]
    [InlineData("{\"value\":1,\"value\":2}")]
    [InlineData("{\"value\":1,\"quality\":\"99\"}")]
    [InlineData("{\"value\":1,\"timestamp\":\"2027-01-01T00:00:00Z\"}")]
    [InlineData("{\"value\":\"bad\"}")]
    [InlineData("{\"value\":1,\"script\":\"ignored?\"}")]
    public void UntrustedPayloadsAreRejected(string json) => Assert.Throws<InvalidDataException>(() => HmiMqttPayloadCodec.Decode(Encoding.UTF8.GetBytes(json), HmiTagType.Number, false, Now));
    private static HmiConnectionProfile Profile(int port) => new()
    {
        Protocol = HmiConnectionProtocol.Mqtt, Host = "127.0.0.1", Port = port, AllowUnsecuredTransport = true,
        Mappings = [new() { Tag = "pressure", Topic = "plant/pressure", CommandTopic = "plant/pressure/set", Writable = true }]
    };
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static MqttServer Broker(int port) => new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder()
        .WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointBoundIPV6Address(IPAddress.None).WithDefaultEndpointPort(port).Build());
    private static async Task<IReadOnlyDictionary<string, HmiTagSample>> WaitForSample(HmiMqttConnection connection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var batch = await connection.ReadAsync(timeout.Token);
            if (batch.Count > 0) return batch;
            await Task.Delay(10, timeout.Token);
        }
    }
    [Fact]
    public async Task RealBrokerDeliversTypedTelemetryAndNonRetainedCommands()
    {
        int port = Port(); using var server = Broker(port); await server.StartAsync();
        try
        {
            using var publisher = new MqttClientFactory().CreateMqttClient();
            await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build());
            var command = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            publisher.ApplicationMessageReceivedAsync += args => { command.TrySetResult(args.ApplicationMessage); return Task.CompletedTask; };
            await publisher.SubscribeAsync(new MqttClientFactory().CreateSubscribeOptionsBuilder().WithTopicFilter("plant/pressure/set").Build());
            await using var connection = new HmiMqttConnection(Profile(port)); await connection.ConnectAsync(default);
            await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("plant/pressure").WithPayload("4.5").WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).Build());
            Assert.Equal(4.5, (await WaitForSample(connection))["pressure"].Value.Number);
            var result = await connection.WriteAsync("pressure", HmiValue.From(6d), default);
            Assert.Equal(HmiWriteDisposition.BrokerAcknowledged, result.Disposition);
            var received = await command.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(received.Retain);
            // No optimistic process feedback update is manufactured by a publish acknowledgement.
            Assert.Empty(await connection.ReadAsync(default));
            await connection.DisconnectAsync(default);
            Assert.Equal(HmiWriteDisposition.NotSent, (await connection.WriteAsync("pressure", HmiValue.From(7d), default)).Disposition);
        }
        finally { await server.StopAsync(); }
    }
    [Fact]
    public async Task RetainedUnstampedPayloadIsQuarantinedThroughRealSubscription()
    {
        int port = Port(); using var server = Broker(port); await server.StartAsync();
        try
        {
            using var publisher = new MqttClientFactory().CreateMqttClient();
            await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build());
            await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("plant/pressure").WithPayload("88").WithRetainFlag().WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).Build());
            await using var connection = new HmiMqttConnection(Profile(port)); await connection.ConnectAsync(default);
            Assert.Equal(HmiQuality.Bad, (await WaitForSample(connection))["pressure"].Quality);
            Assert.True(connection.RejectedMessages > 0);
        }
        finally { await server.StopAsync(); }
    }
    [Fact]
    public async Task CredentialsNeverTravelOverPlaintextEvenWhenTcpIsAllowed()
    {
        const string user = "credential-canary-user-731db5";
        const string password = "credential-canary-password-64a9fc";
        await using var connection = new HmiMqttConnection(Profile(1), _ => ValueTask.FromResult<HmiMqttCredentials?>(new(user, password)));
        var error = await Assert.ThrowsAsync<IOException>(async () => await connection.ConnectAsync(default));
        // Check actual credential canaries, including inner exceptions, not a common English word.
        Assert.DoesNotContain(user, error.ToString());
        Assert.DoesNotContain(password, error.ToString());
        Assert.Contains("TLS", error.Message);
    }
}
