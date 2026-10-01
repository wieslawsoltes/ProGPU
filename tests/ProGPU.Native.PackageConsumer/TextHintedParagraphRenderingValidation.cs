using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using Silk.NET.WebGPU;

// Loaded-library packing/render/provider/lifetime differential. The reference
// executes independent public hinted runs and unpacks their raw original
// contours. It deliberately uses the retained actual measured writer positions:
// the public package API does not expose the full styled/bidi measured writer.
internal static class TextHintedParagraphRenderingValidation
{
    internal static void Run(WgpuContext context, string fontPath, Func<NativeCompositor> createCompositor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(createCompositor);
        Check(context.IsInitialized && !context.IsDisposed &&
            context.BackendKind is WgpuBackendKind.SilkNative or WgpuBackendKind.DawnNative,
            "real initialized native provider context");
        // One dedicated fresh pair per provider run. Both engines retain the
        // same exact render history across all fixtures and projections.
        using var subject = createCompositor();
        using var referenceRenderer = createCompositor();
        Check(!ReferenceEquals(subject, referenceRenderer), "independent fresh selected-provider compositors");
        int cases = 0;
        ulong submittedFrames = 0;
        foreach (var interpreter in new[] { NativeFontHintInterpreter.TrueType35, NativeFontHintInterpreter.TrueType40 })
        for (int variant = 0; variant < 3; variant++)
        {
            bool noInk = variant == 2;
            float dpi = variant == 1 ? 2 : 1, units = 1 / dpi;
            var direction = variant == 1 ? NativeTextDirection.RightToLeft : NativeTextDirection.LeftToRight;
            // The caller supplies the real provider-specific constructor; no
            // opaque text owner or engine ever crosses between providers.
            using var target = CreateTarget(context, "Retained hinted paragraph subject");
            using var referenceTarget = CreateTarget(context, "Independent raw hinted paragraph reference");
            byte[] fontBytes = File.ReadAllBytes(fontPath);
            using var textContext = new NativeTextShapingContext(fontBytes);
            Check(textContext.AddFallbackFont(fontBytes, out uint second, identity: 0x7712) == NativeRendererStatus.Success && second == 1,
                "two original owned font sources");
            NativeTextScalar[] source = (noInk ? "          " : "AVA O O O ").Select((value, index) => new NativeTextScalar
            {
                CodePoint = value, InputIndex = checked((uint)(9 + index)), InputLength = 1,
            }).ToArray();
            NativeTextFeature[] features = [new() { Tag = 0x6B65726E, Value = 0, Start = 0, End = uint.MaxValue }];
            NativeTextStyleRun[] styles =
            [
                new() { ScalarStart = 0, ScalarCount = 3, FontIndex = 0, Scale = 13f / 2048, FeatureCount = 1 },
                new() { ScalarStart = 3, ScalarCount = checked((uint)source.Length - 3), FontIndex = second,
                    Scale = 17f / 2048, FeatureCount = 1 },
            ];
            NativeTextStyleMetrics[] metrics =
            [new() { Ascent = 14 * units, Descent = 4 * units }, new() { Ascent = 18 * units, Descent = 4 * units }];
            NativeHintedParagraphDeviceStyle[] devices =
            [
                new() { FontIndex = 0, SourceScale = styles[0].Scale, LogicalUnitsPerPhysicalPixel = units,
                    XPixelsPerEm266 = 13 * 64, YPixelsPerEm266 = 13 * 64, Interpreter = (uint)interpreter,
                    XPhase266 = variant == 1 ? 7u : 0u, YPhase266 = variant == 1 ? 11u : 0u },
                new() { FontIndex = second, SourceScale = styles[1].Scale, LogicalUnitsPerPhysicalPixel = units,
                    XPixelsPerEm266 = 17 * 64, YPixelsPerEm266 = 17 * 64, Interpreter = (uint)interpreter,
                    XPhase266 = variant == 1 ? 19u : 0u, YPhase266 = variant == 1 ? 23u : 0u },
            ];
            Vector4[] colors = [new(1, 0, 0, 1), new(0, 0, 1, 1)];
            Vector4 clear = new(0, 0, 0, 1);
            Vector2 origin = new(4 * units, 4 * units);
            var shaping = new NativeTextShapeInput([], source, direction: direction, unicodeScript: 0x6C61746E, features: features);
            var options = new NativeTextParagraphOptions(1, 46 * units, Alignment: NativeTextAlignment.Center);
            using var paragraph = textContext.LayoutHintedParagraph(in shaping, in options, styles, metrics, devices);
            Check(paragraph.Runs.Length >= 2 && paragraph.SourceScalars.Length == source.Length &&
                paragraph.Lines.Length > 0 && paragraph.Lines.ToArray().All(line => line.Height > 0 && line.BaselineY > 0),
                "real complete mixed-style measured paragraph with positive source metrics");
            var raw = Unpack(textContext, paragraph, source, features, devices, origin, colors);
            Check(noInk ? raw.Glyphs.Length == 0 : raw.Glyphs.Length > 3,
                "source-indexed repeated draws and no-ink descriptors remain distinct");
            if (!noInk)
                Check(raw.Segments.Any(segment => segment.Kind == NativePathSegmentKind.Line) &&
                    raw.Segments.Any(segment => segment.Kind == NativePathSegmentKind.Quadratic),
                    "independent original contours exercise both straight and quadratic source coverage");

            var beforePreparation = subject.GetLastSubmissionToken();
            uint targetGeneration = target.Generation;
            var invalidColors = colors.ToArray(); invalidColors[^1].W = float.NaN;
            Reject<ArgumentException>(() => { using var invalid = paragraph.PrepareFrame(target, dpi, invalidColors,
                origin, clear, coverage: NativeHintedCoverage.NonzeroVector); }, "late invalid style color");
            Reject<NotSupportedException>(() => { using var invalid = paragraph.PrepareFrame(target, dpi * 2, colors,
                origin, clear, coverage: NativeHintedCoverage.NonzeroVector); }, "device/DPI mapping mismatch");
            Reject<NotSupportedException>(() => { using var invalid = paragraph.PrepareFrame(target, dpi, colors,
                origin, clear, NativeHintedProjectionPolicy.NativeCompute, NativeHintedCoverage.NonzeroVector); }, "unsupported forced projection");
            Check(subject.GetLastSubmissionToken().Equals(beforePreparation) && target.Generation == targetGeneration,
                "failed CPU preparation neither submits nor publishes target contents");

            using var automatic = paragraph.PrepareFrame(target, dpi, colors, origin, clear,
                NativeHintedProjectionPolicy.Automatic, NativeHintedCoverage.NonzeroVector);
            using var scalar = paragraph.PrepareFrame(target, dpi, colors, origin, clear,
                NativeHintedProjectionPolicy.ScalarReference, NativeHintedCoverage.NonzeroVector);
            Check(automatic.Width == target.Width && automatic.Height == target.Height && automatic.DpiScale == dpi &&
                automatic.Projection == NativeHintedProjectionPolicy.Automatic && scalar.Projection == NativeHintedProjectionPolicy.ScalarReference &&
                automatic.Coverage == NativeHintedCoverage.NonzeroVector && scalar.Coverage == NativeHintedCoverage.NonzeroVector &&
                subject.GetLastSubmissionToken().Equals(beforePreparation) && target.Generation == targetGeneration,
                "prepared immutable policy/target identity, no GPU preparation work");
            var retainedPositions = MemoryMarshal.AsBytes(paragraph.Glyphs).ToArray();
            var retainedOwners = MemoryMarshal.AsBytes(paragraph.PositionedOwners).ToArray();
            textContext.Dispose();
            Array.Clear(fontBytes); Array.Clear(source); Array.Clear(features); Array.Clear(styles);
            Array.Clear(metrics); Array.Clear(devices); Array.Clear(colors);
            paragraph.Dispose();
            Check(retainedPositions.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(paragraph.Glyphs)) &&
                retainedOwners.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(paragraph.PositionedOwners)),
                "original formatted snapshots survive source/context/paragraph retirement");
            Reject<ObjectDisposedException>(() => { using var invalid = paragraph.PrepareFrame(target, dpi,
                new Vector4[2], origin, clear, coverage: NativeHintedCoverage.NonzeroVector); }, "retired paragraph cannot publish a new frame");

