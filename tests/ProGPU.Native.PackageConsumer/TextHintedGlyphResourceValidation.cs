using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;

// Original ProGPU public-package CPU controls. Canonical packets use the owned
// MIL builder; expected occurrences and ink bounds use public original hinted
// runs and the paragraph's actual writer frames, never design-outline decoding.
// The public package API does not decode compiled geometry/owner maps. Their
// typed differential belongs to the independent native controls, not this test.
// This fixture does not create a GPU, compare pixels or admit source Display.
internal static class TextHintedGlyphResourceValidation
{
    private const uint Visual = 1, Target = 2, RenderData = 3, Brush = 4, GlyphRun = 5, OtherGlyphRun = 6;
    private const ulong SceneId = 0x48494E544544;

    internal static void Run(string fontPath, NativeMilBackend backend)
    {
        Check(backend is NativeMilBackend.WgpuNative or NativeMilBackend.Dawn, "explicit supported MIL provider");
        NativeRendererInfo info = backend == NativeMilBackend.Dawn
            ? NativeDawnAdapter.GetInfo() : NativeCompositor.GetInfo();
        Check(info.Capabilities.HasFlag(NativeRendererCapabilities.WpfMilChannel), "loaded provider has actual MIL capability");
        if (backend == NativeMilBackend.Dawn)
            Check(info.BackendAbi == NativeDawnAdapter.BackendAbi && info.Name.Contains("Dawn provider", StringComparison.Ordinal),
                "actual loaded Dawn module identity, not an inferred adapter or fallback");
        int cases = 0;
        foreach (var interpreter in new[] { NativeFontHintInterpreter.TrueType35, NativeFontHintInterpreter.TrueType40 })
        foreach (float dpi in new[] { 1f, 2f })
        foreach (bool noInk in new[] { false, true })
        {
            RunCase(fontPath, backend, interpreter, dpi, noInk);
            cases++;
        }
        Console.WriteLine($"package-consumer: loaded hinted glyph resource/MIL CPU transport passed ({cases} layouts, " +
            $"actual {backend}, module={info.Name}); original occurrence/raw records, full public stream/metric rollback and retirement; " +
            "public API does not decode compiled geometry/owner maps or qualify pixels/Display");
    }

