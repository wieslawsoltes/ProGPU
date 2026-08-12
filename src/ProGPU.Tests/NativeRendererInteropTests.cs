using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProGPU.Backend.Native;
using Xunit;

namespace Avalonia.ProGpu.UnitTests;

public class NativeRendererInteropTests
{
    [Fact]
    public void PublicRectangleMatchesNativePodLayout()
    {
        Assert.Equal(32, Unsafe.SizeOf<NativeSolidRectangle>());
        Assert.Equal(0, OffsetOf<NativeSolidRectangle>(nameof(NativeSolidRectangle.X)));
        Assert.Equal(4, OffsetOf<NativeSolidRectangle>(nameof(NativeSolidRectangle.Y)));
        Assert.Equal(8, OffsetOf<NativeSolidRectangle>(nameof(NativeSolidRectangle.Width)));
        Assert.Equal(12, OffsetOf<NativeSolidRectangle>(nameof(NativeSolidRectangle.Height)));
        Assert.Equal(16, OffsetOf<NativeSolidRectangle>(nameof(NativeSolidRectangle.Color)));

        var rectangle = new NativeSolidRectangle(
            1,
            2,
            3,
            4,
            new Vector4(0.1f, 0.2f, 0.3f, 0.4f));
        Assert.Equal(3, rectangle.Width);
        Assert.Equal(0.4f, rectangle.Color.W);
    }

    [Fact]
    public void PrivateInteropRecordsMatchNativeAbiOne()
    {
        Assert.Equal(40, Unsafe.SizeOf<NativeMethods.EngineOptions>());
        Assert.Equal(56, Unsafe.SizeOf<NativeMethods.Frame>());
        Assert.Equal(40, Unsafe.SizeOf<NativeMethods.FrameMetrics>());
        Assert.Equal(56, Unsafe.SizeOf<NativeMethods.AnalyticFrame>());
        Assert.Equal(48, Unsafe.SizeOf<NativeMethods.AnalyticFrameMetrics>());
        Assert.Equal(72, Unsafe.SizeOf<NativeAnalyticPrimitive>());
        Assert.Equal(56, Unsafe.SizeOf<NativeMethods.GeometryFrame>());
        Assert.Equal(56, Unsafe.SizeOf<NativeMethods.GeometryFrameMetrics>());
        Assert.Equal(88, Unsafe.SizeOf<NativeGeometryPrimitive>());
        Assert.Equal(88, Unsafe.SizeOf<NativeMethods.EngineInfo>());
        Assert.Equal(16, Unsafe.SizeOf<NativeMethods.NativeColor>());
        Assert.Equal(1U, NativeMethods.AbiVersion);
        Assert.Equal(1U, NativeMethods.WgpuNativeMay2024BackendAbi);
    }

    [Fact]
    public void CapabilityValuesMatchPublishedNativeHeader()
    {
        Assert.Equal(1UL, (ulong)NativeRendererCapabilities.SolidRectBatch);
        Assert.Equal(2UL, (ulong)NativeRendererCapabilities.SharedVectorShader);
        Assert.Equal(4UL, (ulong)NativeRendererCapabilities.ExternalTarget);
        Assert.Equal(8UL, (ulong)NativeRendererCapabilities.IndexedAnalyticBatch);
        Assert.Equal(16UL, (ulong)NativeRendererCapabilities.Affine2D);
        Assert.Equal(32UL, (ulong)NativeRendererCapabilities.IndexedGeometryBatch);
        Assert.Equal(64UL, (ulong)NativeRendererCapabilities.DeviceStrokes);
        Assert.Equal(6U, (uint)NativeRendererStatus.InternalError);
        Assert.Equal(4U, (uint)NativeRendererTextureFormat.Bgra8UnormSrgb);
    }

    [Fact]
    public void GeometryPrimitiveMatchesNativeAffinePodLayout()
    {
        Assert.Equal(0, OffsetOf<NativeGeometryPrimitive>(nameof(NativeGeometryPrimitive.Kind)));
        Assert.Equal(4, OffsetOf<NativeGeometryPrimitive>(nameof(NativeGeometryPrimitive.Flags)));
        Assert.Equal(8, OffsetOf<NativeGeometryPrimitive>(nameof(NativeGeometryPrimitive.P0)));
        Assert.Equal(16, OffsetOf<NativeGeometryPrimitive>(nameof(NativeGeometryPrimitive.P1)));
        Assert.Equal(24, OffsetOf<NativeGeometryPrimitive>(nameof(NativeGeometryPrimitive.P2)));
        Assert.Equal(32, OffsetOf<NativeGeometryPrimitive>(nameof(NativeGeometryPrimitive.P3)));
        Assert.Equal(40, OffsetOf<NativeGeometryPrimitive>(nameof(NativeGeometryPrimitive.StrokeThickness)));
        Assert.Equal(48, OffsetOf<NativeGeometryPrimitive>(nameof(NativeGeometryPrimitive.Color)));
        Assert.Equal(64, OffsetOf<NativeGeometryPrimitive>(nameof(NativeGeometryPrimitive.Transform)));

        var primitive = new NativeGeometryPrimitive(
            NativeGeometryPrimitiveKind.Line,
            new Vector2(1, 2),
            new Vector2(3, 4),
            Vector4.One,
            Matrix3x2.Identity,
            strokeThickness: 2,
            flags: NativeGeometryPrimitiveFlags.FixedDeviceStroke);
        Assert.Equal(2, primitive.StrokeThickness);
        Assert.Equal(
            NativeGeometryPrimitiveFlags.FixedDeviceStroke,
            primitive.Flags);
    }

