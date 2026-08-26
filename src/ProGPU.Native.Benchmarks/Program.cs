using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Text;
using ProGPU.Vector;
using Silk.NET.WebGPU;

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--directx-hello-texture-oracle",
            StringComparison.OrdinalIgnoreCase)))
{
    DirectXHelloTextureQualification.Run(args);
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--directx-hello-triangle-oracle",
            StringComparison.OrdinalIgnoreCase)))
{
    DirectXHelloTriangleQualification.Run(args);
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--text-shaping",
            StringComparison.OrdinalIgnoreCase)))
{
    NativeTextShapingBenchmark.Run(args);
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--semantic-local-cache-fant",
            StringComparison.OrdinalIgnoreCase)))
{
    RetainedCacheFantQualification.Run();
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--semantic-local-cache-multi-guideline",
            StringComparison.OrdinalIgnoreCase)))
{
    RetainedCacheMultiGuidelineQualification.Run();
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--semantic-per-point-path-guideline",
            StringComparison.OrdinalIgnoreCase)))
{
    PerPointPathGuidelineQualification.Run();
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--semantic-nested-cache-effect",
            StringComparison.OrdinalIgnoreCase)))
{
    RetainedNestedCacheEffectQualification.Run();
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--semantic-cache-mask-effect",
            StringComparison.OrdinalIgnoreCase)))
{
    RetainedCacheMaskEffectQualification.Run();
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--semantic-cache-effect-clip",
            StringComparison.OrdinalIgnoreCase)))
{
    RetainedCacheEffectClipQualification.Run();
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--semantic-bounded-effect",
            StringComparison.OrdinalIgnoreCase)))
{
    RetainedBoundedEffectQualification.Run();
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--semantic-uncached-opacity-effect",
            StringComparison.OrdinalIgnoreCase)))
{
    RetainedOpacityEffectQualification.Run();
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--managed-picture",
            StringComparison.OrdinalIgnoreCase)))
{
    ManagedPictureBenchmark.Run(args);
    return;
}

if (Array.Exists(
        args,
        static value => string.Equals(
            value,
            "--semantic-local-cache-brush-mask",
            StringComparison.OrdinalIgnoreCase)))
{
    RetainedCacheMaskQualification.Run();
    return;
}

const uint width = 960;
const uint height = 540;
int rectangleCount = ReadPositiveArgument("--rectangles", 384);
int warmupCount = ReadNonNegativeArgument("--warmup", 60);
int iterationCount = ReadPositiveArgument("--iterations", 300);
float dpiScale = ReadPositiveFloatArgument("--dpi", 1f);
float logicalWidth = width / dpiScale;
float logicalHeight = height / dpiScale;
bool synchronizeEachFrame = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--sync",
        StringComparison.OrdinalIgnoreCase));
bool drainEachPair = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--drain-each-pair",
        StringComparison.OrdinalIgnoreCase));
bool groupMeasurements = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--grouped",
        StringComparison.OrdinalIgnoreCase));
bool managedGroupFirst = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--managed-first",
        StringComparison.OrdinalIgnoreCase));
string? outputJsonPath = ReadStringArgument("--output-json");
if (drainEachPair && (synchronizeEachFrame || groupMeasurements))
{
    throw new ArgumentException(
        "--drain-each-pair cannot be combined with --sync or --grouped.");
}
bool useAnalyticScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--analytic",
        StringComparison.OrdinalIgnoreCase));
bool useGeometryScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--geometry",
        StringComparison.OrdinalIgnoreCase));
bool useCurveGeometryScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--geometry-curves",
        StringComparison.OrdinalIgnoreCase));
bool usePolylineGeometryScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--geometry-polylines",
        StringComparison.OrdinalIgnoreCase));
bool useSplineGeometryScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--geometry-splines",
        StringComparison.OrdinalIgnoreCase));
bool useDashedGeometryScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--geometry-dashes",
        StringComparison.OrdinalIgnoreCase));
bool usePathScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--paths",
        StringComparison.OrdinalIgnoreCase));
bool useGlyphScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--glyphs",
        StringComparison.OrdinalIgnoreCase));
bool rerasterizeGlyphs = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--rerasterize-glyphs",
        StringComparison.OrdinalIgnoreCase));
if (rerasterizeGlyphs && !useGlyphScene)
{
    throw new ArgumentException(
        "--rerasterize-glyphs requires --glyphs.");
}
bool useImageScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--images",
        StringComparison.OrdinalIgnoreCase));
bool useSemanticLayerEffects = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--semantic-layer-effects",
        StringComparison.OrdinalIgnoreCase));
bool useSemanticScene = useSemanticLayerEffects || Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--semantic-scene",
        StringComparison.OrdinalIgnoreCase));
bool useExternalImageScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--external-images",
        StringComparison.OrdinalIgnoreCase));
bool useMaskedImageScene = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--masked-images",
        StringComparison.OrdinalIgnoreCase));
useExternalImageScene |= useMaskedImageScene;
useImageScene |= useExternalImageScene;
bool forceAtlasGrowth = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--atlas-growth",
        StringComparison.OrdinalIgnoreCase));
if (forceAtlasGrowth && !useGlyphScene && !usePathScene)
{
    throw new ArgumentException("--atlas-growth requires --glyphs or --paths.");
}
bool enableManagedCompiledSceneCache = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--managed-compiled-scene-cache",
        StringComparison.OrdinalIgnoreCase));
useGeometryScene |= useCurveGeometryScene || usePolylineGeometryScene ||
    useSplineGeometryScene || useDashedGeometryScene;
bool writeImages = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--write-images",
        StringComparison.OrdinalIgnoreCase));
bool useDrawState = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--draw-state",
        StringComparison.OrdinalIgnoreCase));
bool useGroupOpacity = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--group-opacity",
        StringComparison.OrdinalIgnoreCase));
string? groupBlendModeArgument = ReadStringArgument("--group-blend-mode");
bool useGroupBlend = groupBlendModeArgument is not null;
GpuBlendMode groupBlendMode = GpuBlendMode.SrcOver;
if (useGroupBlend &&
    (!Enum.TryParse(groupBlendModeArgument, ignoreCase: true, out groupBlendMode) ||
     !Enum.IsDefined(groupBlendMode)))
{
    throw new ArgumentException(
        $"Unknown --group-blend-mode value '{groupBlendModeArgument}'.");
}
bool useGaussianGroupEffect = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--group-gaussian-blur",
        StringComparison.OrdinalIgnoreCase));
bool useBoxGroupEffect = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--group-box-blur",
        StringComparison.OrdinalIgnoreCase));
bool useDropShadowGroupEffect = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--group-drop-shadow",
        StringComparison.OrdinalIgnoreCase));
bool useGroupEffectChain = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--group-effect-chain",
        StringComparison.OrdinalIgnoreCase));
if ((useGaussianGroupEffect ? 1 : 0) +
    (useBoxGroupEffect ? 1 : 0) +
    (useDropShadowGroupEffect ? 1 : 0) +
    (useGroupEffectChain ? 1 : 0) > 1)
{
    throw new ArgumentException("Select only one retained group effect.");
}
bool useGroupEffect = useGaussianGroupEffect || useBoxGroupEffect ||
    useDropShadowGroupEffect || useGroupEffectChain;
bool recomputeGroupEffect = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--recompute-group-effect",
        StringComparison.OrdinalIgnoreCase));
if (recomputeGroupEffect && !useGroupEffect)
{
    throw new ArgumentException(
        "--recompute-group-effect requires a retained group effect.");
}
if (recomputeGroupEffect && useBoxGroupEffect)
{
    throw new ArgumentException(
        "The independent box-blur CPU oracle is a final-output gate, not a timed managed effect implementation.");
}
if (recomputeGroupEffect && useGroupBlend)
{
    throw new ArgumentException(
        "The initial matched group-blend lane does not combine effect recomputation.");
}
float gaussianSigma = ReadPositiveFloatArgument("--blur-sigma", 2f);
Vector2 dropShadowOffset = new(7.5f, 5.25f);
Vector4 dropShadowColor = new(0.08f, 0.16f, 0.32f, 0.72f);
bool useTextureGroupMask = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--group-texture-mask",
        StringComparison.OrdinalIgnoreCase));
bool useRoundedGroupMask = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--group-rounded-mask",
        StringComparison.OrdinalIgnoreCase));
bool useVectorClipChain = Array.Exists(
    args,
    static value => string.Equals(
        value,
        "--group-vector-clip-chain",
        StringComparison.OrdinalIgnoreCase));
if ((useTextureGroupMask ? 1 : 0) +
    (useRoundedGroupMask ? 1 : 0) +
    (useVectorClipChain ? 1 : 0) > 1)
{
    throw new ArgumentException(
        "Select only one common group-mask benchmark mode.");
}
bool useGroupMask = useTextureGroupMask || useRoundedGroupMask ||
    useVectorClipChain;
int analyticKind = ReadArgument("--analytic-kind", -1);
int geometryKind = ReadArgument("--geometry-kind", -1);
int geometryLineMode = ReadArgument("--geometry-line-mode", -1);
int geometryStartCap = ReadArgument("--geometry-start-cap", -1);
int geometryEndCap = ReadArgument("--geometry-end-cap", -1);
int geometryJoin = ReadArgument("--geometry-join", -1);
if (useSemanticScene &&
    (useAnalyticScene || useGeometryScene || usePathScene || useGlyphScene ||
     useImageScene || forceAtlasGrowth || useDrawState || useGroupOpacity ||
     useGroupMask || useGroupBlend || useGroupEffect))
{
    throw new ArgumentException(
        "--semantic-scene is a complete mixed workload and cannot be " +
        "combined with a single-family, atlas-growth, draw-state, group, " +
        "blend, or effect mode.");
}

NativeSolidRectangle[] rectangles = CreateRectangles(
    rectangleCount,
    logicalWidth,
    logicalHeight);
NativeAnalyticPrimitive[] analyticPrimitives = useAnalyticScene
    ? CreateAnalyticPrimitives(
        rectangleCount,
        analyticKind,
        logicalWidth,
        logicalHeight)
    : [];
NativeGeometryPrimitive[] geometryPrimitives = useGeometryScene
    && !usePolylineGeometryScene
    && !useDashedGeometryScene
    && !useSplineGeometryScene
    ? CreateGeometryPrimitives(
        rectangleCount,
        geometryKind,
        geometryLineMode,
        geometryStartCap,
        geometryEndCap,
        useCurveGeometryScene,
        logicalWidth,
        logicalHeight)
    : [];
(NativePathFill[] nativePaths, NativePathSegment[] nativePathSegments) =
    usePathScene
        ? CreateNativePaths(
            rectangleCount,
            logicalWidth,
            logicalHeight,
            forceAtlasGrowth)
        : ([], []);
TtfFont? glyphFont = useGlyphScene || useSemanticScene
    ? InterFontFamily.Regular
    : null;
(NativeGlyphOutline[] nativeGlyphOutlines,
 NativePathSegment[] nativeGlyphSegments,
 NativePositionedGlyph[] nativeGlyphs,
 ushort[] managedGlyphIndices,
 Vector2[] managedGlyphPositions) = useGlyphScene
    ? CreateGlyphScene(
        glyphFont!,
        rectangleCount,
        dpiScale,
        logicalWidth,
        logicalHeight,
        forceAtlasGrowth)
    : ([], [], [], [], []);
(Vector2[] geometryPoints,
 NativePolyline[] geometryPolylines,
 double[] geometryDoubles,
 NativeDashStyle[] geometryDashStyles,
 NativeSpline[] geometrySplines) =
    useDashedGeometryScene
        ? CreateDashedPolylines(
            rectangleCount,
            geometryLineMode,
            geometryStartCap,
            geometryEndCap,
            geometryJoin,
            logicalWidth,
            logicalHeight)
        : useSplineGeometryScene
        ? CreateSplines(
            rectangleCount,
            geometryLineMode,
            geometryStartCap,
            geometryEndCap,
            geometryJoin,
            logicalWidth,
            logicalHeight)
        : usePolylineGeometryScene
        ? CreatePolylines(
            rectangleCount,
            geometryLineMode,
            geometryStartCap,
            geometryEndCap,
            geometryJoin,
            logicalWidth,
            logicalHeight)
        : ([], [], [], [], []);
int semanticFamilyCount = Math.Max(8, rectangleCount / 8);
float semanticWidth = logicalWidth * 0.5f;
float semanticHeight = logicalHeight * 0.5f;
NativeAnalyticPrimitive[] semanticAnalyticPrimitives = useSemanticScene
    ? CreateAnalyticPrimitives(
        semanticFamilyCount,
        forcedKind: -1,
        semanticWidth,
        semanticHeight)
    : [];
(NativeScenePathFill[] semanticPaths,
 NativePathSegment[] semanticPathSegments) = useSemanticScene
    ? CreateSemanticPaths(
        semanticFamilyCount,
        semanticWidth,
        semanticHeight,
        xOffset: semanticWidth)
    : ([], []);
(NativeSceneBrush[] semanticBrushes,
 uint[] semanticAnalyticBrushIndices,
 uint[] semanticPathBrushIndices) = useSemanticScene
    ? CreateSemanticSolidBrushes(semanticAnalyticPrimitives, semanticPaths)
    : ([], [], []);
(NativeSceneGlyphOutline[] semanticGlyphOutlines,
 NativePathSegment[] semanticGlyphSegments,
 NativePositionedGlyph[] semanticGlyphs,
 ushort[] semanticManagedGlyphIndices,
 Vector2[] semanticManagedGlyphPositions) = useSemanticScene
    ? CreateSemanticGlyphScene(
        glyphFont!,
        semanticFamilyCount,
        dpiScale,
        semanticWidth,
        semanticHeight,
        yOffset: semanticHeight)
    : ([], [], [], [], []);
NativeSceneTextStyle[] semanticTextStyles = useSemanticScene
    ? [new NativeSceneTextStyle(
        new Vector4(0.92f, 0.96f, 1f, 1f),
        NativeSceneTextRenderingMode.Grayscale)]
    : [];
Vector4 clearColor = useBoxGroupEffect
    ? Vector4.Zero
    : new Vector4(0.015f, 0.02f, 0.035f, 1f);
NativeImageRect drawClip = new(
    logicalWidth * 0.2f,
    logicalHeight * 0.15f,
    logicalWidth * 0.55f,
    logicalHeight * 0.65f);
const float benchmarkGroupOpacity = 0.625f;
NativeDrawState nativeDrawState = default;
bool effectTimingActive = false;
uint nativeEffectTimingRevision = 100U;
int nativeEffectTimingIndex = 0;
int managedEffectTimingIndex = 0;
uint nativeVertexCount = 0;
uint nativeIndexCount = 0;
int managedVertexCount = 0;
int managedIndexCount = 0;
uint nativeRasterizedPathCount = 0;
ulong nativePathUploadBytes = 0;
ulong nativeCoverageStagingBytes = 0;
uint nativeRasterizedGlyphCount = 0;
ulong nativeGlyphOutlineUploadBytes = 0;
ulong nativeGlyphInstanceUploadBytes = 0;
uint nativeAtlasWidth = 0;
uint nativeAtlasGeneration = 0;
uint nativeAtlasGrowthCount = 0;
NativeGlyphFrameMetrics lastNativeGlyphMetrics = default;
uint nativeGlyphContentRevision = 1U;
NativePathFrameMetrics lastNativePathMetrics = default;
NativeGeometryFrameMetrics lastNativeGeometryMetrics = default;
NativeImageFrameMetrics lastNativeImageMetrics = default;
NativeSceneUpdateMetrics nativeSceneUpdateMetrics = default;
NativeSceneFrameMetrics lastNativeSceneMetrics = default;
const uint benchmarkImageWidth = 192U;
const uint benchmarkImageHeight = 128U;
byte[] imagePixels = useImageScene || useSemanticScene
    ? CreateImagePixels(benchmarkImageWidth, benchmarkImageHeight)
    : [];
byte[] imageMaskPixels = useMaskedImageScene || useTextureGroupMask
    ? CreateImageMaskPixels(benchmarkImageWidth, benchmarkImageHeight)
    : [];
bool nativeImageUploaded = false;
ulong nativeImageTextureUploadBytes = 0UL;
uint nativeImageTextureGeneration = 0U;

using var context = new WgpuContext();
context.Initialize(window: null);
using var nativeTarget = CreateTarget(context, "Native benchmark target");
using var managedTarget = CreateTarget(context, "Managed benchmark target");
using GpuTexture? managedBlendSourceTarget = useGroupBlend
    ? CreateTarget(context, "Managed retained group-blend source")
    : null;
using GpuTexture? managedImageTexture = useImageScene || useSemanticScene
    ? new GpuTexture(
        context,
        benchmarkImageWidth,
        benchmarkImageHeight,
        TextureFormat.Rgba8Unorm,
        TextureUsage.TextureBinding | TextureUsage.CopyDst,
        "Managed retained RGBA benchmark image",
        alphaMode: GpuTextureAlphaMode.Straight)
    : null;
managedImageTexture?.WritePixels<byte>(imagePixels);
using GpuTexture? managedImageMaskTexture = useMaskedImageScene ||
    useTextureGroupMask
    ? new GpuTexture(
        context,
        benchmarkImageWidth,
        benchmarkImageHeight,
        TextureFormat.Rgba8Unorm,
        TextureUsage.TextureBinding | TextureUsage.CopyDst,
        "Managed retained RGBA benchmark image mask",
        alphaMode: GpuTextureAlphaMode.Straight)
    : null;
managedImageMaskTexture?.WritePixels<byte>(imageMaskPixels);
(NativeClipChain Native, PathGeometry Managed) vectorClipChain =
    useVectorClipChain
        ? CreateVectorClipChain(logicalWidth, logicalHeight)
        : default;
NativeGroupMask nativeGroupMask = useTextureGroupMask
    ? NativeGroupMask.TextureMask(
        managedImageMaskTexture!,
        new NativeImageRect(
            60f,
            40f,
            logicalWidth - 120f,
            logicalHeight - 80f),
        NativeImageSampling.Linear,
        revision: 1U)
    : useRoundedGroupMask
    ? NativeGroupMask.RoundedRectangle(
        new NativeImageRect(
            60f,
            40f,
            logicalWidth - 120f,
            logicalHeight - 80f),
        Matrix3x2.Identity,
        new Vector4(36f),
        new Vector4(36f))
    : useVectorClipChain
    ? NativeGroupMask.VectorClipChain(vectorClipChain.Native, revision: 1U)
    : default;
NativeGroupEffect nativeGroupEffect = useGaussianGroupEffect
    ? NativeGroupEffect.GaussianBlur(gaussianSigma, revision: 1U)
    : useBoxGroupEffect
    ? NativeGroupEffect.BoxBlur(gaussianSigma, revision: 1U)
    : useDropShadowGroupEffect
    ? NativeGroupEffect.DropShadow(
        gaussianSigma,
        dropShadowOffset,
        dropShadowColor,
        revision: 1U)
    : default;
NativeGroupEffectChain? nativeGroupEffectChain = useGroupEffectChain
    ? new NativeGroupEffectChain(
        [
            NativeGroupEffect.GaussianBlur(gaussianSigma, revision: 1U),
            NativeGroupEffect.DropShadow(
                gaussianSigma,
                dropShadowOffset,
                dropShadowColor,
                revision: 1U)
        ],
        revision: 1U)
    : null;
NativeGroupEffectChain? timedEffectChainA = useGroupEffectChain
    ? new NativeGroupEffectChain(
        [
            NativeGroupEffect.GaussianBlur(gaussianSigma, revision: 100U),
            NativeGroupEffect.DropShadow(
                gaussianSigma,
                dropShadowOffset,
                dropShadowColor,
                revision: 100U)
        ],
        revision: 100U)
    : null;
NativeGroupEffectChain? timedEffectChainB = useGroupEffectChain
    ? new NativeGroupEffectChain(
        [
            NativeGroupEffect.GaussianBlur(gaussianSigma, revision: 101U),
            NativeGroupEffect.DropShadow(
                gaussianSigma,
                dropShadowOffset + new Vector2(0.25f, 0f),
                dropShadowColor,
                revision: 101U)
        ],
        revision: 101U)
    : null;
