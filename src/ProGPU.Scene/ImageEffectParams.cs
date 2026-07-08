using System;
using System.Numerics;
using ProGPU.Backend;

namespace ProGPU.Scene
{
    public readonly struct ImageEffectColorMatrix
    {
        public ImageEffectColorMatrix(
            Vector4 red,
            Vector4 green,
            Vector4 blue,
            Vector4 alpha,
            Vector4 offset)
        {
            Red = red;
            Green = green;
            Blue = blue;
            Alpha = alpha;
            Offset = offset;
        }

        public Vector4 Red { get; }
        public Vector4 Green { get; }
        public Vector4 Blue { get; }
        public Vector4 Alpha { get; }
        public Vector4 Offset { get; }
    }

    public class ImageEffectParams
    {
        public GpuTexture Texture { get; set; } = null!;
        public Rect Rect { get; set; }
        public Rect SourceRect { get; set; }
        public TextureSamplingMode SamplingMode { get; set; } = TextureSamplingMode.Linear;
        public float Brightness { get; set; } = 0f; // Offset [-1, 1]
        public float Contrast { get; set; } = 1f;   // Multiplier [0, 2]
        public float Saturation { get; set; } = 1f; // Multiplier [0, 2]
        public float Grayscale { get; set; } = 0f;  // Weight [0, 1]
        public float Sepia { get; set; } = 0f;      // Weight [0, 1]
        public float Invert { get; set; } = 0f;     // Weight [0, 1]
        public float BlurSigma { get; set; } = 0f;  // Blur amount
        public ImageEffectColorMatrix? ColorMatrix { get; set; }
        public GpuTexture? MaskTexture { get; set; }
        public string? LastError { get; set; }
    }
}