    [Fact]
    public void AnalyticPrimitiveMatchesNativeAffinePodLayout()
    {
        Assert.Equal(0, OffsetOf<NativeAnalyticPrimitive>(nameof(NativeAnalyticPrimitive.Kind)));
        Assert.Equal(4, OffsetOf<NativeAnalyticPrimitive>(nameof(NativeAnalyticPrimitive.Flags)));
        Assert.Equal(8, OffsetOf<NativeAnalyticPrimitive>(nameof(NativeAnalyticPrimitive.X)));
        Assert.Equal(24, OffsetOf<NativeAnalyticPrimitive>(nameof(NativeAnalyticPrimitive.CornerRadius)));
        Assert.Equal(28, OffsetOf<NativeAnalyticPrimitive>(nameof(NativeAnalyticPrimitive.StrokeThickness)));
        Assert.Equal(32, OffsetOf<NativeAnalyticPrimitive>(nameof(NativeAnalyticPrimitive.Color)));
        Assert.Equal(48, OffsetOf<NativeAnalyticPrimitive>(nameof(NativeAnalyticPrimitive.Transform)));

        var primitive = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Ellipse,
            1,
            2,
            3,
            4,
            Vector4.One,
            Matrix3x2.CreateTranslation(5, 6));
        Assert.Equal(5, primitive.Transform.M31);
        Assert.Equal(6, primitive.Transform.M32);
    }

    [Fact]
    public void NativeBuildReusesProductionShaderAndExactManagedWgpuRevision()
    {
        string cmake = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "CMakeLists.txt"));
        Assert.Contains(
            "../ProGPU.Backend/Shaders/Vector.wgsl",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains("EmbedShader.cmake", cmake, StringComparison.Ordinal);

        string nativeSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native.cpp"));
        Assert.Contains(
            "VectorWgsl.generated.hpp",
            nativeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("@vertex", nativeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("@fragment", nativeSource, StringComparison.Ordinal);

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(
            FindRepoFile("eng", "progpu-native-wgpu.version.json")));
        Assert.Equal(
            "Silk.NET.WebGPU 2.23.0",
            manifest.RootElement.GetProperty("managedBinding").GetString());
        Assert.Equal(
            "33133da4ec5a0174cb21539ef2d3346f75200411",
            manifest.RootElement.GetProperty("revision").GetString());
        Assert.Equal(
            "aef5e428a1fdab2ea770581ae7c95d8779984e0a",
            manifest.RootElement.GetProperty("webGpuHeadersRevision").GetString());

        string packages = File.ReadAllText(FindRepoFile("Directory.Packages.props"));
        Assert.Contains(
            "<PackageVersion Include=\"Silk.NET.WebGPU\" Version=\"2.23.0\" />",
            packages,
            StringComparison.Ordinal);
        Assert.Contains(
            "<PackageVersion Include=\"Silk.NET.WebGPU.Native.WGPU\" Version=\"2.23.0\" />",
            packages,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopNativeSampleSelectsSilkWithoutReinterpretingDawnHandles()
    {
        string program = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Samples.Desktop", "Program.cs"));
        string wrapper = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Backend.Native", "NativeCompositor.cs"));
        string page = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Samples.Desktop", "NativeRendererSamplePage.cs"));

        Assert.Contains("\"--native-renderer\"", program, StringComparison.Ordinal);
        Assert.Contains("if (!useNativeRenderer)", program, StringComparison.Ordinal);
        Assert.Contains(
            "builder.WithGpuContextFactory(CreateDesktopGpuContext)",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "context.BackendKind != WgpuBackendKind.SilkNative",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains(
            "Dawn and browser devices require their own adapters",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains(
            "Restart ProGPU.Samples.Desktop with --native-renderer",
            page,
            StringComparison.Ordinal);
    }

    private static int OffsetOf<T>(string fieldName) where T : struct =>
        checked((int)Marshal.OffsetOf<T>(fieldName));

    private static string FindRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(
                new[] { directory.FullName }
                    .Concat(pathParts)
                    .ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(pathParts)}.");
    }
}