nativeDrawState = useDrawState || useGroupOpacity || useGroupMask ||
    useGroupBlend ||
    useGroupEffect
    ? nativeGroupEffectChain is not null
        ? new NativeDrawState(
            useDrawState ? 0.625f : 1f,
            useDrawState ? drawClip : default,
            useDrawState
                ? NativeDrawStateFlags.ClipRect
                : NativeDrawStateFlags.None,
            useGroupOpacity ? benchmarkGroupOpacity : 1f,
            1U,
            nativeGroupMask,
            nativeGroupEffectChain)
        : new NativeDrawState(
            useDrawState ? 0.625f : 1f,
            useDrawState ? drawClip : default,
            useDrawState
                ? NativeDrawStateFlags.ClipRect
                : NativeDrawStateFlags.None,
            useGroupOpacity ? benchmarkGroupOpacity : 1f,
            useGroupOpacity || useGroupMask || useGroupEffect || useGroupBlend
                ? 1U
                : 0U,
            nativeGroupMask,
            nativeGroupEffect)
    : default;
if (useGroupBlend)
{
    nativeDrawState = nativeDrawState.WithGroupBlendMode(groupBlendMode);
}
// Declare the native compositor after the borrowed source so reverse-order
// disposal releases its retained view before destroying the source texture.
using var native = new NativeCompositor(context, TextureFormat.Rgba8Unorm);
const ulong semanticSceneId = 0x53454D414E544943UL;
const ulong semanticSceneGeneration = 1UL;
byte[] semanticSceneBuffer = useSemanticScene
    ? new byte[GetSemanticSceneBufferSize(
        semanticAnalyticPrimitives,
        semanticPaths,
        semanticPathSegments,
        semanticGlyphOutlines,
        semanticGlyphSegments,
        semanticGlyphs,
        imagePixels,
        semanticBrushes,
        semanticAnalyticBrushIndices,
        semanticPathBrushIndices,
        semanticTextStyles,
        useSemanticLayerEffects)]
    : [];
int semanticSceneLength = 0;
uint expectedSemanticCommandCount = useSemanticLayerEffects ? 10U : 8U;
uint expectedSemanticResourceCount = useSemanticLayerEffects ? 12U : 10U;
if (useSemanticScene)
{
    var semanticImageDraw = new NativeSceneImageDraw(
        benchmarkImageWidth,
        benchmarkImageHeight,
        benchmarkImageWidth * 4U,
        NativeImageSampling.Nearest,
        new NativeImageRect(
            0f,
            0f,
            benchmarkImageWidth,
            benchmarkImageHeight),
        new NativeImageRect(
            semanticWidth + 80f,
            semanticHeight + 60f,
            semanticWidth - 160f,
            semanticHeight - 120f),
        Matrix3x2.Identity,
        1f);
    semanticSceneLength = BuildSemanticScene(
        semanticSceneBuffer,
        semanticSceneId,
        semanticSceneGeneration,
        semanticAnalyticPrimitives,
        semanticPaths,
        semanticPathSegments,
        semanticGlyphOutlines,
        semanticGlyphSegments,
        semanticGlyphs,
        imagePixels,
        semanticBrushes,
        semanticAnalyticBrushIndices,
        semanticPathBrushIndices,
        semanticTextStyles,
        in semanticImageDraw,
        logicalWidth,
        logicalHeight,
        useSemanticLayerEffects,
        gaussianSigma,
        dropShadowOffset,
        dropShadowColor);
    nativeSceneUpdateMetrics = native.UpdateScene(
        semanticSceneBuffer.AsSpan(0, semanticSceneLength));
    NativeSceneUpdateMetrics retainedUpdate = native.UpdateScene(
        semanticSceneBuffer.AsSpan(0, semanticSceneLength));
    if (nativeSceneUpdateMetrics.CommandCount != expectedSemanticCommandCount ||
        nativeSceneUpdateMetrics.ResourceCount != expectedSemanticResourceCount ||
        nativeSceneUpdateMetrics.DrawCount != 8U ||
        nativeSceneUpdateMetrics.SnapshotReused ||
        !retainedUpdate.SnapshotReused ||
        retainedUpdate.SnapshotBytes != nativeSceneUpdateMetrics.SnapshotBytes)
    {
        throw new InvalidOperationException(
            "The retained mixed semantic-scene snapshot contract was not met: " +
            $"initial={nativeSceneUpdateMetrics}, retained={retainedUpdate}.");
    }
}
using var managed = new Compositor(
    context,
    TextureFormat.Rgba8Unorm,
    CompositorOptions.Default with
    {
        // The DrawingVisual already retains its command compilation. Keep the
        // additional whole-scene cache opt-in so its overhead can be measured
        // independently instead of changing the established baseline.
        EnableCompiledSceneCache = enableManagedCompiledSceneCache,
        EnableGpuHitTesting = false,
        PrimarySampleCount = 1
    });
DrawingVisual managedVisual = useImageScene
    ? CreateManagedImageVisual(
        managedImageTexture!,
        managedImageMaskTexture,
        benchmarkImageWidth,
        benchmarkImageHeight,
        logicalWidth,
        logicalHeight)
    : useGlyphScene
    ? CreateManagedGlyphVisual(
        glyphFont!,
        managedGlyphIndices,
        managedGlyphPositions,
        logicalWidth,
        logicalHeight)
    : usePathScene
    ? CreateManagedPathVisual(
        rectangleCount,
        logicalWidth,
        logicalHeight,
        forceAtlasGrowth)
    : useGeometryScene
    ? CreateManagedGeometryVisual(
        geometryPrimitives,
        geometryPoints,
        geometryPolylines,
        geometryDoubles,
        geometryDashStyles,
        geometrySplines,
        logicalWidth,
        logicalHeight)
    : useAnalyticScene
        ? CreateManagedAnalyticVisual(
        analyticPrimitives,
        logicalWidth,
        logicalHeight)
        : CreateManagedVisual(rectangles, logicalWidth, logicalHeight);
Visual managedContentRoot = useSemanticScene
    ? CreateManagedSemanticScene(
        semanticAnalyticPrimitives,
        semanticManagedGlyphIndices,
        semanticManagedGlyphPositions,
        glyphFont!,
        managedImageTexture!,
        benchmarkImageWidth,
        benchmarkImageHeight,
        semanticFamilyCount,
        semanticWidth,
        semanticHeight,
        logicalWidth,
        logicalHeight)
    : managedVisual;
BlurEffect? managedBlurEffect = null;
DropShadowEffect? managedDropShadowEffect = null;
if (useDrawState)
{
    var clip = new Rect(
        drawClip.X,
        drawClip.Y,
        drawClip.Width,
        drawClip.Height);
    managedVisual.Context.Commands.Insert(0, new RenderCommand
    {
        Type = RenderCommandType.PushClip,
        Rect = clip
    });
    managedVisual.Context.Commands.Insert(1, new RenderCommand
    {
        Type = RenderCommandType.PushOpacity,
        FontSize = 0.625f
    });
    managedVisual.Context.Commands.Add(new RenderCommand
    {
        Type = RenderCommandType.PopOpacity
    });
    managedVisual.Context.Commands.Add(new RenderCommand
    {
        Type = RenderCommandType.PopClip
    });
}
if (useGroupOpacity)
{
    managedVisual.CacheAsLayer = true;
    managedVisual.Opacity = benchmarkGroupOpacity;
}
if (useTextureGroupMask)
{
    managedVisual.OpacityMask = new GpuTextureBrush
    {
        Texture = managedImageMaskTexture,
        SourceRect = new Rect(
            0f,
            0f,
            benchmarkImageWidth,
            benchmarkImageHeight),
        DestinationRect = new Rect(
            60f,
            40f,
            logicalWidth - 120f,
            logicalHeight - 80f),
        SamplingMode = TextureSamplingMode.Linear
    };
    managedVisual.OpacityMaskBounds = new Rect(
        60f,
        40f,
        logicalWidth - 120f,
        logicalHeight - 80f);
}
else if (useRoundedGroupMask)
{
    managedVisual.GeometryClip = PrimitivePathGeometry.CreateRoundedRectangle(
        60f,
        40f,
        logicalWidth - 120f,
        logicalHeight - 80f,
        36f,
        36f);
}
else if (useVectorClipChain)
{
    managedVisual.GeometryClip = vectorClipChain.Managed;
}
if (useGaussianGroupEffect || useGroupEffectChain)
{
    managedBlurEffect = new BlurEffect(gaussianSigma);
    managedVisual.Effect = managedBlurEffect;
    managedVisual.EffectContentBounds = new Rect(
        0f,
        0f,
        logicalWidth,
        logicalHeight);
    managedVisual.EffectRasterPadding = 0f;
}
else if (useDropShadowGroupEffect)
{
    managedDropShadowEffect = new DropShadowEffect(
        gaussianSigma,
        dropShadowOffset,
        dropShadowColor);
    managedVisual.Effect = managedDropShadowEffect;
    managedVisual.EffectContentBounds = new Rect(
        0f,
        0f,
        logicalWidth,
        logicalHeight);
    managedVisual.EffectRasterPadding = 0f;
}
Visual managedRenderRoot = managedContentRoot;
if (useSemanticLayerEffects)
{
    managedBlurEffect = new BlurEffect(gaussianSigma);
    managedContentRoot.Effect = managedBlurEffect;
    managedContentRoot.EffectContentBounds = new Rect(
        0f,
        0f,
        logicalWidth,
        logicalHeight);
    managedContentRoot.EffectRasterPadding = 0f;

    var semanticShadowVisual = new ContainerVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    semanticShadowVisual.AddChild(managedContentRoot);
    managedDropShadowEffect = new DropShadowEffect(
        gaussianSigma,
        dropShadowOffset,
        dropShadowColor);
    semanticShadowVisual.Effect = managedDropShadowEffect;
    semanticShadowVisual.EffectContentBounds = new Rect(
        0f,
        0f,
        logicalWidth,
        logicalHeight);
    semanticShadowVisual.EffectRasterPadding = 0f;

    var semanticMaskVisual = new ContainerVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight),
        GeometryClip = PrimitivePathGeometry.CreateRoundedRectangle(
            60f,
            40f,
            logicalWidth - 120f,
            logicalHeight - 80f,
            36f,
            36f)
    };
    semanticMaskVisual.AddChild(semanticShadowVisual);
    managedRenderRoot = semanticMaskVisual;
}
if (useGroupEffectChain)
{
    var outerEffectVisual = new ContainerVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    outerEffectVisual.AddChild(managedVisual);
    managedDropShadowEffect = new DropShadowEffect(
        gaussianSigma,
        dropShadowOffset,
        dropShadowColor);
    outerEffectVisual.Effect = managedDropShadowEffect;
    outerEffectVisual.EffectContentBounds = new Rect(
        0f,
        0f,
        logicalWidth,
        logicalHeight);
    outerEffectVisual.EffectRasterPadding = 0f;
    managedRenderRoot = outerEffectVisual;
}
Visual? managedBlendSourceRoot = null;
if (useGroupBlend)
{
    managedBlendSourceRoot = managedRenderRoot;
    var blendCompositeVisual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    blendCompositeVisual.Context.PushBlendMode(groupBlendMode);
    blendCompositeVisual.Context.DrawTexture(
        managedBlendSourceTarget!,
        new Rect(0f, 0f, logicalWidth, logicalHeight));
    blendCompositeVisual.Context.PopBlendMode();
    managedRenderRoot = blendCompositeVisual;

    managed.RenderOffscreen(
        managedBlendSourceRoot,
        Math.Max(1U, (uint)MathF.Round(logicalWidth)),
        Math.Max(1U, (uint)MathF.Round(logicalHeight)),
        managedBlendSourceTarget!,
        padding: 0f,
        dpiScale,
        Vector4.Zero);
    context.PollDevice(wait: true);
}

// A growth gate first establishes the ordinary 1024-square resource, then
// changes revision to a larger retained set. This exercises transactional
// texture/view/bind-group replacement rather than merely creating a large
// first atlas.
if (forceAtlasGrowth && useGlyphScene)
{
    var seed = CreateGlyphScene(
        glyphFont!,
        96,
        dpiScale,
        logicalWidth,
        logicalHeight,
        forceUniqueOutlines: false);
    native.RenderGlyphs(
        nativeTarget,
        dpiScale,
        seed.Outlines,
        seed.Segments,
        seed.Glyphs,
        clearColor,
        capturePayloadHash: false,
        contentRevision: uint.MaxValue);
    context.PollDevice(wait: true);
}
else if (forceAtlasGrowth && usePathScene)
{
    var seed = CreateNativePaths(
        96,
        logicalWidth,
        logicalHeight,
        forceUniqueOutlines: false);
    native.RenderPaths(
        nativeTarget,
        dpiScale,
        seed.Paths,
        seed.Segments,
        clearColor,
        capturePayloadHash: false,
        contentRevision: uint.MaxValue);
    context.PollDevice(wait: true);
}

// Compile both shader/pipeline paths before correctness or timing evidence.
RenderNative();
uint coldAtlasGeneration = useGlyphScene
    ? lastNativeGlyphMetrics.AtlasGeneration
    : lastNativePathMetrics.AtlasGeneration;
RenderManaged();
context.PollDevice(wait: true);

// Prove that a state-only opacity mutation reuses retained geometry, atlas,
// shaping, and image resources under the same content revision.
if (useDrawState &&
    (useGeometryScene || usePathScene || useGlyphScene || useImageScene))
{
    NativeDrawState originalDrawState = nativeDrawState;
    nativeDrawState = nativeGroupEffectChain is not null
        ? new NativeDrawState(
            0.5f,
            drawClip,
            NativeDrawStateFlags.ClipRect,
            useGroupOpacity ? benchmarkGroupOpacity : 1f,
            1U,
            nativeGroupMask,
            nativeGroupEffectChain)
        : new NativeDrawState(
            0.5f,
            drawClip,
            NativeDrawStateFlags.ClipRect,
            useGroupOpacity ? benchmarkGroupOpacity : 1f,
            useGroupOpacity || useGroupMask || useGroupEffect ? 1U : 0U,
            nativeGroupMask,
            nativeGroupEffect);
    RenderNative();
    if (useGeometryScene &&
        (lastNativeGeometryMetrics.VertexUploadBytes != 0UL ||
         lastNativeGeometryMetrics.IndexUploadBytes != 0UL ||
         lastNativeGeometryMetrics.BrushUploadBytes == 0UL))
    {
        throw new InvalidOperationException(
            "State-only geometry opacity rebuilt geometry instead of updating brushes.");
    }
    if (usePathScene &&
        (lastNativePathMetrics.RasterizedPathCount != 0U ||
         lastNativePathMetrics.PathUploadBytes != 0UL ||
         lastNativePathMetrics.VertexUploadBytes != 0UL ||
         lastNativePathMetrics.IndexUploadBytes != 0UL ||
         lastNativePathMetrics.BrushUploadBytes == 0UL ||
         lastNativePathMetrics.CoverageStagingBytes != 0UL))
    {
        throw new InvalidOperationException(
            "State-only path opacity rerasterized or rebuilt retained paths.");
    }
    if (useGlyphScene &&
        (lastNativeGlyphMetrics.RasterizedGlyphCount != 0U ||
         lastNativeGlyphMetrics.OutlineUploadBytes != 0UL ||
         lastNativeGlyphMetrics.InstanceUploadBytes == 0UL ||
         lastNativeGlyphMetrics.CoverageStagingBytes != 0UL))
    {
        throw new InvalidOperationException(
            "State-only glyph opacity rerasterized outlines instead of updating instances.");
    }
    if (useImageScene &&
        (lastNativeImageMetrics.TextureUploadBytes != 0UL ||
         lastNativeImageMetrics.VertexUploadBytes == 0UL ||
         lastNativeImageMetrics.IndexUploadBytes != 0UL))
    {
        throw new InvalidOperationException(
            "State-only image opacity reuploaded retained texture resources.");
    }
    nativeDrawState = originalDrawState;
    RenderNative();
}

// A mask-only mutation must update only the final composite state. The
// retained family content revision intentionally remains unchanged.
if (useGroupMask)
{
    NativeDrawState originalDrawState = nativeDrawState;
    NativeGroupMask mutatedMask = useTextureGroupMask
        ? NativeGroupMask.TextureMask(
            managedImageMaskTexture!,
            new NativeImageRect(
                64f,
                40f,
                logicalWidth - 120f,
                logicalHeight - 80f),
            NativeImageSampling.Linear,
            revision: 1U)
        : useRoundedGroupMask
        ? NativeGroupMask.RoundedRectangle(
            new NativeImageRect(
                60f,
                40f,
                logicalWidth - 120f,
                logicalHeight - 80f),
            Matrix3x2.CreateTranslation(4f, 0f),
            new Vector4(36f),
            new Vector4(36f))
        : NativeGroupMask.VectorClipChain(
            vectorClipChain.Native,
            revision: 2U);
    nativeDrawState = originalDrawState.GroupEffectChain is { } effectChain
        ? new NativeDrawState(
            originalDrawState.Opacity,
            originalDrawState.ClipRect,
            originalDrawState.Flags,
            originalDrawState.GroupOpacity,
            originalDrawState.GroupRevision,
            mutatedMask,
            effectChain)
        : new NativeDrawState(
            originalDrawState.Opacity,
            originalDrawState.ClipRect,
            originalDrawState.Flags,
            originalDrawState.GroupOpacity,
            originalDrawState.GroupRevision,
            mutatedMask,
            originalDrawState.GroupEffect);
    RenderNative();
    NativeLayerMetrics mutatedLayerMetrics = native.GetLayerMetrics();
    bool validMutation = !mutatedLayerMetrics.CacheHit ||
        mutatedLayerMetrics.ContentPassCount != 0U ||
        mutatedLayerMetrics.CompositePassCount != 1U ||
        mutatedLayerMetrics.MaskKind != mutatedMask.Kind;
    validMutation |= useVectorClipChain
        ? mutatedLayerMetrics.ClipCacheHit ||
          mutatedLayerMetrics.ClipRasterizedPathCount == 0U ||
          mutatedLayerMetrics.ClipPassCount == 0U ||
          mutatedLayerMetrics.ClipPathUploadBytes == 0UL
        : mutatedLayerMetrics.MaskUniformUploadBytes != 96UL;
    if (validMutation)
    {
        throw new InvalidOperationException(
            "Mask-only mutation rebuilt retained content or did not update " +
            "exactly one common-mask uniform block.");
    }

    nativeDrawState = originalDrawState;
    RenderNative();
}

