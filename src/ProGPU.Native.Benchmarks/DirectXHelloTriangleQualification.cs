using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

/// <summary>
/// Replays the deterministic geometry, clear color, and pass-through vertex
/// color contract from Microsoft's D3D12HelloTriangle sample through ProGPU's
/// shared semantic renderer. The Microsoft program remains the independent
/// Windows/D3D12 oracle; this producer runs unchanged on D3D12, Metal, and
/// Vulkan/WebGPU.
/// </summary>
internal static class DirectXHelloTriangleQualification
{
    internal const uint Width = 1280U;
    internal const uint Height = 720U;
    private const ulong SceneId = 0x445833544F524143UL;
    private static readonly Vector4 ClearColor =
        new(0f, 0.2f, 0.4f, 1f);

    public static void Run(string[] args)
    {
        string outputDirectory = ReadRequiredArgument(
            args, "--directx-oracle-output");
        Directory.CreateDirectory(outputDirectory);

        using var context = new WgpuContext();
        context.Initialize(window: null);
        using var target = new GpuTexture(
            context,
            Width,
            Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "DirectX HelloTriangle ProGPU oracle target");
        using var compositor = new NativeCompositor(
            context,
            TextureFormat.Rgba8Unorm);

        byte[] scene = BuildScene();
        NativeSceneUpdateMetrics update = compositor.UpdateScene(scene);
        NativeSceneFrameMetrics frame = compositor.RenderScene(
            target,
            dpiScale: 1f,
            SceneId,
            generation: 1U,
            clearColor: ClearColor);
        context.WaitIdle();
        byte[] pixels = target.ReadPixels();

        Require(
            update.ValidationError == NativeSceneValidationError.None &&
            frame.SubmissionCount > 0U && frame.DrawCallCount == 1U,
            $"The DirectX HelloTriangle scene failed: update={update}, " +
            $"frame={frame}.");
        RequirePixel(pixels, 8, 8, 0, 51, 102, 255, tolerance: 0);
        RequirePixel(pixels, 640, 280, 191, 32, 32, 255, tolerance: 3);
        RequirePixel(pixels, 560, 480, 32, 48, 175, 255, tolerance: 3);
        RequirePixel(pixels, 720, 480, 32, 175, 48, 255, tolerance: 3);

        string stem = BackendStem(context.AdapterBackendType.ToString());
        string imagePath = Path.Combine(
            outputDirectory, $"progpu-{stem}.ppm");
        string contractPath = Path.Combine(
            outputDirectory, $"progpu-{stem}.json");
        WritePpm(imagePath, pixels);
        string pixelHash = Convert.ToHexString(SHA256.HashData(pixels));
        var contract = new
        {
            Contract = "Microsoft.DirectX-Graphics-Samples/D3D12HelloTriangle",
            Width,
            Height,
            Adapter = context.AdapterName,
            Backend = context.AdapterBackendType.ToString(),
            ClearRgba8 = new[] { 0, 51, 102, 255 },
            TrianglePixels = new[]
            {
                new[] { 640, 200 },
                new[] { 800, 520 },
                new[] { 480, 520 }
            },
            PixelSha256 = pixelHash,
            Update = update,
            Frame = frame
        };
        File.WriteAllText(
            contractPath,
            JsonSerializer.Serialize(
                contract,
                new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine(
            "Rendered the Microsoft D3D12HelloTriangle contract through " +
            $"ProGPU on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; image={imagePath}, " +
            $"sha256={pixelHash}.");
    }

    private static byte[] BuildScene()
    {
        Span<NativeSceneMeshVertex> vertices =
            stackalloc NativeSceneMeshVertex[3]
            {
                new(new Vector2(640f, 200f), Vector2.Zero,
                    new Vector4(1f, 0f, 0f, 1f)),
                new(new Vector2(800f, 520f), Vector2.Zero,
                    new Vector4(0f, 1f, 0f, 1f)),
                new(new Vector2(480f, 520f), Vector2.Zero,
                    new Vector4(0f, 0f, 1f, 1f))
            };
        Span<ushort> indices = stackalloc ushort[3] { 0, 1, 2 };
        Span<NativeSceneVertexMesh> meshes =
            stackalloc NativeSceneVertexMesh[1]
            {
                new(
                    vertexOffset: 0U,
                    vertexCount: 3U,
                    indexOffset: 0U,
                    indexCount: 3U,
                    transform: Matrix3x2.Identity,
                    topology: NativeVertexMeshTopology.Triangles,
                    colorBlendMode: NativeVertexColorBlendMode.Modulate,
                    flags: NativeVertexMeshFlags.EdgeAliased)
            };
        Span<NativeSceneBrush> brushes = stackalloc NativeSceneBrush[1]
        {
            NativeSceneBrush.Solid(Vector4.One)
        };
        Span<uint> brushIndices = stackalloc uint[1] { 0U };
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 1,
            resourceCapacity: 2,
            arenaCapacity: 1024);
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            SceneId,
            generation: 1U,
            commandCapacity: 1,
            resourceCapacity: 2);
        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddVertexMeshResource(
                resourceId: 1U,
                generation: 1U,
                meshes,
                vertices,
                indices,
                out uint meshIndex) &&
            builder.TryAddBrushTableResource(
                resourceId: 2U,
                generation: 1U,
                brushes,
                ReadOnlySpan<NativeSceneGradientStop>.Empty,
                out uint brushIndex) &&
            builder.TryDrawVertexMesh(
                commandId: 1U,
                meshIndex,
                new NativeImageRect(480f, 200f, 320f, 320f),
                brushIndex,
                brushIndices) &&
            builder.TryBuild(out stream);
        Require(success, "Failed to build the DirectX HelloTriangle scene.");
        return stream.ToArray();
    }

