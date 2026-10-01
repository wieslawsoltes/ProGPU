using System.Buffers.Binary;
using System.Security.Cryptography;
using ProGPU.Backend.Dawn;
using Xunit;

namespace ProGPU.Tests;

// Original bounded metadata/reporting controls. Synthetic PE bytes are never
// loaded, and no test creates a Dawn instance, adapter, device or provider.
public sealed class DawnAdapterRequestFailureDiagnosticsTests
{
    [Fact]
    public void AttributionStaysInsideOriginalFailureBranchAndDoesNotLoadOrRetry()
    {
        string source = ReadRepoFile("src", "ProGPU.Backend.Dawn", "DawnGpuContext.Offscreen.cs");
        int start = source.IndexOf("if (state.Status != W.RequestAdapterStatus.Success || state.Adapter == AdapterHandle.Null)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = source.IndexOf("AdapterHandle result = state.Adapter;", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string failure = source[start..end], success = source[..start] + source[end..];
        Assert.Contains("Dawn failed to request an offscreen adapter: {state.Status}. {state.Message}", failure, StringComparison.Ordinal);
        Assert.Contains("DawnAdapterRequestFailureDiagnostics.Attach(failure, options.BackendType,", failure, StringComparison.Ordinal);
        Assert.Contains("forceFallbackAdapter, options.FeatureLevel, options.PowerPreference);", failure, StringComparison.Ordinal);
        Assert.Contains("throw failure;", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("DawnAdapterRequestFailureDiagnostics", success, StringComparison.Ordinal);
        Assert.Contains("ForceFallbackAdapter = forceFallbackAdapter", success, StringComparison.Ordinal);
        Assert.Contains("PowerPreference = W.PowerPreference.HighPerformance", success, StringComparison.Ordinal);

        string diagnostic = ReadRepoFile("src", "ProGPU.Backend.Dawn", "DawnAdapterRequestFailureDiagnostics.cs");
        Assert.Contains("DawnNativeProvider.GetModule()", diagnostic, StringComparison.Ordinal);
        Assert.Contains("GetModuleHandleW(nativeName)", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeLibrary.", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadLibrary", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("FreeLibrary", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestAdapter(", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestDevice(", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("DawnInstanceDescriptor", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsExactRequestAndKeepsNotLoadedDistinctFromMissingOrRequired()
    {
        string snapshot = DawnAdapterRequestFailureDiagnostics.FormatSnapshot(
            "arch=Arm64; backend=D3D12; forceFallbackAdapter=True; featureLevel=Core; powerPreference=HighPerformance",
            "path=C:\\original\\webgpu_dawn.dll; diskPeMachine=0xAA64", ["dxcompiler.dll=not loaded"]);
        Assert.Contains("forceFallbackAdapter=True; featureLevel=Core; powerPreference=HighPerformance", snapshot, StringComparison.Ordinal);
        Assert.Contains("dxcompiler.dll=not loaded", snapshot, StringComparison.Ordinal);
        Assert.Contains("not loaded != missing or required", snapshot, StringComparison.Ordinal);
        Assert.Contains("on-disk identity != mapped-image identity", snapshot, StringComparison.Ordinal);
        Assert.Contains("diskPeMachine=0xAA64", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("dxcompiler.dll=missing", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotBoundsAndSanitizesUntrustedPathAndDiagnosticText()
    {
        string snapshot = DawnAdapterRequestFailureDiagnostics.FormatSnapshot("original request", "path=original\r\n\0" + new string('x', 9000), []);
        Assert.Equal(DawnAdapterRequestFailureDiagnostics.MaximumSnapshotCharacters, snapshot.Length);
        Assert.EndsWith(" [truncated]", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', snapshot); Assert.DoesNotContain('\n', snapshot); Assert.DoesNotContain('\0', snapshot);
        Assert.Contains("not loaded != missing or required", snapshot, StringComparison.Ordinal);
        string exact = DawnAdapterRequestFailureDiagnostics.FormatSnapshot(
            new string('r', DawnAdapterRequestFailureDiagnostics.MaximumSnapshotCharacters - "request: ".Length), "omitted provider", []);
        Assert.Equal(DawnAdapterRequestFailureDiagnostics.MaximumSnapshotCharacters, exact.Length);
        Assert.EndsWith(" [truncated]", exact, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportingPreservesOriginalExceptionCategoryMessageAndNativeEvidence()
    {
        const string original = "Dawn failed to request an offscreen adapter: Unavailable. No supported adapters";
        var failure = new InvalidOperationException(original); failure.Data["original"] = 7;
        using var output = new StringWriter();
        DawnAdapterRequestFailureDiagnostics.AttachSnapshot(failure, "exact diagnostic", output);
        Assert.IsType<InvalidOperationException>(failure); Assert.Equal(original, failure.Message);
        Assert.Null(failure.InnerException); Assert.Equal(7, failure.Data["original"]);
        Assert.Equal("exact diagnostic", failure.Data[DawnAdapterRequestFailureDiagnostics.DataKey]);
        Assert.Equal("[Dawn adapter request diagnostic] exact diagnostic" + Environment.NewLine, output.ToString());
        DawnAdapterRequestFailureDiagnostics.AttachSnapshot(failure, "reporting fault", new FaultingWriter());
        Assert.Equal(original, failure.Message); Assert.Equal(7, failure.Data["original"]);
        Assert.Equal("reporting fault", failure.Data[DawnAdapterRequestFailureDiagnostics.DataKey]);
    }

    [Theory]
    [InlineData(0x8664)]
    [InlineData(0xAA64)]
    [InlineData(0xA641)]
    [InlineData(0xA64E)]
    [InlineData(0x1234)]
    public void ReportsOriginalPeMachineWithoutArchitectureAdmission(int machine)
    {
        using var file = new MemoryStream(PeBytes(128, (ushort)machine));
        Assert.Equal((ushort)machine, DawnAdapterRequestFailureDiagnostics.ReadPeMachine(file));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(251)]
    [InlineData(uint.MaxValue)]
    [InlineData(DawnAdapterRequestFailureDiagnostics.MaximumPeHeaderOffset + 1u)]
    public void RejectsUnboundedOrIncompletePeOffsetsBeforeSeeking(uint offset)
    {
        byte[] bytes = PeBytes(128, 0x8664); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3C), offset);
        using var file = new MemoryStream(bytes);
        Assert.Null(DawnAdapterRequestFailureDiagnostics.ReadPeMachine(file));
        Assert.True(file.Position <= bytes.Length);
    }

    [Fact]
    public void IncompleteDosAndDifferentPeSignatureStayUnavailable()
    {
        using var shortFile = new MemoryStream(new byte[63]);
        Assert.Null(DawnAdapterRequestFailureDiagnostics.ReadPeMachine(shortFile));
        byte[] bytes = PeBytes(128, 0x8664); bytes[128] ^= 1;
        using var invalid = new MemoryStream(bytes);
        Assert.Null(DawnAdapterRequestFailureDiagnostics.ReadPeMachine(invalid));
        bytes = PeBytes(128, 0x8664); bytes[0] ^= 1;
        using var invalidDos = new MemoryStream(bytes);
        Assert.Null(DawnAdapterRequestFailureDiagnostics.ReadPeMachine(invalidDos));
    }

    [Fact]
    public void ProviderHashUsesExactBoundedDiskBytesAndRejectsChangedLength()
    {
        byte[] bytes = PeBytes(128, 0x8664);
        using var file = new MemoryStream(bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), DawnAdapterRequestFailureDiagnostics.HashProviderFile(file, bytes.Length));
        file.Position = 0;
        Assert.Throws<IOException>(() => DawnAdapterRequestFailureDiagnostics.HashProviderFile(file, bytes.Length - 1));
        file.Position = 0;
        Assert.Throws<EndOfStreamException>(() => DawnAdapterRequestFailureDiagnostics.HashProviderFile(file, bytes.Length + 1));
        file.Position = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => DawnAdapterRequestFailureDiagnostics.HashProviderFile(file,
            DawnAdapterRequestFailureDiagnostics.MaximumProviderFileBytes + 1));
        Assert.Equal(0, file.Position);
    }

    private static byte[] PeBytes(int offset, ushort machine)
    {
        byte[] bytes = new byte[256]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x5A4D);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3C), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 4), machine); return bytes;
    }

    private sealed class FaultingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("original diagnostic output fault");
    }

    private static string ReadRepoFile(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"Original repository file not found: {Path.Combine(parts)}.");
    }
}