// An effect-only mutation reuses the retained family content while dispatching
// only the retained effect graph for the changed effect revision.
if (useGroupEffect)
{
    NativeDrawState originalDrawState = nativeDrawState;
    NativeGroupEffect mutatedEffect = useDropShadowGroupEffect
        ? NativeGroupEffect.DropShadow(
            gaussianSigma,
            dropShadowOffset + new Vector2(1f, 0f),
            dropShadowColor,
            revision: 2U)
        : useBoxGroupEffect
        ? NativeGroupEffect.BoxBlur(
            gaussianSigma + 1f,
            revision: 2U)
        : NativeGroupEffect.GaussianBlur(
            gaussianSigma + 1f,
            revision: 2U);
    NativeGroupEffectChain? mutatedEffectChain = useGroupEffectChain
        ? new NativeGroupEffectChain(
            [
                NativeGroupEffect.GaussianBlur(
                    gaussianSigma,
                    revision: 1U),
                NativeGroupEffect.DropShadow(
                    gaussianSigma,
                    dropShadowOffset + new Vector2(1f, 0f),
                    dropShadowColor,
                    revision: 2U)
            ],
            revision: 2U)
        : null;
    nativeDrawState = mutatedEffectChain is not null
        ? new NativeDrawState(
            originalDrawState.Opacity,
            originalDrawState.ClipRect,
            originalDrawState.Flags,
            originalDrawState.GroupOpacity,
            originalDrawState.GroupRevision,
            originalDrawState.GroupMask,
            mutatedEffectChain)
        : new NativeDrawState(
            originalDrawState.Opacity,
            originalDrawState.ClipRect,
            originalDrawState.Flags,
            originalDrawState.GroupOpacity,
            originalDrawState.GroupRevision,
            originalDrawState.GroupMask,
            mutatedEffect);
    RenderNative();
    NativeLayerMetrics mutatedEffectMetrics = native.GetLayerMetrics();
    if (!mutatedEffectMetrics.CacheHit ||
        mutatedEffectMetrics.ContentPassCount != 0U ||
        mutatedEffectMetrics.CompositePassCount != 1U ||
        mutatedEffectMetrics.EffectKind != (useGroupEffectChain
            ? NativeGroupEffectKind.DropShadow
            : mutatedEffect.Kind) ||
        mutatedEffectMetrics.EffectRevision != 2U ||
        mutatedEffectMetrics.EffectPassCount !=
            (useGroupEffectChain ? 5U : useDropShadowGroupEffect ? 3U : 2U) ||
        mutatedEffectMetrics.EffectCacheHit ||
        mutatedEffectMetrics.EffectUniformUploadBytes != 32UL)
    {
        throw new InvalidOperationException(
            "Effect-only mutation rebuilt retained content or did not " +
            "dispatch the expected retained compute graph.");
    }

    nativeDrawState = originalDrawState;
    RenderNative();
}

// Compare a second fully warmed submission, not the pipeline's first draw.
ulong nativePayloadHash = RenderNative(capturePayloadHash: true);
NativeLayerMetrics stableLayerMetrics = native.GetLayerMetrics();
if (useSemanticScene &&
    (lastNativeSceneMetrics.CommandCount != expectedSemanticCommandCount ||
     lastNativeSceneMetrics.DrawCallCount !=
        (useSemanticLayerEffects ? 1U : 8U) ||
     lastNativeSceneMetrics.FamilySwitchCount != 8U ||
     lastNativeSceneMetrics.SubmissionCount != 1UL ||
     lastNativeSceneMetrics.VertexUploadBytes != 0UL ||
     lastNativeSceneMetrics.IndexUploadBytes != 0UL ||
     lastNativeSceneMetrics.TextureUploadBytes != 0UL ||
     lastNativeSceneMetrics.UniformUploadBytes != 0UL ||
     lastNativeSceneMetrics.CoverageStagingBytes != 0UL ||
     lastNativeSceneMetrics.BrushUploadBytes != 0UL ||
     lastNativeSceneMetrics.GradientStopUploadBytes != 0UL ||
     lastNativeSceneMetrics.TextStyleUploadBytes != 0UL))
{
    throw new InvalidOperationException(
        "Stable mixed semantic-scene replay did not preserve one ordered " +
        "submission with retained analytic/path/glyph/image resources, " +
        "including distinct repeated path, glyph, and image payloads. " +
        $"commands={lastNativeSceneMetrics.CommandCount} " +
        $"draws={lastNativeSceneMetrics.DrawCallCount} " +
        $"families={lastNativeSceneMetrics.FamilySwitchCount} " +
        $"submissions={lastNativeSceneMetrics.SubmissionCount} " +
        $"vertexUpload={lastNativeSceneMetrics.VertexUploadBytes} " +
        $"indexUpload={lastNativeSceneMetrics.IndexUploadBytes} " +
        $"textureUpload={lastNativeSceneMetrics.TextureUploadBytes} " +
        $"brushUpload={lastNativeSceneMetrics.BrushUploadBytes} " +
        $"gradientStopUpload={lastNativeSceneMetrics.GradientStopUploadBytes} " +
        $"textStyleUpload={lastNativeSceneMetrics.TextStyleUploadBytes} " +
        $"coverage={lastNativeSceneMetrics.CoverageStagingBytes}.");
}
if (useSemanticLayerEffects &&
    (!stableLayerMetrics.CacheHit ||
     stableLayerMetrics.ContentPassCount != 0U ||
     stableLayerMetrics.CompositePassCount != 1U ||
     stableLayerMetrics.MaskKind != NativeGroupMaskKind.RoundedRectangle ||
     stableLayerMetrics.MaskBindGroupGeneration == 0U ||
     stableLayerMetrics.MaskUniformUploadBytes != 0UL ||
     stableLayerMetrics.EffectKind != NativeGroupEffectKind.DropShadow ||
     stableLayerMetrics.EffectRevision != 91U ||
     stableLayerMetrics.EffectChainRevision != 91U ||
     stableLayerMetrics.EffectCount != 2U ||
     stableLayerMetrics.EffectPassCount != 0U ||
     !stableLayerMetrics.EffectCacheHit ||
     stableLayerMetrics.EffectUniformUploadBytes != 0UL ||
     stableLayerMetrics.EffectTextureBytes != (ulong)width * height * 12UL))
{
    throw new InvalidOperationException(
        "Stable semantic mask/effect replay did not retain the completed " +
        $"GPU effect output: {stableLayerMetrics}.");
}
if (useGroupMask &&
    (!stableLayerMetrics.CacheHit ||
     stableLayerMetrics.ContentPassCount != 0U ||
     stableLayerMetrics.CompositePassCount != 1U ||
     stableLayerMetrics.MaskKind != nativeGroupMask.Kind ||
     !stableLayerMetrics.MaskBindGroupCacheHit ||
     stableLayerMetrics.MaskUniformUploadBytes != 0UL ||
     stableLayerMetrics.UniformUploadBytes != 0UL ||
     (useVectorClipChain &&
      (!stableLayerMetrics.ClipCacheHit ||
       stableLayerMetrics.ClipRasterizedPathCount != 0U ||
       stableLayerMetrics.ClipPassCount != 0U ||
       stableLayerMetrics.ClipPathUploadBytes != 0UL ||
       stableLayerMetrics.ClipCoverageStagingBytes != 0UL))))
{
    throw new InvalidOperationException(
        "Stable common-mask replay rebuilt content or uploaded composite state.");
}
if (useGroupEffect &&
    (!stableLayerMetrics.CacheHit ||
     stableLayerMetrics.ContentPassCount != 0U ||
     stableLayerMetrics.CompositePassCount != 1U ||
     stableLayerMetrics.EffectKind != (useGroupEffectChain
        ? NativeGroupEffectKind.DropShadow
        : nativeGroupEffect.Kind) ||
     stableLayerMetrics.EffectRevision != 1U ||
     stableLayerMetrics.EffectCount != (useGroupEffectChain ? 2U : 1U) ||
     stableLayerMetrics.EffectChainRevision != 1U ||
     stableLayerMetrics.EffectPassCount != 0U ||
     !stableLayerMetrics.EffectCacheHit ||
     stableLayerMetrics.EffectUniformUploadBytes != 0UL ||
     stableLayerMetrics.EffectTextureBytes != (ulong)width * height *
        (useGroupEffectChain ? 12UL : 8UL)))
{
    throw new InvalidOperationException(
        "Stable group-effect replay dispatched compute work or " +
        "rebuilt retained content.");
}
if (useGroupBlend &&
    (!stableLayerMetrics.CacheHit ||
     stableLayerMetrics.ContentPassCount != 0U ||
     stableLayerMetrics.CompositePassCount != 1U ||
     stableLayerMetrics.BlendMode != groupBlendMode ||
     stableLayerMetrics.BlendSourcePassCount != 0U ||
     !stableLayerMetrics.BlendPipelineCacheHit))
{
    throw new InvalidOperationException(
        "Stable group-blend replay rebuilt retained source or pipeline state.");
}
if (useDrawState && useGeometryScene &&
    (lastNativeGeometryMetrics.VertexUploadBytes != 0UL ||
     lastNativeGeometryMetrics.IndexUploadBytes != 0UL ||
     lastNativeGeometryMetrics.BrushUploadBytes != 0UL))
{
    throw new InvalidOperationException(
        "Stable native geometry draw state uploaded retained payload.");
}
if (useDrawState && usePathScene &&
    (lastNativePathMetrics.RasterizedPathCount != 0U ||
     lastNativePathMetrics.PathUploadBytes != 0UL ||
     lastNativePathMetrics.VertexUploadBytes != 0UL ||
     lastNativePathMetrics.IndexUploadBytes != 0UL ||
     lastNativePathMetrics.BrushUploadBytes != 0UL ||
     lastNativePathMetrics.CoverageStagingBytes != 0UL))
{
    throw new InvalidOperationException(
        "Stable native path draw state uploaded retained payload.");
}
if (useDrawState && useGlyphScene &&
    (lastNativeGlyphMetrics.RasterizedGlyphCount != 0U ||
     lastNativeGlyphMetrics.OutlineUploadBytes != 0UL ||
     lastNativeGlyphMetrics.InstanceUploadBytes != 0UL ||
     lastNativeGlyphMetrics.CoverageStagingBytes != 0UL))
{
    throw new InvalidOperationException(
        "Stable native glyph draw state uploaded retained payload.");
}
if (forceAtlasGrowth && useGlyphScene &&
    (lastNativeGlyphMetrics.AtlasGeneration != coldAtlasGeneration ||
     lastNativeGlyphMetrics.RasterizedGlyphCount != 0U ||
     lastNativeGlyphMetrics.OutlineUploadBytes != 0UL ||
     lastNativeGlyphMetrics.InstanceUploadBytes != 0UL ||
     lastNativeGlyphMetrics.CoverageStagingBytes != 0UL))
{
    throw new InvalidOperationException(
        "Stable native glyph replay changed the grown atlas or uploaded retained payload.");
}
if (forceAtlasGrowth && usePathScene &&
    (lastNativePathMetrics.AtlasGeneration != coldAtlasGeneration ||
     lastNativePathMetrics.RasterizedPathCount != 0U ||
     lastNativePathMetrics.PathUploadBytes != 0UL ||
     lastNativePathMetrics.VertexUploadBytes != 0UL ||
     lastNativePathMetrics.IndexUploadBytes != 0UL ||
     lastNativePathMetrics.BrushUploadBytes != 0UL ||
     lastNativePathMetrics.CoverageStagingBytes != 0UL))
{
    throw new InvalidOperationException(
        "Stable native path replay changed the grown atlas or uploaded retained payload.");
}
if (useImageScene &&
    (lastNativeImageMetrics.TextureUploadBytes != 0UL ||
     lastNativeImageMetrics.VertexUploadBytes != 0UL ||
     lastNativeImageMetrics.IndexUploadBytes != 0UL ||
     lastNativeImageMetrics.UniformUploadBytes != 0UL))
{
    throw new InvalidOperationException(
        "Stable native image replay uploaded retained texture, geometry, or uniforms.");
}
RenderManaged();
context.PollDevice(wait: true);

byte[] nativePixels = nativeTarget.ReadPixels();
byte[] managedPixels = managedTarget.ReadPixels();
if (useBoxGroupEffect)
{
    managedPixels = ApplySeparableBoxBlur(
        managedPixels,
        checked((int)width),
        checked((int)height),
        Math.Clamp((int)MathF.Floor(gaussianSigma * dpiScale), 0, 128));
}
if (writeImages)
{
    Directory.CreateDirectory("artifacts/progpu-native/differential");
    string familyStem = useSemanticLayerEffects
        ? "semantic-layer-effects"
        : useSemanticScene
        ? "semantic-scene"
        : useImageScene
        ? "images"
        : useGlyphScene
        ? "glyphs"
        : usePathScene
        ? "paths"
        : useGeometryScene
        ? "geometry"
        : useAnalyticScene
        ? "analytic"
        : "solid";
    string imageStem = useSemanticLayerEffects
        ? "semantic-layer-effects"
        : useSemanticScene
        ? "semantic-scene"
        : useGroupEffectChain
        ? $"group-effect-chain-{familyStem}"
        : useDropShadowGroupEffect
        ? $"group-drop-shadow-{familyStem}"
        : useGaussianGroupEffect
        ? $"group-gaussian-blur-{familyStem}"
        : useBoxGroupEffect
        ? $"group-box-blur-{familyStem}"
        : useGroupBlend
        ? $"group-blend-{groupBlendMode.ToString().ToLowerInvariant()}-{familyStem}"
        : useVectorClipChain
        ? useImageScene
            ? "group-vector-clip-images"
            : useGlyphScene
            ? "group-vector-clip-glyphs"
            : usePathScene
            ? "group-vector-clip-paths"
            : useGeometryScene
            ? "group-vector-clip-geometry"
            : useAnalyticScene
            ? "group-vector-clip-analytic"
            : "group-vector-clip-solid"
        : useRoundedGroupMask
        ? useImageScene
            ? "group-rounded-mask-images"
            : useGlyphScene
            ? "group-rounded-mask-glyphs"
            : usePathScene
            ? "group-rounded-mask-paths"
            : useGeometryScene
            ? "group-rounded-mask-geometry"
            : useAnalyticScene
            ? "group-rounded-mask-analytic"
            : "group-rounded-mask-solid"
        : useTextureGroupMask
        ? useImageScene
            ? "group-texture-mask-images"
            : useGlyphScene
            ? "group-texture-mask-glyphs"
            : usePathScene
            ? "group-texture-mask-paths"
            : useGeometryScene
            ? "group-texture-mask-geometry"
            : useAnalyticScene
            ? "group-texture-mask-analytic"
            : "group-texture-mask-solid"
        : useGroupOpacity
        ? useImageScene
            ? "group-opacity-images"
            : useGlyphScene
            ? "group-opacity-glyphs"
            : usePathScene
            ? "group-opacity-paths"
            : useGeometryScene
            ? "group-opacity-geometry"
            : useAnalyticScene
            ? "group-opacity-analytic"
            : "group-opacity-solid"
        : useImageScene
        ? useMaskedImageScene
            ? "masked-images"
            : useExternalImageScene ? "external-images" : "images"
        : useGlyphScene
        ? forceAtlasGrowth ? "glyphs-growth" : "glyphs"
        : usePathScene
        ? forceAtlasGrowth ? "paths-growth" : "paths"
        : useDashedGeometryScene ? "dashes" : "latest";
    WritePpm(
        $"artifacts/progpu-native/differential/{imageStem}-native.ppm",
        nativePixels,
        width,
        height);
    WritePpm(
        $"artifacts/progpu-native/differential/{imageStem}-managed.ppm",
        managedPixels,
        width,
        height);
    WriteDifferencePpm(
        $"artifacts/progpu-native/differential/{imageStem}-difference-64x.ppm",
        nativePixels,
        managedPixels,
        width,
        height,
        amplification: 64);
}
PixelComparison comparison = ComparePixels(nativePixels, managedPixels);
if (forceAtlasGrowth && nativeAtlasWidth <= 1024U)
{
    throw new InvalidOperationException(
        $"The native atlas growth gate did not grow: " +
        $"width={nativeAtlasWidth}, growthCount={nativeAtlasGrowthCount}.");
}
if (forceAtlasGrowth && useGlyphScene && nativeAtlasGrowthCount == 0U)
{
    throw new InvalidOperationException(
        "The native glyph atlas did not publish its growth count.");
}
bool requiresExactPixels = !useSemanticScene &&
    ((useImageScene && !useMaskedImageScene) || useGlyphScene ||
    (!useImageScene && !useGlyphScene && !useAnalyticScene &&
     !useGeometryScene && !usePathScene && dpiScale == 1f));
bool usesGeometryDifferential = useSemanticScene || useGeometryScene || usePathScene;
bool usesTightDifferential =
    (useAnalyticScene && analyticKind is 1 or 2) ||
    (!useAnalyticScene && !useGeometryScene && !requiresExactPixels);
bool usesCommonMaskDifferential = useGroupMask &&
    !usesGeometryDifferential && !useAnalyticScene;
// The managed opacity-mask route first rasterizes the brush into an R8 mask,
// while the native zero-copy route samples the borrowed texture directly.
// Linear filtering can therefore differ by one final channel value after the
// managed intermediate quantization, but must not change any pixel by more.
bool usesDrawStateClipImage = useDrawState && useImageScene;
int maximumAllowedDifference = usesDrawStateClipImage
    ? 128
    : useSemanticLayerEffects
    ? 204
    : useSemanticScene
    ? 96
    : useVectorClipChain
    ? usesGeometryDifferential ? 204 : 64
    : useGroupEffectChain
    ? usesGeometryDifferential ? 204 : 64
    : useDropShadowGroupEffect
    ? usesGeometryDifferential ? 204 : 64
    : useGaussianGroupEffect
    ? 64
    : useBoxGroupEffect
    ? 1
    : useGroupBlend
    ? usesGeometryDifferential ? 204 : 64
    : useMaskedImageScene
    ? 1
    : usesCommonMaskDifferential ? 3
    : requiresExactPixels ? 0
    : usesGeometryDifferential ? 204 : usesTightDifferential ? 3 : 96;
int maximumAllowedPixelsOverTolerance =
    usesDrawStateClipImage
        ? 2048
        : useSemanticLayerEffects
        ? Math.Max(1, comparison.PixelCount / 100)
        : useSemanticScene
        ? Math.Max(1, comparison.PixelCount / 1000)
        : useGroupEffect
        ? Math.Max(1, comparison.PixelCount / 100)
        : useGroupBlend
        ? Math.Max(1, comparison.PixelCount / 100)
        : useVectorClipChain
        ? Math.Max(1, comparison.PixelCount / 100)
        : usesGeometryDifferential
        ? useSplineGeometryScene || useDashedGeometryScene
            ? Math.Max(1, rectangleCount / 32)
            : 1
        : requiresExactPixels || usesTightDifferential ||
          usesCommonMaskDifferential
        ? 0
        : comparison.PixelCount / 40;
double maximumAllowedMeanAbsoluteDifference = usesDrawStateClipImage
    // The managed comparator clips the texture quad and recomputes boundary
    // UVs; native uses the fixed-function scissor and leaves interpolation
    // untouched. Differences are restricted to the one-pixel clip perimeter.
    ? 0.05
    : useSemanticLayerEffects
    // Both routes retain the same mixed scene and effect order, but their
    // independently quantized path/glyph coverage and RGBA8 effect
    // intermediates can disagree on bounded antialiased edge ties.
    ? 0.15
    : useSemanticScene
    // The managed and native retained path/glyph atlases independently own
    // subpixel coverage ties. The image and analytic quadrants remain exact;
    // constrain the mixed aggregate to five thousandths of one byte/channel.
    ? 0.005
    : useGroupEffectChain
    // Two independently quantized RGBA8 intermediate graphs can accumulate
    // two one-byte edge decisions. Analytic source coverage is independently
    // rasterized before both effects; llvmpipe arm64 resolves a bounded set of
    // those edge ties differently. Keep the maximum/pixel-count gates above
    // and retain the tighter one-eighth-channel bound for every other family.
    ? useAnalyticScene ? 0.13 : 0.125
    : useDropShadowGroupEffect
    // Analytic source coverage is independently rasterized before the shared
    // blur/composition graph. Software Vulkan implementations can resolve
    // those subpixel edge ties differently across architectures. Preserve the
    // 64/255 maximum and 1% changed-pixel gates above while allowing the
    // observed aggregate edge noise; all other shadow families stay stricter.
    ? useAnalyticScene ? 0.125 : 0.1
    : useGaussianGroupEffect
    ? 0.075
    : useBoxGroupEffect
    ? 0.01
    : useGroupBlend
    ? 0.125
    : useVectorClipChain
    ? 0.075
    : useMaskedImageScene
    ? 0.05
    : useTextureGroupMask
    ? 0.075
    : useRoundedGroupMask
    ? 0.05
    : requiresExactPixels ? 0.0
    // The independently expanded paths can differ by one byte on shared AA
    // edge ties. Keep the aggregate budget below 0.004/255 per channel while
    // retaining the stricter high-difference pixel limit above.
    : useDashedGeometryScene ? 0.004
    : usesGeometryDifferential || usesTightDifferential ? 0.001 : 0.15;
