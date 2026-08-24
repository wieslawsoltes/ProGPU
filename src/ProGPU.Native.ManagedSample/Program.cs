using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Backend.Native;
using ProGPU.Fonts.Inter;
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
Console.WriteLine(
    $"[ProGPUNativeManaged] adapter '{context.AdapterName}', " +
    $"backend={context.AdapterBackendType}; validating pre-render readback.");
_ = target.ReadPixels();
Console.WriteLine("[ProGPUNativeManaged] pre-render readback passed.");
using var compositor = dawnContext is null
    ? new NativeCompositor(context, TextureFormat.Rgba8Unorm)
    : NativeDawnAdapter.CreateCompositor(
        dawnContext,
        TextureFormat.Rgba8Unorm);

var nestedRecorder = new GpuPictureRecorder();
DrawingContext nestedDrawing = nestedRecorder.BeginRecording(
    new Rect(0f, 0f, 90f, 60f));
nestedDrawing.DrawRectangle(
    new SolidColorBrush(new Vector4(0.08f, 0.42f, 0.95f, 1f)),
    null,
    new Rect(0f, 0f, 90f, 60f));
using GpuPicture nestedPicture = nestedRecorder.EndRecording();

var recorder = new GpuPictureRecorder();
DrawingContext drawing = recorder.BeginRecording(new Rect(0f, 0f, width, height));
drawing.DrawPictureTransformed(
    nestedPicture,
    Matrix4x4.CreateScale(2f, 2f, 1f) *
        Matrix4x4.CreateTranslation(48f, 48f, 0f));
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
var path = new PathGeometry();
var pathFigure = new PathFigure(new Vector2(575f, 54f), isClosed: true);
pathFigure.Segments.Add(new QuadraticBezierSegment(
    new Vector2(575f, 15f),
    new Vector2(600f, 20f)));
pathFigure.Segments.Add(new CubicBezierSegment(
    new Vector2(630f, 20f),
    new Vector2(630f, 54f),
    new Vector2(600f, 58f)));
pathFigure.Segments.Add(new QuadraticBezierSegment(
    new Vector2(575f, 58f),
    pathFigure.StartPoint));
path.Figures.Add(pathFigure);
drawing.DrawPath(
    new SolidColorBrush(new Vector4(0.16f, 0.92f, 0.66f, 1f)),
    null,
    path);
drawing.DrawPolyline(
    new Pen(
        new SolidColorBrush(new Vector4(0.16f, 0.92f, 0.66f, 1f)),
        5f,
        PenLineJoin.Round,
        4f,
        PenLineCap.Round,
        PenLineCap.Triangle,
        PenLineCap.Square,
        [2.0, 1.0],
        0.25,
        PenStrokeTransformMode.Fixed),
    [new(48f, 210f), new(208f, 198f), new(320f, 216f), new(560f, 208f)]);
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
var sampleFont = InterFontFamily.Regular;
ushort[] sampleGlyphs =
[
    sampleFont.GetGlyphIndex('G'),
    sampleFont.GetGlyphIndex('P'),
    sampleFont.GetGlyphIndex('U')
];
drawing.DrawGlyphRun(
    sampleGlyphs,
    [new(0f, 0f), new(30f, 0f), new(58f, 0f)],
    sampleFont,
    38f,
    new SolidColorBrush(new Vector4(0.96f, 0.98f, 1f, 1f)),
    new Vector2(442f, 178f),
    isBold: true,
    textRenderingMode: TextRenderingMode.Grayscale,
    preferGlyphAtlas: true);
