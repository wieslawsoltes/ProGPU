using System.Numerics;

namespace ProGPU.Scene.Extensions
{
    public class SplineExtensionPipeline : ICompositorExtension
    {
        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            compositor.CompileSpline(provider, cmd, transform);
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            // Pure compile-time vector primitive.
        }
    }
}
