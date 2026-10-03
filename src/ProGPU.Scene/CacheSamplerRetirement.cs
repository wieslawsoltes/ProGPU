using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace ProGPU.Scene;

internal interface ICacheSamplerRetirementParticipant
{
    void Retire();
}

// The context's existing queue-only transfer is the sole injected operation.
// Participants retain actual source/device failure and thread policy; this
// coordinator never pretends a returned owner Dispose proves that policy.
internal sealed class CacheSamplerRetirement(Action<IDisposable> enqueue)
{
    // Never invoke participants or acquire RenderLock while held. Source cleanup
    // may own RenderLock and wait for finalizers; transfer must remain independent.
    private readonly object _sync = new();
    private readonly List<WeakReference<ICacheSamplerRetirementParticipant>> _participants = [];
    private readonly Dictionary<ICacheSamplerRetirementParticipant, object?> _failed =
        new(ReferenceEqualityComparer.Instance);
    private bool _retiring;
    private bool _dispatching;

    internal void EnsureAcceptsCapture()
    {
        lock (_sync)
            if (_retiring) throw new ObjectDisposedException(nameof(CacheSamplerRaster), "The owning context is retiring cache sources.");
    }

    internal void Add(ICacheSamplerRetirementParticipant participant)
    {
        lock (_sync)
        {
            EnsureAcceptsCapture();
            _participants.RemoveAll(static entry => !entry.TryGetTarget(out _));
            _participants.Add(new(participant, trackResurrection: true));
        }
    }

    internal void RetainFailure(ICacheSamplerRetirementParticipant participant, object? exactPayload = null)
    {
        lock (_sync)
            if (exactPayload is not null || !_failed.ContainsKey(participant)) _failed[participant] = exactPayload;
    }

    internal void Remove(ICacheSamplerRetirementParticipant participant)
    {
        lock (_sync)
        {
            _failed.Remove(participant);
            _participants.RemoveAll(entry => !entry.TryGetTarget(out var target) || ReferenceEquals(target, participant));
        }
    }

    internal void Queue(IDisposable owner)
    {
        lock (_sync)
        {
            // Closing admission and retaining the complete live snapshot are
            // one handshake. No transfer may target an already-finished drain.
            if (!_retiring) enqueue(owner);
        }
    }

    internal void RetireAll()
    {
        HashSet<ICacheSamplerRetirementParticipant> snapshot;
        lock (_sync)
        {
            if (_dispatching) throw new InvalidOperationException("Cache source retirement cannot complete reentrantly.");
            _retiring = true;
            snapshot = new(_failed.Keys, ReferenceEqualityComparer.Instance);
            foreach (var entry in _participants)
                if (entry.TryGetTarget(out var participant)) snapshot.Add(participant);
            _dispatching = true;
        }
        try
        {
            ExceptionDispatchInfo? failure = null;
            foreach (ICacheSamplerRetirementParticipant participant in snapshot)
            {
                try { participant.Retire(); }
                catch (Exception error)
                {
                    RetainFailure(participant);
                    failure ??= ExceptionDispatchInfo.Capture(error);
                }
            }
            failure?.Throw();
        }
        finally
        {
            lock (_sync) _dispatching = false;
        }
    }
}
