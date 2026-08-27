using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

internal static class RetainedViewport3DQualification
{
    private const uint Width = 128U;
    private const uint Height = 96U;
    private const uint ViewportHandle = 1U;
    private const uint TargetHandle = 2U;
    private const uint ClipHandle = 3U;
    private const uint TransformHandle = 4U;
    private const ulong SceneId = 0x4D494C5633443031UL;
    private const float Opacity = 0.5f;
    private static readonly NativeImageRect Viewport =
        new(32f, 20f, 64f, 48f);
    private static readonly NativeImageRect Clip =
        new(50f, 30f, 28f, 25f);
    private static readonly NativeMilRect ScrollClip =
        new(48, 28, 28, 26);
    private static readonly NativeImageRect TransformedViewport =
        new(32f, 21f, 48f, 36f);
    private static readonly NativeImageRect EffectiveClip =
        new(48f, 28.5f, 18.5f, 18.75f);

    public static void Run()
    {
        using var context = new WgpuContext();
        context.Initialize(window: null);
        using var target = new GpuTexture(
            context,
            Width,
            Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "retained MIL Viewport3D qualification target");
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);

        FrameResult front = Render(
            compositor,
            context,
            target,
            NativeMesh3DFlags.FrontFace,
            reverseWinding: false,
            generation: 1U);
        FrameResult back = Render(
            compositor,
            context,
            target,
            NativeMesh3DFlags.BackFace,
            reverseWinding: true,
            generation: 2U);
        FrameResult glossy = Render(
            compositor,
            context,
            target,
            NativeMesh3DFlags.FrontFace,
            reverseWinding: false,
            generation: 3U,
            shininess: 256f);
        FrameResult orthographic = Render(
            compositor,
            context,
            target,
            NativeMesh3DFlags.FrontFace,
            reverseWinding: false,
            generation: 4U,
            orthographic: true);
        FrameResult pointLit = Render(
            compositor,
            context,
            target,
            NativeMesh3DFlags.FrontFace,
            reverseWinding: false,
            generation: 5U,
            lights: CreatePointLights());
        FrameResult spotLit = Render(
            compositor,
            context,
            target,
            NativeMesh3DFlags.FrontFace,
            reverseWinding: false,
            generation: 6U,
            lights: CreateSpotLights());

        Require(
            front.Update.ValidationError == NativeSceneValidationError.None &&
            back.Update.ValidationError == NativeSceneValidationError.None &&
            glossy.Update.ValidationError == NativeSceneValidationError.None &&
            orthographic.Update.ValidationError == NativeSceneValidationError.None &&
            pointLit.Update.ValidationError == NativeSceneValidationError.None &&
            spotLit.Update.ValidationError == NativeSceneValidationError.None &&
            front.Frame.SubmissionCount > 0U &&
            back.Frame.SubmissionCount > 0U &&
            glossy.Frame.SubmissionCount > 0U &&
            orthographic.Frame.SubmissionCount > 0U &&
            pointLit.Frame.SubmissionCount > 0U &&
            spotLit.Frame.SubmissionCount > 0U &&
            front.Frame.DrawCallCount == 1U &&
            back.Frame.DrawCallCount == 1U &&
            glossy.Frame.DrawCallCount == 1U &&
            orthographic.Frame.DrawCallCount == 1U &&
            pointLit.Frame.DrawCallCount == 1U &&
            spotLit.Frame.DrawCallCount == 1U,
            "retained MIL Viewport3D execution failed: " +
            $"front={front.Update}/{front.Frame}, " +
            $"back={back.Update}/{back.Frame}, " +
            $"glossy={glossy.Update}/{glossy.Frame}, " +
            $"orthographic={orthographic.Update}/{orthographic.Frame}, " +
            $"point={pointLit.Update}/{pointLit.Frame}, " +
            $"spot={spotLit.Update}/{spotLit.Frame}");
        Require(
            IsInside(TransformedViewport, front.Extent) &&
            IsInside(TransformedViewport, back.Extent) &&
            IsInside(TransformedViewport, orthographic.Extent) &&
            IsInside(EffectiveClip, front.Extent) &&
            IsInside(EffectiveClip, back.Extent) &&
            IsInside(EffectiveClip, orthographic.Extent),
            "retained MIL Viewport3D escaped its typed viewport: " +
            $"viewport={FormatRect(TransformedViewport)}, " +
            $"clip={FormatRect(EffectiveClip)}, " +
            $"front={front.Extent}, " +
            $"back={back.Extent}, orthographic={orthographic.Extent}");
        Require(
            front.Pixels.AsSpan().SequenceEqual(back.Pixels),
            "front/back retained MIL face selection produced different pixels");
        Require(
            !front.Pixels.AsSpan().SequenceEqual(glossy.Pixels),
            "retained MIL material shininess did not affect GPU output");
        Require(
            !front.Pixels.AsSpan().SequenceEqual(orthographic.Pixels),
            "retained MIL orthographic camera matched perspective output");
        Require(
            !pointLit.Pixels.AsSpan().SequenceEqual(spotLit.Pixels),
            "retained MIL point and spot lights produced identical output");
        RequireLitPixel(front.Pixels, 56, 39);
        RequireLitPixel(orthographic.Pixels, 56, 39);
        RequireColoredPixel(pointLit.Pixels, 56, 39, "point");
        RequireColoredPixel(spotLit.Pixels, 56, 39, "spot");

