using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Vector;

namespace ProGPU.Scene;

public unsafe partial class Compositor
{
    private readonly Dictionary<IncrementalScenePageLookup, IncrementalScenePage>
        _incrementalScenePages = new();
    private readonly Dictionary<Visual, List<IncrementalScenePageKey>>
        _incrementalScenePageKeysByVisual = new();
    private readonly Dictionary<Visual, long>
        _incrementalScenePageRejectedContentVersions = new();
    private readonly Dictionary<Visual, IncrementalScenePageBackoff>
        _incrementalScenePageVolatilityBackoffs = new();
    private ulong _incrementalScenePageAdmissionRetryFrame;
    private int _incrementalScenePageHits;
    private int _incrementalScenePageMisses;
    private int _incrementalScenePageCompilations;
    private int _incrementalScenePageReusedArrays;
    private long _incrementalScenePageBytes;
    private string? _incrementalScenePageRejectReason;
    private string? _incrementalScenePageMissReason;

    private readonly record struct IncrementalScenePageBoundary(
        int VectorVertexStart,
        int VectorIndexStart,
        int TextVertexStart,
        int TextStyleStart,
        int TextureVertexStart,
        int TextureIndexStart,
        int DrawCallStart,
        int SolidRoundedStart,
        int MaskRenderPassStart,
        int ClipStackCount,
        int OpacityStackCount,
        int BlendModeStackCount,
        Rect? ActiveClipRect,
        float ActiveOpacity,
        GpuBlendMode ActiveBlendMode);

    private readonly record struct IncrementalScenePageKey(
        long RenderContentVersion,
        IncrementalRenderPresentationState PresentationState,
        Matrix4x4 GlobalTransform,
        float ActiveOpacity,
        Rect? ActiveClipRect,
        GpuBlendMode ActiveBlendMode,
        uint LogicalWidth,
        uint LogicalHeight,
        uint? RenderTargetWidth,
        uint? RenderTargetHeight,
        RenderTargetViewport? RenderTargetViewport,
        float DpiScale,
        ulong GlyphAtlasGeneration,
        ulong PathAtlasGeneration,
        bool SolidRoundedSpecialization);

    private readonly record struct IncrementalScenePageLookup(
        Visual Visual,
        IncrementalScenePageKey Key);

    private readonly record struct IncrementalScenePageBackoff(
        long RenderContentVersion,
        ulong RetryFrame);

    private sealed class IncrementalScenePage
    {
        internal required VectorVertex[] VectorVertices { get; init; }
        internal required uint[] VectorIndices { get; init; }
        internal required GlyphInstance[] TextVertices { get; init; }
        internal required VectorVertex[] TextureVertices { get; init; }
        internal required uint[] TextureIndices { get; init; }
        internal required IncrementalScenePageDrawCall[] DrawCalls { get; init; }
        internal required GpuBrush[] Brushes { get; init; }
        internal required GpuTextStyle[] TextStyles { get; init; }
        internal required int LegacyTextVertexCount { get; init; }
        internal required int SolidRoundedPrimitiveCount { get; init; }
        internal required long ByteSize { get; init; }
        internal ulong LastUsedFrame { get; set; }
    }

    // Incremental pages admit only ordinary vector, text, and texture draws.
    // Keeping the complete public CompositorDrawCall in every retained page
    // would also retain chart, static-buffer, extension, and custom-data fields
    // which are rejected at the page boundary. This typed projection preserves
    // exactly the admitted render state and reconstructs the hot draw-call
    // value during replay.
    private readonly struct IncrementalScenePageDrawCall
    {
        internal IncrementalScenePageDrawCall(
            in CompositorDrawCall drawCall,
            uint indexBase)
        {
            Type = drawCall.Type;
            IsSolidRect = drawCall.IsSolidRect;
            IsSolidRounded = drawCall.IsSolidRounded;
            IndexStart = drawCall.IndexStart - indexBase;
            IndexCount = drawCall.IndexCount;
            Texture = drawCall.Texture;
            ClipRect = drawCall.ClipRect;
            BlendMode = drawCall.BlendMode;
            TextureSamplingMode = drawCall.TextureSamplingMode;
            TextureMaxAnisotropy = drawCall.TextureMaxAnisotropy;
            TextureAlphaMode = drawCall.TextureAlphaMode;
        }

        internal DrawCallType Type { get; }
        internal bool IsSolidRect { get; }
        internal bool IsSolidRounded { get; }
        internal uint IndexStart { get; }
        internal uint IndexCount { get; }
        internal GpuTexture? Texture { get; }
        internal Rect? ClipRect { get; }
        internal GpuBlendMode BlendMode { get; }
        internal TextureSamplingMode TextureSamplingMode { get; }
        internal byte TextureMaxAnisotropy { get; }
        internal GpuTextureAlphaMode TextureAlphaMode { get; }

        internal CompositorDrawCall Expand(uint indexBase)
        {
            return new CompositorDrawCall
            {
                Type = Type,
                IsSolidRect = IsSolidRect,
                IsSolidRounded = IsSolidRounded,
                IndexStart = IndexStart + indexBase,
                IndexCount = IndexCount,
                Texture = Texture,
                ClipRect = ClipRect,
                BlendMode = BlendMode,
                TextureSamplingMode = TextureSamplingMode,
                TextureMaxAnisotropy = TextureMaxAnisotropy,
                TextureAlphaMode = TextureAlphaMode
            };
        }
    }

