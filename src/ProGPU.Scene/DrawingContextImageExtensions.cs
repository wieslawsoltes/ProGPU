using System;
using System.Numerics;
using ProGPU.Backend;

namespace ProGPU.Scene
{
    public static class DrawingContextImageExtensions
    {
        public static void DrawImageWithEffect(
            this DrawingContext context,
            GpuTexture texture,
            Rect rect,
            float brightness = 0f,
            float contrast = 1f,
            float saturation = 1f,
            float grayscale = 0f,
            float sepia = 0f,
            float invert = 0f,
            float blurSigma = 0f,
            GpuTexture? maskTexture = null,
            Rect? sourceRect = null,
            TextureSamplingMode samplingMode = TextureSamplingMode.Linear,
            ImageEffectColorMatrix? colorMatrix = null,
            Matrix4x4 transform = default)
        {
            if (texture == null) return;

            var p = new ImageEffectParams
            {
                Texture = texture,
                Rect = rect,
                SourceRect = sourceRect ?? Rect.Empty,
                SamplingMode = samplingMode,
                Brightness = brightness,
                Contrast = contrast,
                Saturation = saturation,
                Grayscale = grayscale,
                Sepia = sepia,
                Invert = invert,
                BlurSigma = blurSigma,
                MaskTexture = maskTexture,
                ColorMatrix = colorMatrix
            };

            context.DrawExtension(
                CompositorBuiltInExtensions.ImageEffect,
                dataParam: p,
                transform: transform
            );
        }
    }
}
