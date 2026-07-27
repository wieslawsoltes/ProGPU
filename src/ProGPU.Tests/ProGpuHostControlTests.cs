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
    public void SharedImageReadbackIsOptInByDefault()
    {
        var control = new ProGpuHostControl();

        Assert.False(control.EnableSharedImageReadback);
        Assert.False(control.EnableSharedTextureMemory);
#pragma warning disable CS0618
        Assert.False(control.EnableZeroCopy);
#pragma warning restore CS0618
    }

    [Fact]
    public void MacSharedTextureMemoryUsesTypedTimelineSynchronization()
    {
        string source = File.ReadAllText(
            FindProGpuHostControlSource()).Replace(
                "\r\n",
                "\n");

        Assert.Contains(
            "DawnGpuContext.CreateMetalPresentation()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompositionGpuImportedImageSynchronizationCapabilities.TimelineSemaphores",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".UpdateWithTimelineSemaphoresAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "memory.BeginAccess(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "memory.EndAccessAndExportMetalSharedEvent(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_isSameDeviceTextureSupported ||\n            _isSharedTextureMemorySupported",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SharedGpuTextureSource.CompositionHandleType",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await drawingSurface.UpdateAsync(importedImage);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "swapchainImages.Length;",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_currentWriteImageIndex + 1) % 2",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NativeLibrary.",
            source,
            StringComparison.Ordinal);
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
    public void SharedImageReadbackRequiresAutomaticSynchronization()
    {
        string source = File.ReadAllText(FindProGpuHostControlSource()).Replace("\r\n", "\n");

        Assert.Contains(
            "interop.GetSynchronizationCapabilities(handleType)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompositionGpuImportedImageSynchronizationCapabilities.Automatic",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CompositionGpuImportedImageSynchronizationCapabilities.TimelineSemaphores);\n                useSharedTexture =",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HostControlRechecksSharedImageReadbackFrameAfterAsyncMap()
    {
        var guardMethod = typeof(ProGpuHostControl).GetMethod(
            "IsCurrentSharedImageReadbackFrame",
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
        Assert.Equal(7, guardParameters.Length);
        Assert.Equal("swapchainImages", guardParameters[0].Name);
        Assert.Equal("imageIndex", guardParameters[1].Name);
        Assert.Equal("image", guardParameters[2].Name);
        Assert.Equal("transferBuffer", guardParameters[3].Name);
        Assert.Equal("importedImage", guardParameters[4].Name);
        Assert.Equal(typeof(ICompositionImportedGpuImage), guardParameters[4].ParameterType);
        Assert.Equal("drawingSurface", guardParameters[5].Name);
        Assert.Equal(typeof(CompositionDrawingSurface), guardParameters[5].ParameterType);
        Assert.Equal("context", guardParameters[6].Name);
        Assert.Equal(typeof(WgpuContext), guardParameters[6].ParameterType);

        Assert.NotNull(unmapMethod);
        var unmapParameters = unmapMethod.GetParameters();
        Assert.Equal(2, unmapParameters.Length);
        Assert.Equal(typeof(WgpuContext), unmapParameters[0].ParameterType);

        Assert.NotNull(copyMethod);
        var copyParameters = copyMethod.GetParameters();
        Assert.Equal(5, copyParameters.Length);
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
    public void AvaloniaHostUpdatesAdaptiveStatesBeforeFrameLayout()
    {
        string source = File.ReadAllText(FindProGpuHostControlSource()).Replace("\r\n", "\n");

        Assert.Contains(
            "VisualStateManager.UpdateAdaptiveStates(WinuiRoot, hostFrame.LogicalSize);\n        WinuiRoot.Measure(hostFrame.LogicalSize);",
            source,
            StringComparison.Ordinal);
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
        Assert.Contains("RecordPresentedFrame(hostFrame, ProGpuAvaloniaPresentationMode.SharedImageReadback);", source, StringComparison.Ordinal);
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
    public void SharedPresentationImagesUseOneTransferBuffer()
    {
        string source = File.ReadAllText(FindProGpuHostControlSource()).Replace("\r\n", "\n");
        string swapchainImage = source[
            source.IndexOf("private class SwapchainImage", StringComparison.Ordinal)..
            source.IndexOf("private sealed class SharedReadbackTransferBuffer", StringComparison.Ordinal)];

        Assert.Contains(
            "private SharedReadbackTransferBuffer? _sharedReadbackTransferBuffer;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_sharedReadbackTransferBuffer =\n                    new SharedReadbackTransferBuffer(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("StagingBuffer", swapchainImage, StringComparison.Ordinal);
        Assert.DoesNotContain("BytesPerRow", swapchainImage, StringComparison.Ordinal);
    }

    [Fact]
    public void MacSharedSurfaceFreesTemporaryUtf8Keys()
    {
        string source = File.ReadAllText(FindProGpuSource(
            "ProGPU.Avalonia",
            "GpuSharingInterop.cs")).Replace("\r\n", "\n");

        Assert.Contains(
            "IntPtr utf8Key = Marshal.StringToHGlobalAnsi(key);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Marshal.FreeHGlobal(utf8Key);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SharedTransferBufferUnmapsBeforeQueueingDisposal()
    {
        string source = File.ReadAllText(FindProGpuHostControlSource()).Replace("\r\n", "\n");

        int unmapIndex = source.IndexOf(
            "_context.Api.BufferUnmap((GpuBuffer*)buffer)",
            StringComparison.Ordinal);
        int queueIndex = source.IndexOf(
            "_context.QueueBufferDisposal(buffer)",
            StringComparison.Ordinal);

        Assert.Contains("public bool IsMapActive { get; set; }", source, StringComparison.Ordinal);
        Assert.True(unmapIndex >= 0, "The transfer buffer must unmap an active map.");
        Assert.True(queueIndex >= 0, "The transfer buffer must queue deferred disposal.");
        Assert.True(unmapIndex < queueIndex, "Mapped transfer buffers must be unmapped before disposal is queued.");
        Assert.Contains("transferBuffer.IsMapActive = true;", source, StringComparison.Ordinal);
        Assert.Contains("!transferBuffer.IsMapActive", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AvaloniaHostStagingBuffersUseDeferredDisposalQueue()
    {
        string source = File.ReadAllText(FindProGpuHostControlSource()).Replace("\r\n", "\n");
        string readbackSource = File.ReadAllText(FindProGpuBackendSource("GpuTextureReadbackBuffer.cs")).Replace("\r\n", "\n");

        Assert.Contains("_context.QueueBufferDisposal(buffer)", source, StringComparison.Ordinal);
        Assert.Contains("_context.QueueBufferDisposal((IntPtr)_buffer)", readbackSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Wgpu.BufferDestroy((GpuBuffer*)buffer)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Wgpu.BufferRelease((GpuBuffer*)buffer)", source, StringComparison.Ordinal);
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
        Assert.Contains("_context.Api.BufferUnmap(_buffer);", readbackSource, StringComparison.Ordinal);
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
