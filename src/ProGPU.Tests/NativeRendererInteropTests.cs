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
