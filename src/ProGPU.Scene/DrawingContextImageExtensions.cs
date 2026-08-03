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
            bool luminanceToAlpha = false,
            Matrix4x4 transform = default,
            ImageEffectSphericalProjection?
                sphericalProjection = null)
        {
            if (texture == null) return;

            var command = new RenderCommand
            {
                Type = RenderCommandType.DrawExtension,
                ExtensionId = CompositorBuiltInExtensions.ImageEffect,
                Texture = texture,
                Rect = rect,
                SrcRect = sourceRect ?? Rect.Empty,
                TextureSamplingMode = samplingMode,
                Transform = transform
            };
            var effect = new ImageEffectCommandData(
                brightness,
                contrast,
                saturation,
                grayscale,
                sepia,
                invert,
                blurSigma,
                maskTexture,
                colorMatrix,
                luminanceToAlpha,
                sphericalProjection:
                    sphericalProjection);
            context.AddImageEffectCommand(command, in effect);
        }

        /// <summary>
        /// Records separate luma and interleaved chroma planes for the shared
        /// image-effect graph. Color-only work stays fused; supported Gaussian
        /// work decodes YUV into its retained horizontal-pass intermediate.
        /// </summary>
        public static void DrawPlanarImageWithEffect(
            this DrawingContext context,
            GpuTexture lumaTexture,
            GpuTexture chromaTexture,
            Rect rect,
            in ImageEffectYuvConversion conversion,
            float brightness = 0f,
            float contrast = 1f,
            float saturation = 1f,
            float grayscale = 0f,
            float sepia = 0f,
            float invert = 0f,
            float blurSigma = 0f,
            GpuTexture? maskTexture = null,
            Rect? sourceRect = null,
            TextureSamplingMode samplingMode =
                TextureSamplingMode.Linear,
            ImageEffectColorMatrix? colorMatrix = null,
            bool luminanceToAlpha = false,
            Matrix4x4 transform = default,
            ImageEffectSphericalProjection?
                sphericalProjection = null)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(lumaTexture);
            ArgumentNullException.ThrowIfNull(chromaTexture);

            var command = new RenderCommand
            {
                Type = RenderCommandType.DrawExtension,
                ExtensionId =
                    CompositorBuiltInExtensions.ImageEffect,
                Texture = lumaTexture,
                Rect = rect,
                SrcRect = sourceRect ?? Rect.Empty,
                TextureSamplingMode = samplingMode,
                Transform = transform
            };
            var effect = new ImageEffectCommandData(
                brightness,
                contrast,
                saturation,
                grayscale,
                sepia,
                invert,
                blurSigma,
                maskTexture,
                colorMatrix,
                luminanceToAlpha,
                chromaTexture,
                conversion,
                sphericalProjection);
            context.AddImageEffectCommand(command, in effect);
        }
    }
}