if (comparison.MaximumChannelDifference > maximumAllowedDifference ||
    comparison.PixelsOverTolerance > maximumAllowedPixelsOverTolerance ||
    comparison.MeanAbsoluteChannelDifference >
        maximumAllowedMeanAbsoluteDifference)
{
    throw new InvalidOperationException(
        $"Native/managed output diverged: max={comparison.MaximumChannelDifference}, " +
        $"pixelsOverTolerance={comparison.PixelsOverTolerance}/{comparison.PixelCount}; " +
        $"meanAbsolute={comparison.MeanAbsoluteChannelDifference:F6}; " +
        $"allowedMax={maximumAllowedDifference}, " +
        $"allowedPixels={maximumAllowedPixelsOverTolerance}, " +
        $"allowedMean={maximumAllowedMeanAbsoluteDifference:F6}; " +
        $"nativePayloadHash={nativePayloadHash:X16}; " +
        $"nativeHash={comparison.NativeFnv1A64}, " +
        $"managedHash={comparison.ManagedFnv1A64}.");
}

effectTimingActive = recomputeGroupEffect;

for (int index = 0; index < warmupCount; index++)
{
    if ((index & 1) == 0)
    {
        RenderNative();
        SynchronizeNativeIfRequested();
        RenderManaged();
        SynchronizeManagedIfRequested();
    }
    else
    {
        RenderManaged();
        SynchronizeManagedIfRequested();
        RenderNative();
        SynchronizeNativeIfRequested();
    }

    if (drainEachPair)
    {
        context.PollDevice(wait: true);
    }
}
context.PollDevice(wait: true);

var nativeTimes = new double[iterationCount];
var managedTimes = new double[iterationCount];
var nativeSubmissionTimes = new double[iterationCount];
var nativeCompletionWaitTimes = new double[iterationCount];
var managedSubmissionTimes = new double[iterationCount];
var managedCompletionWaitTimes = new double[iterationCount];
long nativeAllocationStart = GC.GetAllocatedBytesForCurrentThread();
long nativeAllocatedBytes = 0;
long managedAllocatedBytes = 0;
long nativeSubmissionAllocatedBytes = 0;
long nativeCompletionAllocatedBytes = 0;
long managedSubmissionAllocatedBytes = 0;
long managedCompletionAllocatedBytes = 0;
if (groupMeasurements)
{
    void MeasureNativeGroup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        for (int index = 0; index < iterationCount; index++)
        {
            nativeTimes[index] = MeasureNative(
                out long allocated,
                out long submissionAllocated,
                out long completionAllocated,
                out nativeSubmissionTimes[index],
                out nativeCompletionWaitTimes[index]);
            nativeAllocatedBytes += allocated;
            nativeSubmissionAllocatedBytes += submissionAllocated;
            nativeCompletionAllocatedBytes += completionAllocated;
        }
    }

    void MeasureManagedGroup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        for (int index = 0; index < iterationCount; index++)
        {
            managedTimes[index] = MeasureManaged(
                out long allocated,
                out long submissionAllocated,
                out long completionAllocated,
                out managedSubmissionTimes[index],
                out managedCompletionWaitTimes[index]);
            managedAllocatedBytes += allocated;
            managedSubmissionAllocatedBytes += submissionAllocated;
            managedCompletionAllocatedBytes += completionAllocated;
        }
    }

    if (managedGroupFirst)
    {
        MeasureManagedGroup();
        context.PollDevice(wait: true);
        MeasureNativeGroup();
    }
    else
    {
        MeasureNativeGroup();
        context.PollDevice(wait: true);
        MeasureManagedGroup();
    }
}
else
{
    for (int index = 0; index < iterationCount; index++)
    {
        if ((index & 1) == 0)
        {
            nativeTimes[index] = MeasureNative(
                out long allocated,
                out long submissionAllocated,
                out long completionAllocated,
                out nativeSubmissionTimes[index],
                out nativeCompletionWaitTimes[index]);
            nativeAllocatedBytes += allocated;
            nativeSubmissionAllocatedBytes += submissionAllocated;
            nativeCompletionAllocatedBytes += completionAllocated;
            managedTimes[index] = MeasureManaged(
                out allocated,
                out submissionAllocated,
                out completionAllocated,
                out managedSubmissionTimes[index],
                out managedCompletionWaitTimes[index]);
            managedAllocatedBytes += allocated;
            managedSubmissionAllocatedBytes += submissionAllocated;
            managedCompletionAllocatedBytes += completionAllocated;
        }
        else
        {
            managedTimes[index] = MeasureManaged(
                out long allocated,
                out long submissionAllocated,
                out long completionAllocated,
                out managedSubmissionTimes[index],
                out managedCompletionWaitTimes[index]);
            managedAllocatedBytes += allocated;
            managedSubmissionAllocatedBytes += submissionAllocated;
            managedCompletionAllocatedBytes += completionAllocated;
            nativeTimes[index] = MeasureNative(
                out allocated,
                out submissionAllocated,
                out completionAllocated,
                out nativeSubmissionTimes[index],
                out nativeCompletionWaitTimes[index]);
            nativeAllocatedBytes += allocated;
            nativeSubmissionAllocatedBytes += submissionAllocated;
            nativeCompletionAllocatedBytes += completionAllocated;
        }

        if (drainEachPair)
        {
            // Keep the queue bounded without charging the shared GPU wait to
            // either renderer's CPU submission interval.
            context.PollDevice(wait: true);
        }
    }
}
context.PollDevice(wait: true);
GC.KeepAlive(nativeAllocationStart);

TimingSummary nativeSummary = Summarize(nativeTimes, nativeAllocatedBytes);
TimingSummary managedSummary = Summarize(managedTimes, managedAllocatedBytes);
TimingSummary nativeSubmissionSummary = Summarize(nativeSubmissionTimes, 0);
TimingSummary nativeCompletionWaitSummary = Summarize(nativeCompletionWaitTimes, 0);
TimingSummary managedSubmissionSummary = Summarize(managedSubmissionTimes, 0);
TimingSummary managedCompletionWaitSummary = Summarize(managedCompletionWaitTimes, 0);
ulong combinedMetalAllocatedBytes =
    context.TryCaptureNativeResourceSnapshot(out var resourceSnapshot)
        ? resourceSnapshot.MetalAllocatedBytes
        : 0UL;
NativeLayerMetrics nativeLayerMetrics = native.GetLayerMetrics();
var report = new BenchmarkReport(
    RuntimeInformation: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Adapter: context.AdapterName,
    Backend: context.AdapterBackendType.ToString(),
    Scene: useSemanticLayerEffects
        ? "RetainedSemanticLayerMaskEffectChain"
        : useSemanticScene
        ? "RetainedMixedSemanticScene"
        : useImageScene
        ? useExternalImageScene
            ? useMaskedImageScene
                ? "ZeroCopyMaskedExternalRgbaImage"
                : "ZeroCopyExternalRgbaImage"
            : "RetainedRgbaImage"
        : useGlyphScene
        ? "RetainedPositionedGlyphAtlas"
        : usePathScene
        ? "RetainedPathAtlas"
        : useGeometryScene
        ? useDashedGeometryScene
            ? "IndexedGeometryDashes"
            : useSplineGeometryScene
            ? "IndexedGeometrySplines"
            : usePolylineGeometryScene
            ? "IndexedGeometryPolylines"
            : useCurveGeometryScene
            ? "IndexedGeometryCurves"
            : "IndexedGeometry"
        : useAnalyticScene ? "IndexedAnalytic" : "SolidRectangles",
    RerasterizeGlyphs: rerasterizeGlyphs,
    DifferentialContract: useSemanticLayerEffects
        ? "Matched retained mixed semantic scene through blur/drop-shadow GPU chain and post-effect rounded mask; bounded independent coverage and RGBA8 intermediate edge ownership"
        : useSemanticScene
        ? "Matched retained analytic/path/glyph/image semantic scene; bounded independent path/glyph coverage edge ownership"
        : usesDrawStateClipImage
        ? "Near-exact; differences restricted to managed CPU-clipped texture perimeter versus native scissor"
        : useGroupEffectChain
        ? "Bounded two-node blur/drop-shadow chain differential: max 64/255 (204/255 on independent geometry edge ties), under 1% pixels beyond 3/255, mean under 0.130/255 for analytic source coverage and 0.125/255 otherwise"
        : useDropShadowGroupEffect
        ? "Bounded retained drop-shadow differential: max 64/255 (204/255 on independent geometry edge ties), under 1% pixels beyond 3/255, mean under 0.125/255 for analytic source coverage and 0.1/255 otherwise"
        : useGaussianGroupEffect
        ? "Bounded separable Gaussian-blur differential: max 64/255, under 1% pixels beyond 3/255, mean under 0.075/255 per channel"
        : useBoxGroupEffect
        ? "Separable box-blur compute output against an independent two-pass integer RGBA8 oracle; exact at the default 1x radius and bounded to 1/255 at high DPI"
        : useGroupBlend
        ? "Bounded group-blend differential: max 64/255 (204/255 on independent geometry edge ties), under 1% pixels beyond 3/255, mean under 0.125/255 per channel"
        : useVectorClipChain
        ? "Bounded retained vector-clip AA differential: max 64/255, under 1% edge pixels beyond 3/255, mean under 0.075/255 per channel"
        : useMaskedImageScene
        ? "Near-exact; direct mask sampling versus quantized managed R8 intermediate"
        : useGroupMask && usesGeometryDifferential
        ? "Near-exact common mask plus bounded independent raster edge ownership"
        : useGroupMask && useAnalyticScene
        ? "Bounded analytic raster differential plus near-exact common mask"
        : usesCommonMaskDifferential
        ? "Near-exact common mask; at most 3/255 per channel and no pixels beyond tolerance"
        : requiresExactPixels
        ? "Exact"
        : usesGeometryDifferential
            ? "Near-exact; bounded raster edge ownership ties"
        : usesTightDifferential
            ? "Near-exact pipeline (at most 3/255 per channel)"
            : "Bounded against managed solid-rectangle specialization",
    RectangleCount: rectangleCount,
    DpiScale: dpiScale,
    WarmupIterations: warmupCount,
    MeasuredIterations: iterationCount,
    SynchronizeEachFrame: synchronizeEachFrame,
    DrainEachPair: drainEachPair,
    DrawState: useDrawState,
    GroupOpacity: useGroupOpacity,
    GroupMask: useSemanticLayerEffects
        ? "SemanticRoundedRectangle"
        : useRoundedGroupMask
        ? "RoundedRectangle"
        : useTextureGroupMask
            ? "Texture"
            : useVectorClipChain
                ? "VectorClipChain"
            : "None",
    GroupEffect: useSemanticLayerEffects
        ? "SemanticGaussianBlurThenDropShadow"
        : recomputeGroupEffect
        ? useGroupEffectChain
            ? "GaussianBlurThenDropShadowRecomputed"
            : useDropShadowGroupEffect
            ? "DropShadowRecomputed"
            : "GaussianBlurRecomputed"
        : useGroupEffectChain
            ? "GaussianBlurThenDropShadow"
            : useDropShadowGroupEffect
            ? "DropShadow"
            : useGaussianGroupEffect
            ? "GaussianBlur"
            : useBoxGroupEffect ? "BoxBlur" : "None",
    GroupBlend: useGroupBlend ? groupBlendMode.ToString() : "SrcOver",
    ManagedCompiledSceneCache: enableManagedCompiledSceneCache,
    MeasurementOrder: groupMeasurements
        ? managedGroupFirst ? "GroupedManagedFirst" : "GroupedNativeFirst"
        : "Alternating",
    Native: nativeSummary,
    Managed: managedSummary,
    NativeSubmission: nativeSubmissionSummary,
    NativeCompletionWait: nativeCompletionWaitSummary,
    ManagedSubmission: managedSubmissionSummary,
    ManagedCompletionWait: managedCompletionWaitSummary,
    NativeSubmissionAllocatedBytesPerFrame:
        nativeSubmissionAllocatedBytes / (double)iterationCount,
    NativeCompletionAllocatedBytesPerFrame:
        nativeCompletionAllocatedBytes / (double)iterationCount,
    ManagedSubmissionAllocatedBytesPerFrame:
        managedSubmissionAllocatedBytes / (double)iterationCount,
    ManagedCompletionAllocatedBytesPerFrame:
        managedCompletionAllocatedBytes / (double)iterationCount,
    NativeToManagedP95Ratio: managedSummary.P95Milliseconds == 0
        ? 0
        : nativeSummary.P95Milliseconds / managedSummary.P95Milliseconds,
    CombinedMetalAllocatedBytes: combinedMetalAllocatedBytes,
    NativeVertexCount: nativeVertexCount,
    NativeIndexCount: nativeIndexCount,
    ManagedVertexCount: managedVertexCount,
    ManagedIndexCount: managedIndexCount,
    NativePayloadHash: nativePayloadHash.ToString("X16"),
    NativeRasterizedPathCount: nativeRasterizedPathCount,
    NativePathUploadBytes: nativePathUploadBytes,
    NativeCoverageStagingBytes: nativeCoverageStagingBytes,
    NativeRasterizedGlyphCount: nativeRasterizedGlyphCount,
    NativeGlyphOutlineUploadBytes: nativeGlyphOutlineUploadBytes,
    NativeGlyphInstanceUploadBytes: nativeGlyphInstanceUploadBytes,
    NativeAtlasWidth: nativeAtlasWidth,
    NativeAtlasGeneration: nativeAtlasGeneration,
    NativeAtlasGrowthCount: nativeAtlasGrowthCount,
    NativeImageTextureUploadBytes: nativeImageTextureUploadBytes,
    NativeImageTextureGeneration: nativeImageTextureGeneration,
    NativeSceneUpdateMetrics: nativeSceneUpdateMetrics,
    NativeSceneFrameMetrics: lastNativeSceneMetrics,
    NativeLayerMetrics: nativeLayerMetrics,
    PixelParity: comparison);

