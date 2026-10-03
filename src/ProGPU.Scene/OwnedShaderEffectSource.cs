using System;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using ProGPU.Backend;

namespace ProGPU.Scene;

/// <summary>
/// An owned immutable source recipe. Preparation may realize its previously
/// recorded sampler pictures for the supplied actual target. It must not read
/// mutable source UI, encode a draw, or substitute an outer target frame.
/// </summary>
public interface IShaderEffectPreparation : IDisposable
{
    OwnedShaderEffectParameters Prepare(ShaderEffectPreparationContext context);
}

/// <summary>Actual compositor target and derived implicit-input frame for one preparation.</summary>
public readonly struct ShaderEffectPreparationContext
{
    internal ShaderEffectPreparationContext(Compositor compositor, OwnedShaderEffectSource source,
        Matrix4x4 projection, RenderTargetViewport viewport, uint width, uint height,
        float dpiScale, EffectCaptureFrame captureFrame)
    {
        Compositor = compositor; Source = source; Projection = projection; Viewport = viewport;
        TargetWidth = width; TargetHeight = height; DpiScale = dpiScale; CaptureFrame = captureFrame;
        DeviceIdentity = compositor.Context.DeviceIdentity;
    }

    public Compositor Compositor { get; }
    public OwnedShaderEffectSource Source { get; }
    public WgpuDeviceIdentity DeviceIdentity { get; }
    public Matrix4x4 Projection { get; }
    public RenderTargetViewport Viewport { get; }
    public uint TargetWidth { get; }
    public uint TargetHeight { get; }
    public float DpiScale { get; }
    public EffectCaptureFrame CaptureFrame { get; }

    internal bool Matches(in ShaderEffectPreparationContext other) =>
        ReferenceEquals(Compositor, other.Compositor) && ReferenceEquals(Source, other.Source) &&
        ReferenceEquals(DeviceIdentity, other.DeviceIdentity) && Projection == other.Projection &&
        Viewport == other.Viewport && TargetWidth == other.TargetWidth && TargetHeight == other.TargetHeight &&
        BitConverter.SingleToInt32Bits(DpiScale) == BitConverter.SingleToInt32Bits(other.DpiScale) &&
        CaptureFrame.HasSameCapture(other.CaptureFrame);
}

/// <summary>
/// Owns one immutable source identity and recipe. Construction transfers recipe
/// ownership only on success. Recorded pictures retain independent references;
/// disposing this caller reference does not retire their source snapshots.
/// </summary>
public sealed class OwnedShaderEffectSource : IDisposable
{
    private RetainedResourceLease? _lifetime;
    internal IShaderEffectPreparation Preparation { get; }
    public ShaderEffectSourceCapture SourceCapture { get; }
    public Vector2 SourceTranslation { get; }

    public OwnedShaderEffectSource(ShaderEffectSourceCapture sourceCapture, Vector2 sourceTranslation,
        IShaderEffectPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!sourceCapture.IsValid) throw new ArgumentException("A valid original source capture is required.", nameof(sourceCapture));
        if (!float.IsFinite(sourceTranslation.X) || !float.IsFinite(sourceTranslation.Y))
            throw new ArgumentOutOfRangeException(nameof(sourceTranslation));
        SourceCapture = sourceCapture; SourceTranslation = sourceTranslation; Preparation = preparation;
        _lifetime = RetainedResourceLease.Create(preparation, this);
    }

    internal RetainedResourceLease Acquire() =>
        (_lifetime ?? throw new ObjectDisposedException(nameof(OwnedShaderEffectSource))).AddRef();

    public void Dispose() => Interlocked.Exchange(ref _lifetime, null)?.Dispose();
}

/// <summary>
/// Immutable shader/register snapshot with independent exact sampler leases.
/// Borrowed nonnull sampler textures are not admitted by this owned factory.
/// Existing WpfShaderEffectParams and sampler constructors remain borrowed.
/// </summary>
public sealed class OwnedShaderEffectParameters : IDisposable
{
    private bool _disposed;
    internal WpfShaderEffectParams Snapshot { get; }

    public OwnedShaderEffectParameters(WpfShaderEffectParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Texture is not null)
            throw new ArgumentException("Implicit input belongs to the actual compositor capture.", nameof(parameters));
        var samplers = new WpfShaderEffectSampler[parameters.Samplers.Length];
        try
        {
            for (int index = 0; index < samplers.Length; index++) samplers[index] = parameters.Samplers[index].CloneOwned();
            Snapshot = new WpfShaderEffectParams
            {
                ShaderSource = parameters.ShaderSource, ShaderKey = parameters.ShaderKey,
                Constants = (float[])parameters.Constants.Clone(), Samplers = samplers,
                SamplingMode = parameters.SamplingMode,
                SourceTextureRegisterIndex = parameters.SourceTextureRegisterIndex
            };
        }
        catch (Exception failure)
        {
            foreach (var sampler in samplers)
                try { sampler?.Dispose(); }
                catch (Exception cleanup) { failure.Data["OwnedShaderSamplerCleanup"] = cleanup; }
            throw;
        }
    }

    internal WpfShaderEffectParams GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Snapshot;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ExceptionDispatchInfo? failure = null;
        foreach (var sampler in Snapshot.Samplers)
            try { sampler.Dispose(); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        failure?.Throw();
    }
}

internal sealed class OwnedShaderEffectRecording : Visual, IDisposable
{
    private RetainedResourceLease? _lifetime;
    internal GpuPicture Picture { get; }
    internal OwnedShaderEffectSource Source { get; }

    internal OwnedShaderEffectRecording(GpuPicture content, OwnedShaderEffectSource source)
    {
        var sourceLease = source.Acquire();
        try
        {
            Picture = content.Clone();
            Source = source;
            _lifetime = RetainedResourceLease.Create(new Payload(Picture, sourceLease), this);
        }
        catch { sourceLease.Dispose(); throw; }
    }

    internal RetainedResourceLease Acquire() =>
        (_lifetime ?? throw new ObjectDisposedException(nameof(OwnedShaderEffectRecording))).AddRef();

    public void Dispose() => Interlocked.Exchange(ref _lifetime, null)?.Dispose();

    private sealed class Payload(GpuPicture picture, RetainedResourceLease source) : IDisposable
    {
        public void Dispose()
        {
            ExceptionDispatchInfo? failure = null;
            try { picture.Dispose(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            try { source.Dispose(); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
            failure?.Throw();
        }
    }
}

public partial class DrawingContext
{
    /// <summary>
    /// Records one ordered owned effect scope, without appending/reordering
    /// children or borrowing their lifetime. Content and recipe are independently
    /// retained; target-dependent work occurs only when the actual target exists.
    /// </summary>
    public void DrawOwnedShaderEffect(GpuPicture content, OwnedShaderEffectSource source, Matrix4x4 transform = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(source);
        var recording = new OwnedShaderEffectRecording(content, source);
        try { RetainResource(recording); }
        catch { recording.Dispose(); throw; }
        DrawVisual(recording, transform);
    }
}