        Console.WriteLine(
            "Qualified live retained MIL Viewport3D placement and exact " +
            "front/back material selection " +
            $"on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; " +
            $"viewport={FormatRect(TransformedViewport)}, " +
            $"clip={FormatRect(EffectiveClip)}, " +
            $"opacity={Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
            $"extent={front.Extent}, front={front.Frame}, back={back.Frame}, " +
            $"orthographic={orthographic.Extent}/{orthographic.Frame}, " +
            $"point={FormatPixel(pointLit.Pixels, 56, 39)}, " +
            $"spot={FormatPixel(spotLit.Pixels, 56, 39)}.");
    }

    private static FrameResult Render(
        NativeCompositor compositor,
        WgpuContext context,
        GpuTexture target,
        NativeMesh3DFlags faceMode,
        bool reverseWinding,
        ulong generation,
        float shininess = 1f,
        bool orthographic = false,
        NativeSceneLight3D[]? lights = null)
    {
        byte[] scene = BuildScene(
            faceMode,
            reverseWinding,
            generation,
            shininess,
            orthographic,
            lights);
        NativeSceneUpdateMetrics update = compositor.UpdateScene(scene);
        NativeSceneFrameMetrics frame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation,
            clearColor: new Vector4(0f, 0f, 0f, 1f));
        context.WaitIdle();
        byte[] pixels = target.ReadPixels();
        return new FrameResult(
            update,
            frame,
            pixels,
            MeasureColoredPixels(pixels));
    }

    private static byte[] BuildScene(
        NativeMesh3DFlags faceMode,
        bool reverseWinding,
        ulong generation,
        float shininess,
        bool orthographic,
        NativeSceneLight3D[]? lights)
    {
        var batch = new NativeMilBatchBuilder();
        batch.CreateResource(
            ViewportHandle,
            NativeMilResourceType.Viewport3DVisual);
        batch.CreateResource(
            TargetHandle,
            NativeMilResourceType.GenericRenderTarget);
        batch.CreateResource(
            ClipHandle,
            NativeMilResourceType.RectangleGeometry);
        batch.CreateResource(
            TransformHandle,
            NativeMilResourceType.ScaleTransform);
        batch.CreateVisual(ViewportHandle);
        batch.SetScaleTransform(TransformHandle, 0.75, 0.75);
        batch.SetVisualTransform(ViewportHandle, TransformHandle);
        batch.SetVisualOffset(ViewportHandle, 8, 6);
        batch.SetVisualOpacity(ViewportHandle, Opacity);
        batch.SetVisualScrollableAreaClip(ViewportHandle, ScrollClip);
        batch.SetRectangleGeometry(
            ClipHandle,
            Clip.X,
            Clip.Y,
            Clip.Width,
            Clip.Height);
        batch.SetVisualClip(ViewportHandle, ClipHandle);
        batch.CreateGenericTarget(TargetHandle, Width, Height);
        batch.SetTargetClearColor(
            TargetHandle,
            new NativeMilColor(0f, 0f, 0f, 1f));
        batch.SetTargetRoot(TargetHandle, ViewportHandle);

        using var channel = new NativeMilChannel();
        _ = channel.Apply(batch.WrittenSpan);
        channel.SetViewport3DScene(
            ViewportHandle,
            CreateViewportScene(
                faceMode,
                reverseWinding,
                shininess,
                orthographic,
                lights));
        NativeMilCompiledScene scene = channel.CompileScene(
            TargetHandle,
            SceneId,
            generation);
        Require(
            scene.Stream.Length > 0 && scene.Metrics.VisualCount == 1U,
            "retained MIL Viewport3D did not compile to a semantic scene");
        return scene.Stream;
    }

    private static NativeMilViewport3DScene CreateViewportScene(
        NativeMesh3DFlags faceMode,
        bool reverseWinding,
        float shininess,
        bool orthographic,
        NativeSceneLight3D[]? lights)
    {
        var vertices = new NativeSceneMesh3DVertex[3];
        vertices[0] = CreateVertex(new Vector3(-0.8f, -0.8f, 0f));
        vertices[1] = CreateVertex(new Vector3(0.8f, -0.8f, 0f));
        vertices[2] = CreateVertex(new Vector3(0f, 0.8f, 0f));
        NativeSceneLight3D[] retainedLights = lights ?? [];
        var mesh = new NativeSceneMesh3D
        {
            StructSize = (uint)Unsafe.SizeOf<NativeSceneMesh3D>(),
            Flags = (uint)faceMode,
            Topology = (uint)NativeMesh3DTopology.Triangles,
            RenderMode = (uint)NativeMesh3DRenderMode.Solid,
            VertexOffset = 0U,
            VertexCount = 3U,
            IndexOffset = 0U,
            IndexCount = 3U,
            ModelTransform = new NativeMatrix4x4(Matrix4x4.Identity),
            NormalTransform = new NativeMatrix4x4(Matrix4x4.Identity),
            Color = new Vector4(1f, 0f, 0f, 1f),
            LightDirection = Float4(0f, 0f, -1f, 0.4f),
            AmbientColor = Float4(1f, 1f, 1f, 0.2f),
            SpecularColor = Float4(0f, 1f, 0f, shininess),
            MaterialAmbient = Float4(1f, 1f, 1f, 1f),
            Opacity = 1f,
            ShadingMode = 1U,
            LightOffset = 0U,
            LightCount = (uint)retainedLights.Length
        };
        return new NativeMilViewport3DScene(
            new NativeSceneCamera3D(
                orthographic
                    ? Matrix4x4.CreateOrthographic(
                        2.4f,
                        1.8f,
                        0.1f,
                        100f)
                    : Matrix4x4.CreatePerspectiveFieldOfView(
                        MathF.PI / 4f,
                        Viewport.Width / Viewport.Height,
                        0.1f,
                        100f),
                Matrix4x4.CreateLookAt(
                    new Vector3(0f, 0f, 2f),
                    Vector3.Zero,
                    Vector3.UnitY),
                new Vector3(0f, 0f, 2f)),
            Viewport,
            [mesh],
            vertices,
            reverseWinding ? [0U, 2U, 1U] : [0U, 1U, 2U],
            retainedLights);
    }

    private static NativeSceneLight3D[] CreatePointLights() =>
    [
        CreateAmbientLight(new Vector4(0.05f, 0.05f, 0.05f, 1f)),
        new NativeSceneLight3D
        {
            StructSize = (uint)Unsafe.SizeOf<NativeSceneLight3D>(),
            Kind = (uint)NativeLight3DKind.Point,
            Color = new Vector4(1f, 1f, 1f, 1f),
            PositionRange = Float4(0f, 0f, 2f, 10f),
            AttenuationOuterCos = Float4(1f, 0.15f, 0.05f, 0f)
        }
    ];

    private static NativeSceneLight3D[] CreateSpotLights() =>
    [
        CreateAmbientLight(new Vector4(0.05f, 0.05f, 0.05f, 1f)),
        new NativeSceneLight3D
        {
            StructSize = (uint)Unsafe.SizeOf<NativeSceneLight3D>(),
            Kind = (uint)NativeLight3DKind.Spot,
            Color = new Vector4(1f, 0.8f, 0.6f, 1f),
            PositionRange = Float4(0.45f, 0f, 2f, 10f),
            DirectionInnerCos = Float4(
                0f, 0f, -1f, MathF.Cos(15f * MathF.PI / 180f)),
            AttenuationOuterCos = Float4(
                1f, 0.1f, 0.02f, MathF.Cos(35f * MathF.PI / 180f))
        }
    ];

    private static NativeSceneLight3D CreateAmbientLight(Vector4 color) =>
        new()
        {
            StructSize = (uint)Unsafe.SizeOf<NativeSceneLight3D>(),
            Kind = (uint)NativeLight3DKind.Ambient,
            Color = color
        };

    private static NativeSceneMesh3DVertex CreateVertex(Vector3 position) =>
        new()
        {
            Position = new NativePoint3D(position),
            Normal = new NativePoint3D(new Vector3(0f, 0f, 1f)),
            TextureCoordinate = Vector2.Zero,
            Reserved0 = 0U,
            Reserved1 = 0U
        };

    private static NativeFloat4 Float4(float x, float y, float z, float w) =>
        new() { X = x, Y = y, Z = z, W = w };

    private static PixelExtent MeasureColoredPixels(byte[] pixels)
    {
        int minimumX = (int)Width;
        int minimumY = (int)Height;
        int maximumX = -1;
        int maximumY = -1;
        int count = 0;
        for (int y = 0; y < Height; ++y)
        {
            for (int x = 0; x < Width; ++x)
            {
                int offset = checked((y * (int)Width + x) * 4);
                if (pixels[offset] == 0 && pixels[offset + 1] == 0 &&
                    pixels[offset + 2] == 0)
                {
                    continue;
                }
                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
                ++count;
            }
        }
        return new PixelExtent(
            minimumX,
            minimumY,
            maximumX,
            maximumY,
            count);
    }

    private static void RequireLitPixel(byte[] pixels, int x, int y)
    {
        int offset = checked((y * (int)Width + x) * 4);
        Require(
            pixels[offset] >= 74 && pixels[offset] <= 80 &&
            pixels[offset + 1] >= 49 && pixels[offset + 1] <= 53 &&
            pixels[offset + 2] == 0 && pixels[offset + 3] == 255,
            $"unexpected retained MIL Viewport3D center pixel at ({x},{y}): " +
            $"{pixels[offset]}/{pixels[offset + 1]}/" +
            $"{pixels[offset + 2]}/{pixels[offset + 3]}");
    }

    private static void RequireColoredPixel(
        byte[] pixels,
        int x,
        int y,
        string family)
    {
        int offset = checked((y * (int)Width + x) * 4);
        Require(
            pixels[offset] != 0 || pixels[offset + 1] != 0 ||
                pixels[offset + 2] != 0,
            $"retained MIL {family} light left the center pixel unlit");
    }

    private static string FormatPixel(byte[] pixels, int x, int y)
    {
        int offset = checked((y * (int)Width + x) * 4);
        return $"{pixels[offset]}/{pixels[offset + 1]}/" +
            $"{pixels[offset + 2]}/{pixels[offset + 3]}";
    }

    private static bool IsInside(
        NativeImageRect bounds,
        PixelExtent extent) =>
        extent.IsVisible &&
        extent.MinimumX >= (int)MathF.Floor(bounds.X) &&
        extent.MinimumY >= (int)MathF.Floor(bounds.Y) &&
        extent.MaximumX < (int)MathF.Ceiling(bounds.X + bounds.Width) &&
        extent.MaximumY < (int)MathF.Ceiling(bounds.Y + bounds.Height);

    private static string FormatRect(NativeImageRect value) =>
        FormattableString.Invariant(
            $"[{value.X},{value.Y}]-[{value.X + value.Width},{value.Y + value.Height}]");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private readonly record struct PixelExtent(
        int MinimumX,
        int MinimumY,
        int MaximumX,
        int MaximumY,
        int Count)
    {
        internal bool IsVisible => Count > 0;

        public override string ToString() =>
            $"[{MinimumX},{MinimumY}]-[{MaximumX},{MaximumY}], pixels={Count}";
    }

    private readonly record struct FrameResult(
        NativeSceneUpdateMetrics Update,
        NativeSceneFrameMetrics Frame,
        byte[] Pixels,
        PixelExtent Extent);
}