    private static void RunCase(string fontPath, NativeMilBackend backend, NativeFontHintInterpreter interpreter, float dpi, bool noInk)
    {
        byte[] font = File.ReadAllBytes(fontPath);
        uint upm = ReadUnitsPerEm(font); // Actual original SFNT metadata, not a fixture-specific guessed UPM.
        float units = 1 / dpi, em = 13 * units;
        using var context = new NativeTextShapingContext(font);
        NativeTextScalar[] source = (noInk ? "          " : "AVA O O O ").Select((value, index) => new NativeTextScalar
        {
            CodePoint = value, InputIndex = checked((uint)(9 + index)), InputLength = 1,
        }).ToArray();
        NativeTextFeature[] features = [new() { Tag = 0x6B65726E, Value = 0, Start = 0, End = uint.MaxValue }];
        NativeTextStyleRun[] styles = [new() { ScalarCount = checked((uint)source.Length), FontIndex = 0, Scale = em / upm, FeatureCount = 1 }];
        NativeTextStyleMetrics[] metrics = [new() { Ascent = 14 * units, Descent = 4 * units }];
        NativeHintedParagraphDeviceStyle[] devices = [new()
        {
            FontIndex = 0, SourceScale = styles[0].Scale, LogicalUnitsPerPhysicalPixel = units,
            XPixelsPerEm266 = 13 * 64, YPixelsPerEm266 = 13 * 64, Interpreter = (uint)interpreter,
            XPhase266 = 7, YPhase266 = 11,
        }];
        Vector2 origin = new(4 * units, 4 * units);
        var shaping = new NativeTextShapeInput([], source, direction: NativeTextDirection.LeftToRight,
            unicodeScript: 0x6C61746E, features: features);
        var options = new NativeTextParagraphOptions(1, 200 * units, Alignment: NativeTextAlignment.Center);
        using var paragraph = context.LayoutHintedParagraph(in shaping, in options, styles, metrics, devices);
        using var original = context.ShapeHintedRun(in shaping, 0, devices[0].XPixelsPerEm266, devices[0].YPixelsPerEm266,
            interpreter, devices[0].XPhase266, devices[0].YPhase266);
        var raw = RawSnapshot.Copy(original);
        Check(paragraph.Runs.Length == 1 && paragraph.Runs[0].FontIndex == 0 && paragraph.Runs[0].StyleIndex == 0 &&
            paragraph.Runs[0].SourceScale == em / upm && paragraph.Runs[0].LogicalUnitsPerPhysicalPixel == units &&
            paragraph.Runs[0].LogicalStart == 0 && paragraph.Runs[0].LogicalCount == original.GlyphCount &&
            paragraph.Glyphs.Length == original.GlyphCount && paragraph.Lines.Length == 1,
            "one coherent original horizontal source/run/font/device generation");
        Check(Same<NativeTextScalar>(source, paragraph.SourceScalars) && Same<NativeTextStyleRun>(styles, paragraph.Styles) &&
            Same<NativeTextStyleMetrics>(metrics, paragraph.SourceMetrics), "original public source/style/metric snapshots");
        var selected = SelectOriginal(paragraph, raw, origin, dpi, em, noInk);
        byte[] positionedBytes = MemoryMarshal.AsBytes(paragraph.Glyphs).ToArray();
        byte[] ownerBytes = MemoryMarshal.AsBytes(paragraph.PositionedOwners).ToArray();

        Reject<NotSupportedException>(() => { using var invalid = paragraph.PrepareGlyphResource(dpi * 2,
            coverage: NativeHintedCoverage.AntialiasedVector); }, "different device/DPI contract");
        Reject<NotSupportedException>(() => { using var invalid = paragraph.PrepareGlyphResource(dpi,
            NativeHintedProjectionPolicy.NativeCompute, NativeHintedCoverage.AntialiasedVector); }, "unavailable forced CPU projection");
        using var resource = paragraph.PrepareGlyphResource(dpi, coverage: NativeHintedCoverage.AntialiasedVector);
        using var scalar = paragraph.PrepareGlyphResource(dpi, NativeHintedProjectionPolicy.ScalarReference,
            NativeHintedCoverage.AntialiasedVector);
        Check(resource.DpiScale == dpi && scalar.DpiScale == dpi && resource.Projection == NativeHintedProjectionPolicy.Automatic &&
            scalar.Projection == NativeHintedProjectionPolicy.ScalarReference && resource.Coverage == NativeHintedCoverage.AntialiasedVector &&
            scalar.Coverage == NativeHintedCoverage.AntialiasedVector && SameValue(resource.Counts, paragraph.Counts) &&
            SameValue(scalar.Counts, paragraph.Counts) && SameValue(resource.Result, paragraph.Result) && SameValue(scalar.Result, paragraph.Result),
            "target/paint-independent original counts/result and actual AA execution policies");
        raw.Unchanged(original);

        using var channel = new NativeMilChannel(backend);
        Check(channel.Backend == backend && channel.ResourceCount == 0, "fresh selected-module channel");
        byte[] batch = BuildSourceBatch(selected);
        NativeMilHintedGlyphBinding[] bindings = [Binding(GlyphRun, 0, 0, selected.Indices.Length, origin)];
        // Unused index tails are deliberate invalid sentinels, not extra draws.
        uint[] indices = [.. selected.Indices, uint.MaxValue, uint.MaxValue];
        byte[] batchBefore = batch.ToArray(), bindingBefore = MemoryMarshal.AsBytes(bindings.AsSpan()).ToArray();
        uint[] indicesBefore = indices.ToArray();
        var badIndices = indices.ToArray(); badIndices[selected.Indices.Length - 1] = uint.MaxValue;
        RejectMil(NativeMilStatus.InvalidArgument, () => channel.ApplyWithHintedGlyphResources(batch, [resource], bindings, badIndices),
            "late original occurrence rejects the complete initial canonical graph");
        Check(channel.ResourceCount == 0 && !channel.TryGetVisual(Visual, out _) && !channel.TryGetTarget(Target, out _),
            "failed initial import publishes no resource/visual/target");
        channel.ApplyWithHintedGlyphResources(batch, [resource], bindings, indices);
        Check(batchBefore.AsSpan().SequenceEqual(batch) && bindingBefore.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(bindings.AsSpan())) &&
            indicesBefore.AsSpan().SequenceEqual(indices), "every original batch/binding/index byte and unused tail remains unchanged");
        raw.Unchanged(original);