string reportJson = JsonSerializer.Serialize(
    report,
    new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(reportJson);
if (!string.IsNullOrWhiteSpace(outputJsonPath))
{
    string fullOutputPath = Path.GetFullPath(outputJsonPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
    File.WriteAllText(fullOutputPath, reportJson);
}

ulong RenderNative(bool capturePayloadHash = false)
{
    if (effectTimingActive)
    {
        bool alternate = (nativeEffectTimingIndex++ & 1) != 0;
        if (useGroupEffectChain)
        {
            nativeDrawState = new NativeDrawState(
                nativeDrawState.Opacity,
                nativeDrawState.ClipRect,
                nativeDrawState.Flags,
                nativeDrawState.GroupOpacity,
                nativeDrawState.GroupRevision,
                nativeDrawState.GroupMask,
                alternate ? timedEffectChainB! : timedEffectChainA!);
        }
        else
        {
            NativeGroupEffect timedEffect = useDropShadowGroupEffect
                ? NativeGroupEffect.DropShadow(
                    gaussianSigma,
                    dropShadowOffset + new Vector2(alternate ? 0.25f : 0f, 0f),
                    dropShadowColor,
                    nativeEffectTimingRevision++)
                : useBoxGroupEffect
                ? NativeGroupEffect.BoxBlur(
                    gaussianSigma + (alternate ? 0.25f : 0f),
                    nativeEffectTimingRevision++)
                : NativeGroupEffect.GaussianBlur(
                    gaussianSigma + (alternate ? 0.25f : 0f),
                    nativeEffectTimingRevision++);
            nativeDrawState = new NativeDrawState(
                nativeDrawState.Opacity,
                nativeDrawState.ClipRect,
                nativeDrawState.Flags,
                nativeDrawState.GroupOpacity,
                nativeDrawState.GroupRevision,
                nativeDrawState.GroupMask,
                timedEffect);
        }
    }
    if (useSemanticScene)
    {
        lastNativeSceneMetrics = native.RenderScene(
            nativeTarget,
            dpiScale,
            semanticSceneId,
            semanticSceneGeneration,
            clearColor);
        nativePathUploadBytes = Math.Max(
            nativePathUploadBytes,
            lastNativeSceneMetrics.VertexUploadBytes +
            lastNativeSceneMetrics.IndexUploadBytes);
        nativeCoverageStagingBytes = Math.Max(
            nativeCoverageStagingBytes,
            lastNativeSceneMetrics.CoverageStagingBytes);
        nativeImageTextureUploadBytes = Math.Max(
            nativeImageTextureUploadBytes,
            lastNativeSceneMetrics.TextureUploadBytes);
        return lastNativeSceneMetrics.PayloadHash;
    }
    if (useImageScene)
    {
        float destinationWidth = logicalWidth - 160f;
        float destinationHeight = logicalHeight - 120f;
        NativeImageRect sourceRect = new(
            0f,
            0f,
            benchmarkImageWidth,
            benchmarkImageHeight);
        NativeImageRect destinationRect = new(
            80f,
            60f,
            destinationWidth,
            destinationHeight);
        NativeImageFrameMetrics metrics = useMaskedImageScene
            ? native.RenderMaskedExternalImage(
                nativeTarget,
                managedImageTexture!,
                managedImageMaskTexture!,
                dpiScale,
                sourceRect,
                destinationRect,
                destinationRect,
                Matrix3x2.Identity,
                1f,
                NativeImageSampling.Nearest,
                NativeImageSampling.Linear,
                clearColor,
                sourceRevision: 1U,
                maskRevision: 1U,
                contentRevision: 1U,
                drawState: nativeDrawState)
            : useExternalImageScene
            ? native.RenderExternalImage(
                nativeTarget,
                managedImageTexture!,
                dpiScale,
                sourceRect,
                destinationRect,
                Matrix3x2.Identity,
                1f,
                NativeImageSampling.Nearest,
                clearColor,
                sourceRevision: 1U,
                contentRevision: 1U,
                drawState: nativeDrawState)
            : native.RenderImage(
                nativeTarget,
                dpiScale,
                nativeImageUploaded ? ReadOnlySpan<byte>.Empty : imagePixels,
                benchmarkImageWidth,
                benchmarkImageHeight,
                benchmarkImageWidth * 4U,
                sourceRect,
                destinationRect,
                Matrix3x2.Identity,
                1f,
                NativeImageSampling.Nearest,
                clearColor,
                imageRevision: 1U,
                contentRevision: 1U,
                drawState: nativeDrawState);
        nativeImageUploaded = true;
        lastNativeImageMetrics = metrics;
        nativeVertexCount = metrics.VertexCount;
        nativeIndexCount = metrics.IndexCount;
        nativeImageTextureUploadBytes = Math.Max(
            nativeImageTextureUploadBytes,
            metrics.TextureUploadBytes);
        nativeImageTextureGeneration = Math.Max(
            nativeImageTextureGeneration,
            metrics.TextureGeneration);
        return metrics.PayloadHash;
    }
    if (useGlyphScene)
    {
        NativeGlyphFrameMetrics metrics = native.RenderGlyphs(
            nativeTarget,
            dpiScale,
            nativeGlyphOutlines,
            nativeGlyphSegments,
            nativeGlyphs,
            clearColor,
            capturePayloadHash,
            contentRevision: rerasterizeGlyphs
                ? nativeGlyphContentRevision++
                : 1U,
            drawState: nativeDrawState);
        lastNativeGlyphMetrics = metrics;
        nativeRasterizedGlyphCount = Math.Max(
            nativeRasterizedGlyphCount,
            metrics.RasterizedGlyphCount);
        nativeGlyphOutlineUploadBytes = Math.Max(
            nativeGlyphOutlineUploadBytes,
            metrics.OutlineUploadBytes);
        nativeGlyphInstanceUploadBytes = Math.Max(
            nativeGlyphInstanceUploadBytes,
            metrics.InstanceUploadBytes);
        nativeCoverageStagingBytes = Math.Max(
            nativeCoverageStagingBytes,
            metrics.CoverageStagingBytes);
        nativeAtlasWidth = Math.Max(nativeAtlasWidth, metrics.AtlasWidth);
        nativeAtlasGeneration = Math.Max(
            nativeAtlasGeneration,
            metrics.AtlasGeneration);
        nativeAtlasGrowthCount = Math.Max(
            nativeAtlasGrowthCount,
            metrics.AtlasGrowthCount);
        return metrics.PayloadHash;
    }
    if (usePathScene)
    {
        NativePathFrameMetrics metrics = native.RenderPaths(
            nativeTarget,
            dpiScale,
            nativePaths,
            nativePathSegments,
            clearColor,
            capturePayloadHash,
            contentRevision: 1U,
            drawState: nativeDrawState);
        lastNativePathMetrics = metrics;
        nativeVertexCount = metrics.VertexCount;
        nativeIndexCount = metrics.IndexCount;
        nativeRasterizedPathCount = Math.Max(
            nativeRasterizedPathCount,
            metrics.RasterizedPathCount);
        nativePathUploadBytes = Math.Max(
            nativePathUploadBytes,
            metrics.PathUploadBytes);
        nativeCoverageStagingBytes = Math.Max(
            nativeCoverageStagingBytes,
            metrics.CoverageStagingBytes);
        nativeAtlasWidth = Math.Max(nativeAtlasWidth, metrics.AtlasWidth);
        nativeAtlasGeneration = Math.Max(
            nativeAtlasGeneration,
            metrics.AtlasGeneration);
        return metrics.PayloadHash;
    }
    if (useGeometryScene || usePathScene)
    {
        NativeGeometryFrameMetrics metrics = useSplineGeometryScene
            ? native.RenderGeometry(
                nativeTarget,
                dpiScale,
                geometryPrimitives,
                geometryPoints,
                geometryPolylines,
                geometryDoubles,
                geometrySplines,
                clearColor,
                capturePayloadHash,
                contentRevision: 1U,
                drawState: nativeDrawState)
            : useDashedGeometryScene
            ? native.RenderGeometry(
                nativeTarget,
                dpiScale,
                geometryPrimitives,
                geometryPoints,
                geometryPolylines,
                geometryDoubles,
                geometryDashStyles,
                geometrySplines,
                clearColor,
                capturePayloadHash,
                contentRevision: 1U,
                drawState: nativeDrawState)
            : usePolylineGeometryScene
            ? native.RenderGeometry(
                nativeTarget,
                dpiScale,
                geometryPrimitives,
                geometryPoints,
                geometryPolylines,
                clearColor,
                capturePayloadHash,
                contentRevision: 1U,
                drawState: nativeDrawState)
            : native.RenderGeometry(
                nativeTarget,
                dpiScale,
                geometryPrimitives,
                clearColor,
                capturePayloadHash,
                contentRevision: 1U,
                drawState: nativeDrawState);
        nativeVertexCount = metrics.VertexCount;
        nativeIndexCount = metrics.IndexCount;
        lastNativeGeometryMetrics = metrics;
        return metrics.PayloadHash;
    }
    if (useAnalyticScene)
    {
        native.RenderAnalytic(
            nativeTarget,
            dpiScale,
            analyticPrimitives,
            clearColor,
            nativeDrawState);
    }
    else
    {
        native.Render(
            nativeTarget,
            dpiScale,
            rectangles,
            clearColor,
            nativeDrawState);
    }
    return 0UL;
}

void RenderManaged()
{
    if (effectTimingActive)
    {
        bool alternate = (managedEffectTimingIndex++ & 1) != 0;
        if (useDropShadowGroupEffect || useGroupEffectChain)
        {
            managedDropShadowEffect!.Offset = dropShadowOffset +
                new Vector2(alternate ? 0.25f : 0f, 0f);
        }
        else
        {
            managedBlurEffect!.BlurRadius = gaussianSigma +
                (alternate ? 0.25f : 0f);
        }
    }
    managed.RenderOffscreen(
        managedRenderRoot,
        Math.Max(1U, (uint)MathF.Round(logicalWidth)),
        Math.Max(1U, (uint)MathF.Round(logicalHeight)),
        managedTarget,
        padding: 0f,
        dpiScale,
        clearColor);
    if (useGeometryScene)
    {
        managedVertexCount = managed.Metrics.VectorVerticesCount;
        managedIndexCount = managed.Metrics.VectorIndicesCount;
    }
}

void SynchronizeNativeIfRequested()
{
    if (synchronizeEachFrame)
    {
        native.WaitForSubmission(native.GetLastSubmissionToken());
    }
}

void SynchronizeManagedIfRequested()
{
    if (synchronizeEachFrame)
    {
        context.PollDevice(wait: true);
    }
}

double MeasureNative(
    out long allocatedBytes,
    out long submissionAllocatedBytes,
    out long completionAllocatedBytes,
    out double submissionMilliseconds,
    out double completionWaitMilliseconds)
{
    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
    long timestamp = Stopwatch.GetTimestamp();
    RenderNative();
    long submissionAllocationEnd = GC.GetAllocatedBytesForCurrentThread();
    submissionAllocatedBytes = submissionAllocationEnd - allocationStart;
    NativeSubmissionToken submission = synchronizeEachFrame
        ? native.GetLastSubmissionToken()
        : default;
    submissionMilliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    long waitTimestamp = Stopwatch.GetTimestamp();
    if (synchronizeEachFrame)
    {
        native.WaitForSubmission(submission);
    }
    completionWaitMilliseconds = synchronizeEachFrame
        ? Stopwatch.GetElapsedTime(waitTimestamp).TotalMilliseconds
        : 0.0;
    completionAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() - submissionAllocationEnd;
    double milliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    allocatedBytes = submissionAllocatedBytes + completionAllocatedBytes;
    return milliseconds;
}

double MeasureManaged(
    out long allocatedBytes,
    out long submissionAllocatedBytes,
    out long completionAllocatedBytes,
    out double submissionMilliseconds,
    out double completionWaitMilliseconds)
{
    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
    long timestamp = Stopwatch.GetTimestamp();
    RenderManaged();
    long submissionAllocationEnd = GC.GetAllocatedBytesForCurrentThread();
    submissionAllocatedBytes = submissionAllocationEnd - allocationStart;
    submissionMilliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    long waitTimestamp = Stopwatch.GetTimestamp();
    SynchronizeManagedIfRequested();
    completionWaitMilliseconds = synchronizeEachFrame
        ? Stopwatch.GetElapsedTime(waitTimestamp).TotalMilliseconds
        : 0.0;
    completionAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() - submissionAllocationEnd;
    double milliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    allocatedBytes = submissionAllocatedBytes + completionAllocatedBytes;
    return milliseconds;
}

int ReadPositiveArgument(string name, int fallback)
{
    int value = ReadArgument(name, fallback);
    return value > 0 ? value : fallback;
}

int ReadNonNegativeArgument(string name, int fallback)
{
    int value = ReadArgument(name, fallback);
    return value >= 0 ? value : fallback;
}

float ReadPositiveFloatArgument(string name, float fallback)
{
    for (int index = 0; index + 1 < args.Length; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
            float.TryParse(
                args[index + 1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float value) &&
            float.IsFinite(value) &&
            value > 0f)
        {
            return value;
        }
    }
    return fallback;
}

int ReadArgument(string name, int fallback)
{
    for (int index = 0; index + 1 < args.Length; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[index + 1], out int value))
        {
            return value;
        }
    }
    return fallback;
}

string? ReadStringArgument(string name)
{
    for (int index = 0; index + 1 < args.Length; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }
    return null;
}

static GpuTexture CreateTarget(WgpuContext context, string label) =>
    new(
        context,
        width,
        height,
        TextureFormat.Rgba8Unorm,
        TextureUsage.RenderAttachment |
        TextureUsage.TextureBinding |
        TextureUsage.CopySrc,
        label,
        alphaMode: GpuTextureAlphaMode.Premultiplied);

static byte[] CreateImagePixels(uint imageWidth, uint imageHeight)
{
    var pixels = new byte[checked((int)(imageWidth * imageHeight * 4U))];
    for (uint y = 0U; y < imageHeight; ++y)
    {
        for (uint x = 0U; x < imageWidth; ++x)
        {
            int offset = checked((int)((y * imageWidth + x) * 4U));
            bool checker = ((x / 12U) + (y / 12U)) % 2U == 0U;
            pixels[offset] = (byte)(checker ? 224U : x * 255U / imageWidth);
            pixels[offset + 1] = (byte)(checker ? y * 255U / imageHeight : 48U);
            pixels[offset + 2] = (byte)(checker ? 64U : 255U - x * 255U / imageWidth);
            pixels[offset + 3] = byte.MaxValue;
        }
    }
    return pixels;
}

static byte[] CreateImageMaskPixels(uint imageWidth, uint imageHeight)
{
    var pixels = new byte[checked((int)(imageWidth * imageHeight * 4U))];
    for (uint y = 0U; y < imageHeight; ++y)
    {
        for (uint x = 0U; x < imageWidth; ++x)
        {
            float nx = (x + 0.5f) / imageWidth * 2f - 1f;
            float ny = (y + 0.5f) / imageHeight * 2f - 1f;
            byte coverage = (byte)Math.Clamp(
                (1f - MathF.Sqrt(nx * nx + ny * ny)) * 384f,
                0f,
                255f);
            int offset = checked((int)((y * imageWidth + x) * 4U));
            pixels[offset] = coverage;
            pixels[offset + 1] = coverage;
            pixels[offset + 2] = coverage;
            pixels[offset + 3] = coverage;
        }
    }
    return pixels;
}

static DrawingVisual CreateManagedImageVisual(
    GpuTexture texture,
    GpuTexture? maskTexture,
    uint imageWidth,
    uint imageHeight,
    float logicalWidth,
    float logicalHeight)
{
    var visual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    visual.Context.DrawTexture(
        texture,
        new Rect(80f, 60f, logicalWidth - 160f, logicalHeight - 120f),
        new Rect(0f, 0f, imageWidth, imageHeight),
        Matrix4x4.Identity,
        TextureSamplingMode.Nearest);
    if (maskTexture is not null)
    {
        var destination = new Rect(
            80f,
            60f,
            logicalWidth - 160f,
            logicalHeight - 120f);
        visual.OpacityMask = new GpuTextureBrush
        {
            Texture = maskTexture,
            SourceRect = new Rect(0f, 0f, imageWidth, imageHeight),
            DestinationRect = destination,
            SamplingMode = TextureSamplingMode.Linear
        };
        visual.OpacityMaskBounds = destination;
    }
    return visual;
}

static ContainerVisual CreateManagedSemanticScene(
    ReadOnlySpan<NativeAnalyticPrimitive> analyticPrimitives,
    ushort[] glyphIndices,
    Vector2[] glyphPositions,
    TtfFont glyphFont,
    GpuTexture imageTexture,
    uint imageWidth,
    uint imageHeight,
    int familyCount,
    float quadrantWidth,
    float quadrantHeight,
    float logicalWidth,
    float logicalHeight)
{
    var root = new ContainerVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    DrawingVisual analytic = CreateManagedAnalyticVisual(
        analyticPrimitives,
        quadrantWidth,
        quadrantHeight);
    DrawingVisual paths = CreateManagedPathVisual(
        familyCount,
        quadrantWidth,
        quadrantHeight,
        forceAtlasGrowth: false);
    paths.Offset = new Vector2(quadrantWidth, 0f);
    DrawingVisual glyphs = CreateManagedGlyphVisual(
        glyphFont,
        glyphIndices,
        glyphPositions,
        quadrantWidth,
        quadrantHeight);
    glyphs.Offset = new Vector2(0f, quadrantHeight);
    DrawingVisual image = CreateManagedImageVisual(
        imageTexture,
        maskTexture: null,
        imageWidth,
        imageHeight,
        quadrantWidth,
        quadrantHeight);
    image.Offset = new Vector2(quadrantWidth, quadrantHeight);
    root.AddChild(analytic);
    root.AddChild(paths);
    root.AddChild(glyphs);
    root.AddChild(image);
    return root;
}

static int GetSemanticSceneBufferSize(
    ReadOnlySpan<NativeAnalyticPrimitive> analyticPrimitives,
    ReadOnlySpan<NativeScenePathFill> paths,
    ReadOnlySpan<NativePathSegment> pathSegments,
    ReadOnlySpan<NativeSceneGlyphOutline> glyphOutlines,
    ReadOnlySpan<NativePathSegment> glyphSegments,
    ReadOnlySpan<NativePositionedGlyph> glyphs,
    ReadOnlySpan<byte> imagePixels,
    ReadOnlySpan<NativeSceneBrush> brushes,
    ReadOnlySpan<uint> analyticBrushIndices,
    ReadOnlySpan<uint> pathBrushIndices,
    ReadOnlySpan<NativeSceneTextStyle> textStyles,
    bool includeLayerEffects)
{
    int layerArenaCapacity = includeLayerEffects
        ? checked(
            Unsafe.SizeOf<NativeSceneLayerMask>() +
            Unsafe.SizeOf<NativeSceneEffectChain>() +
            2 * Unsafe.SizeOf<NativeSceneEffect>() +
            Unsafe.SizeOf<NativeSceneLayer>() +
            256)
        : 0;
    int arenaCapacity = checked(
        analyticPrimitives.Length * Unsafe.SizeOf<NativeAnalyticPrimitive>() +
        paths.Length * Unsafe.SizeOf<NativeScenePathFill>() +
        pathSegments.Length * Unsafe.SizeOf<NativePathSegment>() +
        glyphOutlines.Length * Unsafe.SizeOf<NativeSceneGlyphOutline>() +
        glyphSegments.Length * Unsafe.SizeOf<NativePathSegment>() +
        glyphOutlines.Length * Unsafe.SizeOf<NativeSceneGlyphOutline>() +
        glyphSegments.Length * Unsafe.SizeOf<NativePathSegment>() +
        glyphs.Length * Unsafe.SizeOf<NativePositionedGlyph>() +
        pathSegments.Length * Unsafe.SizeOf<NativePathSegment>() +
        imagePixels.Length +
        imagePixels.Length +
        brushes.Length * Unsafe.SizeOf<NativeSceneBrush>() +
        textStyles.Length * Unsafe.SizeOf<NativeSceneTextStyle>() +
        2 * 24 + // Two exact progpu_native_scene_glyph_draw prefixes.
        (analyticBrushIndices.Length + pathBrushIndices.Length) * sizeof(uint) +
        4 * 16 +
        Unsafe.SizeOf<NativeSceneImageDraw>() +
        layerArenaCapacity +
        256);
    return NativeSceneStreamBuilder.GetRequiredBufferSize(
        commandCapacity: includeLayerEffects ? 10 : 8,
        resourceCapacity: includeLayerEffects ? 12 : 10,
        arenaCapacity);
}

static int BuildSemanticScene(
    Span<byte> destination,
    ulong sceneId,
    ulong generation,
    ReadOnlySpan<NativeAnalyticPrimitive> analyticPrimitives,
    ReadOnlySpan<NativeScenePathFill> paths,
    ReadOnlySpan<NativePathSegment> pathSegments,
    ReadOnlySpan<NativeSceneGlyphOutline> glyphOutlines,
    ReadOnlySpan<NativePathSegment> glyphSegments,
    ReadOnlySpan<NativePositionedGlyph> glyphs,
    ReadOnlySpan<byte> imagePixels,
    ReadOnlySpan<NativeSceneBrush> brushes,
    ReadOnlySpan<uint> analyticBrushIndices,
    ReadOnlySpan<uint> pathBrushIndices,
    ReadOnlySpan<NativeSceneTextStyle> textStyles,
    in NativeSceneImageDraw imageDraw,
    float logicalWidth,
    float logicalHeight,
    bool includeLayerEffects,
    float gaussianSigma,
    Vector2 dropShadowOffset,
    Vector4 dropShadowColor)
{
    int analyticSplit = Math.Max(1, analyticPrimitives.Length / 2);
    ReadOnlySpan<NativeAnalyticPrimitive> firstAnalytic =
        analyticPrimitives[..analyticSplit];
    ReadOnlySpan<NativeAnalyticPrimitive> secondAnalytic =
        analyticPrimitives[analyticSplit..];
    if (secondAnalytic.IsEmpty)
    {
        throw new InvalidOperationException(
            "The matched semantic scene requires two analytic payloads.");
    }
    ReadOnlySpan<uint> firstAnalyticBrushIndices =
        analyticBrushIndices[..analyticSplit];
    ReadOnlySpan<uint> secondAnalyticBrushIndices =
        analyticBrushIndices[analyticSplit..];
    int pathSplit = Math.Max(1, paths.Length / 2);
    ReadOnlySpan<NativeScenePathFill> firstPaths = paths[..pathSplit];
    ReadOnlySpan<NativeScenePathFill> secondPaths = paths[pathSplit..];
    if (secondPaths.IsEmpty)
    {
        throw new InvalidOperationException(
            "The matched semantic scene requires two path payloads.");
    }
    ReadOnlySpan<uint> firstPathBrushIndices =
        pathBrushIndices[..pathSplit];
    ReadOnlySpan<uint> secondPathBrushIndices =
        pathBrushIndices[pathSplit..];
    int glyphSplit = Math.Max(1, glyphs.Length / 2);
    ReadOnlySpan<NativePositionedGlyph> firstGlyphs = glyphs[..glyphSplit];
    ReadOnlySpan<NativePositionedGlyph> secondGlyphs = glyphs[glyphSplit..];
    if (secondGlyphs.IsEmpty)
    {
        throw new InvalidOperationException(
            "The matched semantic scene requires two glyph payloads.");
    }
    float sourceHalfWidth = imageDraw.SourceRect.Width * 0.5f;
    float destinationHalfWidth = imageDraw.DestinationRect.Width * 0.5f;
    var firstImageDraw = new NativeSceneImageDraw(
        imageDraw.ImageWidth,
        imageDraw.ImageHeight,
        imageDraw.RowBytes,
        imageDraw.Sampling,
        new NativeImageRect(
            imageDraw.SourceRect.X,
            imageDraw.SourceRect.Y,
            sourceHalfWidth,
            imageDraw.SourceRect.Height),
        new NativeImageRect(
            imageDraw.DestinationRect.X,
            imageDraw.DestinationRect.Y,
            destinationHalfWidth,
            imageDraw.DestinationRect.Height),
        imageDraw.Transform,
        imageDraw.Opacity);
    var secondImageDraw = new NativeSceneImageDraw(
        imageDraw.ImageWidth,
        imageDraw.ImageHeight,
        imageDraw.RowBytes,
        imageDraw.Sampling,
        new NativeImageRect(
            imageDraw.SourceRect.X + sourceHalfWidth,
            imageDraw.SourceRect.Y,
            imageDraw.SourceRect.Width - sourceHalfWidth,
            imageDraw.SourceRect.Height),
        new NativeImageRect(
            imageDraw.DestinationRect.X + destinationHalfWidth,
            imageDraw.DestinationRect.Y,
            imageDraw.DestinationRect.Width - destinationHalfWidth,
            imageDraw.DestinationRect.Height),
        imageDraw.Transform,
        imageDraw.Opacity);
    NativeSceneLayerMask layerMask = default;
    Span<NativeSceneEffect> layerEffects =
        stackalloc NativeSceneEffect[2];
    if (includeLayerEffects)
    {
        layerMask = new NativeSceneLayerMask(
            new NativeImageRect(
                60f,
                40f,
                logicalWidth - 120f,
                logicalHeight - 80f),
            Matrix3x2.Identity,
            new Vector4(36f),
            new Vector4(36f));
        layerEffects[0] = NativeSceneEffect.GaussianBlur(
            gaussianSigma,
            gaussianSigma,
            revision: 1U);
        layerEffects[1] = NativeSceneEffect.DropShadow(
            gaussianSigma,
            dropShadowOffset,
            dropShadowColor,
            revision: 2U);
    }
    var builder = new NativeSceneStreamBuilder(
        destination,
        sceneId,
        generation,
        commandCapacity: includeLayerEffects ? 10 : 8,
        resourceCapacity: includeLayerEffects ? 12 : 10);
    uint brushResource = uint.MaxValue;
    uint layerMaskResource = uint.MaxValue;
    uint layerEffectResource = uint.MaxValue;
    ulong commandOffset = includeLayerEffects ? 1U : 0U;
    ReadOnlySpan<byte> stream = default;
    bool success = builder.TryAddAnalyticResource(
            resourceId: 1U,
            generation,
            firstAnalytic,
            out uint analyticResource) &&
        builder.TryAddPathResource(
            resourceId: 2U,
            generation,
            firstPaths,
            pathSegments,
            out uint pathResource) &&
        builder.TryAddGlyphResource(
            resourceId: 3U,
            generation,
            glyphOutlines,
            glyphSegments,
            out uint glyphResource) &&
        builder.TryAddImageResource(
            resourceId: 4U,
            generation,
            imagePixels,
            out uint imageResource) &&
        builder.TryAddAnalyticResource(
            resourceId: 5U,
            generation,
            secondAnalytic,
            out uint secondAnalyticResource) &&
        builder.TryAddPathResource(
            resourceId: 6U,
            generation,
            secondPaths,
            pathSegments,
            out uint secondPathResource) &&
        builder.TryAddGlyphResource(
            resourceId: 7U,
            generation,
            glyphOutlines,
            glyphSegments,
            out uint secondGlyphResource) &&
        builder.TryAddImageResource(
            resourceId: 8U,
            generation,
            imagePixels,
            out uint secondImageResource) &&
        builder.TryAddBrushTableResource(
            resourceId: 9U,
            generation,
            brushes,
            gradientStops: [],
            out brushResource) &&
        builder.TryAddTextStyleResource(
            resourceId: 10U,
            generation,
            textStyles,
            out uint textStyleResource) &&
        (!includeLayerEffects || builder.TryAddLayerMaskResource(
            resourceId: 100U,
            generation,
            in layerMask,
            out layerMaskResource)) &&
        (!includeLayerEffects || builder.TryAddEffectChainResource(
            resourceId: 101U,
            generation,
            layerEffects,
            revision: 91U,
            out layerEffectResource)) &&
        (!includeLayerEffects || builder.TryPushLayer(
            commandId: 1U,
            new NativeSceneLayer(
                flags: NativeSceneLayerFlags.ForceIsolation,
                maskResourceIndex: layerMaskResource,
                effectResourceIndex: layerEffectResource,
                contentRevision: 1U,
                compositeRevision: 1U))) &&
        builder.TryDrawAnalytic(
            commandId: 1U + commandOffset,
            analyticResource,
            new NativeImageRect(0f, 0f, logicalWidth * 0.5f, logicalHeight * 0.5f),
            brushResource,
            firstAnalyticBrushIndices) &&
        builder.TryDrawPath(
            commandId: 2U + commandOffset,
            pathResource,
            new NativeImageRect(
                logicalWidth * 0.5f,
                0f,
                logicalWidth * 0.5f,
                logicalHeight * 0.5f),
            brushResource,
            firstPathBrushIndices) &&
        builder.TryDrawGlyphRun(
            commandId: 3U + commandOffset,
            glyphResource,
            new NativeImageRect(
                0f,
                logicalHeight * 0.5f,
                logicalWidth * 0.5f,
                logicalHeight * 0.5f),
            firstGlyphs,
            textStyleResource,
            styleIndex: 0U) &&
        builder.TryDrawImage(
            commandId: 4U + commandOffset,
            imageResource,
            new NativeImageRect(
                logicalWidth * 0.5f,
                logicalHeight * 0.5f,
                logicalWidth * 0.5f,
                logicalHeight * 0.5f),
            in firstImageDraw) &&
        builder.TryDrawPath(
            commandId: 5U + commandOffset,
            secondPathResource,
            new NativeImageRect(
                logicalWidth * 0.5f,
                0f,
                logicalWidth * 0.5f,
                logicalHeight * 0.5f),
            brushResource,
            secondPathBrushIndices) &&
        builder.TryDrawGlyphRun(
            commandId: 6U + commandOffset,
            secondGlyphResource,
            new NativeImageRect(
                0f,
                logicalHeight * 0.5f,
                logicalWidth * 0.5f,
                logicalHeight * 0.5f),
            secondGlyphs,
            textStyleResource,
            styleIndex: 0U) &&
        builder.TryDrawImage(
            commandId: 7U + commandOffset,
            secondImageResource,
            new NativeImageRect(
                logicalWidth * 0.5f,
                logicalHeight * 0.5f,
                logicalWidth * 0.5f,
                logicalHeight * 0.5f),
            in secondImageDraw) &&
        builder.TryDrawAnalytic(
            commandId: 8U + commandOffset,
            secondAnalyticResource,
            new NativeImageRect(
                0f,
                0f,
                logicalWidth * 0.5f,
                logicalHeight * 0.5f),
            brushResource,
            secondAnalyticBrushIndices) &&
        (!includeLayerEffects || builder.TryPopLayer(commandId: 10U)) &&
        builder.TryBuild(out stream);
    if (!success)
    {
        throw new InvalidOperationException(
            "The matched semantic-scene benchmark stream could not be built.");
    }
    return stream.Length;
}

