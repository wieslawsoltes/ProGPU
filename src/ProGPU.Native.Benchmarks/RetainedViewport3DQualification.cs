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
    private const ulong SceneId = 0x4D494C5633443031UL;
    private static readonly NativeImageRect Viewport =
        new(32f, 20f, 64f, 48f);

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

        Require(
            front.Update.ValidationError == NativeSceneValidationError.None &&
            back.Update.ValidationError == NativeSceneValidationError.None &&
            front.Frame.SubmissionCount > 0U &&
            back.Frame.SubmissionCount > 0U &&
            front.Frame.DrawCallCount == 1U &&
            back.Frame.DrawCallCount == 1U,
            "retained MIL Viewport3D execution failed: " +
            $"front={front.Update}/{front.Frame}, " +
            $"back={back.Update}/{back.Frame}");
        Require(
            IsInsideViewport(front.Extent) && IsInsideViewport(back.Extent),
            "retained MIL Viewport3D escaped its typed viewport: " +
            $"viewport={Viewport}, front={front.Extent}, " +
            $"back={back.Extent}");
        Require(
            front.Pixels.AsSpan().SequenceEqual(back.Pixels),
            "front/back retained MIL face selection produced different pixels");
        RequireRedPixel(front.Pixels, 64, 44);

        Console.WriteLine(
            "Qualified live retained MIL Viewport3D placement and exact " +
            "front/back material selection " +
            $"on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; " +
            $"viewport={Viewport}, extent={front.Extent}, " +
            $"front={front.Frame}, back={back.Frame}.");
    }

    private static FrameResult Render(
        NativeCompositor compositor,
        WgpuContext context,
        GpuTexture target,
        NativeMesh3DFlags faceMode,
        bool reverseWinding,
        ulong generation)
    {
        byte[] scene = BuildScene(faceMode, reverseWinding, generation);
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
        ulong generation)
    {
        var batch = new NativeMilBatchBuilder();
        batch.CreateResource(
            ViewportHandle,
            NativeMilResourceType.Viewport3DVisual);
        batch.CreateResource(
            TargetHandle,
            NativeMilResourceType.GenericRenderTarget);
        batch.CreateVisual(ViewportHandle);
        batch.CreateGenericTarget(TargetHandle, Width, Height);
        batch.SetTargetClearColor(
            TargetHandle,
            new NativeMilColor(0f, 0f, 0f, 1f));
        batch.SetTargetRoot(TargetHandle, ViewportHandle);

        using var channel = new NativeMilChannel();
        _ = channel.Apply(batch.WrittenSpan);
        channel.SetViewport3DScene(
            ViewportHandle,
            CreateViewportScene(faceMode, reverseWinding));
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
        bool reverseWinding)
    {
        var vertices = new NativeSceneMesh3DVertex[3];
        vertices[0] = CreateVertex(new Vector3(-0.8f, -0.8f, 0f));
        vertices[1] = CreateVertex(new Vector3(0.8f, -0.8f, 0f));
        vertices[2] = CreateVertex(new Vector3(0f, 0.8f, 0f));
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
            LightDirection = Float4(0f, 0f, -1f, 1f),
            AmbientColor = Float4(0f, 0f, 0f, 0f),
            SpecularColor = Float4(0f, 0f, 0f, 1f),
            MaterialAmbient = Float4(0f, 0f, 0f, 1f),
            Opacity = 1f,
            ShadingMode = 0U,
            Reserved0 = 0U,
            Reserved1 = 0U
        };
        return new NativeMilViewport3DScene(
            new NativeSceneCamera3D(
                Matrix4x4.CreatePerspectiveFieldOfView(
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
            reverseWinding ? [0U, 2U, 1U] : [0U, 1U, 2U]);
    }

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

    private static void RequireRedPixel(byte[] pixels, int x, int y)
    {
        int offset = checked((y * (int)Width + x) * 4);
        Require(
            pixels[offset] >= 250 && pixels[offset + 1] == 0 &&
            pixels[offset + 2] == 0 && pixels[offset + 3] == 255,
            $"unexpected retained MIL Viewport3D center pixel at ({x},{y}): " +
            $"{pixels[offset]}/{pixels[offset + 1]}/" +
            $"{pixels[offset + 2]}/{pixels[offset + 3]}");
    }

    private static bool IsInsideViewport(PixelExtent extent) =>
        extent.IsVisible &&
        extent.MinimumX >= (int)Viewport.X &&
        extent.MinimumY >= (int)Viewport.Y &&
        extent.MaximumX < (int)(Viewport.X + Viewport.Width) &&
        extent.MaximumY < (int)(Viewport.Y + Viewport.Height);

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