        ulong serial = 0;
        NativeMilStatefulCompiledScene Compile()
        {
            var request = new NativeMilSceneBuildRequest(Target, SceneId, 1,
                MonotonicTimeNanoseconds: 1_000_000, RequestSerial: ++serial, DpiScaleX: dpi, DpiScaleY: dpi);
            var scene = channel.CompileScene(request);
            Check(scene.BuildResult.RequestSerial == request.RequestSerial, "fresh actual native compile request identity");
            return scene;
        }
        var baseline = Capture(channel, Compile, backend, noInk);

        var lateCanonical = new NativeMilBatchBuilder();
        lateCanonical.SetVisualOffset(Visual, 3.5, -2.25);
        lateCanonical.CreateResource(OtherGlyphRun, NativeMilResourceType.Visual);
        lateCanonical.SetVisualContent(Visual, 0x70000001);
        RejectMil(NativeMilStatus.InvalidHandle, () => channel.ApplyWithHintedGlyphResources(lateCanonical.WrittenSpan,
            [resource], bindings, indices), "late canonical handle after candidate mutations");
        Unchanged(channel, Compile, backend, noInk, baseline);

        var lateBinding = new NativeMilBatchBuilder();
        lateBinding.SetVisualOffset(Visual, 3.5, -2.25);
        WriteRun(lateBinding, OtherGlyphRun, selected);
        NativeMilHintedGlyphBinding[] twoBindings = [bindings[0], Binding(OtherGlyphRun, 1, selected.Indices.Length, selected.Indices.Length, origin)];
        uint[] twoIndices = [.. selected.Indices, .. selected.Indices]; twoIndices[^1] = uint.MaxValue;
        RejectMil(NativeMilStatus.InvalidArgument, () => channel.ApplyWithHintedGlyphResources(lateBinding.WrittenSpan,
            [resource, scalar], twoBindings, twoIndices), "late second binding after a valid first binding and complete imported resources");
        Unchanged(channel, Compile, backend, noInk, baseline);

        ushort[] badIds = selected.GlyphIds.ToArray(); badIds[^1] ^= 1;
        RejectRun(channel, resource, selected with { GlyphIds = badIds }, bindings, indices, "late canonical glyph ID");
        Unchanged(channel, Compile, backend, noInk, baseline);
        RejectRun(channel, resource, selected with { Run = selected.Run with { EmSize = em * 2 } }, bindings, indices, "different original source em");
        Unchanged(channel, Compile, backend, noInk, baseline);
        float[] badAdvances = selected.Advances.ToArray(); badAdvances[^2] += 0.125f;
        RejectRun(channel, resource, selected with { Advances = badAdvances }, bindings, indices, "late original advance/offset interaction");
        Unchanged(channel, Compile, backend, noInk, baseline);
        if (!noInk)
        {
            var bounds = selected.Run.ManagedBounds;
            RejectRun(channel, resource, selected with { Run = selected.Run with { ManagedBounds = bounds with { Width = 0 } } },
                bindings, indices, "original hinted ink outside unchanged source bounds");
            Unchanged(channel, Compile, backend, noInk, baseline);
        }
        var sourceMutation = new NativeMilBatchBuilder(); sourceMutation.SetVisualOffset(Visual, 3.5, -2.25);
        foreach (var invalid in new[]
        {
            Binding(GlyphRun, 1, 0, selected.Indices.Length, origin),
            Binding(GlyphRun, 0, 0, selected.Indices.Length, origin) with { FontIndex = 1 },
            Binding(GlyphRun, 0, 0, selected.Indices.Length - 1, origin),
            Binding(GlyphRun, 0, 0, selected.Indices.Length, origin) with { Reserved = 1 },
            Binding(GlyphRun, 0, 0, selected.Indices.Length, origin + new Vector2(0.125f, 0)),
        })
        {
            RejectMil(NativeMilStatus.InvalidArgument, () => channel.ApplyWithHintedGlyphResources(sourceMutation.WrittenSpan,
                [resource], [invalid], indices), "invalid original resource/font/span/reserved/origin binding");
            Unchanged(channel, Compile, backend, noInk, baseline);
        }
        var changedBasis = bindings.ToArray(); changedBasis[0].Basis = Matrix3x2.CreateScale(2);
        RejectMil(NativeMilStatus.UnsupportedCommand, () => channel.ApplyWithHintedGlyphResources([], [resource], changedBasis, indices),
            "nonidentity source basis is not silently projected");
        Unchanged(channel, Compile, backend, noInk, baseline);

