using System.Net;
using System.Net.Sockets;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using ProGPU.Hmi.OpcUa;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class OpcUaLoopbackTests
{
    [Fact]
    public async Task RealServerReadsWritesBrowsesAndRejectsCommandsFromOldSessions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        string directory = Path.Combine(Path.GetTempPath(), "progpu-opcua-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var telemetry = DefaultTelemetry.Create(_ => { });
        var server = new TestServer();
        bool started = false;
        try
        {
            var config = await HmiOpcUaApplication.CreateAsync(Path.Combine(directory, "server"), token);
            config.ApplicationType = ApplicationType.Server;
            config.ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = new StringCollection { $"opc.tcp://localhost:{port}/hmi-test" },
                SecurityPolicies = new ServerSecurityPolicyCollection
                {
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None }
                },
                UserTokenPolicies = new UserTokenPolicyCollection { new UserTokenPolicy(UserTokenType.Anonymous) }
            };
            await config.ValidateAsync(ApplicationType.Server, token);
            var app = new ApplicationInstance(telemetry) { ApplicationConfiguration = config };
            await app.StartAsync(server, token); started = true;
            var profile = new HmiConnectionProfile
            {
                Protocol = HmiConnectionProtocol.OpcUa, Host = "localhost", Port = port, TimeoutMilliseconds = 10000, AllowUnsecuredTransport = true,
                OpcUa = new HmiOpcUaSettings { SecurityMode = HmiOpcUaSecurityMode.None, EndpointPath = "/hmi-test", ReadBatchSize = 1 },
                Mappings = [new HmiIoMapping { Tag = "Pressure", Writable = true, OpcUa = new HmiOpcUaAddress { NamespaceUri = "urn:progpu:hmi:test", Identifier = "s=Pressure" } }]
            };
            await using var connection = new HmiOpcUaConnection(profile, ct => HmiOpcUaApplication.CreateAsync(Path.Combine(directory, "client"), ct));
            await connection.ConnectAsync(token);
            Assert.True(connection.IsConnected);
            long originalGeneration = connection.ConnectionGeneration;
            var values = await connection.ReadAsync(token);
            Assert.Equal(42d, values["Pressure"].Value.Number);
            Assert.Equal(HmiQuality.Good, values["Pressure"].Quality);
            var nodes = await connection.BrowseAsync("urn:progpu:hmi:test", "s=Plant", 1, token);
            Assert.Single(nodes.Nodes);
            Assert.Equal("s=Pressure", nodes.Nodes[0].Identifier);
            Assert.Equal("urn:progpu:hmi:test", nodes.Nodes[0].NamespaceUri);
            var result = await connection.WriteAsync("Pressure", HmiValue.From(47d), originalGeneration, token);
            Assert.Equal(HmiWriteDisposition.DeviceAcknowledged, result.Disposition);
            var feedback = await connection.ReadAsync(token);
            Assert.Equal(47d, feedback["Pressure"].Value.Number);
            await connection.DisconnectAsync(token);
            await connection.ConnectAsync(token);
            Assert.NotEqual(originalGeneration, connection.ConnectionGeneration);
            var stale = await connection.WriteAsync("Pressure", HmiValue.From(99d), originalGeneration, token);
            Assert.Equal(HmiWriteDisposition.NotSent, stale.Disposition);
            var unchanged = await connection.ReadAsync(token);
            Assert.Equal(47d, unchanged["Pressure"].Value.Number);
        }
        finally
        {
            if (started) await server.StopAsync(CancellationToken.None);
            server.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class TestServer : StandardServer
    {
        protected override ServerProperties LoadServerProperties() => new()
        {
            ManufacturerName = "ProGPU tests", ProductName = "HMI loopback", ProductUri = "urn:progpu:hmi:test",
            SoftwareVersion = "1.0", BuildNumber = "1", BuildDate = DateTime.UtcNow
        };
        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
            => new(server, configuration, null, new TestNodeManager(server, configuration));
    }
    private sealed class TestNodeManager : CustomNodeManager2
    {
        public TestNodeManager(IServerInternal server, ApplicationConfiguration configuration) : base(server, configuration, "urn:progpu:hmi:test") { }
        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            var folder = new FolderState(null)
            {
                NodeId = new NodeId("Plant", NamespaceIndex), BrowseName = new QualifiedName("Plant", NamespaceIndex), DisplayName = "Plant",
                TypeDefinitionId = ObjectTypeIds.FolderType, ReferenceTypeId = ReferenceTypeIds.Organizes
            };
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references)) externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));
            folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            var value = new BaseDataVariableState(folder)
            {
                NodeId = new NodeId("Pressure", NamespaceIndex), BrowseName = new QualifiedName("Pressure", NamespaceIndex), DisplayName = "Pressure",
                ReferenceTypeId = ReferenceTypeIds.HasComponent, TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                DataType = DataTypeIds.Double, ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentReadOrWrite, UserAccessLevel = AccessLevels.CurrentReadOrWrite,
                Value = 42d, StatusCode = StatusCodes.Good, Timestamp = DateTime.UtcNow
            };
            folder.AddChild(value);
            AddPredefinedNode(SystemContext, folder);
        }
    }
}