    private static void WritePpm(string path, byte[] pixels)
    {
        byte[] header = System.Text.Encoding.ASCII.GetBytes(
            $"P6\n{Width} {Height}\n255\n");
        byte[] output = GC.AllocateUninitializedArray<byte>(
            checked(header.Length + (int)(Width * Height * 3U)));
        header.CopyTo(output, 0);
        int destination = header.Length;
        for (int source = 0; source < pixels.Length; source += 4)
        {
            output[destination++] = pixels[source];
            output[destination++] = pixels[source + 1];
            output[destination++] = pixels[source + 2];
        }
        File.WriteAllBytes(path, output);
    }

    private static void RequirePixel(
        byte[] pixels,
        int x,
        int y,
        int red,
        int green,
        int blue,
        int alpha,
        int tolerance)
    {
        int offset = checked((y * (int)Width + x) * 4);
        Require(
            Math.Abs(pixels[offset] - red) <= tolerance &&
            Math.Abs(pixels[offset + 1] - green) <= tolerance &&
            Math.Abs(pixels[offset + 2] - blue) <= tolerance &&
            Math.Abs(pixels[offset + 3] - alpha) <= tolerance,
            $"Unexpected pixel at ({x},{y}): " +
            $"{pixels[offset]}/{pixels[offset + 1]}/" +
            $"{pixels[offset + 2]}/{pixels[offset + 3]}.");
    }

    private static string ReadRequiredArgument(string[] args, string name)
    {
        for (int index = 0; index + 1 < args.Length; ++index)
        {
            if (string.Equals(args[index], name,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(args[index + 1]))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }
        throw new ArgumentException($"{name} requires an output directory.");
    }

    private static string BackendStem(string backend)
    {
        Span<char> buffer = stackalloc char[backend.Length];
        int count = 0;
        foreach (char value in backend)
        {
            if (char.IsLetterOrDigit(value))
                buffer[count++] = char.ToLowerInvariant(value);
        }
        return new string(buffer[..count]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
