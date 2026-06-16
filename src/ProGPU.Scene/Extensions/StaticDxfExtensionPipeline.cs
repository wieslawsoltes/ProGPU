using System;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Scene.Extensions
{
    public class StaticDxfExtensionPipeline : ICompositorExtension
    {
        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            // Precompiled DXF static buffer compile pass is a no-op.
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            if (dc.DataParam != null)
            {
                compositor.DrawStaticDxfBuffer(
                    (Silk.NET.WebGPU.RenderPassEncoder*)renderPassEncoder,
                    dc.DataParam,
                    isOffscreen,
                    dc.MaskTexture,
                    dc.BlendMode);
            }
        }
    }
}
