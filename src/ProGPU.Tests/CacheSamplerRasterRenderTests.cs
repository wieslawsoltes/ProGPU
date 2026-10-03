using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

/// <summary>
/// Actual-device controls for the owned raw cache raster, not a WPF source or
/// Microsoft pixel-parity claim. All expected pixels are independent literals;
/// the test never reads a product image to construct its expected result.
/// </summary>
public sealed class CacheSamplerRasterRenderTests
{
    private const uint TargetWidth = 80;
    private const uint TargetHeight = 48;
    private static readonly Rect SourceBounds = new(12, -6, 8, 4);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentCacheDimensionsAndPrimaryAxesReachTheRealShaderSampler(bool useDawn)
    {
        using var device = new RenderDevice(useDawn);
        using var compositor = CreateCompositor(device.Context);
        using var target = CreateTarget(device.Context);
        using var implicitInput = CreateImplicitInput(device.Context);
        using var picture = RecordQuadrants(changed: false);
        var sourceIdentity = new object();

        // Source extent is8x4. These are explicitly selected fixture primary
        // axes, not claims about an OS display. Actual limits are queried below.
        // Neither cache dimension pair equals the16x8 implicit input or64x32
        // effect output, so binding either of those as the cache cannot pass.
        (double Scale, float X, float Y, uint Width, uint Height)[] cases =
        [
            (1, 2, 1, 16, 4),
            (2, .5f, 2, 8, 16),
            (.5, 1, 2, 4, 4)
        ];
        foreach (var item in cases)
        {
            CacheSamplerRasterFrame frame = CreateFrame(device.Context, item.Scale, item.X, item.Y);
            Assert.Equal(item.Width, frame.PixelWidth);
            Assert.Equal(item.Height, frame.PixelHeight);
            using var raster = compositor.CaptureCacheSampler(picture, frame, sourceIdentity, 1,
                enableClearType: false);
            AssertOwnedRaster(raster, frame, device.Context, sourceIdentity, 1);
            Assert.Equal(item.Width, raster.Texture.Width);
            Assert.Equal(item.Height, raster.Texture.Height);
            Assert.False(raster.Frame.IsEmpty);
            Assert.False(raster.EnableClearType);
            using var sampler = WpfShaderEffectSampler.FromCacheRaster(1, raster, TextureSamplingMode.Nearest);
            var effect = CreateEffect(implicitInput, sampler);
            AssertColdAndWarmPixels(compositor, target, effect, ExpectedQuadrants(changed: false));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewSourceRevisionCannotMutateAnEarlierOwnedSamplerGeneration(bool useDawn)
    {
        using var device = new RenderDevice(useDawn);
        using var compositor = CreateCompositor(device.Context);
        using var target = CreateTarget(device.Context);
        using var implicitInput = CreateImplicitInput(device.Context);
        var sourceIdentity = new object();
        CacheSamplerRasterFrame frame = CreateFrame(device.Context, 1, 2, 1);

        using var firstPicture = RecordQuadrants(changed: false);
        using var first = compositor.CaptureCacheSampler(firstPicture, frame, sourceIdentity, 17,
            enableClearType: false);
        firstPicture.Dispose(); // Only the captured owned generation remains.
        AssertOwnedRaster(first, frame, device.Context, sourceIdentity, 17);
        using var firstSampler = WpfShaderEffectSampler.FromCacheRaster(1, first, TextureSamplingMode.Nearest);
        var firstEffect = CreateEffect(implicitInput, firstSampler);
        AssertColdAndWarmPixels(compositor, target, firstEffect, ExpectedQuadrants(changed: false));

        using var changedPicture = RecordQuadrants(changed: true);
        using var changed = compositor.CaptureCacheSampler(changedPicture, frame, sourceIdentity, 18,
            enableClearType: true);
        changedPicture.Dispose();
        AssertOwnedRaster(changed, frame, device.Context, sourceIdentity, 18);
        Assert.True(changed.EnableClearType);
        Assert.NotSame(first.Texture, changed.Texture);
        Assert.NotEqual(first.Texture.Id, changed.Texture.Id);
        using var changedSampler = WpfShaderEffectSampler.FromCacheRaster(1, changed, TextureSamplingMode.Nearest);
        var changedEffect = CreateEffect(implicitInput, changedSampler);
        AssertColdAndWarmPixels(compositor, target, changedEffect, ExpectedQuadrants(changed: true));

        // The same source owner has advanced. Its earlier actual texture must
        // still produce every original byte, not a repainted shared page.
        AssertColdAndWarmPixels(compositor, target, firstEffect, ExpectedQuadrants(changed: false));
        Assert.Equal(17UL, first.SourceRevision);
        Assert.False(first.IsDisposed);
        Assert.False(first.Texture.IsDisposed);
        changed.Dispose();
        Assert.True(changed.IsDisposed);
        Assert.False(changed.Texture.IsDisposed); // Its parameter still owns a lease.
        changedSampler.Dispose();
        Assert.False(first.Texture.IsDisposed);
        AssertColdAndWarmPixels(compositor, target, firstEffect, ExpectedQuadrants(changed: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MismatchedActualDeviceLimitsRejectWithoutDamagingRetainedPixels(bool useDawn)
    {
        using var device = new RenderDevice(useDawn);
        using var compositor = CreateCompositor(device.Context);
        using var target = CreateTarget(device.Context);
        using var implicitInput = CreateImplicitInput(device.Context);
        using var picture = RecordQuadrants(changed: false);
        var sourceIdentity = new object();
        CacheSamplerRasterFrame valid = CreateFrame(device.Context, 1, 2, 1);
        using var raster = compositor.CaptureCacheSampler(picture, valid, sourceIdentity, 23, false);
        using var sampler = WpfShaderEffectSampler.FromCacheRaster(1, raster, TextureSamplingMode.Nearest);
        var effect = CreateEffect(implicitInput, sampler);
        AssertColdAndWarmPixels(compositor, target, effect, ExpectedQuadrants(changed: false));

        // Only these negative frames deliberately contradict the real query.
        // No counterfeit device or limits are used for a positive capture.
        foreach (bool changeWidth in new[] { true, false })
        {
            uint width = valid.MaximumTextureWidth;
            uint height = valid.MaximumTextureHeight;
            if (changeWidth) width = DifferentLimit(width);
            else height = DifferentLimit(height);
            Assert.True(CacheSamplerRasterFrame.TryCreate(12, -6, 8, 4, 1, 2, 1,
                width, height, out var mismatched));
            Assert.Throws<InvalidOperationException>(() =>
                compositor.CaptureCacheSampler(picture, mismatched, sourceIdentity, 24, false));
            AssertOwnedRaster(raster, valid, device.Context, sourceIdentity, 23);
            Assert.False(picture.IsDisposed);
            AssertColdAndWarmPixels(compositor, target, effect, ExpectedQuadrants(changed: false));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownEmptyAndZeroScaleOwnOneActuallyTransparentShaderTexel(bool useDawn)
    {
        using var device = new RenderDevice(useDawn);
        using var compositor = CreateCompositor(device.Context);
        using var target = CreateTarget(device.Context);
        using var implicitInput = CreateImplicitInput(device.Context);
        Assert.True(device.Context.TryGetCacheRasterLimits(out uint maximumWidth, out uint maximumHeight));
        var recorder = new GpuPictureRecorder();
        recorder.BeginRecording(new Rect(0, 0, 0, 0));
        using var empty = recorder.EndRecording();
        using var colored = RecordQuadrants(changed: false);
        Assert.True(CacheSamplerRasterFrame.TryCreate(0, 0, 0, 0, 1, 2, 1,
            maximumWidth, maximumHeight, out var emptyFrame));
        CacheSamplerRasterFrame zeroScaleFrame = CreateFrame(device.Context, 0, 2, 1);

        foreach (var input in new[] { (Picture: empty, Frame: emptyFrame), (Picture: colored, Frame: zeroScaleFrame) })
        {
            var sourceIdentity = new object();
            using var raster = compositor.CaptureCacheSampler(input.Picture, input.Frame,
                sourceIdentity, 29, false);
            AssertOwnedRaster(raster, input.Frame, device.Context, sourceIdentity, 29);
            Assert.True(raster.Frame.IsEmpty);
            Assert.Equal(1U, raster.Texture.Width);
            Assert.Equal(1U, raster.Texture.Height);
            using var sampler = WpfShaderEffectSampler.FromCacheRaster(1, raster, TextureSamplingMode.Nearest);
            var effect = CreateEffect(implicitInput, sampler);
            effect.ShaderKey = "test_raw_cache_sampler_transparent_texel";
            effect.ShaderSource = """
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    let sampled = wpf_sample_register(1u, uv);
    // Nonnegative UNORM components all vanish only for transparent black.
    // Opaque black, hidden RGB, or the yellow implicit input cannot pass.
    return vec4<f32>(sampled.rgb + vec3<f32>(sampled.a), 1.0);
}
""";
            AssertColdAndWarmPixels(compositor, target, effect, ExpectedBackground());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParameterLeaseSurvivesOwnerDisposalAndRejectedReplacement(bool useDawn)
    {
        using var device = new RenderDevice(useDawn);
        using var compositor = CreateCompositor(device.Context);
        using var target = CreateTarget(device.Context);
        using var implicitInput = CreateImplicitInput(device.Context);
        using var picture = RecordQuadrants(changed: false);
        var sourceIdentity = new object();
        CacheSamplerRasterFrame frame = CreateFrame(device.Context, 1, 2, 1);
        using var raster = compositor.CaptureCacheSampler(picture, frame, sourceIdentity, 31, false);
        using var sampler = WpfShaderEffectSampler.FromCacheRaster(1, raster, TextureSamplingMode.Nearest);
        var effect = CreateEffect(implicitInput, sampler);
        raster.Dispose();
        picture.Dispose();

        Assert.True(raster.IsDisposed);
        Assert.False(raster.Texture.IsDisposed);
        Assert.False(raster.TryAcquireGpuTextureLease(out var rejectedLease));
        Assert.Null(rejectedLease);
        AssertColdAndWarmPixels(compositor, target, effect, ExpectedQuadrants(changed: false));

        using var candidatePicture = RecordQuadrants(changed: true);
        using var candidate = compositor.CaptureCacheSampler(candidatePicture, frame, sourceIdentity, 32, false);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WpfShaderEffectSampler.FromCacheRaster(WpfShaderEffectParams.MaxSamplerRegisterCount, candidate));
        Assert.False(candidate.IsDisposed);
        Assert.False(candidate.Texture.IsDisposed);
        candidate.Dispose();
        Assert.True(candidate.Texture.IsDisposed); // No sampler ever acquired it.
        Assert.Throws<ObjectDisposedException>(() => WpfShaderEffectSampler.FromCacheRaster(1, candidate));

        // A failed candidate cannot replace/retire the existing parameter's
        // exact owner, even after its source cache and recording were released.
        Assert.Same(raster.Texture, sampler.Texture);
        Assert.Equal(31UL, raster.SourceRevision);
        Assert.False(raster.Texture.IsDisposed);
        AssertColdAndWarmPixels(compositor, target, effect, ExpectedQuadrants(changed: false));
        sampler.Dispose();
        compositor.Dispose(); // Ends actual compiled/frame ownership as well.
        Assert.True(raster.Texture.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClonedRecordingOwnsSamplerAfterContextParameterAndSourceRetire(bool useDawn)
    {
        using var device = new RenderDevice(useDawn);
        using var compositor = CreateCompositor(device.Context);
        using var target = CreateTarget(device.Context);
        using var implicitInput = CreateImplicitInput(device.Context);
        using var sourcePicture = RecordQuadrants(changed: false);
        CacheSamplerRasterFrame frame = CreateFrame(device.Context, 1, 2, 1);
        using var raster = compositor.CaptureCacheSampler(sourcePicture, frame, new object(), 41, false);
        using var sampler = WpfShaderEffectSampler.FromCacheRaster(1, raster, TextureSamplingMode.Nearest);
        var effect = CreateEffect(implicitInput, sampler);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(new Rect(0, 0, TargetWidth, TargetHeight));
        drawing.DrawWpfShaderEffect(effect);
        using var recorded = recorder.EndRecording();
        using var clone = recorded.Clone();
        Assert.True(recorded.SharesRetainedCommandStorageWith(clone));
        Assert.Equal(1, recorded.RetainedResourceCount);
        Assert.Equal(1, clone.RetainedResourceCount);
        drawing.Clear();
        recorded.Dispose();
        sampler.Dispose();
        raster.Dispose();
        sourcePicture.Dispose();

        Assert.True(raster.IsDisposed);
        Assert.False(raster.Texture.IsDisposed);
        AssertColdAndWarmPixels(compositor, target, new RecordedSamplerVisual(clone), effect,
            ExpectedQuadrants(changed: false));
        compositor.Dispose(); // No compiled consumer may hide the clone control.
        Assert.False(raster.Texture.IsDisposed);
        clone.Dispose();
        Assert.True(raster.Texture.IsDisposed);
        clone.Dispose();
        sampler.Dispose();
        raster.Dispose(); // Repeated owner endings do not release another use.
    }

    private static uint DifferentLimit(uint actual) => actual == uint.MaxValue ? actual - 1 : actual + 1;

    private static Compositor CreateCompositor(WgpuContext context)
    {
        var compositor = new Compositor(context, TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with { EnableGpuHitTesting = false, PrimarySampleCount = 1 });
        compositor.ClearColor = new Vector4(0, 0, 0, 1);
        Assert.IsType<WpfShaderEffectExtensionPipeline>(
            compositor.GetExtension(CompositorBuiltInExtensions.WpfShaderEffect));
        return compositor;
    }

    private static GpuTexture CreateTarget(WgpuContext context) => new(context,
        TargetWidth, TargetHeight, TextureFormat.Rgba8Unorm,
        TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Cache sampler render-control target",
        alphaMode: GpuTextureAlphaMode.Premultiplied);

    private static GpuTexture CreateImplicitInput(WgpuContext context)
    {
        var texture = new GpuTexture(context, 16, 8, TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst, "Independent yellow implicit input");
        var pixels = new byte[16 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 3] = 255;
        }
        texture.WritePixels(pixels);
        return texture;
    }

    private static CacheSamplerRasterFrame CreateFrame(WgpuContext context, double scale, float primaryX, float primaryY)
    {
        Assert.True(context.TryGetCacheRasterLimits(out uint maximumWidth, out uint maximumHeight));
        Assert.NotEqual(0U, maximumWidth);
        Assert.NotEqual(0U, maximumHeight);
        Assert.True(CacheSamplerRasterFrame.TryCreate(12, -6, 8, 4, scale, primaryX, primaryY,
            maximumWidth, maximumHeight, out var frame));
        return frame;
    }

    private static void AssertOwnedRaster(CacheSamplerRaster raster, CacheSamplerRasterFrame frame,
        WgpuContext context, object identity, ulong revision)
    {
        Assert.Equal(frame, raster.Frame);
        Assert.Same(identity, raster.SourceIdentity);
        Assert.Equal(revision, raster.SourceRevision);
        Assert.Same(context.DeviceIdentity, raster.DeviceIdentity);
        Assert.Same(context, raster.Texture.Context);
        Assert.Equal(GpuTextureAlphaMode.Premultiplied, raster.Texture.AlphaMode);
        Assert.False(raster.IsDisposed);
        Assert.False(raster.Texture.IsDisposed);
    }

    private static GpuPicture RecordQuadrants(bool changed)
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(SourceBounds);
        drawing.DrawRectangle(new SolidColorBrush(changed ? new Vector4(0, 1, 1, 1) : new Vector4(1, 0, 0, 1)),
            null, new Rect(12, -6, 4, 2));
        drawing.DrawRectangle(new SolidColorBrush(new Vector4(0, 1, 0, 1)), null, new Rect(16, -6, 4, 2));
        drawing.DrawRectangle(new SolidColorBrush(new Vector4(0, 0, 1, 1)), null, new Rect(12, -4, 4, 2));
        drawing.DrawRectangle(new SolidColorBrush(changed ? new Vector4(1, 0, 1, 1) : Vector4.One),
            null, new Rect(16, -4, 4, 2));
        return recorder.EndRecording();
    }

    private static WpfShaderEffectParams CreateEffect(GpuTexture implicitInput, WpfShaderEffectSampler sampler) => new()
    {
        Texture = implicitInput,
        SourceTextureRegisterIndex = 0,
        Rect = new Rect(8, 8, 64, 32),
        SamplingMode = TextureSamplingMode.Nearest,
        Samplers = [sampler],
        ShaderKey = "test_independent_raw_cache_sampler",
        ShaderSource = """
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    return wpf_sample_register(1u, uv);
}
"""
    };

    private static void AssertColdAndWarmPixels(Compositor compositor, GpuTexture target,
        WpfShaderEffectParams effect, byte[] expected)
        => AssertColdAndWarmPixels(compositor, target, new SamplerVisual(effect), effect, expected);

    private static unsafe void AssertColdAndWarmPixels(Compositor compositor, GpuTexture target,
        FrameworkElement visual, WpfShaderEffectParams effect, byte[] expected)
    {
        visual.Measure(new Vector2(TargetWidth, TargetHeight));
        visual.Arrange(new Rect(0, 0, TargetWidth, TargetHeight));
        for (int replay = 0; replay < 2; replay++)
        {
            compositor.RenderScene(visual, TargetWidth, TargetHeight, target.ViewPtr);
            Assert.False(effect.IsFailed, effect.LastError);
            Assert.Equal(1, compositor.Metrics.DrawCallsCount);
            byte[] actual = target.ReadPixels();
            Assert.Equal(expected, actual);
        }
    }

    private static byte[] ExpectedBackground()
    {
        var expected = new byte[checked((int)(TargetWidth * TargetHeight * 4))];
        for (int i = 3; i < expected.Length; i += 4) expected[i] = 255;
        return expected;
    }

    private static byte[] ExpectedQuadrants(bool changed)
    {
        byte[] expected = ExpectedBackground();
        for (int y = 8; y < 40; y++)
        for (int x = 8; x < 72; x++)
        {
            int i = (y * (int)TargetWidth + x) * 4;
            // Literal output bands: the oracle uses neither the capture frame
            // matrix nor texture dimensions or rendered source pixels.
            (byte r, byte g, byte b) = (x < 40, y < 24, changed) switch
            {
                (true, true, false) => (255, 0, 0),
                (true, true, true) => (0, 255, 255),
                (false, true, _) => (0, 255, 0),
                (true, false, _) => (0, 0, 255),
                (false, false, false) => (255, 255, 255),
                _ => (255, 0, 255)
            };
            expected[i] = r; expected[i + 1] = g; expected[i + 2] = b;
        }
        return expected;
    }

    private sealed class SamplerVisual(WpfShaderEffectParams effect) : FrameworkElement
    {
        public override void OnRender(DrawingContext context) => context.DrawWpfShaderEffect(effect);
    }

    private sealed class RecordedSamplerVisual(GpuPicture picture) : FrameworkElement
    {
        public override void OnRender(DrawingContext context) => context.DrawPicture(picture);
    }

    private sealed class RenderDevice : IDisposable
    {
        private readonly DawnGpuContext? _dawn;
        public WgpuContext Context { get; }

        public RenderDevice(bool useDawn)
        {
            if (useDawn)
            {
                BackendType backend = OperatingSystem.IsWindows() ? BackendType.D3D12 :
                    OperatingSystem.IsMacOS() ? BackendType.Metal : BackendType.Vulkan;
                _dawn = DawnGpuContext.CreateOffscreen(backend, forceFallbackAdapter: false);
                Context = _dawn.Context;
            }
            else Context = HeadlessWindow.Shared.Context;
        }

        public void Dispose() => _dawn?.Dispose();
    }
}
