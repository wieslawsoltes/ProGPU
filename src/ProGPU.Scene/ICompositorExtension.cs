using System;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Scene
{
    public interface ICompositorExtension
    {
        // Called during the Compositor's CPU compilation pass with raw parameters for zero-allocation performance
        void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd);

        // Called after CPU compilation and before any compositor render pass.
        // Extensions may prepare retained GPU resources and return a transient
        // draw-call replacement. The compositor restores the original retained
        // draw call after encoding, including when compiled-scene replay is used.
        bool TryPrepareDrawCall(
            Compositor compositor,
            bool isOffscreen,
            in Compositor.CompositorDrawCall drawCall,
            out Compositor.CompositorDrawCall preparedDrawCall)
        {
            preparedDrawCall = drawCall;
            return false;
        }

        // Called during the active WebGPU render pass with raw parameters for zero-allocation performance
        unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc);

        // Optional lifecycle hooks for custom resource management
        void BeginFrame(Compositor compositor) { }
        void EndFrame(Compositor compositor) { }
        void BeginStaticCompile(Compositor compositor, StaticCompilationContext context) { }
        void EndStaticCompile(Compositor compositor, StaticCompilationContext context, DxfStaticBuffer staticBuffer) { }
    }
}
