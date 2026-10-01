using Opc.Ua;
using ProGPU.Hmi.OpcUa;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class OpcUaCodecTests
{
    private static HmiIoMapping Mapping(HmiOpcUaDataType type) => new()
    {
        Tag = "Value", Type = type == HmiOpcUaDataType.Boolean ? HmiTagType.Boolean : type == HmiOpcUaDataType.String ? HmiTagType.Text : HmiTagType.Number,
        OpcUa = new HmiOpcUaAddress { Identifier = "s=Value", NamespaceUri = "urn:progpu:test", DataType = type }
    };
    [Theory]
    [InlineData(HmiOpcUaDataType.SByte)] [InlineData(HmiOpcUaDataType.Byte)] [InlineData(HmiOpcUaDataType.Int16)] [InlineData(HmiOpcUaDataType.UInt16)]
    [InlineData(HmiOpcUaDataType.Int32)] [InlineData(HmiOpcUaDataType.UInt32)] [InlineData(HmiOpcUaDataType.Int64)] [InlineData(HmiOpcUaDataType.UInt64)]
    [InlineData(HmiOpcUaDataType.Float)] [InlineData(HmiOpcUaDataType.Double)]
    public void EveryNumericEncodingRetainsItsExactClrType(HmiOpcUaDataType type)
    {
        var mapping = Mapping(type);
        var encoded = HmiOpcUaCodec.Encode(mapping, HmiValue.From(42d));
        Assert.Equal(HmiOpcUaCodec.ClrType(type), encoded.GetType());
        Assert.Equal(42d, HmiOpcUaCodec.DecodeScalar(mapping, encoded).Number);
    }
    [Fact]
    public void BooleanAndStringDoNotUseNumericCoercion()
    {
        Assert.True(HmiOpcUaCodec.DecodeScalar(Mapping(HmiOpcUaDataType.Boolean), true).Boolean);
        Assert.Equal("Ready", HmiOpcUaCodec.DecodeScalar(Mapping(HmiOpcUaDataType.String), "Ready").Text);
        Assert.Throws<InvalidDataException>(() => HmiOpcUaCodec.DecodeScalar(Mapping(HmiOpcUaDataType.Boolean), 1));
        Assert.Throws<InvalidDataException>(() => HmiOpcUaCodec.DecodeScalar(Mapping(HmiOpcUaDataType.Double), 42));
        Assert.Throws<InvalidDataException>(() => HmiOpcUaCodec.DecodeScalar(Mapping(HmiOpcUaDataType.Double), new[] { 42d }));
    }
    [Fact]
    public void EngineeringScalingRoundTripsAndRejectsFractionalIntegerWrites()
    {
        var mapping = Mapping(HmiOpcUaDataType.Int16); mapping.Scale = .25; mapping.Offset = -5;
        Assert.Equal(10d, HmiOpcUaCodec.DecodeScalar(mapping, (short)60).Number);
        Assert.Equal((short)60, Assert.IsType<short>(HmiOpcUaCodec.Encode(mapping, HmiValue.From(10d))));
        Assert.Throws<InvalidDataException>(() => HmiOpcUaCodec.Encode(mapping, HmiValue.From(10.1)));
    }
    [Fact]
    public void NonfiniteAndLossyValuesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => HmiOpcUaCodec.DecodeScalar(Mapping(HmiOpcUaDataType.Double), double.NaN));
        Assert.Throws<InvalidDataException>(() => HmiOpcUaCodec.DecodeScalar(Mapping(HmiOpcUaDataType.UInt64), ulong.MaxValue));
        Assert.Throws<InvalidDataException>(() => HmiOpcUaCodec.Encode(Mapping(HmiOpcUaDataType.Int64), HmiValue.From(9007199254740992d)));
        Assert.Throws<OverflowException>(() => HmiOpcUaCodec.Encode(Mapping(HmiOpcUaDataType.UInt16), HmiValue.From(-1d)));
        Assert.Throws<InvalidDataException>(() => HmiOpcUaCodec.Encode(Mapping(HmiOpcUaDataType.Float), HmiValue.From(double.MaxValue)));
    }
    [Fact]
    public void SourceQualityAndTimestampAreNotReplacedByReceiptTime()
    {
        var now = DateTimeOffset.UtcNow;
        var value = new DataValue(new Variant(42d)) { SourceTimestamp = now.AddSeconds(-5).UtcDateTime, StatusCode = StatusCodes.Good };
        var sample = HmiOpcUaCodec.Decode(Mapping(HmiOpcUaDataType.Double), value, now);
        Assert.Equal(now.AddSeconds(-5), sample.Timestamp);
        Assert.Equal(HmiQuality.Good, sample.Quality);
        value.SourceTimestamp = DateTime.MinValue;
        Assert.Equal(HmiQuality.Uncertain, HmiOpcUaCodec.Decode(Mapping(HmiOpcUaDataType.Double), value, now).Quality);
        value.Value = "wrong type";
        Assert.Equal(HmiQuality.Bad, HmiOpcUaCodec.Decode(Mapping(HmiOpcUaDataType.Double), value, now).Quality);
        value.Value = 42d; value.SourceTimestamp = now.AddMinutes(1).UtcDateTime;
        Assert.Equal(HmiQuality.Bad, HmiOpcUaCodec.Decode(Mapping(HmiOpcUaDataType.Double), value, now).Quality);
        value.SourceTimestamp = now.UtcDateTime; value.StatusCode = StatusCodes.BadNoCommunication;
        Assert.Equal(HmiQuality.Bad, HmiOpcUaCodec.Decode(Mapping(HmiOpcUaDataType.Double), value, now).Quality);
    }
    [Fact]
    public void NamespaceUrisSurviveIndexReorderingAndRejectAbsentNamespaces()
    {
        var mapping = Mapping(HmiOpcUaDataType.Double);
        var first = new NamespaceTable(); first.Append("urn:other"); first.Append("urn:progpu:test");
        var second = new NamespaceTable(); second.Append("urn:progpu:test"); second.Append("urn:other");
        Assert.NotEqual(HmiOpcUaCodec.Resolve(mapping.OpcUa, first).NamespaceIndex, HmiOpcUaCodec.Resolve(mapping.OpcUa, second).NamespaceIndex);
        Assert.Equal("Value", HmiOpcUaCodec.Resolve(mapping.OpcUa, second).Identifier);
        Assert.Throws<InvalidDataException>(() => HmiOpcUaCodec.Resolve(mapping.OpcUa, new NamespaceTable()));
    }
    [Theory]
    [InlineData("ns=2;s=Value")] [InlineData("i=-1")] [InlineData("i=4294967296")] [InlineData("g=invalid")] [InlineData("b=%%%")]
    public void InvalidOrIndexBasedNodeIdentifiersAreRejected(string identifier)
    {
        var address = Mapping(HmiOpcUaDataType.Double).OpcUa; address.Identifier = identifier;
        Assert.Throws<InvalidDataException>(() => address.Validate(HmiTagType.Number));
    }
    [Fact]
    public void SecuritySelectionNeverRedirectsOrDowngrades()
    {
        var profile = new HmiConnectionProfile { Protocol = HmiConnectionProtocol.OpcUa, Host = "localhost", Port = 4840 };
        var insecure = new EndpointDescription { EndpointUrl = "opc.tcp://localhost:4840/", SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None };
        Assert.Throws<System.Security.Authentication.AuthenticationException>(() => HmiOpcUaConnection.SelectEndpoint(profile, [insecure]));
        var secure = new EndpointDescription { EndpointUrl = insecure.EndpointUrl, SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 };
        Assert.Same(secure, HmiOpcUaConnection.SelectEndpoint(profile, [secure]));
        secure.EndpointUrl = "opc.tcp://other-host:4840/";
        Assert.Throws<System.Security.Authentication.AuthenticationException>(() => HmiOpcUaConnection.SelectEndpoint(profile, [secure]));
        profile.OpcUa.SecurityMode = HmiOpcUaSecurityMode.None;
        Assert.Throws<InvalidOperationException>(profile.RequireTransportPermission);
        profile.AllowUnsecuredTransport = true; profile.RequireTransportPermission();
    }
    [Fact]
    public void ProtocolSettingsOwnDetachedCopiesAndRoundTrip()
    {
        var project = HmiDemoProject.Create();
        project.Connections.Add(new HmiConnectionProfile
        {
            Protocol = HmiConnectionProtocol.OpcUa, Port = 4840,
            Mappings = [new HmiIoMapping { Tag = "Tank.Level", OpcUa = new HmiOpcUaAddress { NamespaceUri = "urn:plant", Identifier = "s=Tank.Level" } }]
        });
        var clone = HmiProjectSerializer.Clone(project);
        clone.Connections[0].OpcUa.EndpointPath = "/other";
        clone.Connections[0].Mappings[0].OpcUa.Identifier = "s=other";
        Assert.Equal("/", project.Connections[0].OpcUa.EndpointPath);
        Assert.Equal("s=Tank.Level", project.Connections[0].Mappings[0].OpcUa.Identifier);
        Assert.DoesNotContain(typeof(HmiOpcUaConnection).Assembly.GetReferencedAssemblies(), a => a.Name!.Contains("WinUI", StringComparison.Ordinal));
    }
}
