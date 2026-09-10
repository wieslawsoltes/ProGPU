using ACadSharp;

namespace ProGPU.CAD;

/// <summary>
/// File container used to read or write a CAD document.
/// </summary>
public enum CadDocumentFormat
{
    Auto = 0,
    Dxf = 1,
    Dwg = 2
}

/// <summary>
/// Describes upstream format support separately from ProGPU certification.
/// </summary>
public readonly record struct CadFormatCapabilities(
    bool CanRead,
    bool CanWrite,
    bool IsWriteCertified);

public static class CadFormatSupport
{
    public static CadFormatCapabilities GetCapabilities(
        CadDocumentFormat format,
        ACadVersion version)
    {
        return format switch
        {
            CadDocumentFormat.Dxf => new CadFormatCapabilities(
                CanRead: version >= ACadVersion.AC1009,
                CanWrite: version >= ACadVersion.AC1012,
                IsWriteCertified: false),
            CadDocumentFormat.Dwg => new CadFormatCapabilities(
                CanRead: version >= ACadVersion.AC1014,
                CanWrite: version is >= ACadVersion.AC1014 and not ACadVersion.AC1021,
                IsWriteCertified: false),
            _ => new CadFormatCapabilities(false, false, false)
        };
    }
}
