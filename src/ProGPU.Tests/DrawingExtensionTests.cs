using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class DrawingExtensionTests
{
    private sealed class Payload;
    private sealed unsafe class Probe : ICompositorExtension, IDisposable
    {
        public int Compiles, Renders, Begins, Ends, Disposals;
        public object? Data;
        public void Compile(Compositor compositor, IRenderDataProvider? provider, Matrix4x4 transform, ref RenderCommand command) { Compiles++; Data = command.DataParam; }
        public void Render(Compositor compositor, void* pass, bool offscreen, in Compositor.CompositorDrawCall call) { Renders++; Data = call.DataParam; }
        public void BeginFrame(Compositor compositor) => Begins++;
        public void EndFrame(Compositor compositor) => Ends++;
        public void Dispose() => Disposals++;
    }

    [Fact]
    public void TypedRecordingPreservesPayloadBoundsAndTransformWithoutAllocation()
    {
        var definition = new DrawingExtension<Payload>("fixture", static () => new Probe());
        var data = new Payload(); var bounds = new Rect(10, 20, 30, 40);
        var matrix = Matrix4x4.CreateTranslation(3, 7, 0);
        var context = new DrawingContext(); context.EnsureCommandCapacity(1);
        void Record() { context.Clear(); context.DrawExtension(definition, bounds, data, matrix); }
        for (int i = 0; i < 1000; i++) Record();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) Record();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawExtension, command.Type);
        Assert.Same(data, command.DataParam); Assert.Equal(bounds, command.Rect); Assert.Equal(matrix, command.Transform);
        var other = new DrawingExtension<object>("fixture", static () => new Probe());
        context.DrawExtension(other, bounds, data);
        Assert.NotEqual(command.ExtensionId, context.Commands[1].ExtensionId);
        Assert.Throws<ArgumentNullException>(() => context.DrawExtension(definition, bounds, null!));
    }

    [Fact]
    public void RegistrationIsIdempotentAndUsesExistingCompileRenderAndDisposeLifecycle()
    {
        using var window = new HeadlessWindow(32, 32);
        int factories = 0;
        var definition = new DrawingExtension<Payload>("fixture", () => { factories++; return new Probe(); });
        Assert.Null(window.Compositor.GetDrawingExtension(definition));
        var probe = Assert.IsType<Probe>(window.Compositor.RegisterDrawingExtension(definition));
        Assert.Same(probe, window.Compositor.RegisterDrawingExtension(definition));
        Assert.Same(probe, window.Compositor.GetDrawingExtension(definition)); Assert.Equal(1, factories);
        var data = new Payload(); var visual = new DrawingVisual { Size = new(32, 32) };
        visual.Context.DrawExtension(definition, new(0, 0, 32, 32), data);
        using var target = new GpuTexture(window.Context, 32, 32, Silk.NET.WebGPU.TextureFormat.Rgba8Unorm,
            Silk.NET.WebGPU.TextureUsage.RenderAttachment | Silk.NET.WebGPU.TextureUsage.TextureBinding);
        unsafe { for (int i = 0; i < 3; i++) window.Compositor.RenderScene(visual, 32, 32, 32, 32, 1, target.ViewPtr); }
        Assert.Equal(3, probe.Renders); Assert.Same(data, probe.Data); Assert.Equal(probe.Begins, probe.Ends);
        window.Compositor.Dispose(); window.Compositor.Dispose(); Assert.Equal(1, probe.Disposals);
        Assert.Throws<ObjectDisposedException>(() => window.Compositor.RegisterDrawingExtension(definition));
    }

    [Fact]
    public void NativePictureCompilerStillRejectsApplicationCallbacksExplicitly()
    {
        var definition = new DrawingExtension<Payload>("managed callback", static () => new Probe());
        var drawing = new DrawingContext(); drawing.DrawExtension(definition, new(0, 0, 32, 32), new Payload());
        using var picture = new GpuPicture(drawing.Commands.ToArray(), [], [], [], []);
        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(picture, 1, 1, out var compiled, out var failure));
        Assert.Null(compiled); Assert.Equal(NativePictureCompileError.UnsupportedCommand, failure.Error);
    }

    [Fact]
    public void WindowDefersFactoryAndRecreatesInstanceAfterMobileSurfaceSuspension()
    {
        var window = new Window(); int factories = 0;
        var definition = new DrawingExtension<Payload>("fixture", () => { factories++; return new Probe(); });
        window.RegisterDrawingExtension(definition); window.RegisterDrawingExtension(definition);
        Assert.Equal(0, factories);
        using var context = new WgpuContext(); context.Initialize(null);
        window.InitializeExternalRenderer(context, 1);
        var first = Assert.IsType<Probe>(window.Compositor!.GetDrawingExtension(definition));
        Assert.Equal(1, factories);
        window.RegisterDrawingExtension(definition); Assert.Equal(1, factories);
        window.SuspendExternalRenderer(); Assert.Equal(1, first.Disposals);
        window.InitializeExternalRenderer(context, 2);
        var second = Assert.IsType<Probe>(window.Compositor!.GetDrawingExtension(definition));
        Assert.NotSame(first, second); Assert.Equal(2, factories);
        window.SuspendExternalRenderer(); Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public void FailedFactoryDoesNotPoisonRegistrationAndLateWindowRegistrationWorks()
    {
        var window = new Window(); using var context = new WgpuContext(); context.Initialize(null);
        window.InitializeExternalRenderer(context, 1);
        bool fail = true;
        var definition = new DrawingExtension<Payload>("retry", () => fail ? throw new InvalidOperationException("fixture") : new Probe());
        Assert.Throws<InvalidOperationException>(() => window.RegisterDrawingExtension(definition));
        Assert.Null(window.Compositor!.GetDrawingExtension(definition));
        fail = false; window.RegisterDrawingExtension(definition);
        var probe = Assert.IsType<Probe>(window.Compositor.GetDrawingExtension(definition));
        window.SuspendExternalRenderer(); Assert.Equal(1, probe.Disposals);
    }
}
