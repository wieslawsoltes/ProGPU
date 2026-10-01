using Opc.Ua;
using Opc.Ua.Configuration;

namespace ProGPU.Hmi.OpcUa;

/// <summary>Creates a local application identity; never accepts or installs a remote server certificate automatically.</summary>
public static class HmiOpcUaApplication
{
    internal static ITelemetryContext Telemetry { get; } = DefaultTelemetry.Create(_ => { });
    private static readonly SemaphoreSlim CertificateGate = new(1, 1);
    public static string DefaultPkiDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProGPU", "Hmi", "OpcUa", "pki");

    public static async ValueTask<ApplicationConfiguration> CreateAsync(string? pkiDirectory = null, CancellationToken cancellationToken = default)
    {
        string directory = Path.GetFullPath(pkiDirectory ?? DefaultPkiDirectory);
        var configuration = new ApplicationConfiguration(Telemetry)
        {
            ApplicationName = "ProGPU HMI",
            ApplicationUri = "urn:" + System.Net.Dns.GetHostName() + ":ProGPU:Hmi",
            ProductUri = "urn:ProGPU:Hmi",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificates = new CertificateIdentifierCollection
                {
                    new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "own"),
                        SubjectName = "CN=ProGPU HMI", CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType
                    }
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "issuers") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(directory, "rejected") },
                AutoAcceptUntrustedCertificates = false,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048,
                AddAppCertToTrustedStore = false
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 10000, MaxStringLength = 16384, MaxByteStringLength = 1048576,
                MaxArrayLength = 65536, MaxMessageSize = 4194304, MaxBufferSize = 65536, ChannelLifetime = 300000, SecurityTokenLifetime = 60000
            },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
        };
        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken).ConfigureAwait(false);
        await CertificateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var application = new ApplicationInstance(Telemetry) { ApplicationConfiguration = configuration };
            if (!await application.CheckApplicationInstanceCertificatesAsync(true, ct: cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The OPC UA application certificate is missing or invalid.");
        }
        finally { CertificateGate.Release(); }
        return configuration;
    }
}