            using (var wrongTarget = CreateTarget(context, "Same-size wrong paragraph target identity"))
            {
                uint wrongGeneration = wrongTarget.Generation;
                Reject<ArgumentException>(() => subject.RenderHintedParagraphFrame(wrongTarget, automatic), "different target object");
                Check(subject.GetLastSubmissionToken().Equals(beforePreparation) && target.Generation == targetGeneration &&
                    wrongTarget.Generation == wrongGeneration, "wrong target rejected before GPU/publication");
            }

            byte[]? firstPixels = null;
            foreach (var frame in new[] { automatic, automatic, scalar })
            {
                var before = subject.GetLastSubmissionToken();
                uint beforeGeneration = target.Generation, beforeView = target.ViewGeneration;
                var actualMetrics = subject.RenderHintedParagraphFrame(target, frame);
                byte[] actualPixels = CompleteAndRead(subject, target, before);
                Check(target.Generation == beforeGeneration + 1 && target.ViewGeneration == beforeView,
                    "successful selected render publishes content without changing prepared view");
                var referenceBefore = referenceRenderer.GetLastSubmissionToken();
                var expectedMetrics = referenceRenderer.RenderGlyphs(referenceTarget, dpi,
                    raw.Outlines, raw.Segments, raw.Glyphs, clear); // flags/revision zero, original default draw state
                byte[] expectedPixels = CompleteAndRead(referenceRenderer, referenceTarget, referenceBefore);
                Check(actualPixels.Length == 96 * 96 * 4 && actualPixels.AsSpan().SequenceEqual(expectedPixels),
                    "every raw GPU pixel matches independently unpacked source records");
                Check(actualMetrics.Equals(expectedMetrics) && actualMetrics.SubmissionCount == ++submittedFrames &&
                    actualMetrics.GlyphCount == raw.Glyphs.Length && actualMetrics.PayloadHash == 0,
                    $"every glyph frame metric matches reference and exactly one native render submission; actual={actualMetrics}, expected={expectedMetrics}");
                Check(firstPixels is null || firstPixels.AsSpan().SequenceEqual(actualPixels),
                    "automatic/scalar prepared frames preserve repeated original output");
                firstPixels ??= actualPixels;
                VerifyPixels(actualPixels, actualMetrics, noInk);
            }
            scalar.Dispose();
            var completed = subject.GetLastSubmissionToken();
            targetGeneration = target.Generation;
            Reject<ObjectDisposedException>(() => subject.RenderHintedParagraphFrame(target, scalar), "retired frame owner");
            Check(subject.GetLastSubmissionToken().Equals(completed) && target.Generation == targetGeneration,
                "retired frame rejected before GPU/publication");
            uint originalViewGeneration = target.ViewGeneration;
            target.Resize(97, 96); target.Resize(96, 96);
            Check(target.Width == automatic.Width && target.Height == automatic.Height &&
                target.ViewGeneration != originalViewGeneration, "same target dimensions restored with a genuinely recreated view");
            targetGeneration = target.Generation;
            Reject<ArgumentException>(() => subject.RenderHintedParagraphFrame(target, automatic), "recreated original target view");
            Check(subject.GetLastSubmissionToken().Equals(completed) && target.Generation == targetGeneration,
                "recreated view rejected before GPU/publication");
            target.Dispose();
            Reject<ObjectDisposedException>(() => subject.RenderHintedParagraphFrame(target, automatic), "disposed original target");
            Check(subject.GetLastSubmissionToken().Equals(completed), "disposed target rejected before GPU submission");
            cases++;
            Console.WriteLine($"package-consumer: loaded hinted paragraph GPU provider={context.BackendKind}, interpreter={interpreter}, " +
                $"variant={variant}, draws={raw.Glyphs.Length}, full pixels/every metric and retired ownership passed");
        }
        Console.WriteLine($"package-consumer: loaded prepared hinted paragraph rendering passed ({cases} layouts, actual {context.BackendKind}); " +
            "independent raw source outlines with retained actual writer positions, not a public full-writer differential");
    }

    private static GpuTexture CreateTarget(WgpuContext context, string label) => new(context, 96, 96,
        TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc, label,
        alphaMode: GpuTextureAlphaMode.Premultiplied);

    private static byte[] CompleteAndRead(NativeCompositor renderer, GpuTexture target, NativeSubmissionToken before)
    {
        var token = renderer.GetLastSubmissionToken();
        Check(token.IsValid && token.Value > before.Value, "real native queue submission advanced");
        renderer.WaitForSubmission(token); // original real completion path and deadlines
        Check(renderer.IsSubmissionComplete(token), "actual native submission remains complete");
        return target.ReadPixels(); // original texture copy/map ownership and deadline
    }

    private sealed record RawFrame(NativeGlyphOutline[] Outlines, NativePathSegment[] Segments, NativePositionedGlyph[] Glyphs);

    private static RawFrame Unpack(NativeTextShapingContext context, NativeHintedParagraph paragraph, NativeTextScalar[] source,
        NativeTextFeature[] features, NativeHintedParagraphDeviceStyle[] devices, Vector2 origin, Vector4[] colors)
    {
        var outlines = new List<NativeGlyphOutline>();
        var segments = new List<NativePathSegment>();
        var maps = new List<uint[]>();
        var originalOwners = new List<NativeHintedParagraphGlyphOwner>();
        var logical = new List<NativeTextShapingGlyph>();
        for (int runIndex = 0; runIndex < paragraph.Runs.Length; runIndex++)
        {
            var run = paragraph.Runs[runIndex]; var style = paragraph.Styles[checked((int)run.StyleIndex)];
            var device = devices[checked((int)run.StyleIndex)];
            int start = checked((int)run.ScalarStart), count = checked((int)run.ScalarCount);
            var input = new NativeTextShapeInput([], source.AsSpan(start, count),
                direction: (run.BidiLevel & 1) == 0 ? NativeTextDirection.LeftToRight : NativeTextDirection.RightToLeft,
                unicodeScript: 0x6C61746E, language: style.Language,
                features: features.AsSpan(checked((int)style.FeatureStart), checked((int)style.FeatureCount)),
                preContext: source.AsSpan(0, start), postContext: source.AsSpan(start + count));
            using var original = context.ShapeHintedRun(in input, run.FontIndex, device.XPixelsPerEm266, device.YPixelsPerEm266,
                (NativeFontHintInterpreter)device.Interpreter, device.XPhase266, device.YPhase266);
            var shaped = new NativeTextShapingGlyph[checked((int)original.GlyphCount)];
            var descriptors = new uint[shaped.Length]; original.CopyGlyphsTo(shaped, descriptors);
            Check(original.GlyphCount == run.LogicalCount && run.LogicalStart == logical.Count &&
                run.SourceDescriptorCount <= original.OutlineCounts.Glyphs, "independent full run source/descriptor partition");
            // Latin fixtures have one scalar per cluster; restoring original
            // logical order is independently source-indexed, never glyph-ID based.
            foreach (int index in Enumerable.Range(0, shaped.Length).OrderBy(index => shaped[index].Cluster))
            {
                logical.Add(shaped[index]);
                originalOwners.Add(new() { RunIndex = checked((uint)runIndex), RunGlyphIndex = checked((uint)index),
                    DescriptorIndex = descriptors[index] });
            }
            var rawGlyphs = new NativeHintedGlyph[checked((int)original.OutlineCounts.Glyphs)];
            var points = new NativeHintedPoint[checked((int)original.OutlineCounts.Points)];
            var tags = new byte[points.Length]; var contours = new int[checked((int)original.OutlineCounts.Contours)];
            original.CopyOutlinesTo(rawGlyphs, points, tags, contours);
            for (int i = 0; i < shaped.Length; i++)
                Check(descriptors[i] < run.SourceDescriptorCount && rawGlyphs[checked((int)descriptors[i])].GlyphIndex == shaped[i].GlyphId,
                    "each original shaped glyph references its exact independently captured raw descriptor");
            var rawBytes = MemoryMarshal.AsBytes(rawGlyphs.AsSpan()).ToArray();
            var pointBytes = MemoryMarshal.AsBytes(points.AsSpan()).ToArray();
            var originalTags = tags.ToArray(); var originalContours = contours.ToArray();
            var map = new uint[checked((int)run.SourceDescriptorCount)]; Array.Fill(map, uint.MaxValue);
            for (int descriptor = 0; descriptor < map.Length; descriptor++)
            {
                var raw = rawGlyphs[descriptor];
                var localPoints = points.AsSpan(checked((int)raw.PointOffset), checked((int)raw.PointCount));
                var localTags = tags.AsSpan(checked((int)raw.PointOffset), checked((int)raw.PointCount));
                var localContours = contours.AsSpan(checked((int)raw.ContourOffset), checked((int)raw.ContourCount));
                Check((raw.OutlineFlags & ~0x10Du) == 0,
                    $"raw outline belongs to explicit nonzero-vector public flag contract: run={runIndex}, descriptor={descriptor}, " +
                    $"glyph={raw.GlyphIndex}, flags=0x{raw.OutlineFlags:X}, points={raw.PointCount}, contours={raw.ContourCount}, " +
                    $"interpreter={device.Interpreter}, ppem=({device.XPixelsPerEm266},{device.YPixelsPerEm266}), " +
                    $"phase=({device.XPhase266},{device.YPhase266})");
                if (localPoints.IsEmpty)
                {
                    Check(localTags.IsEmpty && localContours.IsEmpty, "original no-ink topology");
                    continue;
                }
                Vector2 minimum = Physical(localPoints[0]), maximum = minimum;
                foreach (var point in localPoints) { var physical = Physical(point); minimum = Vector2.Min(minimum, physical); maximum = Vector2.Max(maximum, physical); }
                var decoded = DecodeContours(localPoints, localTags, localContours);
                if (decoded.Count == 0 || maximum.X <= minimum.X || maximum.Y <= minimum.Y) continue;
                map[descriptor] = checked((uint)outlines.Count);
                outlines.Add(new(checked((nuint)segments.Count), checked((nuint)decoded.Count), minimum, maximum, 1));
                segments.AddRange(decoded);
            }
            Check(rawBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(rawGlyphs.AsSpan())) &&
                pointBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(points.AsSpan())) &&
                originalTags.AsSpan().SequenceEqual(tags) && originalContours.AsSpan().SequenceEqual(contours),
                "independent unpacking preserves every original raw metadata byte");
            maps.Add(map);
        }
        Check(MemoryMarshal.AsBytes(logical.ToArray().AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(paragraph.LogicalGlyphs)) &&
            MemoryMarshal.AsBytes(originalOwners.ToArray().AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(paragraph.LogicalOwners)),
            "independently executed original run glyphs and exact source/descriptor owners");
        var glyphs = new List<NativePositionedGlyph>();
        for (int i = 0; i < paragraph.Glyphs.Length; i++)
        {
            var positioned = paragraph.Glyphs[i]; var owner = originalOwners[checked((int)positioned.GlyphIndex)];
            Check(SameValue(owner, paragraph.PositionedOwners[i]) && positioned.GlyphId == logical[checked((int)positioned.GlyphIndex)].GlyphId,
                "actual positioned draw retains exact independently referenced logical source");
            uint outline = maps[checked((int)owner.RunIndex)][checked((int)owner.DescriptorIndex)];
            if (outline == uint.MaxValue) continue;
            var run = paragraph.Runs[checked((int)owner.RunIndex)];
            glyphs.Add(new(outline, new(positioned.X + origin.X, positioned.Y + origin.Y), new(1, 0), new(0, 1),
                colors[checked((int)run.StyleIndex)]));
        }
        return new(outlines.ToArray(), segments.ToArray(), glyphs.ToArray());
    }

    // Independent raw TrueType on/conic contour records. Only the curve-kind
    // bits determine topology; touch/scan metadata stays byte-exact above. The
    // package font is TrueType, so unexpected cubic/unknown topology fails.
    private static List<NativePathSegment> DecodeContours(ReadOnlySpan<NativeHintedPoint> points, ReadOnlySpan<byte> tags, ReadOnlySpan<int> contours)
    {
        var output = new List<NativePathSegment>();
        int start = 0;
        foreach (int end in contours)
        {
            Check(end >= start && end < points.Length, "original contour-end range");
            int count = end + 1 - start;
            var contour = points.Slice(start, count); var kinds = tags.Slice(start, count);
            for (int i = 0; i < count; i++)
                Check((kinds[i] & 3) <= 1 && (kinds[i] & 0xE0) == 0 && (i == 0 || (kinds[i] & 4) == 0),
                    "original TrueType curve kinds and contour-start scan-mode-zero contract");
            start = end + 1;
            if (count < 2) continue;
            bool firstOn = (kinds[0] & 1) != 0, lastOn = (kinds[^1] & 1) != 0;
            Vector2 current = firstOn ? Physical(contour[0]) : lastOn ? Physical(contour[^1]) : Midpoint(Physical(contour[0]), Physical(contour[^1]));
            int index = firstOn ? 1 : 0, processed = 0;
            while (processed < count)
            {
                int selected = index % count;
                if ((kinds[selected] & 1) != 0)
                {
                    var to = Physical(contour[selected]); output.Add(new(NativePathSegmentKind.Line, current, to));
                    current = to; index++; processed++;
                }
                else
                {
                    int next = (index + 1) % count; var control = Physical(contour[selected]);
                    bool nextOn = (kinds[next] & 1) != 0;
                    var to = nextOn ? Physical(contour[next]) : Midpoint(control, Physical(contour[next]));
                    output.Add(new(NativePathSegmentKind.Quadratic, current, control, to)); current = to;
                    index += nextOn ? 2 : 1; processed += nextOn ? 2 : 1;
                }
            }
        }
        Check(start == points.Length, "every original contour point consumed");
        return output;
    }

    private static Vector2 Physical(NativeHintedPoint point)
    {
        Check(point.X266 is >= int.MinValue and <= int.MaxValue && point.Y266 is >= int.MinValue and <= int.MaxValue,
            "signed original device point range");
        var result = new Vector2((float)point.X266 / 64, (float)point.Y266 / 64);
        Check((double)result.X == point.X266 / 64.0 && (double)result.Y == point.Y266 / 64.0, "exact original physical point conversion");
        return result;
    }
    private static Vector2 Midpoint(Vector2 first, Vector2 second) => new((first.X + second.X) * 0.5f, (first.Y + second.Y) * 0.5f);

    private static void VerifyPixels(byte[] pixels, NativeGlyphFrameMetrics metrics, bool noInk)
    {
        bool firstColor = false, secondColor = false, painted = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            painted |= pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0;
            firstColor |= pixels[i] != 0 && pixels[i + 2] == 0;
            secondColor |= pixels[i + 2] != 0 && pixels[i] == 0;
            Check(pixels[i + 3] == 255, "every output alpha preserves opaque original clear");
        }
        Check(painted != noInk, "not an empty-image differential or a painted no-ink paragraph");
        if (!noInk)
            Check(firstColor && secondColor && metrics.InstanceUploadBytes != 0 && metrics.GlyphCount > 3,
                "both original source styles and repeated positioned draws reach the selected renderer");
        else
            Check(metrics.GlyphCount == 0 && metrics.RasterizedGlyphCount == 0 && metrics.InstanceUploadBytes == 0 &&
                metrics.OutlineUploadBytes == 0 && metrics.CoverageStagingBytes == 0 && metrics.UniformUploadBytes == 0,
                "original no-ink owners publish only clear, without raster/instance uploads");
    }
    private static bool SameValue<T>(T expected, T actual) where T : unmanaged =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref expected, 1)).SequenceEqual(
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref actual, 1)));
    private static void Reject<T>(Action action, string message) where T : Exception
    {
        bool rejected = false;
        try { action(); } catch (T) { rejected = true; }
        Check(rejected, message + " rejected");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Native prepared hinted paragraph GPU package: " + message);
    }
}
