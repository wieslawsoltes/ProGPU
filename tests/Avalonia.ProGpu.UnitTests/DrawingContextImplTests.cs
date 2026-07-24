using System;
using System.Globalization;
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
using Xunit;

namespace Avalonia.ProGpu.UnitTests
{
    public class DrawingContextImplTests
    {
        [Fact]
        public void Framebuffer_Render_Target_Supports_Direct_Rendering()
        {
            using var target = new FramebufferRenderTarget(new TestFramebufferPlatformSurface());

            Assert.True(target.Properties.RetainsPreviousFrameContents);
            Assert.True(target.Properties.IsSuitableForDirectRendering);
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
        public void DrawLine_With_Zero_Thickness_Pen_Does_Not_Throw()
        {
            var target = CreateTarget();
            target.DrawLine(new Pen(Brushes.Black, 0), new Point(0, 0), new Point(10, 10));
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
        }

        [Fact]
        public void DrawRectangle_With_Zero_Thickness_Pen_Does_Not_Throw()
        {
            var target = CreateTarget();
            target.DrawRectangle(Brushes.Black, new Pen(Brushes.Black, 0), new RoundedRect(new Rect(0, 0, 100, 100), new CornerRadius(4)));
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
    }
}
