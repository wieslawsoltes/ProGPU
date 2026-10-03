using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.ExceptionServices;
using ProGPU.Backend;

namespace ProGPU.Scene;

public unsafe partial class Compositor
{
    private readonly List<OwnedShaderTarget> _ownedShaderTargets = [];
    private readonly HashSet<OwnedShaderEffectSource> _ownedShaderSourcesPreparing = [];

    private ShaderEffectPreparationContext GetOwnedShaderTarget(OwnedShaderEffectSource source)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_context.IsInitialized || _context.IsDeviceLost)
            throw new InvalidOperationException("The owned shader preparation device is unavailable.");
        GetRootRenderTargetSize(_currentWidth, _currentHeight, out uint width, out uint height);
        var viewport = NormalizeRenderTargetViewport(
            _explicitRenderTargetViewport ?? RenderTargetViewport.Full(width, height), width, height);
        if (!EffectCaptureFrame.TryResolveSourcePixelsPerUnit(_currentProjection, viewport, out var scale) ||
            !EffectCaptureFrame.TryCreateSource(source.SourceCapture, source.SourceTranslation, scale,
                _currentDpiScale, out var frame))
            throw new InvalidOperationException("The actual owned shader target cannot represent its source capture.");
        return new ShaderEffectPreparationContext(this, source, _currentProjection, viewport,
            width, height, _currentDpiScale, frame);
    }

    private OwnedShaderTarget PrepareOwnedShaderEffect(OwnedShaderEffectRecording recording)
    {
        var context = GetOwnedShaderTarget(recording.Source);
        foreach (var existing in _ownedShaderTargets)
        {
            if (!ReferenceEquals(existing.Recording, recording) || !existing.Context.Matches(context)) continue;
            existing.LastUsedFrame = _frameNumber;
            return existing;
        }
        // This is a typed source-generation boundary, never a generic callback
        // while drawing shader vertices. The guard covers nested sampler source
        // realizations as well as ordinary implicit-input recursion.
        if (_ownedShaderSourcesPreparing.Count >= 256 || !_ownedShaderSourcesPreparing.Add(recording.Source))
            throw new InvalidOperationException("Owned shader source preparation contains a cycle or exceeds its depth budget.");

        RetainedResourceLease? sourceLease = null;
        OwnedShaderEffectParameters? parameters = null;
        OwnedShaderTarget? candidate = null;
        int priorTargetCount = _ownedShaderTargets.Count;
        try
        {
            sourceLease = recording.Acquire();
            parameters = recording.Source.Preparation.Prepare(context) ??
                throw new InvalidOperationException("Owned shader preparation returned no parameter generation.");
            parameters.ValidateDevice(_context);
            if (!context.Matches(GetOwnedShaderTarget(recording.Source)))
                throw new InvalidOperationException("Owned shader preparation changed its actual target or device.");
            candidate = new OwnedShaderTarget(this, recording, context, parameters, sourceLease, _frameNumber);
            parameters = null;
            sourceLease = null;

            // Materialize before publishing the target variant. An exception
            // leaves every earlier target's parameters and texture unchanged.
            PrepareEffectTexture(candidate.Visual);
            if (!context.Matches(GetOwnedShaderTarget(recording.Source)))
                throw new InvalidOperationException("Owned shader capture changed its actual target or device.");
            _ownedShaderTargets.Add(candidate);
            return candidate;
        }
        catch (Exception failure)
        {
            // Nested sampler realizations may have completed during this
            // candidate. Roll back only those new variants, never an earlier
            // generation that was reused by the failed source.
            try
            {
                var nested = _ownedShaderTargets.GetRange(priorTargetCount, _ownedShaderTargets.Count - priorTargetCount);
                _ownedShaderTargets.RemoveRange(priorTargetCount, nested.Count);
                foreach (var target in nested)
                    try { target.Dispose(); }
                    catch (Exception cleanup) { NoteOwnedShaderCleanup(failure, "OwnedShaderNestedCleanup", cleanup); }
            }
            catch (Exception cleanup) { NoteOwnedShaderCleanup(failure, "OwnedShaderRollbackCleanup", cleanup); }
            try { candidate?.Dispose(); }
            catch (Exception cleanup) { NoteOwnedShaderCleanup(failure, "OwnedShaderCandidateCleanup", cleanup); }
            try { parameters?.Dispose(); }
            catch (Exception cleanup) { NoteOwnedShaderCleanup(failure, "OwnedShaderParameterCleanup", cleanup); }
            try { sourceLease?.Dispose(); }
            catch (Exception cleanup) { NoteOwnedShaderCleanup(failure, "OwnedShaderSourceCleanup", cleanup); }
            throw;
        }
        finally { _ownedShaderSourcesPreparing.Remove(recording.Source); }
    }

    private static void NoteOwnedShaderCleanup(Exception failure, string key, Exception cleanup)
    {
        try { failure.Data[key] = cleanup; }
        catch { }
    }

    private void CompileOwnedShaderEffect(OwnedShaderEffectRecording recording, Matrix4x4 transform)
    {
        OwnedShaderTarget target = PrepareOwnedShaderEffect(recording);
        var lease = target.Acquire();
        try { _frameRetainedResources.Add(lease); }
        catch { lease.Dispose(); throw; }
        TrackEmbeddedVisual(target.Visual);
        CompileVisualTree(target.Visual, transform);
    }

    // Keep only actual variants used in this completed frame. Resizing cannot
    // accumulate a historical texture list; exact draw/compiled-scene leases can
    // still outlive cache eviction. Never evict on a failed candidate/frame.
    private void SweepOwnedShaderTargets()
    {
        ExceptionDispatchInfo? failure = null;
        for (int index = _ownedShaderTargets.Count - 1; index >= 0; index--)
        {
            var target = _ownedShaderTargets[index];
            if (target.LastUsedFrame == _frameNumber) continue;
            _ownedShaderTargets.RemoveAt(index);
            try { target.Dispose(); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        }
        failure?.Throw();
    }

    private void DisposeOwnedShaderTargets()
    {
        var targets = _ownedShaderTargets.ToArray();
        _ownedShaderTargets.Clear();
        ExceptionDispatchInfo? failure = null;
        foreach (var target in targets)
            try { target.Dispose(); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        failure?.Throw();
    }

    private sealed class PreparedOwnedShaderVisual : DrawingVisual { }

    private sealed class OwnedShaderTarget : IDisposable
    {
        private readonly RetainedResourceLease _lifetime;
        internal OwnedShaderEffectRecording Recording { get; }
        internal ShaderEffectPreparationContext Context { get; }
        internal PreparedOwnedShaderVisual Visual { get; }
        internal ulong LastUsedFrame { get; set; }

        internal OwnedShaderTarget(Compositor compositor, OwnedShaderEffectRecording recording,
            ShaderEffectPreparationContext context, OwnedShaderEffectParameters parameters,
            RetainedResourceLease sourceLease, ulong frame)
        {
            Recording = recording; Context = context; LastUsedFrame = frame;
            Visual = new PreparedOwnedShaderVisual
            {
                Size = new Vector2((float)recording.Source.SourceCapture.Width, (float)recording.Source.SourceCapture.Height),
                EffectSourceTranslation = recording.Source.SourceTranslation,
                Effect = new WpfShaderEffect(parameters.GetSnapshot())
                {
                    SourceCapture = recording.Source.SourceCapture,
                    CaptureSourceVisualOpacity = true
                }
            };
            try
            {
                Visual.Context.DrawPicture(recording.Picture);
                _lifetime = RetainedResourceLease.Create(new TargetResources(compositor, Visual, parameters, sourceLease));
            }
            catch (Exception failure)
            {
                try { Visual.Context.Clear(); }
                catch (Exception cleanup) { NoteOwnedShaderCleanup(failure, "OwnedShaderRecordingCleanup", cleanup); }
                throw;
            }
        }

        internal RetainedResourceLease Acquire() => _lifetime.AddRef();
        public void Dispose() => _lifetime.Dispose();

        private sealed class TargetResources(Compositor compositor, PreparedOwnedShaderVisual visual,
            OwnedShaderEffectParameters parameters, RetainedResourceLease source) : IDisposable
        {
            public void Dispose()
            {
                ExceptionDispatchInfo? failure = null;
                compositor._effectCacheKeys.Remove(visual);
                compositor._wpfShaderEffectDrawParams.Remove(visual);
                if (compositor._effectTextures.Remove(visual, out var textures))
                    try { textures.Dispose(); }
                    catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
                try { visual.Context.Clear(); }
                catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
                try { parameters.Dispose(); }
                catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
                try { source.Dispose(); }
                catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
                failure?.Throw();
            }
        }
    }
}
