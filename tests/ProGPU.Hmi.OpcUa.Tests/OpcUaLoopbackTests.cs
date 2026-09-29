using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Opc.Ua;
using Opc.Ua.Configuration;
using ProGPU.Hmi.OpcUa;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class OpcUaLoopbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealServerReadsWritesBrowsesAndRejectsCommandsFromOldSessions(bool encrypted)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        string directory = Path.Combine(Path.GetTempPath(), "progpu-opcua-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var telemetry = DefaultTelemetry.Create(_ => { });
        var server = new OpcUaTestServer();
        bool started = false;
        try
        {
            var serverConfiguration = await OpcUaTestServer.CreateConfigurationAsync(
                Path.Combine(directory, "server"), $"opc.tcp://localhost:{port}/hmi-test", encrypted, telemetry, token);
            var app = new ApplicationInstance(telemetry) { ApplicationConfiguration = serverConfiguration };
            // The pinned SDK's application-level startup has no CancellationToken overload.
            await app.StartAsync(server); started = true;
            token.ThrowIfCancellationRequested();
            var clientConfiguration = await HmiOpcUaApplication.CreateAsync(Path.Combine(directory, "client"), token);
            var profile = new HmiConnectionProfile
            {
                Protocol = HmiConnectionProtocol.OpcUa, Host = "localhost", Port = port, TimeoutMilliseconds = 10000, AllowUnsecuredTransport = !encrypted,
                OpcUa = new HmiOpcUaSettings
                {
                    SecurityMode = encrypted ? HmiOpcUaSecurityMode.SignAndEncrypt : HmiOpcUaSecurityMode.None,
                    EndpointPath = "/hmi-test", ReadBatchSize = 1
                },
                Mappings = [new HmiIoMapping { Tag = "Pressure", Writable = true, OpcUa = new HmiOpcUaAddress { NamespaceUri = OpcUaTestServer.PlantNamespace, Identifier = "s=Pressure" } }]
            };
            await using var connection = new HmiOpcUaConnection(profile, _ => ValueTask.FromResult(clientConfiguration));
            if (encrypted)
            {
                var serverIdentity = await serverConfiguration.SecurityConfiguration.ApplicationCertificates[0].FindAsync(
                    false, serverConfiguration.ApplicationUri, telemetry, token);
                Assert.NotNull(serverIdentity);
                using var publicServerIdentity = X509CertificateLoader.LoadCertificate(serverIdentity.RawData);
                var certificateError = await Assert.ThrowsAsync<ServiceResultException>(() => clientConfiguration.CertificateValidator.ValidateAsync(publicServerIdentity, token));
                Assert.Equal(StatusCodes.BadCertificateUntrusted, certificateError.StatusCode);
                await Assert.ThrowsAsync<IOException>(async () => await connection.ConnectAsync(token));
                Assert.False(connection.IsConnected);
                Assert.False(clientConfiguration.SecurityConfiguration.AutoAcceptUntrustedCertificates);
                // These identities were generated and verified by this test. Trust is explicit and mutual.
                await TrustPublicIdentityAsync(clientConfiguration, serverConfiguration, telemetry, token);
                await TrustPublicIdentityAsync(serverConfiguration, clientConfiguration, telemetry, token);
            }
            await connection.ConnectAsync(token);
            Assert.True(connection.IsConnected);
            long originalGeneration = connection.ConnectionGeneration;
            var values = await connection.ReadAsync(token);
            Assert.Equal(42d, values["Pressure"].Value.Number);
            Assert.Equal(HmiQuality.Good, values["Pressure"].Quality);
            var nodes = await connection.BrowseAsync(OpcUaTestServer.PlantNamespace, "s=Plant", 1, token);
            Assert.Single(nodes.Nodes);
            Assert.Equal("s=Pressure", nodes.Nodes[0].Identifier);
            Assert.Equal(OpcUaTestServer.PlantNamespace, nodes.Nodes[0].NamespaceUri);
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
    private static async Task TrustPublicIdentityAsync(ApplicationConfiguration owner, ApplicationConfiguration peer, ITelemetryContext telemetry, CancellationToken token)
    {
        var peerIdentity = await peer.SecurityConfiguration.ApplicationCertificates[0].FindAsync(false, peer.ApplicationUri, telemetry, token);
        Assert.NotNull(peerIdentity);
        using var publicIdentity = X509CertificateLoader.LoadCertificate(peerIdentity.RawData);
        using (var store = owner.SecurityConfiguration.TrustedPeerCertificates.OpenStore(telemetry))
            await store.AddAsync(publicIdentity, ct: token);
        await owner.CertificateValidator.UpdateAsync(owner.SecurityConfiguration, owner.ApplicationUri, token);
    }
}
