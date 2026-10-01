using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

// Authentic public native hinted contours and retained writer positions, with
// explicitly authored fractional/repeated-overlap scene placement. This is an
// additive selected-provider GPU differential, not recorded-source factory or
// complete package qualification. The original paragraph controls run first.
internal static class TextHintedGlyphPaintRenderingValidation
{
    internal delegate (NativeGlyphOutline[] Outlines, NativePathSegment[] Segments,
        NativePositionedGlyph[] Glyphs) UnpackOriginalGlyphs(
        NativeTextShapingContext context, NativeHintedParagraph paragraph,
        NativeTextScalar[] source, NativeTextFeature[] features,
        NativeHintedParagraphDeviceStyle[] devices, Vector2 origin, Vector4[] colors);

    internal static void Run(WgpuContext context, string fontPath,
        Func<NativeCompositor> createCompositor, UnpackOriginalGlyphs unpack)
    {
        Check(context.IsInitialized && !context.IsDisposed &&
            context.BackendKind is WgpuBackendKind.SilkNative or WgpuBackendKind.DawnNative,
            "actual initialized selected native provider");
        int cases = 0;
        foreach (float dpi in new[] { 1f, 2f })
        {
            byte[]? singlePixels = null;
            for (int overlap = 0; overlap < 2; overlap++)
            {
                using var subject = createCompositor();
                using var reference = createCompositor();
                Check(!ReferenceEquals(subject, reference), "fresh independent selected-provider engines");
                using var target = CreateTarget(context, "Direct hinted constant-gradient subject");
                using var referenceTarget = CreateTarget(context, "Original solid hinted text reference");
                byte[] font = File.ReadAllBytes(fontPath);
                string fontSha256 = Convert.ToHexString(SHA256.HashData(font));
                using var producer = new NativeTextShapingContext(font);
                NativeTextScalar[] source = [new() { CodePoint = 'O', InputIndex = 9, InputLength = 1 }];
                NativeTextFeature[] features = [new() { Tag = 0x6B65726E, Value = 0, Start = 0, End = uint.MaxValue }];
                NativeTextStyleRun[] styles = [new() { ScalarCount = 1, Scale = 17f / 2048, FeatureCount = 1 }];
                NativeTextStyleMetrics[] metrics = [new() { Ascent = 18 / dpi, Descent = 4 / dpi }];
                NativeHintedParagraphDeviceStyle[] devices = [new()
                {
                    SourceScale = styles[0].Scale, LogicalUnitsPerPhysicalPixel = 1 / dpi,
                    XPixelsPerEm266 = 17 * 64, YPixelsPerEm266 = 17 * 64,
                    Interpreter = (uint)NativeFontHintInterpreter.TrueType40,
                    XPhase266 = 7, YPhase266 = 11
                }];
                var input = new NativeTextShapeInput([], source,
                    direction: NativeTextDirection.LeftToRight, unicodeScript: 0x6C61746E, features: features);
                var options = new NativeTextParagraphOptions(1, 46 / dpi);
                using var paragraph = producer.LayoutHintedParagraph(in input, in options, styles, metrics, devices);
                var origin = new Vector2(8.125f / dpi, 8.375f / dpi);
                var paintColor = new Vector4(0.25f, 0.75f, 0.5f, 0.625f);
                const float brushOpacity = 0.75f;
                var solidColor = new Vector4(paintColor.X, paintColor.Y, paintColor.Z,
                    paintColor.W * brushOpacity);
                Vector4[] colors = [solidColor];
                var original = unpack(producer, paragraph, source, features, devices, origin, colors);
                Check(original.Glyphs.Length == 1 && original.Outlines.Length != 0 &&
                    original.Segments.Any(segment => segment.Kind == NativePathSegmentKind.Quadratic),
                    "independently unpacked original hinted O has real quadratic ink and one writer position");
                var glyphs = original.Glyphs.ToArray();
                if (overlap != 0)
                {
                    ref readonly var first = ref original.Glyphs[0];
                    // Repeated paint is authored placement, not a reshaped or
                    // invented font glyph. Preserve every public glyph field.
                    glyphs = [first, new(first.OutlineIndex,
                        first.Position + new Vector2(0.375f / dpi, 0.125f / dpi),
                        first.BasisX, first.BasisY, first.Color, first.AtlasToLogicalScale,
                        first.BoldOffset, first.ItalicSkew)];
                }
                byte[] outlineBytes = MemoryMarshal.AsBytes(original.Outlines.AsSpan()).ToArray();
                byte[] segmentBytes = MemoryMarshal.AsBytes(original.Segments.AsSpan()).ToArray();
                byte[] glyphBytes = MemoryMarshal.AsBytes(glyphs.AsSpan()).ToArray();
                byte[] writerBytes = MemoryMarshal.AsBytes(paragraph.Glyphs).ToArray();
                var beforePreparation = subject.GetLastSubmissionToken();
                using var retainedProducerFrame = paragraph.PrepareFrame(target, dpi, colors, origin,
                    Vector4.Zero, coverage: NativeHintedCoverage.AntialiasedVector);
                Check(subject.GetLastSubmissionToken().Equals(beforePreparation),
                    "retaining original producer geometry is CPU-only");
                producer.Dispose();
                paragraph.Dispose();
                Array.Clear(font); Array.Clear(source); Array.Clear(features); Array.Clear(styles);
                Array.Clear(metrics); Array.Clear(devices); Array.Clear(colors);
                Check(writerBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(paragraph.Glyphs)),
                    "retired producer preserves original captured writer positions");

                const ulong sceneId = 0x7719;
                byte[] scene = CreateScene(sceneId, dpi, original.Outlines, original.Segments,
                    glyphs, origin, paintColor, brushOpacity);
                var update = subject.UpdateScene(scene);
                Check(update.CommandCount == 1 && update.ResourceCount == 2 && update.DrawCount == 1 &&
                    update.SceneId == sceneId && update.Generation == 1 &&
                    subject.GetLastSubmissionToken().Equals(beforePreparation),
                    "one direct command26 and original glyph/material resources install without submission");
                byte[]? firstPixels = null;
                for (int frame = 0; frame < 2; frame++)
                {
                    var before = subject.GetLastSubmissionToken();
                    uint generation = target.Generation, view = target.ViewGeneration;
                    var actual = subject.RenderScene(target, dpi, sceneId, 1, Vector4.Zero);
                    byte[] pixels = CompleteAndRead(subject, target, before);
                    var referenceBefore = reference.GetLastSubmissionToken();
                    // Original unstyled solid renderer; flags/revision remain
                    // zero so its geometry is independently rerasterized.
                    var expected = reference.RenderGlyphs(referenceTarget, dpi,
                        original.Outlines, original.Segments, glyphs, Vector4.Zero);
                    byte[] expectedPixels = CompleteAndRead(reference, referenceTarget, referenceBefore);
                    CheckPixels(pixels, expectedPixels,
                        $"full RGBA differential dpi={dpi}, overlap={overlap}, frame={frame}");
                    // Scene metrics describe this RenderScene call; the original
                    // RenderGlyphs metric is the engine's cumulative count.
                    Check(actual.CommandCount == 1 && actual.DrawCallCount == 1 &&
                        expected.DrawCallCount == 1 && expected.GlyphCount == glyphs.Length &&
                        actual.SubmissionCount == 1 && expected.SubmissionCount == (ulong)frame + 1 &&
                        expected.RasterizedGlyphCount != 0 && expected.PayloadHash == 0,
                        $"one real render submission on each independent engine and uncached original solid control: " +
                        $"frame={frame}, scene commands/draws/submissions={actual.CommandCount}/{actual.DrawCallCount}/{actual.SubmissionCount}, " +
                        $"glyph draws/count/submissions/rasterized/hash={expected.DrawCallCount}/{expected.GlyphCount}/{expected.SubmissionCount}/{expected.RasterizedGlyphCount}/{expected.PayloadHash}");
                    Check(target.Generation == generation + 1 && target.ViewGeneration == view,
                        "actual completed paint publishes contents without exchanging the target view");
                    if (frame == 0)
                    {
                        Check(actual.VertexUploadBytes != 0 && actual.BrushUploadBytes != 0 &&
                            actual.GradientStopUploadBytes != 0,
                            "cold direct glyph, original gradient and stop uploads reach the actual renderer");
                        VerifyInk(pixels, solidColor.W);
                        if (overlap == 0) singlePixels = pixels;
                        else Check(singlePixels is not null && HasGreaterAlpha(pixels, singlePixels),
                            "two original translucent occurrences actually overlap rather than becoming a union mask");
                    }
                    else
                    {
                        Check(firstPixels!.AsSpan().SequenceEqual(pixels) &&
                            actual.CoverageStagingBytes == 0 && actual.BrushUploadBytes == 0 &&
                            actual.GradientStopUploadBytes == 0,
                            "warm resident paint preserves every pixel without coverage/material restaging");
                    }
                    firstPixels ??= pixels;
                    Check(outlineBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(original.Outlines.AsSpan())) &&
                        segmentBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(original.Segments.AsSpan())) &&
                        glyphBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(glyphs.AsSpan())),
                        "both actual renderers leave all independently unpacked original geometry bytes immutable");
                }
                cases++;
                Console.WriteLine($"package-consumer: authentic native hinted direct paint provider={context.BackendKind}, " +
                    $"dpi={dpi}, authoredOverlap={overlap}, cold/warm full RGBA vs original solid text passed");
                if (dpi == 2 && overlap == 1)
                {
                    VerifyTextureReplacement(context, createCompositor, original.Outlines, original.Segments,
                        glyphs, dpi, solidColor.W, fontSha256, writerBytes);
                    VerifyAffinePaints(context, createCompositor, original.Outlines, original.Segments,
                        glyphs, dpi, solidColor.W);
                }
            }
        }
        Check(cases == 4, "all additive DPI/fractional/overlap controls ran");
    }

    private static void VerifyTextureReplacement(WgpuContext context, Func<NativeCompositor> createCompositor,
        NativeGlyphOutline[] outlines, NativePathSegment[] segments, NativePositionedGlyph[] glyphs,
        float dpi, float opacity, string fontSha256, byte[] writerBytes)
    {
        using var subject = createCompositor();
        using var reference = createCompositor();
        using var target = CreateTarget(context, "Direct hinted same-count texture replacement");
        using var referenceTarget = CreateTarget(context, "Original solid texture-color text reference");
        using var first = new GpuTexture(context, 1, 1, TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst, "Original first opaque image",
            alphaMode: GpuTextureAlphaMode.Straight);
        using var second = new GpuTexture(context, 1, 1, TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst, "Original replacement opaque image",
            alphaMode: GpuTextureAlphaMode.Straight);
        byte[][] sourcePixels = [[64, 192, 128, 255], [192, 64, 32, 255]];
        first.WritePixels<byte>(sourcePixels[0]); second.WritePixels<byte>(sourcePixels[1]);
        const ulong sceneId = 0x771A;
        byte[] scene = CreateTextureScene(sceneId, dpi, outlines, segments, glyphs, opacity);
        var update = subject.UpdateScene(scene);
        Check(update.CommandCount == 1 && update.ResourceCount == 2 && update.DrawCount == 1,
            "one original glyph draw and one external IMAGE binding slot");
        byte[] immutableScene = scene.ToArray();
        byte[]? previous = null, originalPixels = null;
        for (int frame = 0; frame < 4; frame++)
        {
            int image = frame / 2;
            if ((frame & 1) == 0)
            {
                var beforeBinding = subject.GetLastSubmissionToken();
                // Same resource id, generation, role and binding count; ONLY
                // the original actual view changes. No scene update hides it.
                subject.BindSceneExternalImages([new(2, 1, image == 0 ? first : second)]);
                Check(subject.GetLastSubmissionToken().Equals(beforeBinding),
                    "same-count image replacement does not manufacture a GPU submission");
            }
            byte[] texel = sourcePixels[image];
            var solidColor = new Vector4(texel[0] / 255f, texel[1] / 255f, texel[2] / 255f, opacity);
            var solidGlyphs = new NativePositionedGlyph[glyphs.Length];
            for (int i = 0; i < glyphs.Length; i++)
            {
                ref readonly var glyph = ref glyphs[i];
                solidGlyphs[i] = new(glyph.OutlineIndex, glyph.Position, glyph.BasisX, glyph.BasisY,
                    solidColor, glyph.AtlasToLogicalScale, glyph.BoldOffset, glyph.ItalicSkew);
            }
            var before = subject.GetLastSubmissionToken();
            var actual = subject.RenderScene(target, dpi, sceneId, 1, Vector4.Zero);
            byte[] pixels = CompleteAndRead(subject, target, before);
            var referenceBefore = reference.GetLastSubmissionToken();
            var expected = reference.RenderGlyphs(referenceTarget, dpi, outlines, segments, solidGlyphs, Vector4.Zero);
            byte[] expectedPixels = CompleteAndRead(reference, referenceTarget, referenceBefore);
            try
            {
                CheckPixels(pixels, expectedPixels,
                    $"same-count actual texture replacement full RGBA frame={frame}");
            }
            catch (InvalidOperationException failure)
            {
                // Test-only opt-in capture after actual completion. The original
                // exact-pixel failure remains primary even if receipt I/O fails.
                NativeHintedPaintFailureReceipt.TryCapture(context, failure, fontSha256,
                    dpi, frame, outlines, segments, glyphs, solidGlyphs, writerBytes,
                    scene, paint, texel, pixels, expectedPixels);
                throw;
            }
            Check(actual.CommandCount == 1 && actual.DrawCallCount == 1 && expected.DrawCallCount == 1 &&
                expected.GlyphCount == glyphs.Length && actual.SubmissionCount == 1 &&
                expected.SubmissionCount == (ulong)frame + 1 && expected.RasterizedGlyphCount != 0,
                $"same-count texture replay and independent original solid reference each really submit once: " +
                $"frame={frame}, scene commands/draws/submissions={actual.CommandCount}/{actual.DrawCallCount}/{actual.SubmissionCount}, " +
                $"glyph draws/count/submissions/rasterized={expected.DrawCallCount}/{expected.GlyphCount}/{expected.SubmissionCount}/{expected.RasterizedGlyphCount}");
            VerifyInk(pixels, opacity);
            if ((frame & 1) != 0)
                Check(previous!.AsSpan().SequenceEqual(pixels) && actual.CoverageStagingBytes == 0,
                    "each original texture view has independent exact warm replay without coverage restaging");
            if (frame == 2)
                Check(!originalPixels!.AsSpan().SequenceEqual(pixels),
                    "actual replacement changes the sampled image rather than retaining the previous binding");
            originalPixels ??= pixels;
            previous = pixels;
            Check(immutableScene.AsSpan().SequenceEqual(scene), "image replacement leaves original immutable scene bytes unchanged");
        }
        Console.WriteLine($"package-consumer: authentic native hinted opaque bounded texture provider={context.BackendKind}, " +
            "same-count original-view replacement and both cold/warm full RGBA solid controls passed");
    }

    private static void VerifyAffinePaints(WgpuContext context, Func<NativeCompositor> createCompositor,
        NativeGlyphOutline[] outlines, NativePathSegment[] segments, NativePositionedGlyph[] original,
        float dpi, float opacity)
    {
        // Reuse two actual engines across all new cases. Authored presentation
        // transforms never change the independently unpacked hinted contours.
        using var subject = createCompositor();
        using var reference = createCompositor();
        Check(!ReferenceEquals(subject, reference), "independent affine paint and original Text engines");
        using var target = CreateTarget(context, "Original hinted affine paint subject");
        using var referenceTarget = CreateTarget(context, "Original affine solid Text reference");
        using var texture = new GpuTexture(context, 1, 1, TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst, "Original opaque affine paint image",
            alphaMode: GpuTextureAlphaMode.Straight);
        byte[] texel = [64, 192, 128, 255];
        texture.WritePixels<byte>(texel);
        var color = new Vector4(texel[0] / 255f, texel[1] / 255f, texel[2] / 255f, opacity);
        (string Name, Vector2 X, Vector2 Y, float Italic, bool Singular)[] transforms =
        [
            ("quarter-turn", new(0, 1), new(-1, 0), 0, false),
            ("reflection", new(-1, 0), new(0, 1), 0, false),
            // Non-binary coefficients exercise actual independently rounded
            // canonical corners, not an assumed exact parallelogram.
            ("italic-shear", new(1, 0.23f), new(0.61f, 1), 0.37f, false),
            ("singular", new(1, 0), new(2, 0), 0, true)
        ];
        byte[] outlineBytes = MemoryMarshal.AsBytes(outlines.AsSpan()).ToArray();
        byte[] segmentBytes = MemoryMarshal.AsBytes(segments.AsSpan()).ToArray();
        byte[] originalBytes = MemoryMarshal.AsBytes(original.AsSpan()).ToArray();
        ulong referenceSubmissions = 0;
        int cases = 0;
        foreach (var transform in transforms)
        {
            var glyphs = new NativePositionedGlyph[original.Length];
            for (int i = 0; i < glyphs.Length; i++)
            {
                ref readonly var glyph = ref original[i];
                Vector2 relative = glyph.Position - original[0].Position;
                Vector2 position = new Vector2(48.125f / dpi, 48.375f / dpi) +
                    relative.X * transform.X + relative.Y * transform.Y;
                glyphs[i] = new(glyph.OutlineIndex, position, transform.X, transform.Y,
                    color, glyph.AtlasToLogicalScale, glyph.BoldOffset, transform.Italic);
            }
            byte[] glyphBytes = MemoryMarshal.AsBytes(glyphs.AsSpan()).ToArray();
            foreach (bool boundedTexture in new[] { false, true })
            {
                ulong sceneId = 0x7720UL + (ulong)cases;
                byte[] scene = boundedTexture
                    ? CreateTextureScene(sceneId, dpi, outlines, segments, glyphs, opacity)
                    : CreateScene(sceneId, dpi, outlines, segments, glyphs, Vector2.Zero, color, 1f);
                byte[] sceneBytes = scene.ToArray();
                var beforeUpdate = subject.GetLastSubmissionToken();
                var update = subject.UpdateScene(scene);
                if (boundedTexture) subject.BindSceneExternalImages([new(2, 1, texture)]);
                Check(update.CommandCount == 1 && update.ResourceCount == 2 && update.DrawCount == 1 &&
                    update.SceneId == sceneId && subject.GetLastSubmissionToken().Equals(beforeUpdate),
                    $"{transform.Name}: exact affine scene/material/image publication does not submit");
                byte[]? coldPixels = null;
                for (int frame = 0; frame < 2; frame++)
                {
                    string name = $"affine={transform.Name}, boundedTexture={boundedTexture}, frame={frame}, dpi={dpi}";
                    var before = subject.GetLastSubmissionToken();
                    uint generation = target.Generation, view = target.ViewGeneration;
                    var actual = subject.RenderScene(target, dpi, sceneId, 1, Vector4.Zero);
                    byte[] pixels = CompleteAndRead(subject, target, before);
                    var referenceBefore = reference.GetLastSubmissionToken();
                    // Identical original positioned records, including both
                    // canonical triangles, through the original Text path.
                    var expected = reference.RenderGlyphs(referenceTarget, dpi, outlines, segments, glyphs, Vector4.Zero);
                    byte[] expectedPixels = CompleteAndRead(reference, referenceTarget, referenceBefore);
                    referenceSubmissions++;
                    CheckPixels(pixels, expectedPixels, name + ": complete original Text RGBA differential");
                    Check(actual.CommandCount == 1 && actual.DrawCallCount == 1 && actual.SubmissionCount == 1 &&
                        expected.DrawCallCount == 1 && expected.GlyphCount == glyphs.Length &&
                        expected.SubmissionCount == referenceSubmissions && expected.RasterizedGlyphCount != 0 &&
                        expected.PayloadHash == 0 && target.Generation == generation + 1 && target.ViewGeneration == view,
                        name + $": real independent submissions and uncached reference, " +
                        $"scene commands/draws/submissions={actual.CommandCount}/{actual.DrawCallCount}/{actual.SubmissionCount}, " +
                        $"glyph draws/count/submissions/rasterized/hash={expected.DrawCallCount}/{expected.GlyphCount}/{expected.SubmissionCount}/{expected.RasterizedGlyphCount}/{expected.PayloadHash}");
                    if (transform.Singular)
                        Check(pixels.All(value => value == 0), name + ": exact zero-area canonical triangles emit no ink");
                    else VerifyInk(pixels, opacity);
                    if (frame != 0)
                        Check(coldPixels!.AsSpan().SequenceEqual(pixels) && actual.CoverageStagingBytes == 0 &&
                            actual.BrushUploadBytes == 0 && actual.GradientStopUploadBytes == 0,
                            name + ": exact warm replay without coverage/material restaging");
                    coldPixels ??= pixels;
                    Check(sceneBytes.AsSpan().SequenceEqual(scene) &&
                        glyphBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(glyphs.AsSpan())) &&
                        outlineBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(outlines.AsSpan())) &&
                        segmentBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(segments.AsSpan())) &&
                        originalBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(original.AsSpan())),
                        name + ": original contours, occurrences and scene bytes stay immutable");
                }
                cases++;
            }
        }
        Check(cases == 8 && referenceSubmissions == 16, "all affine material/image cold/warm controls ran");
        Console.WriteLine($"package-consumer: authentic native hinted affine paint provider={context.BackendKind}, " +
            "quarter-turn/reflection/italic-shear/singular material and bounded image cold/warm full RGBA controls passed");
    }

    private static byte[] CreateTextureScene(ulong sceneId, float dpi, NativeGlyphOutline[] outlines,
        NativePathSegment[] segments, NativePositionedGlyph[] glyphs, float opacity)
    {
        var sceneOutlines = new NativeSceneGlyphOutline[outlines.Length];
        for (int i = 0; i < outlines.Length; i++)
        {
            ref readonly var outline = ref outlines[i];
            sceneOutlines[i] = new(checked((ulong)outline.SegmentOffset), checked((ulong)outline.SegmentCount),
                outline.Minimum, outline.Maximum, outline.RasterScale, outline.SubpixelX);
        }
        byte[] storage = new byte[NativeSceneStreamBuilder.GetRequiredBufferSize(1, 2,
            checked(MemoryMarshal.AsBytes(sceneOutlines.AsSpan()).Length +
                MemoryMarshal.AsBytes(segments.AsSpan()).Length + MemoryMarshal.AsBytes(glyphs.AsSpan()).Length + 192))];
        var builder = new NativeSceneStreamBuilder(storage, sceneId, 1, commandCapacity: 1, resourceCapacity: 2);
        Check(builder.TryAddGlyphResource(1, 1, sceneOutlines, segments, out uint glyphResource),
            "texture control shares exact independently unpacked original glyph geometry");
        Check(builder.TryAddExternalImageResource(2, 1, out uint imageResource), "original external IMAGE resource");
        float extent = 96 / dpi;
        var paint = new NativeSceneGlyphPaint(NativeSceneGlyphPaint.Texture, 0,
            NativeSceneGlyphPaint.BoundedTexture,
            new(0, 0, opacity, 0), new(0, 0, 1, 1), new(0, 0, extent, 0),
            new(extent, extent, 0, extent), new(0, 0.5f, 0, 0));
        Check(builder.TryDrawPaintedGlyphRun(1, glyphResource, new(0, 0, extent, extent), glyphs,
            imageResource, 0, in paint), "bounded original image-quad direct glyph paint");
        Check(builder.TryBuild(out var stream), "complete pointer-free texture paint scene");
        return stream.ToArray();
    }

    private static byte[] CreateScene(ulong sceneId, float dpi, NativeGlyphOutline[] outlines,
        NativePathSegment[] segments, NativePositionedGlyph[] glyphs, Vector2 origin,
        Vector4 color, float opacity)
    {
        var sceneOutlines = new NativeSceneGlyphOutline[outlines.Length];
        for (int i = 0; i < outlines.Length; i++)
        {
            ref readonly var outline = ref outlines[i];
            sceneOutlines[i] = new(checked((ulong)outline.SegmentOffset), checked((ulong)outline.SegmentCount),
                outline.Minimum, outline.Maximum, outline.RasterScale, outline.SubpixelX);
        }
        NativeSceneGradientStop[] stops = [new(color, 0), new(color, 1)];
        NativeSceneBrush[] brushes = [NativeSceneBrush.LinearGradient(new(-17.25f, 4.125f),
            new(53.75f, -8.375f), 0, stops, opacity)];
        var paint = new NativeSceneGlyphPaint(NativeSceneGlyphPaint.Material, 0, 0,
            new(origin.X, origin.Y, 0, 0));
        int arena = checked(MemoryMarshal.AsBytes(sceneOutlines.AsSpan()).Length +
            MemoryMarshal.AsBytes(segments.AsSpan()).Length + MemoryMarshal.AsBytes(glyphs.AsSpan()).Length +
            MemoryMarshal.AsBytes(brushes.AsSpan()).Length + MemoryMarshal.AsBytes(stops.AsSpan()).Length + 128 + 64);
        byte[] storage = new byte[NativeSceneStreamBuilder.GetRequiredBufferSize(1, 2, arena)];
        var builder = new NativeSceneStreamBuilder(storage, sceneId, 1, commandCapacity: 1, resourceCapacity: 2);
        Check(builder.TryAddGlyphResource(1, 1, sceneOutlines, segments, out uint glyphResource),
            "exact original outline/segment bytes enter one glyph resource");
        Check(builder.TryAddBrushTableResource(2, 1, brushes, stops, out uint materialResource),
            "original constant translucent gradient table");
        Check(builder.TryDrawPaintedGlyphRun(1, glyphResource, new(0, 0, 96 / dpi, 96 / dpi),
            glyphs, materialResource, 0, in paint), "typed canonical grayscale direct glyph paint command");
        Check(builder.TryBuild(out var stream), "complete immutable scene");
        return stream.ToArray();
    }

    private static GpuTexture CreateTarget(WgpuContext context, string label) => new(context, 96, 96,
        TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc, label,
        alphaMode: GpuTextureAlphaMode.Premultiplied);

    private static byte[] CompleteAndRead(NativeCompositor renderer, GpuTexture target, NativeSubmissionToken before)
    {
        var token = renderer.GetLastSubmissionToken();
        Check(token.IsValid && token.Value > before.Value, "real native queue submission advances");
        renderer.WaitForSubmission(token); // Existing actual completion and deadlines.
        Check(renderer.IsSubmissionComplete(token), "original native submission is actually complete");
        return target.ReadPixels(); // Existing copy/map ownership and deadline.
    }

    private static void VerifyInk(byte[] pixels, float opacity)
    {
        bool ink = false, edge = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            ink |= pixels[i] != 0 && pixels[i + 1] != 0 && pixels[i + 2] != 0;
            edge |= pixels[i + 3] > 0 && pixels[i + 3] < opacity * 255 - 1;
        }
        Check(ink && edge, "actual translucent ink and fractional filtered coverage, not an empty differential");
    }

    private static bool HasGreaterAlpha(byte[] overlap, byte[] single)
    {
        for (int i = 3; i < overlap.Length; i += 4)
            if (single[i] != 0 && overlap[i] > single[i]) return true;
        return false;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Native hinted direct paint GPU package: " + message);
    }

    private static void CheckPixels(byte[] actual, byte[] expected, string message)
    {
        Check(actual.Length == 96 * 96 * 4 && expected.Length == actual.Length,
            message + $"; byte lengths actual={actual.Length}, expected={expected.Length}");
        int first = -1, different = 0, maximumDelta = 0;
        Span<int> differencesByChannel = stackalloc int[4];
        Span<int> maximumByChannel = stackalloc int[4];
        differencesByChannel.Clear();
        maximumByChannel.Clear();
        for (int index = 0; index < actual.Length; index++)
        {
            if (actual[index] == expected[index]) continue;
            if (first < 0) first = index;
            different++;
            int delta = Math.Abs(actual[index] - expected[index]);
            maximumDelta = Math.Max(maximumDelta, delta);
            differencesByChannel[index % 4]++;
            maximumByChannel[index % 4] = Math.Max(maximumByChannel[index % 4], delta);
        }
        Check(first < 0, message + (first < 0 ? string.Empty :
            $"; different bytes={different}, max delta={maximumDelta}, first x={(first / 4) % 96}, y={(first / 4) / 96}, channel={first % 4}, actual={actual[first]}, expected={expected[first]}, " +
            $"RGBA differences={string.Join(',', differencesByChannel.ToArray())}, RGBA max deltas={string.Join(',', maximumByChannel.ToArray())}, " +
            $"first pixel actual={string.Join(',', actual.AsSpan(first & ~3, 4).ToArray())}, expected={string.Join(',', expected.AsSpan(first & ~3, 4).ToArray())}"));
    }
}
