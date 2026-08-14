using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
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
        Assert.Equal(72, Unsafe.SizeOf<NativeDawnMethods.EngineOptions>());
        Assert.Equal(24, Unsafe.SizeOf<DawnNativeDeviceHandles>());
        Assert.Equal(
            24,
            OffsetOf<NativeDawnMethods.EngineOptions>(
                nameof(NativeDawnMethods.EngineOptions.ResolverContext)));
        Assert.Equal(
            40,
            OffsetOf<NativeDawnMethods.EngineOptions>(
                nameof(NativeDawnMethods.EngineOptions.Instance)));
        Assert.Equal(64, Unsafe.SizeOf<NativeMethods.Frame>());
        Assert.Equal(
            56,
            OffsetOf<NativeMethods.Frame>(nameof(NativeMethods.Frame.DrawState)));
        Assert.Equal(40, Unsafe.SizeOf<NativeMethods.FrameMetrics>());
        Assert.Equal(64, Unsafe.SizeOf<NativeMethods.AnalyticFrame>());
        Assert.Equal(
            56,
            OffsetOf<NativeMethods.AnalyticFrame>(
                nameof(NativeMethods.AnalyticFrame.DrawState)));
        Assert.Equal(48, Unsafe.SizeOf<NativeMethods.AnalyticFrameMetrics>());
        Assert.Equal(72, Unsafe.SizeOf<NativeAnalyticPrimitive>());
        Assert.Equal(152, Unsafe.SizeOf<NativeMethods.GeometryFrame>());
        Assert.Equal(
            144,
            OffsetOf<NativeMethods.GeometryFrame>(
                nameof(NativeMethods.GeometryFrame.DrawState)));
        Assert.Equal(64, Unsafe.SizeOf<NativeMethods.GeometryFrameMetrics>());
        Assert.Equal(88, Unsafe.SizeOf<NativeGeometryPrimitive>());
        Assert.Equal(72, Unsafe.SizeOf<NativePolyline>());
        Assert.Equal(32, Unsafe.SizeOf<NativeDashStyle>());
        Assert.Equal(112, Unsafe.SizeOf<NativeSpline>());
        Assert.Equal(48, Unsafe.SizeOf<NativePathSegment>());
        Assert.Equal(80, Unsafe.SizeOf<NativePathFill>());
        Assert.Equal(88, Unsafe.SizeOf<NativeMethods.PathFrame>());
        Assert.Equal(
            80,
            OffsetOf<NativeMethods.PathFrame>(
                nameof(NativeMethods.PathFrame.DrawState)));
        Assert.Equal(96, Unsafe.SizeOf<NativeMethods.PathFrameMetrics>());
        Assert.Equal(40, Unsafe.SizeOf<NativeGlyphOutline>());
        Assert.Equal(64, Unsafe.SizeOf<NativePositionedGlyph>());
        Assert.Equal(104, Unsafe.SizeOf<NativeMethods.GlyphFrame>());
        Assert.Equal(
            96,
            OffsetOf<NativeMethods.GlyphFrame>(
                nameof(NativeMethods.GlyphFrame.DrawState)));
        Assert.Equal(80, Unsafe.SizeOf<NativeMethods.GlyphFrameMetrics>());
        Assert.Equal(16, Unsafe.SizeOf<NativeImageRect>());
        Assert.Equal(72, Unsafe.SizeOf<NativeMethods.DrawState>());
        Assert.Equal(
            0,
            OffsetOf<NativeMethods.DrawState>(
                nameof(NativeMethods.DrawState.StructSize)));
        Assert.Equal(
            4,
            OffsetOf<NativeMethods.DrawState>(nameof(NativeMethods.DrawState.Flags)));
        Assert.Equal(
            8,
            OffsetOf<NativeMethods.DrawState>(nameof(NativeMethods.DrawState.Opacity)));
        Assert.Equal(
            16,
            OffsetOf<NativeMethods.DrawState>(nameof(NativeMethods.DrawState.ClipRect)));
        Assert.Equal(
            32,
            OffsetOf<NativeMethods.DrawState>(nameof(NativeMethods.DrawState.GroupOpacity)));
        Assert.Equal(
            36,
            OffsetOf<NativeMethods.DrawState>(nameof(NativeMethods.DrawState.GroupRevision)));
        Assert.Equal(
            40,
            OffsetOf<NativeMethods.DrawState>(nameof(NativeMethods.DrawState.GroupMask)));
        Assert.Equal(
            48,
            OffsetOf<NativeMethods.DrawState>(nameof(NativeMethods.DrawState.GroupEffect)));
        Assert.Equal(
            56,
            OffsetOf<NativeMethods.DrawState>(nameof(NativeMethods.DrawState.GroupEffectChain)));
        Assert.Equal(
            64,
            OffsetOf<NativeMethods.DrawState>(nameof(NativeMethods.DrawState.GroupBlendMode)));
        Assert.Equal(56, Unsafe.SizeOf<NativeMethods.GroupEffect>());
        Assert.Equal(
            16,
            OffsetOf<NativeMethods.GroupEffect>(nameof(NativeMethods.GroupEffect.SigmaX)));
        Assert.Equal(
            20,
            OffsetOf<NativeMethods.GroupEffect>(nameof(NativeMethods.GroupEffect.SigmaY)));
        Assert.Equal(
            32,
            OffsetOf<NativeMethods.GroupEffect>(nameof(NativeMethods.GroupEffect.OffsetX)));
        Assert.Equal(
            52,
            OffsetOf<NativeMethods.GroupEffect>(nameof(NativeMethods.GroupEffect.ColorA)));
        Assert.Equal(24, Unsafe.SizeOf<NativeMethods.GroupEffectChain>());
        Assert.Equal(
            16,
            OffsetOf<NativeMethods.GroupEffectChain>(nameof(NativeMethods.GroupEffectChain.Effects)));
        Assert.Equal(152, Unsafe.SizeOf<NativeMethods.GroupMask>());
        Assert.Equal(
            16,
            OffsetOf<NativeMethods.GroupMask>(nameof(NativeMethods.GroupMask.ExternalView)));
        Assert.Equal(
            48,
            OffsetOf<NativeMethods.GroupMask>(nameof(NativeMethods.GroupMask.DestinationRect)));
        Assert.Equal(
            80,
            OffsetOf<NativeMethods.GroupMask>(nameof(NativeMethods.GroupMask.Transform)));
        Assert.Equal(
            136,
            OffsetOf<NativeMethods.GroupMask>(nameof(NativeMethods.GroupMask.Opacity)));
        Assert.Equal(
            144,
            OffsetOf<NativeMethods.GroupMask>(nameof(NativeMethods.GroupMask.ClipChain)));
        Assert.Equal(40, Unsafe.SizeOf<NativeMethods.ClipChain>());
        Assert.Equal(72, Unsafe.SizeOf<NativeClipPath>());
        Assert.Equal(200, Unsafe.SizeOf<NativeMethods.LayerMetrics>());
        Assert.Equal(
            56,
            OffsetOf<NativeMethods.LayerMetrics>(nameof(NativeMethods.LayerMetrics.MaskKind)));
        Assert.Equal(
            72,
            OffsetOf<NativeMethods.LayerMetrics>(nameof(NativeMethods.LayerMetrics.MaskUniformUploadBytes)));
        Assert.Equal(
            80,
            OffsetOf<NativeMethods.LayerMetrics>(nameof(NativeMethods.LayerMetrics.ClipPathCount)));
        Assert.Equal(
            96,
            OffsetOf<NativeMethods.LayerMetrics>(nameof(NativeMethods.LayerMetrics.ClipPathUploadBytes)));
        Assert.Equal(
            120,
            OffsetOf<NativeMethods.LayerMetrics>(nameof(NativeMethods.LayerMetrics.EffectKind)));
        Assert.Equal(
            136,
            OffsetOf<NativeMethods.LayerMetrics>(nameof(NativeMethods.LayerMetrics.EffectUniformUploadBytes)));
        Assert.Equal(
            144,
            OffsetOf<NativeMethods.LayerMetrics>(nameof(NativeMethods.LayerMetrics.EffectTextureBytes)));
        Assert.Equal(
            168,
            OffsetOf<NativeMethods.LayerMetrics>(nameof(NativeMethods.LayerMetrics.BlendMode)));
        Assert.Equal(
            192,
            OffsetOf<NativeMethods.LayerMetrics>(nameof(NativeMethods.LayerMetrics.BlendSourceTextureBytes)));
        Assert.Equal(208, Unsafe.SizeOf<NativeMethods.ImageFrame>());
        Assert.Equal(
            200,
            OffsetOf<NativeMethods.ImageFrame>(
                nameof(NativeMethods.ImageFrame.DrawState)));
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
        Assert.Equal(80, Unsafe.SizeOf<NativeMethods.SceneHeader>());
        Assert.Equal(48, Unsafe.SizeOf<NativeMethods.SceneResource>());
        Assert.Equal(64, Unsafe.SizeOf<NativeMethods.SceneCommand>());
        Assert.Equal(64, Unsafe.SizeOf<NativeMethods.SceneMetrics>());
        Assert.Equal(88, Unsafe.SizeOf<NativeSceneImageDraw>());
        Assert.Equal(16, Unsafe.SizeOf<NativeSceneImageSamplingOptions>());
        Assert.Equal(96, Unsafe.SizeOf<NativeSceneImageColorMatrix>());
        Assert.Equal(64, Unsafe.SizeOf<NativeSceneState>());
        Assert.Equal(64, Unsafe.SizeOf<NativeSceneLayer>());
        Assert.Equal(104, Unsafe.SizeOf<NativeSceneLayerMask>());
        Assert.Equal(80, Unsafe.SizeOf<NativeSceneLayerCoverageMask>());
        Assert.Equal(16, Unsafe.SizeOf<NativeSceneEffectChain>());
        Assert.Equal(56, Unsafe.SizeOf<NativeSceneEffect>());
        Assert.Equal(80, Unsafe.SizeOf<NativeScenePathFill>());
        Assert.Equal(40, Unsafe.SizeOf<NativeSceneGlyphOutline>());
        Assert.Equal(56, Unsafe.SizeOf<NativeMethods.SceneFrame>());
        Assert.Equal(256, Unsafe.SizeOf<NativeSceneBrush>());
        Assert.Equal(32, Unsafe.SizeOf<NativeSceneGradientStop>());
        Assert.Equal(32, Unsafe.SizeOf<NativeSceneTextStyle>());
        Assert.Equal(48, Unsafe.SizeOf<NativeSceneColorGlyphBitmap>());
        Assert.Equal(24, Unsafe.SizeOf<NativeSceneGlyphDraw>());
        Assert.Equal(104, Unsafe.SizeOf<NativeMethods.SceneFrameMetrics>());
        Assert.Equal(
            72,
            OffsetOf<NativeMethods.SceneFrameMetrics>(
                nameof(NativeMethods.SceneFrameMetrics.BrushUploadBytes)));
        Assert.Equal(
            80,
            OffsetOf<NativeMethods.SceneFrameMetrics>(
                nameof(NativeMethods.SceneFrameMetrics.GradientStopUploadBytes)));
        Assert.Equal(
            88,
            OffsetOf<NativeMethods.SceneFrameMetrics>(
                nameof(NativeMethods.SceneFrameMetrics.TextStyleUploadBytes)));
        Assert.Equal(
            96,
            OffsetOf<NativeMethods.SceneFrameMetrics>(
                nameof(NativeMethods.SceneFrameMetrics.ColorGlyphUploadBytes)));
        Assert.Equal(
            24,
            OffsetOf<NativeSceneImageDraw>(
                nameof(NativeSceneImageDraw.SourceRect)));
        Assert.Equal(
            56,
            OffsetOf<NativeSceneImageDraw>(
                nameof(NativeSceneImageDraw.Transform)));
        Assert.Equal(
            8,
            OffsetOf<NativeSceneImageSamplingOptions>(
                nameof(NativeSceneImageSamplingOptions.CubicB)));
        Assert.Equal(
            8,
            OffsetOf<NativeSceneImageColorMatrix>(
                nameof(NativeSceneImageColorMatrix.Red)));
        Assert.Equal(
            72,
            OffsetOf<NativeSceneImageColorMatrix>(
                nameof(NativeSceneImageColorMatrix.Offset)));
        Assert.Equal(
            8,
            OffsetOf<NativeSceneState>(nameof(NativeSceneState.Transform)));
        Assert.Equal(
            32,
            OffsetOf<NativeSceneState>(nameof(NativeSceneState.Opacity)));
        Assert.Equal(
            40,
            OffsetOf<NativeSceneState>(nameof(NativeSceneState.ClipRect)));
        Assert.Equal(
            8,
            OffsetOf<NativeSceneLayer>(nameof(NativeSceneLayer.Bounds)));
        Assert.Equal(
            24,
            OffsetOf<NativeSceneLayer>(nameof(NativeSceneLayer.Opacity)));
        Assert.Equal(
            32,
            OffsetOf<NativeSceneLayer>(
                nameof(NativeSceneLayer.MaskResourceIndex)));
        Assert.Equal(
            40,
            OffsetOf<NativeSceneLayer>(
                nameof(NativeSceneLayer.ContentRevision)));
        Assert.Equal(
            16,
            OffsetOf<NativeSceneLayerMask>(nameof(NativeSceneLayerMask.Bounds)));
        Assert.Equal(
            32,
            OffsetOf<NativeSceneLayerMask>(nameof(NativeSceneLayerMask.Transform)));
        Assert.Equal(
            88,
            OffsetOf<NativeSceneLayerMask>(nameof(NativeSceneLayerMask.Opacity)));
        Assert.Equal(
            32,
            OffsetOf<NativeScenePathFill>(
                nameof(NativeScenePathFill.Color)));
        Assert.Equal(
            32,
            OffsetOf<NativeSceneGlyphOutline>(
                nameof(NativeSceneGlyphOutline.RasterScale)));
        Assert.Equal(
            16,
            OffsetOf<NativeMethods.SceneFrame>(
                nameof(NativeMethods.SceneFrame.TargetView)));
        Assert.Equal(
            40,
            OffsetOf<NativeMethods.SceneFrame>(
                nameof(NativeMethods.SceneFrame.SceneId)));
        Assert.Equal(
            24,
            OffsetOf<NativeMethods.SceneHeader>(nameof(NativeMethods.SceneHeader.SceneId)));
        Assert.Equal(
            40,
            OffsetOf<NativeMethods.SceneHeader>(nameof(NativeMethods.SceneHeader.CommandOffset)));
        Assert.Equal(
            64,
            OffsetOf<NativeMethods.SceneHeader>(nameof(NativeMethods.SceneHeader.ArenaOffset)));
        Assert.Equal(
            16,
            OffsetOf<NativeMethods.SceneResource>(nameof(NativeMethods.SceneResource.ResourceId)));
        Assert.Equal(
            32,
            OffsetOf<NativeMethods.SceneResource>(nameof(NativeMethods.SceneResource.PayloadOffset)));
        Assert.Equal(
            16,
            OffsetOf<NativeMethods.SceneCommand>(nameof(NativeMethods.SceneCommand.CommandId)));
        Assert.Equal(
            40,
            OffsetOf<NativeMethods.SceneCommand>(nameof(NativeMethods.SceneCommand.Bounds)));
        Assert.Equal(3U, NativeMethods.AbiVersion);
        Assert.Equal(1U, NativeMethods.WgpuNativeMay2024BackendAbi);
        Assert.Equal(2U, NativeMethods.DawnWebScene2026JulyBackendAbi);
        Assert.Equal(1U, NativeDawnAdapter.AdapterAbiVersion);
        Assert.Equal(2U, NativeDawnAdapter.RequiredProviderAbiVersion);
        Assert.Equal(2U, NativeDawnAdapter.BackendAbi);
    }

    [Fact]
    public void PublicDrawStateSeparatesPrimitiveOpacityClipAndGroupOpacity()
    {
        var state = new NativeDrawState(
            0.625f,
            new NativeImageRect(1.25f, 2.5f, 30.75f, 40.5f),
            NativeDrawStateFlags.ClipRect,
            0.4f,
            17U);

        Assert.Equal(0.625f, state.Opacity);
        Assert.Equal(NativeDrawStateFlags.ClipRect, state.Flags);
        Assert.Equal(1.25f, state.ClipRect.X);
        Assert.Equal(40.5f, state.ClipRect.Height);
        Assert.Equal(0.4f, state.GroupOpacity);
        Assert.Equal(17U, state.GroupRevision);
        Assert.Equal(1f, NativeDrawState.Default.EffectiveOpacity);
        Assert.Equal(1f, NativeDrawState.Default.EffectiveGroupOpacity);
        Assert.Equal(GpuBlendMode.SrcOver, NativeDrawState.Default.EffectiveGroupBlendMode);
    }

    [Fact]
    public void PublicDrawStateCarriesTypedGroupBlendMode()
    {
        var state = new NativeDrawState(
            0.75f,
            default,
            NativeDrawStateFlags.None,
            0.5f,
            19U,
            GpuBlendMode.Overlay);
        var defaultWithBlend = default(NativeDrawState)
            .WithGroupBlendMode(GpuBlendMode.Multiply);

        Assert.Equal(GpuBlendMode.Overlay, state.GroupBlendMode);
        Assert.Equal(GpuBlendMode.Multiply, defaultWithBlend.GroupBlendMode);
        Assert.Equal(1f, defaultWithBlend.EffectiveOpacity);
        Assert.Equal(1f, defaultWithBlend.EffectiveGroupOpacity);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            state.WithGroupBlendMode((GpuBlendMode)29));
    }

    [Fact]
    public void PublicDrawStateCarriesTypedAnalyticGroupMask()
    {
        var mask = NativeGroupMask.RoundedRectangle(
            new NativeImageRect(2f, 3f, 40f, 20f),
            new Matrix3x2(1f, 0.25f, -0.1f, 1f, 5f, 7f),
            new Vector4(4f, 5f, 6f, 7f),
            new Vector4(8f, 9f, 10f, 11f),
            0.75f);
        var state = new NativeDrawState(
            1f,
            default,
            NativeDrawStateFlags.None,
            0.5f,
            9U,
            mask);

        Assert.Equal(NativeGroupMaskKind.RoundedRectangle, state.GroupMask.Kind);
        Assert.Equal(40f, state.GroupMask.Bounds.Width);
        Assert.Equal(0.25f, state.GroupMask.Transform.M12);
        Assert.Equal(6f, state.GroupMask.CornerRadiiX.Z);
        Assert.Equal(11f, state.GroupMask.CornerRadiiY.W);
        Assert.Equal(0.75f, state.GroupMask.Opacity);
    }

    [Fact]
    public void PublicDrawStateCarriesTypedGaussianGroupEffect()
    {
        var effect = NativeGroupEffect.GaussianBlur(2.5f, 4.5f, 23U);
        var state = new NativeDrawState(
            1f,
            default,
            NativeDrawStateFlags.None,
            0.75f,
            19U,
            default,
            effect);

        Assert.Equal(NativeGroupEffectKind.GaussianBlur, state.GroupEffect.Kind);
        Assert.Equal(2.5f, state.GroupEffect.SigmaX);
        Assert.Equal(4.5f, state.GroupEffect.SigmaY);
        Assert.Equal(23U, state.GroupEffect.Revision);
        Assert.True(state.GroupEffect.IsEnabled);
    }

    [Fact]
    public void PublicDrawStateCarriesTypedDropShadowGroupEffect()
    {
        var effect = NativeGroupEffect.DropShadow(
            3.5f,
            new Vector2(7f, -2f),
            new Vector4(0.1f, 0.2f, 0.3f, 0.75f),
            29U);

        Assert.Equal(NativeGroupEffectKind.DropShadow, effect.Kind);
        Assert.Equal(3.5f, effect.SigmaX);
        Assert.Equal(new Vector2(7f, -2f), effect.Offset);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.75f), effect.Color);
        Assert.Equal(29U, effect.Revision);
    }

    [Fact]
    public void PublicDrawStateCarriesImmutableBoundedGroupEffectChain()
    {
        var source = new[]
        {
            NativeGroupEffect.GaussianBlur(1.5f, 31U),
            NativeGroupEffect.DropShadow(
                2f,
                new Vector2(4f, 3f),
                new Vector4(0.2f, 0.1f, 0.4f, 0.6f),
                32U)
        };
        var chain = new NativeGroupEffectChain(source, 41U);
        var state = new NativeDrawState(
            1f,
            default,
            NativeDrawStateFlags.None,
            0.75f,
            19U,
            default,
            chain);
        source[0] = NativeGroupEffect.GaussianBlur(9f, 99U);

        Assert.Same(chain, state.GroupEffectChain);
        Assert.Equal(2, chain.Count);
        Assert.Equal(41U, chain.Revision);
        Assert.Equal(1.5f, chain.Effects[0].SigmaX);
        Assert.Equal(NativeGroupEffectKind.DropShadow, chain.Effects[1].Kind);
    }

    [Fact]
    public void PublicGroupEffectChainRejectsInvalidBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativeGroupEffectChain([], 1U));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativeGroupEffectChain(
                new NativeGroupEffect[
                    NativeGroupEffectChain.MaximumEffectCount + 1],
                1U));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativeGroupEffectChain(
                [NativeGroupEffect.GaussianBlur(1f, 1U)],
                0U));
    }

    [Fact]
    public void PublicDrawStateCarriesImmutableTypedVectorClipChain()
    {
        var segments = new[]
        {
            new NativePathSegment(
                NativePathSegmentKind.Line,
                new Vector2(0f, 0f),
                new Vector2(10f, 0f)),
            new NativePathSegment(
                NativePathSegmentKind.Line,
                new Vector2(10f, 0f),
                new Vector2(10f, 10f)),
            new NativePathSegment(
                NativePathSegmentKind.Line,
                new Vector2(10f, 10f),
                new Vector2(0f, 0f))
        };
        var paths = new[]
        {
            new NativeClipPath(
                0U,
                3U,
                Vector2.Zero,
                new Vector2(10f),
                Matrix3x2.CreateSkew(0.2f, -0.1f) *
                    Matrix3x2.CreateTranslation(4f, 5f),
                NativeClipOperation.Difference,
                NativeFillRule.EvenOdd,
                8U)
        };
        var chain = new NativeClipChain(paths, segments);
        NativeGroupMask mask = NativeGroupMask.VectorClipChain(chain, 17U);
        var state = new NativeDrawState(
            1f,
            default,
            NativeDrawStateFlags.None,
            1f,
            1U,
            mask);

        paths[0] = default;
        segments[0] = default;

        Assert.Equal(NativeGroupMaskKind.VectorClipChain, state.GroupMask.Kind);
        Assert.Equal(17U, state.GroupMask.Revision);
        Assert.Same(chain, state.GroupMask.ClipChain);
        Assert.Equal(1, chain.PathCount);
        Assert.Equal(3, chain.SegmentCount);
    }

    [Fact]
    public void VectorClipChainRejectsOutOfRangeSegmentArena()
    {
        var path = new NativeClipPath(
            1U,
            1U,
            Vector2.Zero,
            new Vector2(10f),
            Matrix3x2.Identity);
        var segment = new NativePathSegment(
            NativePathSegmentKind.Line,
            Vector2.Zero,
            Vector2.One);

        Assert.Throws<ArgumentException>(() =>
            new NativeClipChain([path], [segment]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeGroupMask.VectorClipChain(
                new NativeClipChain(
                    [new NativeClipPath(
                        0U,
                        1U,
                        Vector2.Zero,
                        new Vector2(10f),
                        Matrix3x2.Identity)],
                    [segment]),
                0U));
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
        Assert.Equal(
            1048576UL,
            (ulong)NativeRendererCapabilities.FrameDrawState);
        Assert.Equal(
            2097152UL,
            (ulong)NativeRendererCapabilities.GroupOpacity);
        Assert.Equal(
            4194304UL,
            (ulong)NativeRendererCapabilities.CommonGroupMask);
        Assert.Equal(
            8388608UL,
            (ulong)NativeRendererCapabilities.AnalyticRoundedGroupMask);
        Assert.Equal(
            16777216UL,
            (ulong)NativeRendererCapabilities.RetainedVectorClipChain);
        Assert.Equal(
            268435456UL,
            (ulong)NativeRendererCapabilities.GroupBlendModes);
        Assert.Equal(
            536870912UL,
            (ulong)NativeRendererCapabilities.SemanticSceneSnapshots);
        Assert.Equal(
            1073741824UL,
            (ulong)NativeRendererCapabilities.SemanticSceneRendering);
        Assert.Equal(
            2147483648UL,
            (ulong)NativeRendererCapabilities.SemanticRetainedBrushes);
        Assert.Equal(
            4294967296UL,
            (ulong)NativeRendererCapabilities.SemanticRetainedTextStyles);
        Assert.Equal(
            17179869184UL,
            (ulong)NativeRendererCapabilities.DeviceLossRecreation);
        Assert.Equal(
            34359738368UL,
            (ulong)NativeRendererCapabilities.SemanticGeometryBatch);
        Assert.Equal(
            68719476736UL,
            (ulong)NativeRendererCapabilities.SemanticPointBatch);
        Assert.Equal(16, Unsafe.SizeOf<NativeSubmissionToken>());
        Assert.Equal(3U, (uint)NativeGeometryPrimitiveKind.QuadraticBezier);
        Assert.Equal(4U, (uint)NativeGeometryPrimitiveKind.CubicBezier);
        Assert.Equal(5U, (uint)NativeGeometryPrimitiveKind.DotGrid);
        Assert.Equal(64, Unsafe.SizeOf<NativeScenePointBatch>());
        Assert.Equal(11U, (uint)NativeSceneResourceKind.PointBatch);
        Assert.Equal(21U, (uint)NativeSceneCommandKind.DrawPointBatch);
        Assert.Equal(6U, (uint)NativeRendererStatus.InternalError);
        Assert.Equal(4U, (uint)NativeRendererTextureFormat.Bgra8UnormSrgb);
    }

    [Fact]
    public void SemanticSceneBuilderProducesCanonicalPointerFreeMixedStream()
    {
        Assert.Equal(
            1024,
            NativeSceneStreamBuilder.GetRequiredBufferSize(8, 4, 240));
        Span<byte> destination = stackalloc byte[2048];
        Span<byte> payload = stackalloc byte[8];
        payload.Fill(0x5a);
        var builder = new NativeSceneStreamBuilder(
            destination,
            sceneId: 41U,
            generation: 7U,
            commandCapacity: 8,
            resourceCapacity: 4);
        Assert.True(builder.TryAddResource(
            NativeSceneResourceKind.AnalyticBatch,
            100U,
            10U,
            payload,
            out uint analytic));
        Assert.True(builder.TryAddResource(
            NativeSceneResourceKind.PathBatch,
            101U,
            11U,
            payload,
            out uint path));
        Assert.True(builder.TryAddResource(
            NativeSceneResourceKind.GlyphRun,
            102U,
            12U,
            payload,
            out uint glyph));
        Assert.True(builder.TryAddResource(
            NativeSceneResourceKind.Image,
            103U,
            13U,
            payload,
            out uint image));
        Assert.False(builder.TryDrawPath(
            999U,
            analytic,
            new NativeImageRect(0f, 0f, 1f, 1f)));
        Assert.True(builder.TrySave(1000U));
        Assert.True(builder.TryDrawAnalytic(
            1001U,
            analytic,
            new NativeImageRect(0f, 0f, 100f, 80f)));
        Assert.True(builder.TryPushLayer(1002U));
        Assert.True(builder.TryDrawPath(
            1003U,
            path,
            new NativeImageRect(5f, 6f, 70f, 60f)));
        Assert.True(builder.TryDrawGlyphRun(
            1004U,
            glyph,
            new NativeImageRect(7f, 8f, 50f, 20f)));
        Assert.True(builder.TryDrawImage(
            1005U,
            image,
            new NativeImageRect(9f, 10f, 40f, 30f)));
        Assert.True(builder.TryPopLayer(1006U));
        Assert.True(builder.TryRestore(1007U));
        Assert.True(builder.TryBuild(out ReadOnlySpan<byte> stream));

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        Assert.Equal(NativeMethods.SceneStreamMagic, header.Magic);
        Assert.Equal(NativeMethods.SceneStreamVersion, header.StreamVersion);
        Assert.Equal(NativeMethods.SceneStreamEndianMarker, header.EndianMarker);
        Assert.Equal(41UL, header.SceneId);
        Assert.Equal(7UL, header.Generation);
        Assert.Equal(8U, header.CommandCount);
        Assert.Equal(4U, header.ResourceCount);
        Assert.Equal((uint)stream.Length, header.TotalSize);
        Assert.Equal(0U, header.CommandOffset & 7U);
        Assert.Equal(0U, header.ResourceOffset & 7U);
        Assert.Equal(0U, header.ArenaOffset & 7U);

        var firstResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            stream[(int)header.ResourceOffset..]);
        Assert.Equal(100UL, firstResource.ResourceId);
        Assert.Equal(NativeSceneResourceKind.AnalyticBatch, firstResource.Kind);
        Assert.Equal(8U, firstResource.PayloadSize);
        Assert.DoesNotContain(
            (byte)0,
            stream.Slice((int)firstResource.PayloadOffset, 8).ToArray());
    }

    [Fact]
    public void SemanticSceneBuilderWritesRetainedGeometryWithoutAllocation()
    {
        Span<byte> destination = stackalloc byte[4096];
        Span<NativeGeometryPrimitive> primitives = stackalloc NativeGeometryPrimitive[2]
        {
            new(
                NativeGeometryPrimitiveKind.Line,
                new Vector2(2f, 3f),
                new Vector2(42f, 19f),
                new Vector4(1f, 0f, 0f, 1f),
                Matrix3x2.Identity,
                strokeThickness: 2f,
                startCap: NativeStrokeCap.Round,
                endCap: NativeStrokeCap.Square),
            new(
                NativeGeometryPrimitiveKind.Triangle,
                new Vector2(5f, 6f),
                new Vector2(25f, 7f),
                new Vector4(0f, 1f, 0f, 1f),
                Matrix3x2.Identity,
                p2: new Vector2(12f, 30f))
        };
        Span<NativeSceneBrush> brushes = stackalloc NativeSceneBrush[1]
        {
            NativeSceneBrush.Solid(new Vector4(0.2f, 0.4f, 0.8f, 1f))
        };
        Span<uint> brushIndices = stackalloc uint[2] { 0U, 0U };
        var builder = new NativeSceneStreamBuilder(
            destination,
            sceneId: 51U,
            generation: 3U,
            commandCapacity: 1,
            resourceCapacity: 2);
        Assert.True(builder.TryAddGeometryResource(
            100U,
            1U,
            primitives,
            out uint geometry));
        Assert.True(builder.TryAddBrushTableResource(
            101U,
            1U,
            brushes,
            ReadOnlySpan<NativeSceneGradientStop>.Empty,
            out uint brushTable));
        Assert.True(builder.TryDrawGeometry(
            1000U,
            geometry,
            new NativeImageRect(2f, 3f, 40f, 27f),
            brushTable,
            brushIndices));
        Assert.True(builder.TryBuild(out ReadOnlySpan<byte> stream));

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        Assert.Equal(2U, header.ResourceCount);
        Assert.Equal(1U, header.CommandCount);
        var command = MemoryMarshal.Read<NativeMethods.SceneCommand>(
            stream[(int)header.CommandOffset..]);
        Assert.Equal(NativeSceneCommandKind.DrawGeometry, command.Kind);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            stream[(int)header.ResourceOffset..]);
        Assert.Equal(NativeSceneResourceKind.GeometryBatch, resource.Kind);
        Assert.Equal(
            (uint)(primitives.Length * Unsafe.SizeOf<NativeGeometryPrimitive>()),
            resource.PayloadSize);
    }

    [Fact]
    public void SemanticSceneBuilderValidatesCompactPointBatchesTransactionally()
    {
        Span<byte> destination = stackalloc byte[2048];
        Span<Vector2> points = stackalloc Vector2[3]
        {
            new(4f, 5f),
            new(12f, 9f),
            new(20f, 13f)
        };
        Span<NativeScenePointBatch> batches =
            stackalloc NativeScenePointBatch[1]
            {
                new(
                    pointOffset: 0U,
                    pointCount: 3U,
                    radius: 2f,
                    color: Vector4.One,
                    transform: Matrix3x2.Identity,
                    flags: NativePointBatchFlags.Round)
            };
        Span<NativeSceneBrush> brushes = stackalloc NativeSceneBrush[1]
        {
            NativeSceneBrush.Solid(Vector4.One)
        };
        Span<uint> brushIndices = stackalloc uint[1] { 0U };
        var builder = new NativeSceneStreamBuilder(
            destination,
            sceneId: 52U,
            generation: 1U,
            commandCapacity: 1,
            resourceCapacity: 2);

        points[1] = new Vector2(float.NaN, 9f);
        Assert.False(builder.TryAddPointBatchResource(
            100U,
            1U,
            batches,
            points,
            out _));
        points[1] = new Vector2(12f, 9f);
        Assert.True(builder.TryAddPointBatchResource(
            100U,
            1U,
            batches,
            points,
            out uint pointBatch));
        Assert.True(builder.TryAddBrushTableResource(
            101U,
            1U,
            brushes,
            ReadOnlySpan<NativeSceneGradientStop>.Empty,
            out uint brushTable));
        Assert.True(builder.TryDrawPointBatch(
            1000U,
            pointBatch,
            new NativeImageRect(2f, 3f, 20f, 12f),
            brushTable,
            brushIndices));
        Assert.True(builder.TryBuild(out ReadOnlySpan<byte> stream));

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        Assert.Equal(2U, header.ResourceCount);
        Assert.Equal(1U, header.CommandCount);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            stream[(int)header.ResourceOffset..]);
        Assert.Equal(NativeSceneResourceKind.PointBatch, resource.Kind);
        Assert.Equal(
            (uint)Unsafe.SizeOf<NativeScenePointBatch>(),
            resource.PayloadSize);
        Assert.Equal(
            (uint)(points.Length * Unsafe.SizeOf<Vector2>()),
            resource.AuxiliarySize);
    }

    [Fact]
    public void SemanticSceneBuilderIsAllocationFreeAndRejectsUnbalancedScopes()
    {
        Span<byte> destination = stackalloc byte[512];
        Span<byte> payload = stackalloc byte[8];
        payload.Fill(1);

        static bool BuildOnce(Span<byte> bytes, ReadOnlySpan<byte> data)
        {
            var builder = new NativeSceneStreamBuilder(
                bytes,
                1U,
                1U,
                commandCapacity: 1,
                resourceCapacity: 1);
            return builder.TryAddResource(
                    NativeSceneResourceKind.AnalyticBatch,
                    1U,
                    1U,
                    data,
                    out uint resource) &&
                builder.TryDrawAnalytic(
                    1U,
                    resource,
                    new NativeImageRect(0f, 0f, 1f, 1f)) &&
                builder.TryBuild(out _);
        }

        Assert.True(BuildOnce(destination, payload));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int iteration = 0; iteration < 10_000; ++iteration)
        {
            success &= BuildOnce(destination, payload);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var unbalanced = new NativeSceneStreamBuilder(
            destination,
            2U,
            1U,
            commandCapacity: 1,
            resourceCapacity: 0);
        Assert.True(unbalanced.TryPushLayer(1U));
        Assert.False(unbalanced.TryBuild(out _));
        Assert.True(success);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void SemanticSceneBuilderWritesRetainedBrushTablesWithoutAllocation()
    {
        Span<byte> destination = stackalloc byte[4096];
        Span<NativeAnalyticPrimitive> primitives =
            stackalloc NativeAnalyticPrimitive[2];
        primitives[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            1f,
            2f,
            30f,
            20f,
            Vector4.One,
            Matrix3x2.Identity);
        primitives[1] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Ellipse,
            40f,
            2f,
            20f,
            20f,
            Vector4.One,
            Matrix3x2.Identity);
        Span<NativeSceneGradientStop> stops =
            stackalloc NativeSceneGradientStop[2];
        stops[0] = new NativeSceneGradientStop(
            new Vector4(1f, 0f, 0f, 1f),
            0f);
        stops[1] = new NativeSceneGradientStop(
            new Vector4(0f, 0f, 1f, 1f),
            1f);
        Span<NativeSceneBrush> brushes =
            stackalloc NativeSceneBrush[2];
        brushes[0] = NativeSceneBrush.Solid(
            new Vector4(0.25f, 0.5f, 0.75f, 1f));
        brushes[1] = NativeSceneBrush.LinearGradient(
            Vector2.Zero,
            new Vector2(64f, 0f),
            0U,
            stops);
        Span<uint> brushIndices = stackalloc uint[2] { 0U, 1U };

        static bool BuildBrushScene(
            Span<byte> bytes,
            ReadOnlySpan<NativeAnalyticPrimitive> primitives,
            ReadOnlySpan<NativeSceneBrush> brushes,
            ReadOnlySpan<NativeSceneGradientStop> stops,
            ReadOnlySpan<uint> brushIndices,
            out ReadOnlySpan<byte> stream)
        {
            stream = default;
            var builder = new NativeSceneStreamBuilder(
                bytes,
                sceneId: 21U,
                generation: 4U,
                commandCapacity: 1,
                resourceCapacity: 2);
            return builder.TryAddAnalyticResource(
                    1U,
                    1U,
                    primitives,
                    out uint analyticIndex) &&
                builder.TryAddBrushTableResource(
                    2U,
                    1U,
                    brushes,
                    stops,
                    out uint brushIndex) &&
                builder.TryDrawAnalytic(
                    1U,
                    analyticIndex,
                    new NativeImageRect(0f, 0f, 64f, 32f),
                    brushIndex,
                    brushIndices) &&
                builder.TryBuild(out stream);
        }

        Assert.True(BuildBrushScene(
            destination,
            primitives,
            brushes,
            stops,
            brushIndices,
            out var stream));
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        var brushResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            stream[((int)header.ResourceOffset + 48)..]);
        var command = MemoryMarshal.Read<NativeMethods.SceneCommand>(
            stream[(int)header.CommandOffset..]);
        var drawBrushes = MemoryMarshal.Read<NativeSceneDrawBrushes>(
            stream[(int)command.PayloadOffset..]);
        var storedGradient = MemoryMarshal.Read<NativeSceneBrush>(
            stream[((int)brushResource.PayloadOffset + 256)..]);
        Assert.Equal(NativeSceneResourceKind.BrushTable, brushResource.Kind);
        Assert.Equal(512U, brushResource.PayloadSize);
        Assert.Equal(64U, brushResource.AuxiliarySize);
        Assert.Equal(16U + 2U * sizeof(uint), command.PayloadSize);
        Assert.Equal(1U, drawBrushes.BrushResourceIndex);
        Assert.Equal(2U, drawBrushes.BrushCount);
        Assert.Equal(NativeSceneBrushKind.LinearGradient, storedGradient.Kind);
        Assert.Equal(2U, storedGradient.StopCount);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), storedGradient.Color0);
        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), storedGradient.Color1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int iteration = 0; iteration < 10_000; ++iteration)
        {
            success &= BuildBrushScene(
                destination,
                primitives,
                brushes,
                stops,
                brushIndices,
                out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(success);
        Assert.Equal(0L, allocated);

        Span<NativeAnalyticPrimitive> one =
            stackalloc NativeAnalyticPrimitive[1];
        one[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            0f,
            0f,
            1f,
            1f,
            Vector4.One,
            Matrix3x2.Identity);
        Span<NativeSceneBrush> solid = stackalloc NativeSceneBrush[1];
        solid[0] = NativeSceneBrush.Solid(Vector4.One);
        var invalid = new NativeSceneStreamBuilder(
            destination,
            22U,
            1U,
            commandCapacity: 1,
            resourceCapacity: 2);
        Assert.True(invalid.TryAddAnalyticResource(
            1U, 1U, one, out uint analytic));
        Assert.True(invalid.TryAddBrushTableResource(
            2U, 1U, solid, [], out uint brush));
        Span<uint> wrongCount = stackalloc uint[2] { 0U, 0U };
        Assert.False(invalid.TryDrawAnalytic(
            1U,
            analytic,
            new NativeImageRect(0f, 0f, 1f, 1f),
            brush,
            wrongCount));
        Span<uint> wrongIndex = stackalloc uint[1] { 1U };
        Assert.False(invalid.TryDrawAnalytic(
            1U,
            analytic,
            new NativeImageRect(0f, 0f, 1f, 1f),
            brush,
            wrongIndex));
    }

    [Fact]
    public void SemanticSceneBuilderWritesTypedAbsoluteStateReferences()
    {
        Span<byte> destination = stackalloc byte[2048];
        Span<NativeAnalyticPrimitive> analytic =
            stackalloc NativeAnalyticPrimitive[1];
        analytic[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            1f,
            2f,
            3f,
            4f,
            new Vector4(1f),
            Matrix3x2.Identity);
        var state = new NativeSceneState(
            Matrix3x2.CreateScale(2f) *
                Matrix3x2.CreateTranslation(5f, 7f),
            opacity: 0.5f,
            flags: NativeSceneStateFlags.ClipRect,
            clipRect: new NativeImageRect(1f, 2f, 30f, 40f));
        var builder = new NativeSceneStreamBuilder(
            destination,
            sceneId: 8U,
            generation: 2U,
            commandCapacity: 4,
            resourceCapacity: 2);
        Assert.True(builder.TryAddAnalyticResource(
            1U,
            1U,
            analytic,
            out uint analyticIndex));
        Assert.True(builder.TryAddStateResource(
            2U,
            1U,
            state,
            out uint stateIndex));
        Assert.False(builder.TrySave(9U, analyticIndex));
        Assert.True(builder.TrySave(10U, stateIndex));
        Assert.True(builder.TryDrawAnalytic(
            11U,
            analyticIndex,
            new NativeImageRect(0f, 0f, 10f, 10f)));
        Assert.True(builder.TryRestore(12U));
        Assert.True(builder.TryDrawAnalytic(
            13U,
            analyticIndex,
            new NativeImageRect(0f, 0f, 10f, 10f),
            stateIndex: stateIndex));
        Assert.True(builder.TryBuild(out ReadOnlySpan<byte> stream));

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        var stateResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            stream[((int)header.ResourceOffset + 48)..]);
        var storedState = MemoryMarshal.Read<NativeSceneState>(
            stream[(int)stateResource.PayloadOffset..]);
        Assert.Equal(NativeSceneResourceKind.State, stateResource.Kind);
        Assert.Equal(64U, stateResource.PayloadSize);
        Assert.Equal(0.5f, storedState.Opacity);
        Assert.Equal(state.Transform, storedState.Transform);
        Assert.Equal(NativeSceneStateFlags.ClipRect, storedState.Flags);
        Assert.Equal(1f, storedState.ClipRect.X);
        Assert.Equal(2f, storedState.ClipRect.Y);
        Assert.Equal(30f, storedState.ClipRect.Width);
        Assert.Equal(40f, storedState.ClipRect.Height);

        var save = MemoryMarshal.Read<NativeMethods.SceneCommand>(
            stream[(int)header.CommandOffset..]);
        var inheritedDraw = MemoryMarshal.Read<NativeMethods.SceneCommand>(
            stream[((int)header.CommandOffset + 64)..]);
        var overrideDraw = MemoryMarshal.Read<NativeMethods.SceneCommand>(
            stream[((int)header.CommandOffset + 192)..]);
        Assert.Equal(stateIndex, save.StateIndex);
        Assert.Equal(NativeMethods.SceneNoIndex, inheritedDraw.StateIndex);
        Assert.Equal(stateIndex, overrideDraw.StateIndex);
    }

    [Fact]
    public void SemanticPerlinBrushTableIsBoundedAndAllocationFree()
    {
        Span<byte> destination = stackalloc byte[24 * 1024];
        Span<NativeSceneGradientStop> table =
            stackalloc NativeSceneGradientStop[
                checked((int)NativeSceneBrush.PerlinTableRecordCount)];
        for (int index = 0; index < table.Length; index++)
        {
            table[index] = new NativeSceneGradientStop(
                new Vector4(
                    (index & 1) == 0 ? -0.5f : 0.5f,
                    (index & 2) == 0 ? -0.25f : 0.25f,
                    0.75f,
                    -0.75f),
                (index * 73) & 255);
        }
        Span<NativeSceneBrush> brushes = stackalloc NativeSceneBrush[1];
        brushes[0] = NativeSceneBrush.PerlinNoise(
            new Vector2(0.08f, 0.12f),
            new Vector2(16f, 16f),
            new Vector2(128f, 128f),
            17f,
            3U,
            turbulence: true,
            useExactTable: true,
            coordinateTransform: new Matrix3x2(
                0.5f, 0f, 0f, 0.75f, 2f, -1f));

        static bool Build(
            Span<byte> bytes,
            ReadOnlySpan<NativeSceneBrush> brushTable,
            ReadOnlySpan<NativeSceneGradientStop> stopTable)
        {
            var builder = new NativeSceneStreamBuilder(
                bytes,
                31U,
                1U,
                commandCapacity: 0,
                resourceCapacity: 1);
            return builder.TryAddBrushTableResource(
                    1U,
                    1U,
                    brushTable,
                    stopTable,
                    out _) &&
                builder.TryBuild(out _);
        }

        Assert.True(Build(destination, brushes, table));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            success &= Build(destination, brushes, table);
        }
        Assert.True(success);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.False(Build(destination, brushes, table[..^1]));

        brushes[0] = NativeSceneBrush.PerlinNoise(
            new Vector2(0.08f, 0.12f),
            Vector2.Zero,
            Vector2.Zero,
            17f,
            3U,
            turbulence: false);
        Assert.True(Build(destination, brushes, []));
    }

    [Fact]
    public void SemanticSceneBuilderWritesTypedLayersWithoutAllocation()
    {
        Span<byte> destination = stackalloc byte[4096];
        var layer = new NativeSceneLayer(
            opacity: 0.5f,
            blendMode: GpuBlendMode.Overlay,
            flags: NativeSceneLayerFlags.Bounds |
                NativeSceneLayerFlags.Backdrop |
                NativeSceneLayerFlags.ForceIsolation,
            bounds: new NativeImageRect(2f, 3f, 40f, 50f),
            contentRevision: 7U,
            compositeRevision: 9U);

        static bool BuildLayer(
            Span<byte> bytes,
            in NativeSceneLayer descriptor,
            out ReadOnlySpan<byte> stream)
        {
            stream = default;
            var builder = new NativeSceneStreamBuilder(
                bytes,
                sceneId: 9U,
                generation: 3U,
                commandCapacity: 2,
                resourceCapacity: 0);
            return builder.TryPushLayer(1U, in descriptor) &&
                builder.TryPopLayer(2U) &&
                builder.TryBuild(out stream);
        }

        Assert.True(BuildLayer(destination, in layer, out var stream));
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        var push = MemoryMarshal.Read<NativeMethods.SceneCommand>(
            stream[(int)header.CommandOffset..]);
        var stored = MemoryMarshal.Read<NativeSceneLayer>(
            stream[(int)push.PayloadOffset..]);
        Assert.Equal(64U, push.PayloadSize);
        Assert.Equal(layer.Flags, stored.Flags);
        Assert.Equal(layer.Bounds.X, stored.Bounds.X);
        Assert.Equal(layer.Bounds.Y, stored.Bounds.Y);
        Assert.Equal(layer.Bounds.Width, stored.Bounds.Width);
        Assert.Equal(layer.Bounds.Height, stored.Bounds.Height);
        Assert.Equal(0.5f, stored.Opacity);
        Assert.Equal(GpuBlendMode.Overlay, stored.BlendMode);
        Assert.Equal(uint.MaxValue, stored.MaskResourceIndex);
        Assert.Equal(uint.MaxValue, stored.EffectResourceIndex);
        Assert.Equal(7UL, stored.ContentRevision);
        Assert.Equal(9UL, stored.CompositeRevision);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int iteration = 0; iteration < 10_000; ++iteration)
        {
            success &= BuildLayer(destination, in layer, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(success);
        Assert.Equal(0L, allocated);

        NativeSceneLayer invalid = default;
        var invalidBuilder = new NativeSceneStreamBuilder(
            destination,
            10U,
            1U,
            commandCapacity: 1,
            resourceCapacity: 0);
        Assert.False(invalidBuilder.TryPushLayer(1U, in invalid));

        invalid = new NativeSceneLayer(
            flags: NativeSceneLayerFlags.None,
            bounds: new NativeImageRect(1f, 2f, 3f, 4f));
        invalidBuilder = new NativeSceneStreamBuilder(
            destination,
            10U,
            2U,
            commandCapacity: 1,
            resourceCapacity: 0);
        Assert.False(invalidBuilder.TryPushLayer(1U, in invalid));

        invalid = new NativeSceneLayer(maskResourceIndex: 0U);
        invalidBuilder = new NativeSceneStreamBuilder(
            destination,
            10U,
            3U,
            commandCapacity: 1,
            resourceCapacity: 0);
        Assert.False(invalidBuilder.TryPushLayer(1U, in invalid));

        Span<byte> encodedLayer = stackalloc byte[64];
        MemoryMarshal.Write(encodedLayer, in layer);
        encodedLayer[56] = 1;
        invalid = MemoryMarshal.Read<NativeSceneLayer>(encodedLayer);
        invalidBuilder = new NativeSceneStreamBuilder(
            destination,
            10U,
            4U,
            commandCapacity: 1,
            resourceCapacity: 0);
        Assert.False(invalidBuilder.TryPushLayer(1U, in invalid));
    }

    [Fact]
    public void SemanticSceneBuilderWritesTypedLayerResourcesWithoutAllocation()
    {
        Span<byte> destination = stackalloc byte[4096];
        var mask = new NativeSceneLayerMask(
            new NativeImageRect(4f, 5f, 24f, 16f),
            Matrix3x2.CreateTranslation(2f, 3f),
            new Vector4(3f, 4f, 5f, 6f),
            new Vector4(6f, 5f, 4f, 3f),
            opacity: 0.75f);
        var effect = NativeSceneEffect.GaussianBlur(2f, 1.5f, revision: 3U);

        static bool BuildLayerResources(
            Span<byte> bytes,
            in NativeSceneLayerMask maskDescriptor,
            in NativeSceneEffect effectDescriptor,
            out ReadOnlySpan<byte> stream)
        {
            stream = default;
            Span<NativeSceneEffect> effects = stackalloc NativeSceneEffect[1];
            effects[0] = effectDescriptor;
            var builder = new NativeSceneStreamBuilder(
                bytes,
                sceneId: 13U,
                generation: 2U,
                commandCapacity: 2,
                resourceCapacity: 2);
            if (!builder.TryAddLayerMaskResource(
                    1U,
                    1U,
                    in maskDescriptor,
                    out uint maskIndex) ||
                !builder.TryAddEffectChainResource(
                    2U,
                    1U,
                    effects,
                    revision: 9U,
                    out uint effectIndex))
            {
                return false;
            }

            var layer = new NativeSceneLayer(
                opacity: 0.5f,
                flags: NativeSceneLayerFlags.Bounds |
                    NativeSceneLayerFlags.ForceIsolation,
                bounds: new NativeImageRect(0f, 0f, 32f, 24f),
                maskResourceIndex: maskIndex,
                effectResourceIndex: effectIndex,
                contentRevision: 11U,
                compositeRevision: 12U);
            return builder.TryPushLayer(1U, in layer) &&
                builder.TryPopLayer(2U) &&
                builder.TryBuild(out stream);
        }

        Assert.True(BuildLayerResources(
            destination,
            in mask,
            in effect,
            out var stream));
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        var maskResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            stream[(int)header.ResourceOffset..]);
        var effectResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            stream[((int)header.ResourceOffset + 48)..]);
        Assert.Equal(NativeSceneResourceKind.LayerMask, maskResource.Kind);
        Assert.Equal(104U, maskResource.PayloadSize);
        Assert.Equal(NativeSceneResourceKind.EffectChain, effectResource.Kind);
        Assert.Equal(16U, effectResource.PayloadSize);
        Assert.Equal(56U, effectResource.AuxiliarySize);

        var storedMask = MemoryMarshal.Read<NativeSceneLayerMask>(
            stream[(int)maskResource.PayloadOffset..]);
        var storedChain = MemoryMarshal.Read<NativeSceneEffectChain>(
            stream[(int)effectResource.PayloadOffset..]);
        var storedEffect = MemoryMarshal.Read<NativeSceneEffect>(
            stream[(int)effectResource.AuxiliaryOffset..]);
        Assert.Equal(NativeSceneLayerMaskKind.RoundedRectangle, storedMask.Kind);
        Assert.Equal(0.75f, storedMask.Opacity);
        Assert.Equal(1U, storedChain.EffectCount);
        Assert.Equal(9U, storedChain.Revision);
        Assert.Equal(NativeGroupEffectKind.GaussianBlur, storedEffect.Kind);
        Assert.Equal(2f, storedEffect.SigmaX);
        Assert.Equal(1.5f, storedEffect.SigmaY);

        var push = MemoryMarshal.Read<NativeMethods.SceneCommand>(
            stream[(int)header.CommandOffset..]);
        var storedLayer = MemoryMarshal.Read<NativeSceneLayer>(
            stream[(int)push.PayloadOffset..]);
        Assert.Equal(0U, storedLayer.MaskResourceIndex);
        Assert.Equal(1U, storedLayer.EffectResourceIndex);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int iteration = 0; iteration < 10_000; ++iteration)
        {
            success &= BuildLayerResources(
                destination,
                in mask,
                in effect,
                out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(success);
        Assert.Equal(0L, allocated);

        var invalidMask = new NativeSceneLayerMask(
            new NativeImageRect(0f, 0f, 8f, 8f),
            new Matrix3x2(),
            Vector4.Zero,
            Vector4.Zero);
        var invalidBuilder = new NativeSceneStreamBuilder(
            destination,
            14U,
            1U,
            commandCapacity: 1,
            resourceCapacity: 1);
        Assert.False(invalidBuilder.TryAddLayerMaskResource(
            1U,
            1U,
            in invalidMask,
            out _));

        var unrepresentableInverseMask = new NativeSceneLayerMask(
            new NativeImageRect(0f, 0f, 8f, 8f),
            new Matrix3x2(1.0e-39f, 0f, 0f, 3.0e34f, 0f, 0f),
            Vector4.Zero,
            Vector4.Zero);
        invalidBuilder = new NativeSceneStreamBuilder(
            destination,
            15U,
            1U,
            commandCapacity: 1,
            resourceCapacity: 1);
        Assert.False(invalidBuilder.TryAddLayerMaskResource(
            1U,
            1U,
            in unrepresentableInverseMask,
            out _));

        Span<NativeSceneEffect> invalidEffects = stackalloc NativeSceneEffect[1];
        invalidEffects[0] = NativeSceneEffect.GaussianBlur(
            0f,
            1f,
            revision: 1U);
        invalidBuilder = new NativeSceneStreamBuilder(
            destination,
            16U,
            1U,
            commandCapacity: 1,
            resourceCapacity: 1);
        Assert.False(invalidBuilder.TryAddEffectChainResource(
            1U,
            1U,
            invalidEffects,
            revision: 1U,
            out _));

        Span<NativeSceneEffect> validEffects = stackalloc NativeSceneEffect[1];
        validEffects[0] = effect;
        var wrongKindBuilder = new NativeSceneStreamBuilder(
            destination,
            17U,
            1U,
            commandCapacity: 2,
            resourceCapacity: 1);
        Assert.True(wrongKindBuilder.TryAddEffectChainResource(
            1U,
            1U,
            validEffects,
            revision: 1U,
            out uint wrongKindIndex));
        var wrongKindLayer = new NativeSceneLayer(
            flags: NativeSceneLayerFlags.ForceIsolation,
            maskResourceIndex: wrongKindIndex);
        Assert.False(wrongKindBuilder.TryPushLayer(1U, in wrongKindLayer));
    }

    [Fact]
    public void SemanticSceneBuilderWritesCoverageMaskWithoutAllocation()
    {
        Span<byte> destination = stackalloc byte[2048];
        Span<byte> coverage = stackalloc byte[16]
        {
            0, 255, 255, 0,
            0, 255, 255, 0,
            255, 255, 255, 255,
            255, 0, 0, 255
        };
        var mask = new NativeSceneLayerCoverageMask(
            4U,
            4U,
            4U,
            new NativeImageRect(0f, 0f, 4f, 4f),
            new Matrix3x2(1f, 0.25f, -0.125f, 1f, 3f, 5f),
            opacity: 0.75f);

        static bool Build(
            Span<byte> bytes,
            ReadOnlySpan<byte> coverageBytes,
            in NativeSceneLayerCoverageMask descriptor,
            out ReadOnlySpan<byte> stream)
        {
            stream = default;
            var builder = new NativeSceneStreamBuilder(
                bytes,
                18U,
                2U,
                commandCapacity: 2,
                resourceCapacity: 1);
            if (!builder.TryAddLayerCoverageMaskResource(
                    1U,
                    1U,
                    in descriptor,
                    coverageBytes,
                    out uint maskIndex))
            {
                return false;
            }
            var layer = new NativeSceneLayer(
                flags: NativeSceneLayerFlags.ForceIsolation,
                maskResourceIndex: maskIndex);
            return builder.TryPushLayer(1U, in layer) &&
                builder.TryPopLayer(2U) && builder.TryBuild(out stream);
        }

        Assert.True(Build(destination, coverage, in mask, out var stream));
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            stream[(int)header.ResourceOffset..]);
        var stored = MemoryMarshal.Read<NativeSceneLayerCoverageMask>(
            stream[(int)resource.PayloadOffset..]);
        Assert.Equal(80U, resource.PayloadSize);
        Assert.Equal(16U, resource.AuxiliarySize);
        Assert.Equal(NativeSceneLayerMaskKind.CoverageBitmap, stored.Kind);
        Assert.Equal(0.75f, stored.Opacity);
        Assert.True(stream.Slice(
            (int)resource.AuxiliaryOffset,
            (int)resource.AuxiliarySize).SequenceEqual(coverage));

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int iteration = 0; iteration < 10_000; ++iteration)
        {
            success &= Build(destination, coverage, in mask, out _);
        }
        Assert.True(success);
        Assert.Equal(
            0L,
            GC.GetAllocatedBytesForCurrentThread() - before);

        var invalidBuilder = new NativeSceneStreamBuilder(
            destination,
            19U,
            1U,
            commandCapacity: 1,
            resourceCapacity: 1);
        Assert.False(invalidBuilder.TryAddLayerCoverageMaskResource(
            1U,
            1U,
            in mask,
            coverage[..^1],
            out _));
    }

    [Fact]
    public void SemanticSceneBuilderBoundsMaterializedLayerDepth()
    {
        Span<byte> destination = stackalloc byte[8192];
        var builder = new NativeSceneStreamBuilder(
            destination,
            11U,
            1U,
            commandCapacity: 34,
            resourceCapacity: 0);
        for (ulong commandId = 1U; commandId <= 17U; ++commandId)
        {
            Assert.True(builder.TryPushLayer(commandId));
        }
        for (ulong commandId = 18U; commandId <= 34U; ++commandId)
        {
            Assert.True(builder.TryPopLayer(commandId));
        }
        Assert.True(builder.TryBuild(out _));

        var materialized = new NativeSceneLayer(
            flags: NativeSceneLayerFlags.ForceIsolation);
        builder = new NativeSceneStreamBuilder(
            destination,
            12U,
            1U,
            commandCapacity: 32,
            resourceCapacity: 0);
        for (ulong commandId = 1U; commandId <= 16U; ++commandId)
        {
            Assert.True(builder.TryPushLayer(commandId, in materialized));
        }
        Assert.False(builder.TryPushLayer(17U, in materialized));
        for (ulong commandId = 18U; commandId <= 33U; ++commandId)
        {
            Assert.True(builder.TryPopLayer(commandId));
        }
        Assert.True(builder.TryBuild(out _));
    }

    [Fact]
    public void SemanticSceneBuilderWritesTypedMixedPayloadsWithoutAllocation()
    {
        Span<byte> destination = stackalloc byte[4096];
        Span<NativeAnalyticPrimitive> analytic = stackalloc NativeAnalyticPrimitive[1];
        Span<NativeScenePathFill> paths = stackalloc NativeScenePathFill[1];
        Span<NativeSceneGlyphOutline> outlines = stackalloc NativeSceneGlyphOutline[1];
        Span<NativePathSegment> segments = stackalloc NativePathSegment[1];
        Span<NativePositionedGlyph> glyphs = stackalloc NativePositionedGlyph[1];
        Span<NativeSceneTextStyle> textStyles =
            stackalloc NativeSceneTextStyle[1];
        Span<byte> pixels = stackalloc byte[4];
        analytic[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Rectangle,
            1f,
            2f,
            3f,
            4f,
            new Vector4(1f),
            Matrix3x2.Identity);
        paths[0] = new NativeScenePathFill(
            0,
            1,
            Vector2.Zero,
            Vector2.One,
            new Vector4(1f),
            Matrix3x2.Identity,
            NativeFillRule.NonZero,
            sampleGrid: 4);
        outlines[0] = new NativeSceneGlyphOutline(
            0,
            1,
            Vector2.Zero,
            Vector2.One,
            1f,
            0f);
        segments[0] = new NativePathSegment(
            NativePathSegmentKind.Line,
            Vector2.Zero,
            Vector2.One);
        glyphs[0] = new NativePositionedGlyph(
            0,
            Vector2.Zero,
            Vector2.UnitX,
            Vector2.UnitY,
            new Vector4(1f));
        textStyles[0] = new NativeSceneTextStyle(
            new Vector4(0.25f, 0.5f, 0.75f, 0.8f),
            NativeSceneTextRenderingMode.Grayscale);
        pixels.Fill(0xff);
        var image = new NativeSceneImageDraw(
            1,
            1,
            4,
            NativeImageSampling.Nearest,
            new NativeImageRect(0f, 0f, 1f, 1f),
            new NativeImageRect(8f, 8f, 1f, 1f),
            Matrix3x2.Identity,
            1f);

        static bool Build(
            Span<byte> bytes,
            ReadOnlySpan<NativeAnalyticPrimitive> analyticPayload,
            ReadOnlySpan<NativeScenePathFill> pathPayload,
            ReadOnlySpan<NativeSceneGlyphOutline> outlinePayload,
            ReadOnlySpan<NativePathSegment> segmentPayload,
            ReadOnlySpan<NativePositionedGlyph> glyphPayload,
            ReadOnlySpan<NativeSceneTextStyle> textStylePayload,
            ReadOnlySpan<byte> imagePayload,
            in NativeSceneImageDraw imageDraw)
        {
            var builder = new NativeSceneStreamBuilder(
                bytes,
                88U,
                3U,
                commandCapacity: 5,
                resourceCapacity: 5);
            return builder.TryAddAnalyticResource(
                    1U, 1U, analyticPayload, out uint analyticResource) &&
                builder.TryAddPathResource(
                    2U,
                    1U,
                    pathPayload,
                    segmentPayload,
                    out uint pathResource) &&
                builder.TryAddGlyphResource(
                    3U,
                    1U,
                    outlinePayload,
                    segmentPayload,
                    out uint glyphResource) &&
                builder.TryAddTextStyleResource(
                    4U,
                    1U,
                    textStylePayload,
                    out uint textStyleResource) &&
                builder.TryAddImageResource(
                    5U, 1U, imagePayload, out uint imageResource) &&
                builder.TryDrawAnalytic(
                    1U,
                    analyticResource,
                    new NativeImageRect(0f, 0f, 4f, 4f)) &&
                builder.TryDrawPath(
                    2U,
                    pathResource,
                    new NativeImageRect(0f, 0f, 1f, 1f)) &&
                builder.TryDrawGlyphRun(
                    3U,
                    glyphResource,
                    new NativeImageRect(0f, 0f, 1f, 1f),
                    glyphPayload) &&
                builder.TryDrawGlyphRun(
                    4U,
                    glyphResource,
                    new NativeImageRect(0f, 0f, 1f, 1f),
                    glyphPayload,
                    textStyleResource,
                    styleIndex: 0U) &&
                builder.TryDrawImage(
                    5U,
                    imageResource,
                    new NativeImageRect(8f, 8f, 1f, 1f),
                    in imageDraw) &&
                builder.TryBuild(out _);
        }

        Assert.True(Build(
            destination,
            analytic,
            paths,
            outlines,
            segments,
            glyphs,
            textStyles,
            pixels,
            in image));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int iteration = 0; iteration < 10_000; ++iteration)
        {
            success &= Build(
                destination,
                analytic,
                paths,
                outlines,
                segments,
                glyphs,
                textStyles,
                pixels,
                in image);
        }
        Assert.True(success);
        Assert.Equal(
            0L,
            GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void SemanticCubicImageBuilderRequiresCanonicalSuffixWithoutAllocation()
    {
        Span<byte> destination = stackalloc byte[1024];
        Span<byte> pixels = stackalloc byte[16];
        var image = new NativeSceneImageDraw(
            2,
            2,
            8,
            NativeImageSampling.Cubic,
            new NativeImageRect(0f, 0f, 2f, 2f),
            new NativeImageRect(0f, 0f, 8f, 8f),
            Matrix3x2.Identity,
            1f);
        var sampling = NativeSceneImageSamplingOptions.Mitchell;

        static bool Build(
            Span<byte> bytes,
            ReadOnlySpan<byte> imagePayload,
            in NativeSceneImageDraw draw,
            in NativeSceneImageSamplingOptions options)
        {
            var builder = new NativeSceneStreamBuilder(
                bytes,
                91U,
                1U,
                commandCapacity: 1,
                resourceCapacity: 1);
            return builder.TryAddImageResource(
                    1U, 1U, imagePayload, out uint resource) &&
                !builder.TryDrawImage(
                    1U,
                    resource,
                    new NativeImageRect(0f, 0f, 8f, 8f),
                    in draw) &&
                builder.TryDrawImage(
                    1U,
                    resource,
                    new NativeImageRect(0f, 0f, 8f, 8f),
                    in draw,
                    in options) &&
                builder.TryBuild(out _);
        }

        Assert.True(Build(destination, pixels, in image, in sampling));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int iteration = 0; iteration < 10_000; ++iteration)
        {
            success &= Build(destination, pixels, in image, in sampling);
        }
        Assert.True(success);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);

        var invalid = new NativeSceneImageSamplingOptions(float.NaN, 0.5f);
        var invalidBuilder = new NativeSceneStreamBuilder(
            destination, 92U, 1U, commandCapacity: 1, resourceCapacity: 1);
        Assert.True(invalidBuilder.TryAddImageResource(
            1U, 1U, pixels, out uint invalidResource));
        Assert.False(invalidBuilder.TryDrawImage(
            1U,
            invalidResource,
            new NativeImageRect(0f, 0f, 8f, 8f),
            in image,
            in invalid));
    }

    [Fact]
    public void SemanticImageColorMatrixCombinesWithCubicWithoutAllocation()
    {
        Span<byte> destination = stackalloc byte[2048];
        Span<byte> pixels = stackalloc byte[16];
        var image = new NativeSceneImageDraw(
            2,
            2,
            8,
            NativeImageSampling.Cubic,
            new NativeImageRect(0f, 0f, 2f, 2f),
            new NativeImageRect(0f, 0f, 8f, 8f),
            Matrix3x2.Identity,
            1f,
            NativeSceneImageFlags.ColorMatrix);
        var sampling = NativeSceneImageSamplingOptions.Mitchell;
        var matrix = new NativeSceneImageColorMatrix(
            new Vector4(0.2126f, 0.7152f, 0.0722f, 0f),
            new Vector4(0.2126f, 0.7152f, 0.0722f, 0f),
            new Vector4(0.2126f, 0.7152f, 0.0722f, 0f),
            Vector4.UnitW,
            Vector4.Zero);

        static bool Build(
            Span<byte> bytes,
            ReadOnlySpan<byte> imagePayload,
            in NativeSceneImageDraw draw,
            in NativeSceneImageSamplingOptions options,
            in NativeSceneImageColorMatrix colorMatrix)
        {
            var builder = new NativeSceneStreamBuilder(
                bytes,
                93U,
                1U,
                commandCapacity: 1,
                resourceCapacity: 1);
            return builder.TryAddImageResource(
                    1U, 1U, imagePayload, out uint resource) &&
                builder.TryDrawImage(
                    1U,
                    resource,
                    new NativeImageRect(0f, 0f, 8f, 8f),
                    in draw,
                    in options,
                    in colorMatrix) &&
                builder.TryBuild(out _);
        }

        Assert.True(Build(
            destination,
            pixels,
            in image,
            in sampling,
            in matrix));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int iteration = 0; iteration < 10_000; ++iteration)
        {
            success &= Build(
                destination,
                pixels,
                in image,
                in sampling,
                in matrix);
        }
        Assert.True(success);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);

        var invalid = new NativeSceneImageColorMatrix(
            new Vector4(float.PositiveInfinity),
            Vector4.UnitY,
            Vector4.UnitZ,
            Vector4.UnitW,
            Vector4.Zero);
        var invalidBuilder = new NativeSceneStreamBuilder(
            destination, 94U, 1U, commandCapacity: 1, resourceCapacity: 1);
        Assert.True(invalidBuilder.TryAddImageResource(
            1U, 1U, pixels, out uint invalidResource));
        Assert.False(invalidBuilder.TryDrawImage(
            1U,
            invalidResource,
            new NativeImageRect(0f, 0f, 8f, 8f),
            in image,
            in sampling,
            in invalid));
    }

    [Fact]
    public void SemanticColorGlyphBuilderIsValidatedAndAllocationFree()
    {
        Span<byte> destination = stackalloc byte[1024];
        Span<NativeSceneColorGlyphBitmap> bitmaps =
            stackalloc NativeSceneColorGlyphBitmap[1];
        Span<byte> pixels = stackalloc byte[16];
        bitmaps[0] = new NativeSceneColorGlyphBitmap(
            0U, 2U, 2U, 8U, 1f, -2f, 12f, 14f);
        pixels.Fill(0xff);

        static bool Build(
            Span<byte> bytes,
            ReadOnlySpan<NativeSceneColorGlyphBitmap> bitmapPayload,
            ReadOnlySpan<byte> pixelPayload)
        {
            var builder = new NativeSceneStreamBuilder(
                bytes,
                97U,
                1U,
                commandCapacity: 0,
                resourceCapacity: 1);
            return builder.TryAddColorGlyphResource(
                    1U,
                    1U,
                    bitmapPayload,
                    pixelPayload,
                    out _) &&
                builder.TryBuild(out _);
        }

        Assert.True(Build(destination, bitmaps, pixels));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool success = true;
        for (int iteration = 0; iteration < 10_000; ++iteration)
        {
            success &= Build(destination, bitmaps, pixels);
        }
        Assert.True(success);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);

        bitmaps[0] = new NativeSceneColorGlyphBitmap(
            0U, 2U, 2U, 7U, 0f, 0f);
        Assert.False(Build(destination, bitmaps, pixels));
        bitmaps[0] = new NativeSceneColorGlyphBitmap(
            0U, 16_385U, 1U, 65_540U, 0f, 0f);
        Assert.False(Build(destination, bitmaps, pixels));
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
        Assert.Equal(0, OffsetOf<NativeSceneColorGlyphBitmap>(
            nameof(NativeSceneColorGlyphBitmap.PixelOffset)));
        Assert.Equal(8, OffsetOf<NativeSceneColorGlyphBitmap>(
            nameof(NativeSceneColorGlyphBitmap.Width)));
        Assert.Equal(24, OffsetOf<NativeSceneColorGlyphBitmap>(
            nameof(NativeSceneColorGlyphBitmap.BearX)));
        Assert.Equal(32, OffsetOf<NativeSceneColorGlyphBitmap>(
            nameof(NativeSceneColorGlyphBitmap.RenderWidth)));
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
        Assert.Contains(
            "src/progpu_native_vector_execution.cpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "src/progpu_native_path_execution.cpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "src/progpu_native_glyph_execution.cpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "src/progpu_native_texture_execution.cpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "src/progpu_native_semantic_update_execution.cpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "src/progpu_native_semantic_draw_execution.cpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "src/progpu_native_semantic_render_execution.cpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "src/progpu_native_layer_resource_execution.cpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "src/progpu_native_effect_execution.cpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "src/progpu_native_layer_composite_execution.cpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "src/progpu_native_advanced_blend_execution.cpp",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "../ProGPU.Backend/Shaders/AdvancedBlend.wgsl",
            cmake,
            StringComparison.Ordinal);

        string pipelineSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_pipeline.cpp"));
        Assert.Contains(
            "VectorWgsl.generated.hpp",
            pipelineSource,
            StringComparison.Ordinal);
        string pathTextSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_path_text_resources.cpp"));
        Assert.Contains(
            "GlyphRasterizerWgsl.generated.hpp",
            pathTextSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "TextWgsl.generated.hpp",
            pathTextSource,
            StringComparison.Ordinal);
        string imageLayerSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_image_layer_resources.cpp"));
        string clipSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_clip_resources.cpp"));
        string clipExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_clip_execution.cpp"));
        string imageExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_image_execution.cpp"));
        string layerResourceExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_layer_resource_execution.cpp"));
        string effectExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_effect_execution.cpp"));
        string layerCompositeExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_layer_composite_execution.cpp"));
        string vectorExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_vector_execution.cpp"));
        string pathExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_path_execution.cpp"));
        string glyphExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_glyph_execution.cpp"));
        string textureExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_texture_execution.cpp"));
        string semanticUpdateExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_semantic_update_execution.cpp"));
        string semanticDrawExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_semantic_draw_execution.cpp"));
        string semanticRenderExecutionSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_semantic_render_execution.cpp"));
        string advancedBlendSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_advanced_blend_execution.cpp"));
        string frameExecutionCommonSource = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "src", "progpu_native_frame_execution_common.hpp"));
        Assert.Contains(
            "GaussianBlurHorizontalWgsl.generated.hpp",
            effectExecutionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GroupDropShadowComposeWgsl.generated.hpp",
            effectExecutionSource,
            StringComparison.Ordinal);
        foreach (string source in new[]
                 {
                     pipelineSource,
                     pathTextSource,
                     imageLayerSource,
                     clipSource,
                     clipExecutionSource,
                     imageExecutionSource,
                     layerResourceExecutionSource,
                     effectExecutionSource,
                     layerCompositeExecutionSource,
                     vectorExecutionSource,
                     pathExecutionSource,
                     glyphExecutionSource,
                     textureExecutionSource,
                     semanticUpdateExecutionSource,
                     semanticDrawExecutionSource,
                     semanticRenderExecutionSource,
                     advancedBlendSource,
                     frameExecutionCommonSource,
                 })
        {
            Assert.DoesNotContain("@vertex", source, StringComparison.Ordinal);
            Assert.DoesNotContain("@fragment", source, StringComparison.Ordinal);
            Assert.DoesNotContain("@compute", source, StringComparison.Ordinal);
        }

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
    public void NativeRendererHasARealBrowserWebGpuGate()
    {
        string cmake = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "CMakeLists.txt"));
        string browserHeader = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "include", "progpu_native_browser.h"));
        string browserSmoke = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "browser", "progpu_native_browser_smoke.cpp"));
        string browserEvidence = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "browser", "progpu_native_browser_evidence.cpp"));
        string browserTest = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "browser", "test.mjs"));
        string verifier = File.ReadAllText(FindRepoFile(
            "eng", "progpu-test-native-browser.sh"));
        string workflow = File.ReadAllText(FindRepoFile(
            ".github", "workflows", "build.yml"));

        Assert.Contains("if(EMSCRIPTEN)", cmake, StringComparison.Ordinal);
        Assert.Contains(
            "--use-port=emdawnwebgpu:cpp_bindings=false",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains("-sEXIT_RUNTIME=0", cmake, StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_BROWSER_ADAPTER_ABI_VERSION",
            browserHeader,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_BACKEND_ABI_BROWSER_WEBGPU_2025_10",
            browserSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_CAPABILITY_EXPLICIT_QUEUE_TIMELINE",
            browserSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "create_semantic_backdrop_scene_stream",
            browserSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "semantic_metrics.command_count != 6U",
            browserSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "semantic_metrics.draw_call_count != 6U",
            browserSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "semantic_metrics.submission_count != 1U",
            browserSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "emscripten_request_animation_frame(\n            render_browser_frame",
            browserSmoke,
            StringComparison.Ordinal);
        Assert.Contains("WGPUTextureUsage_CopySrc", browserEvidence,
            StringComparison.Ordinal);
        Assert.Contains("wgpuCommandEncoderCopyTextureToBuffer", browserEvidence,
            StringComparison.Ordinal);
        Assert.DoesNotContain("wgpuCommandEncoderCopyTextureToTexture", browserEvidence,
            StringComparison.Ordinal);
        Assert.Contains("WGPUCallbackMode_AllowSpontaneous", browserEvidence,
            StringComparison.Ordinal);
        Assert.Contains("navigator.gpu", File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "browser", "pre.js")), StringComparison.Ordinal);
        Assert.Contains("uncapturederror", File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "browser", "pre.js")), StringComparison.Ordinal);
        Assert.Contains("channel: \"chromium\"", browserTest, StringComparison.Ordinal);
        Assert.Contains("--enable-unsafe-webgpu", browserTest, StringComparison.Ordinal);
        Assert.DoesNotContain("--enable-features=Vulkan", browserTest,
            StringComparison.Ordinal);
        Assert.Contains("offscreen-texture-readback", browserTest,
            StringComparison.Ordinal);
        Assert.Contains("Browser coverage mask lost the H bridge", browserTest,
            StringComparison.Ordinal);
        Assert.Contains("Browser coverage mask escaped its transformed bounds", browserTest,
            StringComparison.Ordinal);
        Assert.Contains("locator(\"#progpu-native-evidence\").screenshot", browserTest,
            StringComparison.Ordinal);
        Assert.Contains("emcmake", verifier, StringComparison.Ordinal);
        Assert.Contains("native-cpp-browser", workflow, StringComparison.Ordinal);
        Assert.Contains("Upload native browser evidence", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeRendererGeneratesSharedShadersOnceForEveryVariant()
    {
        string cmake = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "CMakeLists.txt"));

        Assert.Contains(
            "add_custom_target(progpu_native_generated_shaders",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "add_dependencies(progpu_native progpu_native_generated_shaders)",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "add_dependencies(progpu_native_dawn progpu_native_generated_shaders)",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_browser_smoke\n        progpu_native_generated_shaders",
            cmake,
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
        string nativeBuild = File.ReadAllText(FindRepoFile(
            "eng", "build-progpu-native.sh"));
        string windowsBuild = File.ReadAllText(FindRepoFile(
            "eng", "build-progpu-native-windows.ps1"));

        Assert.Contains(
            "add_library(progpu_native_dawn ${PROGPU_NATIVE_DAWN_LIBRARY_TYPE}",
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
        Assert.Contains(
            "--dawn --device-loss \"${managed_capture}\"",
            providerVerifier,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU.Scene.Native",
            providerVerifier,
            StringComparison.Ordinal);
        string managedSampleProject = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native.ManagedSample",
            "ProGPU.Native.ManagedSample.csproj"));
        string managedSample = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native.ManagedSample", "Program.cs"));
        Assert.Contains(
            "PackageReference Include=\"ProGPU.Scene.Native\"",
            managedSampleProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuPictureNativeSceneCompiler.TryCompile",
            managedSample,
            StringComparison.Ordinal);
        Assert.Contains(
            "GradientStopUploadBytes",
            managedSample,
            StringComparison.Ordinal);
        Assert.Contains("PushOpacity", managedSample, StringComparison.Ordinal);
        Assert.Contains("PushClip", managedSample, StringComparison.Ordinal);
        Assert.Contains(
            "MaximumStackDepth",
            managedSample,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeCommandCount",
            managedSample,
            StringComparison.Ordinal);
        Assert.Contains("--managed-picture", nativeBuild, StringComparison.Ordinal);
        Assert.Contains("--managed-picture", windowsBuild, StringComparison.Ordinal);

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
    public void NativeRendererBuildsAndPackagesProviderResolvedMobileAdapters()
    {
        string cmake = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "CMakeLists.txt"));
        string builder = File.ReadAllText(FindRepoFile(
            "eng", "build-progpu-native-mobile.sh"));
        string verifier = File.ReadAllText(FindRepoFile(
            "eng", "progpu-verify-native-mobile-package.sh"));
        string android = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Android", "ProGPU.Android.csproj"));
        string ios = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.iOS", "ProGPU.iOS.csproj"));
        string buildWorkflow = File.ReadAllText(FindRepoFile(
            ".github", "workflows", "build.yml"));
        string releaseWorkflow = File.ReadAllText(FindRepoFile(
            ".github", "workflows", "release.yml"));
        string managedAdapter = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Backend.Native", "NativeDawnAdapter.cs"));
        string managedInterop = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Backend.Native", "NativeRendererInterop.cs"));

        Assert.Contains("PROGPU_NATIVE_BUILD_WGPU_TARGET", cmake,
            StringComparison.Ordinal);
        Assert.Contains("PROGPU_NATIVE_DAWN_LIBRARY_TYPE", cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "01addc4ba8a2915a061b7095a6768b512071ab96",
            builder,
            StringComparison.Ordinal);
        Assert.Contains("ANDROID_PLATFORM=android-30", builder,
            StringComparison.Ordinal);
        Assert.Contains("darwin-arm64 darwin-x86_64", builder,
            StringComparison.Ordinal);
        Assert.Contains("linux-x86_64 linux-aarch64", builder,
            StringComparison.Ordinal);
        Assert.Contains("bin/llvm-readelf", builder,
            StringComparison.Ordinal);
        Assert.DoesNotContain("-type f -name llvm-readelf", builder,
            StringComparison.Ordinal);
        Assert.Contains("xcodebuild -create-xcframework", builder,
            StringComparison.Ordinal);
        Assert.Contains("provider-resolved-dawn", builder,
            StringComparison.Ordinal);
        Assert.Contains("runtimes/android-arm64/native", verifier,
            StringComparison.Ordinal);
        Assert.Contains("runtimes/ios/native", verifier,
            StringComparison.Ordinal);
        Assert.Contains("ProGPU.Backend.Native", android,
            StringComparison.Ordinal);
        Assert.Contains("ProGpuNativeDawnAndroidRoot", android,
            StringComparison.Ordinal);
        Assert.Contains("ProGPU.Backend.Native", ios,
            StringComparison.Ordinal);
        Assert.Contains("ProGpuNativeDawnIOSXCFramework", ios,
            StringComparison.Ordinal);
        Assert.Contains("Build provider-resolved mobile C++ renderer",
            buildWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("progpu-verify-native-mobile-package.sh",
            buildWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("Build provider-resolved mobile C++ renderer",
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("progpu-verify-native-mobile-package.sh",
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("CreateCompositor(", managedAdapter,
            StringComparison.Ordinal);
        Assert.Contains("RecreateCompositor(", managedAdapter,
            StringComparison.Ordinal);
        Assert.Contains("ValidateScene(", managedAdapter,
            StringComparison.Ordinal);
        Assert.Contains("ResolveDawnProc", managedAdapter,
            StringComparison.Ordinal);
        Assert.Contains("NativeDawnMethods.RenderScene", managedInterop,
            StringComparison.Ordinal);
        Assert.Contains("NativeDawnMethods.PollSubmission", managedInterop,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeRendererPublishesBackendExplicitHardwareEvidence()
    {
        string sample = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Native", "samples", "progpu_native_sample.cpp"));
        string unixBuild = File.ReadAllText(FindRepoFile(
            "eng", "build-progpu-native.sh"));
        string windowsBuild = File.ReadAllText(FindRepoFile(
            "eng", "build-progpu-native-windows.ps1"));
        string buildWorkflow = File.ReadAllText(FindRepoFile(
            ".github", "workflows", "build.yml"));
        string releaseWorkflow = File.ReadAllText(FindRepoFile(
            ".github", "workflows", "release.yml"));

        Assert.Contains("return WGPUBackendType_D3D12;", sample,
            StringComparison.Ordinal);
        Assert.Contains("return WGPUBackendType_Metal;", sample,
            StringComparison.Ordinal);
        Assert.Contains("return WGPUBackendType_Vulkan;", sample,
            StringComparison.Ordinal);
        Assert.Contains("wgpuAdapterGetProperties", sample,
            StringComparison.Ordinal);
        Assert.Contains(
            "adapter_properties.backendType != platform_backend_type()",
            sample,
            StringComparison.Ordinal);
        Assert.Contains("has_expected_colors", sample,
            StringComparison.Ordinal);
        Assert.Contains("progpu-native-provider.txt", unixBuild,
            StringComparison.Ordinal);
        Assert.Contains("--semantic-layer-effects", unixBuild,
            StringComparison.Ordinal);
        Assert.Contains("progpu_native_sample.exe", windowsBuild,
            StringComparison.Ordinal);
        Assert.Contains("--semantic-layer-effects", windowsBuild,
            StringComparison.Ordinal);
        Assert.Contains("The D3D12 native renderer backend sample failed.",
            windowsBuild,
            StringComparison.Ordinal);
        Assert.Contains("Upload native backend execution evidence", buildWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("Upload native backend execution evidence", releaseWorkflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopNativeSampleUsesTypedSilkAndDawnAdapters()
    {
        string program = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Samples.Desktop", "Program.cs"));
        string wrapper = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Backend.Native", "NativeCompositor.cs"));
        string page = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Samples.Desktop", "NativeRendererSamplePage.cs"));
        string representativeScene = File.ReadAllText(FindRepoFile(
            "src", "ProGPU.Samples.Desktop",
            "NativeRepresentativeSceneSample.cs"));

        Assert.Contains("\"--native-renderer\"", program, StringComparison.Ordinal);
        Assert.Contains("\"--native-renderer-dawn\"", program,
            StringComparison.Ordinal);
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
            "CurrentDawnContext = dawn",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeDawnAdapter.CreateCompositor(",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "--native-renderer only for the direct wgpu-native comparison",
            page,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RenderExternalImage(", page, StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureAlphaMode.Straight",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeBatchMode.RepresentativeScene",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "_compositor.RenderScene(",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeSceneImageSamplingOptions.Mitchell",
            representativeScene,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeSceneImageFlags.ColorMatrix",
            representativeScene,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeSceneLayerFlags.ForceIsolation",
            representativeScene,
            StringComparison.Ordinal);
        Assert.Contains(
            "builder.TryAddEffectChainResource(",
            representativeScene,
            StringComparison.Ordinal);
        Assert.Contains(
            "builder.TryBuild(out stream)",
            representativeScene,
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
