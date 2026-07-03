using System.IO;
using System.Reflection;
using Avalonia.Rendering.Composition;
using ProGPU.Avalonia;
using ProGPU.Backend;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public class ProGpuHostControlTests
{
    [Fact]
    public void ZeroCopyCompositionIsOptInByDefault()
    {
        var control = new ProGpuHostControl();

        Assert.False(control.EnableZeroCopy);
    }

    [Fact]
    public void HostControlCanFallbackWhenSharedImageImportFails()
    {
        var resizeMethod = typeof(ProGpuHostControl).GetMethod(
            "ResizeSharedResources",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var fallbackMethod = typeof(ProGpuHostControl).GetMethod(
            "TryUseCustomVisualFallback",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(resizeMethod);
        Assert.Equal(typeof(bool), resizeMethod.ReturnType);
        Assert.NotNull(fallbackMethod);
        Assert.Equal(typeof(bool), fallbackMethod.ReturnType);
    }

    [Fact]
    public void HostControlRechecksZeroCopyFrameAfterAsyncMap()
    {
        var guardMethod = typeof(ProGpuHostControl).GetMethod(
            "IsCurrentZeroCopyFrame",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var unmapMethod = typeof(ProGpuHostControl).GetMethod(
            "TryUnmapStagingBuffer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var copyMethod = typeof(ProGpuHostControl).GetMethod(
            "CopyMappedToSharedTexture",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(guardMethod);
        Assert.Equal(typeof(bool), guardMethod.ReturnType);

        var guardParameters = guardMethod.GetParameters();
        Assert.Equal(6, guardParameters.Length);
        Assert.Equal("swapchainImages", guardParameters[0].Name);
        Assert.Equal("imageIndex", guardParameters[1].Name);
        Assert.Equal("image", guardParameters[2].Name);
        Assert.Equal("importedImage", guardParameters[3].Name);
        Assert.Equal(typeof(ICompositionImportedGpuImage), guardParameters[3].ParameterType);
        Assert.Equal("drawingSurface", guardParameters[4].Name);
        Assert.Equal(typeof(CompositionDrawingSurface), guardParameters[4].ParameterType);
        Assert.Equal("context", guardParameters[5].Name);
        Assert.Equal(typeof(WgpuContext), guardParameters[5].ParameterType);

        Assert.NotNull(unmapMethod);
        var unmapParameters = unmapMethod.GetParameters();
        Assert.Equal(2, unmapParameters.Length);
        Assert.Equal(typeof(WgpuContext), unmapParameters[0].ParameterType);

        Assert.NotNull(copyMethod);
        var copyParameters = copyMethod.GetParameters();
        Assert.Equal(4, copyParameters.Length);
        Assert.Equal(typeof(WgpuContext), copyParameters[0].ParameterType);
    }

    [Fact]
    public void HostControlRechecksGraphicsStateBeforeCreatingFallbackVisual()
    {
        string source = File.ReadAllText(FindProGpuHostControlSource()).Replace("\r\n", "\n");

        Assert.Contains("var expectedContext = _wgpuContext;", source, StringComparison.Ordinal);
        Assert.Contains("var expectedCompositor = _compositor;", source, StringComparison.Ordinal);
        Assert.Contains("IsCompositionSurfaceSetupCurrent(compositor, expectedContext, expectedCompositor)", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_wgpuContext, context)", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_compositor, compositor)", source, StringComparison.Ordinal);
        Assert.Contains("_wgpuContext == null || _compositor == null || _wgpuContext.IsDisposed", source, StringComparison.Ordinal);
        Assert.Contains("!_customVisualHandler.Matches(_wgpuContext, _compositor)", source, StringComparison.Ordinal);
        Assert.Contains("internal bool Matches(WgpuContext context, WinuiCompositor compositor)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AvaloniaHostCoalescesRenderRequestsOutsidePaintCallback()
    {
        string source = File.ReadAllText(FindProGpuHostControlSource()).Replace("\r\n", "\n");
        string sampleSource = File.ReadAllText(FindProGpuAvaloniaSampleSource()).Replace("\r\n", "\n");
        int renderIndex = source.IndexOf("public override void Render(", StringComparison.Ordinal);
        int customVisualIndex = source.IndexOf("public unsafe class ProGpuCustomVisualHandler", StringComparison.Ordinal);

        Assert.True(renderIndex >= 0, "Expected ProGpuHostControl.Render override.");
        Assert.True(customVisualIndex > renderIndex, "Expected custom visual handler after host control.");

        string renderMethod = source[renderIndex..customVisualIndex];

        Assert.Contains("private bool _renderDispatchQueued = false;", source, StringComparison.Ordinal);
        Assert.Contains("public void RequestRender()", source, StringComparison.Ordinal);
        Assert.Contains("private void QueueRenderUpdate()", source, StringComparison.Ordinal);
        Assert.Contains("private async void ProcessQueuedRenderUpdate()", source, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.UIThread.Post(ProcessQueuedRenderUpdate, DispatcherPriority.Render);", source, StringComparison.Ordinal);
        Assert.Contains("if (change.Property == WinuiRootProperty)", source, StringComparison.Ordinal);
        Assert.Contains("InvalidateMeasure();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private async void QueueRenderUpdate()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueRenderUpdate();", renderMethod, StringComparison.Ordinal);
        Assert.Contains("ProGpuHost.RequestRender();", sampleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ProGpuHost.InvalidateVisual();", sampleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AvaloniaHostExposesTypedCompositorFrameDiagnostics()
    {
        string source = File.ReadAllText(FindProGpuHostControlSource()).Replace("\r\n", "\n");
        string sampleSource = File.ReadAllText(FindProGpuAvaloniaSampleSource()).Replace("\r\n", "\n");

        Assert.False(ProGpuAvaloniaHostFrameState.Empty.HasPresentedFrame);
        Assert.Equal(ProGpuAvaloniaPresentationMode.None, ProGpuAvaloniaHostFrameState.Empty.PresentationMode);

        var frame = CompositorHostFrame.FromLogicalSize(100, 50, 2);
        var state = new ProGpuAvaloniaHostFrameState(
            frame,
            ProGpuAvaloniaPresentationMode.CustomVisualReadback,
            3,
            1,
            2,
            false,
            true,
            string.Empty);

        Assert.True(state.HasPresentedFrame);
        Assert.Equal(200u, state.HostFrame.RenderTargetWidth);
        Assert.Equal(100u, state.HostFrame.RenderTargetHeight);
        Assert.Equal(3ul, state.PresentedFrameCount);
        Assert.Equal(1ul, state.ZeroCopyPresentedFrameCount);
        Assert.Equal(2ul, state.ReadbackPresentedFrameCount);

        Assert.Contains("public ProGpuAvaloniaHostFrameState LastPresentedFrameState", source, StringComparison.Ordinal);
        Assert.Contains("private void RecordPresentedFrame(CompositorHostFrame frame, ProGpuAvaloniaPresentationMode mode)", source, StringComparison.Ordinal);
        Assert.Contains("RecordPresentedFrame(hostFrame, ProGpuAvaloniaPresentationMode.ZeroCopySharedTexture);", source, StringComparison.Ordinal);
        Assert.Contains("new ProGpuCustomVisualHandler(_wgpuContext, _compositor, RecordReadbackPresentedFrame)", source, StringComparison.Ordinal);
        Assert.Contains("_framePresented?.Invoke(hostFrame);", source, StringComparison.Ordinal);
        Assert.Contains("var frameState = ProGpuHost.LastPresentedFrameState;", sampleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetField(", sampleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty(", sampleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BindingFlags", sampleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseSharedResourcesIgnoresPartiallyInitializedSwapchainImages()
    {
        var control = new ProGpuHostControl();
        var swapchainImageType = typeof(ProGpuHostControl).GetNestedType(
            "SwapchainImage",
            BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(ProGpuHostControl).FullName, "SwapchainImage");
        var field = typeof(ProGpuHostControl).GetField(
            "_swapchainImages",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ProGpuHostControl).FullName, "_swapchainImages");
        var releaseMethod = typeof(ProGpuHostControl).GetMethod(
            "ReleaseSharedResources",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ProGpuHostControl).FullName, "ReleaseSharedResources");

        field.SetValue(control, Array.CreateInstance(swapchainImageType, 2));

        releaseMethod.Invoke(control, null);

        Assert.Null(field.GetValue(control));
    }

    [Fact]
    public void SwapchainImageDisposesStagingBufferThroughDeferredQueue()
    {
        var context = new WgpuContext();
        var swapchainImageType = typeof(ProGpuHostControl).GetNestedType(
            "SwapchainImage",
            BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(ProGpuHostControl).FullName, "SwapchainImage");
        var stagingBufferField = swapchainImageType.GetField(
            "StagingBuffer",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(swapchainImageType.FullName, "StagingBuffer");
        var stagingBufferSizeField = swapchainImageType.GetField(
            "StagingBufferSize",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(swapchainImageType.FullName, "StagingBufferSize");
        var bytesPerRowField = swapchainImageType.GetField(
            "BytesPerRow",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(swapchainImageType.FullName, "BytesPerRow");
        var image = Activator.CreateInstance(
            swapchainImageType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [context],
            culture: null)
            ?? throw new InvalidOperationException("Expected SwapchainImage instance.");
        var stagingBuffer = new IntPtr(0x1234);

        stagingBufferField.SetValue(image, stagingBuffer);
        stagingBufferSizeField.SetValue(image, 256u);
        bytesPerRowField.SetValue(image, 64u);

        try
        {
            ((IDisposable)image).Dispose();

            Assert.Contains(stagingBuffer, context.PendingBuffers);
            Assert.Equal(IntPtr.Zero, stagingBufferField.GetValue(image));
            Assert.Equal(0u, stagingBufferSizeField.GetValue(image));
            Assert.Equal(0u, bytesPerRowField.GetValue(image));
        }
        finally
        {
            context.PendingBuffers.Clear();
        }
    }

    [Fact]
    public void SwapchainImageDisposeUnmapsActiveStagingBufferBeforeQueueingDisposal()
    {
        string source = File.ReadAllText(FindProGpuHostControlSource()).Replace("\r\n", "\n");

        int unmapIndex = source.IndexOf(
            "_context.Wgpu.BufferUnmap((GpuBuffer*)StagingBuffer)",
            StringComparison.Ordinal);
        int queueIndex = source.IndexOf(
            "_context.QueueBufferDisposal(StagingBuffer)",
            StringComparison.Ordinal);

        Assert.Contains("public bool IsStagingBufferMapActive;", source, StringComparison.Ordinal);
        Assert.True(unmapIndex >= 0, "SwapchainImage.Dispose must unmap an active staging-buffer map.");
        Assert.True(queueIndex >= 0, "SwapchainImage.Dispose must queue staging-buffer disposal.");
        Assert.True(unmapIndex < queueIndex, "Mapped staging buffers must be unmapped before disposal is queued.");
        Assert.Contains("image.IsStagingBufferMapActive = true;", source, StringComparison.Ordinal);
        Assert.Contains("!image.IsStagingBufferMapActive", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AvaloniaHostStagingBuffersUseDeferredDisposalQueue()
    {
        string source = File.ReadAllText(FindProGpuHostControlSource()).Replace("\r\n", "\n");
        string readbackSource = File.ReadAllText(FindProGpuBackendSource("GpuTextureReadbackBuffer.cs")).Replace("\r\n", "\n");

        Assert.Contains("_context.QueueBufferDisposal(StagingBuffer)", source, StringComparison.Ordinal);
        Assert.Contains("_context.QueueBufferDisposal((IntPtr)_buffer)", readbackSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Wgpu.BufferDestroy((GpuBuffer*)StagingBuffer)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Wgpu.BufferRelease((GpuBuffer*)StagingBuffer)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Wgpu.BufferDestroy(_stagingBuffer)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Wgpu.BufferRelease(_stagingBuffer)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Wgpu.BufferDestroy(_buffer)", readbackSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Wgpu.BufferRelease(_buffer)", readbackSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AvaloniaCustomVisualFallbackUsesSharedReadbackBuffer()
    {
        string source = File.ReadAllText(FindProGpuHostControlSource()).Replace("\r\n", "\n");
        string readbackSource = File.ReadAllText(FindProGpuBackendSource("GpuTextureReadbackBuffer.cs")).Replace("\r\n", "\n");

        Assert.Contains("private GpuTextureReadbackBuffer? _readbackBuffer;", source, StringComparison.Ordinal);
        Assert.Contains("_readbackBuffer.TryReadTextureRows(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private GpuBuffer* _stagingBuffer;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BufferMapAsync(_stagingBuffer", source, StringComparison.Ordinal);

        Assert.Contains("private void UnmapActiveBuffer()", readbackSource, StringComparison.Ordinal);
        Assert.Contains("_context.Wgpu.BufferUnmap(_buffer);", readbackSource, StringComparison.Ordinal);
        Assert.Contains("finally\n        {\n            UnmapActiveBuffer();\n        }", readbackSource, StringComparison.Ordinal);
        Assert.Contains("QueueBufferDisposal();", readbackSource, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuTextureReadbackBufferAlignsRowsToWebGpuPitch()
    {
        Assert.Equal(0u, GpuTextureReadbackBuffer.AlignBytesPerRow(0, 4));
        Assert.Equal(256u, GpuTextureReadbackBuffer.AlignBytesPerRow(1, 4));
        Assert.Equal(256u, GpuTextureReadbackBuffer.AlignBytesPerRow(64, 4));
        Assert.Equal(512u, GpuTextureReadbackBuffer.AlignBytesPerRow(65, 4));
        Assert.Equal(512u, GpuTextureReadbackBuffer.AlignBytesPerRow(1, 257));
    }

    [Fact]
    public void GpuTextureReadbackBufferRejectsInvalidPixelStride()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuTextureReadbackBuffer.AlignBytesPerRow(1, 0));
    }

    [Fact]
    public void GpuTextureReadPixelsUsesSharedReadbackBuffer()
    {
        string source = File.ReadAllText(FindProGpuBackendSource("GpuTexture.cs")).Replace("\r\n", "\n");
        string method = source[
            source.IndexOf("public byte[] ReadPixels", StringComparison.Ordinal)..source.IndexOf(
                "private uint GetMipDepthOrArrayLayers",
                StringComparison.Ordinal)];

        Assert.Contains("var readbackBuffer = new GpuTextureReadbackBuffer(_context);", method, StringComparison.Ordinal);
        Assert.Contains("readbackBuffer.TryReadTextureRows(", method, StringComparison.Ordinal);
        Assert.Contains("_context.CleanupPendingResources();", method, StringComparison.Ordinal);
        Assert.DoesNotContain(".BufferMapAsync(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("wgpuDevicePoll", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BufferGetConstMappedRange", method, StringComparison.Ordinal);
        Assert.DoesNotContain("BufferUnmap(readbackBuffer", method, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuBufferReadBytesUsesContextPollingAndQueuedReadbackDisposal()
    {
        string source = File.ReadAllText(FindProGpuBackendSource("GpuBuffer.cs")).Replace("\r\n", "\n");

        Assert.Contains("_context.PollDevice(wait: false)", source, StringComparison.Ordinal);
        Assert.Contains("QueueTemporaryReadbackBufferDisposal(readbackBuffer)", source, StringComparison.Ordinal);
        Assert.Contains("_context.QueueBufferDisposal((IntPtr)buffer)", source, StringComparison.Ordinal);
        Assert.Contains("_context.CleanupPendingResources();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("wgpuDevicePoll", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BufferDestroy(readbackBuffer)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BufferRelease(readbackBuffer)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BufferDestroy(buffer)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BufferRelease(buffer)", source, StringComparison.Ordinal);
    }

    private static string FindProGpuHostControlSource()
    {
        return FindProGpuSource("ProGPU.Avalonia", "ProGpuHostControl.cs");
    }

    private static string FindProGpuAvaloniaSampleSource()
    {
        return FindProGpuSource("ProGPU.Samples.Avalonia", "MainWindow.axaml.cs");
    }

    private static string FindProGpuBackendSource(string fileName)
    {
        return FindProGpuSource("ProGPU.Backend", fileName);
    }

    private static string FindProGpuSource(string projectDirectory, string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            foreach (string candidate in new[]
                     {
                         Path.Combine(directory.FullName, projectDirectory, fileName),
                         Path.Combine(directory.FullName, "src", projectDirectory, fileName)
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"Could not locate {projectDirectory} {fileName}.");
    }
}