    private void ResetIncrementalScenePageFrameMetrics()
    {
        ResetRetainedCompositionPictureFrameMetrics();
        _incrementalScenePageHits = 0;
        _incrementalScenePageMisses = 0;
        _incrementalScenePageCompilations = 0;
        _incrementalScenePageReusedArrays = 0;
        _incrementalScenePageRejectReason = null;
        _incrementalScenePageMissReason = null;
        ResetIncrementalSceneUploadFrameMetrics();
    }

    private IncrementalScenePageKey CreateIncrementalScenePageKey(
        Visual node,
        in Matrix4x4 globalTransform)
    {
        return new IncrementalScenePageKey(
            node.RenderContentVersion,
            node is IIncrementalRenderCommandCache incrementalCache
                ? incrementalCache.IncrementalPresentationState
                : default,
            globalTransform,
            _activeOpacity,
            _activeClipRect,
            _activeBlendMode,
            _currentWidth,
            _currentHeight,
            _explicitRenderTargetWidth,
            _explicitRenderTargetHeight,
            _explicitRenderTargetViewport,
            _currentDpiScale,
            _atlas.Generation,
            _pathAtlas.Generation,
            _previousSolidRoundedPrimitiveCount >=
                SolidRoundedSpecializationThreshold);
    }

    private bool CanUseIncrementalScenePage(Visual node)
    {
        if (!Options.EnableIncrementalScenePages)
            return RejectIncrementalScenePage("Disabled");
        if (_frameNumber < _incrementalScenePageAdmissionRetryFrame)
        {
            return RejectIncrementalScenePage(
                "Incremental page cache is cooling down");
        }
        if (Options.EnableGpuHitTesting)
            return RejectIncrementalScenePage("GPU hit testing active");
        if (ActiveCompilationContext != null)
            return RejectIncrementalScenePage("Static compilation active");
        if (node is not IIncrementalRenderCommandCache incrementalCache)
            return RejectIncrementalScenePage(
                "Commands are not incremental-page owned");
        if (!incrementalCache.CanCacheIncrementalPage)
            return RejectIncrementalScenePage(
                "Command producer is volatile");
        if (node is DrawingVisual)
            return RejectIncrementalScenePage("Mutable drawing visual");
        if (_incrementalScenePageRejectedContentVersions.TryGetValue(
                node,
                out long rejectedContentVersion) &&
            rejectedContentVersion == node.RenderContentVersion)
        {
            return RejectIncrementalScenePage(
                "Command stream is not page-compatible");
        }
        if (_incrementalScenePageVolatilityBackoffs.TryGetValue(
                node,
                out IncrementalScenePageBackoff backoff))
        {
            if (backoff.RenderContentVersion ==
                    node.RenderContentVersion &&
                _frameNumber < backoff.RetryFrame)
            {
                return RejectIncrementalScenePage(
                    "Composition state is volatile");
            }

            _incrementalScenePageVolatilityBackoffs.Remove(node);
        }
        if (_maskStack.Count != 0 ||
            _maskRenderPasses.Count != 0 ||
            _incrementalPagesBlockedByAnalyticMask)
            return RejectIncrementalScenePage("Mask scope active");
        if (_activeBlendMode != GpuBlendMode.SrcOver)
            return RejectIncrementalScenePage("Blend scope active");
        if (_useGpuTransformsActive)
            return RejectIncrementalScenePage("GPU transform scope active");
        return true;
    }

    private bool RejectIncrementalScenePage(string reason)
    {
        _incrementalScenePageRejectReason ??= reason;
        return false;
    }

    private bool TryReplayIncrementalScenePage(
        Visual node,
        in Matrix4x4 globalTransform)
    {
        if (!CanUseIncrementalScenePage(node))
        {
            return false;
        }

        var key = CreateIncrementalScenePageKey(node, globalTransform);
        var lookup = new IncrementalScenePageLookup(node, key);
        if (!_incrementalScenePages.TryGetValue(lookup, out var page) ||
            !AreIncrementalPageTexturesValid(page))
        {
            if (ShouldBackOffVolatileIncrementalScenePage(node, key))
            {
                RemoveIncrementalScenePages(node);
                _incrementalScenePageVolatilityBackoffs[node] =
                    new IncrementalScenePageBackoff(
                        node.RenderContentVersion,
                        _frameNumber + (ulong)Options
                            .IncrementalScenePageVolatilityCooldownFrames);
                _incrementalScenePageRejectReason =
                    "Composition state is volatile";
                return false;
            }

            _incrementalScenePageMissReason ??=
                DescribeIncrementalScenePageMiss(node, key);
            _incrementalScenePageMisses++;
            return false;
        }

        CommitPendingDrawCalls();
        AppendIncrementalScenePage(page);
        _pathAtlas.MarkRetainedPathReplay();
        page.LastUsedFrame = _frameNumber;
        _incrementalScenePageHits++;
        return true;
    }

