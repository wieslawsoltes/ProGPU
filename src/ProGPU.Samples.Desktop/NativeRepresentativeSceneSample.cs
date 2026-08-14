using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Backend;
using ProGPU.Backend.Native;

namespace ProGPU.Samples.Desktop;

/// <summary>
/// Builds the immutable mixed-domain stream used by the native renderer page.
/// The scene is constructed only when sample content changes; stable frames
/// cross the C ABI once and replay retained WebGPU resources without upload.
/// </summary>
internal static class NativeRepresentativeSceneSample
{
    public const ulong SceneId = 0x53414D504C454E31UL;
    public const uint CommandCount = 8;
    public const uint ResourceCount = 9;
    public const uint DrawCount = 4;

    private const uint ImageWidth = 192;
    private const uint ImageHeight = 128;

    public static int RequiredBufferSize
    {
        get
        {
            int arenaCapacity = checked(
                4 * Unsafe.SizeOf<NativeAnalyticPrimitive>() +
                Unsafe.SizeOf<NativeScenePathFill>() +
                12 * Unsafe.SizeOf<NativePathSegment>() +
                Unsafe.SizeOf<NativeSceneGlyphOutline>() +
                4 * Unsafe.SizeOf<NativePathSegment>() +
                5 * Unsafe.SizeOf<NativePositionedGlyph>() +
                checked((int)(ImageWidth * ImageHeight * 4)) +
                2 * Unsafe.SizeOf<NativeSceneBrush>() +
                3 * Unsafe.SizeOf<NativeSceneGradientStop>() +
                Unsafe.SizeOf<NativeSceneTextStyle>() +
                Unsafe.SizeOf<NativeSceneState>() +
                Unsafe.SizeOf<NativeSceneLayerMask>() +
                Unsafe.SizeOf<NativeSceneEffectChain>() +
                2 * Unsafe.SizeOf<NativeSceneEffect>() +
                Unsafe.SizeOf<NativeSceneLayer>() +
                24 + // Exact semantic glyph-draw prefix.
                Unsafe.SizeOf<NativeSceneImageDraw>() +
                Unsafe.SizeOf<NativeSceneImageSamplingOptions>() +
                Unsafe.SizeOf<NativeSceneImageColorMatrix>() +
                6 * sizeof(uint) +
                512);
            return NativeSceneStreamBuilder.GetRequiredBufferSize(
                (int)CommandCount,
                (int)ResourceCount,
                arenaCapacity);
        }
    }

