using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

// Test-only immutable font/notice dependency. The workflow and producer consume
// this same embedded manifest; no ambient Windows font is admitted as fallback.
internal sealed record ReferenceFontPin(string Repository, string Commit, string FamilyName,
    string FontPath, string FontFileName, long FontLength, string FontSha256,
    string LicensePath, string LicenseFileName, long LicenseLength, string LicenseSha256)
{
    internal static ReferenceFontPin Load()
    {
        using Stream stream = typeof(ReferenceFontPin).Assembly.GetManifestResourceStream("rtl-font.json")
            ?? throw new InvalidOperationException("Missing original RTL font pin.");
        return JsonSerializer.Deserialize<ReferenceFontPin>(stream)
            ?? throw new InvalidOperationException("Invalid original RTL font pin.");
    }

    internal void Verify(string directory)
    {
        VerifyPayload(File.ReadAllBytes(Path.Combine(directory, FontFileName)), FontLength, FontSha256);
        VerifyPayload(File.ReadAllBytes(Path.Combine(directory, LicenseFileName)), LicenseLength, LicenseSha256);
    }

    internal static void VerifyPayload(ReadOnlySpan<byte> bytes, long length, string sha256)
    {
        if (bytes.Length != length || Convert.ToHexString(SHA256.HashData(bytes)) != sha256)
            throw new InvalidOperationException("Original RTL font or notice does not match its immutable pin.");
    }
}
