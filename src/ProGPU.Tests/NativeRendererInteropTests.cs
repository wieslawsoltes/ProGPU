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
    public void PrivateInteropRecordsMatchNativeAbiThree()
    {
        Assert.Equal(40, Unsafe.SizeOf<NativeMethods.EngineOptions>());
        Assert.Equal(56, Unsafe.SizeOf<NativeMethods.Frame>());
        Assert.Equal(40, Unsafe.SizeOf<NativeMethods.FrameMetrics>());
        Assert.Equal(56, Unsafe.SizeOf<NativeMethods.AnalyticFrame>());
        Assert.Equal(48, Unsafe.SizeOf<NativeMethods.AnalyticFrameMetrics>());
        Assert.Equal(72, Unsafe.SizeOf<NativeAnalyticPrimitive>());
        Assert.Equal(144, Unsafe.SizeOf<NativeMethods.GeometryFrame>());
        Assert.Equal(64, Unsafe.SizeOf<NativeMethods.GeometryFrameMetrics>());
        Assert.Equal(88, Unsafe.SizeOf<NativeGeometryPrimitive>());
        Assert.Equal(72, Unsafe.SizeOf<NativePolyline>());
        Assert.Equal(32, Unsafe.SizeOf<NativeDashStyle>());
        Assert.Equal(112, Unsafe.SizeOf<NativeSpline>());
        Assert.Equal(48, Unsafe.SizeOf<NativePathSegment>());
        Assert.Equal(80, Unsafe.SizeOf<NativePathFill>());
        Assert.Equal(80, Unsafe.SizeOf<NativeMethods.PathFrame>());
        Assert.Equal(96, Unsafe.SizeOf<NativeMethods.PathFrameMetrics>());
        Assert.Equal(40, Unsafe.SizeOf<NativeGlyphOutline>());
        Assert.Equal(64, Unsafe.SizeOf<NativePositionedGlyph>());
        Assert.Equal(96, Unsafe.SizeOf<NativeMethods.GlyphFrame>());
        Assert.Equal(80, Unsafe.SizeOf<NativeMethods.GlyphFrameMetrics>());
        Assert.Equal(16, Unsafe.SizeOf<NativeImageRect>());
        Assert.Equal(200, Unsafe.SizeOf<NativeMethods.ImageFrame>());
        Assert.Equal(72, Unsafe.SizeOf<NativeMethods.ImageFrameMetrics>());
        Assert.Equal(
            144,
            OffsetOf<NativeMethods.ImageFrame>(
                nameof(NativeMethods.ImageFrame.ExternalSourceView)));
        Assert.Equal(
            152,
            OffsetOf<NativeMethods.ImageFrame>(
                nameof(NativeMethods.ImageFrame.SourceFlags)));
        Assert.Equal(
            160,
            OffsetOf<NativeMethods.ImageFrame>(
                nameof(NativeMethods.ImageFrame.ExternalMaskView)));
        Assert.Equal(
            176,
            OffsetOf<NativeMethods.ImageFrame>(
                nameof(NativeMethods.ImageFrame.MaskDestinationRect)));
        Assert.Equal(88, Unsafe.SizeOf<NativeMethods.EngineInfo>());
        Assert.Equal(16, Unsafe.SizeOf<NativeMethods.NativeColor>());
        Assert.Equal(3U, NativeMethods.AbiVersion);
        Assert.Equal(1U, NativeMethods.WgpuNativeMay2024BackendAbi);
        Assert.Equal(2U, NativeMethods.DawnWebScene2026JulyBackendAbi);
        Assert.Equal(1U, NativeDawnAdapter.AdapterAbiVersion);
        Assert.Equal(2U, NativeDawnAdapter.RequiredProviderAbiVersion);
        Assert.Equal(2U, NativeDawnAdapter.BackendAbi);
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
        Assert.Equal(128UL, (ulong)NativeRendererCapabilities.BezierStrokes);
        Assert.Equal(256UL, (ulong)NativeRendererCapabilities.StrokeCaps);
        Assert.Equal(512UL, (ulong)NativeRendererCapabilities.ConnectedStrokes);
        Assert.Equal(1024UL, (ulong)NativeRendererCapabilities.SplineStrokes);
        Assert.Equal(2048UL, (ulong)NativeRendererCapabilities.DashedStrokes);
        Assert.Equal(
            4096UL,
            (ulong)NativeRendererCapabilities.RetainedGeometryReplay);
        Assert.Equal(8192UL, (ulong)NativeRendererCapabilities.PathFillAtlas);
        Assert.Equal(
            16384UL,
            (ulong)NativeRendererCapabilities.PositionedGlyphAtlas);
        Assert.Equal(
            32768UL,
            (ulong)NativeRendererCapabilities.ResizableAtlases);
        Assert.Equal(
            65536UL,
            (ulong)NativeRendererCapabilities.RetainedRgbaImage);
        Assert.Equal(
            131072UL,
            (ulong)NativeRendererCapabilities.ExternalRgbaView);
        Assert.Equal(
            262144UL,
            (ulong)NativeRendererCapabilities.ExternalImageMask);
        Assert.Equal(
            524288UL,
            (ulong)NativeRendererCapabilities.ExplicitQueueTimeline);
        Assert.Equal(16, Unsafe.SizeOf<NativeSubmissionToken>());
        Assert.Equal(3U, (uint)NativeGeometryPrimitiveKind.QuadraticBezier);
        Assert.Equal(4U, (uint)NativeGeometryPrimitiveKind.CubicBezier);
        Assert.Equal(6U, (uint)NativeRendererStatus.InternalError);
        Assert.Equal(4U, (uint)NativeRendererTextureFormat.Bgra8UnormSrgb);
    }

    [Fact]
    public void PathRecordsMatchPublishedNativeStorageLayout()
    {
        Assert.Equal(0, OffsetOf<NativePathSegment>(nameof(NativePathSegment.P0)));
        Assert.Equal(8, OffsetOf<NativePathSegment>(nameof(NativePathSegment.P1)));
        Assert.Equal(16, OffsetOf<NativePathSegment>(nameof(NativePathSegment.P2)));
        Assert.Equal(24, OffsetOf<NativePathSegment>(nameof(NativePathSegment.P3)));
        Assert.Equal(32, OffsetOf<NativePathSegment>(nameof(NativePathSegment.Kind)));
        Assert.Equal(0, OffsetOf<NativePathFill>(nameof(NativePathFill.SegmentOffset)));
        Assert.Equal(16, OffsetOf<NativePathFill>(nameof(NativePathFill.Minimum)));
        Assert.Equal(24, OffsetOf<NativePathFill>(nameof(NativePathFill.Maximum)));
        Assert.Equal(32, OffsetOf<NativePathFill>(nameof(NativePathFill.Color)));
        Assert.Equal(48, OffsetOf<NativePathFill>(nameof(NativePathFill.Transform)));
        Assert.Equal(72, OffsetOf<NativePathFill>(nameof(NativePathFill.FillRule)));
        Assert.Equal(76, OffsetOf<NativePathFill>(nameof(NativePathFill.SampleGrid)));
    }

    [Fact]
    public void PositionedGlyphRecordsMatchPublishedNativeStorageLayout()
    {
        Assert.Equal(0, OffsetOf<NativeGlyphOutline>(nameof(NativeGlyphOutline.SegmentOffset)));
        Assert.Equal(16, OffsetOf<NativeGlyphOutline>(nameof(NativeGlyphOutline.Minimum)));
        Assert.Equal(24, OffsetOf<NativeGlyphOutline>(nameof(NativeGlyphOutline.Maximum)));
        Assert.Equal(32, OffsetOf<NativeGlyphOutline>(nameof(NativeGlyphOutline.RasterScale)));
        Assert.Equal(36, OffsetOf<NativeGlyphOutline>(nameof(NativeGlyphOutline.SubpixelX)));
        Assert.Equal(0, OffsetOf<NativePositionedGlyph>(nameof(NativePositionedGlyph.OutlineIndex)));
        Assert.Equal(8, OffsetOf<NativePositionedGlyph>(nameof(NativePositionedGlyph.Position)));
        Assert.Equal(16, OffsetOf<NativePositionedGlyph>(nameof(NativePositionedGlyph.BasisX)));
        Assert.Equal(24, OffsetOf<NativePositionedGlyph>(nameof(NativePositionedGlyph.BasisY)));
        Assert.Equal(32, OffsetOf<NativePositionedGlyph>(nameof(NativePositionedGlyph.Color)));
        Assert.Equal(48, OffsetOf<NativePositionedGlyph>(nameof(NativePositionedGlyph.AtlasToLogicalScale)));
        Assert.Equal(52, OffsetOf<NativePositionedGlyph>(nameof(NativePositionedGlyph.BoldOffset)));
        Assert.Equal(56, OffsetOf<NativePositionedGlyph>(nameof(NativePositionedGlyph.ItalicSkew)));
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

        var capped = new NativeGeometryPrimitive(
            NativeGeometryPrimitiveKind.CubicBezier,
            Vector2.Zero,
            Vector2.One,
            Vector4.One,
            Matrix3x2.Identity,
            startCap: NativeStrokeCap.Round,
            endCap: NativeStrokeCap.Triangle);
        Assert.Equal(NativeStrokeCap.Round, capped.StartCap);
        Assert.Equal(NativeStrokeCap.Triangle, capped.EndCap);

        var polyline = new NativePolyline(
            4,
            8,
            Vector4.One,
            Matrix3x2.Identity,
            3f,
            startCap: NativeStrokeCap.Square,
            endCap: NativeStrokeCap.Round,
            lineJoin: NativeStrokeJoin.Bevel,
            isClosed: true,
            dashStyle: 3);
        Assert.Equal((nuint)4, polyline.PointOffset);
        Assert.Equal((nuint)8, polyline.PointCount);
        Assert.Equal(NativeStrokeCap.Square, polyline.StartCap);
        Assert.Equal(NativeStrokeCap.Round, polyline.EndCap);
        Assert.Equal(NativeStrokeJoin.Bevel, polyline.LineJoin);
        Assert.True(polyline.IsClosed);
        Assert.Equal(3U, polyline.DashStyle);

        var dashStyle = new NativeDashStyle(
            12,
            3,
            -2.5,
            NativeStrokeCap.Triangle);
        Assert.Equal((nuint)12, dashStyle.IntervalOffset);
        Assert.Equal((nuint)3, dashStyle.IntervalCount);
        Assert.Equal(-2.5, dashStyle.Offset);
        Assert.Equal(NativeStrokeCap.Triangle, dashStyle.Cap);

        var normalizedMiter = new NativePolyline(
            0,
            2,
            Vector4.One,
            Matrix3x2.Identity,
            1f,
            float.NaN);
        Assert.Equal(1f, normalizedMiter.MiterLimit);

        var spline = new NativeSpline(polyline, 3, 12, 4, 20, 8);
        Assert.Equal(polyline, spline.Stroke);
        Assert.Equal((nuint)3, spline.KnotOffset);
        Assert.Equal((nuint)12, spline.KnotCount);
        Assert.Equal((nuint)20, spline.WeightOffset);
        Assert.Equal((nuint)8, spline.WeightCount);
        Assert.Equal(4U, spline.Degree);
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
        Assert.Contains(
            "../ProGPU.Backend/Shaders/GlyphRasterizer.wgsl",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "../ProGPU.Backend/Shaders/Text.wgsl",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains("EmbedShader.cmake", cmake, StringComparison.Ordinal);

        string nativeSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native.cpp"));
        Assert.Contains(
            "VectorWgsl.generated.hpp",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GlyphRasterizerWgsl.generated.hpp",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "TextWgsl.generated.hpp",
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
    public void NativeRendererHasAnExactProviderResolvedWebSceneDawnGate()
    {
        string cmake = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "CMakeLists.txt"));
        string compatibility = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_webgpu_compat.hpp"));
        string verifier = File.ReadAllText(FindRepoFile(
            "eng", "progpu-verify-native-dawn-header.sh"));
        string providerVerifier = File.ReadAllText(FindRepoFile(
            "eng", "progpu-verify-native-webscene-provider.sh"));
        string providerTest = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "tests",
            "progpu_native_webscene_provider_tests.cpp"));
        string buildWorkflow = File.ReadAllText(FindRepoFile(
            ".github", "workflows", "build.yml"));
        string releaseWorkflow = File.ReadAllText(FindRepoFile(
            ".github", "workflows", "release.yml"));
        string packageProject = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Backend.Native", "ProGPU.Backend.Native.csproj"));

        Assert.Contains(
            "add_library(progpu_native_dawn SHARED",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DAWN_ABI=1",
            cmake,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "target_link_libraries(progpu_native_dawn PRIVATE",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains("WGPUStringView", compatibility, StringComparison.Ordinal);
        Assert.Contains("WGPUShaderSourceWGSL", compatibility, StringComparison.Ordinal);
        Assert.Contains(
            "wgpuQueueOnSubmittedWorkDone",
            compatibility,
            StringComparison.Ordinal);
        Assert.Contains(
            "wgpuQueueSubmitForIndex",
            compatibility,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu-native-dawn.version.json",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "imports WebGPU procedures directly",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_dawn",
            packageProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_dawn.h",
            packageProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_webscene_provider_tests",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "build-native-gpu-runtime.sh",
            providerVerifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_engine_poll_submission",
            providerTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "WEBSCENE_GPU_EXTERNAL_TEXTURE_GPU_COMPLETE",
            providerTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "webscene_gpu_provider_retain_external_texture",
            providerTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "Verify exact WebScene provider on Metal",
            buildWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Verify exact WebScene provider on Metal",
            releaseWorkflow,
            StringComparison.Ordinal);

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(
            FindRepoFile("eng", "progpu-native-dawn.version.json")));
        Assert.Equal(
            "02823bf8d2e56548b2780d6b92ae7065be1d8605",
            manifest.RootElement.GetProperty("providerRevision").GetString());
        Assert.Equal(
            2,
            manifest.RootElement.GetProperty("providerAbi").GetInt32());
        Assert.Equal(
            "710c33013c53ab2700d332c25ff51430251a8cc4",
            manifest.RootElement.GetProperty("dawnRevision").GetString());
        Assert.Equal(
            "01addc4ba8a2915a061b7095a6768b512071ab96",
            manifest.RootElement.GetProperty("webGpuHeadersRevision").GetString());
        Assert.Equal(
            "provider-hardware-integration",
            manifest.RootElement.GetProperty("status").GetString());
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
        Assert.Contains("RenderExternalImage(", page, StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureAlphaMode.Straight",
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