    public static int Build(
        Span<byte> destination,
        ReadOnlySpan<byte> imagePixels,
        ulong generation,
        int palette)
    {
        if (imagePixels.Length != ImageWidth * ImageHeight * 4)
        {
            throw new ArgumentException(
                "The representative scene requires one 192x128 RGBA image.",
                nameof(imagePixels));
        }

        Span<NativeAnalyticPrimitive> analytic = stackalloc NativeAnalyticPrimitive[4];
        analytic[0] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.RoundedRectangle,
            72f,
            62f,
            248f,
            136f,
            Vector4.One,
            Matrix3x2.CreateRotation(0.045f, new Vector2(196f, 130f)),
            cornerRadius: 28f);
        analytic[1] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Ellipse,
            118f,
            224f,
            154f,
            154f,
            Vector4.One,
            Matrix3x2.Identity);
        analytic[2] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.RoundedRectangle,
            292f,
            248f,
            126f,
            90f,
            Vector4.One,
            Matrix3x2.CreateSkew(-0.16f, 0f),
            cornerRadius: 18f);
        analytic[3] = new NativeAnalyticPrimitive(
            NativeAnalyticPrimitiveKind.Ellipse,
            326f,
            98f,
            92f,
            92f,
            Vector4.One,
            Matrix3x2.Identity,
            strokeThickness: 7f);

        Span<NativePathSegment> pathSegments = stackalloc NativePathSegment[4];
        const float kappa = 0.55228475f;
        pathSegments[0] = new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(0f, -1f),
            new Vector2(kappa, -1f),
            new Vector2(1f, -kappa),
            new Vector2(1f, 0f));
        pathSegments[1] = new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(1f, 0f),
            new Vector2(1f, kappa),
            new Vector2(kappa, 1f),
            new Vector2(0f, 1f));
        pathSegments[2] = new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(0f, 1f),
            new Vector2(-kappa, 1f),
            new Vector2(-1f, kappa),
            new Vector2(-1f, 0f));
        pathSegments[3] = new NativePathSegment(
            NativePathSegmentKind.Cubic,
            new Vector2(-1f, 0f),
            new Vector2(-1f, -kappa),
            new Vector2(-kappa, -1f),
            new Vector2(0f, -1f));
        Span<NativeScenePathFill> paths = stackalloc NativeScenePathFill[1];
        paths[0] = new NativeScenePathFill(
            0,
            4,
            new Vector2(-1f),
            Vector2.One,
            Vector4.One,
            Matrix3x2.CreateScale(118f, 74f) *
                Matrix3x2.CreateRotation(-0.12f) *
                Matrix3x2.CreateTranslation(604f, 144f),
            sampleGrid: 8);

        Span<NativePathSegment> glyphSegments = stackalloc NativePathSegment[12];
        WriteRectangle(glyphSegments, 0, 0f, 0f, 0.25f, 1f);
        WriteRectangle(glyphSegments, 4, 0.75f, 0f, 1f, 1f);
        WriteRectangle(glyphSegments, 8, 0.25f, 0.4f, 0.75f, 0.6f);
        Span<NativeSceneGlyphOutline> glyphOutlines =
            stackalloc NativeSceneGlyphOutline[1];
        glyphOutlines[0] = new NativeSceneGlyphOutline(
            0,
            12,
            Vector2.Zero,
            Vector2.One,
            rasterScale: 52f,
            subpixelX: 0.25f);
        Span<NativePositionedGlyph> glyphs = stackalloc NativePositionedGlyph[5];
        for (int index = 0; index < glyphs.Length; index++)
        {
            float height = index % 2 == 0 ? 52f : 34f;
            glyphs[index] = new NativePositionedGlyph(
                0,
                new Vector2(90f + index * 62f, 426f - height),
                Vector2.UnitX,
                Vector2.UnitY,
                Vector4.One);
        }

        Span<NativeSceneGradientStop> stops = stackalloc NativeSceneGradientStop[3];
        stops[0] = new NativeSceneGradientStop(
            palette % 2 == 0
                ? new Vector4(0.02f, 0.62f, 1f, 1f)
                : new Vector4(0.78f, 0.24f, 1f, 1f),
            0f);
        stops[1] = new NativeSceneGradientStop(
            new Vector4(0.12f, 0.95f, 0.72f, 1f),
            0.52f);
        stops[2] = new NativeSceneGradientStop(
            new Vector4(1f, 0.42f, 0.18f, 1f),
            1f);
        Span<NativeSceneBrush> brushes = stackalloc NativeSceneBrush[2];
        brushes[0] = NativeSceneBrush.LinearGradient(
            new Vector2(48f, 40f),
            new Vector2(430f, 430f),
            0,
            stops,
            coordinateTransform: Matrix3x2.CreateRotation(0.08f));
        brushes[1] = NativeSceneBrush.Solid(
            new Vector4(0.92f, 0.96f, 1f, 1f));
        Span<uint> analyticBrushes = stackalloc uint[] { 0, 0, 0, 1 };
        Span<uint> pathBrushes = stackalloc uint[] { 0 };
        Span<NativeSceneTextStyle> textStyles = stackalloc NativeSceneTextStyle[1];
        textStyles[0] = new NativeSceneTextStyle(
            new Vector4(0.92f, 0.96f, 1f, 1f),
            NativeSceneTextRenderingMode.Grayscale);

        var state = new NativeSceneState(
            Matrix3x2.CreateTranslation(8f, -2f),
            opacity: 0.96f,
            flags: NativeSceneStateFlags.ClipRect,
            clipRect: new NativeImageRect(36f, 28f, 888f, 484f));
        var mask = new NativeSceneLayerMask(
            new NativeImageRect(48f, 36f, 864f, 468f),
            Matrix3x2.Identity,
            new Vector4(34f),
            new Vector4(34f));
        Span<NativeSceneEffect> effects = stackalloc NativeSceneEffect[2];
        effects[0] = NativeSceneEffect.GaussianBlur(2.25f, 2.25f, revision: 1);
        effects[1] = NativeSceneEffect.DropShadow(
            3f,
            new Vector2(8f, 10f),
            new Vector4(0f, 0f, 0f, 0.58f),
            revision: 2);

        var image = new NativeSceneImageDraw(
            ImageWidth,
            ImageHeight,
            ImageWidth * 4,
            NativeImageSampling.Cubic,
            new NativeImageRect(0f, 0f, ImageWidth, ImageHeight),
            new NativeImageRect(520f, 260f, 340f, 206f),
            Matrix3x2.CreateRotation(0.025f, new Vector2(690f, 363f)),
            0.94f,
            NativeSceneImageFlags.ColorMatrix);
        NativeSceneImageSamplingOptions sampling =
            NativeSceneImageSamplingOptions.Mitchell;
        var colorMatrix = new NativeSceneImageColorMatrix(
            new Vector4(1.04f, 0f, 0f, 0f),
            new Vector4(0f, 1f, 0f, 0f),
            new Vector4(0f, 0f, 0.94f, 0f),
            Vector4.UnitW,
            new Vector4(0.01f, 0f, 0.02f, 0f));

        var builder = new NativeSceneStreamBuilder(
            destination,
            SceneId,
            generation,
            (int)CommandCount,
            (int)ResourceCount);
        ReadOnlySpan<byte> stream = default;
        bool success = builder.TryAddAnalyticResource(
                1,
                generation,
                analytic,
                out uint analyticResource) &&
            builder.TryAddPathResource(
                2,
                generation,
                paths,
                pathSegments,
                out uint pathResource) &&
            builder.TryAddGlyphResource(
                3,
                generation,
                glyphOutlines,
                glyphSegments,
                out uint glyphResource) &&
            builder.TryAddImageResource(
                4,
                generation,
                imagePixels,
                out uint imageResource) &&
            builder.TryAddBrushTableResource(
                5,
                generation,
                brushes,
                stops,
                out uint brushResource) &&
            builder.TryAddTextStyleResource(
                6,
                generation,
                textStyles,
                out uint textStyleResource) &&
            builder.TryAddStateResource(
                7,
                generation,
                in state,
                out uint stateResource) &&
            builder.TryAddLayerMaskResource(
                8,
                generation,
                in mask,
                out uint maskResource) &&
            builder.TryAddEffectChainResource(
                9,
                generation,
                effects,
                revision: checked((uint)generation),
                out uint effectResource) &&
            builder.TrySave(1, stateResource) &&
            builder.TryPushLayer(
                2,
                new NativeSceneLayer(
                    flags: NativeSceneLayerFlags.Bounds |
                        NativeSceneLayerFlags.ForceIsolation,
                    bounds: new NativeImageRect(48f, 36f, 864f, 468f),
                    maskResourceIndex: maskResource,
                    effectResourceIndex: effectResource,
                    contentRevision: generation,
                    compositeRevision: generation)) &&
            builder.TryDrawAnalytic(
                3,
                analyticResource,
                new NativeImageRect(60f, 48f, 390f, 392f),
                brushResource,
                analyticBrushes) &&
            builder.TryDrawPath(
                4,
                pathResource,
                new NativeImageRect(470f, 54f, 272f, 190f),
                brushResource,
                pathBrushes) &&
            builder.TryDrawGlyphRun(
                5,
                glyphResource,
                new NativeImageRect(80f, 354f, 330f, 84f),
                glyphs,
                textStyleResource,
                styleIndex: 0) &&
            builder.TryDrawImage(
                6,
                imageResource,
                new NativeImageRect(500f, 244f, 380f, 244f),
                in image,
                in sampling,
                in colorMatrix) &&
            builder.TryPopLayer(7) &&
            builder.TryRestore(8) &&
            builder.TryBuild(out stream);
        if (!success)
        {
            throw new InvalidOperationException(
                "The representative native semantic scene could not be built.");
        }
        return stream.Length;
    }

    private static NativePathSegment Line(
        float x0,
        float y0,
        float x1,
        float y1) => new(
            NativePathSegmentKind.Line,
            new Vector2(x0, y0),
            new Vector2(x1, y1));

    private static void WriteRectangle(
        Span<NativePathSegment> destination,
        int offset,
        float left,
        float top,
        float right,
        float bottom)
    {
        destination[offset] = Line(left, top, right, top);
        destination[offset + 1] = Line(right, top, right, bottom);
        destination[offset + 2] = Line(right, bottom, left, bottom);
        destination[offset + 3] = Line(left, bottom, left, top);
    }
}