ushort[] secondGlyphs =
[
    sampleFont.GetGlyphIndex('C'),
    sampleFont.GetGlyphIndex('+'),
    sampleFont.GetGlyphIndex('+')
];
drawing.DrawGlyphRun(
    secondGlyphs,
    [new(0f, 0f), new(27f, 0f), new(51f, 0f)],
    sampleFont,
    30f,
    new SolidColorBrush(new Vector4(0.96f, 0.98f, 1f, 1f)),
    new Vector2(82f, 152f),
    textRenderingMode: TextRenderingMode.Grayscale,
    preferGlyphAtlas: true);
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
if (updateMetrics.CommandCount != 13U ||
    updateMetrics.ResourceCount != 13U ||
    updateMetrics.DrawCount != 9U ||
    updateMetrics.MaximumStackDepth != 2U ||
    compiled.SourceCommandCount != 16 ||
    compiled.NativeCommandCount != 13 ||
    compiled.NativeDrawCount != 9 ||
    compiled.PathCount != 1 ||
    compiled.PathSegmentCount != 3 ||
    compiled.StrokeCount != 1 ||
    compiled.StrokePointCount != 4 ||
    compiled.StrokeDoubleCount != 2 ||
    compiled.GlyphOutlineCount != 5 ||
    compiled.PositionedGlyphCount != 9 ||
    compiled.TextStyleCount != 1 ||
    compiled.BrushCount != 8 ||
    compiled.GradientStopCount != 2)
{
    throw new InvalidOperationException(
        $"The compiled managed picture contract is invalid: {updateMetrics}; " +
        $"source={compiled.SourceCommandCount}, native={compiled.NativeCommandCount}, " +
        $"draws={compiled.NativeDrawCount}, paths={compiled.PathCount}/" +
        $"{compiled.PathSegmentCount}, strokes={compiled.StrokeCount}/" +
        $"{compiled.StrokePointCount}/{compiled.StrokeDoubleCount}, " +
        $"glyphs={compiled.GlyphOutlineCount}/" +
        $"{compiled.GlyphSegmentCount}/{compiled.PositionedGlyphCount}, " +
        $"styles={compiled.TextStyleCount}, " +
        $"brushes={compiled.BrushCount}, stops={compiled.GradientStopCount}.");
}
NativeSceneFrameMetrics metrics = compositor.RenderScene(
    target,
    dpiScale: 1f,
    sceneId,
    sceneGeneration,
    new Vector4(0.02f, 0.025f, 0.04f, 1f));
context.WaitIdle();
Console.WriteLine(
    $"[ProGPUNativeManaged] first retained frame submitted " +
    $"({metrics.SubmissionCount} native submissions, " +
    $"{metrics.VertexUploadBytes} vertex bytes, " +
    $"{metrics.CoverageStagingBytes} coverage bytes); " +
    "validating post-build buffer allocation.");
using (var allocationProbe = new GpuBuffer(
    context,
    4,
    BufferUsage.CopySrc | BufferUsage.CopyDst,
    "ProGPU managed post-native-render allocation probe"))
{
    allocationProbe.WriteBytes([1, 2, 3, 4]);
    context.PollDevice(wait: false);
}
Console.WriteLine(
    "[ProGPUNativeManaged] post-build general buffer allocation passed; " +
    "validating readback heap allocation.");
_ = target.ReadPixels();
Console.WriteLine("[ProGPUNativeManaged] post-build readback passed.");
metrics = compositor.RenderScene(
    target,
    dpiScale: 1f,
    sceneId,
    sceneGeneration,
    new Vector4(0.02f, 0.025f, 0.04f, 1f));
if (metrics.VertexUploadBytes != 0U ||
    metrics.IndexUploadBytes != 0U ||
    metrics.BrushUploadBytes != 0U ||
    metrics.GradientStopUploadBytes != 0U ||
    metrics.TextStyleUploadBytes != 0U ||
    metrics.CoverageStagingBytes != 0U)
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
    var pathCenter = Pixel(600, 40);
    var background = Pixel(10, 10);
    int brightTextPixels = 0;
    for (int y = 138; y < 184; y++)
    {
        for (int x = 438; x < 548; x++)
        {
            ReadOnlySpan<byte> textPixel = Pixel(x, y);
            if (textPixel[0] > 220 && textPixel[1] > 220 &&
                textPixel[2] > 220)
            {
                brightTextPixels++;
            }
        }
    }
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
        pathCenter[1] > 180 && pathCenter[2] > 120 &&
        brightTextPixels > 40 &&
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
