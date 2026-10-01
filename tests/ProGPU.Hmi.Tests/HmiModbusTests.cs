using ProGPU.Hmi.Modbus;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiModbusTests
{
    public static IEnumerable<object[]> Encodings()
    {
        foreach (var encoding in Enum.GetValues<HmiRegisterEncoding>().Where(e => e != HmiRegisterEncoding.Boolean))
            foreach (var order in Enum.GetValues<HmiRegisterOrder>()) yield return [encoding, order];
    }
    [Theory, MemberData(nameof(Encodings))]
    public void RegisterRepresentationAndEngineeringScaleRoundTrip(HmiRegisterEncoding encoding, HmiRegisterOrder order)
    {
        var mapping = new HmiIoMapping { Encoding = encoding, Order = order, Scale = 0.25, Offset = -10 };
        var bytes = HmiRegisterCodec.Encode(40, mapping);
        Assert.Equal(mapping.RegisterCount * 2, bytes.Length);
        Assert.Equal(40, HmiRegisterCodec.Decode(bytes, mapping));
    }
    [Fact]
    public void FloatWireBytesAreIndependentOfHostEndianness()
    {
        var mapping = new HmiIoMapping { Encoding = HmiRegisterEncoding.Float32 };
        Assert.Equal(new byte[] { 0x3f, 0x80, 0, 0 }, HmiRegisterCodec.Encode(1, mapping));
        mapping.Order = HmiRegisterOrder.SwapWords;
        Assert.Equal(new byte[] { 0, 0, 0x3f, 0x80 }, HmiRegisterCodec.Encode(1, mapping));
    }
    [Fact]
    public void OverflowRoundingAndNaNAreRejected()
    {
        var mapping = new HmiIoMapping();
        Assert.Throws<InvalidDataException>(() => HmiRegisterCodec.Encode(65536, mapping));
        Assert.Throws<InvalidDataException>(() => HmiRegisterCodec.Encode(0.5, mapping));
        Assert.Throws<InvalidDataException>(() => HmiRegisterCodec.Encode(double.NaN, mapping));
        mapping.Encoding = HmiRegisterEncoding.Float32;
        Assert.Throws<InvalidDataException>(() => HmiRegisterCodec.Decode(new byte[] { 0x7f, 0xc0, 0, 0 }, mapping));
    }
    private static HmiConnectionProfile Profile(int port) => new()
    {
        Id = "test", Host = "127.0.0.1", Port = port, AllowUnsecuredTransport = true, TimeoutMilliseconds = 1000,
        Mappings = [new() { Tag = "value", Writable = true }]
    };
    [Fact]
    public async Task FragmentedResponsesAndAdjacentMappingsUseStrictRealTcp()
    {
        await using var server = new ModbusLoopbackServer { FragmentReplies = true };
        server.Registers[0] = 12; server.Registers[1] = 34; server.Registers[9] = 56;
        var profile = Profile(server.Port);
        profile.Mappings.Add(new() { Tag = "adjacent", Address = 1 }); profile.Mappings.Add(new() { Tag = "separate", Address = 9 });
        await using var connection = new HmiModbusConnection(profile);
        await connection.ConnectAsync(default);
        var data = await connection.ReadAsync(default);
        Assert.Equal(12d, data["value"].Value.Number); Assert.Equal(34d, data["adjacent"].Value.Number); Assert.Equal(56d, data["separate"].Value.Number);
        Assert.Equal(2, server.ReadCount); // Unconfigured address gaps are never read.
    }
    [Theory]
    [InlineData(HmiModbusArea.Coil)] [InlineData(HmiModbusArea.DiscreteInput)]
    public async Task BitAreasDecodeLsbFirst(HmiModbusArea area)
    {
        await using var server = new ModbusLoopbackServer(); server.Coils[7] = true;
        var profile = Profile(server.Port);
        profile.Mappings = [new() { Tag = "bit", Type = HmiTagType.Boolean, Area = area, Encoding = HmiRegisterEncoding.Boolean, Address = 7 }];
        await using var connection = new HmiModbusConnection(profile); await connection.ConnectAsync(default);
        Assert.True((await connection.ReadAsync(default))["bit"].Value.Boolean);
    }
    [Fact]
    public async Task CoilAndMultiRegisterWritesUseExactAcknowledgements()
    {
        await using var server = new ModbusLoopbackServer();
        var profile = Profile(server.Port);
        profile.Mappings.Add(new() { Tag = "float", Address = 4, Encoding = HmiRegisterEncoding.Float32, Writable = true });
        profile.Mappings.Add(new() { Tag = "coil", Type = HmiTagType.Boolean, Area = HmiModbusArea.Coil, Encoding = HmiRegisterEncoding.Boolean, Writable = true });
        await using var connection = new HmiModbusConnection(profile); await connection.ConnectAsync(default);
        Assert.Equal(HmiWriteDisposition.DeviceAcknowledged, (await connection.WriteAsync("value", HmiValue.From(42d), default)).Disposition);
        Assert.Equal(HmiWriteDisposition.DeviceAcknowledged, (await connection.WriteAsync("float", HmiValue.From(1d), default)).Disposition);
        Assert.Equal(HmiWriteDisposition.DeviceAcknowledged, (await connection.WriteAsync("coil", HmiValue.From(true), default)).Disposition);
        Assert.Equal(42, server.Registers[0]); Assert.Equal(0x3f80, server.Registers[4]); Assert.True(server.Coils[0]);
    }
    [Fact]
    public async Task LostWriteReplyIsIndeterminateAndNeverRetried()
    {
        await using var server = new ModbusLoopbackServer { CloseAfterWrite = true };
        await using var connection = new HmiModbusConnection(Profile(server.Port)); await connection.ConnectAsync(default);
        var result = await connection.WriteAsync("value", HmiValue.From(99d), default);
        Assert.Equal(HmiWriteDisposition.Indeterminate, result.Disposition);
        Assert.Equal(1, server.WriteCount); Assert.Equal(99, server.Registers[0]); Assert.False(connection.IsConnected);
    }
    [Fact]
    public async Task DeviceExceptionIsNotMistakenForAnAcknowledgement()
    {
        await using var server = new ModbusLoopbackServer { ExceptionCode = 2 };
        await using var connection = new HmiModbusConnection(Profile(server.Port)); await connection.ConnectAsync(default);
        Assert.Equal(HmiWriteDisposition.Rejected, (await connection.WriteAsync("value", HmiValue.From(5d), default)).Disposition);
        Assert.Equal(0, server.Registers[0]);
        await Assert.ThrowsAsync<HmiModbusException>(async () => await connection.ReadAsync(default));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task InvalidMbapPoisonedStreamIsRetired(bool wrongTransaction)
    {
        await using var server = new ModbusLoopbackServer { WrongTransaction = wrongTransaction, OversizedReply = !wrongTransaction };
        await using var connection = new HmiModbusConnection(Profile(server.Port)); await connection.ConnectAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await connection.ReadAsync(default));
        Assert.False(connection.IsConnected);
    }
    [Fact]
    public async Task ReadCancellationClosesTheUnpairedStream()
    {
        await using var server = new ModbusLoopbackServer { SuppressReplies = true };
        await using var connection = new HmiModbusConnection(Profile(server.Port)); await connection.ConnectAsync(default);
        using var cancellation = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connection.ReadAsync(cancellation.Token));
        Assert.False(connection.IsConnected);
    }
    [Fact]
    public async Task PlaintextRequiresExplicitPermissionBeforeConnecting()
    {
        var profile = Profile(1); profile.AllowUnsecuredTransport = false;
        await using var connection = new HmiModbusConnection(profile);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.ConnectAsync(default));
    }
}
