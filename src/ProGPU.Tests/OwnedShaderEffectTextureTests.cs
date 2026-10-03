using System;
using System.Runtime.CompilerServices;
using System.Threading;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Scene;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class OwnedShaderEffectTextureTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void IndependentSamplerCloneOutlivesSourceAndSnapshotsMetadata(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var texture = CreateTexture(device.Context);
        using var owner = new OwnedShaderEffectTexture(texture);
        using var sampler = WpfShaderEffectSampler.FromOwnedTexture(3, owner, TextureSamplingMode.Nearest);
        using var clone = sampler.CloneOwned();
        sampler.RegisterIndex = 4;
        sampler.SamplingMode = TextureSamplingMode.Linear;
        owner.Dispose();
        sampler.Dispose();
        Assert.False(owner.TryGetGpuTexture(out _));
        Assert.False(owner.TryAcquireGpuTextureLease(out _));
        Assert.Equal(3, clone.RegisterIndex);
        Assert.Equal(TextureSamplingMode.Nearest, clone.SamplingMode);
        Assert.Same(texture, clone.Texture);
        Assert.False(texture.IsDisposed);

        var retainedSource = Assert.IsAssignableFrom<IProGpuTextureLeaseSource>(clone.RetainedTextureSource);
        Assert.True(retainedSource.TryAcquireGpuTextureLease(out var last));
        using (last)
        {
            clone.Dispose();
            Assert.Same(texture, last.Texture);
            Assert.False(texture.IsDisposed);
        }
        Assert.True(texture.IsDisposed);
        clone.Dispose(); owner.Dispose();
        Assert.Throws<InvalidOperationException>(() => clone.CloneOwned());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DuplicateTransferAndInvalidRegisterDoNotEndOriginalOwnership(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var texture = CreateTexture(device.Context);
        using var owner = new OwnedShaderEffectTexture(texture);
        Assert.Throws<ArgumentException>(() => new OwnedShaderEffectTexture(texture));
        Assert.Throws<ArgumentOutOfRangeException>(() => WpfShaderEffectSampler.FromOwnedTexture(-1, owner));
        Assert.True(owner.TryGetGpuTexture(out var actual));
        Assert.Same(texture, actual);
        Assert.False(texture.IsDisposed);
        owner.Dispose();
        Assert.True(texture.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => new OwnedShaderEffectTexture(texture));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OwnedSamplerCannotChangeItsRetainedTextureButBorrowedConstructorStaysBorrowed(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var texture = CreateTexture(device.Context);
        using var other = CreateTexture(device.Context);
        using var owner = new OwnedShaderEffectTexture(texture);
        using var owned = WpfShaderEffectSampler.FromOwnedTexture(1, owner);
        Assert.Throws<InvalidOperationException>(() => owned.Texture = other);
        Assert.Same(texture, owned.Texture);

        using var borrowed = new WpfShaderEffectSampler(2, other);
        Assert.Throws<InvalidOperationException>(() => borrowed.CloneOwned());
        borrowed.Dispose();
        Assert.False(other.IsDisposed);
        borrowed.Texture = texture;
        Assert.Same(texture, borrowed.Texture);
        Assert.Throws<InvalidOperationException>(() => borrowed.CloneOwned());
    }

    [Fact]
    public void ImplicitSamplerAndOwnedParametersSnapshotCallerMetadata()
    {
        using var implicitSampler = new WpfShaderEffectSampler(2, null, TextureSamplingMode.Nearest);
        var parameters = new WpfShaderEffectParams
        {
            ShaderSource = "original shader source",
            ShaderKey = "original owned recipe",
            Constants = [1, 2, 3, 4],
            Samplers = [implicitSampler],
            SourceTextureRegisterIndex = 2,
            SamplingMode = TextureSamplingMode.Nearest
        };
        using var owned = new OwnedShaderEffectParameters(parameters);
        parameters.Constants[0] = 99;
        parameters.ShaderKey = "changed caller";
        parameters.ShaderSource = "changed shader";
        parameters.Samplers = [];
        implicitSampler.RegisterIndex = 7;
        implicitSampler.SamplingMode = TextureSamplingMode.Linear;

        var snapshot = owned.GetSnapshot();
        Assert.Equal(new float[] { 1, 2, 3, 4 }, snapshot.Constants);
        Assert.Equal("original owned recipe", snapshot.ShaderKey);
        Assert.Equal("original shader source", snapshot.ShaderSource);
        Assert.Equal(2, snapshot.SourceTextureRegisterIndex);
        Assert.Equal(TextureSamplingMode.Nearest, snapshot.SamplingMode);
        Assert.Equal(2, Assert.Single(snapshot.Samplers).RegisterIndex);
        Assert.Equal(TextureSamplingMode.Nearest, snapshot.Samplers[0].SamplingMode);
        Assert.Null(snapshot.Samplers[0].Texture);
        owned.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owned.GetSnapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedParameterCandidateReleasesOnlyItsNewSamplerLeases(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var texture = CreateTexture(device.Context);
        using var borrowedTexture = CreateTexture(device.Context);
        using var owner = new OwnedShaderEffectTexture(texture);
        using var ownedSampler = WpfShaderEffectSampler.FromOwnedTexture(1, owner);
        using var borrowedSampler = new WpfShaderEffectSampler(2, borrowedTexture);
        var parameters = new WpfShaderEffectParams { Samplers = [ownedSampler, borrowedSampler] };
        Assert.Throws<InvalidOperationException>(() => new OwnedShaderEffectParameters(parameters));
        Assert.False(texture.IsDisposed);
        owner.Dispose();
        Assert.False(texture.IsDisposed);
        ownedSampler.Dispose();
        // No leaked clone from the failed later register may retain texture.
        Assert.True(texture.IsDisposed);
        Assert.False(borrowedTexture.IsDisposed);
        Assert.Same(ownedSampler, parameters.Samplers[0]);
        Assert.Same(borrowedSampler, parameters.Samplers[1]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RecordedPictureCloneRetainsExactSamplerAfterOwnersEnd(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var texture = CreateTexture(device.Context);
        using var owner = new OwnedShaderEffectTexture(texture);
        using var sampler = WpfShaderEffectSampler.FromOwnedTexture(1, owner);
        var recorder = new GpuPictureRecorder();
        var drawing = recorder.BeginRecording(new Rect(0, 0, 8, 4));
        drawing.DrawWpfShaderEffect(new WpfShaderEffectParams
        {
            Rect = new Rect(0, 0, 8, 4), Samplers = [sampler]
        });
        using var picture = recorder.EndRecording();
        using var clone = picture.Clone();
        Assert.True(picture.SharesRetainedCommandStorageWith(clone));
        Assert.Equal(1, picture.RetainedResourceCount);
        Assert.Equal(1, clone.RetainedResourceCount);
        drawing.Clear();
        picture.Dispose(); sampler.Dispose(); owner.Dispose();
        Assert.False(texture.IsDisposed);
        clone.Dispose();
        Assert.True(texture.IsDisposed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LastOffThreadLeaseTransfersRetirementToCreatingThread(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var texture = CreateTexture(device.Context);
        using var owner = new OwnedShaderEffectTexture(texture);
        Assert.True(owner.TryAcquireGpuTextureLease(out var lease));
        owner.Dispose();
        Exception? failure = null;
        var worker = new Thread(() => { try { lease.Dispose(); } catch (Exception error) { failure = error; } })
        { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
        Assert.False(texture.IsDisposed);
        Assert.True(PendingOwners(device.Context) > 0);
        Drain(device.Context);
        Assert.True(texture.IsDisposed);
        Assert.Equal(0, PendingOwners(device.Context));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ContextShutdownRetiresLiveOwnedGenerationsWithoutCollection(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var texture = CreateTexture(device.Context);
        using var owner = new OwnedShaderEffectTexture(texture);
        using var sampler = WpfShaderEffectSampler.FromOwnedTexture(1, owner);
        using var clone = sampler.CloneOwned();
        var retainedSource = Assert.IsAssignableFrom<IProGpuTextureLeaseSource>(clone.RetainedTextureSource);
        device.Dispose();
        Assert.True(texture.IsDisposed);
        Assert.True(owner.IsDisposed);
        Assert.False(owner.TryAcquireGpuTextureLease(out _));
        Assert.False(retainedSource.TryAcquireGpuTextureLease(out _));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void AbandonedOwnerOrParameterOnlyQueuesRetirement(bool useDawn, bool abandonParameter)
    {
        using var device = new OwnedDevice(useDawn);
        using var texture = Abandon(device.Context, abandonParameter);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Assert.False(texture.IsDisposed);
        Assert.True(PendingOwners(device.Context) > 0);
        Drain(device.Context);
        Assert.True(texture.IsDisposed);
        Assert.Equal(0, PendingOwners(device.Context));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static GpuTexture Abandon(WgpuContext context, bool parameter)
    {
        var texture = CreateTexture(context);
        var owner = new OwnedShaderEffectTexture(texture);
        if (parameter)
        {
            var sampler = WpfShaderEffectSampler.FromOwnedTexture(1, owner);
            owner.Dispose();
            GC.KeepAlive(sampler);
        }
        GC.KeepAlive(owner);
        return texture;
    }

    private static GpuTexture CreateTexture(WgpuContext context) => new(context, 8, 4,
        TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.RenderAttachment,
        "Owned shader generation control", alphaMode: GpuTextureAlphaMode.Premultiplied);

    private static int PendingOwners(WgpuContext context)
    {
        lock (context.DisposalLock) return context.PendingExternalTextureOwners.Count;
    }

    private static void Drain(WgpuContext context)
    {
        context.CleanupPendingResources();
        context.CleanupPendingResources();
    }

    private sealed class OwnedDevice : IDisposable
    {
        private readonly DawnGpuContext? _dawn;
        private bool _disposed;
        internal WgpuContext Context { get; }
        internal OwnedDevice(bool dawn)
        {
            if (dawn)
            {
                BackendType backend = OperatingSystem.IsWindows() ? BackendType.D3D12 :
                    OperatingSystem.IsMacOS() ? BackendType.Metal : BackendType.Vulkan;
                _dawn = DawnGpuContext.CreateOffscreen(backend, forceFallbackAdapter: false);
                Context = _dawn.Context;
            }
            else
            {
                Context = new WgpuContext();
                Context.Initialize(null);
            }
        }
        public void Dispose()
        {
            if (_disposed) return;
            if (_dawn is not null) _dawn.Dispose(); else Context.Dispose();
            _disposed = true;
        }
    }
}
