namespace ProGPU.Hmi;

public enum HmiOpcUaSecurityMode { SignAndEncrypt, None }
public enum HmiOpcUaSecurityPolicy { Basic256Sha256, Aes128Sha256RsaOaep, Aes256Sha256RsaPss }
public enum HmiOpcUaDataType { Boolean, SByte, Byte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double, String }

/// <summary>Inert OPC UA endpoint settings. Private keys, passwords and certificate trust are host-owned.</summary>
public sealed class HmiOpcUaSettings
{
    public string EndpointPath { get; set; } = "/";
    public HmiOpcUaSecurityMode SecurityMode { get; set; } = HmiOpcUaSecurityMode.SignAndEncrypt;
    public HmiOpcUaSecurityPolicy SecurityPolicy { get; set; } = HmiOpcUaSecurityPolicy.Basic256Sha256;
    public int ReadBatchSize { get; set; } = 128;
    public int SessionTimeoutMilliseconds { get; set; } = 60000;
    public HmiOpcUaSettings Copy() => (HmiOpcUaSettings)MemberwiseClone();
    public void Validate()
    {
        if (EndpointPath == null || EndpointPath.Length > 1024 || !EndpointPath.StartsWith('/') ||
            EndpointPath.Contains('?') || EndpointPath.Contains('#') || EndpointPath.Contains('\\') || EndpointPath.Any(char.IsControl))
            throw new InvalidDataException("OPC UA endpoint path must be an absolute path without query, fragment or control characters.");
        if (!Enum.IsDefined(SecurityMode) || !Enum.IsDefined(SecurityPolicy) || ReadBatchSize is < 1 or > 512 || SessionTimeoutMilliseconds is < 10000 or > 3600000)
            throw new InvalidDataException("Invalid OPC UA security, batch or session settings.");
    }
}

/// <summary>Stable namespace URI plus a namespace-local i=, s=, g= or b= identifier; never persist a server's volatile namespace index.</summary>
public sealed class HmiOpcUaAddress
{
    public string NamespaceUri { get; set; } = "";
    public string Identifier { get; set; } = "i=2259";
    public HmiOpcUaDataType DataType { get; set; } = HmiOpcUaDataType.Double;
    public HmiOpcUaAddress Copy() => (HmiOpcUaAddress)MemberwiseClone();
    public void Validate(HmiTagType tagType)
    {
        if (NamespaceUri == null || NamespaceUri.Length > 4096 || NamespaceUri.Any(char.IsControl) ||
            NamespaceUri.Length > 0 && !Uri.TryCreate(NamespaceUri, UriKind.Absolute, out _))
            throw new InvalidDataException("OPC UA namespace must be an absolute namespace URI or empty for namespace zero.");
        if (Identifier == null || Identifier.Length is < 3 or > 4096 || Identifier[1] != '=' || !"isgb".Contains(Identifier[0]) || Identifier.Any(char.IsControl))
            throw new InvalidDataException("Use a namespace-local OPC UA identifier: i=number, s=text, g=GUID or b=base64.");
        if (!Enum.IsDefined(DataType) || tagType != (DataType == HmiOpcUaDataType.Boolean ? HmiTagType.Boolean : DataType == HmiOpcUaDataType.String ? HmiTagType.Text : HmiTagType.Number))
            throw new InvalidDataException("OPC UA scalar type must match the mapped HMI tag type.");
        if (Identifier[0] == 'i' && !uint.TryParse(Identifier.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("Invalid OPC UA numeric identifier.");
        if (Identifier[0] == 'g' && !Guid.TryParseExact(Identifier.AsSpan(2), "D", out _)) throw new InvalidDataException("Invalid OPC UA GUID identifier.");
        if (Identifier[0] == 'b')
        {
            Span<byte> bytes = stackalloc byte[3072];
            if (!Convert.TryFromBase64String(Identifier[2..], bytes, out int count) || count == 0) throw new InvalidDataException("Invalid OPC UA opaque identifier.");
        }
    }
}
