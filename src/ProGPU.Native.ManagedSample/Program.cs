using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Backend.Native;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using Silk.NET.WebGPU;

const uint width = 640;
const uint height = 360;
bool useDawn = args.Contains("--dawn", StringComparer.Ordinal);
bool recreateAfterDeviceLoss = args.Contains(
    "--device-loss",
    StringComparer.Ordinal);
string? requestedOutput = args.FirstOrDefault(
    argument =>
        !string.Equals(argument, "--dawn", StringComparison.Ordinal) &&
        !string.Equals(argument, "--device-loss", StringComparison.Ordinal));
var outputPath = requestedOutput is not null
    ? requestedOutput
    : "progpu-native-managed-sample.ppm";

DawnGpuContext? dawnContext = null;
WgpuContext context;
if (useDawn)
{
    dawnContext = DawnGpuContext.CreateMetalPresentation();
    context = dawnContext.Context;
}
else
{
    context = new WgpuContext();
    context.Initialize(window: null);
}
using IDisposable contextOwner = (IDisposable?)dawnContext ?? context;
using var target = new GpuTexture(
    context,
    width,
    height,
    TextureFormat.Rgba8Unorm,
    TextureUsage.RenderAttachment | TextureUsage.CopySrc,
    "ProGPU native managed sample target");
using var compositor = dawnContext is null
    ? new NativeCompositor(context, TextureFormat.Rgba8Unorm)
    : NativeDawnAdapter.CreateCompositor(
        dawnContext,
        TextureFormat.Rgba8Unorm);

var recorder = new GpuPictureRecorder();
DrawingContext drawing = recorder.BeginRecording(new Rect(0f, 0f, width, height));
drawing.DrawRectangle(
    new SolidColorBrush(new Vector4(0.08f, 0.42f, 0.95f, 1f)),
    null,
    new Rect(48f, 48f, 180f, 120f));
drawing.DrawRectangle(
    new SolidColorBrush(new Vector4(0.98f, 0.52f, 0.08f, 1f)),
    null,
    new Rect(280f, 64f, 280f, 132f));
drawing.DrawVertexMesh(
    new SolidColorBrush(Vector4.One),
    new VertexMesh2D(
        VertexMeshTopology.Triangles,
        [new(232f, 20f), new(278f, 20f), new(255f, 58f)],
        colors:
        [
            new(1f, 0f, 0f, 1f),
            new(0f, 1f, 0f, 1f),
            new(0f, 0f, 1f, 1f)
        ],
        indices: [0, 1, 2]),
    VertexColorBlendMode.Dst);
drawing.PushOpacity(0.75f);
drawing.PushClip(new Rect(128f, 224f, 256f, 72f));
drawing.DrawRectangle(
    new LinearGradientBrush(
        new Vector2(128f, 224f),
        new Vector2(512f, 224f),
        [
            new GradientStop(new Vector4(0.20f, 0.82f, 0.48f, 1f), 0f),
            new GradientStop(new Vector4(0.92f, 0.22f, 0.72f, 1f), 1f)
        ]),
    null,
    new Rect(128f, 224f, 384f, 72f));
drawing.PopClip();
drawing.PopOpacity();
drawing.DrawDotGrid(
    new SolidColorBrush(new Vector4(0.10f, 0.82f, 1f, 1f)),
    new Rect(32f, 304f, 560f, 40f),
    spacing: 16f,
    radius: 4f,
    phase: new Vector2(40f, 320f));
drawing.DrawPointBatch(
    new SolidColorBrush(new Vector4(1f, 0.82f, 0.08f, 1f)),
    [new(96f, 342f), new(128f, 342f), new(160f, 342f)],
    radius: 6f,
    round: true);
drawing.DrawPointBatch(
    new SolidColorBrush(new Vector4(1f, 0.12f, 0.72f, 1f)),
    [new(448f, 342f), new(464f, 342f), new(480f, 342f)],
    radius: 0f,
    round: false,
    isEdgeAliased: true);
using GpuPicture picture = recorder.EndRecording();
const ulong sceneId = 0x4D414E4147454455UL;
const ulong sceneGeneration = 1UL;
if (!GpuPictureNativeSceneCompiler.TryCompile(
        picture,
        sceneId,
        sceneGeneration,
        out NativeCompiledPicture? compiled,
        out NativePictureCompileFailure failure) ||
    compiled is null)
{
    throw new InvalidOperationException(
        $"The managed picture compiler failed: {failure}.");
}
NativeSceneUpdateMetrics updateMetrics = compositor.UpdateScene(compiled.Stream);
if (updateMetrics.CommandCount != 9U ||
    updateMetrics.ResourceCount != 8U ||
    updateMetrics.DrawCount != 5U ||
    updateMetrics.MaximumStackDepth != 2U ||
    compiled.SourceCommandCount != 11 ||
    compiled.NativeCommandCount != 9 ||
    compiled.NativeDrawCount != 5 ||
    compiled.BrushCount != 7 ||
    compiled.GradientStopCount != 2)
{
    throw new InvalidOperationException(
        $"The compiled managed picture contract is invalid: {updateMetrics}.");
}
NativeSceneFrameMetrics metrics = compositor.RenderScene(
    target,
    dpiScale: 1f,
    sceneId,
    sceneGeneration,
    new Vector4(0.02f, 0.025f, 0.04f, 1f));
