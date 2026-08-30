using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Reflection;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public class NativePictureCompilerTests
{
    [Fact]
    public void CompilerTransfersRetainedHitTestIndexInSemanticSceneUpdate()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 64f, 48f));
        drawing.DrawRectangle(
            new SolidColorBrush(Vector4.One),
            null,
            new Rect(4f, 6f, 20f, 12f));
        using GpuPicture picture = recorder.EndRecording();
        GpuHitTestPrimitive[] primitives =
        [
            GpuHitTestPrimitive.RectangleFill(
                42,
                new Vector2(4f, 6f),
                new Vector2(24f, 18f),
                Vector2.Zero)
        ];
        GpuHitTestIndex hitTestIndex = GpuHitTestIndex.Build(primitives);

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            330U,
            2U,
            NativePictureCompileOptions.Default,
            hitTestIndex,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        ref readonly NativeMethods.SceneResource resource = ref resources[^1];
        Assert.Equal(NativeSceneResourceKind.HitTestIndex, resource.Kind);
        NativeSceneHitTestIndex page =
            MemoryMarshal.Read<NativeSceneHitTestIndex>(
                compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset)));
        Assert.Equal(1U, page.PrimitiveCount);
        Assert.Equal(1U, page.NodeCount);
        Assert.Equal(1U, page.PrimitiveIndexCount);
        NativeGpuHitTestPrimitive nativePrimitive =
            MemoryMarshal.Read<NativeGpuHitTestPrimitive>(
                compiled.Stream.Slice(
                    checked((int)resource.AuxiliaryOffset +
                        (int)page.PrimitiveOffset)));
        Assert.Equal(42, nativePrimitive.Id);
        Assert.Equal(
            (uint)NativeGpuHitTestPrimitiveKind.RectangleFill,
            nativePrimitive.Kind);
    }

    [Fact]
    public void EveryRenderCommandHasDocumentedNativeCapability()
    {
        RenderCommandType[] commandTypes =
            Enum.GetValues<RenderCommandType>();
        Assert.All(commandTypes, static commandType =>
            Assert.NotEqual(
                NativePictureCommandCapability.Unknown,
                GpuPictureNativeSceneCompiler.GetCommandCapability(
                    commandType)));

        RenderCommandType[] unsupported = commandTypes
            .Where(static commandType =>
                GpuPictureNativeSceneCompiler.GetCommandCapability(
                    commandType) ==
                NativePictureCommandCapability.ExplicitlyUnsupported)
            .ToArray();
        Assert.Equal(
            [
                RenderCommandType.DrawVisual
            ],
            unsupported);
    }

    [Fact]
    public void CompilerLowersRetainedLineAndAcisEdgesToNative3DResources()
    {
        var pen = new Pen(
            new SolidColorBrush(new Vector4(0.25f, 0.5f, 0.75f, 1f))
            {
                Opacity = 0.8f
            },
            3f);
        Matrix4x4 model = Matrix4x4.CreateRotationX(0.35f) *
            Matrix4x4.CreateTranslation(2f, 3f, 4f);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawExtension,
                    ExtensionId = CompositorBuiltInExtensions.Line3D,
                    DataParam = pen,
                    FloatBufferOffset = 0,
                    FloatBufferCount = 6
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawExtension,
                    ExtensionId = CompositorBuiltInExtensions.AcisSolid,
                    Pen = pen,
                    Line3DBufferOffset = 0,
                    Line3DBufferCount = 1,
                    Transform = model
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            [new Line3D(new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f))],
            [0f, 1f, 2f, 3f, 4f, 5f]);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            0.75f,
            1.5f,
            0.1f,
            100f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(
            new Vector3(0f, 0f, 8f),
            Vector3.Zero,
            Vector3.UnitY);

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            301U,
            9U,
            new NativePictureCompileOptions(
                2f,
                projection,
                view,
                new Vector3(0f, 0f, 8f)),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(2, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.Line3DCount);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        Assert.Equal(NativeSceneResourceKind.Line3DBatch, resources[0].Kind);
        Assert.Equal(NativeSceneResourceKind.Line3DBatch, resources[1].Kind);
        NativeSceneLine3D acis = MemoryMarshal.Read<NativeSceneLine3D>(
            compiled.Stream.Slice(checked((int)resources[1].PayloadOffset)));
        Assert.Equal(model.M23, acis.Transform.M23);
        Assert.Equal(model.M43, acis.Transform.M43);
        Assert.Equal(0.8f, acis.Opacity);

        NativeMethods.SceneCommand firstCommand =
            MemoryMarshal.Read<NativeMethods.SceneCommand>(
                compiled.Stream.Slice(checked((int)header.CommandOffset)));
        Assert.Equal(
            NativeSceneCommandKind.DrawLine3DBatch,
            firstCommand.Kind);
        NativeSceneCamera3D camera = MemoryMarshal.Read<NativeSceneCamera3D>(
            compiled.Stream.Slice(checked((int)firstCommand.PayloadOffset)));
        Assert.Equal(projection.M11, camera.Projection.M11);
        Assert.Equal(view.M43, camera.View.M43);
        Assert.Equal(8f, camera.CameraPosition.Z);
    }

    [Fact]
    public void CompilerCarriesNestedGpuCameraIntoNative3DDraw()
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 2f);
        using var nested = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawExtension,
                    ExtensionId = CompositorBuiltInExtensions.Line3D,
                    DataParam = pen,
                    FloatBufferCount = 6
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            [0f, 0f, 0f, 1f, 1f, 1f]);
        Matrix4x4 cameraView = Matrix4x4.CreateLookAt(
            new Vector3(0f, 0f, 6f),
            Vector3.Zero,
            Vector3.UnitY);
        using var outer = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPicture,
                    Picture = nested,
                    UseGpuTransforms = true,
                    CameraView = cameraView
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            outer,
            302U,
            3U,
            new NativePictureCompileOptions(
                1f,
                Matrix4x4.Identity,
                Matrix4x4.Identity,
                new Vector3(0f, 0f, 6f)),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneCommand command =
            MemoryMarshal.Read<NativeMethods.SceneCommand>(
                compiled.Stream.Slice(checked((int)header.CommandOffset)));
        NativeSceneCamera3D camera = MemoryMarshal.Read<NativeSceneCamera3D>(
            compiled.Stream.Slice(checked((int)command.PayloadOffset)));
        Assert.Equal(cameraView.M33, camera.View.M33);
        Assert.Equal(cameraView.M43, camera.View.M43);
    }

    [Fact]
    public void CompilerFoldsNestedAffineGpuCameraIntoNative2DTransform()
    {
        Matrix4x4 local = Matrix4x4.CreateTranslation(2f, 3f, 0f);
        Matrix4x4 camera = Matrix4x4.CreateScale(2f, -2f, 1f) *
            Matrix4x4.CreateTranslation(11f, 13f, 0f);
        Matrix4x4 parent = Matrix4x4.CreateTranslation(101f, 103f, 0f);
        using var leaf = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(1f, 2f, 8f, 6f),
                    Brush = new SolidColorBrush(Vector4.One),
                    Transform = local,
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());
        using var cameraPicture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPicture,
                    Picture = leaf,
                    UseGpuTransforms = true,
                    CameraView = camera,
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());
        using var root = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPicture,
                    Picture = cameraPicture,
                    Transform = parent,
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            root,
            303U,
            4U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.AnalyticPrimitiveCount);
        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneResource resource =
            MemoryMarshal.Read<NativeMethods.SceneResource>(
                compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        NativeAnalyticPrimitive primitive =
            MemoryMarshal.Read<NativeAnalyticPrimitive>(
                compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(
            ToAffine(local) * ToAffine(camera) * ToAffine(parent),
            primitive.Transform);
    }

    [Fact]
    public void CompilerRejectsNestedPerspectiveGpuCameraForNative2D()
    {
        using var leaf = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(1f, 2f, 8f, 6f),
                    Brush = new SolidColorBrush(Vector4.One),
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());
        using var root = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPicture,
                    Picture = leaf,
                    UseGpuTransforms = true,
                    CameraView = Matrix4x4.CreatePerspectiveFieldOfView(
                        0.75f,
                        1.5f,
                        0.1f,
                        100f),
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            root,
            304U,
            5U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnsupportedTransform, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawPicture, failure.CommandType);
    }

    [Fact]
    public void CompilerLowersGpuSeriesWithDeviceFixedWidths()
    {
        Brush brush = new SolidColorBrush(new Vector4(0.8f, 0.3f, 0.2f, 1f));
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawExtension,
                    ExtensionId = CompositorBuiltInExtensions.GpuLineSeries,
                    Brush = brush,
                    GpuPointsCount = 3,
                    FloatBufferOffset = 0,
                    FloatBufferCount = 6,
                    RadiusX = 2f,
                    Scale = new Vector2(2f, 3f)
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawExtension,
                    ExtensionId = CompositorBuiltInExtensions.GpuScatterSeries,
                    Brush = brush,
                    GpuPointsCount = 2,
                    FloatBufferOffset = 6,
                    FloatBufferCount = 4,
                    RadiusX = 4f,
                    Translate = new Vector2(5f, 7f)
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            [0f, 0f, 1f, 1f, 2f, 0f, 3f, 4f, 5f, 6f]);

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            303U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.StrokeCount);
        Assert.Equal(1, compiled.PointBatchCount);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        NativeSceneStroke line = MemoryMarshal.Read<NativeSceneStroke>(
            compiled.Stream.Slice(checked((int)resources[0].PayloadOffset)));
        NativeScenePointBatch scatter =
            MemoryMarshal.Read<NativeScenePointBatch>(
                compiled.Stream.Slice(checked((int)resources[1].PayloadOffset)));
        Assert.True((line.Flags & NativePolylineFlags.FixedDeviceStroke) != 0);
        Assert.True((scatter.Flags &
            NativePointBatchFlags.FixedDeviceRadius) != 0);
    }

    [Fact]
    public void CompilerIsolatesEachDrawInsideManagedBlendScope()
    {
        Brush brush = new SolidColorBrush(Vector4.One);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushBlendMode,
                    IntParam = (int)GpuBlendMode.Multiply
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Brush = brush
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(5f, 5f, 10f, 10f),
                    Brush = brush
                },
                new RenderCommand { Type = RenderCommandType.PopBlendMode }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            304U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(6, compiled.NativeCommandCount);
        Assert.Equal(2, compiled.NativeDrawCount);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneCommand> commands =
            MemoryMarshal.Cast<byte, NativeMethods.SceneCommand>(
                compiled.Stream.Slice(
                    checked((int)header.CommandOffset),
                    checked((int)header.CommandCount *
                        Unsafe.SizeOf<NativeMethods.SceneCommand>())));
        Assert.Equal(NativeSceneCommandKind.PushLayer, commands[0].Kind);
        Assert.Equal(NativeSceneCommandKind.DrawAnalytic, commands[1].Kind);
        Assert.Equal(NativeSceneCommandKind.PopLayer, commands[2].Kind);
        Assert.Equal(NativeSceneCommandKind.PushLayer, commands[3].Kind);
        NativeSceneLayer layer = MemoryMarshal.Read<NativeSceneLayer>(
            compiled.Stream.Slice(checked((int)commands[0].PayloadOffset)));
        Assert.Equal(GpuBlendMode.Multiply, layer.BlendMode);
        Assert.True((layer.Flags & NativeSceneLayerFlags.ForceIsolation) != 0);
    }

    [Fact]
    public void CompilerShapesLegacyTextBeforeNativeGlyphLowering()
    {
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawText,
                    Text = "Native text",
                    Font = InterFontFamily.Regular,
                    FontSize = 18f,
                    Brush = new SolidColorBrush(Vector4.One),
                    Position = new Vector2(4f, 24f),
                    PreferGlyphAtlas = true
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            305U,
            1U,
            new NativePictureCompileOptions(2f),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.True(compiled.PositionedGlyphCount > 0);
        Assert.True(compiled.GlyphOutlineCount > 0);
        Assert.Equal(1, compiled.TextStyleCount);
    }

    [Fact]
    public void CompilerLowersRepeatedBitmapGlyphsToOneDecodedColorResource()
    {
        var font = new TtfFont(SfntFontFaceTests.BuildSingleBitmapGlyphFont());
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawGlyphRun,
                    GlyphIndices = [1, 1],
                    GlyphPositions =
                    [new Vector2(8.25f, 24f), new Vector2(32.25f, 24f)],
                    Font = font,
                    FontSize = 20f,
                    Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.5f)),
                    TextRenderingMode = TextRenderingMode.Grayscale,
                    PreferGlyphAtlas = true
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            95U,
            11U,
            new NativePictureCompileOptions(2f),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(0, compiled.GlyphOutlineCount);
        Assert.Equal(1, compiled.ColorGlyphBitmapCount);
        Assert.Equal(4, compiled.ColorGlyphPixelBytes);
        Assert.Equal(2, compiled.PositionedGlyphCount);
        Assert.Equal(1, compiled.TextStyleCount);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        Assert.Equal(NativeSceneResourceKind.GlyphRun, resources[0].Kind);
        Assert.True((resources[0].Flags &
            NativeSceneRecordFlags.ColorGlyphBitmaps) != 0);
        Assert.Equal(
            (uint)Unsafe.SizeOf<NativeSceneColorGlyphBitmap>(),
            resources[0].PayloadSize);
        Assert.Equal(4U, resources[0].AuxiliarySize);
        NativeSceneColorGlyphBitmap bitmap =
            MemoryMarshal.Read<NativeSceneColorGlyphBitmap>(
                compiled.Stream.Slice(
                    checked((int)resources[0].PayloadOffset)));
        Assert.Equal(1U, bitmap.Width);
        Assert.Equal(1U, bitmap.Height);
        Assert.Equal(4U, bitmap.RowBytes);
        Assert.Equal(2f, bitmap.BearX);
        Assert.Equal(5f, bitmap.BearY);
        NativeMethods.SceneCommand command =
            MemoryMarshal.Read<NativeMethods.SceneCommand>(
                compiled.Stream.Slice(checked((int)header.CommandOffset)));
        ReadOnlySpan<NativePositionedGlyph> positioned =
            MemoryMarshal.Cast<byte, NativePositionedGlyph>(
                compiled.Stream.Slice(
                    checked((int)command.PayloadOffset + 24),
                    2 * Unsafe.SizeOf<NativePositionedGlyph>()));
        Assert.Equal(2f, positioned[0].AtlasToLogicalScale);
        Assert.Equal(new Vector2(8f, 24f), positioned[0].Position);
        Assert.Equal(new Vector2(32f, 24f), positioned[1].Position);
        NativeSceneTextStyle style =
            MemoryMarshal.Read<NativeSceneTextStyle>(
                compiled.Stream.Slice(
                    checked((int)resources[1].PayloadOffset)));
        Assert.Equal(0.5f, style.Color.W);
        Assert.Contains(
            compiled.Stream.Slice(
                checked((int)resources[0].AuxiliaryOffset),
                checked((int)resources[0].AuxiliarySize)).ToArray(),
            static value => value != 0);
    }

    [Fact]
    public void CompilerPreservesMixedMonochromeAndColorGlyphDrawOrder()
    {
        var font = new TtfFont(
            CompositorReviewRegressionTests.BuildColorLayerFont());
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawGlyphRun,
                    GlyphIndices = [2, 1, 3],
                    GlyphPositions =
                    [
                        new Vector2(2f, 24f),
                        new Vector2(22.125f, 24.25f),
                        new Vector2(44f, 24f)
                    ],
                    Font = font,
                    FontSize = 20f,
                    Brush = new SolidColorBrush(Vector4.One),
                    TextRenderingMode = TextRenderingMode.Grayscale,
                    PreferGlyphAtlas = true
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            90U,
            6U,
            new NativePictureCompileOptions(2f),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(3, compiled.NativeCommandCount);
        Assert.Equal(3, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.PathCount);
        Assert.True(compiled.PathSegmentCount > 0);
        Assert.Equal(2, compiled.GlyphOutlineCount);
        Assert.Equal(2, compiled.PositionedGlyphCount);
        Assert.Equal(1, compiled.TextStyleCount);
        Assert.Equal(2, compiled.BrushCount);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneCommand> commands =
            MemoryMarshal.Cast<byte, NativeMethods.SceneCommand>(
                compiled.Stream.Slice(
                    checked((int)header.CommandOffset),
                    checked((int)header.CommandCount *
                        Unsafe.SizeOf<NativeMethods.SceneCommand>())));
        Assert.Equal(
            [
                NativeSceneCommandKind.DrawGlyphRun,
                NativeSceneCommandKind.DrawPath,
                NativeSceneCommandKind.DrawGlyphRun
            ],
            commands.ToArray().Select(static command => command.Kind));
    }

    [Fact]
    public void CompilerDeduplicatesRepeatedColorGlyphSolidMaterials()
    {
        var font = new TtfFont(
            CompositorReviewRegressionTests.BuildColorLayerFont());
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawGlyphRun,
                    GlyphIndices = [1, 1, 1],
                    GlyphPositions =
                    [
                        new Vector2(2f, 24f),
                        new Vector2(24f, 24f),
                        new Vector2(46f, 24f)
                    ],
                    Font = font,
                    FontSize = 20f,
                    Brush = new SolidColorBrush(Vector4.One),
                    TextRenderingMode = TextRenderingMode.Grayscale,
                    PreferGlyphAtlas = true
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            94U,
            10U,
            new NativePictureCompileOptions(2f),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(6, compiled.PathCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.BrushCount);
        Assert.Equal(0, compiled.TextStyleCount);
    }

    [Fact]
    public void CompilerLowersExplicitVectorGlyphRenderingToPathResource()
    {
        var font = InterFontFamily.Regular;
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawGlyphRun,
                    GlyphIndices = [font.GetGlyphIndex('A')],
                    GlyphPositions = [new Vector2(12.125f, 30.25f)],
                    Font = font,
                    FontSize = 24f,
                    Brush = new SolidColorBrush(Vector4.One),
                    UseVectorGlyphRendering = true,
                    TextRenderingMode = TextRenderingMode.Grayscale
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            93U,
            9U,
            new NativePictureCompileOptions(2f),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.PathCount);
        Assert.True(compiled.PathSegmentCount > 0);
        Assert.Equal(0, compiled.GlyphOutlineCount);
        Assert.Equal(0, compiled.PositionedGlyphCount);
        Assert.Equal(1, compiled.BrushCount);
    }

    [Fact]
    public void CompilerLowersShapedGlyphRunToNativeOutlineAndStyleResources()
    {
        var font = InterFontFamily.Regular;
        ushort[] glyphIndices =
        [font.GetGlyphIndex('A'), font.GetGlyphIndex('V')];
        Vector2[] glyphPositions =
        [new(12.125f, 30.25f), new(35.625f, 30.25f)];
        var brush = new SolidColorBrush(new Vector4(0.2f, 0.4f, 0.8f, 0.75f))
        {
            Opacity = 0.5f
        };
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawGlyphRun,
                    GlyphIndices = glyphIndices,
                    GlyphPositions = glyphPositions,
                    Font = font,
                    FontSize = 24f,
                    Brush = brush,
                    Position = new Vector2(3f, 4f),
                    Transform = Matrix4x4.CreateScale(1.5f, 0.8f, 1f),
                    IsBold = true,
                    IsItalic = true,
                    HasFontTransform = true,
                    FontTransform = new Vector2(1.2f, 0.1f),
                    TextRenderingMode = TextRenderingMode.ClearType,
                    PreferGlyphAtlas = true
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            91U,
            7U,
            new NativePictureCompileOptions(2f),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(2f, compiled.TargetDpiScale);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.GlyphOutlineCount);
        Assert.True(compiled.GlyphSegmentCount > 0);
        Assert.Equal(4, compiled.PositionedGlyphCount);
        Assert.Equal(1, compiled.TextStyleCount);
        Assert.Equal(0, compiled.BrushCount);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        Assert.Equal(1U, header.CommandCount);
        Assert.Equal(2U, header.ResourceCount);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        Assert.Equal(NativeSceneResourceKind.GlyphRun, resources[0].Kind);
        Assert.Equal(
            NativeSceneResourceKind.TextStyleTable,
            resources[1].Kind);
        Assert.Equal(
            2U * (uint)Unsafe.SizeOf<NativeSceneGlyphOutline>(),
            resources[0].PayloadSize);
        ReadOnlySpan<NativeSceneGlyphOutline> outlines =
            MemoryMarshal.Cast<byte, NativeSceneGlyphOutline>(
                compiled.Stream.Slice(
                    checked((int)resources[0].PayloadOffset),
                    checked((int)resources[0].PayloadSize)));
        Assert.All(outlines.ToArray(), outline =>
        {
            Assert.Equal(72f / font.UnitsPerEm, outline.RasterScale, 5);
            Assert.Equal(0f, outline.SubpixelX);
        });

        NativeSceneTextStyle style = MemoryMarshal.Read<NativeSceneTextStyle>(
            compiled.Stream.Slice(checked((int)resources[1].PayloadOffset)));
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.8f, 0.375f), style.Color);
        Assert.Equal(
            NativeSceneTextRenderingMode.ClearType,
            style.TextRenderingMode);

        NativeMethods.SceneCommand command =
            MemoryMarshal.Read<NativeMethods.SceneCommand>(
                compiled.Stream.Slice(checked((int)header.CommandOffset)));
        Assert.Equal(NativeSceneCommandKind.DrawGlyphRun, command.Kind);
        Assert.True((command.Flags & NativeSceneRecordFlags.StyledGlyphs) != 0);
        Assert.Equal(
            24U + 4U * (uint)Unsafe.SizeOf<NativePositionedGlyph>(),
            command.PayloadSize);
        ReadOnlySpan<NativePositionedGlyph> positioned =
            MemoryMarshal.Cast<byte, NativePositionedGlyph>(
                compiled.Stream.Slice(
                    checked((int)command.PayloadOffset + 24),
                    4 * Unsafe.SizeOf<NativePositionedGlyph>()));
        Assert.Equal(1.8f, positioned[0].BasisX.X, 5);
        Assert.Equal(0f, positioned[0].BasisX.Y);
        Assert.Equal(0f, positioned[0].BasisY.X);
        Assert.Equal(0.8f, positioned[0].BasisY.Y, 5);
        Assert.Equal(0f, positioned[0].BoldOffset);
        Assert.Equal(24f * 0.035f / 1.2f, positioned[1].BoldOffset, 5);
        Assert.Equal((0.22f - 0.1f) / 1.2f, positioned[0].ItalicSkew, 5);
    }

    [Fact]
    public void CompilerPreservesFourWayPhysicalGlyphPhaseAtTargetDpi()
    {
        var font = InterFontFamily.Regular;
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawGlyphRun,
                    GlyphIndices = [font.GetGlyphIndex('i')],
                    GlyphPositions = [new Vector2(5.125f, 12.2f)],
                    Font = font,
                    FontSize = 11f,
                    Brush = new SolidColorBrush(Vector4.One),
                    Transform = Matrix4x4.Identity,
                    TextRenderingMode = TextRenderingMode.Grayscale,
                    TextHintingMode = TextHintingMode.Fixed,
                    PreferGlyphAtlas = true
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            92U,
            8U,
            new NativePictureCompileOptions(2f),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneResource resource =
            MemoryMarshal.Read<NativeMethods.SceneResource>(
                compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        NativeSceneGlyphOutline outline =
            MemoryMarshal.Read<NativeSceneGlyphOutline>(
                compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(0.25f, outline.SubpixelX);
        NativeMethods.SceneCommand command =
            MemoryMarshal.Read<NativeMethods.SceneCommand>(
                compiled.Stream.Slice(checked((int)header.CommandOffset)));
        NativePositionedGlyph glyph =
            MemoryMarshal.Read<NativePositionedGlyph>(
                compiled.Stream.Slice(checked((int)command.PayloadOffset + 24)));
        Assert.Equal(new Vector2(5f, 12f), glyph.Position);
    }

    [Fact]
    public void CompilerRejectsInvalidTargetDpiBeforeReadingPictureCommands()
    {
        using var picture = new GpuPicture(
            [new RenderCommand { Type = RenderCommandType.DrawTexture }],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            new NativePictureCompileOptions(float.NaN),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.InvalidArgument, failure.Error);
        Assert.Equal(-1, failure.CommandIndex);
    }

    [Fact]
    public void CompilerCarriesExactTexturePixelSnappingIntoNativeScene()
    {
        using GpuTexture texture = CreateUnbackedTexture(16U, 8U);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawTexture,
                    Texture = texture,
                    Rect = new Rect(1.26f, -2.24f, 4f, 3f),
                    SrcRect = new Rect(2f, 1f, 8f, 4f),
                    TextureSamplingMode = TextureSamplingMode.Linear,
                    SnapTextureToPixels = true,
                    Transform = Matrix4x4.Identity
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            122U,
            1U,
            new NativePictureCompileOptions(2f),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.ExternalImages.Length);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneCommand command =
            MemoryMarshal.Read<NativeMethods.SceneCommand>(
                compiled.Stream.Slice(checked((int)header.CommandOffset)));
        NativeSceneImageDraw draw =
            MemoryMarshal.Read<NativeSceneImageDraw>(
                compiled.Stream.Slice(checked((int)command.PayloadOffset)));
        Assert.Equal(NativeSceneImageFlags.SnapToPixels, draw.Flags);
        Assert.Equal(new NativeImageRect(2f, 1f, 8f, 4f), draw.SourceRect);
        Assert.Equal(1.01f, command.Bounds.X, 3);
        Assert.Equal(-2.49f, command.Bounds.Y, 3);
        Assert.Equal(4.5f, command.Bounds.Width, 3);
        Assert.Equal(3.5f, command.Bounds.Height, 3);
    }

    [Fact]
    public void CompilerCarriesPremultipliedTextureSourceIntoNativeScene()
    {
        using GpuTexture texture = CreateUnbackedTexture(16U, 8U);
        texture.AlphaMode = GpuTextureAlphaMode.Premultiplied;
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawTexture,
                    Texture = texture,
                    Rect = new Rect(1f, 2f, 4f, 3f),
                    SrcRect = new Rect(2f, 1f, 8f, 4f),
                    TextureSamplingMode = TextureSamplingMode.Cubic,
                    Transform = Matrix4x4.Identity
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            123U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneCommand command =
            MemoryMarshal.Read<NativeMethods.SceneCommand>(
                compiled.Stream.Slice(checked((int)header.CommandOffset)));
        NativeSceneImageDraw draw =
            MemoryMarshal.Read<NativeSceneImageDraw>(
                compiled.Stream.Slice(checked((int)command.PayloadOffset)));
        Assert.Equal(
            NativeSceneImageFlags.SourcePremultiplied,
            draw.Flags);
        Assert.Equal(NativeImageSampling.Cubic, draw.Sampling);
        Assert.Equal(
            (uint)(Unsafe.SizeOf<NativeSceneImageDraw>() +
                Unsafe.SizeOf<NativeSceneImageSamplingOptions>()),
            command.PayloadSize);
    }

    [Theory]
    [InlineData(TextureSamplingMode.Nearest, NativeImageSampling.Nearest, 1U)]
    [InlineData(TextureSamplingMode.Linear, NativeImageSampling.Linear, 1U)]
    [InlineData(TextureSamplingMode.Cubic, NativeImageSampling.Cubic, 1U)]
    [InlineData(
        TextureSamplingMode.LinearMipmap,
        NativeImageSampling.LinearMipmap,
        16U)]
    [InlineData(
        TextureSamplingMode.MagLinearMinLinearMipNearest,
        NativeImageSampling.MagLinearMinLinearMipNearest,
        1U)]
    [InlineData(
        TextureSamplingMode.MagLinearMinNearestMipLinear,
        NativeImageSampling.MagLinearMinNearestMipLinear,
        1U)]
    [InlineData(
        TextureSamplingMode.MagLinearMinNearestMipNearest,
        NativeImageSampling.MagLinearMinNearestMipNearest,
        1U)]
    [InlineData(
        TextureSamplingMode.MagNearestMinLinearMipLinear,
        NativeImageSampling.MagNearestMinLinearMipLinear,
        1U)]
    [InlineData(
        TextureSamplingMode.MagNearestMinLinearMipNearest,
        NativeImageSampling.MagNearestMinLinearMipNearest,
        1U)]
    [InlineData(
        TextureSamplingMode.MagNearestMinNearestMipLinear,
        NativeImageSampling.MagNearestMinNearestMipLinear,
        1U)]
    public void CompilerCarriesEveryManagedTextureSamplerIntoNativeScene(
        TextureSamplingMode sourceSampling,
        NativeImageSampling expectedSampling,
        uint expectedMaxAnisotropy)
    {
        using GpuTexture texture = CreateUnbackedTexture(16U, 8U);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawTexture,
                    Texture = texture,
                    Rect = new Rect(1f, 2f, 4f, 3f),
                    SrcRect = new Rect(2f, 1f, 8f, 4f),
                    TextureSamplingMode = sourceSampling,
                    TextureMaxAnisotropy = 64,
                    Transform = Matrix4x4.Identity
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            126U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneCommand command =
            MemoryMarshal.Read<NativeMethods.SceneCommand>(
                compiled.Stream.Slice(checked((int)header.CommandOffset)));
        NativeSceneImageDraw draw =
            MemoryMarshal.Read<NativeSceneImageDraw>(
                compiled.Stream.Slice(checked((int)command.PayloadOffset)));
        Assert.Equal(expectedSampling, draw.Sampling);
        Assert.Equal(expectedMaxAnisotropy, draw.MaxAnisotropy);
    }

    [Fact]
    public void CompilerLowersTexturePatchesToOneNativeImageDraw()
    {
        using GpuTexture texture = CreateUnbackedTexture(16U, 8U);
        texture.AlphaMode = GpuTextureAlphaMode.Premultiplied;
        TexturePatch[] patches =
        [
            new(
                new Rect(0f, 0f, 4f, 4f),
                new Rect(1f, 2f, 8f, 6f)),
            new(
                new Rect(10f, 2f, 4f, 6f),
                new Vector4(1f, 0.5f, 0.25f, 0.5f)),
            new(
                new Rect(4f, 0f, 4f, 4f),
                new Rect(16f, 2f, 8f, 6f),
                Matrix3x2.CreateTranslation(1f, 0f),
                new Vector4(0.2f, 0.4f, 0.6f, 0.8f),
                VertexColorBlendMode.Multiply)
        ];
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawTexture,
                    Texture = texture,
                    TexturePatches = patches,
                    TextureSamplingMode = TextureSamplingMode.Cubic,
                    SnapTextureToPixels = true,
                    Transform = Matrix4x4.Identity
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            124U,
            1U,
            new NativePictureCompileOptions(2f),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(1, compiled.ExternalImages.Length);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneCommand command =
            MemoryMarshal.Read<NativeMethods.SceneCommand>(
                compiled.Stream.Slice(checked((int)header.CommandOffset)));
        int payloadOffset = checked((int)command.PayloadOffset);
        NativeSceneImageDraw draw = MemoryMarshal.Read<NativeSceneImageDraw>(
            compiled.Stream.Slice(payloadOffset));
        Assert.Equal(
            NativeSceneImageFlags.PatchBatch |
                NativeSceneImageFlags.SourcePremultiplied |
                NativeSceneImageFlags.SnapToPixels,
            draw.Flags);
        int batchOffset = payloadOffset +
            Unsafe.SizeOf<NativeSceneImageDraw>() +
            Unsafe.SizeOf<NativeSceneImageSamplingOptions>();
        NativeSceneImagePatchBatch batch =
            MemoryMarshal.Read<NativeSceneImagePatchBatch>(
                compiled.Stream.Slice(batchOffset));
        Assert.Equal(3U, batch.PatchCount);
        ReadOnlySpan<NativeSceneImagePatch> retained =
            MemoryMarshal.Cast<byte, NativeSceneImagePatch>(
                compiled.Stream.Slice(
                    batchOffset + Unsafe.SizeOf<NativeSceneImagePatchBatch>(),
                    3 * Unsafe.SizeOf<NativeSceneImagePatch>()));
        Assert.Equal(NativeSceneImagePatchKind.Texture, retained[0].Kind);
        Assert.Equal(NativeSceneImagePatchKind.FixedColor, retained[1].Kind);
        Assert.Equal(NativeSceneImagePatchKind.AtlasColor, retained[2].Kind);
        Assert.Equal(
            NativeImagePatchColorBlendMode.Multiply,
            retained[2].ColorBlendMode);
        Assert.Equal(1f, retained[2].Transform.M31);
    }

    [Fact]
    public void CompilerBatchesConsecutiveCompatibleTexturesIntoOneNativeDraw()
    {
        using GpuTexture texture = CreateUnbackedTexture(16U, 8U);
        using var picture = new GpuPicture(
            [
                CreateTextureCommand(texture, new Rect(0f, 0f, 8f, 8f)),
                CreateTextureCommand(texture, new Rect(8f, 0f, 8f, 8f)),
                CreateTextureCommand(texture, new Rect(16f, 0f, 8f, 8f))
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            127U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(3, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(1, compiled.ExternalImages.Length);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneCommand command =
            MemoryMarshal.Read<NativeMethods.SceneCommand>(
                compiled.Stream.Slice(checked((int)header.CommandOffset)));
        int payloadOffset = checked((int)command.PayloadOffset);
        NativeSceneImageDraw draw = MemoryMarshal.Read<NativeSceneImageDraw>(
            compiled.Stream.Slice(payloadOffset));
        Assert.Equal(NativeSceneImageFlags.PatchBatch, draw.Flags);
        int batchOffset = payloadOffset + Unsafe.SizeOf<NativeSceneImageDraw>();
        NativeSceneImagePatchBatch batch =
            MemoryMarshal.Read<NativeSceneImagePatchBatch>(
                compiled.Stream.Slice(batchOffset));
        Assert.Equal(3U, batch.PatchCount);
        ReadOnlySpan<NativeSceneImagePatch> patches =
            MemoryMarshal.Cast<byte, NativeSceneImagePatch>(
                compiled.Stream.Slice(
                    batchOffset + Unsafe.SizeOf<NativeSceneImagePatchBatch>(),
                    3 * Unsafe.SizeOf<NativeSceneImagePatch>()));
        Assert.All(
            patches.ToArray(),
            static patch => Assert.Equal(
                NativeSceneImagePatchKind.Texture,
                patch.Kind));
        Assert.Equal(0f, patches[0].DestinationRect.X);
        Assert.Equal(8f, patches[1].DestinationRect.X);
        Assert.Equal(16f, patches[2].DestinationRect.X);
        Assert.Equal(new NativeImageRect(0f, 0f, 24f, 8f), command.Bounds);
    }

    [Fact]
    public void CompilerPreservesTextureBatchStateBoundaries()
    {
        using GpuTexture first = CreateUnbackedTexture(16U, 8U);
        using GpuTexture second = CreateUnbackedTexture(16U, 8U);
        RenderCommand nearest = CreateTextureCommand(
            first,
            new Rect(8f, 0f, 8f, 8f));
        nearest.TextureSamplingMode = TextureSamplingMode.Nearest;
        using var picture = new GpuPicture(
            [
                CreateTextureCommand(first, new Rect(0f, 0f, 8f, 8f)),
                nearest,
                CreateTextureCommand(second, new Rect(16f, 0f, 8f, 8f))
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            128U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(3, compiled.NativeDrawCount);
        Assert.Equal(3, compiled.ExternalImages.Length);
    }

    private static RenderCommand CreateTextureCommand(
        GpuTexture texture,
        Rect destination) =>
        new()
        {
            Type = RenderCommandType.DrawTexture,
            Texture = texture,
            Rect = destination,
            SrcRect = new Rect(0f, 0f, 16f, 8f),
            TextureSamplingMode = TextureSamplingMode.Linear,
            Transform = Matrix4x4.Identity
        };

    [Fact]
    public void CompilerLowersAndBatchesSupportedImmutablePictureCommands()
    {
        var red = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f));
        var green = new SolidColorBrush(new Vector4(0f, 1f, 0f, 1f));
        var bluePen = new Pen(
            new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
            2f,
            startLineCap: PenLineCap.Round,
            endLineCap: PenLineCap.Square);
        var commands = new RenderCommand[]
        {
            new()
            {
                Type = RenderCommandType.DrawRect,
                Rect = new Rect(2f, 3f, 20f, 12f),
                Brush = red,
                Transform = Matrix4x4.Identity
            },
            new()
            {
                Type = RenderCommandType.DrawEllipse,
                Position2 = new Vector2(32f, 10f),
                RadiusX = 7f,
                RadiusY = 5f,
                Brush = green,
                Transform = Matrix4x4.Identity
            },
            new()
            {
                Type = RenderCommandType.DrawLine,
                Position = new Vector2(4f, 25f),
                Position2 = new Vector2(45f, 28f),
                Pen = bluePen,
                IsPenThicknessLocal = true,
                Transform = Matrix4x4.Identity
            },
            new()
            {
                Type = RenderCommandType.FillTriangle,
                Position = new Vector2(5f, 34f),
                Position2 = new Vector2(25f, 34f),
                Position3 = new Vector2(15f, 46f),
                Brush = red,
                Transform = Matrix4x4.Identity
            },
            new()
            {
                Type = RenderCommandType.DrawRect,
                Rect = new Rect(50f, 2f, 8f, 8f),
                Brush = green,
                Transform = Matrix4x4.Identity
            }
        };
        using var picture = new GpuPicture(
            commands,
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            81U,
            4U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(5, compiled.SourceCommandCount);
        Assert.Equal(3, compiled.NativeCommandCount);
        Assert.Equal(3, compiled.NativeDrawCount);
        Assert.Equal(3, compiled.AnalyticPrimitiveCount);
        Assert.Equal(2, compiled.GeometryPrimitiveCount);
        Assert.Equal(3, compiled.BrushCount);
        Assert.Equal(0, compiled.GradientStopCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        Assert.Equal(3U, header.CommandCount);
        Assert.Equal(4U, header.ResourceCount);
        Assert.Equal(81UL, header.SceneId);
        Assert.Equal(4UL, header.Generation);
        var secondResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(
                checked((int)header.ResourceOffset) +
                    Unsafe.SizeOf<NativeMethods.SceneResource>()));
        Assert.Equal(NativeSceneResourceKind.GeometryBatch, secondResource.Kind);
    }

    [Fact]
    public void CompilerLowersAdvancedAnalyticPrimitiveStrokesThroughNativePaths()
    {
        var fill = new SolidColorBrush(
            new Vector4(0.8f, 0.2f, 0.1f, 1f));
        var dashed = new Pen(
            new SolidColorBrush(new Vector4(0.1f, 0.7f, 1f, 1f)),
            3f,
            PenLineJoin.Round,
            5f,
            PenLineCap.Round,
            PenLineCap.Square,
            PenLineCap.Triangle,
            [2.0, 1.0],
            0.5);
        var hairline = new Pen(
            new LinearGradientBrush(
                new Vector2(0f, 0f),
                new Vector2(80f, 0f),
                [
                    new GradientStop(Vector4.One, 0f),
                    new GradientStop(new Vector4(1f, 0f, 0f, 1f), 1f)
                ]),
            Pen.HairlineThickness,
            PenLineJoin.Bevel);
        var fixedPen = new Pen(
            new SolidColorBrush(new Vector4(0.2f, 0.9f, 0.4f, 1f)),
            4f,
            PenLineJoin.Round,
            strokeTransformMode: PenStrokeTransformMode.Fixed);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 160f, 100f));
        drawing.DrawRectangle(fill, dashed, new Rect(4f, 4f, 28f, 18f));
        drawing.DrawEllipse(null, hairline, new Vector2(52f, 14f), 14f, 9f);
        drawing.DrawCircle(null, fixedPen, new Vector2(84f, 14f), 11f);
        drawing.DrawRoundedRectangle(
            fill,
            dashed,
            new Rect(4f, 40f, 56f, 28f),
            7f);
        drawing.DrawRoundedRectangle(
            fill,
            fixedPen,
            new Rect(76f, 40f, 64f, 34f),
            radiusX: 12f,
            radiusY: 6f);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            82U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(5, compiled.SourceCommandCount);
        Assert.Equal(2, compiled.AnalyticPrimitiveCount);
        Assert.Equal(1, compiled.PathCount);
        Assert.True(compiled.PathSegmentCount >= 8);
        Assert.True(compiled.GeometryPrimitiveCount > 8);
        Assert.True(compiled.NativeDrawCount >= 5);
    }

    [Fact]
    public void CompilerSnapshotsAllRetainedGradientFamiliesAndTransforms()
    {
        GradientStop[] stops =
        [
            new(new Vector4(1f, 0f, 0f, 1f), 0f),
            new(new Vector4(0f, 0f, 1f, 1f), 1f)
        ];
        var linear = new LinearGradientBrush(
            new Vector2(2f, 3f),
            new Vector2(40f, 3f),
            stops)
        {
            Opacity = 0.75f,
            CoordinateTransform = Matrix4x4.CreateTranslation(4f, 5f, 0f),
            SpreadMethod = GradientSpreadMethod.Reflect,
            ColorInterpolationMode =
                GradientColorInterpolationMode.ScRgbLinearInterpolation
        };
        var radial = new RadialGradientBrush(
            new Vector2(20f, 20f),
            new Vector2(18f, 19f),
            12f,
            8f,
            stops);
        var conical = new TwoPointConicalGradientBrush(
            new Vector2(5f, 5f),
            2f,
            new Vector2(30f, 20f),
            14f,
            stops)
        {
            OutsideColor = new Vector4(0f, 1f, 0f, 1f)
        };
        var sweep = new SweepGradientBrush(new Vector2(20f, 20f), stops)
        {
            StartAngle = 20f,
            EndAngle = 300f
        };
        using var picture = new GpuPicture(
            new[]
            {
                Rectangle(linear, 0f),
                Rectangle(radial, 12f),
                Rectangle(conical, 24f),
                Rectangle(sweep, 36f)
            },
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            90U,
            2U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(4, compiled.BrushCount);
        Assert.Equal(8, compiled.GradientStopCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        Assert.Equal(1U, header.CommandCount);
        Assert.Equal(2U, header.ResourceCount);
        var brushResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(
                checked((int)header.ResourceOffset) +
                    Unsafe.SizeOf<NativeMethods.SceneResource>()));
        Assert.Equal(NativeSceneResourceKind.BrushTable, brushResource.Kind);
        ReadOnlySpan<NativeSceneBrush> brushes = MemoryMarshal.Cast<byte, NativeSceneBrush>(
            compiled.Stream.Slice(
                checked((int)brushResource.PayloadOffset),
                checked((int)brushResource.PayloadSize)));
        Assert.Equal(NativeSceneBrushKind.LinearGradient, brushes[0].Kind);
        Assert.Equal(NativeSceneBrushKind.RadialGradient, brushes[1].Kind);
        Assert.Equal(NativeSceneBrushKind.TwoPointConicalGradient, brushes[2].Kind);
        Assert.Equal(NativeSceneBrushKind.SweepGradient, brushes[3].Kind);
        Assert.Equal(0.75f, brushes[0].Opacity);
        Assert.Equal(4f, brushes[0].CoordinateTransform0.Z);
        Assert.Equal(5f, brushes[0].CoordinateTransform1.Z);

        static RenderCommand Rectangle(Brush brush, float x) => new()
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(x, 0f, 10f, 10f),
            Brush = brush,
            Transform = Matrix4x4.Identity
        };
    }

    [Fact]
    public void CompilerLowersDotGridToOneNativeGeometryPrimitive()
    {
        var brush = new LinearGradientBrush(
            new Vector2(4f, 6f),
            new Vector2(44f, 6f),
            [
                new GradientStop(new Vector4(1f, 1f, 0f, 1f), 0f),
                new GradientStop(new Vector4(0f, 1f, 1f, 1f), 1f)
            ]);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 64f, 48f));
        drawing.DrawDotGrid(
            brush,
            new Rect(4f, 6f, 40f, 30f),
            8f,
            1.5f,
            new Vector2(2f, 3f));
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            92U,
            4U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(0, compiled.AnalyticPrimitiveCount);
        Assert.Equal(1, compiled.GeometryPrimitiveCount);
        Assert.Equal(1, compiled.BrushCount);
        Assert.Equal(2, compiled.GradientStopCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        var primitive = MemoryMarshal.Read<NativeGeometryPrimitive>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(NativeGeometryPrimitiveKind.DotGrid, primitive.Kind);
        Assert.Equal(new Vector2(4f, 6f), primitive.P0);
        Assert.Equal(new Vector2(40f, 30f), primitive.P1);
        Assert.Equal(new Vector2(2f, 3f), primitive.P2);
        Assert.Equal(new Vector2(8f, 1.5f), primitive.P3);
    }

    [Fact]
    public void CompilerLowersIsometricDeviceDotGridToOneNativeGeometryPrimitive()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 80f, 60f));
        float cosine30 = MathF.Sqrt(3f) * 0.5f;
        Matrix4x4 transform = new(
            cosine30, 0.5f, 0f, 0f,
            -cosine30, 0.5f, 0f, 0f,
            0f, 0f, 1f, 0f,
            32f, 32f, 0f, 1f);
        drawing.DrawDeviceDotGrid(
            new SolidColorBrush(Vector4.One),
            new Rect(-10f, -20f, 60f, 40f),
            new Vector2(7f, 11f),
            0.875f,
            transform);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            93U,
            4U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(1, compiled.GeometryPrimitiveCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        var primitive = MemoryMarshal.Read<NativeGeometryPrimitive>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(NativeGeometryPrimitiveKind.DotGrid, primitive.Kind);
        Assert.Equal(new Vector2(-10f, -20f), primitive.P0);
        Assert.Equal(new Vector2(60f, 40f), primitive.P1);
        Assert.Equal(Vector2.Zero, primitive.P2);
        Assert.Equal(new Vector2(7f, 11f), primitive.P3);
        Assert.Equal(0.875f, primitive.StrokeThickness);
        Assert.Equal(
            new Matrix3x2(
                cosine30,
                0.5f,
                -cosine30,
                0.5f,
                32f,
                32f),
            primitive.Transform);
    }

    [Fact]
    public void CompilerLowersDeviceLineGridToOneNativeGeometryPrimitive()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 80f, 60f));
        drawing.DrawDeviceLineGrid(
            new SolidColorBrush(Vector4.One),
            new Rect(-10f, -20f, 60f, 40f),
            new Vector2(7f, 11f),
            1.25f,
            7,
            Matrix4x4.CreateScale(2f, 3f, 1f));
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            94U,
            4U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(1, compiled.GeometryPrimitiveCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        var primitive = MemoryMarshal.Read<NativeGeometryPrimitive>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(NativeGeometryPrimitiveKind.DotGrid, primitive.Kind);
        Assert.Equal(new Vector2(-10f, -20f), primitive.P0);
        Assert.Equal(new Vector2(60f, 40f), primitive.P1);
        Assert.Equal(new Vector2(1f, 7f), primitive.P2);
        Assert.Equal(new Vector2(7f, 11f), primitive.P3);
        Assert.Equal(1.25f, primitive.StrokeThickness);
        Assert.Equal(new Matrix3x2(2f, 0f, 0f, 3f, 0f, 0f), primitive.Transform);
    }

    [Fact]
    public void CompilerCoalescesPointBatchesIntoCompactNativeResource()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 96f, 64f));
        drawing.DrawPointBatch(
            new SolidColorBrush(new Vector4(1f, 0.2f, 0.1f, 1f)),
            [new(12f, 16f), new(28f, 20f), new(44f, 18f)],
            3f,
            round: true);
        drawing.DrawPointBatch(
            new LinearGradientBrush(
                new Vector2(0f, 0f),
                new Vector2(96f, 0f),
                [
                    new GradientStop(new Vector4(0f, 1f, 1f, 1f), 0f),
                    new GradientStop(new Vector4(1f, 0f, 1f, 1f), 1f)
                ]),
            [new(60f, 22f), new(76f, 18f)],
            radius: 0f,
            round: false,
            isEdgeAliased: true);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            93U,
            5U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(2, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.PointBatchCount);
        Assert.Equal(5, compiled.PointCount);
        Assert.Equal(0, compiled.VertexMeshCount);
        Assert.Equal(2, compiled.BrushCount);
        Assert.Equal(2, compiled.GradientStopCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.PointBatch, resource.Kind);
        Assert.Equal(
            2 * Unsafe.SizeOf<NativeScenePointBatch>(),
            checked((int)resource.PayloadSize));
        Assert.Equal(
            5 * Unsafe.SizeOf<Vector2>(),
            checked((int)resource.AuxiliarySize));

        ReadOnlySpan<NativeScenePointBatch> nativeBatches =
            MemoryMarshal.Cast<byte, NativeScenePointBatch>(
                compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset),
                    checked((int)resource.PayloadSize)));
        Assert.Equal(0U, nativeBatches[0].PointOffset);
        Assert.Equal(3U, nativeBatches[0].PointCount);
        Assert.Equal(3f, nativeBatches[0].Radius);
        Assert.Equal(NativePointBatchFlags.Round, nativeBatches[0].Flags);
        Assert.Equal(3U, nativeBatches[1].PointOffset);
        Assert.Equal(2U, nativeBatches[1].PointCount);
        Assert.Equal(0.5f, nativeBatches[1].Radius);
        Assert.Equal(
            NativePointBatchFlags.EdgeAliased |
                NativePointBatchFlags.Hairline,
            nativeBatches[1].Flags);
    }

    [Fact]
    public void CompilerConsumesSpanBackedPointBatchWithoutInlineArray()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 64f, 48f));
        Span<Vector2> source = stackalloc Vector2[2]
        {
            new(7f, 11f),
            new(19f, 23f),
        };
        drawing.DrawPointBatch(
            new SolidColorBrush(Vector4.One),
            source,
            radius: 0f,
            round: true);
        RenderCommand command = Assert.Single(drawing.Commands);
        Assert.Null(command.PolylinePoints);
        Assert.Equal(0, command.PointBufferOffset);
        Assert.Equal(2, command.PointBufferCount);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            94U,
            6U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.PointBatchCount);
        Assert.Equal(2, compiled.PointCount);
    }

    [Fact]
    public void CompilerCoalescesVertexMeshesAndPreservesPackedAttributes()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 96f, 64f));
        drawing.DrawVertexMesh(
            new SolidColorBrush(new Vector4(0.2f, 0.4f, 0.8f, 1f)),
            new VertexMesh2D(
                VertexMeshTopology.Triangles,
                [new(2f, 3f), new(22f, 3f), new(12f, 18f)]),
            VertexColorBlendMode.SrcOver,
            Matrix4x4.CreateTranslation(4f, 5f, 0f));
        drawing.DrawVertexMesh(
            new LinearGradientBrush(
                new Vector2(0f, 0f),
                new Vector2(96f, 0f),
                [
                    new GradientStop(new Vector4(1f, 0f, 0f, 1f), 0f),
                    new GradientStop(new Vector4(0f, 0f, 1f, 1f), 1f)
                ]),
            new VertexMesh2D(
                VertexMeshTopology.TriangleStrip,
                [new(30f, 8f), new(50f, 8f), new(30f, 28f), new(50f, 28f)],
                [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.One],
                [
                    new(1f, 0f, 0f, 0.5f),
                    new(0f, 1f, 0f, 1f),
                    new(0f, 0f, 1f, 0.75f),
                    Vector4.One
                ],
                [0, 1, 2, 3]),
            VertexColorBlendMode.SoftLight,
            isEdgeAliased: true);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            94U,
            6U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(2, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.VertexMeshCount);
        Assert.Equal(7, compiled.MeshVertexCount);
        Assert.Equal(4, compiled.MeshIndexCount);
        Assert.Equal(0, compiled.PointBatchCount);
        Assert.Equal(2, compiled.BrushCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.VertexMesh, resource.Kind);
        Assert.Equal(
            2 * Unsafe.SizeOf<NativeSceneVertexMesh>(),
            checked((int)resource.PayloadSize));
        Assert.Equal(
            7 * Unsafe.SizeOf<NativeSceneMeshVertex>() + 4 * sizeof(ushort),
            checked((int)resource.AuxiliarySize));

        ReadOnlySpan<NativeSceneVertexMesh> meshes =
            MemoryMarshal.Cast<byte, NativeSceneVertexMesh>(
                compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset),
                    checked((int)resource.PayloadSize)));
        Assert.Equal(0U, meshes[0].VertexOffset);
        Assert.Equal(3U, meshes[0].VertexCount);
        Assert.Equal(0U, meshes[0].IndexCount);
        Assert.Equal(NativeVertexColorBlendMode.SrcOver, meshes[0].ColorBlendMode);
        Assert.Equal(3U, meshes[1].VertexOffset);
        Assert.Equal(4U, meshes[1].VertexCount);
        Assert.Equal(0U, meshes[1].IndexOffset);
        Assert.Equal(4U, meshes[1].IndexCount);
        Assert.Equal(NativeVertexMeshTopology.TriangleStrip, meshes[1].Topology);
        Assert.Equal(NativeVertexMeshFlags.EdgeAliased, meshes[1].Flags);
        Assert.Equal(
            NativeVertexColorBlendMode.SoftLight,
            meshes[1].ColorBlendMode);

        ReadOnlySpan<NativeSceneMeshVertex> vertices =
            MemoryMarshal.Cast<byte, NativeSceneMeshVertex>(
                compiled.Stream.Slice(
                    checked((int)resource.AuxiliaryOffset),
                    7 * Unsafe.SizeOf<NativeSceneMeshVertex>()));
        Assert.Equal(new Vector2(2f, 3f), vertices[0].Position);
        Assert.Equal(vertices[0].Position, vertices[0].TextureCoordinate);
        Assert.Equal(Vector4.One, vertices[0].Color);
        Assert.Equal(Vector2.Zero, vertices[3].TextureCoordinate);
        Assert.Equal(new Vector4(1f, 0f, 0f, 0.5f), vertices[3].Color);
        ReadOnlySpan<ushort> indices = MemoryMarshal.Cast<byte, ushort>(
            compiled.Stream.Slice(
                checked((int)resource.AuxiliaryOffset) +
                    7 * Unsafe.SizeOf<NativeSceneMeshVertex>(),
                4 * sizeof(ushort)));
        Assert.True(indices.SequenceEqual(new ushort[] { 0, 1, 2, 3 }));
    }

    [Fact]
    public void CompilerCoalescesPolylineAndSplineStrokeContracts()
    {
        var fixedPen = new Pen(
            new SolidColorBrush(new Vector4(0.2f, 0.7f, 0.9f, 1f)),
            3f,
            PenLineJoin.Round,
            5f,
            PenLineCap.Round,
            PenLineCap.Triangle,
            PenLineCap.Square,
            [2.0, 1.0],
            0.5,
            PenStrokeTransformMode.Fixed);
        var hairlinePen = new Pen(
            new LinearGradientBrush(
                new Vector2(0f, 0f),
                new Vector2(96f, 0f),
                [
                    new GradientStop(Vector4.One, 0f),
                    new GradientStop(new Vector4(1f, 0f, 0f, 1f), 1f)
                ]),
            Pen.HairlineThickness,
            PenLineJoin.Bevel);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 96f, 64f));
        drawing.DrawPolyline(
            fixedPen,
            [new(4f, 8f), new(28f, 10f), new(38f, 28f)],
            isClosed: true);
        drawing.DrawSpline(
            hairlinePen,
            [new(48f, 28f), new(62f, 6f), new(82f, 28f)],
            [0.0, 0.0, 0.0, 1.0, 1.0, 1.0],
            [1.0, 0.7071067811865476, 1.0],
            degree: 2,
            isClosed: false);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            8U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(2, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.StrokeCount);
        Assert.Equal(6, compiled.StrokePointCount);
        Assert.Equal(11, compiled.StrokeDoubleCount);
        Assert.Equal(2, compiled.BrushCount);
        Assert.Equal(2, compiled.GradientStopCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.StrokeBatch, resource.Kind);
        Assert.Equal(
            2 * Unsafe.SizeOf<NativeSceneStroke>(),
            checked((int)resource.PayloadSize));
        Assert.Equal(
            6 * Unsafe.SizeOf<Vector2>() + 11 * sizeof(double),
            checked((int)resource.AuxiliarySize));

        ReadOnlySpan<NativeSceneStroke> strokes =
            MemoryMarshal.Cast<byte, NativeSceneStroke>(
                compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset),
                    checked((int)resource.PayloadSize)));
        Assert.Equal(NativeSceneStrokeKind.Polyline, strokes[0].Kind);
        Assert.Equal(0UL, strokes[0].PointOffset);
        Assert.Equal(3UL, strokes[0].PointCount);
        Assert.Equal(0UL, strokes[0].DashIntervalOffset);
        Assert.Equal(2UL, strokes[0].DashIntervalCount);
        Assert.Equal(
            NativePolylineFlags.FixedDeviceStroke |
                NativePolylineFlags.Closed,
            strokes[0].Flags);
        Assert.Equal(NativeStrokeCap.Round, strokes[0].StartCap);
        Assert.Equal(NativeStrokeCap.Triangle, strokes[0].EndCap);
        Assert.Equal(NativeStrokeJoin.Round, strokes[0].LineJoin);
        Assert.Equal(NativeStrokeCap.Square, strokes[0].DashCap);
        Assert.Equal(NativeSceneStrokeKind.Spline, strokes[1].Kind);
        Assert.Equal(3UL, strokes[1].PointOffset);
        Assert.Equal(2UL, strokes[1].KnotOffset);
        Assert.Equal(6UL, strokes[1].KnotCount);
        Assert.Equal(8UL, strokes[1].WeightOffset);
        Assert.Equal(3UL, strokes[1].WeightCount);
        Assert.Equal(11UL, strokes[1].DashIntervalOffset);
        Assert.Equal(NativePolylineFlags.Hairline, strokes[1].Flags);
        Assert.Equal(2U, strokes[1].Degree);

        ReadOnlySpan<Vector2> points = MemoryMarshal.Cast<byte, Vector2>(
            compiled.Stream.Slice(
                checked((int)resource.AuxiliaryOffset),
                6 * Unsafe.SizeOf<Vector2>()));
        Assert.Equal(new Vector2(4f, 8f), points[0]);
        Assert.Equal(new Vector2(82f, 28f), points[5]);
        ReadOnlySpan<double> doubles = MemoryMarshal.Cast<byte, double>(
            compiled.Stream.Slice(
                checked((int)resource.AuxiliaryOffset) +
                    6 * Unsafe.SizeOf<Vector2>(),
                11 * sizeof(double)));
        Assert.True(doubles.SequenceEqual(new double[]
        {
            2.0, 1.0,
            0.0, 0.0, 0.0, 1.0, 1.0, 1.0,
            1.0, 0.7071067811865476, 1.0
        }));
    }

    [Fact]
    public void CompilerCoalescesRetainedPathFillsAndPreservesSegments()
    {
        var firstPath = new PathGeometry();
        var firstFigure = new PathFigure(new Vector2(4f, 5f), isClosed: true);
        firstFigure.Segments.Add(new LineSegment(new Vector2(28f, 5f)));
        firstFigure.Segments.Add(new QuadraticBezierSegment(
            new Vector2(36f, 18f),
            new Vector2(24f, 30f)));
        firstFigure.Segments.Add(new RationalQuadraticBezierSegment(
            new Vector2(20f, 34f),
            new Vector2(16f, 32f),
            0.75f));
        firstFigure.Segments.Add(new RationalCubicBezierSegment(
            new Vector2(14f, 34f),
            new Vector2(10f, 31f),
            new Vector2(8f, 28f),
            0.5f,
            1.5f));
        firstFigure.Segments.Add(new CubicBezierSegment(
            new Vector2(6f, 26f),
            new Vector2(4f, 22f),
            new Vector2(4f, 18f)));
        firstPath.Figures.Add(firstFigure);

        var secondPath = new PathGeometry { FillRule = FillRule.EvenOdd };
        var secondFigure = new PathFigure(new Vector2(44f, 21f), isClosed: true);
        secondFigure.Segments.Add(new LineSegment(new Vector2(58f, 8f)));
        secondFigure.Segments.Add(new ArcSegment(
            new Vector2(72f, 21f),
            new Vector2(14f, 14f),
            0f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        secondPath.Figures.Add(secondFigure);

        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 96f, 64f));
        drawing.DrawPath(
            new SolidColorBrush(new Vector4(0.8f, 0.2f, 0.1f, 1f)),
            null,
            firstPath,
            Matrix4x4.CreateTranslation(2f, 3f, 0f));
        drawing.DrawPath(
            new LinearGradientBrush(
                new Vector2(44f, 8f),
                new Vector2(72f, 34f),
                [
                    new GradientStop(Vector4.One, 0f),
                    new GradientStop(new Vector4(0f, 0f, 1f, 1f), 1f)
                ]),
            null,
            secondPath);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            95U,
            7U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(2, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.PathCount);
        Assert.Equal(9, compiled.PathSegmentCount);
        Assert.Equal(0, compiled.VertexMeshCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.PathBatch, resource.Kind);
        ReadOnlySpan<NativeScenePathFill> paths =
            MemoryMarshal.Cast<byte, NativeScenePathFill>(
                compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset),
                    checked((int)resource.PayloadSize)));
        Assert.Equal(2, paths.Length);
        Assert.Equal(0UL, paths[0].SegmentOffset);
        Assert.Equal(6UL, paths[0].SegmentCount);
        Assert.Equal(new Vector2(2f, 3f), new Vector2(
            paths[0].Transform.M31,
            paths[0].Transform.M32));
        Assert.Equal(6UL, paths[1].SegmentOffset);
        Assert.Equal(3UL, paths[1].SegmentCount);
        Assert.Equal(NativeFillRule.EvenOdd, paths[1].FillRule);
        Assert.Equal(4U, paths[1].SampleGrid);

        ReadOnlySpan<NativePathSegment> segments =
            MemoryMarshal.Cast<byte, NativePathSegment>(
                compiled.Stream.Slice(
                    checked((int)resource.AuxiliaryOffset),
                    checked((int)resource.AuxiliarySize)));
        Assert.Equal(NativePathSegmentKind.Line, segments[0].Kind);
        Assert.Equal(NativePathSegmentKind.Quadratic, segments[1].Kind);
        Assert.Equal(NativePathSegmentKind.RationalQuadratic, segments[2].Kind);
        Assert.Equal(0.75f, BitConverter.UInt32BitsToSingle(segments[2].Pad0));
        Assert.Equal(Vector2.Zero, segments[2].P3);
        Assert.Equal(NativePathSegmentKind.RationalCubic, segments[3].Kind);
        Assert.Equal(0.5f, BitConverter.UInt32BitsToSingle(segments[3].Pad0));
        Assert.Equal(1.5f, BitConverter.UInt32BitsToSingle(segments[3].Pad1));
        Assert.Equal(0U, segments[3].Pad2);
        Assert.Equal(NativePathSegmentKind.Cubic, segments[4].Kind);
        Assert.Equal(new Vector2(4f, 5f), segments[5].P1);
        Assert.Equal(NativePathSegmentKind.Arc, segments[7].Kind);
        Assert.True(segments[7].P3.X > 0f && segments[7].P3.Y > 0f);
    }

    [Fact]
    public void CompilerLowersLineOnlyPathStrokeAsExactGeometry()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(2f, 2f));
        figure.Segments.Add(new LineSegment(new Vector2(20f, 20f)));
        path.Figures.Add(figure);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 32f, 32f));
        drawing.DrawPath(
            null,
            new Pen(new SolidColorBrush(Vector4.One), 2f),
            path);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            8U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.GeometryPrimitiveCount);
        Assert.Equal(0, compiled.StrokeCount);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneResource resource =
            MemoryMarshal.Read<NativeMethods.SceneResource>(
                compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.GeometryBatch, resource.Kind);
        NativeGeometryPrimitive stroke = MemoryMarshal.Read<NativeGeometryPrimitive>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(NativeGeometryPrimitiveKind.Line, stroke.Kind);
        Assert.Equal(new Vector2(2f, 2f), stroke.P0);
        Assert.Equal(new Vector2(20f, 20f), stroke.P1);
        Assert.Equal(2f, stroke.StrokeThickness);
    }

    [Fact]
    public void CompilerLowersHatchExtensionsThroughSharedPathShader()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(2f, 2f), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(30f, 2f)));
        figure.Segments.Add(new LineSegment(new Vector2(30f, 20f)));
        figure.Segments.Add(new LineSegment(new Vector2(2f, 20f)));
        path.Figures.Add(figure);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 32f, 24f));
        drawing.DrawHatch(
            new HatchPatternBrush(
                0.35f,
                6f,
                1.5f,
                new Vector4(1f, 0.25f, 0.1f, 1f))
            {
                CoordinateTransform = Matrix4x4.CreateTranslation(3f, 5f, 0f),
            },
            path);
        drawing.DrawHatch(
            new CrossHatchBrush(
                0.7f,
                8f,
                2f,
                new Vector4(0.1f, 0.5f, 1f, 1f))
            {
                CoordinateTransform = Matrix4x4.CreateScale(2f, 3f, 1f),
            },
            path);
        drawing.DrawHatch(
            new HatchPatternSetBrush(
                [
                    new HatchPatternLineFamily(
                        new Vector2(1f, 2f),
                        Vector2.UnitX,
                        3f,
                        5f,
                        0,
                        6,
                        8.5f),
                    new HatchPatternLineFamily(
                        new Vector2(4f, 5f),
                        Vector2.UnitY,
                        -2f,
                        7f,
                        6,
                        0,
                        0f),
                ],
                [2f, -1f, 0f, -0.5f, 3f, -2f],
                0f,
                new Vector4(0.25f, 0.5f, 0.75f, 1f)),
            path);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            196U,
            3U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(3, compiled.PathCount);
        Assert.Equal(8, compiled.GradientStopCount);
        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        NativeMethods.SceneResource brushResource = resources.ToArray().Single(
            static resource =>
                resource.Kind == NativeSceneResourceKind.BrushTable);
        ReadOnlySpan<NativeSceneBrush> brushes =
            MemoryMarshal.Cast<byte, NativeSceneBrush>(
                compiled.Stream.Slice(
                    checked((int)brushResource.PayloadOffset),
                    checked((int)brushResource.PayloadSize)));
        Assert.Equal(NativeSceneBrushKind.HatchPattern, brushes[0].Kind);
        Assert.Equal(NativeSceneBrushKind.CrossHatch, brushes[1].Kind);
        Assert.Equal(NativeSceneBrushKind.HatchPatternSet, brushes[2].Kind);
        Assert.Equal(8U, brushes[2].StopCount);
        Assert.Equal((NativeSceneGradientSpread)2U, brushes[2].Spread);
        Assert.Equal(6f, brushes[0].Center.X);
        Assert.Equal(2f, brushes[1].Center.Y);
        Assert.Equal(3f, brushes[0].CoordinateTransform0.Z);
        Assert.Equal(5f, brushes[0].CoordinateTransform1.Z);
        Assert.Equal(2f, brushes[1].CoordinateTransform0.X);
        Assert.Equal(3f, brushes[1].CoordinateTransform1.Y);
        ReadOnlySpan<NativeSceneGradientStop> records =
            MemoryMarshal.Cast<byte, NativeSceneGradientStop>(
                compiled.Stream.Slice(
                    checked((int)brushResource.AuxiliaryOffset),
                    checked((int)brushResource.AuxiliarySize)));
        Assert.Equal(8, records.Length);
        Assert.Equal(new Vector4(1f, 2f, 1f, 0f), records[0].Color);
        Assert.Equal(5f, records[0].Offset);
        Assert.Equal(new Vector4(3f, 8.5f, 6f, 0f), records[1].Color);
        Assert.Equal(new Vector4(2f, -1f, 0f, -0.5f), records[2].Color);
        Assert.Equal(3f, records[2].Offset);
        Assert.Equal(-2f, records[3].Color.X);
    }

    [Fact]
    public void CompilerLowersCurvedPathStrokeWithExactSegmentsCapsAndJoins()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(2f, 2f));
        figure.Segments.Add(new QuadraticBezierSegment(
            new Vector2(10f, 2f),
            new Vector2(20f, 20f)));
        figure.Segments.Add(new CubicBezierSegment(
            new Vector2(24f, 28f),
            new Vector2(30f, 4f),
            new Vector2(34f, 12f)));
        figure.Segments.Add(new ArcSegment(
            new Vector2(48f, 20f),
            new Vector2(12f, 8f),
            18f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        path.Figures.Add(figure);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 64f, 40f));
        drawing.DrawPath(
            null,
            new Pen(
                new SolidColorBrush(Vector4.One),
                2f,
                PenLineJoin.Round,
                4f,
                PenLineCap.Round,
                PenLineCap.Square),
            path);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            8U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(7, compiled.GeometryPrimitiveCount);
        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneResource resource =
            MemoryMarshal.Read<NativeMethods.SceneResource>(
                compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.GeometryBatch, resource.Kind);
        ReadOnlySpan<NativeGeometryPrimitive> primitives =
            MemoryMarshal.Cast<byte, NativeGeometryPrimitive>(
                compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset),
                    checked((int)resource.PayloadSize)));
        Assert.Equal(NativeGeometryPrimitiveKind.PathCap, primitives[0].Kind);
        Assert.Equal(NativeStrokeCap.Round, primitives[0].StartCap);
        Assert.Equal(NativeGeometryPrimitiveKind.QuadraticBezier, primitives[1].Kind);
        Assert.Equal(NativeGeometryPrimitiveKind.PathJoin, primitives[2].Kind);
        Assert.Equal(NativeGeometryPrimitiveKind.CubicBezier, primitives[3].Kind);
        Assert.Equal(NativeGeometryPrimitiveKind.PathJoin, primitives[4].Kind);
        Assert.Equal(NativeGeometryPrimitiveKind.Arc, primitives[5].Kind);
        Assert.True(MathF.Abs(
            primitives[5].P1.X * primitives[5].P2.Y -
            primitives[5].P1.Y * primitives[5].P2.X) > 0.0001f);
        Assert.Equal(NativeGeometryPrimitiveKind.PathCap, primitives[6].Kind);
        Assert.Equal(NativeStrokeCap.Square, primitives[6].StartCap);
    }

    [Fact]
    public void CompilerMaterializesCurvedDashesOnceAndKeepsFixedDeviceStroke()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(3f, 4f));
        figure.Segments.Add(new QuadraticBezierSegment(
            new Vector2(20f, 32f),
            new Vector2(42f, 8f)));
        path.Figures.Add(figure);
        var pen = new Pen(
            new SolidColorBrush(Vector4.One),
            3f,
            PenLineJoin.Bevel,
            4f,
            PenLineCap.Round,
            PenLineCap.Triangle,
            PenLineCap.Square,
            [2.0, 1.0],
            0.5,
            PenStrokeTransformMode.Fixed);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 128f, 64f));
        drawing.DrawPath(
            null,
            pen,
            path,
            Matrix4x4.CreateScale(2.5f, 0.75f, 1f));
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            128U,
            9U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.True(compiled.GeometryPrimitiveCount > 2);
        Assert.Equal(0, compiled.StrokeCount);
        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneResource resource =
            MemoryMarshal.Read<NativeMethods.SceneResource>(
                compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        ReadOnlySpan<NativeGeometryPrimitive> primitives =
            MemoryMarshal.Cast<byte, NativeGeometryPrimitive>(
                compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset),
                    checked((int)resource.PayloadSize)));
        Assert.All(primitives.ToArray(), primitive =>
            Assert.True(primitive.Flags.HasFlag(
                NativeGeometryPrimitiveFlags.FixedDeviceStroke)));
        Assert.Contains(primitives.ToArray(), primitive =>
            primitive.Kind == NativeGeometryPrimitiveKind.QuadraticBezier);
    }

    [Fact]
    public void CompilerLowersNestedOpacityAndAxisAlignedClipScopes()
    {
        var red = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f));
        using var picture = new GpuPicture(
            new RenderCommand[]
            {
                new()
                {
                    Type = RenderCommandType.PushOpacity,
                    FontSize = 0.5f
                },
                Rectangle(red, 0f),
                new()
                {
                    Type = RenderCommandType.PushClip,
                    Rect = new Rect(4f, 5f, 20f, 10f),
                    Transform = Matrix4x4.CreateScale(2f, 3f, 1f) *
                        Matrix4x4.CreateTranslation(7f, 11f, 0f)
                },
                Rectangle(red, 12f),
                new() { Type = RenderCommandType.PopClip },
                Rectangle(red, 24f),
                new() { Type = RenderCommandType.PopOpacity },
                Rectangle(red, 36f)
            },
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            91U,
            3U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(8, compiled.SourceCommandCount);
        Assert.Equal(8, compiled.NativeCommandCount);
        Assert.Equal(4, compiled.NativeDrawCount);
        Assert.Equal(4, compiled.AnalyticPrimitiveCount);
        Assert.Equal(1, compiled.BrushCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        Assert.Equal(8U, header.CommandCount);
        Assert.Equal(7U, header.ResourceCount);
        ReadOnlySpan<NativeMethods.SceneCommand> commands =
            MemoryMarshal.Cast<byte, NativeMethods.SceneCommand>(
                compiled.Stream.Slice(
                    checked((int)header.CommandOffset),
                    checked((int)header.CommandCount *
                        Unsafe.SizeOf<NativeMethods.SceneCommand>())));
        Assert.Equal(
            [
                NativeSceneCommandKind.Save,
                NativeSceneCommandKind.DrawAnalytic,
                NativeSceneCommandKind.Save,
                NativeSceneCommandKind.DrawAnalytic,
                NativeSceneCommandKind.Restore,
                NativeSceneCommandKind.DrawAnalytic,
                NativeSceneCommandKind.Restore,
                NativeSceneCommandKind.DrawAnalytic
            ],
            commands.ToArray().Select(static command => command.Kind));
        Assert.Equal(5U, commands[0].StateIndex);
        Assert.Equal(6U, commands[2].StateIndex);

        int resourcesStart = checked((int)header.ResourceOffset);
        int resourceSize = Unsafe.SizeOf<NativeMethods.SceneResource>();
        var opacityResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(resourcesStart + 5 * resourceSize));
        var clipResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(resourcesStart + 6 * resourceSize));
        Assert.Equal(NativeSceneResourceKind.State, opacityResource.Kind);
        Assert.Equal(NativeSceneResourceKind.State, clipResource.Kind);
        var opacityState = MemoryMarshal.Read<NativeSceneState>(
            compiled.Stream.Slice(checked((int)opacityResource.PayloadOffset)));
        var clipState = MemoryMarshal.Read<NativeSceneState>(
            compiled.Stream.Slice(checked((int)clipResource.PayloadOffset)));
        Assert.Equal(0.5f, opacityState.Opacity);
        Assert.Equal(NativeSceneStateFlags.None, opacityState.Flags);
        Assert.Equal(0.5f, clipState.Opacity);
        Assert.Equal(NativeSceneStateFlags.ClipRect, clipState.Flags);
        Assert.Equal(15f, clipState.ClipRect.X);
        Assert.Equal(26f, clipState.ClipRect.Y);
        Assert.Equal(40f, clipState.ClipRect.Width);
        Assert.Equal(30f, clipState.ClipRect.Height);

        static RenderCommand Rectangle(Brush brush, float x) => new()
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(x, 0f, 10f, 10f),
            Brush = brush,
            Transform = Matrix4x4.Identity
        };
    }

    [Theory]
    [InlineData(RenderCommandType.PopOpacity)]
    [InlineData(RenderCommandType.PopClip)]
    [InlineData(RenderCommandType.PopOpacityMask)]
    [InlineData(RenderCommandType.PopGeometryClip)]
    public void CompilerFailsClosedForUnbalancedStatePop(RenderCommandType pop)
    {
        using var picture = new GpuPicture(
            [new RenderCommand { Type = pop }],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnbalancedState, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(pop, failure.CommandType);
    }

    [Theory]
    [InlineData(RenderCommandType.PushOpacity)]
    [InlineData(RenderCommandType.PushClip)]
    [InlineData(RenderCommandType.PushOpacityMask)]
    public void CompilerFailsClosedForUnterminatedStatePush(RenderCommandType push)
    {
        RenderCommand command = push switch
        {
            RenderCommandType.PushOpacity => new RenderCommand
            {
                Type = push,
                FontSize = 0.5f
            },
            RenderCommandType.PushClip => new RenderCommand
            {
                Type = push,
                Rect = new Rect(0f, 0f, 10f, 10f),
                Transform = Matrix4x4.Identity
            },
            _ => new RenderCommand
            {
                Type = push,
                Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.5f)),
                Rect = new Rect(0f, 0f, 10f, 10f),
                Transform = Matrix4x4.Identity
            }
        };
        using var picture = new GpuPicture(
            [command],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnbalancedState, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(push, failure.CommandType);
    }

    [Fact]
    public void CompilerLowersAxisAlignedSolidOpacityMaskToNativeState()
    {
        Matrix4x4 transform = Matrix4x4.CreateScale(1.5f, 0.75f, 1f) *
            Matrix4x4.CreateTranslation(11f, 7f, 0f);
        var mask = new SolidColorBrush(new Vector4(0.2f, 0.4f, 0.8f, 0.5f))
        {
            Opacity = 0.6f
        };
        var fill = new SolidColorBrush(new Vector4(1f, 0.2f, 0.1f, 1f));
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = mask,
                    Rect = new Rect(4f, 6f, 80f, 50f),
                    Transform = transform
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = fill,
                    Rect = new Rect(0f, 0f, 100f, 70f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            90U,
            7U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(3, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        Assert.Equal(3U, header.CommandCount);
        Assert.Equal(3U, header.ResourceCount);
        ReadOnlySpan<NativeMethods.SceneCommand> commands =
            MemoryMarshal.Cast<byte, NativeMethods.SceneCommand>(
                compiled.Stream.Slice(
                    checked((int)header.CommandOffset),
                    checked((int)header.CommandCount *
                        Unsafe.SizeOf<NativeMethods.SceneCommand>())));
        Assert.Equal(NativeSceneCommandKind.Save, commands[0].Kind);
        Assert.Equal(NativeSceneCommandKind.DrawAnalytic, commands[1].Kind);
        Assert.Equal(NativeSceneCommandKind.Restore, commands[2].Kind);
        Assert.Equal(2U, commands[0].StateIndex);

        int resourceOffset = checked((int)header.ResourceOffset +
            2 * Unsafe.SizeOf<NativeMethods.SceneResource>());
        var stateResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(resourceOffset));
        Assert.Equal(NativeSceneResourceKind.State, stateResource.Kind);
        var state = MemoryMarshal.Read<NativeSceneState>(
            compiled.Stream.Slice(checked((int)stateResource.PayloadOffset)));
        Assert.Equal(0.3f, state.Opacity, 5);
        Assert.Equal(NativeSceneStateFlags.ClipRect, state.Flags);
        Assert.Equal(17f, state.ClipRect.X, 5);
        Assert.Equal(11.5f, state.ClipRect.Y, 5);
        Assert.Equal(120f, state.ClipRect.Width, 5);
        Assert.Equal(37.5f, state.ClipRect.Height, 5);
    }

    [Fact]
    public void CompilerLowersAffineRoundedGeometryClipToPerDrawMaskState()
    {
        PathGeometry clip = PrimitivePathGeometry.CreateRoundedRectangle(
            3f,
            4f,
            40f,
            24f,
            7f,
            5f);
        Matrix4x4 clipTransform =
            Matrix4x4.CreateScale(1.25f, 0.8f, 1f) *
            Matrix4x4.CreateRotationZ(0.18f) *
            Matrix4x4.CreateTranslation(9f, 6f, 0f);
        var cyan = new SolidColorBrush(
            new Vector4(0f, 0.8f, 1f, 0.65f));
        var magenta = new SolidColorBrush(
            new Vector4(1f, 0.2f, 0.55f, 0.65f));
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = clip,
                    Transform = clipTransform
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = cyan,
                    Rect = new Rect(0f, 0f, 30f, 24f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = magenta,
                    Rect = new Rect(16f, 3f, 30f, 24f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            92U,
            3U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(3, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        Assert.Equal(4U, header.ResourceCount);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        Assert.Equal(NativeSceneResourceKind.LayerMask, resources[2].Kind);
        Assert.Equal(NativeSceneResourceKind.State, resources[3].Kind);
        var mask = MemoryMarshal.Read<NativeSceneLayerMask>(
            compiled.Stream.Slice(
                checked((int)resources[2].PayloadOffset)));
        var state = MemoryMarshal.Read<NativeSceneState>(
            compiled.Stream.Slice(
                checked((int)resources[3].PayloadOffset)));
        Assert.Equal(NativeSceneLayerMaskKind.RoundedRectangle, mask.Kind);
        Assert.Equal(3f, mask.Bounds.X);
        Assert.Equal(4f, mask.Bounds.Y);
        Assert.Equal(40f, mask.Bounds.Width);
        Assert.Equal(24f, mask.Bounds.Height);
        Assert.Equal(new Vector4(7f), mask.CornerRadiiX);
        Assert.Equal(new Vector4(5f), mask.CornerRadiiY);
        Assert.Equal(
            new Matrix3x2(
                clipTransform.M11,
                clipTransform.M12,
                clipTransform.M21,
                clipTransform.M22,
                clipTransform.M41,
                clipTransform.M42),
            mask.Transform);
        Assert.Equal(NativeSceneStateFlags.Mask, state.Flags);
        Assert.Equal(2U, state.MaskResourceIndex);

        ReadOnlySpan<NativeMethods.SceneCommand> commands =
            MemoryMarshal.Cast<byte, NativeMethods.SceneCommand>(
                compiled.Stream.Slice(
                    checked((int)header.CommandOffset),
                    checked((int)header.CommandCount *
                        Unsafe.SizeOf<NativeMethods.SceneCommand>())));
        Assert.Equal(NativeSceneCommandKind.Save, commands[0].Kind);
        Assert.Equal(3U, commands[0].StateIndex);
        Assert.Equal(NativeSceneCommandKind.DrawAnalytic, commands[1].Kind);
        Assert.Equal(NativeSceneCommandKind.Restore, commands[2].Kind);
    }

    [Fact]
    public void CompilerLowersNestedGeometryMasksToBoundedAnalyticChain()
    {
        PathGeometry outer = PrimitivePathGeometry.CreateRoundedRectangle(
            0f, 0f, 40f, 30f, 5f, 5f);
        PathGeometry inner = PrimitivePathGeometry.CreateRectangle(
            4f, 4f, 20f, 12f);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = outer,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = inner,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 40f, 30f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        Assert.Equal(6U, header.ResourceCount);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        Assert.Equal(NativeSceneResourceKind.LayerMask, resources[3].Kind);
        var chain = MemoryMarshal.Read<NativeSceneLayerMaskChain>(
            compiled.Stream.Slice(checked((int)resources[3].PayloadOffset)));
        Assert.Equal(NativeSceneLayerMaskKind.AnalyticChain, chain.Kind);
        Assert.Equal(2U, chain.MaskCount);
        Assert.Equal(40f, chain.Mask0.Bounds.Width);
        Assert.Equal(20f, chain.Mask1.Bounds.Width);
        Assert.Equal(default, chain.Mask2);
        Assert.Equal(default, chain.Mask3);
    }

    [Fact]
    public void CompilerPromotesFifthNestedGeometryMaskToGpuVectorChain()
    {
        PathGeometry mask = PrimitivePathGeometry.CreateRectangle(
            0f, 0f, 40f, 30f);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = mask,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = mask,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = mask,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = mask,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = mask,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 40f, 30f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        bool success = GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure);
        Assert.True(success, failure.ToString());
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        NativeMethods.SceneResource vectorResource = default;
        for (int index = 0; index < resources.Length; index++)
        {
            NativeMethods.SceneResource resource = resources[index];
            if (resource.Kind == NativeSceneResourceKind.LayerMask &&
                MemoryMarshal.Read<NativeSceneLayerVectorMask>(
                    compiled.Stream.Slice(
                        checked((int)resource.PayloadOffset))).Kind ==
                    NativeSceneLayerMaskKind.VectorClipChain)
            {
                vectorResource = resource;
                break;
            }
        }
        Assert.NotEqual(0U, vectorResource.PayloadOffset);
        var vectorMask = MemoryMarshal.Read<NativeSceneLayerVectorMask>(
            compiled.Stream.Slice(
                checked((int)vectorResource.PayloadOffset)));
        Assert.Equal(5U, vectorMask.PathCount);
        Assert.True(vectorMask.SegmentCount >= 20U);
        Assert.True(vectorResource.AuxiliarySize > 0U);
    }

    [Fact]
    public void CompilerLowersGeneralCurveGeometryClipToGpuVectorMask()
    {
        var clip = new PathGeometry { FillRule = FillRule.EvenOdd };
        var figure = new PathFigure(new Vector2(4f, 6f), isClosed: true);
        figure.Segments.Add(new QuadraticBezierSegment(
            new Vector2(32f, -2f),
            new Vector2(48f, 18f)));
        figure.Segments.Add(new CubicBezierSegment(
            new Vector2(52f, 36f),
            new Vector2(18f, 42f),
            new Vector2(4f, 6f)));
        clip.Figures.Add(figure);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = clip,
                    Transform = new Matrix4x4(
                        1f, -0.08f, 0f, 0f,
                        0.12f, 1f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        0f, 0f, 0f, 1f) *
                        Matrix4x4.CreateTranslation(7f, 5f, 0f)
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 64f, 48f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            14U,
            3U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure), failure.ToString());
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        NativeMethods.SceneResource maskResource = default;
        for (int index = 0; index < resources.Length; index++)
        {
            if (resources[index].Kind == NativeSceneResourceKind.LayerMask)
            {
                maskResource = resources[index];
                break;
            }
        }
        Assert.NotEqual(0U, maskResource.PayloadOffset);
        var mask = MemoryMarshal.Read<NativeSceneLayerVectorMask>(
            compiled.Stream.Slice(
                checked((int)maskResource.PayloadOffset)));
        Assert.Equal(NativeSceneLayerMaskKind.VectorClipChain, mask.Kind);
        Assert.Equal(1U, mask.PathCount);
        ReadOnlySpan<NativeSceneClipPath> paths = MemoryMarshal.Cast<
            byte,
            NativeSceneClipPath>(compiled.Stream.Slice(
                checked((int)maskResource.AuxiliaryOffset),
                Unsafe.SizeOf<NativeSceneClipPath>()));
        Assert.Equal(NativeFillRule.EvenOdd, paths[0].FillRule);
        Assert.Equal(NativeClipOperation.Intersect, paths[0].Operation);
        Assert.Equal(4U, paths[0].SampleGrid);
    }

    [Fact]
    public void CompilerLowersCombinedPathFillToGpuBooleanProgram()
    {
        var path = new PathGeometry
        {
            IsCombined = true,
            Op = 0,
            PathA = PrimitivePathGeometry.CreateRectangle(4f, 4f, 52f, 40f),
            PathB = PrimitivePathGeometry.CreateRectangle(20f, 12f, 24f, 22f)
        };
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPath,
                    Path = path,
                    Brush = new SolidColorBrush(Vector4.One),
                    Transform = Matrix4x4.Identity
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            31U,
            2U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.PathCount);
        Assert.Equal(8, compiled.PathSegmentCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.PathBatch, resource.Kind);
        var fill = MemoryMarshal.Read<NativeScenePathFill>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(0UL, fill.SegmentOffset);
        Assert.Equal(8UL, fill.SegmentCount);
        Assert.Equal(0UL, fill.BooleanNodeOffset);
        Assert.Equal(3UL, fill.BooleanNodeCount);
        Assert.Equal(
            checked((uint)(
                8 * Unsafe.SizeOf<NativePathSegment>() +
                3 * Unsafe.SizeOf<NativeScenePathBooleanNode>())),
            resource.AuxiliarySize);
        ReadOnlySpan<NativeScenePathBooleanNode> nodes = MemoryMarshal.Cast<
            byte,
            NativeScenePathBooleanNode>(compiled.Stream.Slice(
                checked((int)resource.AuxiliaryOffset) +
                    8 * Unsafe.SizeOf<NativePathSegment>(),
                3 * Unsafe.SizeOf<NativeScenePathBooleanNode>()));
        Assert.Equal(NativePathBooleanNodeKind.Leaf, nodes[0].Kind);
        Assert.Equal(0UL, nodes[0].SegmentOffset);
        Assert.Equal(NativePathBooleanNodeKind.Leaf, nodes[1].Kind);
        Assert.Equal(4UL, nodes[1].SegmentOffset);
        Assert.Equal(NativePathBooleanNodeKind.Difference, nodes[2].Kind);
    }

    [Fact]
    public void CompilerDropsProvablyEmptyPathDraw()
    {
        PathGeometry path = PathOpGeometrySolver.Combine(
            new PathGeometry(),
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 8f, 8f),
            op: 0);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPath,
                    Path = path,
                    Brush = new SolidColorBrush(Vector4.One),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(12f, 12f, 4f, 4f),
                    Brush = new SolidColorBrush(Vector4.One),
                    Transform = Matrix4x4.Identity
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            32U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure), failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(0, compiled.PathCount);
        Assert.Equal(0, compiled.PathSegmentCount);
    }

    [Fact]
    public void CompilerLowersCombinedGeometryClipToGpuBooleanProgram()
    {
        var clip = new PathGeometry
        {
            IsCombined = true,
            Op = 0,
            PathA = PrimitivePathGeometry.CreateRectangle(4f, 4f, 52f, 40f),
            PathB = PrimitivePathGeometry.CreateRectangle(20f, 12f, 24f, 22f)
        };
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = clip,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 64f, 48f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            15U,
            4U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure), failure.ToString());
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        NativeMethods.SceneResource maskResource = default;
        foreach (NativeMethods.SceneResource resource in resources)
        {
            if (resource.Kind == NativeSceneResourceKind.LayerMask)
            {
                maskResource = resource;
                break;
            }
        }
        Assert.NotEqual(0U, maskResource.PayloadOffset);
        var mask = MemoryMarshal.Read<NativeSceneLayerVectorMask>(
            compiled.Stream.Slice(checked((int)maskResource.PayloadOffset)));
        Assert.Equal(1U, mask.PathCount);
        Assert.Equal(3U, mask.BooleanNodeCount);
        int pathBytes = Unsafe.SizeOf<NativeSceneClipPath>();
        int segmentBytes = checked(
            (int)mask.SegmentCount * Unsafe.SizeOf<NativePathSegment>());
        var path = MemoryMarshal.Read<NativeSceneClipPath>(
            compiled.Stream.Slice(checked((int)maskResource.AuxiliaryOffset)));
        Assert.Equal(3UL, path.BooleanNodeCount);
        ReadOnlySpan<NativeScenePathBooleanNode> nodes = MemoryMarshal.Cast<
            byte,
            NativeScenePathBooleanNode>(compiled.Stream.Slice(
                checked((int)maskResource.AuxiliaryOffset + pathBytes +
                    segmentBytes),
                3 * Unsafe.SizeOf<NativeScenePathBooleanNode>()));
        Assert.Equal(NativePathBooleanNodeKind.Leaf, nodes[0].Kind);
        Assert.Equal(NativePathBooleanNodeKind.Leaf, nodes[1].Kind);
        Assert.Equal(NativePathBooleanNodeKind.Difference, nodes[2].Kind);
    }

    [Fact]
    public void CompilerLowersProvablyEmptyCombinedClipToEmptyScissor()
    {
        PathGeometry clip = PathOpGeometrySolver.Combine(
            new PathGeometry(),
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 8f, 8f),
            op: 0);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = clip,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 32f, 32f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            16U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure), failure.ToString());
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount *
                        Unsafe.SizeOf<NativeMethods.SceneResource>())));
        NativeSceneState state = default;
        int layerMaskCount = 0;
        foreach (NativeMethods.SceneResource resource in resources)
        {
            if (resource.Kind == NativeSceneResourceKind.State)
            {
                state = MemoryMarshal.Read<NativeSceneState>(
                    compiled.Stream.Slice(
                        checked((int)resource.PayloadOffset)));
            }
            else if (resource.Kind == NativeSceneResourceKind.LayerMask)
            {
                layerMaskCount++;
            }
        }
        Assert.Equal(0, layerMaskCount);
        Assert.Equal(NativeSceneStateFlags.ClipRect, state.Flags);
        Assert.Equal(default, state.ClipRect);
    }

    [Fact]
    public void CompilerRejectsCyclicCombinedGeometryWithoutRecursing()
    {
        var clip = new PathGeometry { IsCombined = true, Op = 2 };
        clip.PathA = clip;
        clip.PathB = PrimitivePathGeometry.CreateRectangle(0f, 0f, 8f, 8f);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = clip,
                    Transform = Matrix4x4.Identity
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            17U,
            1U,
            out _,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileError.InvalidGeometry, failure.Error);
    }

    [Fact]
    public void CompilerLowersGradientOpacityMaskToGpuGeneratedNativeMask()
    {
        var gradient = new LinearGradientBrush(
            Vector2.Zero,
            Vector2.One,
            [
                new GradientStop(Vector4.One, 0f),
                new GradientStop(Vector4.Zero, 1f)
            ]);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = gradient,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        bool success = GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure);
        Assert.True(success, failure.ToString());
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount) *
                        Unsafe.SizeOf<NativeMethods.SceneResource>()));
        NativeMethods.SceneResource resource = Assert.Single(
            resources.ToArray(),
            static item =>
                item.Kind == NativeSceneResourceKind.LayerMask &&
                item.PayloadSize == Unsafe.SizeOf<NativeSceneLayerBrushMask>());
        var stored = MemoryMarshal.Read<NativeSceneLayerBrushMask>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(NativeSceneLayerMaskKind.Brush, stored.Kind);
        Assert.Equal(2U, stored.GradientStopCount);
        Assert.Equal(NativeSceneBrushKind.LinearGradient, stored.Brush.Kind);
        Assert.Equal(2U, stored.Brush.StopCount);
        Assert.Equal(0U, stored.Brush.StopOffset);
        ReadOnlySpan<NativeSceneGradientStop> stops = MemoryMarshal.Cast<
            byte,
            NativeSceneGradientStop>(compiled.Stream.Slice(
                checked((int)resource.AuxiliaryOffset),
                checked((int)resource.AuxiliarySize)));
        Assert.Equal(2, stops.Length);
        Assert.Equal(0f, stops[0].Offset);
        Assert.Equal(1f, stops[1].Offset);
    }

    [Fact]
    public void CompilerLowersRotatedSolidOpacityMaskToGpuGeneratedNativeMask()
    {
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Transform = Matrix4x4.CreateRotationZ(0.2f)
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        bool success = GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure);
        Assert.True(success, failure.ToString());
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount) *
                        Unsafe.SizeOf<NativeMethods.SceneResource>()));
        Assert.Contains(resources.ToArray(), static item =>
            item.Kind == NativeSceneResourceKind.LayerMask &&
            item.PayloadSize == Unsafe.SizeOf<NativeSceneLayerBrushMask>());
    }

    [Fact]
    public void CompilerLowersNestedBrushAndGeometryMasksToGpuCompositeMask()
    {
        var first = new LinearGradientBrush(
            Vector2.Zero,
            new Vector2(20f, 0f),
            [
                new GradientStop(Vector4.One, 0f),
                new GradientStop(new Vector4(1f, 1f, 1f, 0.25f), 1f)
            ]);
        var second = new LinearGradientBrush(
            Vector2.Zero,
            new Vector2(0f, 20f),
            [
                new GradientStop(new Vector4(1f, 1f, 1f, 0.5f), 0f),
                new GradientStop(Vector4.One, 1f)
            ]);
        PathGeometry geometry = PrimitivePathGeometry.CreateRectangle(
            2f,
            3f,
            16f,
            14f);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = first,
                    Rect = new Rect(0f, 0f, 20f, 20f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = geometry,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = second,
                    Rect = new Rect(0f, 0f, 20f, 20f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 20f, 20f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        bool success = GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            23U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure);
        Assert.True(success, failure.ToString());
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount) *
                        Unsafe.SizeOf<NativeMethods.SceneResource>()));
        NativeMethods.SceneResource[] compositeResources = resources.ToArray()
            .Where(static item =>
                item.Kind == NativeSceneResourceKind.LayerMask &&
                item.PayloadSize ==
                    Unsafe.SizeOf<NativeSceneLayerCompositeMask>())
            .ToArray();
        Assert.Equal(2, compositeResources.Length);
        NativeMethods.SceneResource resource = compositeResources[^1];
        var stored = MemoryMarshal.Read<NativeSceneLayerCompositeMask>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(NativeSceneLayerMaskKind.Composite, stored.Kind);
        Assert.Equal(3U, stored.ComponentCount);
        Assert.Equal(2U, stored.BrushMaskCount);
        Assert.Equal(1U, stored.PathCount);
        Assert.Equal(4U, stored.GradientStopCount);

        int brushBytes = checked(
            (int)stored.BrushMaskCount *
                Unsafe.SizeOf<NativeSceneLayerBrushMask>());
        ReadOnlySpan<NativeSceneLayerBrushMask> brushes = MemoryMarshal.Cast<
            byte,
            NativeSceneLayerBrushMask>(compiled.Stream.Slice(
                checked((int)resource.AuxiliaryOffset),
                brushBytes));
        Assert.Equal(0U, brushes[0].Brush.StopOffset);
        Assert.Equal(2U, brushes[1].Brush.StopOffset);
        Assert.Equal(2U, brushes[0].GradientStopCount);
        Assert.Equal(2U, brushes[1].GradientStopCount);
    }

    [Fact]
    public void CompilerFailsClosedForNonFiniteSolidOpacityMask()
    {
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = new SolidColorBrush(
                        new Vector4(1f, 1f, 1f, float.NaN)),
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.InvalidState, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.PushOpacityMask, failure.CommandType);
    }

    [Fact]
    public void CompilerLowersPictureOpacityMaskToNestedNativeScene()
    {
        using var maskPicture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(1f, 2f, 6f, 8f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(12f, 2f, 6f, 8f),
                    Transform = Matrix4x4.Identity
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());
        Matrix4x4 transform = Matrix4x4.CreateScale(2f, 3f, 1f) *
            Matrix4x4.CreateTranslation(5f, 7f, 0f);
        var bounds = new Rect(0f, 0f, 20f, 12f);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Picture = maskPicture,
                    Rect = bounds,
                    Transform = transform
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = bounds,
                    Transform = transform
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            101U,
            7U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure), failure.ToString());
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount) *
                        Unsafe.SizeOf<NativeMethods.SceneResource>()));
        NativeMethods.SceneResource resource = resources.ToArray().Single(
            static item =>
                item.Kind == NativeSceneResourceKind.LayerMask &&
                item.PayloadSize ==
                    Unsafe.SizeOf<NativeSceneLayerPictureMask>());
        var stored = MemoryMarshal.Read<NativeSceneLayerPictureMask>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(NativeSceneLayerMaskKind.Picture, stored.Kind);
        Assert.Equal(0U, stored.StreamOffset);
        Assert.Equal(resource.AuxiliarySize, stored.StreamSize);
        Assert.Equal(
            new NativeImageRect(
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height),
            stored.Bounds);
        Assert.Equal(
            new Matrix3x2(
                transform.M11,
                transform.M12,
                transform.M21,
                transform.M22,
                transform.M41,
                transform.M42),
            stored.Transform);
        var nestedHeader = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream.Slice(checked((int)resource.AuxiliaryOffset)));
        Assert.Equal(resource.AuxiliarySize, nestedHeader.TotalSize);
        Assert.NotEqual(header.SceneId, nestedHeader.SceneId);
        Assert.Equal(header.Generation, nestedHeader.Generation);
        Assert.Equal(1U, nestedHeader.CommandCount);
    }

    [Fact]
    public void CompilerComposesBrushAndPictureOpacityMasks()
    {
        using var maskPicture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(2f, 2f, 8f, 8f),
                    Transform = Matrix4x4.Identity
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());
        var gradient = new LinearGradientBrush(
            Vector2.Zero,
            new Vector2(20f, 0f),
            [
                new GradientStop(Vector4.One, 0f),
                new GradientStop(new Vector4(1f, 1f, 1f, 0.25f), 1f)
            ]);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = gradient,
                    Rect = new Rect(0f, 0f, 20f, 12f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Picture = maskPicture,
                    Rect = new Rect(0f, 0f, 20f, 12f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 20f, 12f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            102U,
            8U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure), failure.ToString());
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount) *
                        Unsafe.SizeOf<NativeMethods.SceneResource>()));
        NativeMethods.SceneResource resource = resources.ToArray().Last(
            static item =>
                item.Kind == NativeSceneResourceKind.LayerMask &&
                item.PayloadSize ==
                    Unsafe.SizeOf<NativeSceneLayerCompositeMask>());
        var stored = MemoryMarshal.Read<NativeSceneLayerCompositeMask>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(2U, stored.ComponentCount);
        Assert.Equal(1U, stored.BrushMaskCount);
        Assert.Equal(1U, stored.PictureMaskCount);
        Assert.True(stored.PictureStreamBytes >=
            Unsafe.SizeOf<NativeMethods.SceneHeader>());
        int pictureOffset = checked((int)resource.AuxiliaryOffset) +
            Unsafe.SizeOf<NativeSceneLayerBrushMask>();
        var pictureMask = MemoryMarshal.Read<NativeSceneLayerPictureMask>(
            compiled.Stream.Slice(pictureOffset));
        Assert.Equal(0U, pictureMask.StreamOffset);
        Assert.Equal(stored.PictureStreamBytes, pictureMask.StreamSize);
        int streamOffset = pictureOffset +
            Unsafe.SizeOf<NativeSceneLayerPictureMask>();
        var nestedHeader = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream.Slice(streamOffset));
        Assert.Equal(stored.PictureStreamBytes, nestedHeader.TotalSize);
    }

    [Fact]
    public void CompilerLowersStrokedPathOpacityMaskToGpuGeometryMask()
    {
        var gradient = new LinearGradientBrush(
            Vector2.Zero,
            new Vector2(20f, 0f),
            [
                new GradientStop(Vector4.One, 0f),
                new GradientStop(new Vector4(1f, 1f, 1f, 0.25f), 1f)
            ]);
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(2f, 10f), false);
        figure.Segments.Add(new LineSegment(new Vector2(18f, 10f)));
        path.Figures.Add(figure);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Path = path,
                    Pen = new Pen(gradient, 4f),
                    Rect = new Rect(0f, 0f, 20f, 20f),
                    Transform = Matrix4x4.Identity,
                    IsPenThicknessLocal = true,
                    GeometryCache = RenderCommandGeometryCache.ForStrokePath(path)
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 20f, 20f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        bool success = GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            24U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure);
        Assert.True(success, failure.ToString());
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount) *
                        Unsafe.SizeOf<NativeMethods.SceneResource>()));
        NativeMethods.SceneResource resource = resources.ToArray().Single(
            static item =>
                item.Kind == NativeSceneResourceKind.LayerMask &&
                item.PayloadSize ==
                    Unsafe.SizeOf<NativeSceneLayerGeometryMask>());
        var stored = MemoryMarshal.Read<NativeSceneLayerGeometryMask>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(NativeSceneLayerMaskKind.Geometry, stored.Kind);
        Assert.Equal(1U, stored.PrimitiveCount);
        Assert.Equal(2U, stored.GradientStopCount);
        Assert.Equal(0U, stored.Brush.StopOffset);
        var primitive = MemoryMarshal.Read<NativeGeometryPrimitive>(
            compiled.Stream.Slice(checked((int)resource.AuxiliaryOffset)));
        Assert.Equal(NativeGeometryPrimitiveKind.Line, primitive.Kind);
        Assert.Equal(4f, primitive.StrokeThickness);
    }

    [Fact]
    public void CompilerComposesBrushVectorAndStrokedPathOpacityMasks()
    {
        var horizontal = new LinearGradientBrush(
            Vector2.Zero,
            new Vector2(20f, 0f),
            [
                new GradientStop(Vector4.One, 0f),
                new GradientStop(new Vector4(1f, 1f, 1f, 0.25f), 1f)
            ]);
        var vertical = new LinearGradientBrush(
            Vector2.Zero,
            new Vector2(0f, 20f),
            [
                new GradientStop(new Vector4(1f, 1f, 1f, 0.5f), 0f),
                new GradientStop(Vector4.One, 1f)
            ]);
        PathGeometry clip = PrimitivePathGeometry.CreateRectangle(
            1f,
            2f,
            18f,
            16f);
        var stroke = new PathGeometry();
        var figure = new PathFigure(new Vector2(2f, 10f), false);
        figure.Segments.Add(new LineSegment(new Vector2(18f, 10f)));
        stroke.Figures.Add(figure);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = horizontal,
                    Rect = new Rect(0f, 0f, 20f, 20f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = clip,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Path = stroke,
                    Pen = new Pen(vertical, 4f),
                    Rect = new Rect(0f, 0f, 20f, 20f),
                    Transform = Matrix4x4.Identity,
                    IsPenThicknessLocal = true,
                    GeometryCache = RenderCommandGeometryCache.ForStrokePath(stroke)
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 20f, 20f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask },
                new RenderCommand { Type = RenderCommandType.PopGeometryClip },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        bool success = GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            25U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure);
        Assert.True(success, failure.ToString());
        Assert.NotNull(compiled);
        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)header.ResourceCount) *
                        Unsafe.SizeOf<NativeMethods.SceneResource>()));
        NativeMethods.SceneResource resource = resources.ToArray().Last(
            static item =>
                item.Kind == NativeSceneResourceKind.LayerMask &&
                item.PayloadSize ==
                    Unsafe.SizeOf<NativeSceneLayerCompositeMask>());
        var stored = MemoryMarshal.Read<NativeSceneLayerCompositeMask>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(3U, stored.ComponentCount);
        Assert.Equal(1U, stored.BrushMaskCount);
        Assert.Equal(1U, stored.GeometryMaskCount);
        Assert.Equal(1U, stored.GeometryPrimitiveCount);
        Assert.Equal(1U, stored.PathCount);
        Assert.Equal(4U, stored.GradientStopCount);

        int auxiliaryOffset = checked((int)resource.AuxiliaryOffset);
        var brushMask = MemoryMarshal.Read<NativeSceneLayerBrushMask>(
            compiled.Stream.Slice(auxiliaryOffset));
        auxiliaryOffset += Unsafe.SizeOf<NativeSceneLayerBrushMask>();
        var geometryMask = MemoryMarshal.Read<NativeSceneLayerGeometryMask>(
            compiled.Stream.Slice(auxiliaryOffset));
        auxiliaryOffset += Unsafe.SizeOf<NativeSceneLayerGeometryMask>();
        var primitive = MemoryMarshal.Read<NativeGeometryPrimitive>(
            compiled.Stream.Slice(auxiliaryOffset));
        Assert.Equal(2U, brushMask.Brush.StopOffset);
        Assert.Equal(0U, geometryMask.Brush.StopOffset);
        Assert.Equal(0U, geometryMask.PrimitiveOffset);
        Assert.Equal(1U, geometryMask.PrimitiveCount);
        Assert.Equal(NativeGeometryPrimitiveKind.Line, primitive.Kind);
        Assert.Equal(4f, primitive.StrokeThickness);
    }

    [Fact]
    public void CompilerFlattensNestedPicturesWithOwnerBuffersAndTransforms()
    {
        var fill = new SolidColorBrush(new Vector4(0.8f, 0.2f, 0.1f, 1f));
        var pen = new Pen(
            new SolidColorBrush(new Vector4(0.1f, 0.7f, 1f, 1f)),
            3f);
        Matrix4x4 local = Matrix4x4.CreateTranslation(2f, 3f, 0f);
        Matrix4x4 parent = Matrix4x4.CreateScale(2f, 3f, 1f) *
            Matrix4x4.CreateTranslation(11f, 13f, 0f);
        Vector2[] retainedPoints =
        [
            new(1f, 2f),
            new(6f, 4f),
            new(9f, 8f)
        ];
        using var nested = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacity,
                    FontSize = 0.5f
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(1f, 2f, 8f, 6f),
                    Brush = fill,
                    Transform = local
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPolyline,
                    Pen = pen,
                    PointBufferOffset = 0,
                    PointBufferCount = retainedPoints.Length,
                    IsPenThicknessLocal = true,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacity }
            ],
            retainedPoints,
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());
        using var outer = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPicture,
                    Picture = nested,
                    Transform = parent
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            outer,
            93U,
            5U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(5, compiled.SourceCommandCount);
        Assert.Equal(4, compiled.NativeCommandCount);
        Assert.Equal(2, compiled.NativeDrawCount);
        Assert.Equal(1, compiled.AnalyticPrimitiveCount);
        Assert.Equal(1, compiled.StrokeCount);
        Assert.Equal(retainedPoints.Length, compiled.StrokePointCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        int resourceSize = Unsafe.SizeOf<NativeMethods.SceneResource>();
        int resourceOffset = checked((int)header.ResourceOffset);
        var analyticResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(resourceOffset));
        var strokeResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(resourceOffset + resourceSize));
        var analytic = MemoryMarshal.Read<NativeAnalyticPrimitive>(
            compiled.Stream.Slice(checked((int)analyticResource.PayloadOffset)));
        var stroke = MemoryMarshal.Read<NativeSceneStroke>(
            compiled.Stream.Slice(checked((int)strokeResource.PayloadOffset)));
        Matrix3x2 expectedParent = new(
            parent.M11,
            parent.M12,
            parent.M21,
            parent.M22,
            parent.M41,
            parent.M42);
        Matrix3x2 expectedLocal = new(
            local.M11,
            local.M12,
            local.M21,
            local.M22,
            local.M41,
            local.M42);
        Assert.Equal(expectedLocal * expectedParent, analytic.Transform);
        Assert.Equal(expectedParent, stroke.Transform);
    }

    [Fact]
    public void CompilerReportsNestedFailureAtContainingPictureCommand()
    {
        using var nested = new GpuPicture(
            [new RenderCommand { Type = RenderCommandType.DrawTexture }],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());
        using var outer = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Brush = new SolidColorBrush(Vector4.One),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPicture,
                    Picture = nested
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            outer,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnsupportedCommand, failure.Error);
        Assert.Equal(1, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawPicture, failure.CommandType);
    }

    [Fact]
    public void CompilerRejectsStateScopeCrossingNestedPictureBoundary()
    {
        using var nested = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacity,
                    FontSize = 0.5f
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());
        using var outer = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPicture,
                    Picture = nested
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            outer,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnbalancedState, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawPicture, failure.CommandType);
    }

    [Fact]
    public void CompilerRejectsPicturesBeyondMaximumNestedDepth()
    {
        var pictures = new List<GpuPicture>();
        try
        {
            GpuPicture child = new(
                [
                    new RenderCommand
                    {
                        Type = RenderCommandType.DrawRect,
                        Rect = new Rect(0f, 0f, 1f, 1f),
                        Brush = new SolidColorBrush(Vector4.One)
                    }
                ],
                Array.Empty<Vector2>(),
                Array.Empty<double>(),
                Array.Empty<Line3D>(),
                Array.Empty<float>());
            pictures.Add(child);
            for (int depth = 0; depth < 64; depth++)
            {
                child = new GpuPicture(
                    [
                        new RenderCommand
                        {
                            Type = RenderCommandType.DrawPicture,
                            Picture = child
                        }
                    ],
                    Array.Empty<Vector2>(),
                    Array.Empty<double>(),
                    Array.Empty<Line3D>(),
                    Array.Empty<float>());
                pictures.Add(child);
            }

            Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
                child,
                1U,
                1U,
                out NativeCompiledPicture? compiled,
                out NativePictureCompileFailure failure));
            Assert.Null(compiled);
            Assert.Equal(
                NativePictureCompileError.CapacityExceeded,
                failure.Error);
            Assert.Equal(0, failure.CommandIndex);
            Assert.Equal(RenderCommandType.DrawPicture, failure.CommandType);
        }
        finally
        {
            foreach (GpuPicture picture in pictures)
            {
                picture.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompilerFlattensStaticDxfRetainedSourceWithTransform(
        bool useLegacyCommand)
    {
        var source = new DrawingContext();
        source.DrawRectangle(
            new SolidColorBrush(new Vector4(0.8f, 0.2f, 0.1f, 1f)),
            null,
            new Rect(2f, 3f, 8f, 6f));
        using DxfStaticBuffer staticBuffer =
            HeadlessWindow.Shared.Compositor.CompileStaticDxf(source);
        source.Clear();
        source.DrawRectangle(
            new SolidColorBrush(Vector4.One),
            null,
            new Rect(40f, 50f, 60f, 70f));
        Matrix4x4 placement = Matrix4x4.CreateTranslation(11f, 13f, 0f);
        RenderCommand command = useLegacyCommand
            ? new RenderCommand
            {
                Type = RenderCommandType.DrawStaticDxf,
                StaticBuffer = staticBuffer,
                Transform = placement
            }
            : new RenderCommand
            {
                Type = RenderCommandType.DrawExtension,
                ExtensionId = CompositorBuiltInExtensions.StaticDxf,
                DataParam = staticBuffer,
                Transform = placement
            };
        using var picture = new GpuPicture(
            [command],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            127U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(2, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(1, compiled.AnalyticPrimitiveCount);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)(header.ResourceCount * header.ResourceStride))));
        NativeMethods.SceneResource resource = Assert.Single(
            resources.ToArray(),
            static item =>
                item.Kind == NativeSceneResourceKind.AnalyticBatch);
        NativeAnalyticPrimitive primitive =
            MemoryMarshal.Read<NativeAnalyticPrimitive>(
                compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(
            new Matrix3x2(1f, 0f, 0f, 1f, 11f, 13f),
            primitive.Transform);
        Assert.Equal(2f, primitive.X);
        Assert.Equal(3f, primitive.Y);
        Assert.Equal(8f, primitive.Width);
        Assert.Equal(6f, primitive.Height);
    }

    [Fact]
    public void CompilerAppliesStaticDxfZoomToGlyphRasterScale()
    {
        var font = InterFontFamily.Regular;
        var source = new DrawingContext();
        source.DrawGlyphRun(
            [font.GetGlyphIndex('A')],
            [new Vector2(12f, 28f)],
            font,
            16f,
            new SolidColorBrush(Vector4.One),
            Vector2.Zero);
        using DxfStaticBuffer staticBuffer =
            HeadlessWindow.Shared.Compositor.CompileStaticDxf(
                source,
                staticZoom: 3f);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawExtension,
                    ExtensionId = CompositorBuiltInExtensions.StaticDxf,
                    DataParam = staticBuffer
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            128U,
            1U,
            new NativePictureCompileOptions(2f),
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(2f, compiled.TargetDpiScale);
        Assert.Equal(1, compiled.GlyphOutlineCount);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)(header.ResourceCount * header.ResourceStride))));
        NativeMethods.SceneResource resource = Assert.Single(
            resources.ToArray(),
            static item => item.Kind == NativeSceneResourceKind.GlyphRun);
        NativeSceneGlyphOutline outline =
            MemoryMarshal.Read<NativeSceneGlyphOutline>(
                compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(96f / font.UnitsPerEm, outline.RasterScale, 5);
    }

    [Fact]
    public void CompilerRejectsDisposedStaticDxfWithTypedFailure()
    {
        var source = new DrawingContext();
        source.DrawRectangle(
            new SolidColorBrush(Vector4.One),
            null,
            new Rect(0f, 0f, 8f, 8f));
        DxfStaticBuffer staticBuffer =
            HeadlessWindow.Shared.Compositor.CompileStaticDxf(source);
        staticBuffer.Dispose();
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawExtension,
                    ExtensionId = CompositorBuiltInExtensions.StaticDxf,
                    DataParam = staticBuffer
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            129U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.InvalidArgument, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawExtension, failure.CommandType);
    }

    [Fact]
    public void CompilerRejectsGpuLateTransforms()
    {
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Brush = new SolidColorBrush(Vector4.One),
                    UseGpuTransforms = true
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(
            NativePictureCompileError.UnsupportedTransform,
            failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawRect, failure.CommandType);
    }

    [Fact]
    public void CompilerFailsClosedForNonAxisAlignedClip()
    {
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushClip,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Transform = Matrix4x4.CreateRotationZ(0.2f)
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.InvalidState, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.PushClip, failure.CommandType);
    }

    [Fact]
    public void CompilerLowersPerlinNoiseWithExactManagedTable()
    {
        var brush = new PerlinNoiseBrush(
            isTurbulence: true,
            baseFrequency: new Vector2(0.13f, 0.07f),
            numOctaves: 4,
            seed: -17f,
            tileSize: new Vector2(64f, 48f))
        {
            Opacity = 0.75f,
            CoordinateTransform = Matrix4x4.CreateTranslation(3f, 5f, 0f)
        };
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(0f, 0f, 32f, 24f),
                    Brush = brush,
                    Transform = Matrix4x4.Identity
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            119U,
            3U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.BrushCount);
        Assert.Equal(512, compiled.GradientStopCount);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        ReadOnlySpan<NativeMethods.SceneResource> resources =
            MemoryMarshal.Cast<byte, NativeMethods.SceneResource>(
                compiled.Stream.Slice(
                    checked((int)header.ResourceOffset),
                    checked((int)(header.ResourceCount * header.ResourceStride))));
        NativeMethods.SceneResource brushResource = Assert.Single(
            resources.ToArray(),
            static resource =>
                resource.Kind == NativeSceneResourceKind.BrushTable);
        NativeSceneBrush nativeBrush = MemoryMarshal.Read<NativeSceneBrush>(
            compiled.Stream.Slice(checked((int)brushResource.PayloadOffset)));
        Assert.Equal(NativeSceneBrushKind.PerlinNoise, nativeBrush.Kind);
        Assert.Equal(4U, nativeBrush.StopCount);
        Assert.Equal(0U, nativeBrush.StopOffset);
        Assert.Equal(NativeSceneGradientInterpolation.ScRgb,
            nativeBrush.Interpolation);
        Assert.Equal(0.75f, nativeBrush.Opacity);

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            119U,
            3U,
            out NativeCompiledPicture? second,
            out failure),
            failure.ToString());
        Assert.NotNull(second);
        Assert.True(compiled.Stream.SequenceEqual(second.Stream));
    }

    [Fact]
    public void CompilerLowersUnequalRoundedRectangleFillAsExactArcPath()
    {
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRoundedRect,
                    Rect = new Rect(2f, 3f, 40f, 24f),
                    RadiusX = 9f,
                    RadiusY = 5f,
                    Brush = new SolidColorBrush(Vector4.One),
                    Transform = Matrix4x4.Identity
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            121U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(0, compiled.AnalyticPrimitiveCount);
        Assert.Equal(1, compiled.PathCount);
        Assert.Equal(8, compiled.PathSegmentCount);

        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        NativeMethods.SceneResource resource =
            MemoryMarshal.Read<NativeMethods.SceneResource>(
                compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.PathBatch, resource.Kind);
        ReadOnlySpan<NativePathSegment> segments =
            MemoryMarshal.Cast<byte, NativePathSegment>(
                compiled.Stream.Slice(
                    checked((int)resource.AuxiliaryOffset),
                    checked((int)resource.AuxiliarySize)));
        Assert.Equal(4, segments.ToArray().Count(static segment =>
            segment.Kind == NativePathSegmentKind.Arc));
        Assert.All(
            segments.ToArray().Where(static segment =>
                segment.Kind == NativePathSegmentKind.Arc),
            static segment =>
            {
                Assert.Equal(9f, segment.P3.X);
                Assert.Equal(5f, segment.P3.Y);
            });
    }

    [Fact]
    public void CompilerFailsClosedForUnsupportedBrush()
    {
        var unsupported = new UnknownBrush();
        using var picture = new GpuPicture(
            new[]
            {
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Brush = unsupported,
                    Transform = Matrix4x4.Identity
                }
            },
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnsupportedBrush, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawRect, failure.CommandType);
    }

    private sealed class UnknownBrush : Brush
    {
    }

    private static Matrix3x2 ToAffine(Matrix4x4 value) => new(
        value.M11,
        value.M12,
        value.M21,
        value.M22,
        value.M41,
        value.M42);

    private static GpuTexture CreateUnbackedTexture(uint width, uint height)
    {
        var texture = (GpuTexture)RuntimeHelpers.GetUninitializedObject(
            typeof(GpuTexture));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(GpuTexture).GetField("<Width>k__BackingField", flags)!
            .SetValue(texture, width);
        typeof(GpuTexture).GetField("<Height>k__BackingField", flags)!
            .SetValue(texture, height);
        return texture;
    }
}