        // Scalar import actually republishes the same original generation; it
        // must preserve the complete public scene, not just the topmost draw.
        var recreation = new NativeMilBatchBuilder(); WriteRun(recreation, GlyphRun, selected);
        channel.ApplyWithHintedGlyphResources(recreation.WrittenSpan, [scalar], bindings, indices);
        Unchanged(channel, Compile, backend, noInk, baseline);
        raw.Unchanged(original);
        scalar.Dispose();
        Reject<ObjectDisposedException>(() => channel.ApplyWithHintedGlyphResources(lateBinding.WrittenSpan,
            [resource, scalar], twoBindings, twoIndices), "disposed later resource after acquiring the first import lease");
        Unchanged(channel, Compile, backend, noInk, baseline);
        channel.ApplyWithHintedGlyphResources([], [resource], bindings, indices);
        Unchanged(channel, Compile, backend, noInk, baseline);

        original.Dispose(); context.Dispose(); paragraph.Dispose(); resource.Dispose();
        Array.Clear(font); Array.Clear(source); Array.Clear(features); Array.Clear(styles); Array.Clear(metrics); Array.Clear(devices);
        Array.Clear(batch); Array.Clear(bindings); Array.Clear(indices); Array.Clear(selected.GlyphIds); Array.Clear(selected.Advances);
        Array.Clear(selected.Offsets); Array.Clear(selected.Indices);
        Check(resource.IsDisposed && scalar.IsDisposed && positionedBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(paragraph.Glyphs)) &&
            ownerBytes.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(paragraph.PositionedOwners)),
            "original writer snapshots survive source/context/paragraph/producer-wrapper retirement");
        Unchanged(channel, Compile, backend, noInk, baseline);
        Reject<ObjectDisposedException>(() => { using var invalid = paragraph.PrepareGlyphResource(dpi,
            coverage: NativeHintedCoverage.AntialiasedVector); }, "retired paragraph cannot prepare a new resource");
        Reject<ObjectDisposedException>(() => channel.ApplyWithHintedGlyphResources(lateCanonical.WrittenSpan,
            [resource], [], []), "retired resource cannot publish candidate canonical mutations");
        Unchanged(channel, Compile, backend, noInk, baseline);
        channel.Dispose();
        Reject<ObjectDisposedException>(() => channel.ApplyWithHintedGlyphResources([], [], [], []), "retired selected-provider channel");
        Check(channel.IsDisposed, "explicit channel retirement");
    }

    private sealed record Selection(NativeMilGlyphRun Run, ushort[] GlyphIds, float[] Advances, Vector2[] Offsets, uint[] Indices);

    private static Selection SelectOriginal(NativeHintedParagraph paragraph, RawSnapshot raw, Vector2 origin, float dpi, float em, bool noInk)
    {
        Check(Same<NativeTextShapingGlyph>(raw.Glyphs, paragraph.LogicalGlyphs), "independent original hinted glyph metrics/source identities");
        var ids = new ushort[paragraph.Glyphs.Length]; var advances = new float[ids.Length];
        var offsets = new Vector2[ids.Length]; var indices = new uint[ids.Length];
        Vector2 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        int ink = 0, empty = 0, repeated = 0; float cursor = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            var glyph = paragraph.Glyphs[i]; var owner = paragraph.PositionedOwners[i];
            Check(glyph.GlyphIndex == i && glyph.FontIndex == 0 && glyph.AdvanceY == 0 && paragraph.BidiLevels[i] == 0 &&
                owner.RunIndex == 0 && owner.RunGlyphIndex == i && owner.DescriptorIndex == raw.Descriptors[i] &&
                SameValue(owner, paragraph.LogicalOwners[i]) && glyph.GlyphId == raw.Glyphs[i].GlyphId,
                "each actual original occurrence retains its full logical/run/descriptor identity");
            Check(owner.DescriptorIndex < paragraph.Runs[0].SourceDescriptorCount && owner.DescriptorIndex < raw.Outlines.Length,
                "original source descriptor range, including no ink");
            var outline = raw.Outlines[checked((int)owner.DescriptorIndex)];
            Check(outline.GlyphIndex == glyph.GlyphId, "independent raw descriptor has the selected original glyph");
            indices[i] = checked((uint)i); ids[i] = checked((ushort)glyph.GlyphId); advances[i] = glyph.AdvanceX;
            var position = new Vector2(glyph.X + origin.X, glyph.Y + origin.Y);
            float prefix = origin.X + cursor;
            offsets[i] = new(position.X - prefix, glyph.Y);
            Check(prefix + offsets[i].X == position.X && origin.Y + offsets[i].Y == position.Y && float.IsFinite(cursor + advances[i]),
                "actual measured advances and source offsets reconstruct exact original writer placement");
            cursor += advances[i];
            var points = raw.Points.AsSpan(checked((int)outline.PointOffset), checked((int)outline.PointCount));
            if (points.IsEmpty)
            {
                Check(outline.ContourCount == 0, "original no-ink occurrence has no invented contours"); empty++; continue;
            }
            var lo = Physical(points[0]); var hi = lo;
            foreach (var point in points) { var p = Physical(point); lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
            Check(hi.X > lo.X && hi.Y > lo.Y && outline.ContourCount > 0, "actual selected original hinted ink bounds");
            minimum = Vector2.Min(minimum, new(position.X + lo.X / dpi, position.Y - hi.Y / dpi));
            maximum = Vector2.Max(maximum, new(position.X + hi.X / dpi, position.Y - lo.Y / dpi));
            ink++;
            if (ids.AsSpan(0, i).Contains(ids[i])) repeated++;
        }
        Check(noInk ? empty == ids.Length && ink == 0 : ink > 3 && empty > 0 && repeated > 0,
            "every repeated/no-ink occurrence stays source-indexed, never selected by glyph ID or outline phase");
        NativeMilRect bounds = ink == 0 ? new(0, 0, 0, 0) : new(minimum.X, minimum.Y,
            (double)maximum.X - minimum.X, (double)maximum.Y - minimum.Y);
        return new(new(new(origin.X, origin.Y), em, bounds), ids, advances, offsets, indices);
    }

    private static byte[] BuildSourceBatch(Selection selected)
    {
        var drawing = new NativeMilRenderDataBuilder(); drawing.DrawGlyphRun(Brush, GlyphRun);
        var batch = new NativeMilBatchBuilder();
        batch.CreateResource(Visual, NativeMilResourceType.Visual);
        batch.CreateResource(Target, NativeMilResourceType.GenericRenderTarget);
        batch.CreateResource(RenderData, NativeMilResourceType.RenderData);
        batch.CreateResource(Brush, NativeMilResourceType.SolidColorBrush);
        batch.CreateVisual(Visual); batch.SetVisualContent(Visual, RenderData);
        batch.SetSolidColorBrush(Brush, new(0.2f, 0.6f, 1, 1));
        WriteRun(batch, GlyphRun, selected); batch.SetRenderData(RenderData, drawing);
        batch.CreateGenericTarget(Target, 256, 96); batch.SetTargetClearColor(Target, new(0, 0, 0, 1));
        batch.SetTargetRoot(Target, Visual);
        return batch.ToArray();
    }

    private static void WriteRun(NativeMilBatchBuilder batch, uint handle, Selection selected) =>
        batch.SetGlyphRun(handle, selected.Run, selected.GlyphIds, selected.Advances, selected.Offsets);

    private static NativeMilHintedGlyphBinding Binding(uint handle, uint resource, int start, int count, Vector2 origin) => new()
    {
        GlyphRunHandle = handle, ResourceIndex = resource, FontIndex = 0,
        PositionedIndexStart = checked((uint)start), PositionedIndexCount = checked((uint)count),
        LogicalOrigin = origin, Basis = Matrix3x2.Identity,
    };

    private sealed record ChannelSnapshot(nuint ResourceCount, NativeMilVisualSnapshot Visual, NativeMilTargetSnapshot Target,
        NativeMilStatefulCompiledScene Scene, NativeSceneUpdateMetrics Validation);

    private static ChannelSnapshot Capture(NativeMilChannel channel, Func<NativeMilStatefulCompiledScene> compile, NativeMilBackend backend, bool noInk)
    {
        bool foundVisual = channel.TryGetVisual(Visual, out var visual);
        bool foundTarget = channel.TryGetTarget(Target, out var target);
        Check(channel.ResourceCount == 5 && foundVisual && foundTarget,
            "complete original canonical graph has exactly five resources");
        var scene = compile();
        var validation = backend == NativeMilBackend.Dawn ? NativeDawnAdapter.ValidateScene(scene.Stream) : NativeCompositor.ValidateScene(scene.Stream);
        Check(scene.Stream.Length > 0 && scene.Metrics.VisualCount == 1 && scene.Metrics.RectangleCount == 0 && scene.Metrics.EllipseCount == 0 &&
            scene.Metrics.RoundedRectangleCount == 0 && scene.Metrics.LineCount == 0 && scene.Metrics.BrushCount == 0 &&
            scene.Metrics.MaximumVisualDepth == 1 &&
            scene.Metrics.StreamBytes == (ulong)scene.Stream.Length && scene.BuildResult.StreamBytes == (ulong)scene.Stream.Length &&
            scene.BuildResult.Flags == NativeMilSceneBuildResultFlags.None && scene.BuildResult.NextDueTimeNanoseconds == 0 &&
            validation.SceneId == SceneId && validation.Generation == 1 && validation.SnapshotBytes == (ulong)scene.Stream.Length &&
            validation.DrawCount == (noInk ? 0u : 1u) && validation.ValidationError == NativeSceneValidationError.None &&
            validation.ErrorOffset == 0 && !validation.SnapshotReused,
            "actual selected-provider CPU compilation/validator accepts every original draw or no-ink occurrence");
        Check(visual.Handle == Visual && visual.ContentHandle == RenderData && visual.ChildCount == 0 && visual.OffsetX == 0 &&
            visual.OffsetY == 0 && visual.Opacity == 1 && target.Handle == Target && target.RootHandle == Visual &&
            target.ClearRed == 0 && target.ClearGreen == 0 && target.ClearBlue == 0 && target.ClearAlpha == 1,
            "unaltered original source owner/content/paint/target snapshots");
        return new(channel.ResourceCount, visual, target, scene, validation);
    }

    private static void Unchanged(NativeMilChannel channel, Func<NativeMilStatefulCompiledScene> compile, NativeMilBackend backend,
        bool noInk, ChannelSnapshot before)
    {
        var after = Capture(channel, compile, backend, noInk);
        Check(after.ResourceCount == before.ResourceCount && after.Visual.Equals(before.Visual) && after.Target.Equals(before.Target) &&
            after.Scene.Metrics.Equals(before.Scene.Metrics) && after.Scene.BuildResult.Flags == before.Scene.BuildResult.Flags &&
            after.Scene.BuildResult.NextDueTimeNanoseconds == before.Scene.BuildResult.NextDueTimeNanoseconds &&
            after.Scene.BuildResult.StreamBytes == before.Scene.BuildResult.StreamBytes && after.Validation.Equals(before.Validation) &&
            after.Scene.Stream.AsSpan().SequenceEqual(before.Scene.Stream),
            "every public graph field/compiled byte/CPU metric survives rejected updates or original producer retirement");
    }

    private static void RejectRun(NativeMilChannel channel, NativeHintedGlyphResource resource, Selection selected,
        NativeMilHintedGlyphBinding[] bindings, uint[] indices, string label)
    {
        var update = new NativeMilBatchBuilder(); update.SetVisualOffset(Visual, 3.5, -2.25); WriteRun(update, GlyphRun, selected);
        RejectMil(NativeMilStatus.InvalidArgument, () => channel.ApplyWithHintedGlyphResources(update.WrittenSpan, [resource], bindings, indices), label);
    }

    private sealed record RawSnapshot(NativeTextShapingGlyph[] Glyphs, uint[] Descriptors, NativeHintedGlyph[] Outlines,
        NativeHintedPoint[] Points, byte[] Tags, int[] Contours)
    {
        internal static RawSnapshot Copy(NativeHintedTextRun run)
        {
            var glyphs = new NativeTextShapingGlyph[checked((int)run.GlyphCount)]; var descriptors = new uint[glyphs.Length];
            var outlines = new NativeHintedGlyph[checked((int)run.OutlineCounts.Glyphs)];
            var points = new NativeHintedPoint[checked((int)run.OutlineCounts.Points)]; var tags = new byte[points.Length];
            var contours = new int[checked((int)run.OutlineCounts.Contours)];
            run.CopyGlyphsTo(glyphs, descriptors); run.CopyOutlinesTo(outlines, points, tags, contours);
            return new(glyphs, descriptors, outlines, points, tags, contours);
        }
        internal void Unchanged(NativeHintedTextRun run)
        {
            var after = Copy(run);
            Check(Same<NativeTextShapingGlyph>(Glyphs, after.Glyphs) && Same<uint>(Descriptors, after.Descriptors) &&
                Same<NativeHintedGlyph>(Outlines, after.Outlines) && Same<NativeHintedPoint>(Points, after.Points) &&
                Tags.AsSpan().SequenceEqual(after.Tags) && Contours.AsSpan().SequenceEqual(after.Contours),
                "every independently captured raw flag/tag/point/contour/metric/source descriptor remains byte-exact");
        }
    }

    private static Vector2 Physical(NativeHintedPoint point)
    {
        var value = new Vector2((float)point.X266 / 64, (float)point.Y266 / 64);
        Check((double)value.X == point.X266 / 64.0 && (double)value.Y == point.Y266 / 64.0, "exact signed raw physical-point conversion");
        return value;
    }

    private static uint ReadUnitsPerEm(ReadOnlySpan<byte> bytes)
    {
        Check(bytes.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(bytes) == 0x00010000, "actual standalone TrueType SFNT");
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        Check(count > 0 && count <= (bytes.Length - 12) / 16, "bounded original SFNT directory");
        for (int i = 0; i < count; i++)
        {
            var table = bytes.Slice(12 + i * 16, 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(table) != 0x68656164) continue; // head
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(table[8..]), length = BinaryPrimitives.ReadUInt32BigEndian(table[12..]);
            Check(offset <= bytes.Length && length >= 20 && length <= (uint)bytes.Length - offset, "complete original head table");
            uint upm = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(checked((int)offset + 18), 2));
            Check(upm > 0, "actual original font units per em"); return upm;
        }
        throw new InvalidOperationException("Hinted glyph resource fixture requires the original font's head table.");
    }

    private static bool Same<T>(ReadOnlySpan<T> first, ReadOnlySpan<T> second) where T : unmanaged =>
        MemoryMarshal.AsBytes(first).SequenceEqual(MemoryMarshal.AsBytes(second));
    private static bool SameValue<T>(T first, T second) where T : unmanaged =>
        Same<T>(MemoryMarshal.CreateReadOnlySpan(ref first, 1), MemoryMarshal.CreateReadOnlySpan(ref second, 1));
    private static void RejectMil(NativeMilStatus expected, Action action, string label)
    {
        try { action(); }
        catch (NativeMilException error) { Check(error.Status == expected, $"{label}: exact native status {error.Status}/{expected}"); return; }
        throw new InvalidOperationException($"Hinted glyph resource fixture accepted {label}.");
    }
    private static void Reject<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Hinted glyph resource fixture accepted {label}.");
    }
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException($"Hinted glyph resource package control failed: {label}.");
    }
}