static NativeSolidRectangle[] CreateRectangles(
    int count,
    float logicalWidth,
    float logicalHeight)
{
    var result = new NativeSolidRectangle[count];
    const float inset = 18f;
    const float gap = 3f;
    float usableWidth = logicalWidth - inset * 2f;
    float usableHeight = logicalHeight - inset * 2f;
    int columns = Math.Max(
        1,
        (int)MathF.Ceiling(MathF.Sqrt(count * usableWidth / usableHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = usableWidth / columns;
    float cellHeight = usableHeight / rows;
    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        Vector4 color = new(
            0.12f + 0.45f * Wave(phase),
            0.3f + 0.62f * Wave(phase + 0.333f),
            0.45f + 0.5f * Wave(phase + 0.666f),
            1f);
        result[index] = new NativeSolidRectangle(
            inset + column * cellWidth + gap * 0.5f,
            inset + row * cellHeight + gap * 0.5f,
            Math.Max(1f, cellWidth - gap),
            Math.Max(1f, cellHeight - gap),
            color);
    }
    return result;

    static float Wave(float phase) =>
        0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
}

static DrawingVisual CreateManagedVisual(
    ReadOnlySpan<NativeSolidRectangle> rectangles,
    float logicalWidth,
    float logicalHeight)
{
    var visual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    foreach (ref readonly NativeSolidRectangle rectangle in rectangles)
    {
        visual.Context.DrawRectangle(
            new SolidColorBrush(rectangle.Color),
            pen: null,
            new Rect(
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height));
    }
    return visual;
}

static NativeAnalyticPrimitive[] CreateAnalyticPrimitives(
    int count,
    int forcedKind,
    float logicalWidth,
    float logicalHeight)
{
    var result = new NativeAnalyticPrimitive[count];
    const float inset = 24f;
    float usableWidth = logicalWidth - inset * 2f;
    float usableHeight = logicalHeight - inset * 2f;
    int columns = Math.Max(
        1,
        (int)MathF.Ceiling(MathF.Sqrt(count * usableWidth / usableHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = usableWidth / columns;
    float cellHeight = usableHeight / rows;
    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float itemWidth = Math.Max(2f, cellWidth * 0.64f);
        float itemHeight = Math.Max(2f, cellHeight * 0.58f);
        float centerX = inset + (column + 0.5f) * cellWidth;
        float centerY = inset + (row + 0.5f) * cellHeight;
        float phase = index * 0.61803398875f % 1f;
        Vector4 color = new(
            0.16f + 0.68f * Wave(phase),
            0.2f + 0.72f * Wave(phase + 0.333f),
            0.25f + 0.7f * Wave(phase + 0.666f),
            0.55f + 0.4f * Wave(phase + 0.17f));
        Matrix3x2 transform =
            Matrix3x2.CreateScale(
                0.82f + 0.32f * Wave(phase + 0.21f),
                0.78f + 0.38f * Wave(phase + 0.49f)) *
            Matrix3x2.CreateSkew(
                (Wave(phase + 0.77f) - 0.5f) * 0.18f,
                0f) *
            Matrix3x2.CreateRotation(
                (Wave(phase + 0.91f) - 0.5f) * 0.28f) *
            Matrix3x2.CreateTranslation(centerX, centerY);
        var kind = forcedKind is >= 0 and <= 2
            ? (NativeAnalyticPrimitiveKind)forcedKind
            : (NativeAnalyticPrimitiveKind)(index % 3);
        bool stroke = (index & 1) != 0;
        result[index] = new NativeAnalyticPrimitive(
            kind,
            -itemWidth * 0.5f,
            -itemHeight * 0.5f,
            itemWidth,
            itemHeight,
            color,
            transform,
            cornerRadius: Math.Min(itemWidth, itemHeight) * 0.22f,
            strokeThickness: stroke ? 1f + index % 4 : 0f);
    }
    return result;

    static float Wave(float phase) =>
        0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
}

static DrawingVisual CreateManagedAnalyticVisual(
    ReadOnlySpan<NativeAnalyticPrimitive> primitives,
    float logicalWidth,
    float logicalHeight)
{
    var visual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    foreach (ref readonly NativeAnalyticPrimitive primitive in primitives)
    {
        var solid = new SolidColorBrush(primitive.Color);
        Brush? fill = primitive.StrokeThickness > 0f ? null : solid;
        Pen? pen = primitive.StrokeThickness > 0f
            ? new Pen(solid, primitive.StrokeThickness)
            : null;
        Matrix3x2 affine = primitive.Transform;
        var transform = new Matrix4x4(
            affine.M11, affine.M12, 0f, 0f,
            affine.M21, affine.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            affine.M31, affine.M32, 0f, 1f);
        var rect = new Rect(
            primitive.X,
            primitive.Y,
            primitive.Width,
            primitive.Height);
        switch (primitive.Kind)
        {
            case NativeAnalyticPrimitiveKind.Rectangle:
                visual.Context.DrawRectangle(fill, pen, rect, transform);
                break;
            case NativeAnalyticPrimitiveKind.Ellipse:
                visual.Context.DrawEllipse(
                    fill,
                    pen,
                    new Vector2(
                        primitive.X + primitive.Width * 0.5f,
                        primitive.Y + primitive.Height * 0.5f),
                    primitive.Width * 0.5f,
                    primitive.Height * 0.5f,
                    transform);
                break;
            case NativeAnalyticPrimitiveKind.RoundedRectangle:
                visual.Context.DrawRoundedRectangle(
                    fill,
                    pen,
                    rect,
                    primitive.CornerRadius,
                    primitive.CornerRadius,
                    transform);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported analytic primitive {primitive.Kind}.");
        }
    }
    return visual;
}

static NativeGeometryPrimitive[] CreateGeometryPrimitives(
    int count,
    int forcedKind,
    int forcedLineMode,
    int forcedStartCap,
    int forcedEndCap,
    bool curvesOnly,
    float logicalWidth,
    float logicalHeight)
{
    var result = new NativeGeometryPrimitive[count];
    const float inset = 24f;
    float usableWidth = logicalWidth - inset * 2f;
    float usableHeight = logicalHeight - inset * 2f;
    NativeStrokeCap startCap = forcedStartCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedStartCap
        : NativeStrokeCap.Flat;
    NativeStrokeCap endCap = forcedEndCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedEndCap
        : NativeStrokeCap.Flat;
    int columns = Math.Max(
        1,
        (int)MathF.Ceiling(MathF.Sqrt(count * usableWidth / usableHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = usableWidth / columns;
    float cellHeight = usableHeight / rows;
    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        float itemWidth = Math.Max(2f, cellWidth * 0.58f);
        float itemHeight = Math.Max(2f, cellHeight * 0.52f);
        Vector2 center = new(
            inset + (column + 0.5f) * cellWidth,
            inset + (row + 0.5f) * cellHeight);
        Vector4 color = new(
            0.16f + 0.68f * Wave(phase),
            0.2f + 0.72f * Wave(phase + 0.333f),
            0.25f + 0.7f * Wave(phase + 0.666f),
            0.6f + 0.35f * Wave(phase + 0.17f));
        Matrix3x2 transform =
            Matrix3x2.CreateScale(
                0.82f + 0.36f * Wave(phase + 0.21f),
                0.76f + 0.48f * Wave(phase + 0.49f)) *
            Matrix3x2.CreateSkew(
                (Wave(phase + 0.77f) - 0.5f) * 0.24f,
                (Wave(phase + 0.43f) - 0.5f) * 0.12f) *
            Matrix3x2.CreateRotation(
                (Wave(phase + 0.91f) - 0.5f) * 0.32f) *
            Matrix3x2.CreateTranslation(center);
        int kind = forcedKind is >= 0 and <= 4
            ? forcedKind
            : curvesOnly ? 3 + index % 2 : index % 3;
        switch (kind)
        {
            case 0:
                int lineMode = forcedLineMode is >= 0 and <= 2
                    ? forcedLineMode
                    : index % 9 / 3;
                NativeGeometryPrimitiveFlags flags = lineMode switch
                {
                    0 => NativeGeometryPrimitiveFlags.Hairline,
                    1 => NativeGeometryPrimitiveFlags.FixedDeviceStroke,
                    _ => NativeGeometryPrimitiveFlags.None
                };
                result[index] = new NativeGeometryPrimitive(
                    NativeGeometryPrimitiveKind.Line,
                    new Vector2(-itemWidth * 0.5f, -itemHeight * 0.22f),
                    new Vector2(itemWidth * 0.5f, itemHeight * 0.22f),
                    color,
                    transform,
                    strokeThickness: flags == NativeGeometryPrimitiveFlags.Hairline
                        ? 0f
                        : 1f + index % 4,
                    flags: flags,
                    startCap: startCap,
                    endCap: endCap);
                break;
            case 1:
                result[index] = new NativeGeometryPrimitive(
                    NativeGeometryPrimitiveKind.Triangle,
                    new Vector2(-itemWidth * 0.5f, itemHeight * 0.45f),
                    new Vector2(0f, -itemHeight * 0.5f),
                    color,
                    transform,
                    p2: new Vector2(itemWidth * 0.5f, itemHeight * 0.45f));
                break;
            case 2:
                result[index] = new NativeGeometryPrimitive(
                    NativeGeometryPrimitiveKind.Quadrilateral,
                    new Vector2(-itemWidth * 0.5f, -itemHeight * 0.35f),
                    new Vector2(itemWidth * 0.35f, -itemHeight * 0.5f),
                    color,
                    transform,
                    p2: new Vector2(itemWidth * 0.5f, itemHeight * 0.35f),
                    p3: new Vector2(-itemWidth * 0.35f, itemHeight * 0.5f));
                break;
            case 3:
            case 4:
                int curveLineMode = forcedLineMode is >= 0 and <= 2
                    ? forcedLineMode
                    : index % 9 / 3;
                NativeGeometryPrimitiveFlags curveFlags = curveLineMode switch
                {
                    0 => NativeGeometryPrimitiveFlags.Hairline,
                    1 => NativeGeometryPrimitiveFlags.FixedDeviceStroke,
                    _ => NativeGeometryPrimitiveFlags.None
                };
                result[index] = new NativeGeometryPrimitive(
                    kind == 3
                        ? NativeGeometryPrimitiveKind.QuadraticBezier
                        : NativeGeometryPrimitiveKind.CubicBezier,
                    new Vector2(-itemWidth * 0.5f, itemHeight * 0.22f),
                    new Vector2(-itemWidth * 0.18f, -itemHeight * 0.62f),
                    color,
                    transform,
                    p2: new Vector2(itemWidth * 0.18f, itemHeight * 0.58f),
                    p3: new Vector2(itemWidth * 0.5f, -itemHeight * 0.18f),
                    strokeThickness: curveFlags == NativeGeometryPrimitiveFlags.Hairline
                        ? 0f
                        : 1f + index % 4,
                    flags: curveFlags,
                    startCap: startCap,
                    endCap: endCap);
                break;
            default:
                throw new InvalidOperationException("Unsupported geometry kind.");
        }
    }
    return result;

    static float Wave(float phase) =>
        0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
}

static (Vector2[] Points,
        NativePolyline[] Polylines,
        double[] Doubles,
        NativeDashStyle[] DashStyles,
        NativeSpline[] Splines) CreateDashedPolylines(
    int count,
    int forcedLineMode,
    int forcedStartCap,
    int forcedEndCap,
    int forcedJoin,
    float logicalWidth,
    float logicalHeight)
{
    var scene = CreatePolylines(
        count,
        forcedLineMode is >= 0 and <= 2 ? forcedLineMode : 2,
        forcedStartCap,
        forcedEndCap,
        forcedJoin,
        logicalWidth,
        logicalHeight);
    var polylines = scene.Polylines;
    for (int index = 0; index < polylines.Length; index++)
    {
        NativePolyline source = polylines[index];
        polylines[index] = new NativePolyline(
            source.PointOffset,
            source.PointCount,
            source.Color,
            source.Transform,
            source.StrokeThickness,
            source.MiterLimit,
            source.Flags,
            source.StartCap,
            source.EndCap,
            source.LineJoin,
            source.IsClosed,
            dashStyle: 1);
    }
    double[] intervals = [1.75, 0.9, 0.45];
    NativeDashStyle[] styles =
    [
        new NativeDashStyle(
            0,
            (nuint)intervals.Length,
            -0.35,
            NativeStrokeCap.Round)
    ];
    return (scene.Points, polylines, intervals, styles, []);
}

static (Vector2[] Points,
        NativePolyline[] Polylines,
        double[] Doubles,
        NativeDashStyle[] DashStyles,
        NativeSpline[] Splines) CreatePolylines(
    int count,
    int forcedLineMode,
    int forcedStartCap,
    int forcedEndCap,
    int forcedJoin,
    float logicalWidth,
    float logicalHeight)
{
    const int pointsPerPolyline = 4;
    var points = new Vector2[count * pointsPerPolyline];
    var polylines = new NativePolyline[count];
    const float inset = 24f;
    float usableWidth = logicalWidth - inset * 2f;
    float usableHeight = logicalHeight - inset * 2f;
    int columns = Math.Max(
        1,
        (int)MathF.Ceiling(MathF.Sqrt(count * usableWidth / usableHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = usableWidth / columns;
    float cellHeight = usableHeight / rows;
    NativeStrokeCap startCap = forcedStartCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedStartCap
        : NativeStrokeCap.Round;
    NativeStrokeCap endCap = forcedEndCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedEndCap
        : NativeStrokeCap.Triangle;

    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        float itemWidth = Math.Max(3f, cellWidth * 0.62f);
        float itemHeight = Math.Max(3f, cellHeight * 0.58f);
        int offset = index * pointsPerPolyline;
        points[offset] = new(-itemWidth * 0.5f, itemHeight * 0.22f);
        points[offset + 1] = new(-itemWidth * 0.18f, -itemHeight * 0.46f);
        points[offset + 2] = new(itemWidth * 0.16f, itemHeight * 0.44f);
        points[offset + 3] = new(itemWidth * 0.5f, -itemHeight * 0.18f);
        Vector2 center = new(
            inset + (column + 0.5f) * cellWidth,
            inset + (row + 0.5f) * cellHeight);
        Matrix3x2 transform =
            Matrix3x2.CreateScale(
                0.8f + 0.4f * Wave(phase + 0.21f),
                0.72f + 0.56f * Wave(phase + 0.49f)) *
            Matrix3x2.CreateSkew(
                (Wave(phase + 0.77f) - 0.5f) * 0.28f,
                (Wave(phase + 0.43f) - 0.5f) * 0.14f) *
            Matrix3x2.CreateRotation(
                (Wave(phase + 0.91f) - 0.5f) * 0.36f) *
            Matrix3x2.CreateTranslation(center);
        Vector4 color = new(
            0.16f + 0.68f * Wave(phase),
            0.2f + 0.72f * Wave(phase + 0.333f),
            0.25f + 0.7f * Wave(phase + 0.666f),
            0.6f + 0.35f * Wave(phase + 0.17f));
        int lineMode = forcedLineMode is >= 0 and <= 2
            ? forcedLineMode
            : index % 9 / 3;
        NativePolylineFlags flags = lineMode switch
        {
            0 => NativePolylineFlags.Hairline,
            1 => NativePolylineFlags.FixedDeviceStroke,
            _ => NativePolylineFlags.None
        };
        NativeStrokeJoin join = forcedJoin is >= 0 and <= 2
            ? (NativeStrokeJoin)forcedJoin
            : (NativeStrokeJoin)(index % 3);
        polylines[index] = new NativePolyline(
            (nuint)offset,
            (nuint)pointsPerPolyline,
            color,
            transform,
            flags == NativePolylineFlags.Hairline ? 0f : 1f + index % 4,
            miterLimit: 2f + index % 5,
            flags: flags,
            startCap: startCap,
            endCap: endCap,
            lineJoin: join,
            isClosed: index % 4 == 3);
    }
    return (points, polylines, [], [], []);

    static float Wave(float phase) =>
        0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
}

static (Vector2[] Points,
        NativePolyline[] Polylines,
        double[] Doubles,
        NativeDashStyle[] DashStyles,
        NativeSpline[] Splines) CreateSplines(
    int count,
    int forcedLineMode,
    int forcedStartCap,
    int forcedEndCap,
    int forcedJoin,
    float logicalWidth,
    float logicalHeight)
{
    const int controlPointsPerSpline = 6;
    const int knotsPerSpline = 10;
    const int weightsPerSpline = 6;
    const int doublesPerSpline = knotsPerSpline + weightsPerSpline;
    var points = new Vector2[count * controlPointsPerSpline];
    var doubles = new double[count * doublesPerSpline];
    var splines = new NativeSpline[count];
    const float inset = 24f;
    float usableWidth = logicalWidth - inset * 2f;
    float usableHeight = logicalHeight - inset * 2f;
    int columns = Math.Max(
        1,
        (int)MathF.Ceiling(MathF.Sqrt(count * usableWidth / usableHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = usableWidth / columns;
    float cellHeight = usableHeight / rows;
    NativeStrokeCap startCap = forcedStartCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedStartCap
        : NativeStrokeCap.Round;
    NativeStrokeCap endCap = forcedEndCap is >= 0 and <= 3
        ? (NativeStrokeCap)forcedEndCap
        : NativeStrokeCap.Triangle;
    ReadOnlySpan<double> knots = [0, 0, 0, 0, 1, 2, 3, 3, 3, 3];

    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        float itemWidth = Math.Max(4f, cellWidth * 0.72f);
        float itemHeight = Math.Max(4f, cellHeight * 0.68f);
        int pointOffset = index * controlPointsPerSpline;
        points[pointOffset] = new(-itemWidth * 0.5f, itemHeight * 0.12f);
        points[pointOffset + 1] = new(-itemWidth * 0.34f, -itemHeight * 0.5f);
        points[pointOffset + 2] = new(-itemWidth * 0.1f, itemHeight * 0.48f);
        points[pointOffset + 3] = new(itemWidth * 0.12f, -itemHeight * 0.46f);
        points[pointOffset + 4] = new(itemWidth * 0.34f, itemHeight * 0.5f);
        points[pointOffset + 5] = new(itemWidth * 0.5f, -itemHeight * 0.1f);
        int doubleOffset = index * doublesPerSpline;
        knots.CopyTo(doubles.AsSpan(doubleOffset, knotsPerSpline));
        for (int weight = 0; weight < weightsPerSpline; weight++)
        {
            doubles[doubleOffset + knotsPerSpline + weight] =
                0.78 + 0.44 * Wave(phase + weight * 0.137f);
        }
        Vector2 center = new(
            inset + (column + 0.5f) * cellWidth,
            inset + (row + 0.5f) * cellHeight);
        Matrix3x2 transform =
            Matrix3x2.CreateScale(
                0.82f + 0.38f * Wave(phase + 0.21f),
                0.74f + 0.52f * Wave(phase + 0.49f)) *
            Matrix3x2.CreateSkew(
                (Wave(phase + 0.77f) - 0.5f) * 0.24f,
                (Wave(phase + 0.43f) - 0.5f) * 0.12f) *
            Matrix3x2.CreateRotation(
                (Wave(phase + 0.91f) - 0.5f) * 0.32f) *
            Matrix3x2.CreateTranslation(center);
        Vector4 color = new(
            0.16f + 0.68f * Wave(phase),
            0.2f + 0.72f * Wave(phase + 0.333f),
            0.25f + 0.7f * Wave(phase + 0.666f),
            0.6f + 0.35f * Wave(phase + 0.17f));
        int lineMode = forcedLineMode is >= 0 and <= 2
            ? forcedLineMode
            : index % 9 / 3;
        NativePolylineFlags flags = lineMode switch
        {
            0 => NativePolylineFlags.Hairline,
            1 => NativePolylineFlags.FixedDeviceStroke,
            _ => NativePolylineFlags.None
        };
        NativeStrokeJoin join = forcedJoin is >= 0 and <= 2
            ? (NativeStrokeJoin)forcedJoin
            : (NativeStrokeJoin)(index % 3);
        var stroke = new NativePolyline(
            (nuint)pointOffset,
            controlPointsPerSpline,
            color,
            transform,
            flags == NativePolylineFlags.Hairline ? 0f : 1f + index % 4,
            miterLimit: 2f + index % 5,
            flags: flags,
            startCap: startCap,
            endCap: endCap,
            lineJoin: join,
            isClosed: index % 4 == 3);
        splines[index] = new NativeSpline(
            stroke,
            (nuint)doubleOffset,
            knotsPerSpline,
            degree: 3,
            weightOffset: (nuint)(doubleOffset + knotsPerSpline),
            weightCount: weightsPerSpline);
    }
    return (points, [], doubles, [], splines);

    static float Wave(float phase) =>
        0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
}

static DrawingVisual CreateManagedGeometryVisual(
    ReadOnlySpan<NativeGeometryPrimitive> primitives,
    ReadOnlySpan<Vector2> points,
    ReadOnlySpan<NativePolyline> polylines,
    ReadOnlySpan<double> doubles,
    ReadOnlySpan<NativeDashStyle> dashStyles,
    ReadOnlySpan<NativeSpline> splines,
    float logicalWidth,
    float logicalHeight)
{
    var visual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    foreach (ref readonly NativeGeometryPrimitive primitive in primitives)
    {
        var brush = new SolidColorBrush(primitive.Color);
        Matrix3x2 affine = primitive.Transform;
        var transform = new Matrix4x4(
            affine.M11, affine.M12, 0f, 0f,
            affine.M21, affine.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            affine.M31, affine.M32, 0f, 1f);
        switch (primitive.Kind)
        {
            case NativeGeometryPrimitiveKind.Line:
                var mode = (primitive.Flags &
                    NativeGeometryPrimitiveFlags.FixedDeviceStroke) != 0
                    ? PenStrokeTransformMode.Fixed
                    : PenStrokeTransformMode.Normal;
                float thickness = (primitive.Flags &
                    NativeGeometryPrimitiveFlags.Hairline) != 0
                    ? Pen.HairlineThickness
                    : primitive.StrokeThickness;
                visual.Context.DrawLine(
                    new Pen(
                        brush,
                        thickness,
                        startLineCap: ToPenLineCap(primitive.StartCap),
                        endLineCap: ToPenLineCap(primitive.EndCap),
                        strokeTransformMode: mode),
                    primitive.P0,
                    primitive.P1,
                    transform);
                break;
            case NativeGeometryPrimitiveKind.Triangle:
                visual.Context.FillTriangle(
                    brush,
                    Vector2.Transform(primitive.P0, affine),
                    Vector2.Transform(primitive.P1, affine),
                    Vector2.Transform(primitive.P2, affine));
                break;
            case NativeGeometryPrimitiveKind.Quadrilateral:
                visual.Context.FillQuad(
                    brush,
                    Vector2.Transform(primitive.P0, affine),
                    Vector2.Transform(primitive.P1, affine),
                    Vector2.Transform(primitive.P2, affine),
                    Vector2.Transform(primitive.P3, affine));
                break;
            case NativeGeometryPrimitiveKind.QuadraticBezier:
            case NativeGeometryPrimitiveKind.CubicBezier:
                var curveMode = (primitive.Flags &
                    NativeGeometryPrimitiveFlags.FixedDeviceStroke) != 0
                    ? PenStrokeTransformMode.Fixed
                    : PenStrokeTransformMode.Normal;
                float curveThickness = (primitive.Flags &
                    NativeGeometryPrimitiveFlags.Hairline) != 0
                    ? Pen.HairlineThickness
                    : primitive.StrokeThickness;
                var curvePen = new Pen(
                    brush,
                    curveThickness,
                    startLineCap: ToPenLineCap(primitive.StartCap),
                    endLineCap: ToPenLineCap(primitive.EndCap),
                    strokeTransformMode: curveMode);
                visual.Context.Commands.Add(new RenderCommand
                {
                    Type = primitive.Kind == NativeGeometryPrimitiveKind.QuadraticBezier
                        ? RenderCommandType.DrawBezier
                        : RenderCommandType.DrawCubicBezier,
                    Pen = curvePen,
                    Position = primitive.P0,
                    Position2 = primitive.P1,
                    Position3 = primitive.P2,
                    Position4 = primitive.P3,
                    Transform = transform,
                    IsPenThicknessLocal = true,
                    IsEdgeAliased = (primitive.Flags &
                        NativeGeometryPrimitiveFlags.EdgeAliased) != 0,
                    GeometryCache = primitive.StartCap != NativeStrokeCap.Flat ||
                        primitive.EndCap != NativeStrokeCap.Flat
                        ? RenderCommandGeometryCache.ForStrokePath(
                            primitive.Kind == NativeGeometryPrimitiveKind.QuadraticBezier
                                ? RenderCommandGeometryCache.CreateQuadraticBezierPath(
                                    primitive.P0,
                                    primitive.P1,
                                    primitive.P2)
                                : RenderCommandGeometryCache.CreateCubicBezierPath(
                                    primitive.P0,
                                    primitive.P1,
                                    primitive.P2,
                                    primitive.P3))
                        : null
                });
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported geometry primitive {primitive.Kind}.");
        }
    }
    foreach (ref readonly NativePolyline polyline in polylines)
    {
        int offset = checked((int)polyline.PointOffset);
        int count = checked((int)polyline.PointCount);
        var brush = new SolidColorBrush(polyline.Color);
        var mode = (polyline.Flags &
            NativePolylineFlags.FixedDeviceStroke) != 0
            ? PenStrokeTransformMode.Fixed
            : PenStrokeTransformMode.Normal;
        float thickness = (polyline.Flags & NativePolylineFlags.Hairline) != 0
            ? Pen.HairlineThickness
            : polyline.StrokeThickness;
        double[]? dashArray = null;
        double dashOffset = 0.0;
        PenLineCap dashCap = PenLineCap.Flat;
        if (polyline.DashStyle != 0U)
        {
            NativeDashStyle dash = dashStyles[
                checked((int)polyline.DashStyle - 1)];
            dashArray = doubles.Slice(
                checked((int)dash.IntervalOffset),
                checked((int)dash.IntervalCount)).ToArray();
            dashOffset = dash.Offset;
            dashCap = ToPenLineCap(dash.Cap);
        }
        visual.Context.DrawPolyline(
            new Pen(
                brush,
                thickness,
                lineJoin: ToPenLineJoin(polyline.LineJoin),
                miterLimit: polyline.MiterLimit,
                startLineCap: ToPenLineCap(polyline.StartCap),
                endLineCap: ToPenLineCap(polyline.EndCap),
                dashCap: dashCap,
                dashArray: dashArray,
                dashOffset: dashOffset,
                strokeTransformMode: mode),
            points.Slice(offset, count),
            polyline.IsClosed);
        RenderCommand command = visual.Context.Commands[^1];
        Matrix3x2 affine = polyline.Transform;
        command.Transform = new Matrix4x4(
            affine.M11, affine.M12, 0f, 0f,
            affine.M21, affine.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            affine.M31, affine.M32, 0f, 1f);
        command.IsPenThicknessLocal = true;
        command.IsEdgeAliased =
            (polyline.Flags & NativePolylineFlags.EdgeAliased) != 0;
        visual.Context.Commands[^1] = command;
    }
    foreach (ref readonly NativeSpline spline in splines)
    {
        NativePolyline stroke = spline.Stroke;
        int pointOffset = checked((int)stroke.PointOffset);
        int pointCount = checked((int)stroke.PointCount);
        int knotOffset = checked((int)spline.KnotOffset);
        int knotCount = checked((int)spline.KnotCount);
        int weightOffset = checked((int)spline.WeightOffset);
        int weightCount = checked((int)spline.WeightCount);
        var brush = new SolidColorBrush(stroke.Color);
        var mode = (stroke.Flags & NativePolylineFlags.FixedDeviceStroke) != 0
            ? PenStrokeTransformMode.Fixed
            : PenStrokeTransformMode.Normal;
        float thickness = (stroke.Flags & NativePolylineFlags.Hairline) != 0
            ? Pen.HairlineThickness
            : stroke.StrokeThickness;
        double[]? dashArray = null;
        double dashOffset = 0.0;
        PenLineCap dashCap = PenLineCap.Flat;
        if (stroke.DashStyle != 0U)
        {
            NativeDashStyle dash = dashStyles[
                checked((int)stroke.DashStyle - 1)];
            dashArray = doubles.Slice(
                checked((int)dash.IntervalOffset),
                checked((int)dash.IntervalCount)).ToArray();
            dashOffset = dash.Offset;
            dashCap = ToPenLineCap(dash.Cap);
        }
        visual.Context.DrawSpline(
            new Pen(
                brush,
                thickness,
                lineJoin: ToPenLineJoin(stroke.LineJoin),
                miterLimit: stroke.MiterLimit,
                startLineCap: ToPenLineCap(stroke.StartCap),
                endLineCap: ToPenLineCap(stroke.EndCap),
                dashCap: dashCap,
                dashArray: dashArray,
                dashOffset: dashOffset,
                strokeTransformMode: mode),
            points.Slice(pointOffset, pointCount),
            doubles.Slice(knotOffset, knotCount),
            weightCount == 0
                ? ReadOnlySpan<double>.Empty
                : doubles.Slice(weightOffset, weightCount),
            checked((int)spline.Degree),
            stroke.IsClosed);
        RenderCommand command = visual.Context.Commands[^1];
        Matrix3x2 affine = stroke.Transform;
        command.Transform = new Matrix4x4(
            affine.M11, affine.M12, 0f, 0f,
            affine.M21, affine.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            affine.M31, affine.M32, 0f, 1f);
        command.IsPenThicknessLocal = true;
        command.IsEdgeAliased =
            (stroke.Flags & NativePolylineFlags.EdgeAliased) != 0;
        visual.Context.Commands[^1] = command;
    }
    return visual;

    static PenLineCap ToPenLineCap(NativeStrokeCap cap) => cap switch
    {
        NativeStrokeCap.Square => PenLineCap.Square,
        NativeStrokeCap.Round => PenLineCap.Round,
        NativeStrokeCap.Triangle => PenLineCap.Triangle,
        _ => PenLineCap.Flat
    };

    static PenLineJoin ToPenLineJoin(NativeStrokeJoin join) => join switch
    {
        NativeStrokeJoin.Bevel => PenLineJoin.Bevel,
        NativeStrokeJoin.Round => PenLineJoin.Round,
        _ => PenLineJoin.Miter
    };
}

static (NativePathFill[] Paths, NativePathSegment[] Segments) CreateNativePaths(
    int count,
    float logicalWidth,
    float logicalHeight,
    bool forceUniqueOutlines)
{
    const float radius = 12f;
    const float kappa = 0.55228475f;
    var baseSegments = new[]
    {
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(0f, -radius),
            new Vector2(radius * kappa, -radius),
            new Vector2(radius, -radius * kappa),
            new Vector2(radius, 0f)),
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(radius, 0f),
            new Vector2(radius, radius * kappa),
            new Vector2(radius * kappa, radius),
            new Vector2(0f, radius)),
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(0f, radius),
            new Vector2(-radius * kappa, radius),
            new Vector2(-radius, radius * kappa),
            new Vector2(-radius, 0f)),
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(-radius, 0f),
            new Vector2(-radius, -radius * kappa),
            new Vector2(-radius * kappa, -radius),
            new Vector2(0f, -radius))
    };
    NativePathSegment[] segments = forceUniqueOutlines
        ? new NativePathSegment[count * baseSegments.Length]
        : baseSegments;
    if (forceUniqueOutlines)
    {
        for (int index = 0; index < count; index++)
        {
            baseSegments.CopyTo(segments, index * baseSegments.Length);
        }
    }
    var paths = new NativePathFill[count];
    int columns = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(
        count * logicalWidth / logicalHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = logicalWidth / columns;
    float cellHeight = logicalHeight / rows;
    float scale = forceUniqueOutlines
        ? 1.5f
        : MathF.Min(cellWidth, cellHeight) / (radius * 2.8f);
    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        Vector4 color = new(
            0.2f + 0.7f * (0.5f + 0.5f * MathF.Sin(phase * MathF.Tau)),
            0.25f + 0.65f * (0.5f + 0.5f * MathF.Sin((phase + 0.333f) * MathF.Tau)),
            0.3f + 0.6f * (0.5f + 0.5f * MathF.Sin((phase + 0.666f) * MathF.Tau)),
            1f);
        Matrix3x2 transform = Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation(
                (column + 0.5f) * cellWidth,
                (row + 0.5f) * cellHeight);
        paths[index] = new NativePathFill(
            forceUniqueOutlines ? (nuint)(index * baseSegments.Length) : 0,
            (nuint)baseSegments.Length,
            new Vector2(-radius),
            new Vector2(radius),
            color,
            transform);
    }
    return (paths, segments);
}

static (NativeScenePathFill[] Paths, NativePathSegment[] Segments)
    CreateSemanticPaths(
        int count,
        float logicalWidth,
        float logicalHeight,
        float xOffset)
{
    (NativePathFill[] sourcePaths, NativePathSegment[] segments) =
        CreateNativePaths(
            count,
            logicalWidth,
            logicalHeight,
            forceUniqueOutlines: false);
    var paths = new NativeScenePathFill[sourcePaths.Length];
    for (int index = 0; index < sourcePaths.Length; ++index)
    {
        NativePathFill source = sourcePaths[index];
        Matrix3x2 transform = source.Transform;
        transform.M31 += xOffset;
        paths[index] = new NativeScenePathFill(
            (ulong)source.SegmentOffset,
            (ulong)source.SegmentCount,
            source.Minimum,
            source.Maximum,
            source.Color,
            transform,
            source.FillRule,
            source.SampleGrid);
    }
    return (paths, segments);
}

static (
    NativeSceneBrush[] Brushes,
    uint[] AnalyticIndices,
    uint[] PathIndices) CreateSemanticSolidBrushes(
    ReadOnlySpan<NativeAnalyticPrimitive> analyticPrimitives,
    ReadOnlySpan<NativeScenePathFill> paths)
{
    var brushes = new NativeSceneBrush[
        analyticPrimitives.Length + paths.Length];
    var analyticIndices = new uint[analyticPrimitives.Length];
    var pathIndices = new uint[paths.Length];
    int brushIndex = 0;
    for (int index = 0; index < analyticPrimitives.Length; ++index)
    {
        brushes[brushIndex] = NativeSceneBrush.Solid(
            analyticPrimitives[index].Color);
        analyticIndices[index] = checked((uint)brushIndex++);
    }
    for (int index = 0; index < paths.Length; ++index)
    {
        brushes[brushIndex] = NativeSceneBrush.Solid(paths[index].Color);
        pathIndices[index] = checked((uint)brushIndex++);
    }
    return (brushes, analyticIndices, pathIndices);
}

static (
    NativeSceneGlyphOutline[] Outlines,
    NativePathSegment[] Segments,
    NativePositionedGlyph[] Glyphs,
    ushort[] GlyphIndices,
    Vector2[] GlyphPositions) CreateSemanticGlyphScene(
        TtfFont font,
        int count,
        float dpiScale,
        float logicalWidth,
        float logicalHeight,
        float yOffset)
{
    (NativeGlyphOutline[] sourceOutlines,
     NativePathSegment[] segments,
     NativePositionedGlyph[] sourceGlyphs,
     ushort[] glyphIndices,
     Vector2[] glyphPositions) = CreateGlyphScene(
        font,
        count,
        dpiScale,
        logicalWidth,
        logicalHeight,
        forceUniqueOutlines: false);
    var outlines = new NativeSceneGlyphOutline[sourceOutlines.Length];
    for (int index = 0; index < sourceOutlines.Length; ++index)
    {
        NativeGlyphOutline source = sourceOutlines[index];
        outlines[index] = new NativeSceneGlyphOutline(
            (ulong)source.SegmentOffset,
            (ulong)source.SegmentCount,
            source.Minimum,
            source.Maximum,
            source.RasterScale,
            source.SubpixelX);
    }
    var glyphs = new NativePositionedGlyph[sourceGlyphs.Length];
    for (int index = 0; index < sourceGlyphs.Length; ++index)
    {
        NativePositionedGlyph source = sourceGlyphs[index];
        glyphs[index] = new NativePositionedGlyph(
            source.OutlineIndex,
            source.Position + new Vector2(0f, yOffset),
            source.BasisX,
            source.BasisY,
            source.Color,
            source.AtlasToLogicalScale,
            source.BoldOffset,
            source.ItalicSkew);
    }
    return (outlines, segments, glyphs, glyphIndices, glyphPositions);
}

static (NativeClipChain Native, PathGeometry Managed) CreateVectorClipChain(
    float logicalWidth,
    float logicalHeight)
{
    const float kappa = 0.55228475f;
    var segments = new[]
    {
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(0f, -1f),
            new Vector2(kappa, -1f),
            new Vector2(1f, -kappa),
            new Vector2(1f, 0f)),
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(1f, 0f),
            new Vector2(1f, kappa),
            new Vector2(kappa, 1f),
            new Vector2(0f, 1f)),
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(0f, 1f),
            new Vector2(-kappa, 1f),
            new Vector2(-1f, kappa),
            new Vector2(-1f, 0f)),
        new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(-1f, 0f),
            new Vector2(-1f, -kappa),
            new Vector2(-kappa, -1f),
            new Vector2(0f, -1f))
    };
    Matrix3x2 outerTransform = Matrix3x2.CreateScale(
            logicalWidth * 0.34f,
            logicalHeight * 0.34f) *
        Matrix3x2.CreateRotation(0.12f) *
        Matrix3x2.CreateTranslation(
            logicalWidth * 0.5f,
            logicalHeight * 0.5f);
    Matrix3x2 holeTransform = Matrix3x2.CreateScale(
            logicalWidth * 0.12f,
            logicalHeight * 0.15f) *
        Matrix3x2.CreateSkew(0.18f, -0.08f) *
        Matrix3x2.CreateRotation(-0.35f) *
        Matrix3x2.CreateTranslation(
            logicalWidth * 0.54f,
            logicalHeight * 0.48f);
    var paths = new[]
    {
        new NativeClipPath(
            0U,
            (nuint)segments.Length,
            new Vector2(-1f),
            new Vector2(1f),
            outerTransform,
            NativeClipOperation.Intersect,
            sampleGrid: 8U),
        new NativeClipPath(
            0U,
            (nuint)segments.Length,
            new Vector2(-1f),
            new Vector2(1f),
            holeTransform,
            NativeClipOperation.Difference,
            sampleGrid: 8U)
    };

    PathGeometry unitCircle = new();
    PathFigure figure = new(new Vector2(0f, -1f), isClosed: true);
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(kappa, -1f),
        new Vector2(1f, -kappa),
        new Vector2(1f, 0f)));
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(1f, kappa),
        new Vector2(kappa, 1f),
        new Vector2(0f, 1f)));
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(-kappa, 1f),
        new Vector2(-1f, kappa),
        new Vector2(-1f, 0f)));
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(-1f, -kappa),
        new Vector2(-kappa, -1f),
        new Vector2(0f, -1f)));
    unitCircle.Figures.Add(figure);

    var managed = new PathGeometry
    {
        IsCombined = true,
        PathA = unitCircle.CreateTransformed(ToMatrix4x4(outerTransform)),
        PathB = unitCircle.CreateTransformed(ToMatrix4x4(holeTransform)),
        Op = 0,
        FillRule = FillRule.Nonzero
    };
    return (new NativeClipChain(paths, segments), managed);
}

