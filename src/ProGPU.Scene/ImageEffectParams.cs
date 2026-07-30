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

    /// <summary>
    /// Converts normalized luma/chroma samples into straight RGB. Offset and
    /// scale values encode full/limited range and bit depth; the three rows
    /// encode the selected BT.601, BT.709, or BT.2020 matrix.
    /// </summary>
    public readonly struct ImageEffectYuvConversion
    {
        public ImageEffectYuvConversion(
            Vector4 range,
            Vector4 red,
            Vector4 green,
            Vector4 blue)
        {
            Range = range;
            Red = red;
            Green = green;
            Blue = blue;
        }

        public Vector4 Range { get; }
        public Vector4 Red { get; }
        public Vector4 Green { get; }
        public Vector4 Blue { get; }
    }

    /// <summary>
    /// Maps a perspective output quad into an equirectangular source texture.
    /// The orientation is normalized at the public API boundary.
    /// </summary>
    public readonly struct ImageEffectSphericalProjection
    {
        public ImageEffectSphericalProjection(
            Vector4 sourceUvRect,
            Quaternion viewOrientation,
            float horizontalFieldOfViewRadians,
            float outputAspectRatio)
        {
            SourceUvRect = sourceUvRect;
            ViewOrientation = viewOrientation;
            HorizontalFieldOfViewRadians =
                horizontalFieldOfViewRadians;
            OutputAspectRatio = outputAspectRatio;
        }

        public Vector4 SourceUvRect { get; }
        public Quaternion ViewOrientation { get; }
        public float HorizontalFieldOfViewRadians { get; }
        public float OutputAspectRatio { get; }
    }

    /// <summary>
    /// Immutable, inline image-effect state used by retained render commands.
    /// Keeping this value on the command avoids a managed allocation for every
    /// changing image or video frame.
    /// </summary>
    public readonly struct ImageEffectCommandData
    {
        public ImageEffectCommandData(
            float brightness,
            float contrast,
            float saturation,
            float grayscale,
            float sepia,
            float invert,
            float blurSigma,
            GpuTexture? maskTexture,
            ImageEffectColorMatrix? colorMatrix,
            bool luminanceToAlpha,
            GpuTexture? chromaTexture = null,
            ImageEffectYuvConversion? yuvConversion = null,
            ImageEffectSphericalProjection?
                sphericalProjection = null)
        {
            Brightness = brightness;
            Contrast = contrast;
            Saturation = saturation;
            Grayscale = grayscale;
            Sepia = sepia;
            Invert = invert;
            BlurSigma = blurSigma;
            MaskTexture = maskTexture;
            ColorMatrix = colorMatrix;
            LuminanceToAlpha = luminanceToAlpha;
            ChromaTexture = chromaTexture;
            YuvConversion = yuvConversion;
            SphericalProjection = sphericalProjection;
        }

        public float Brightness { get; }
        public float Contrast { get; }
        public float Saturation { get; }
        public float Grayscale { get; }
        public float Sepia { get; }
        public float Invert { get; }
        public float BlurSigma { get; }
        public GpuTexture? MaskTexture { get; }
        public ImageEffectColorMatrix? ColorMatrix { get; }
        public bool LuminanceToAlpha { get; }
        public GpuTexture? ChromaTexture { get; }
        public ImageEffectYuvConversion? YuvConversion { get; }
        public ImageEffectSphericalProjection?
            SphericalProjection { get; }

        internal ImageEffectCommandData
            WithRgbSourceWithoutBlur()
        {
            return new ImageEffectCommandData(
                Brightness,
                Contrast,
                Saturation,
                Grayscale,
                Sepia,
                Invert,
                0f,
                MaskTexture,
                ColorMatrix,
                LuminanceToAlpha,
                chromaTexture: null,
                yuvConversion: null,
                sphericalProjection:
                    SphericalProjection);
        }
    }

    /// <summary>
    /// Legacy reference payload retained for source compatibility. New drawing
    /// commands use <see cref="ImageEffectCommandData"/> inline.
    /// </summary>
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
        public bool LuminanceToAlpha { get; set; }
        public GpuTexture? MaskTexture { get; set; }
        public string? LastError { get; set; }
    }
}
