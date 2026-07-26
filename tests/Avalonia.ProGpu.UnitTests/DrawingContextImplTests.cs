using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Text;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Avalonia.ProGpu.UnitTests
{
    public class DrawingContextImplTests
    {
        [Fact]
        public void ProGpu_Options_Prefer_Dawn_Without_Requiring_It()
        {
            var options = new ProGpuOptions();

            Assert.True(options.UseDawnMetalPresentation);
            Assert.False(options.RequireDawnMetalPresentation);
        }

        [Fact]
        public void Backend_Compositor_Uses_Compact_Core_Reservations_Without_Gpu_Hit_Testing()
        {
            CompositorOptions options = DrawingContextImpl.BackendCompositorOptions;

            Assert.Equal(1024u, options.InitialVertexCount);
            Assert.Equal(1536u, options.InitialIndexCount);
            Assert.Equal(64u, options.InitialBrushCount);
            Assert.Equal(512u, options.InitialGradientStopCount);
            Assert.Equal(260u, options.InitialGlyphAtlasSize);
            Assert.Equal(64u, options.InitialColorGlyphAtlasSize);
            Assert.Equal(16u * 1024u, options.GlyphUniformStagingBytes);
            Assert.Equal(GlyphAtlas.DefaultCoverageRingBufferSize, options.GlyphCoverageStagingBytes);
            Assert.False(options.EnableGpuHitTesting);
        }

        [Fact]
        public void Framebuffer_Render_Target_Supports_Direct_Rendering()
        {
            using var target = new FramebufferRenderTarget(new TestFramebufferPlatformSurface());

            Assert.True(target.Properties.RetainsPreviousFrameContents);
            Assert.True(target.Properties.IsSuitableForDirectRendering);
        }

        [Fact]
        public void Backend_Context_Propagates_Strict_Native_Composition_Option()
        {
            var renderInterface = new PlatformRenderInterface(
                requireNativeCompositionScene: true);
            using var backend = renderInterface.CreateBackendContext(null);
            using var target = Assert.IsType<FramebufferRenderTarget>(
                backend.CreateRenderTarget(
                    new IPlatformRenderSurface[]
                    {
                        new TestFramebufferPlatformSurface()
                    }));

            Assert.True(target.RequireNativeCompositionScene);
        }

        [Fact]
        public void Backend_Context_Reports_Device_Loss_And_Replacement_Starts_Healthy()
        {
            var renderInterface = new PlatformRenderInterface();
            using var existing = renderInterface.CreateBackendContext(null);

            WgpuContext.RaiseWebGpuDeviceLost(
                Silk.NET.WebGPU.DeviceLostReason.Unknown,
                "synthetic Avalonia backend loss");

            Assert.True(existing.IsLost);

            using var replacement = renderInterface.CreateBackendContext(null);

            Assert.False(replacement.IsLost);
        }

        [Fact]
        public void Framebuffer_Render_Target_Forces_Full_Redraw_For_Every_Frame()
        {
            using var target = new FramebufferRenderTarget(
                new TestFramebufferPlatformSurface(createFramebuffer: true));
            using var context = target.CreateDrawingContext(
                new IRenderTarget.RenderTargetSceneInfo(new PixelSize(4, 3), 1),
                out var properties);

            Assert.False(properties.PreviousFrameIsRetained);
        }

        [Fact]
        public void Render_Target_Bitmap_Renders_Directly_To_Its_Sampleable_Texture()
        {
            using var bitmap = new RenderTargetBitmapImpl(
                new PixelSize(8, 6),
                new Vector(96, 96));
            var texture = Assert.IsType<GpuTexture>(bitmap.Texture);
            uint initialGeneration = texture.Generation;

            Assert.True(
                texture.Usage.HasFlag(
                    Silk.NET.WebGPU.TextureUsage.RenderAttachment));
            Assert.True(
                texture.Usage.HasFlag(
                    Silk.NET.WebGPU.TextureUsage.TextureBinding));
            Assert.True(
                texture.Usage.HasFlag(
                    Silk.NET.WebGPU.TextureUsage.CopySrc));
            Assert.False(bitmap.HasAllocatedCpuPixels);
            Assert.False(bitmap.HasCurrentCpuPixels);
            Assert.False(bitmap.HasIntermediateTexture);

            using (var context = Assert.IsType<DrawingContextImpl>(
                       bitmap.CreateDrawingContext()))
            {
                context.Clear(Colors.Red);
            }

            Assert.Same(texture, bitmap.Texture);
            Assert.True(texture.Generation > initialGeneration);
            Assert.Equal(2, bitmap.Version);
            Assert.False(bitmap.HasAllocatedCpuPixels);
            Assert.False(bitmap.HasCurrentCpuPixels);
            Assert.False(bitmap.HasIntermediateTexture);

            byte[] pixels = texture.ReadPixels();
            Assert.Equal(255, pixels[0]);
            Assert.Equal(0, pixels[1]);
            Assert.Equal(0, pixels[2]);
            Assert.Equal(255, pixels[3]);
        }

        [Fact]
        public void Render_Target_Bitmap_Reads_Back_Only_At_Explicit_Cpu_Boundary()
        {
            using var bitmap = new RenderTargetBitmapImpl(
                new PixelSize(4, 3),
                new Vector(96, 96));
            var texture = Assert.IsType<GpuTexture>(bitmap.Texture);

            using (var context = Assert.IsType<DrawingContextImpl>(
                       bitmap.CreateDrawingContext()))
            {
                context.Clear(Colors.Blue);
            }

            Assert.False(bitmap.HasAllocatedCpuPixels);
            Assert.False(bitmap.HasCurrentCpuPixels);

            using var encoded = new MemoryStream();
            bitmap.Save(encoded);

            Assert.True(encoded.Length > 0);
            Assert.True(bitmap.HasAllocatedCpuPixels);
            Assert.True(bitmap.HasCurrentCpuPixels);
            Assert.Same(texture, bitmap.Texture);
            Assert.False(bitmap.HasIntermediateTexture);
        }

        [Fact]
        public void Gpu_Render_Target_Invalidation_Holds_Owner_Then_Device_Locks()
        {
            using var owner = CreateTarget();
            WgpuContext gpu = Assert.IsType<WgpuContext>(
                WgpuContext.Current);
            using var texture = new GpuTexture(
                gpu,
                4,
                3,
                Silk.NET.WebGPU.TextureFormat.Rgba8Unorm,
                Silk.NET.WebGPU.TextureUsage.RenderAttachment |
                Silk.NET.WebGPU.TextureUsage.TextureBinding,
                "Avalonia synchronization test");
            var ownerLock = new object();
            bool observedOwnerLock = false;
            bool observedDeviceLock = false;
            using var context = new DrawingContextImpl(
                new DrawingContextImpl.CreateInfo
                {
                    Dpi = new Vector(96, 96),
                    GpuRenderTarget = texture,
                    GpuRenderSynchronizationLock = ownerLock,
                    GpuRenderStarting = () =>
                    {
                        observedOwnerLock =
                            Monitor.IsEntered(ownerLock);
                        observedDeviceLock =
                            Monitor.IsEntered(gpu.RenderLock);
                    }
                });
            context.Clear(Colors.Red);

            context.Dispose();

            Assert.True(observedOwnerLock);
            Assert.True(observedDeviceLock);
        }

        [Fact]
        public void Surface_Render_Target_Save_Flushes_The_Gpu_Texture()
        {
            using var renderTarget = new SurfaceRenderTarget(
                new SurfaceRenderTarget.CreateInfo
                {
                    Width = 5,
                    Height = 4,
                    Dpi = new Vector(96, 96),
                    Format = PixelFormats.Rgba8888
                });
            var texture = Assert.IsType<GpuTexture>(
                renderTarget.Texture);
            ulong textureId = texture.Id;
            uint initialGeneration = texture.Generation;
            var context = Assert.IsType<DrawingContextImpl>(
                renderTarget.CreateDrawingContext());
            context.Clear(Colors.Lime);

            using var encoded = new MemoryStream();
            renderTarget.Save(encoded);
            encoded.Position = 0;
            using SixLabors.ImageSharp.Image<Rgba32> image =
                SixLabors.ImageSharp.Image.Load<Rgba32>(encoded);

            Assert.Equal(textureId, renderTarget.Texture?.Id);
            Assert.True(texture.Generation > initialGeneration);
            Assert.Empty(context.DrawingContext.Commands);
            Assert.Equal(2, renderTarget.Version);
            Assert.Equal(0, image[0, 0].R);
            Assert.Equal(255, image[0, 0].G);
            Assert.Equal(0, image[0, 0].B);
            Assert.Equal(255, image[0, 0].A);
        }

        [Fact]
        public void Surface_Render_Target_Renews_Its_Preserved_Context_Lease()
        {
            using var renderTarget = new SurfaceRenderTarget(
                new SurfaceRenderTarget.CreateInfo
                {
                    Width = 5,
                    Height = 4,
                    Dpi = new Vector(96, 96),
                    Format = PixelFormats.Rgba8888
                });

            var first = Assert.IsType<DrawingContextImpl>(
                renderTarget.CreateDrawingContext());
            first.Clear(Colors.Red);
            first.Dispose();

            var second = Assert.IsType<DrawingContextImpl>(
                renderTarget.CreateDrawingContext());
            Assert.Same(first, second);
            second.Clear(Colors.Blue);
            second.Dispose();

            using var encoded = new MemoryStream();
            renderTarget.Save(encoded);
            encoded.Position = 0;
            using SixLabors.ImageSharp.Image<Rgba32> image =
                SixLabors.ImageSharp.Image.Load<Rgba32>(encoded);

            Assert.Equal(0, image[0, 0].R);
            Assert.Equal(0, image[0, 0].G);
            Assert.Equal(255, image[0, 0].B);
            Assert.Equal(255, image[0, 0].A);
        }

        [Fact]
        public void Surface_Render_Target_NonAffined_Snapshot_Is_A_Lazy_Context_Portable_Copy()
        {
            using var renderTarget = new SurfaceRenderTarget(
                new SurfaceRenderTarget.CreateInfo
                {
                    Width = 5,
                    Height = 4,
                    Dpi = new Vector(144, 120)
                });
            var context = Assert.IsType<DrawingContextImpl>(
                renderTarget.CreateDrawingContext());
            context.Clear(Color.FromArgb(255, 10, 20, 30));

            using var snapshot = Assert.IsType<ImmutableBitmap>(
                renderTarget.CreateNonAffinedSnapshot());

            Assert.NotSame(renderTarget, snapshot);
            Assert.Equal(renderTarget.PixelSize, snapshot.PixelSize);
            Assert.Equal(renderTarget.Dpi, snapshot.Dpi);
            Assert.Null(snapshot.Texture);
            Assert.True(snapshot.HasRetainedDecodedPixels);
            Assert.Equal(2, renderTarget.Version);

            using var encoded = new MemoryStream();
            snapshot.Save(encoded);
            encoded.Position = 0;
            using SixLabors.ImageSharp.Image<Rgba32> image =
                SixLabors.ImageSharp.Image.Load<Rgba32>(encoded);

            Assert.Null(snapshot.Texture);
            Assert.Equal(10, image[0, 0].R);
            Assert.Equal(20, image[0, 0].G);
            Assert.Equal(30, image[0, 0].B);
            Assert.Equal(255, image[0, 0].A);

            using var destination = CreateTarget();
            destination.DrawBitmap(
                snapshot,
                1,
                new Rect(0, 0, 5, 4),
                new Rect(0, 0, 5, 4));
            RenderCommand command = Assert.Single(
                destination.DrawingContext.Commands);

            Assert.Same(snapshot.Texture, command.Texture);
            Assert.Same(
                snapshot.Texture?.Context,
                WgpuContext.Current);
            Assert.True(snapshot.HasRetainedDecodedPixels);
        }

        [Fact]
        public void Writeable_Bgra_Bitmap_Save_Preserves_Channel_Order()
        {
            using var bitmap = new WriteableBitmapImpl(
                new PixelSize(1, 1),
                new Vector(96, 96),
                PixelFormats.Bgra8888,
                AlphaFormat.Unpremul);
            using (ILockedFramebuffer framebuffer = bitmap.Lock())
            {
                Marshal.WriteByte(framebuffer.Address, 0, 30);
                Marshal.WriteByte(framebuffer.Address, 1, 20);
                Marshal.WriteByte(framebuffer.Address, 2, 10);
                Marshal.WriteByte(framebuffer.Address, 3, 255);
            }

            using var encoded = new MemoryStream();
            bitmap.Save(encoded);
            encoded.Position = 0;
            using SixLabors.ImageSharp.Image<Rgba32> image =
                SixLabors.ImageSharp.Image.Load<Rgba32>(encoded);

            Assert.Equal(10, image[0, 0].R);
            Assert.Equal(20, image[0, 0].G);
            Assert.Equal(30, image[0, 0].B);
            Assert.Equal(255, image[0, 0].A);
        }

        [Fact]
        public void Immutable_Stream_Bitmap_Retains_Encoded_Data_Until_Gpu_Or_Cpu_Pixels_Are_Requested()
        {
            byte[] png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            using var scope = WgpuContext.PushCurrent(null);
            using var source = new MemoryStream(png);
            using var bitmap = new ImmutableBitmap(source);

            Assert.Equal(new PixelSize(1, 1), bitmap.PixelSize);
            Assert.Null(bitmap.Texture);
            Assert.False(bitmap.HasRetainedDecodedPixels);

            using var saved = new MemoryStream();
            bitmap.Save(saved);

            Assert.True(saved.Length > 0);
            Assert.False(bitmap.HasRetainedDecodedPixels);
        }

        [Fact]
        public void DrawLine_With_Zero_Thickness_Pen_Does_Not_Throw()
        {
            var target = CreateTarget();
            target.DrawLine(new Pen(Brushes.Black, 0), new Point(0, 0), new Point(10, 10));
        }

        [Fact]
        public void DrawLine_With_Solid_Pen_Does_Not_Create_A_General_Path_Cache()
        {
            using var target = CreateTarget();

            target.DrawLine(new Pen(Brushes.Black, 3), new Point(0, 0), new Point(10, 10));

            var command = Assert.Single(target.DrawingContext.Commands);
            Assert.Equal(RenderCommandType.DrawLine, command.Type);
            Assert.Null(command.GeometryCache);
        }

        [Fact]
        public void DrawLine_Preserves_Pen_Stroke_Style()
        {
            using var target = CreateTarget();
            var pen = new Pen(
                Brushes.Black,
                3,
                new DashStyle(new[] { 2.0, 4.0 }, 1.5),
                PenLineCap.Round,
                PenLineJoin.Bevel,
                7);

            target.DrawLine(pen, new Point(0, 0), new Point(10, 10));

            var command = Assert.Single(target.DrawingContext.Commands);
            var nativePen = Assert.IsType<ProGPU.Vector.Pen>(command.Pen);
            Assert.Equal(3, nativePen.Thickness);
            Assert.Equal(ProGPU.Vector.PenLineCap.Round, nativePen.StartLineCap);
            Assert.Equal(ProGPU.Vector.PenLineCap.Round, nativePen.EndLineCap);
            Assert.Equal(ProGPU.Vector.PenLineCap.Round, nativePen.DashCap);
            Assert.Equal(ProGPU.Vector.PenLineJoin.Bevel, nativePen.LineJoin);
            Assert.Equal(7, nativePen.MiterLimit);
            Assert.Equal(new[] { 2.0, 4.0 }, nativePen.DashArray);
            Assert.Equal(1.5, nativePen.DashOffset);
            Assert.NotNull(command.GeometryCache);
        }

        [Fact]
        public void DrawGeometry_Reuses_Cache_Until_Stream_Geometry_Changes()
        {
            var geometry = new StreamGeometryImpl();
            using (var geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(new Point(0, 0));
                geometryContext.LineTo(new Point(10, 0));
                geometryContext.LineTo(new Point(10, 10));
                geometryContext.EndFigure(isClosed: false);
            }

            using var firstTarget = CreateTarget();
            firstTarget.DrawGeometry(null, new Pen(Brushes.Black, 2), geometry);
            var firstCache = Assert.Single(firstTarget.DrawingContext.Commands).GeometryCache;

            using var secondTarget = CreateTarget();
            secondTarget.DrawGeometry(null, new Pen(Brushes.Black, 2), geometry);
            var secondCache = Assert.Single(secondTarget.DrawingContext.Commands).GeometryCache;

            Assert.NotNull(firstCache);
            Assert.Same(firstCache, secondCache);

            using (var geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(new Point(0, 0));
                geometryContext.LineTo(new Point(20, 0));
                geometryContext.EndFigure(isClosed: false);
            }

            using var changedTarget = CreateTarget();
            changedTarget.DrawGeometry(null, new Pen(Brushes.Black, 2), geometry);
            var changedCache = Assert.Single(changedTarget.DrawingContext.Commands).GeometryCache;

            Assert.NotNull(changedCache);
            Assert.NotSame(firstCache, changedCache);
        }

        [Fact]
        public void DrawRectangle_With_Zero_Thickness_Pen_Does_Not_Throw()
        {
            var target = CreateTarget();
            target.DrawRectangle(Brushes.Black, new Pen(Brushes.Black, 0), new RoundedRect(new Rect(0, 0, 100, 100), new CornerRadius(4)));
        }

        [Fact]
        public void Blur_Effect_Records_A_Bounded_Native_Visual()
        {
            using var target = CreateTarget();

            target.PushEffect(
                new Rect(5, 15, 40, 50),
                new Avalonia.Media.BlurEffect { Radius = 4 });
            target.DrawRectangle(
                Brushes.Red,
                null,
                new RoundedRect(new Rect(10, 20, 30, 40)));
            target.PopEffect();

            var command = Assert.Single(target.DrawingContext.Commands);
            Assert.Equal(RenderCommandType.DrawVisual, command.Type);
            Assert.NotNull(command.Visual);
            var effect = Assert.IsType<ProGPU.Scene.BlurEffect>(
                command.Visual!.Effect);
            Assert.Equal(
                DrawingContextImpl.EffectRadiusToSigma(4),
                effect.BlurRadius);
            Assert.Equal(
                new ProGPU.Scene.Rect(10, 20, 30, 40),
                command.Visual.EffectContentBounds);
            Assert.Equal(5f, command.Visual.EffectRasterPadding);
            Assert.Equal(1, target.DrawingContext.RetainedResourceCount);
        }

        [Fact]
        public void Drop_Shadow_Effect_Preserves_Offset_Color_And_Opacity()
        {
            using var target = CreateTarget();

            target.PushEffect(
                new Rect(10, 18, 40, 50),
                new Avalonia.Media.DropShadowEffect
                {
                    OffsetX = 5,
                    OffsetY = 3,
                    BlurRadius = 4,
                    Color = Color.FromArgb(128, 20, 40, 60),
                    Opacity = 0.5
                });
            target.DrawRectangle(
                Brushes.Red,
                null,
                new RoundedRect(new Rect(10, 20, 30, 40)));
            target.PopEffect();

            var command = Assert.Single(target.DrawingContext.Commands);
            var visual = Assert.IsAssignableFrom<ProGPU.Scene.Visual>(
                command.Visual);
            var effect = Assert.IsType<ProGPU.Scene.DropShadowEffect>(
                visual.Effect);
            Assert.Equal(new System.Numerics.Vector2(5, 3), effect.Offset);
            Assert.Equal(20f / 255f, effect.Color.X, 6);
            Assert.Equal(40f / 255f, effect.Color.Y, 6);
            Assert.Equal(60f / 255f, effect.Color.Z, 6);
            Assert.Equal(128f / 255f * 0.5f, effect.Color.W, 6);
            Assert.Equal(
                new ProGPU.Scene.Rect(10, 20, 30, 40),
                visual.EffectContentBounds);
            Assert.Equal(5f, visual.EffectRasterPadding);
        }

        [Fact]
        public void Reset_Discards_An_Unbalanced_Effect_Scope()
        {
            using var target = CreateTarget();
            ProGPU.Scene.DrawingContext owner = target.DrawingContext;

            target.PushEffect(
                new Rect(5, 15, 40, 50),
                new Avalonia.Media.BlurEffect { Radius = 4 });
            target.DrawRectangle(
                Brushes.Red,
                null,
                new RoundedRect(new Rect(10, 20, 30, 40)));

            Assert.NotSame(owner, target.DrawingContext);

            target.Reset();

            Assert.Same(owner, target.DrawingContext);
            Assert.Empty(owner.Commands);
            Assert.Equal(0, owner.RetainedResourceCount);
        }

        [Fact]
        public void Conic_Gradient_Uses_A_Native_Rotated_Sweep_Brush()
        {
            using var target = CreateTarget();
            var brush = new ConicGradientBrush
            {
                Angle = 23,
                Center = RelativePoint.Center,
                SpreadMethod = GradientSpreadMethod.Repeat,
                GradientStops = new GradientStops
                {
                    new(Colors.Red, 0),
                    new(Colors.Blue, 1)
                }
            };

            target.DrawRectangle(
                brush,
                null,
                new RoundedRect(new Rect(10, 20, 100, 60)));

            var command = Assert.Single(target.DrawingContext.Commands);
            var sweep = Assert.IsType<ProGPU.Vector.SweepGradientBrush>(
                command.Brush);
            Assert.Equal(new System.Numerics.Vector2(60, 50), sweep.Center);
            Assert.Equal(0f, sweep.StartAngle);
            Assert.Equal(360f, sweep.EndAngle);
            Assert.Equal(
                ProGPU.Vector.GradientSpreadMethod.Repeat,
                sweep.SpreadMethod);
            Assert.Equal(2, sweep.Stops.Length);

            float radians = (23f - 90f) * MathF.PI / 180f;
            var startDirection =
                sweep.Center +
                new System.Numerics.Vector2(
                    MathF.Cos(radians),
                    MathF.Sin(radians));
            var rotated = System.Numerics.Vector2.Transform(
                startDirection,
                sweep.CoordinateTransform);
            Assert.Equal(sweep.Center.X + 1f, rotated.X, 5);
            Assert.Equal(sweep.Center.Y, rotated.Y, 5);
            Assert.True(
                DrawingContextImpl.SupportsRetainedCompositionOpacityMask(
                    brush));
        }

#if AVALONIA_MONOREPO_TESTS
        [Fact]
        public void Solid_Glyph_Run_Uses_Retained_ProGpu_Text_Command_Across_Redraws()
        {
            using var app = UnitTestApplication.Start(
                TestServices.MockPlatformRenderInterface.With(
                    renderInterface: new PlatformRenderInterface(),
                    fontManagerImpl: new CustomFontManagerImpl()));
            var shaped = TextShaper.Current.ShapeText(
                "ControlCatalog",
                new TextShaperOptions(
                    Typeface.Default.GlyphTypeface,
                    16,
                    0,
                    CultureInfo.InvariantCulture));
            using var glyphRun = new GlyphRun(
                shaped.GlyphTypeface,
                shaped.FontRenderingEmSize,
                shaped.Text,
                shaped,
                baselineOrigin: new Point(7, 19),
                biDiLevel: shaped.BidiLevel);
            using var firstTarget = CreateTarget();
            firstTarget.PushTextOptions(new TextOptions
            {
                TextRenderingMode = Avalonia.Media.TextRenderingMode.Alias,
                TextHintingMode = Avalonia.Media.TextHintingMode.None
            });

            firstTarget.DrawGlyphRun(Brushes.Black, glyphRun.PlatformImpl.Item);

            var firstCommand = Assert.Single(firstTarget.DrawingContext.Commands);
            Assert.Equal(RenderCommandType.DrawGlyphRun, firstCommand.Type);
            Assert.Equal(shaped.Length, firstCommand.GlyphIndices?.Length);
            Assert.Equal(shaped.Length, firstCommand.GlyphPositions?.Length);
            Assert.Equal(new System.Numerics.Vector2(7, 19), firstCommand.Position);
            Assert.Equal(ProGPU.Scene.TextRenderingMode.Aliased, firstCommand.TextRenderingMode);
            Assert.Equal(ProGPU.Scene.TextHintingMode.Animated, firstCommand.TextHintingMode);

            using var secondTarget = CreateTarget();
            secondTarget.DrawGlyphRun(Brushes.Black, glyphRun.PlatformImpl.Item);

            var secondCommand = Assert.Single(secondTarget.DrawingContext.Commands);
            Assert.Equal(RenderCommandType.DrawGlyphRun, secondCommand.Type);
            Assert.Same(firstCommand.GlyphIndices, secondCommand.GlyphIndices);
            Assert.Same(firstCommand.GlyphPositions, secondCommand.GlyphPositions);
            Assert.DoesNotContain(
                secondTarget.DrawingContext.Commands,
                command => command.Type == RenderCommandType.DrawPath);
        }
#endif

        [Fact]
        public void Digger_Acrylic_Records_Shader_Material_With_Source_Replacement()
        {
            var target = CreateTarget();
            var material = new ExperimentalAcrylicMaterial
            {
                BackgroundSource = AcrylicBackgroundSource.Digger,
                TintColor = Colors.Red,
                TintOpacity = 0.9,
                MaterialOpacity = 0.8,
                FallbackColor = Colors.Blue
            };

            target.DrawRectangle(
                material,
                new RoundedRect(new Rect(10, 20, 100, 80), new CornerRadius(2, 4, 6, 8)));

            Assert.Collection(target.DrawingContext.Commands,
                command =>
                {
                    Assert.Equal(RenderCommandType.PushBlendMode, command.Type);
                    Assert.Equal((int)GpuBlendMode.Src, command.IntParam);
                },
                command =>
                {
                    Assert.Equal(RenderCommandType.DrawExtension, command.Type);
                    Assert.Equal(CompositorBuiltInExtensions.BackdropMaterial, command.ExtensionId);
                    var parameters = Assert.IsType<BackdropMaterialParams>(command.DataParam);
                    Assert.Equal(new ProGPU.Scene.Rect(10, 20, 100, 80), parameters.Rect);
                    Assert.Equal(ProGPU.Vector.BackdropMaterialKind.Acrylic, parameters.Kind);
                    Assert.Equal(ProGPU.Vector.BackdropMaterialSource.HostBackdrop, parameters.Source);
                    Assert.Equal(1f, parameters.TintColor.X);
                    Assert.Equal(0f, parameters.TintColor.Y);
                    Assert.Equal(0f, parameters.TintColor.Z);
                    Assert.Equal(new System.Numerics.Vector4(2, 4, 6, 8), parameters.CornerRadiiX);
                    Assert.Equal(new System.Numerics.Vector4(2, 4, 6, 8), parameters.CornerRadiiY);
                    Assert.Equal(0.0225f, parameters.NoiseOpacity);
                },
                command => Assert.Equal(RenderCommandType.PopBlendMode, command.Type));
        }

        [Fact]
        public void Non_Digger_Acrylic_Uses_Normal_Composition()
        {
            var target = CreateTarget();
            var material = new ExperimentalAcrylicMaterial
            {
                BackgroundSource = AcrylicBackgroundSource.None,
                TintColor = Colors.Green
            };

            target.DrawRectangle(material, new RoundedRect(new Rect(0, 0, 20, 10)));

            var command = Assert.Single(target.DrawingContext.Commands);
            var parameters = Assert.IsType<BackdropMaterialParams>(command.DataParam);
            Assert.Equal(RenderCommandType.DrawExtension, command.Type);
            Assert.Equal(ProGPU.Vector.BackdropMaterialSource.None, parameters.Source);
        }

        [Fact]
        public void ScaleDrawingToDpi_Applies_Dpi_PostTransform_To_DrawCommands()
        {
            var target = CreateTarget(new Vector(192, 144), scaleDrawingToDpi: true);

            target.DrawLine(new Pen(Brushes.Black, 1), new Point(1, 2), new Point(3, 4));

            var command = Assert.Single(target.DrawingContext.Commands);
            Assert.Equal(RenderCommandType.DrawLine, command.Type);
            Assert.Equal(2f, command.Position.X);
            Assert.Equal(3f, command.Position.Y);
            Assert.Equal(6f, command.Position2.X);
            Assert.Equal(6f, command.Position2.Y);
        }

        [Fact]
        public void Multi_Rect_Region_Uses_Geometry_Clip_With_Matching_Nested_Pops()
        {
            var target = CreateTarget();
            var region = new SkiaRegionImpl();
            region.AddRect(CreatePixelRect(10, 20, 30, 40));
            region.AddRect(CreatePixelRect(50, 60, 80, 90));

            target.PushClip(region);
            target.PushClip(new Rect(1, 2, 3, 4));
            target.PopClip();
            target.PopClip();

            Assert.Collection(target.DrawingContext.Commands,
                command =>
                {
                    Assert.Equal(RenderCommandType.PushGeometryClip, command.Type);
                    Assert.Equal(2, command.Path?.Figures.Count);
                },
                command => Assert.Equal(RenderCommandType.PushClip, command.Type),
                command => Assert.Equal(RenderCommandType.PopClip, command.Type),
                command => Assert.Equal(RenderCommandType.PopGeometryClip, command.Type));
        }

        [Fact]
        public void Single_Rect_Region_Uses_Rectangle_Clip()
        {
            var target = CreateTarget();
            var region = new SkiaRegionImpl();
            region.AddRect(CreatePixelRect(10, 20, 30, 40));

            target.PushClip(region);
            target.PopClip();

            Assert.Collection(target.DrawingContext.Commands,
                command =>
                {
                    Assert.Equal(RenderCommandType.PushClip, command.Type);
                    Assert.Equal(new ProGPU.Scene.Rect(10, 20, 20, 20), command.Rect);
                },
                command => Assert.Equal(RenderCommandType.PopClip, command.Type));
        }

        [Fact]
        public void DrawRectangle_Records_Local_Rect_And_Full_Transform()
        {
            var target = CreateTarget();
            var transform = Matrix.CreateRotation(Math.PI / 6) * Matrix.CreateTranslation(20, 30);
            target.Transform = transform;

            target.DrawRectangle(Brushes.Red, null, new RoundedRect(new Rect(1, 2, 30, 40)));

            var command = Assert.Single(target.DrawingContext.Commands);
            Assert.Equal(RenderCommandType.DrawRect, command.Type);
            Assert.Equal(new ProGPU.Scene.Rect(1, 2, 30, 40), command.Rect);
            Assert.Equal((float)transform.M11, command.Transform.M11);
            Assert.Equal((float)transform.M12, command.Transform.M12);
            Assert.Equal((float)transform.M21, command.Transform.M21);
            Assert.Equal((float)transform.M22, command.Transform.M22);
            Assert.Equal((float)transform.M31, command.Transform.M41);
            Assert.Equal((float)transform.M32, command.Transform.M42);
        }

        [Fact]
        public void Solid_Primitive_Draws_Do_Not_Allocate_Temporary_Path_Graphs()
        {
            using var target = CreateTarget();
            const int drawCount = 256;
            target.DrawingContext.EnsureCommandCapacity(drawCount * 2);
            target.DrawRectangle(
                Brushes.Red,
                null,
                new RoundedRect(new Rect(0, 0, 30, 40), new CornerRadius(4)));
            target.DrawingContext.Clear();

            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < drawCount; index++)
            {
                target.DrawRectangle(
                    Brushes.Red,
                    null,
                    new RoundedRect(new Rect(index, 2, 30, 40), new CornerRadius(4)));
                target.DrawEllipse(Brushes.Blue, null, new Rect(index, 4, 20, 12));
            }
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            Assert.True(
                allocatedBytes < 48 * 1024,
                $"Primitive recording allocated {allocatedBytes:N0} bytes.");
            Assert.All(
                target.DrawingContext.Commands,
                command => Assert.NotEqual(RenderCommandType.DrawPath, command.Type));
        }

        [Fact]
        public void Repeated_Solid_Styles_Reuse_Converted_Brushes_And_Pens()
        {
            using var target = CreateTarget();
            var brush = new SolidColorBrush(Colors.CornflowerBlue) { Opacity = 0.75 };
            var pen = new Pen(brush, 2, lineCap: PenLineCap.Round);

            target.DrawRectangle(brush, pen, new RoundedRect(new Rect(0, 0, 20, 10)));
            target.DrawRectangle(brush, pen, new RoundedRect(new Rect(30, 0, 20, 10)));

            Assert.Equal(2, target.DrawingContext.Commands.Count);
            Assert.Same(target.DrawingContext.Commands[0].Brush, target.DrawingContext.Commands[1].Brush);
            Assert.Same(target.DrawingContext.Commands[0].Pen, target.DrawingContext.Commands[1].Pen);
        }

        [Fact]
        public void Mutated_Solid_Style_Uses_A_New_Converted_Value()
        {
            using var target = CreateTarget();
            var brush = new SolidColorBrush(Colors.Red);

            target.DrawRectangle(brush, null, new RoundedRect(new Rect(0, 0, 20, 10)));
            brush.Color = Colors.Blue;
            target.DrawRectangle(brush, null, new RoundedRect(new Rect(30, 0, 20, 10)));

            Assert.Equal(2, target.DrawingContext.Commands.Count);
            Assert.NotSame(target.DrawingContext.Commands[0].Brush, target.DrawingContext.Commands[1].Brush);
            Assert.Equal(
                new System.Numerics.Vector4(1, 0, 0, 1),
                Assert.IsType<ProGPU.Vector.SolidColorBrush>(target.DrawingContext.Commands[0].Brush).Color);
            Assert.Equal(
                new System.Numerics.Vector4(0, 0, 1, 1),
                Assert.IsType<ProGPU.Vector.SolidColorBrush>(target.DrawingContext.Commands[1].Brush).Color);
        }

        [Fact]
        public void Rotated_Rectangle_Clip_Uses_All_Four_Corners()
        {
            var target = CreateTarget();
            target.Transform = Matrix.CreateRotation(Math.PI / 2) * Matrix.CreateTranslation(20, 4);

            target.PushClip(new Rect(0, 0, 12, 4));

            var command = Assert.Single(target.DrawingContext.Commands);
            Assert.Equal(RenderCommandType.PushClip, command.Type);
            Assert.Equal(16, command.Rect.X, 3);
            Assert.Equal(4, command.Rect.Y, 3);
            Assert.Equal(4, command.Rect.Width, 3);
            Assert.Equal(12, command.Rect.Height, 3);
        }

#if AVALONIA_MONOREPO_TESTS
        [Fact]
        public void ImageBrush_Records_Texture_Command_With_Premultiplied_Alpha()
        {
            var target = CreateTarget();
            var data = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.Copy(new byte[]
                {
                    0, 0, 255, 255,
                    0, 255, 0, 255,
                    255, 0, 0, 255,
                    0, 0, 0, 0
                }, 0, data, 16);

                var impl = new ImmutableBitmap(
                    new PixelSize(2, 2),
                    new Vector(96, 96),
                    8,
                    PixelFormats.Rgba8888,
                    AlphaFormat.Premul,
                    data);
                using var bitmapRef = RefCountable.Create<IBitmapImpl>(impl);
                using var bitmap = new Bitmap(bitmapRef);

                target.DrawRectangle(
                    new ImageBrush(bitmap),
                    null,
                    new RoundedRect(new Rect(10, 20, 40, 30)));

                var command = Assert.Single(
                    target.DrawingContext.Commands.Where(x => x.Type == RenderCommandType.DrawTexture));
                Assert.Equal(new ProGPU.Scene.Rect(15, 20, 30, 30), command.Rect);
                Assert.Equal(new ProGPU.Scene.Rect(0, 0, 2, 2), command.SrcRect);
                Assert.Equal(GpuTextureAlphaMode.Premultiplied, command.Texture?.AlphaMode);
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }
#endif

#if AVALONIA_MONOREPO_TESTS
        [Fact]
        public void DrawingBrush_OpacityMask_Survives_Recording_Context_Dispose()
        {
            using var app = UnitTestApplication.Start(
                TestServices.MockPlatformRenderInterface.With(renderInterface: new PlatformRenderInterface()));
            var renderTarget = new SurfaceRenderTarget(new SurfaceRenderTarget.CreateInfo
            {
                Width = 100,
                Height = 20,
                Dpi = new Vector(96, 96)
            });
            var target = Assert.IsType<DrawingContextImpl>(renderTarget.CreateDrawingContext());
            var mask = new DrawingBrush
            {
                Drawing = new GeometryDrawing
                {
                    Brush = Brushes.Black,
                    Geometry = new GeometryGroup
                    {
                        Children =
                        {
                            new RectangleGeometry(new Rect(0, 0, 30, 20)),
                            new RectangleGeometry(new Rect(70, 0, 30, 20))
                        }
                    }
                }
            };

            target.PushOpacityMask(mask, new Rect(0, 0, 100, 20));
            target.PopOpacityMask();

            Assert.Collection(target.DrawingContext.Commands,
                command =>
                {
                    Assert.Equal(RenderCommandType.PushOpacityMask, command.Type);
                    Assert.NotNull(command.Picture);
                    Assert.Contains(command.Picture.Commands, nested => nested.Type == RenderCommandType.DrawPath);
                },
                command => Assert.Equal(RenderCommandType.PopOpacityMask, command.Type));
            Assert.Equal(1, target.DrawingContext.RetainedResourceCount);

            target.Dispose();

            Assert.Equal(2, target.DrawingContext.Commands.Count);
            Assert.Equal(1, target.DrawingContext.RetainedResourceCount);

            renderTarget.Dispose();

            Assert.Empty(target.DrawingContext.Commands);
            Assert.Equal(0, target.DrawingContext.RetainedResourceCount);
        }
#endif

        [Fact]
        public void ProGpu_Api_Lease_Exposes_Current_Drawing_State()
        {
            using var target = CreateTarget(new Vector(144, 120), scaleDrawingToDpi: true);
            var transform = Matrix.CreateScale(2, 3) * Matrix.CreateTranslation(10, 20);
            target.Transform = transform;
            target.PushOpacity(0.5, null);
            var feature = Assert.IsAssignableFrom<IProGpuApiLeaseFeature>(
                target.GetFeature(typeof(IProGpuApiLeaseFeature)));

            using (var lease = feature.Lease())
            {
                Assert.Same(target.DrawingContext, lease.DrawingContext);
                Assert.Same(WgpuContext.Current, lease.WgpuContext);
                Assert.Equal(new Vector(144, 120), lease.Dpi);
                Assert.Equal(0.5, lease.CurrentOpacity);
                Assert.Equal(3f, lease.CurrentTransform.M11);
                Assert.Equal(3.75f, lease.CurrentTransform.M22);
                Assert.Equal(15f, lease.CurrentTransform.M41);
                Assert.Equal(25f, lease.CurrentTransform.M42);

                lease.DrawingContext.DrawRectangle(
                    new ProGPU.Vector.SolidColorBrush(new System.Numerics.Vector4(0.1f, 0.4f, 0.9f, 1f)),
                    null,
                    new ProGPU.Scene.Rect(2, 4, 20, 10),
                    lease.CurrentTransform);
            }

            target.PopOpacity();
            Assert.Contains(target.DrawingContext.Commands, command => command.Type == RenderCommandType.DrawRect);
        }

        [Fact]
        public void ProGpu_Api_Lease_Is_Exclusive()
        {
            using var target = CreateTarget();
            var feature = Assert.IsAssignableFrom<IProGpuApiLeaseFeature>(
                target.GetFeature(typeof(IProGpuApiLeaseFeature)));

            using var lease = feature.Lease();

            Assert.Throws<InvalidOperationException>(() => feature.Lease());
            Assert.Throws<InvalidOperationException>(() => target.Clear(Colors.Transparent));
            Assert.Throws<InvalidOperationException>(() => target.Dispose());
        }

        [Fact]
        public void Disposing_ProGpu_Api_Lease_Releases_Context()
        {
            using var target = CreateTarget();
            var feature = Assert.IsAssignableFrom<IProGpuApiLeaseFeature>(
                target.GetFeature(typeof(IProGpuApiLeaseFeature)));
            var lease = feature.Lease();

            lease.Dispose();
            lease.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _ = lease.DrawingContext);
            target.DrawLine(new Pen(Brushes.Black, 1), new Point(0, 0), new Point(5, 5));
        }

        [Fact]
        public void ProGpu_Api_Lease_Must_Be_Disposed_On_Acquiring_Thread()
        {
            using var target = CreateTarget();
            var feature = Assert.IsAssignableFrom<IProGpuApiLeaseFeature>(
                target.GetFeature(typeof(IProGpuApiLeaseFeature)));
            var lease = feature.Lease();
            Exception? disposeError = null;
            var thread = new Thread(() => disposeError = Record.Exception(lease.Dispose));

            thread.Start();
            thread.Join();

            Assert.IsType<InvalidOperationException>(disposeError);
            lease.Dispose();
            target.Clear(Colors.Transparent);
        }

        [Fact]
        public void Offscreen_Cache_Reuses_Cleared_Recording_Context_And_Its_Capacity()
        {
            using var cache = new OffscreenTextureCache();
            var first = cache.RentRecordingContext();
            var retainedResource = new TrackingDisposable();
            first.EnsureCommandCapacity(512);
            first.Commands.Add(default);
            first.RetainResource(retainedResource);

            cache.ReturnRecordingContext(first);

            Assert.True(retainedResource.IsDisposed);
            Assert.Empty(first.Commands);
            Assert.True(first.Commands.Capacity >= 512);

            var second = cache.RentRecordingContext();

            Assert.Same(first, second);
            Assert.Empty(second.Commands);
            Assert.True(second.Commands.Capacity >= 512);
            cache.ReturnRecordingContext(second);
        }

        private static DrawingContextImpl CreateTarget()
        {
            return CreateTarget(new Vector(96, 96), scaleDrawingToDpi: false);
        }

        private static DrawingContextImpl CreateTarget(Vector dpi, bool scaleDrawingToDpi)
        {
            var createInfo = new DrawingContextImpl.CreateInfo
            {
                Dpi = dpi,
                ScaleDrawingToDpi = scaleDrawingToDpi
            };
            return new DrawingContextImpl(createInfo);
        }

        private static LtrbPixelRect CreatePixelRect(int left, int top, int right, int bottom) =>
            new()
            {
                Left = left,
                Top = top,
                Right = right,
                Bottom = bottom
            };

        private sealed class TestFramebufferPlatformSurface : IFramebufferPlatformSurface
        {
            private readonly bool _createFramebuffer;

            public TestFramebufferPlatformSurface(bool createFramebuffer = false)
            {
                _createFramebuffer = createFramebuffer;
            }

            public IFramebufferRenderTarget CreateFramebufferRenderTarget()
            {
                if (_createFramebuffer)
                {
                    return new FuncFramebufferRenderTarget(
                        (IRenderTarget.RenderTargetSceneInfo _, out FramebufferLockProperties properties) =>
                        {
                            properties = new FramebufferLockProperties(PreviousFrameIsRetained: true);
                            return new LockedFramebuffer(
                                IntPtr.Zero,
                                new PixelSize(4, 3),
                                16,
                                new Vector(96, 96),
                                PixelFormats.Bgra8888,
                                AlphaFormat.Premul,
                                null);
                        },
                        retainsFrameContents: true);
                }

                return new FuncFramebufferRenderTarget(
                    () => throw new InvalidOperationException("The retention capability test does not lock the surface."));
            }
        }

        private sealed class TrackingDisposable : IDisposable
        {
            public bool IsDisposed { get; private set; }

            public void Dispose()
            {
                IsDisposed = true;
            }
        }
    }
}