static Matrix4x4 ToMatrix4x4(Matrix3x2 value) => new(
    value.M11, value.M12, 0f, 0f,
    value.M21, value.M22, 0f, 0f,
    0f, 0f, 1f, 0f,
    value.M31, value.M32, 0f, 1f);

static DrawingVisual CreateManagedPathVisual(
    int count,
    float logicalWidth,
    float logicalHeight,
    bool forceAtlasGrowth)
{
    const float radius = 12f;
    const float kappa = 0.55228475f;
    var path = new PathGeometry();
    var figure = new PathFigure(new Vector2(0f, -radius), isClosed: true);
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(radius * kappa, -radius),
        new Vector2(radius, -radius * kappa),
        new Vector2(radius, 0f)));
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(radius, radius * kappa),
        new Vector2(radius * kappa, radius),
        new Vector2(0f, radius)));
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(-radius * kappa, radius),
        new Vector2(-radius, radius * kappa),
        new Vector2(-radius, 0f)));
    figure.Segments.Add(new CubicBezierSegment(
        new Vector2(-radius, -radius * kappa),
        new Vector2(-radius * kappa, -radius),
        new Vector2(0f, -radius)));
    path.Figures.Add(figure);

    var visual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    int columns = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(
        count * logicalWidth / logicalHeight)));
    int rows = (count + columns - 1) / columns;
    float cellWidth = logicalWidth / columns;
    float cellHeight = logicalHeight / rows;
    float scale = forceAtlasGrowth
        ? 1.5f
        : MathF.Min(cellWidth, cellHeight) / (radius * 2.8f);
    for (int index = 0; index < count; index++)
    {
        int column = index % columns;
        int row = index / columns;
        float phase = index * 0.61803398875f % 1f;
        Vector4 color = new(
            0.2f + 0.7f * (0.5f + 0.5f * MathF.Sin(phase * MathF.Tau)),
            0.25f + 0.65f * (0.5f + 0.5f * MathF.Sin((phase + 0.333f) * MathF.Tau)),
            0.3f + 0.6f * (0.5f + 0.5f * MathF.Sin((phase + 0.666f) * MathF.Tau)),
            1f);
        Matrix3x2 affine = Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation(
                (column + 0.5f) * cellWidth,
                (row + 0.5f) * cellHeight);
        visual.Context.DrawPath(
            new SolidColorBrush(color),
            pen: null,
            path,
            new Matrix4x4(
                affine.M11, affine.M12, 0f, 0f,
                affine.M21, affine.M22, 0f, 0f,
                0f, 0f, 1f, 0f,
                affine.M31, affine.M32, 0f, 1f));
    }
    return visual;
}

