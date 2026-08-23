using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Backend;
using ProGPU.Vector;

namespace ProGPU.Scene;

public unsafe partial class Compositor
{
    private readonly ConditionalWeakTable<GpuPicture, RetainedPictureObservation>
        _retainedPictureObservations = new();
    private readonly Dictionary<RetainedPicturePageLookup, IncrementalScenePage>
        _retainedCompositionPictures = new();
    private readonly Dictionary<GpuPicture, List<RetainedPicturePageKey>>
        _retainedCompositionPictureKeys = new();
    private long _retainedCompositionPictureHits;
    private long _retainedCompositionPictureMisses;
    private long _retainedCompositionPictureCompilations;

    private readonly record struct RetainedPicturePageKey(
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

    private readonly record struct RetainedPicturePageLookup(
        GpuPicture Picture,
        RetainedPicturePageKey Key);

    private sealed class RetainedPictureObservation
    {
        internal int Count;
    }

    private RetainedPicturePageKey CreateRetainedPicturePageKey(
        in Matrix4x4 globalTransform)
    {
        return new RetainedPicturePageKey(
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

    private void ResetRetainedCompositionPictureFrameMetrics()
    {
        _retainedCompositionPictureHits = 0;
        _retainedCompositionPictureMisses = 0;
        _retainedCompositionPictureCompilations = 0;
        SweepRetainedCompositionPictures();
    }

    private bool CanUseRetainedCompositionPicture(GpuPicture picture)
    {
        if (!Options.EnableRetainedCompositionPictures ||
            !Options.EnableIncrementalScenePages ||
            Options.EnableGpuHitTesting ||
            ActiveCompilationContext != null ||
            _maskStack.Count != 0 ||
            _maskRenderPasses.Count != 0 ||
            _incrementalPagesBlockedByAnalyticMask ||
            _activeBlendMode != GpuBlendMode.SrcOver ||
            _useGpuTransformsActive)
        {
            return false;
        }

        for (int index = 0; index < picture.CommandCount; index++)
        {
            RenderCommand command = picture.GetCommand(index);
            if (command.UseGpuTransforms ||
                !IsIncrementalScenePageCommandSupported(command.Type) ||
                command.Type == RenderCommandType.DrawVisual)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryReplayRetainedCompositionPicture(
        GpuPicture picture,
        in Matrix4x4 globalTransform)
    {
        if (!CanUseRetainedCompositionPicture(picture))
        {
            return false;
        }

        RetainedPictureObservation observation =
            _retainedPictureObservations.GetOrCreateValue(picture);
        if (observation.Count < 2)
        {
            observation.Count++;
        }

        var key = CreateRetainedPicturePageKey(globalTransform);
        var lookup = new RetainedPicturePageLookup(picture, key);
        if (!_retainedCompositionPictures.TryGetValue(
                lookup,
                out IncrementalScenePage? page) ||
            !AreIncrementalPageTexturesValid(page))
        {
            if (page != null)
            {
                RemoveRetainedCompositionPicture(lookup);
            }
            _retainedCompositionPictureMisses++;
            return false;
        }

        CommitPendingDrawCalls();
        AppendIncrementalScenePage(page);
        _pathAtlas.MarkRetainedPathReplay();
        page.LastUsedFrame = _frameNumber;
        _retainedCompositionPictureHits++;
        return true;
    }

    private bool TryBeginRetainedCompositionPicture(
        GpuPicture picture,
        out IncrementalScenePageBoundary boundary)
    {
        boundary = default;
        if (!CanUseRetainedCompositionPicture(picture) ||
            !_retainedPictureObservations.TryGetValue(
                picture,
                out RetainedPictureObservation? observation) ||
            observation.Count < 2)
        {
            return false;
        }

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

    private void CompleteRetainedCompositionPicture(
        GpuPicture picture,
        in Matrix4x4 globalTransform,
        in IncrementalScenePageBoundary boundary)
    {
        CommitPendingDrawCalls();
        if (_maskRenderPasses.Count != boundary.MaskRenderPassStart ||
            _clipStack.Count != boundary.ClipStackCount ||
            _opacityStack.Count != boundary.OpacityStackCount ||
            _blendModeStack.Count != boundary.BlendModeStackCount ||
            _activeClipRect != boundary.ActiveClipRect ||
            _activeOpacity != boundary.ActiveOpacity ||
            _activeBlendMode != boundary.ActiveBlendMode)
        {
            MergeIncrementalDrawCallsFrom(boundary.DrawCallStart);
            return;
        }

        IncrementalScenePage? page = CaptureIncrementalScenePage(
            boundary,
            reusablePage: null);
        MergeIncrementalDrawCallsFrom(boundary.DrawCallStart);
        if (page == null)
        {
            return;
        }

        var key = CreateRetainedPicturePageKey(globalTransform);
        var lookup = new RetainedPicturePageLookup(picture, key);
        if (_retainedCompositionPictures.ContainsKey(lookup))
        {
            return;
        }

        while (_retainedCompositionPictures.Count >=
               Options.MaximumRetainedCompositionPictures)
        {
            EvictOldestRetainedCompositionPicture();
        }
        TrimRetainedCompositionPictureVariants(picture);

        page.LastUsedFrame = _frameNumber;
        _retainedCompositionPictures.Add(lookup, page);
        if (!_retainedCompositionPictureKeys.TryGetValue(
                picture,
                out List<RetainedPicturePageKey>? keys))
        {
            keys = new List<RetainedPicturePageKey>(
                Math.Min(
                    Options.MaximumIncrementalScenePageVariantsPerVisual,
                    2));
            _retainedCompositionPictureKeys.Add(picture, keys);
        }
        keys.Add(key);
        _retainedCompositionPictureCompilations++;
    }

    private void TrimRetainedCompositionPictureVariants(GpuPicture picture)
    {
        if (!_retainedCompositionPictureKeys.TryGetValue(
                picture,
                out List<RetainedPicturePageKey>? keys))
        {
            return;
        }

        while (keys.Count >=
               Options.MaximumIncrementalScenePageVariantsPerVisual)
        {
            RetainedPicturePageKey oldestKey = keys[0];
            ulong oldestFrame = ulong.MaxValue;
            foreach (RetainedPicturePageKey key in keys)
            {
                var lookup = new RetainedPicturePageLookup(picture, key);
                if (_retainedCompositionPictures.TryGetValue(
                        lookup,
                        out IncrementalScenePage? page) &&
                    page.LastUsedFrame < oldestFrame)
                {
                    oldestKey = key;
                    oldestFrame = page.LastUsedFrame;
                }
            }
            RemoveRetainedCompositionPicture(
                new RetainedPicturePageLookup(picture, oldestKey));
        }
    }

    private void EvictOldestRetainedCompositionPicture()
    {
        RetainedPicturePageLookup? oldestLookup = null;
        IncrementalScenePage? oldestPage = null;
        foreach (var entry in _retainedCompositionPictures)
        {
            if (oldestPage == null ||
                entry.Value.LastUsedFrame < oldestPage.LastUsedFrame)
            {
                oldestLookup = entry.Key;
                oldestPage = entry.Value;
            }
        }

        if (oldestLookup.HasValue)
        {
            RemoveRetainedCompositionPicture(oldestLookup.Value);
        }
    }

    private void SweepRetainedCompositionPictures()
    {
        if (_retainedCompositionPictures.Count == 0 ||
            _frameNumber % 60 != 0)
        {
            return;
        }

        List<RetainedPicturePageLookup>? expired = null;
        foreach (var entry in _retainedCompositionPictures)
        {
            if (_frameNumber - entry.Value.LastUsedFrame >
                (ulong)Options.RetainedCompositionPictureRetentionFrames)
            {
                (expired ??= new()).Add(entry.Key);
            }
        }

        if (expired == null)
        {
            return;
        }
        foreach (RetainedPicturePageLookup lookup in expired)
        {
            RemoveRetainedCompositionPicture(lookup);
        }
    }

    private void RemoveRetainedCompositionPicture(
        in RetainedPicturePageLookup lookup)
    {
        _retainedCompositionPictures.Remove(lookup);
        if (!_retainedCompositionPictureKeys.TryGetValue(
                lookup.Picture,
                out List<RetainedPicturePageKey>? keys))
        {
            return;
        }

        keys.Remove(lookup.Key);
        if (keys.Count == 0)
        {
            _retainedCompositionPictureKeys.Remove(lookup.Picture);
        }
    }
}
