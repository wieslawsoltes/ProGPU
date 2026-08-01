using System.Reflection;
using System.Runtime.CompilerServices;
using ProGPU.Scene;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkCanvasOwnershipCompatibilityTests
{
    [Fact]
    public void CanvasUsesSkObjectLifetimeAndClearsItsHandleIdempotently()
    {
        var canvas = new SKCanvas(new DrawingContext(), 16f, 16f);

        Assert.IsAssignableFrom<SKObject>(canvas);
        Assert.NotEqual(IntPtr.Zero, canvas.Handle);

        canvas.Dispose();
        canvas.Dispose();

        Assert.Equal(IntPtr.Zero, canvas.Handle);
    }

    [Fact]
    public void ClipOverloadsExposeTheOfficialNonAntialiasedDefault()
    {
        var methods = typeof(SKCanvas).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var clipRect = Assert.Single(methods, static method =>
            method.Name == nameof(SKCanvas.ClipRect) && method.GetParameters().Length == 3);
        var clipPath = Assert.Single(methods, static method =>
            method.Name == nameof(SKCanvas.ClipPath) && method.GetParameters().Length == 3);

        Assert.Equal(false, clipRect.GetParameters()[2].DefaultValue);
        Assert.Equal(false, clipPath.GetParameters()[2].DefaultValue);
    }

    [Fact]
    public void RecordingContextHierarchyAndBackendValuesMatchNative()
    {
        Assert.True(typeof(SKObject).IsAssignableFrom(typeof(GRRecordingContext)));
        Assert.True(typeof(GRRecordingContext).IsAssignableFrom(typeof(GRContext)));
        Assert.Equal(0, (int)GRBackend.Metal);
        Assert.Equal(1, (int)GRBackend.OpenGL);
        Assert.Equal(2, (int)GRBackend.Vulkan);
        Assert.Equal(3, (int)GRBackend.Dawn);
        Assert.Equal(4, (int)GRBackend.Direct3D);
        Assert.Equal(5, (int)GRBackend.Unsupported);
    }

    [Fact]
    public void StandaloneRetainedCanvasHasNoNativeSurfaceOrRecordingContext()
    {
        var drawingContext = new DrawingContext();
        using var canvas = new SKCanvas(drawingContext, 100f, 80f);

        Assert.Null(canvas.Surface);
        Assert.Null(canvas.Context);
        Assert.Same(drawingContext, canvas.DrawingContext);
    }

    [Fact]
    public void AttachedOwnersAreReturnedByIdentityAndCanDetach()
    {
        var drawingContext = new DrawingContext();
        using var canvas = new SKCanvas(drawingContext, 100f, 80f);
        var surface = (SKSurface)RuntimeHelpers.GetUninitializedObject(typeof(SKSurface));
        var recordingContext = (GRRecordingContext)RuntimeHelpers.GetUninitializedObject(
            typeof(GRRecordingContext));

        canvas.AttachSurface(surface);
        canvas.AttachRecordingContext(recordingContext);
        Assert.Same(surface, canvas.Surface);
        Assert.Same(recordingContext, canvas.Context);

        canvas.DetachSurface(surface);
        canvas.AttachRecordingContext(null);
        Assert.Null(canvas.Surface);
        Assert.Null(canvas.Context);
    }
}