static (
    NativeGlyphOutline[] Outlines,
    NativePathSegment[] Segments,
    NativePositionedGlyph[] Glyphs,
    ushort[] GlyphIndices,
    Vector2[] GlyphPositions) CreateGlyphScene(
    TtfFont font,
    int count,
    float dpiScale,
    float logicalWidth,
    float logicalHeight,
    bool forceUniqueOutlines)
{
    const float fontSize = 20f;
    const string alphabet = "ProGPUWebNative0123456789ABCDEFGHJKLMNQRSTUVXYZ";
    float targetRasterSize = Math.Clamp(fontSize * dpiScale, 4f, 256f);
    float rasterScale = targetRasterSize / font.UnitsPerEm;
    float atlasToLogicalScale = fontSize * dpiScale / targetRasterSize;
    float subpixelX = targetRasterSize <= 24f ? 0.25f : 0f;
    int columns = Math.Max(1, (int)(logicalWidth / 44f));
    int rows = Math.Max(1, (int)MathF.Ceiling(count / (float)columns));
    float cellHeight = Math.Min(42f, logicalHeight / rows);

    var outlines = new List<NativeGlyphOutline>();
    var segments = new List<NativePathSegment>();
    var glyphs = new List<NativePositionedGlyph>(count);
    var glyphIndices = new ushort[count];
    var glyphPositions = new Vector2[count];
    var outlineIndices = new Dictionary<ushort, uint>();
    Vector4 color = new(0.92f, 0.96f, 1f, 1f);

    for (int index = 0; index < count; index++)
    {
        ushort glyphIndex = font.GetGlyphIndex(alphabet[index % alphabet.Length]);
        int column = index % columns;
        int row = index / columns;
        var managedPosition = new Vector2(
            18f + column * 44f + subpixelX / dpiScale,
            Math.Min(logicalHeight - 10f, 34f + row * cellHeight));
        var nativePosition = new Vector2(
            MathF.Floor(managedPosition.X * dpiScale),
            MathF.Round(managedPosition.Y * dpiScale)) / dpiScale;
        glyphIndices[index] = glyphIndex;
        glyphPositions[index] = managedPosition;

        if (forceUniqueOutlines ||
            !outlineIndices.TryGetValue(glyphIndex, out uint outlineIndex))
        {
            PathGeometry? outline = font.GetGlyphOutline(glyphIndex);
            if (outline == null ||
                !outline.TryGetBounds(out Vector2 minimum, out Vector2 maximum))
            {
                throw new InvalidOperationException(
                    $"Glyph {glyphIndex} has no renderable outline.");
            }
            nuint segmentOffset = (nuint)segments.Count;
            foreach (PathFigure figure in outline.Figures)
            {
                Vector2 current = figure.StartPoint;
                foreach (PathSegment segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case LineSegment line:
                            segments.Add(new NativePathSegment(
                                NativePathSegmentKind.Line,
                                current,
                                line.Point));
                            current = line.Point;
                            break;
                        case QuadraticBezierSegment quadratic:
                            segments.Add(new NativePathSegment(
                                NativePathSegmentKind.Quadratic,
                                current,
                                quadratic.ControlPoint,
                                quadratic.Point));
                            current = quadratic.Point;
                            break;
                        case CubicBezierSegment cubic:
                            segments.Add(new NativePathSegment(
                                NativePathSegmentKind.Cubic,
                                current,
                                cubic.ControlPoint1,
                                cubic.ControlPoint2,
                                cubic.Point));
                            current = cubic.Point;
                            break;
                    }
                }
                if (figure.IsClosed && current != figure.StartPoint)
                {
                    segments.Add(new NativePathSegment(
                        NativePathSegmentKind.Line,
                        current,
                        figure.StartPoint));
                }
            }
            nuint segmentCount = (nuint)segments.Count - segmentOffset;
            if (segmentCount == 0)
            {
                throw new InvalidOperationException(
                    $"Glyph {glyphIndex} has an empty outline.");
            }
            outlineIndex = checked((uint)outlines.Count);
            if (!forceUniqueOutlines)
            {
                outlineIndices.Add(glyphIndex, outlineIndex);
            }
            outlines.Add(new NativeGlyphOutline(
                segmentOffset,
                segmentCount,
                minimum,
                maximum,
                rasterScale,
                subpixelX));
        }

        glyphs.Add(new NativePositionedGlyph(
            outlineIndex,
            nativePosition,
            Vector2.UnitX,
            Vector2.UnitY,
            color,
            atlasToLogicalScale));
    }

    return (
        outlines.ToArray(),
        segments.ToArray(),
        glyphs.ToArray(),
        glyphIndices,
        glyphPositions);
}

static DrawingVisual CreateManagedGlyphVisual(
    TtfFont font,
    ushort[] glyphIndices,
    Vector2[] glyphPositions,
    float logicalWidth,
    float logicalHeight)
{
    var visual = new DrawingVisual
    {
        Size = new Vector2(logicalWidth, logicalHeight)
    };
    visual.Context.DrawGlyphRun(
        glyphIndices,
        glyphPositions,
        font,
        20f,
        new SolidColorBrush(new Vector4(0.92f, 0.96f, 1f, 1f)),
        Vector2.Zero,
        textRenderingMode: TextRenderingMode.Grayscale,
        preferGlyphAtlas: true);
    return visual;
}

static byte[] ApplySeparableBoxBlur(
    ReadOnlySpan<byte> source,
    int width,
    int height,
    int radius)
{
    if (radius == 0)
        return source.ToArray();
    if (source.Length != checked(width * height * 4))
        throw new ArgumentException("The RGBA8 oracle source has an invalid size.", nameof(source));

    byte[] horizontal = new byte[source.Length];
    byte[] output = new byte[source.Length];
    int sampleCount = checked(radius * 2 + 1);
    for (int y = 0; y < height; ++y)
    {
        for (int x = 0; x < width; ++x)
        {
            int outputOffset = (y * width + x) * 4;
            for (int channel = 0; channel < 4; ++channel)
            {
                int sum = 0;
                for (int offset = -radius; offset <= radius; ++offset)
                {
                    int sampleX = x + offset;
                    if ((uint)sampleX < (uint)width)
                        sum += source[(y * width + sampleX) * 4 + channel];
                }
                horizontal[outputOffset + channel] =
                    (byte)((sum + sampleCount / 2) / sampleCount);
            }
        }
    }
    for (int y = 0; y < height; ++y)
    {
        for (int x = 0; x < width; ++x)
        {
            int outputOffset = (y * width + x) * 4;
            for (int channel = 0; channel < 4; ++channel)
            {
                int sum = 0;
                for (int offset = -radius; offset <= radius; ++offset)
                {
                    int sampleY = y + offset;
                    if ((uint)sampleY < (uint)height)
                        sum += horizontal[(sampleY * width + x) * 4 + channel];
                }
                output[outputOffset + channel] =
                    (byte)((sum + sampleCount / 2) / sampleCount);
            }
        }
    }
    return output;
}

static PixelComparison ComparePixels(
    ReadOnlySpan<byte> native,
    ReadOnlySpan<byte> managed)
{
    if (native.Length != managed.Length || native.Length % 4 != 0)
    {
        throw new InvalidOperationException("Pixel buffers are not comparable.");
    }
    int maximum = 0;
    int pixelsOverTolerance = 0;
    long totalAbsoluteDifference = 0;
    ulong nativeHash = 14695981039346656037UL;
    ulong managedHash = 14695981039346656037UL;
    for (int offset = 0; offset < native.Length; offset += 4)
    {
        bool overTolerance = false;
        for (int channel = 0; channel < 4; channel++)
        {
            int difference = Math.Abs(native[offset + channel] - managed[offset + channel]);
            maximum = Math.Max(maximum, difference);
            totalAbsoluteDifference += difference;
            overTolerance |= difference > 3;
            nativeHash = (nativeHash ^ native[offset + channel]) * 1099511628211UL;
            managedHash = (managedHash ^ managed[offset + channel]) * 1099511628211UL;
        }
        if (overTolerance)
        {
            pixelsOverTolerance++;
        }
    }
    return new PixelComparison(
        native.Length / 4,
        maximum,
        pixelsOverTolerance,
        totalAbsoluteDifference,
        totalAbsoluteDifference / (double)native.Length,
        nativeHash.ToString("X16"),
        managedHash.ToString("X16"));
}

static void WritePpm(
    string path,
    ReadOnlySpan<byte> rgba,
    uint imageWidth,
    uint imageHeight)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
    writer.Write(System.Text.Encoding.ASCII.GetBytes(
        $"P6\n{imageWidth} {imageHeight}\n255\n"));
    for (int offset = 0; offset < rgba.Length; offset += 4)
    {
        writer.Write(rgba[offset]);
        writer.Write(rgba[offset + 1]);
        writer.Write(rgba[offset + 2]);
    }
}

static void WriteDifferencePpm(
    string path,
    ReadOnlySpan<byte> left,
    ReadOnlySpan<byte> right,
    uint imageWidth,
    uint imageHeight,
    int amplification)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
    writer.Write(System.Text.Encoding.ASCII.GetBytes(
        $"P6\n{imageWidth} {imageHeight}\n255\n"));
    for (int offset = 0; offset < left.Length; offset += 4)
    {
        writer.Write((byte)Math.Min(
            byte.MaxValue,
            Math.Abs(left[offset] - right[offset]) * amplification));
        writer.Write((byte)Math.Min(
            byte.MaxValue,
            Math.Abs(left[offset + 1] - right[offset + 1]) * amplification));
        writer.Write((byte)Math.Min(
            byte.MaxValue,
            Math.Abs(left[offset + 2] - right[offset + 2]) * amplification));
    }
}

static TimingSummary Summarize(double[] samples, long allocatedBytes)
{
    double[] ordered = (double[])samples.Clone();
    Array.Sort(ordered);
    return new TimingSummary(
        MeanMilliseconds: samples.Average(),
        P50Milliseconds: Percentile(ordered, 0.50),
        P95Milliseconds: Percentile(ordered, 0.95),
        MaximumMilliseconds: ordered[^1],
        TotalManagedAllocatedBytes: allocatedBytes,
        ManagedAllocatedBytesPerFrame: allocatedBytes / (double)samples.Length);
}

static double Percentile(double[] ordered, double percentile)
{
    int index = Math.Clamp(
        (int)Math.Ceiling(percentile * ordered.Length) - 1,
        0,
        ordered.Length - 1);
    return ordered[index];
}

internal sealed record BenchmarkReport(
    string RuntimeInformation,
    string OperatingSystem,
    string Adapter,
    string Backend,
    string Scene,
    bool RerasterizeGlyphs,
    string DifferentialContract,
    int RectangleCount,
    float DpiScale,
    int WarmupIterations,
    int MeasuredIterations,
    bool SynchronizeEachFrame,
    bool DrainEachPair,
    bool DrawState,
    bool GroupOpacity,
    string GroupMask,
    string GroupEffect,
    string GroupBlend,
    bool ManagedCompiledSceneCache,
    string MeasurementOrder,
    TimingSummary Native,
    TimingSummary Managed,
    TimingSummary NativeSubmission,
    TimingSummary NativeCompletionWait,
    TimingSummary ManagedSubmission,
    TimingSummary ManagedCompletionWait,
    double NativeSubmissionAllocatedBytesPerFrame,
    double NativeCompletionAllocatedBytesPerFrame,
    double ManagedSubmissionAllocatedBytesPerFrame,
    double ManagedCompletionAllocatedBytesPerFrame,
    double NativeToManagedP95Ratio,
    ulong CombinedMetalAllocatedBytes,
    uint NativeVertexCount,
    uint NativeIndexCount,
    int ManagedVertexCount,
    int ManagedIndexCount,
    string NativePayloadHash,
    uint NativeRasterizedPathCount,
    ulong NativePathUploadBytes,
    ulong NativeCoverageStagingBytes,
    uint NativeRasterizedGlyphCount,
    ulong NativeGlyphOutlineUploadBytes,
    ulong NativeGlyphInstanceUploadBytes,
    uint NativeAtlasWidth,
    uint NativeAtlasGeneration,
    uint NativeAtlasGrowthCount,
    ulong NativeImageTextureUploadBytes,
    uint NativeImageTextureGeneration,
    NativeSceneUpdateMetrics NativeSceneUpdateMetrics,
    NativeSceneFrameMetrics NativeSceneFrameMetrics,
    NativeLayerMetrics NativeLayerMetrics,
    PixelComparison PixelParity);

internal sealed record TimingSummary(
    double MeanMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds,
    long TotalManagedAllocatedBytes,
    double ManagedAllocatedBytesPerFrame);

internal sealed record PixelComparison(
    int PixelCount,
    int MaximumChannelDifference,
    int PixelsOverTolerance,
    long TotalAbsoluteChannelDifference,
    double MeanAbsoluteChannelDifference,
    string NativeFnv1A64,
    string ManagedFnv1A64);