    private bool ShouldBackOffVolatileIncrementalScenePage(
        Visual node,
        in IncrementalScenePageKey key)
    {
        if (!_incrementalScenePageKeysByVisual.TryGetValue(
                node,
                out List<IncrementalScenePageKey>? keys))
        {
            return false;
        }

        int placementVariants = 0;
        foreach (IncrementalScenePageKey cached in keys)
        {
            if (!HasSameIncrementalPageContentAndTarget(cached, key))
                continue;

            placementVariants++;
            if (placementVariants >=
                Options.MaximumIncrementalScenePageVariantsPerVisual)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSameIncrementalPageContentAndTarget(
        in IncrementalScenePageKey left,
        in IncrementalScenePageKey right)
    {
        return left.RenderContentVersion == right.RenderContentVersion &&
            left.PresentationState == right.PresentationState &&
            left.LogicalWidth == right.LogicalWidth &&
            left.LogicalHeight == right.LogicalHeight &&
            left.RenderTargetWidth == right.RenderTargetWidth &&
            left.RenderTargetHeight == right.RenderTargetHeight &&
            left.RenderTargetViewport == right.RenderTargetViewport &&
            left.DpiScale == right.DpiScale &&
            left.GlyphAtlasGeneration == right.GlyphAtlasGeneration &&
            left.PathAtlasGeneration == right.PathAtlasGeneration &&
            left.SolidRoundedSpecialization ==
                right.SolidRoundedSpecialization;
    }

    private string DescribeIncrementalScenePageMiss(
        Visual node,
        in IncrementalScenePageKey key)
    {
        if (!_incrementalScenePageKeysByVisual.TryGetValue(
                node,
                out List<IncrementalScenePageKey>? keys))
        {
            return "First page for visual";
        }

        foreach (IncrementalScenePageKey cached in keys)
        {
            if (cached.RenderContentVersion != key.RenderContentVersion)
                return "Render content changed";
            if (cached.PresentationState != key.PresentationState)
                return "Presentation state changed";
            if (cached.GlobalTransform != key.GlobalTransform)
                return "Global transform changed";
            if (cached.ActiveOpacity != key.ActiveOpacity)
                return "Effective opacity changed";
            if (cached.ActiveClipRect != key.ActiveClipRect)
                return "Effective clip changed";
            if (cached.ActiveBlendMode != key.ActiveBlendMode)
                return "Effective blend changed";
            if (cached.LogicalWidth != key.LogicalWidth ||
                cached.LogicalHeight != key.LogicalHeight)
                return "Logical target changed";
            if (cached.RenderTargetWidth != key.RenderTargetWidth ||
                cached.RenderTargetHeight != key.RenderTargetHeight ||
                cached.RenderTargetViewport != key.RenderTargetViewport)
                return "Physical target changed";
            if (cached.DpiScale != key.DpiScale)
                return "DPI changed";
            if (cached.GlyphAtlasGeneration != key.GlyphAtlasGeneration)
                return "Glyph atlas changed";
            if (cached.PathAtlasGeneration != key.PathAtlasGeneration)
                return "Path atlas changed";
            if (cached.SolidRoundedSpecialization !=
                key.SolidRoundedSpecialization)
                return "Rounded specialization changed";
            return _incrementalScenePages.TryGetValue(
                       new IncrementalScenePageLookup(node, cached),
                       out IncrementalScenePage? page) &&
                   AreIncrementalPageTexturesValid(page)
                ? "Equivalent page lookup missed"
                : "Texture became invalid";
        }

        return "First page for visual";
    }

    private bool TryBeginIncrementalScenePage(
        Visual node,
        IReadOnlyList<RenderCommand> commands,
        IOwnedRenderCommandCache? ownedRenderCommandCache,
        out IncrementalScenePageBoundary boundary)
    {
        boundary = default;
        if (!CanUseIncrementalScenePage(node))
        {
            return false;
        }

        int commandCount = ownedRenderCommandCache?.RenderCommandCount ??
            commands.Count;
        for (int index = 0; index < commandCount; index++)
        {
            RenderCommand command = ownedRenderCommandCache is null
                ? commands[index]
                : ownedRenderCommandCache.GetRenderCommand(index);
            if (command.UseGpuTransforms ||
                !RenderCommand.IsIncrementalScenePageCommandSupported(
                    command.Type))
            {
                _incrementalScenePageRejectReason ??=
                    command.UseGpuTransforms
                        ? "Command uses GPU transforms"
                        : $"Unsupported command: {command.Type}";
                _incrementalScenePageRejectedContentVersions[node] =
                    node.RenderContentVersion;
                return false;
            }
        }

        _incrementalScenePageRejectedContentVersions.Remove(node);
        CommitPendingDrawCalls();
        boundary = new IncrementalScenePageBoundary(
            _vectorVerticesList.Count,
            _vectorIndicesList.Count,
            _textVerticesList.Count,
            _activeTextStyles.Count,
            _textureVerticesList.Count,
            _textureIndicesList.Count,
            _drawCalls.Count,
            _currentSolidRoundedPrimitiveCount,
            _maskRenderPasses.Count,
            _clipStack.Count,
            _opacityStack.Count,
            _blendModeStack.Count,
            _activeClipRect,
            _activeOpacity,
            _activeBlendMode);
        return true;
    }

    private void CompleteIncrementalScenePage(
        Visual node,
        in Matrix4x4 globalTransform,
        in IncrementalScenePageBoundary boundary)
    {
        CommitPendingDrawCalls();
        if (_maskRenderPasses.Count != boundary.MaskRenderPassStart)
        {
            _incrementalScenePageRejectReason ??=
                "Page created mask render passes";
            MergeIncrementalDrawCallsFrom(boundary.DrawCallStart);
            return;
        }
        if (_clipStack.Count != boundary.ClipStackCount ||
            _opacityStack.Count != boundary.OpacityStackCount ||
            _blendModeStack.Count != boundary.BlendModeStackCount ||
            _activeClipRect != boundary.ActiveClipRect ||
            _activeOpacity != boundary.ActiveOpacity ||
            _activeBlendMode != boundary.ActiveBlendMode)
        {
            _incrementalScenePageRejectReason ??=
                "Unbalanced page composition scope";
            MergeIncrementalDrawCallsFrom(boundary.DrawCallStart);
            return;
        }

        IncrementalScenePageKey key =
            CreateIncrementalScenePageKey(node, globalTransform);
        var lookup = new IncrementalScenePageLookup(node, key);
        if (!_incrementalScenePages.ContainsKey(lookup) &&
            _incrementalScenePages.Count >=
                Options.MaximumIncrementalScenePages)
        {
            ClearIncrementalScenePages();
            _incrementalScenePageAdmissionRetryFrame =
                _frameNumber + (ulong)Options
                    .IncrementalScenePageVolatilityCooldownFrames;
            _incrementalScenePageRejectReason =
                "Incremental page cache is saturated";
            MergeIncrementalDrawCallsFrom(boundary.DrawCallStart);
            return;
        }

        IncrementalScenePage? reusablePage =
            TakeReusableIncrementalScenePage(node, key);
        IncrementalScenePage? page = CaptureIncrementalScenePage(
            boundary,
            reusablePage);
        MergeIncrementalDrawCallsFrom(boundary.DrawCallStart);
        if (page == null)
        {
            return;
        }

        RemoveObsoleteIncrementalScenePageRevisions(
            node,
            key.RenderContentVersion);
        if (_incrementalScenePages.TryGetValue(lookup, out var previous))
        {
            _incrementalScenePageBytes -= previous.ByteSize;
        }
        else if (_incrementalScenePages.Count >=
                 Options.MaximumIncrementalScenePages)
        {
            EvictOldestIncrementalScenePage();
        }

        page.LastUsedFrame = _frameNumber;
        _incrementalScenePages[lookup] = page;
        if (!_incrementalScenePageKeysByVisual.TryGetValue(
                node,
                out List<IncrementalScenePageKey>? keys))
        {
            // The default policy admits at most two placement variants. A
            // compact linear list avoids a HashSet object, bucket array, and
            // oversized entry array per visual; lookup remains bounded O(K)
            // for the configured K variants and the later algorithms already
            // scan the same keys.
            keys = new List<IncrementalScenePageKey>(
                Math.Min(
                    Options.MaximumIncrementalScenePageVariantsPerVisual,
                    2));
            _incrementalScenePageKeysByVisual.Add(node, keys);
        }
        if (!keys.Contains(key))
        {
            keys.Add(key);
        }
        _incrementalScenePageBytes += page.ByteSize;
        _incrementalScenePageCompilations++;
    }

    private IncrementalScenePage? TakeReusableIncrementalScenePage(
        Visual node,
        in IncrementalScenePageKey currentKey)
    {
        var currentLookup = new IncrementalScenePageLookup(node, currentKey);
        if (_incrementalScenePages.TryGetValue(
                currentLookup,
                out IncrementalScenePage? exactPage))
        {
            RemoveIncrementalScenePageForReuse(
                node,
                currentKey,
                currentLookup,
                exactPage);
            return exactPage;
        }

        if (!_incrementalScenePageKeysByVisual.TryGetValue(
                node,
                out List<IncrementalScenePageKey>? keys) ||
            !TryFindObsoleteIncrementalScenePageKey(
                keys,
                currentKey.RenderContentVersion,
                out IncrementalScenePageKey obsoleteKey))
        {
            return null;
        }

        var obsoleteLookup =
            new IncrementalScenePageLookup(node, obsoleteKey);
        if (!_incrementalScenePages.TryGetValue(
                obsoleteLookup,
                out IncrementalScenePage? obsoletePage))
        {
            keys.Remove(obsoleteKey);
            return null;
        }

        RemoveIncrementalScenePageForReuse(
            node,
            obsoleteKey,
            obsoleteLookup,
            obsoletePage);
        return obsoletePage;
    }

    private void RemoveIncrementalScenePageForReuse(
        Visual node,
        in IncrementalScenePageKey key,
        in IncrementalScenePageLookup lookup,
        IncrementalScenePage page)
    {
        _incrementalScenePages.Remove(lookup);
        _incrementalScenePageBytes -= page.ByteSize;
        if (_incrementalScenePageKeysByVisual.TryGetValue(
                node,
                out List<IncrementalScenePageKey>? keys))
        {
            keys.Remove(key);
        }
    }

    private void RemoveObsoleteIncrementalScenePageRevisions(
        Visual node,
        long currentContentVersion)
    {
        if (!_incrementalScenePageKeysByVisual.TryGetValue(
                node,
                out List<IncrementalScenePageKey>? keys))
        {
            return;
        }

        while (TryFindObsoleteIncrementalScenePageKey(
                   keys,
                   currentContentVersion,
                   out IncrementalScenePageKey obsoleteKey))
        {
            var obsoleteLookup =
                new IncrementalScenePageLookup(node, obsoleteKey);
            if (_incrementalScenePages.Remove(
                    obsoleteLookup,
                    out IncrementalScenePage? obsoletePage))
            {
                _incrementalScenePageBytes -= obsoletePage.ByteSize;
            }
            keys.Remove(obsoleteKey);
        }
    }

    private static bool TryFindObsoleteIncrementalScenePageKey(
        List<IncrementalScenePageKey> keys,
        long currentContentVersion,
        out IncrementalScenePageKey obsoleteKey)
    {
        foreach (IncrementalScenePageKey key in keys)
        {
            if (key.RenderContentVersion != currentContentVersion)
            {
                obsoleteKey = key;
                return true;
            }
        }

        obsoleteKey = default;
        return false;
    }

    private IncrementalScenePage? CaptureIncrementalScenePage(
        in IncrementalScenePageBoundary boundary,
        IncrementalScenePage? reusablePage)
    {
        int vectorVertexCount =
            _vectorVerticesList.Count - boundary.VectorVertexStart;
        int vectorIndexCount =
            _vectorIndicesList.Count - boundary.VectorIndexStart;
        int textVertexCount =
            _textVerticesList.Count - boundary.TextVertexStart;
        int textStyleCount =
            _activeTextStyles.Count - boundary.TextStyleStart;
        int textureVertexCount =
            _textureVerticesList.Count - boundary.TextureVertexStart;
        int textureIndexCount =
            _textureIndicesList.Count - boundary.TextureIndexStart;
        int drawCallCount = _drawCalls.Count - boundary.DrawCallStart;

        for (int index = boundary.DrawCallStart;
             index < _drawCalls.Count;
             index++)
        {
            CompositorDrawCall drawCall = _drawCalls[index];
            if (drawCall.Type is not (
                    DrawCallType.Vector or
                    DrawCallType.Text or
                    DrawCallType.Texture) ||
                drawCall.MaskTexture != null ||
                drawCall.MaskBindGroupOverride != 0)
            {
                _incrementalScenePageRejectReason ??=
                    "Unsupported compiled draw call";
                return null;
            }
        }

        var vectorVertices = CopyIncrementalScenePageArray(
            CollectionsMarshal.AsSpan(_vectorVerticesList)
                .Slice(boundary.VectorVertexStart, vectorVertexCount),
            reusablePage?.VectorVertices);
        var vectorIndices = CopyIncrementalScenePageArray(
            CollectionsMarshal.AsSpan(_vectorIndicesList)
                .Slice(boundary.VectorIndexStart, vectorIndexCount),
            reusablePage?.VectorIndices);
        var textVertices = CopyIncrementalScenePageArray(
            CollectionsMarshal.AsSpan(_textVerticesList)
                .Slice(boundary.TextVertexStart, textVertexCount),
            reusablePage?.TextVertices);
        var textureVertices = CopyIncrementalScenePageArray(
            CollectionsMarshal.AsSpan(_textureVerticesList)
                .Slice(boundary.TextureVertexStart, textureVertexCount),
            reusablePage?.TextureVertices);
        var textureIndices = CopyIncrementalScenePageArray(
            CollectionsMarshal.AsSpan(_textureIndicesList)
                .Slice(boundary.TextureIndexStart, textureIndexCount),
            reusablePage?.TextureIndices);
        var drawCalls = CopyIncrementalScenePageDrawCalls(
            CollectionsMarshal.AsSpan(_drawCalls)
                .Slice(boundary.DrawCallStart, drawCallCount),
            boundary,
            reusablePage?.DrawCalls);
        var textStyles = CopyIncrementalScenePageArray(
            CollectionsMarshal.AsSpan(_activeTextStyles)
                .Slice(boundary.TextStyleStart, textStyleCount),
            reusablePage?.TextStyles);

        for (int index = 0; index < vectorIndices.Length; index++)
        {
            vectorIndices[index] -= (uint)boundary.VectorVertexStart;
        }
        for (int index = 0; index < textureIndices.Length; index++)
        {
            textureIndices[index] -= (uint)boundary.TextureVertexStart;
        }
        if (!TryNormalizeIncrementalPageBrushes(
                vectorVertices,
                reusablePage?.Brushes,
                out GpuBrush[] brushes))
        {
            _incrementalScenePageRejectReason ??=
                "Non-solid page brush";
            return null;
        }
        if (!TryNormalizeIncrementalPageTextStyles(
                textVertices,
                boundary.TextStyleStart,
                textStyles.Length))
        {
            _incrementalScenePageRejectReason ??=
                "Text style is outside page ownership";
            return null;
        }

        long byteSize =
            (long)vectorVertices.Length * Marshal.SizeOf<VectorVertex>() +
            (long)vectorIndices.Length * sizeof(uint) +
            (long)textVertices.Length * Marshal.SizeOf<GlyphInstance>() +
            (long)textureVertices.Length * Marshal.SizeOf<VectorVertex>() +
            (long)textureIndices.Length * sizeof(uint) +
            (long)drawCalls.Length *
                Unsafe.SizeOf<IncrementalScenePageDrawCall>() +
            (long)brushes.Length * Marshal.SizeOf<GpuBrush>() +
            (long)textStyles.Length * Marshal.SizeOf<GpuTextStyle>();

        return new IncrementalScenePage
        {
            VectorVertices = vectorVertices,
            VectorIndices = vectorIndices,
            TextVertices = textVertices,
            TextureVertices = textureVertices,
            TextureIndices = textureIndices,
            DrawCalls = drawCalls,
            Brushes = brushes,
            TextStyles = textStyles,
            LegacyTextVertexCount =
                CountLegacyTextVertices(textVertices),
            SolidRoundedPrimitiveCount =
                _currentSolidRoundedPrimitiveCount -
                boundary.SolidRoundedStart,
            ByteSize = byteSize
        };
    }

    private T[] CopyIncrementalScenePageArray<T>(
        ReadOnlySpan<T> source,
        T[]? reusable)
    {
        if (reusable != null && reusable.Length == source.Length)
        {
            source.CopyTo(reusable);
            if (reusable.Length != 0)
            {
                _incrementalScenePageReusedArrays++;
            }
            return reusable;
        }

        return source.ToArray();
    }

    private IncrementalScenePageDrawCall[] CopyIncrementalScenePageDrawCalls(
        ReadOnlySpan<CompositorDrawCall> source,
        in IncrementalScenePageBoundary boundary,
        IncrementalScenePageDrawCall[]? reusable)
    {
        IncrementalScenePageDrawCall[] result;
        if (reusable != null && reusable.Length == source.Length)
        {
            result = reusable;
            if (result.Length != 0)
            {
                _incrementalScenePageReusedArrays++;
            }
        }
        else
        {
            result = new IncrementalScenePageDrawCall[source.Length];
        }

        for (int index = 0; index < source.Length; index++)
        {
            CompositorDrawCall drawCall = source[index];
            uint indexBase = drawCall.Type switch
            {
                DrawCallType.Vector => (uint)boundary.VectorIndexStart,
                DrawCallType.Text => (uint)boundary.TextVertexStart,
                DrawCallType.Texture => (uint)boundary.TextureIndexStart,
                _ => 0u
            };
            result[index] = new IncrementalScenePageDrawCall(
                drawCall,
                indexBase);
        }

        return result;
    }

    private bool TryNormalizeIncrementalPageBrushes(
        Span<VectorVertex> vectorVertices,
        GpuBrush[]? reusableBrushes,
        out GpuBrush[] brushes)
    {
        Span<int> globalBrushIndices = stackalloc int[MaxBrushes];
        int brushCount = 0;

        for (int index = 0; index < vectorVertices.Length; index++)
        {
            if (!TryCollectIncrementalPageBrush(
                    vectorVertices[index].BrushIndex,
                    globalBrushIndices,
                    ref brushCount))
            {
                brushes = Array.Empty<GpuBrush>();
                return false;
            }
        }
        if (reusableBrushes != null &&
            reusableBrushes.Length == brushCount)
        {
            brushes = reusableBrushes;
            if (brushes.Length != 0)
            {
                _incrementalScenePageReusedArrays++;
            }
        }
        else
        {
            brushes = new GpuBrush[brushCount];
        }
        for (int index = 0; index < brushCount; index++)
        {
            brushes[index] = _activeBrushes[globalBrushIndices[index]];
        }

        for (int index = 0; index < vectorVertices.Length; index++)
        {
            VectorVertex vertex = vectorVertices[index];
            vertex.BrushIndex = NormalizeIncrementalPageBrushIndex(
                vertex.BrushIndex,
                globalBrushIndices[..brushCount]);
            vectorVertices[index] = vertex;
        }
        return true;
    }

    private static bool TryNormalizeIncrementalPageTextStyles(
        Span<GlyphInstance> textVertices,
        int globalStyleStart,
        int styleCount)
    {
        for (int index = 0; index < textVertices.Length; index++)
        {
            GlyphInstance vertex = textVertices[index];
            if (vertex.BrushIndex < 0f)
            {
                continue;
            }

            int globalIndex = (int)MathF.Round(vertex.BrushIndex);
            int localIndex = globalIndex - globalStyleStart;
            if ((uint)localIndex >= (uint)styleCount)
            {
                return false;
            }

            vertex.BrushIndex = localIndex;
            textVertices[index] = vertex;
        }

        return true;
    }

    private static int CountLegacyTextVertices(
        ReadOnlySpan<GlyphInstance> textVertices)
    {
        int count = 0;
        for (int index = 0; index < textVertices.Length; index++)
        {
            if (textVertices[index].BrushIndex < 0f)
            {
                count++;
            }
        }
        return count;
    }

    private bool TryCollectIncrementalPageBrush(
        float brushIndex,
        Span<int> globalBrushIndices,
        ref int brushCount)
    {
        int globalIndex = (int)MathF.Round(brushIndex);
        if ((uint)globalIndex >= (uint)_activeBrushes.Count)
        {
            return true;
        }

        if (_activeBrushes[globalIndex].Type != 0)
        {
            return false;
        }

        for (int index = 0; index < brushCount; index++)
        {
            if (globalBrushIndices[index] == globalIndex)
            {
                return true;
            }
        }

        if (brushCount >= globalBrushIndices.Length)
        {
            return false;
        }

        globalBrushIndices[brushCount++] = globalIndex;
        return true;
    }

    private static float NormalizeIncrementalPageBrushIndex(
        float brushIndex,
        ReadOnlySpan<int> globalBrushIndices)
    {
        int globalIndex = (int)MathF.Round(brushIndex);
        for (int index = 0; index < globalBrushIndices.Length; index++)
        {
            if (globalBrushIndices[index] == globalIndex)
            {
                return index;
            }
        }

        return 0f;
    }

    private void AppendIncrementalScenePage(IncrementalScenePage page)
    {
        Span<float> brushMap = page.Brushes.Length <= MaxBrushes
            ? stackalloc float[page.Brushes.Length]
            : new float[page.Brushes.Length];
        for (int index = 0; index < page.Brushes.Length; index++)
        {
            brushMap[index] = RegisterIncrementalSolidBrush(
                page.Brushes[index]);
        }

        int vectorVertexStart = _vectorVerticesList.Count;
        _vectorVerticesList.EnsureCapacity(
            vectorVertexStart + page.VectorVertices.Length);
        if (page.Brushes.Length == 0)
        {
            // Specialized solid primitives carry their color inline and have no
            // brush-table indices to translate. This is the common retained UI
            // page path, so append it as one contiguous copy instead of visiting
            // every vertex merely to prove that the empty brush map has no entry.
            _vectorVerticesList.AddRange(page.VectorVertices);
        }
        else
        {
            for (int index = 0; index < page.VectorVertices.Length; index++)
            {
                VectorVertex vertex = page.VectorVertices[index];
                int localBrushIndex = (int)MathF.Round(vertex.BrushIndex);
                if ((uint)localBrushIndex < (uint)brushMap.Length)
                {
                    vertex.BrushIndex = brushMap[localBrushIndex];
                }
                _vectorVerticesList.Add(vertex);
            }
        }

        int vectorIndexStart = _vectorIndicesList.Count;
        _vectorIndicesList.EnsureCapacity(
            vectorIndexStart + page.VectorIndices.Length);
        CollectionsMarshal.SetCount(
            _vectorIndicesList,
            vectorIndexStart + page.VectorIndices.Length);
        Span<uint> appendedVectorIndices =
            CollectionsMarshal.AsSpan(_vectorIndicesList)
                .Slice(vectorIndexStart, page.VectorIndices.Length);
        for (int index = 0; index < page.VectorIndices.Length; index++)
        {
            appendedVectorIndices[index] =
                page.VectorIndices[index] + (uint)vectorVertexStart;
        }

        int textVertexStart = _textVerticesList.Count;
        int textStyleStart = _activeTextStyles.Count;
        _activeTextStyles.AddRange(page.TextStyles);
        _legacyTextVertexCount += page.LegacyTextVertexCount;
        _textVerticesList.EnsureCapacity(
            textVertexStart + page.TextVertices.Length);
        for (int index = 0; index < page.TextVertices.Length; index++)
        {
            GlyphInstance vertex = page.TextVertices[index];
            if (vertex.BrushIndex >= 0f)
            {
                vertex.BrushIndex += textStyleStart;
            }
            _textVerticesList.Add(vertex);
        }

        int textureVertexStart = _textureVerticesList.Count;
        _textureVerticesList.AddRange(page.TextureVertices);
        int textureIndexStart = _textureIndicesList.Count;
        _textureIndicesList.EnsureCapacity(
            textureIndexStart + page.TextureIndices.Length);
        CollectionsMarshal.SetCount(
            _textureIndicesList,
            textureIndexStart + page.TextureIndices.Length);
        Span<uint> appendedTextureIndices =
            CollectionsMarshal.AsSpan(_textureIndicesList)
                .Slice(textureIndexStart, page.TextureIndices.Length);
        for (int index = 0; index < page.TextureIndices.Length; index++)
        {
            appendedTextureIndices[index] =
                page.TextureIndices[index] + (uint)textureVertexStart;
        }

        for (int index = 0; index < page.DrawCalls.Length; index++)
        {
            IncrementalScenePageDrawCall retainedDrawCall =
                page.DrawCalls[index];
            uint indexBase = retainedDrawCall.Type switch
            {
                DrawCallType.Vector => (uint)vectorIndexStart,
                DrawCallType.Text => (uint)textVertexStart,
                DrawCallType.Texture => (uint)textureIndexStart,
                _ => 0u
            };
            CompositorDrawCall drawCall =
                retainedDrawCall.Expand(indexBase);
            AppendOrMergeIncrementalDrawCall(drawCall);
        }

        _currentSolidRoundedPrimitiveCount +=
            page.SolidRoundedPrimitiveCount;
        _pendingVectorStart = (uint)_vectorIndicesList.Count;
        _pendingTextStart = (uint)_textVerticesList.Count;
        _currentBatchType = BatchType.None;
    }

    private void MergeIncrementalDrawCallsFrom(int start)
    {
        if (start <= 0 || start >= _drawCalls.Count)
        {
            return;
        }

        int write = start;
        for (int read = start; read < _drawCalls.Count; read++)
        {
            CompositorDrawCall current = _drawCalls[read];
            if (write > 0 &&
                TryMergeIncrementalDrawCall(write - 1, current))
            {
                continue;
            }

            _drawCalls[write++] = current;
        }

        if (write < _drawCalls.Count)
        {
            _drawCalls.RemoveRange(write, _drawCalls.Count - write);
        }
    }

    private void AppendOrMergeIncrementalDrawCall(
        in CompositorDrawCall drawCall)
    {
        if (_drawCalls.Count == 0 ||
            !TryMergeIncrementalDrawCall(
                _drawCalls.Count - 1,
                drawCall))
        {
            _drawCalls.Add(drawCall);
        }
    }

    private bool TryMergeIncrementalDrawCall(
        int previousIndex,
        in CompositorDrawCall current)
    {
        CompositorDrawCall previous = _drawCalls[previousIndex];
        if (previous.Type != current.Type ||
            previous.Type is not (DrawCallType.Vector or DrawCallType.Text) ||
            previous.IndexStart + previous.IndexCount != current.IndexStart ||
            previous.IsSolidRect != current.IsSolidRect ||
            previous.IsSolidRounded != current.IsSolidRounded ||
            previous.ClipRect != current.ClipRect ||
            !ReferenceEquals(previous.MaskTexture, current.MaskTexture) ||
            previous.BlendMode != current.BlendMode)
        {
            return false;
        }

        previous.IndexCount += current.IndexCount;
        _drawCalls[previousIndex] = previous;
        return true;
    }

    private float RegisterIncrementalSolidBrush(in GpuBrush brush)
    {
        for (int index = 0; index < _activeBrushes.Count; index++)
        {
            if (BrushesEqual(_activeBrushes[index], brush))
            {
                return index;
            }
        }

        if (_activeBrushes.Count < MaxBrushes)
        {
            _activeBrushes.Add(brush);
            return _activeBrushes.Count - 1;
        }

        return 0f;
    }

    private bool AreIncrementalPageTexturesValid(IncrementalScenePage page)
    {
        for (int index = 0; index < page.DrawCalls.Length; index++)
        {
            IncrementalScenePageDrawCall drawCall = page.DrawCalls[index];
            if (drawCall.Type == DrawCallType.Texture &&
                !IsTextureBindable(drawCall.Texture))
            {
                return false;
            }
        }

        return true;
    }

    private void EvictOldestIncrementalScenePage()
    {
        IncrementalScenePageLookup? oldestLookup = null;
        IncrementalScenePage? oldestPage = null;
        foreach (var entry in _incrementalScenePages)
        {
            if (oldestPage == null ||
                entry.Value.LastUsedFrame < oldestPage.LastUsedFrame)
            {
                oldestLookup = entry.Key;
                oldestPage = entry.Value;
            }
        }

        if (oldestLookup.HasValue &&
            _incrementalScenePages.Remove(
                oldestLookup.Value,
                out var removed))
        {
            _incrementalScenePageBytes -= removed.ByteSize;
            RemoveIncrementalScenePageLookup(oldestLookup.Value);
        }
    }

    private void RemoveIncrementalScenePageLookup(
        in IncrementalScenePageLookup lookup)
    {
        if (!_incrementalScenePageKeysByVisual.TryGetValue(
                lookup.Visual,
                out List<IncrementalScenePageKey>? keys))
        {
            return;
        }

        keys.Remove(lookup.Key);
        if (keys.Count == 0)
        {
            _incrementalScenePageKeysByVisual.Remove(lookup.Visual);
        }
    }

    private void RemoveIncrementalScenePages(Visual visual)
    {
        if (!_incrementalScenePageKeysByVisual.TryGetValue(
                visual,
                out List<IncrementalScenePageKey>? keys))
        {
            return;
        }

        foreach (IncrementalScenePageKey key in keys)
        {
            var lookup = new IncrementalScenePageLookup(visual, key);
            if (_incrementalScenePages.Remove(
                    lookup,
                    out IncrementalScenePage? removed))
            {
                _incrementalScenePageBytes -= removed.ByteSize;
            }
        }

        keys.Clear();
        _incrementalScenePageKeysByVisual.Remove(visual);
    }

    private void ClearIncrementalScenePages()
    {
        _incrementalScenePages.Clear();
        _incrementalScenePageKeysByVisual.Clear();
        _incrementalScenePageBytes = 0;
    }
}
