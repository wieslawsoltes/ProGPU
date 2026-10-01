using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace ProGPU.Hmi.Tests;

/// <summary>A real, locally hosted OPC Foundation server with one writable test variable.</summary>
internal sealed class OpcUaTestServer : StandardServer
{
    internal const string PlantNamespace = "urn:progpu:hmi:test";

    internal static async Task<ApplicationConfiguration> CreateConfigurationAsync(string directory, string endpoint, bool encrypted, ITelemetryContext telemetry, CancellationToken token)
    {
        // Configure the server address before certificate creation so localhost is present in its identity.
        var configuration = new ApplicationConfiguration(telemetry)
        {
            ApplicationName = "ProGPU HMI loopback server",
            ApplicationUri = "urn:progpu:hmi:test-server:" + Guid.NewGuid().ToString("N"),
            ProductUri = "urn:progpu:hmi:test-server",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificates = new CertificateIdentifierCollection
                {
                    new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "own"),
                        SubjectName = "CN=ProGPU HMI loopback server", CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType
                    }
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "issuers") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "rejected") },
                AutoAcceptUntrustedCertificates = false, RejectSHA1SignedCertificates = true, MinimumCertificateKeySize = 2048, AddAppCertToTrustedStore = false
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 10000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = new StringCollection { endpoint },
                SecurityPolicies = new ServerSecurityPolicyCollection
                {
                    new ServerSecurityPolicy
                    {
                        SecurityMode = encrypted ? MessageSecurityMode.SignAndEncrypt : MessageSecurityMode.None,
                        SecurityPolicyUri = encrypted ? SecurityPolicies.Basic256Sha256 : SecurityPolicies.None
                    }
                },
                UserTokenPolicies = new UserTokenPolicyCollection { new UserTokenPolicy(UserTokenType.Anonymous) }
            }
        };
        await configuration.ValidateAsync(ApplicationType.Server, token);
        var application = new ApplicationInstance(telemetry) { ApplicationConfiguration = configuration };
        if (!await application.CheckApplicationInstanceCertificatesAsync(true, ct: token))
            throw new InvalidOperationException("Test server certificate provisioning failed.");
        return configuration;
    }

    protected override ServerProperties LoadServerProperties() => new()
    {
        ManufacturerName = "ProGPU tests", ProductName = "HMI loopback", ProductUri = PlantNamespace,
        SoftwareVersion = "1.0", BuildNumber = "1", BuildDate = DateTime.UtcNow
    };
    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        => new(server, configuration, null, new PlantNodeManager(server, configuration));

    private sealed class PlantNodeManager : CustomNodeManager2
    {
        public PlantNodeManager(IServerInternal server, ApplicationConfiguration configuration) : base(server, configuration, PlantNamespace) { }
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
