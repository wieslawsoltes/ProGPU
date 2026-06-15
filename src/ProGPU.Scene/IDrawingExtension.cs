using System;
using System.Numerics;

namespace ProGPU.Scene
{
    public interface IDrawingExtension
    {
        // Called during the Compositor's compilation pass
        void Compile(Compositor compositor, Matrix4x4 transform);

        // Called during the active WebGPU render pass
        unsafe void Render(Compositor compositor, void* renderPassEncoder, bool isOffscreen);
    }

    public static class CompositorBuiltInExtensions
    {
        public const int StaticDxf = 0;
        public const int AcisSolid = 1;
        public const int Line3D = 2;
        public const int Hatch = 3;
        public const int Spline = 4;
        public const int GpuLineSeries = 5;
        public const int GpuScatterSeries = 6;
        public const int CustomGrid = 7;
        public const int Mesh3D = 8;
        public const int PathOps = 9;
        public const int ImageEffect = 10;
        public const int ShaderToy = 11;
        public const int WpfShaderEffect = 12;
    }
}
