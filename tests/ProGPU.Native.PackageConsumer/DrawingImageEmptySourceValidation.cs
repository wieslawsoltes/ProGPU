using System.Buffers.Binary;
using ProGPU.Backend.Native;

// Real packaged C imports on both providers. These are device-free channel,
// ownership and scene-compilation controls, not GPU or original-WPF evidence.
internal static class DrawingImageEmptySourceValidation
{
    internal static void Run()
    {
        foreach (NativeMilBackend backend in new[] { NativeMilBackend.WgpuNative, NativeMilBackend.Dawn })
        {
            ValidateDirectOwners(backend);
            ValidateRetainedCacheClosure(backend);
            Console.WriteLine($"package-consumer: {backend} known-empty DrawingImage ownership passed");
        }
    }

    private static void ValidateDirectOwners(NativeMilBackend backend)
    {
        using var channel = new NativeMilChannel(backend);
        var batch = new NativeMilBatchBuilder();
        batch.CreateResource(1, NativeMilResourceType.DrawingImage);
        batch.CreateResource(2, NativeMilResourceType.DrawingGroup);
        batch.CreateResource(3, NativeMilResourceType.Visual);
        batch.CreateResource(4, NativeMilResourceType.DrawingImage);
        batch.CreateResource(5, NativeMilResourceType.DrawingGroup);
        channel.Apply(batch.WrittenSpan);
        Reject(() => channel.SetDrawingImageEmptySource(1, 2), NativeMilStatus.InvalidHandle);
        batch.Clear();
        batch.SetDrawingImage(1, 0);
        batch.SetDrawingGroup(2, new NativeMilDrawingGroup(Opacity: 1), []);
        batch.CreateVisual(3);
        channel.Apply(batch.WrittenSpan);

        // Initialized, childless original drawing: no invented positive bounds.
        channel.SetDrawingImageBounds(1, new NativeMilRect(2, 3, 4, 5));
        ulong generation = channel.GetResourceGeneration(1);
        channel.SetDrawingImageEmptySource(1, 2);
        Check(channel.GetResourceGeneration(1) != generation, "binding did not advance image generation");
        generation = channel.GetResourceGeneration(1);
        nuint count = channel.ResourceCount;
        foreach ((uint image, uint drawing) in new[]
        {
            (0U, 2U), (99U, 2U), (3U, 2U), (4U, 2U),
            (1U, 0U), (1U, 99U), (1U, 3U), (1U, 5U),
        })
        {
            Reject(() => channel.SetDrawingImageEmptySource(image, drawing), NativeMilStatus.InvalidHandle);
            Check(channel.GetResourceGeneration(1) == generation && channel.ResourceCount == count,
                "rejected owner candidate changed the retained image");
        }
        Reject(() => channel.SetDrawingImageBounds(1, new NativeMilRect(0, 0, 8, 8)),
            NativeMilStatus.InvalidArgument);
        Check(channel.GetResourceGeneration(1) == generation, "bounds replaced the empty witness");

        batch.Clear();
        batch.DeleteResource(2, NativeMilResourceType.DrawingGroup);
        Reject(() => channel.Apply(batch.WrittenSpan), NativeMilStatus.InvalidGraph);
        Check(channel.GetResourceGeneration(1) == generation && channel.ResourceCount == count,
            "deleting an owned source changed channel state");

        // Canonical positive publication clears the witness; that ordinary
        // drawing is still a real dependency, not a detachable empty edge.
        batch.Clear();
        batch.SetDrawingImage(1, 2);
        channel.Apply(batch.WrittenSpan);
        Reject(() => channel.SetDrawingImageEmptySource(1, 2), NativeMilStatus.InvalidArgument);
        channel.SetDrawingImageBounds(1, new NativeMilRect(2, 3, 4, 5));
        batch.Clear();
        batch.DeleteResource(2, NativeMilResourceType.DrawingGroup);
        Reject(() => channel.Apply(batch.WrittenSpan), NativeMilStatus.InvalidGraph);

        // Only canonical null removes the former source ownership. Zero is
        // deliberately not an overloaded SetDrawingImageEmptySource clear.
        batch.Clear();
        batch.SetDrawingImage(1, 0);
        batch.DeleteResource(2, NativeMilResourceType.DrawingGroup);
        channel.Apply(batch.WrittenSpan);
        Check(channel.ResourceCount == count - 1, "canonical null retained the old empty source");
        Reject(() => channel.SetDrawingImageEmptySource(1, 2), NativeMilStatus.InvalidHandle);
        channel.Dispose();
        try { channel.SetDrawingImageEmptySource(1, 2); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("disposed DrawingImage owner admitted");
    }

    private static void ValidateRetainedCacheClosure(NativeMilBackend backend)
    {
        using var channel = new NativeMilChannel(backend);
        channel.Apply(CreateCacheScene().WrittenSpan);
        // These are explicit CPU fixture inputs, not inferred device limits or
        // a host policy claim. There is no device creation in this consumer.
        channel.SetBitmapCacheBrushRasterPolicy(CacheBrush,
            new NativeMilBitmapCacheRasterPolicy(1, 1, 4096, 4096, 1));
        // The white rectangle really supplies these nonempty source bounds;
        // the distinct DrawingImage below supplies no source geometry.
        channel.SetVisualCacheBounds(CacheVisual, new NativeMilRect(0, 0, 8, 8));
        NativeMilCompiledScene nullScene = Compile(channel);
        byte[] originalNullBytes = nullScene.Stream.ToArray();

        channel.SetDrawingImageEmptySource(Image, EmptyDrawing);
        NativeMilCompiledScene emptyScene = Compile(channel);
        byte[] originalEmptyBytes = emptyScene.Stream.ToArray();
        Check(!emptyScene.Stream.AsSpan().SequenceEqual(nullScene.Stream),
            "known-empty owner did not participate in retained cache identity");
        CheckUnchangedScene(channel, emptyScene);

        ulong imageGeneration = channel.GetResourceGeneration(Image);
        ulong drawingGeneration = channel.GetResourceGeneration(EmptyDrawing);
        ulong cacheGeneration = channel.GetResourceGeneration(Cache);
        nuint count = channel.ResourceCount;
        var update = new NativeMilBatchBuilder();
        update.SetBitmapCache(Cache, new NativeMilBitmapCache(RenderAtScale: 2));
        update.DeleteResource(EmptyDrawing, NativeMilResourceType.DrawingGroup);
        Reject(() => channel.Apply(update.WrittenSpan), NativeMilStatus.InvalidGraph);
        Check(channel.GetResourceGeneration(Cache) == cacheGeneration &&
            channel.GetResourceGeneration(Image) == imageGeneration &&
            channel.GetResourceGeneration(EmptyDrawing) == drawingGeneration && channel.ResourceCount == count,
            "late failed candidate partially published a cache/source revision");
        Reject(() => channel.SetDrawingImageEmptySource(Image, MissingDrawing), NativeMilStatus.InvalidHandle);
        Check(channel.GetResourceGeneration(Image) == imageGeneration, "failed witness candidate replaced prior ownership");
        CheckUnchangedScene(channel, emptyScene);

        // A nonpainting drawing update must still revise the cache closure.
        update.Clear();
        update.SetDrawingGroup(EmptyDrawing, new NativeMilDrawingGroup(Opacity: 0.5), []);
        channel.Apply(update.WrittenSpan);
        NativeMilCompiledScene revisedEmpty = Compile(channel);
        Check(channel.GetResourceGeneration(Image) == imageGeneration &&
            channel.GetResourceGeneration(EmptyDrawing) != drawingGeneration &&
            !revisedEmpty.Stream.AsSpan().SequenceEqual(emptyScene.Stream),
            "empty child generation was hidden behind canonical drawing zero");
        CheckUnchangedScene(channel, revisedEmpty);

        update.Clear();
        update.SetBitmapCache(Cache, new NativeMilBitmapCache(RenderAtScale: 2));
        channel.Apply(update.WrittenSpan);
        NativeMilCompiledScene resized = Compile(channel);
        Check(channel.GetResourceGeneration(Cache) != cacheGeneration &&
            !resized.Stream.AsSpan().SequenceEqual(revisedEmpty.Stream),
            "selected cache revision was omitted from retained compilation");
        CheckUnchangedScene(channel, resized);

        update.Clear();
        update.SetDrawingGroup(EmptyDrawing, new NativeMilDrawingGroup(Opacity: 1), [EmptyDrawing]);
        Reject(() => channel.Apply(update.WrittenSpan), NativeMilStatus.InvalidGraph);
        CheckUnchangedScene(channel, resized);

        // Initialized typed video is deliberately unsupported by this shader
        // capture family. Empty paint must not bypass descendant preflight.
        update.Clear();
        update.SetDrawingGroup(EmptyDrawing, new NativeMilDrawingGroup(Opacity: 0), [Video]);
        channel.Apply(update.WrittenSpan);
        Reject(() => Compile(channel), NativeMilStatus.UnsupportedCommand);
        Check(emptyScene.Stream.AsSpan().SequenceEqual(originalEmptyBytes),
            "failed scene compilation mutated an already published snapshot");
        update.Clear();
        update.SetDrawingGroup(EmptyDrawing, new NativeMilDrawingGroup(Opacity: 1), []);
        channel.Apply(update.WrittenSpan);
        _ = Compile(channel);

        // Refill through canonical publication, then return to the exact
        // source-known empty witness. The positive pixels stay native-owned;
        // this CPU control compares retained streams, not rendered colors.
        update.Clear();
        update.SetDrawingImage(Image, PositiveDrawing);
        channel.Apply(update.WrittenSpan);
        channel.SetDrawingImageBounds(Image, new NativeMilRect(0, 0, 4, 4));
        NativeMilCompiledScene positive = Compile(channel);
        Reject(() => channel.SetDrawingImageEmptySource(Image, EmptyDrawing), NativeMilStatus.InvalidArgument);
        CheckUnchangedScene(channel, positive);
        update.Clear();
        update.SetDrawingImage(Image, 0);
        channel.Apply(update.WrittenSpan);
        channel.SetDrawingImageEmptySource(Image, EmptyDrawing);
        NativeMilCompiledScene restored = Compile(channel);
        Check(!restored.Stream.AsSpan().SequenceEqual(positive.Stream), "positive-to-empty transition reused stale scene");
        Reject(() => channel.SetDrawingImageBounds(Image, new NativeMilRect(0, 0, 4, 4)),
            NativeMilStatus.InvalidArgument);
        CheckUnchangedScene(channel, restored);

        update.Clear();
        update.SetDrawingImage(Image, 0);
        update.DeleteResource(EmptyDrawing, NativeMilResourceType.DrawingGroup);
        channel.Apply(update.WrittenSpan);
        _ = Compile(channel);
        Check(channel.ResourceCount == count - 1, "cleared witness kept a dead drawing owner");
        channel.Dispose();
        Check(nullScene.Stream.AsSpan().SequenceEqual(originalNullBytes) &&
            emptyScene.Stream.AsSpan().SequenceEqual(originalEmptyBytes),
            "channel retirement invalidated owned managed scene snapshots");
    }

    private const uint Root = 1, Target = 2, Content = 3, White = 4, Shader = 5, Effect = 6,
        CacheBrush = 7, Cache = 8, CacheVisual = 9, CacheContent = 10, Image = 11,
        EmptyDrawing = 12, PositiveDrawing = 13, PositiveGeometry = 14, Color = 15,
        MissingDrawing = 16, Video = 17;

    private static NativeMilBatchBuilder CreateCacheScene()
    {
        var batch = new NativeMilBatchBuilder();
        foreach ((uint handle, NativeMilResourceType type) in new[]
        {
            (Root, NativeMilResourceType.Visual), (Target, NativeMilResourceType.GenericRenderTarget),
            (Content, NativeMilResourceType.RenderData), (White, NativeMilResourceType.SolidColorBrush),
            (Shader, NativeMilResourceType.PixelShader), (Effect, NativeMilResourceType.ShaderEffect),
            (CacheBrush, NativeMilResourceType.BitmapCacheBrush), (Cache, NativeMilResourceType.BitmapCache),
            (CacheVisual, NativeMilResourceType.Visual), (CacheContent, NativeMilResourceType.RenderData),
            (Image, NativeMilResourceType.DrawingImage), (EmptyDrawing, NativeMilResourceType.DrawingGroup),
            (PositiveDrawing, NativeMilResourceType.GeometryDrawing), (PositiveGeometry, NativeMilResourceType.RectangleGeometry),
            (Color, NativeMilResourceType.SolidColorBrush), (MissingDrawing, NativeMilResourceType.DrawingGroup),
            (Video, NativeMilResourceType.VideoDrawing),
        }) batch.CreateResource(handle, type);

        batch.CreateVisual(Root);
        batch.CreateVisual(CacheVisual);
        batch.SetSolidColorBrush(White, new NativeMilColor(1, 1, 1, 1));
        batch.SetSolidColorBrush(Color, new NativeMilColor(1, 0, 0, 1));
        batch.SetRectangleGeometry(PositiveGeometry, 0, 0, 4, 4);
        batch.SetGeometryDrawing(PositiveDrawing, Color, 0, PositiveGeometry);
        batch.SetDrawingGroup(EmptyDrawing, new NativeMilDrawingGroup(Opacity: 1), []);
        batch.SetVideoDrawing(Video, 0, 0, 4, 4, 0);
        batch.SetDrawingImage(Image, 0);
        batch.SetBitmapCache(Cache, new NativeMilBitmapCache(RenderAtScale: 1));
        batch.SetBitmapCacheBrush(CacheBrush, new NativeMilBitmapCacheBrush(CacheVisual, Cache));
        batch.SetPixelShader(Shader, SampleProgram());
        batch.SetShaderEffect(Effect, Shader, [], [], 0, NativeMilShaderSamplingMode.NearestNeighbor, CacheBrush);
        batch.SetVisualEffect(Root, Effect);
        var options = new NativeMilRenderOptions(NativeMilRenderOptionFlags.EdgeMode |
            NativeMilRenderOptionFlags.BitmapScalingMode, NativeMilEdgeMode.Aliased, NativeMilBitmapScalingMode.NearestNeighbor);
        batch.SetVisualRenderOptions(Root, options);
        batch.SetVisualRenderOptions(CacheVisual, options);
        var content = new NativeMilRenderDataBuilder();
        content.DrawRectangle(0, 0, 8, 8, White);
        batch.SetRenderData(Content, content);
        batch.SetVisualContent(Root, Content);
        var cacheContent = new NativeMilRenderDataBuilder();
        cacheContent.DrawRectangle(0, 0, 8, 8, White);
        cacheContent.DrawImage(new NativeMilRect(0, 0, 8, 8), Image);
        batch.SetRenderData(CacheContent, cacheContent);
        batch.SetVisualContent(CacheVisual, CacheContent);
        batch.CreateGenericTarget(Target, 16, 16);
        batch.SetTargetRoot(Target, Root);
        return batch;
    }

    private static byte[] SampleProgram()
    {
        // Owned ps_2_0 tokens: dcl t0.xy; dcl_2d s0; texld r0,t0,s0; mov oC0,r0; end.
        uint[] words = [0xffff0200, 0x0200001f, 0x80000000, 0xb0030000,
            0x0200001f, 0x90000000, 0xa00f0800, 0x03000042, 0x800f0000,
            0xb0e40000, 0xa0e40800, 0x02000001, 0x800f0800, 0x80e40000, 0x0000ffff];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), words[i]);
        return bytes;
    }

    private static NativeMilCompiledScene Compile(NativeMilChannel channel)
    {
        NativeMilCompiledScene scene = channel.CompileScene(Target, 0xD1E0, 1);
        Check(scene.Stream.Length != 0, "native compiler published an empty stream");
        return scene;
    }

    private static void CheckUnchangedScene(NativeMilChannel channel, NativeMilCompiledScene before)
    {
        NativeMilCompiledScene after = Compile(channel);
        Check(after.Metrics == before.Metrics && after.Stream.AsSpan().SequenceEqual(before.Stream),
            "unchanged or rejected candidate changed the complete native scene");
    }

    private static void Reject(Action action, NativeMilStatus expected)
    {
        try { action(); }
        catch (NativeMilException error) when (error.Status == expected) { return; }
        throw new InvalidOperationException($"Expected native {expected} for invalid DrawingImage source state.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
