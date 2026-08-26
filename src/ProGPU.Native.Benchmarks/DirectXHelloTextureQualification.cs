using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

/// <summary>
/// Replays the deterministic geometry, point-sampled checkerboard, UV mapping,
/// and clear-color contract from Microsoft's D3D12HelloTexture sample through
/// ProGPU's shared semantic renderer. The affine UV mapping is represented by
/// the image destination rectangle; edge-aliased cover meshes retain the
/// sample's triangular raster boundary.
/// </summary>
internal static class DirectXHelloTextureQualification
{
    internal const uint Width = 1280U;
    internal const uint Height = 720U;
    private const uint TextureWidth = 256U;
    private const uint TextureHeight = 256U;
    private const ulong SceneId = 0x44585445584F5241UL;
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
            "DirectX HelloTexture ProGPU oracle target");
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
            frame.SubmissionCount > 0U && frame.DrawCallCount == 2U,
            $"The DirectX HelloTexture scene failed: update={update}, " +
            $"frame={frame}.");
        RequirePixel(pixels, 8, 8, 0, 51, 102, 255);
        RequirePixel(pixels, 640, 360, 0, 0, 0, 255);
        RequirePixel(pixels, 600, 360, 255, 255, 255, 255);
        RequirePixel(pixels, 680, 360, 255, 255, 255, 255);
        RequirePixel(pixels, 640, 440, 0, 0, 0, 255);

        string stem = BackendStem(context.AdapterBackendType.ToString());
        string imagePath = Path.Combine(
            outputDirectory, $"progpu-hello-texture-{stem}.ppm");
        string contractPath = Path.Combine(
            outputDirectory, $"progpu-hello-texture-{stem}.json");
        WritePpm(imagePath, pixels);
        string pixelHash = Convert.ToHexString(SHA256.HashData(pixels));
        var contract = new
        {
            Contract = "Microsoft.DirectX-Graphics-Samples/D3D12HelloTexture",
            Width,
            Height,
            TextureWidth,
            TextureHeight,
            Adapter = context.AdapterName,
            Backend = context.AdapterBackendType.ToString(),
            Sampling = NativeImageSampling.Nearest.ToString(),
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
            "Rendered the Microsoft D3D12HelloTexture contract through " +
            $"ProGPU on adapter '{context.AdapterName}', " +
            $"backend={context.AdapterBackendType}; image={imagePath}, " +
            $"sha256={pixelHash}.");
    }

    private static byte[] BuildScene()
    {
        byte[] texture = BuildCheckerboardTexture();
        var image = new NativeSceneImageDraw(
            TextureWidth,
            TextureHeight,
            TextureWidth * 4U,
            NativeImageSampling.Nearest,
            new NativeImageRect(0f, 0f, TextureWidth, TextureHeight),
            new NativeImageRect(480f, 200f, 320f, 320f),
            Matrix3x2.Identity,
            opacity: 1f);
        Span<NativeSceneMeshVertex> vertices =
            stackalloc NativeSceneMeshVertex[6]
            {
                Vertex(480f, 200f),
                Vertex(640f, 200f),
                Vertex(480f, 520f),
                Vertex(640f, 200f),
                Vertex(800f, 200f),
                Vertex(800f, 520f)
            };
        Span<ushort> indices =
            stackalloc ushort[6] { 0, 1, 2, 3, 4, 5 };
        Span<NativeSceneVertexMesh> meshes =
            stackalloc NativeSceneVertexMesh[1]
            {
                new(
                    vertexOffset: 0U,
                    vertexCount: 6U,
                    indexOffset: 0U,
                    indexCount: 6U,
                    transform: Matrix3x2.Identity,
                    topology: NativeVertexMeshTopology.Triangles,
                    colorBlendMode: NativeVertexColorBlendMode.Modulate,
                    flags: NativeVertexMeshFlags.EdgeAliased)
            };
        Span<NativeSceneBrush> brushes = stackalloc NativeSceneBrush[1]
        {
            NativeSceneBrush.Solid(ClearColor)
        };
        Span<uint> brushIndices = stackalloc uint[1] { 0U };
        int size = NativeSceneStreamBuilder.GetRequiredBufferSize(
            commandCapacity: 2,
            resourceCapacity: 3,
            arenaCapacity: checked(texture.Length + 2048));
        byte[] destination = GC.AllocateUninitializedArray<byte>(size);
        var builder = new NativeSceneStreamBuilder(
            destination,
            SceneId,
            generation: 1U,
            commandCapacity: 2,
            resourceCapacity: 3);
        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddImageResource(
                resourceId: 1U,
                generation: 1U,
                texture,
                out uint imageIndex) &&
            builder.TryAddVertexMeshResource(
                resourceId: 2U,
                generation: 1U,
                meshes,
                vertices,
                indices,
                out uint meshIndex) &&
            builder.TryAddBrushTableResource(
                resourceId: 3U,
                generation: 1U,
                brushes,
                ReadOnlySpan<NativeSceneGradientStop>.Empty,
                out uint brushIndex) &&
            builder.TryDrawImage(
                commandId: 1U,
                imageIndex,
                new NativeImageRect(480f, 200f, 320f, 320f),
                in image) &&
            builder.TryDrawVertexMesh(
                commandId: 2U,
                meshIndex,
                new NativeImageRect(480f, 200f, 320f, 320f),
                brushIndex,
                brushIndices) &&
            builder.TryBuild(out stream);
        Require(success, "Failed to build the DirectX HelloTexture scene.");
        return stream.ToArray();
    }

    private static NativeSceneMeshVertex Vertex(float x, float y) =>
        new(new Vector2(x, y), Vector2.Zero, Vector4.One);

    private static byte[] BuildCheckerboardTexture()
    {
        byte[] pixels = GC.AllocateUninitializedArray<byte>(
            checked((int)(TextureWidth * TextureHeight * 4U)));
        const uint cellSize = TextureWidth / 8U;
        for (uint y = 0U; y < TextureHeight; ++y)
        {
            for (uint x = 0U; x < TextureWidth; ++x)
            {
                byte value = (x / cellSize) % 2U == (y / cellSize) % 2U
                    ? (byte)0
                    : (byte)255;
                int offset = checked((int)((y * TextureWidth + x) * 4U));
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }
        return pixels;
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
        int alpha)
    {
        int offset = checked((y * (int)Width + x) * 4);
        Require(
            pixels[offset] == red &&
            pixels[offset + 1] == green &&
            pixels[offset + 2] == blue &&
            pixels[offset + 3] == alpha,
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