metrics = compositor.RenderScene(
    target,
    dpiScale: 1f,
    sceneId,
    sceneGeneration,
    new Vector4(0.02f, 0.025f, 0.04f, 1f));
if (metrics.VertexUploadBytes != 0U ||
    metrics.IndexUploadBytes != 0U ||
    metrics.BrushUploadBytes != 0U ||
    metrics.GradientStopUploadBytes != 0U)
{
    throw new InvalidOperationException(
        "Stable managed-picture replay rebuilt retained native resources.");
}
byte[] pixels = target.ReadPixels();

if (!HasExpectedColors(pixels, checked((int)width)))
{
    throw new InvalidOperationException(
        "The managed host did not observe the expected native GPU pixels.");
}
if (recreateAfterDeviceLoss)
{
    if (dawnContext is null)
    {
        throw new ArgumentException(
            "--device-loss requires the --dawn provider path.");
    }
    dawnContext.ForceDeviceLossForDiagnostics();
    for (int attempt = 0;
         attempt < 5_000 && !context.IsDeviceLost;
         attempt++)
    {
        context.PollDevice(wait: false);
        Thread.Sleep(1);
    }
    if (!context.IsDeviceLost)
    {
        throw new InvalidOperationException(
            "The forced Dawn device loss was not observed.");
    }

    using DawnGpuContext replacement =
        DawnGpuContext.CreateMetalPresentation();
    using NativeCompositor replacementCompositor =
        NativeDawnAdapter.RecreateCompositor(
            compositor,
            replacement);
    using var replacementTarget = new GpuTexture(
        replacement.Context,
        width,
        height,
        TextureFormat.Rgba8Unorm,
        TextureUsage.RenderAttachment | TextureUsage.CopySrc,
        "ProGPU recreated native managed sample target");
    metrics = replacementCompositor.RenderScene(
        replacementTarget,
        dpiScale: 1f,
        sceneId,
        sceneGeneration,
        new Vector4(0.02f, 0.025f, 0.04f, 1f));
    pixels = replacementTarget.ReadPixels();
    if (!HasExpectedColors(pixels, checked((int)width)))
    {
        throw new InvalidOperationException(
            "The recreated Dawn/C++ renderer did not preserve expected GPU pixels.");
    }
}

WritePpm(outputPath, pixels, checked((int)width), checked((int)height));
var info = dawnContext is null
    ? NativeCompositor.GetInfo()
    : NativeDawnAdapter.GetInfo();
Console.WriteLine(
    $"[ProGPUNativeManaged] backend={(useDawn ? "Dawn" : "wgpu-native")}; " +
    $"recreated={recreateAfterDeviceLoss}; " +
    $"{info.Name}; " +
    $"sourceCommands={compiled.SourceCommandCount}; " +
    $"nativeCommands={metrics.CommandCount}; draws={metrics.DrawCallCount}; " +
    $"submissions={metrics.SubmissionCount}; output={outputPath}");

static bool HasExpectedColors(byte[] pixels, int width)
{
    ReadOnlySpan<byte> Pixel(int x, int y) =>
        pixels.AsSpan((y * width + x) * 4, 4);
    var blue = Pixel(100, 100);
    var amber = Pixel(360, 130);
    var gradientStart = Pixel(160, 260);
    var gradientInside = Pixel(352, 260);
    var clippedGradient = Pixel(480, 260);
    var gridDot = Pixel(40, 320);
    var gridGap = Pixel(48, 320);
    var roundPoint = Pixel(96, 342);
    var hairlinePoint = Pixel(448, 342);
    var meshCenter = Pixel(255, 34);
    var background = Pixel(10, 10);
    return blue[2] > 180 && blue[0] < 100 &&
        amber[0] > 180 && amber[1] > 90 &&
        gradientStart[1] > gradientStart[0] &&
        gradientInside[0] > gradientInside[1] &&
        clippedGradient[0] < 30 && clippedGradient[1] < 30 &&
        gridDot[1] > 160 && gridDot[2] > 200 &&
        gridGap[0] < 30 && gridGap[1] < 30 &&
        roundPoint[0] > 200 && roundPoint[1] > 150 &&
        hairlinePoint[0] > 200 && hairlinePoint[2] > 120 &&
        meshCenter[0] + meshCenter[1] + meshCenter[2] > 180 &&
        background[0] < 30 && background[1] < 30;
}

static void WritePpm(
    string path,
    byte[] pixels,
    int width,
    int height)
{
    using var output = File.Create(path);
    using var writer = new BinaryWriter(output, System.Text.Encoding.ASCII);
    writer.Write(System.Text.Encoding.ASCII.GetBytes(
        $"P6\n{width} {height}\n255\n"));
    for (var offset = 0; offset < pixels.Length; offset += 4)
    {
        writer.Write(pixels, offset, 3);
    }
}
