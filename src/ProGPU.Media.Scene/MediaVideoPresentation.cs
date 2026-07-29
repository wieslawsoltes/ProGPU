using System.Numerics;
using ProGPU.Backend;
using ProGPU.Media.Playback;
using ProGPU.Scene;

namespace ProGPU.Media.Rendering;

public enum MediaVideoStretch
{
    None,
    Fill,
    Uniform,
    UniformToFill
}

public enum MediaVideoRotation
{
    None,
    Clockwise90Degrees,
    Clockwise180Degrees,
    Clockwise270Degrees
}

public enum MediaSphericalVideoFrameFormat
{
    None,
    Equirectangular
}

public readonly struct MediaSphericalProjectionOptions
{
    public MediaSphericalProjectionOptions(
        bool isEnabled,
        MediaSphericalVideoFrameFormat frameFormat,
        float horizontalFieldOfViewInDegrees = 120f,
        Quaternion viewOrientation = default)
    {
        if (!float.IsFinite(horizontalFieldOfViewInDegrees) ||
            horizontalFieldOfViewInDegrees <= 0f ||
            horizontalFieldOfViewInDegrees >= 180f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(horizontalFieldOfViewInDegrees));
        }
        if (!Enum.IsDefined(frameFormat))
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameFormat));
        }

        IsEnabled = isEnabled;
        FrameFormat = frameFormat;
        HorizontalFieldOfViewInDegrees =
            horizontalFieldOfViewInDegrees;
        Quaternion orientation = viewOrientation == default
            ? Quaternion.Identity
            : viewOrientation;
        float orientationLengthSquared =
            orientation.LengthSquared();
        if (!float.IsFinite(orientation.X) ||
            !float.IsFinite(orientation.Y) ||
            !float.IsFinite(orientation.Z) ||
            !float.IsFinite(orientation.W) ||
            !float.IsFinite(orientationLengthSquared) ||
            orientationLengthSquared <= float.Epsilon)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewOrientation));
        }

        ViewOrientation = Quaternion.Normalize(orientation);
    }

    public bool IsEnabled { get; }
    public MediaSphericalVideoFrameFormat FrameFormat { get; }
    public float HorizontalFieldOfViewInDegrees { get; }
    public Quaternion ViewOrientation { get; }
    public bool IsActive =>
        IsEnabled &&
        FrameFormat ==
            MediaSphericalVideoFrameFormat.Equirectangular;
}

/// <summary>
/// Immutable GPU post-processing parameters. Identity is a single texture
/// sample; enabled effects use ProGPU.Scene's combined image-effect shader.
/// </summary>
public readonly struct MediaVideoEffectOptions
{
    public MediaVideoEffectOptions()
        : this(
            brightness: 0f,
            contrast: 1f,
            saturation: 1f,
            grayscale: 0f,
            sepia: 0f,
            invert: 0f,
            blurSigma: 0f,
            colorMatrix: null,
            luminanceToAlpha: false,
            samplingMode: TextureSamplingMode.Linear)
    {
    }

    public MediaVideoEffectOptions(
        float brightness = 0f,
        float contrast = 1f,
        float saturation = 1f,
        float grayscale = 0f,
        float sepia = 0f,
        float invert = 0f,
        float blurSigma = 0f,
        ImageEffectColorMatrix? colorMatrix = null,
        bool luminanceToAlpha = false,
        TextureSamplingMode samplingMode = TextureSamplingMode.Linear)
    {
        Brightness = brightness;
        Contrast = contrast;
        Saturation = saturation;
        Grayscale = grayscale;
        Sepia = sepia;
        Invert = invert;
        BlurSigma = blurSigma;
        ColorMatrix = colorMatrix;
        LuminanceToAlpha = luminanceToAlpha;
        SamplingMode = samplingMode;
    }

    public static MediaVideoEffectOptions Identity => new();

    public float Brightness { get; }
    public float Contrast { get; }
    public float Saturation { get; }
    public float Grayscale { get; }
    public float Sepia { get; }
    public float Invert { get; }
    public float BlurSigma { get; }
    public ImageEffectColorMatrix? ColorMatrix { get; }
    public bool LuminanceToAlpha { get; }
    public TextureSamplingMode SamplingMode { get; }

    public bool IsIdentity =>
        Brightness == 0f &&
        Contrast == 1f &&
        Saturation == 1f &&
        Grayscale == 0f &&
        Sepia == 0f &&
        Invert == 0f &&
        BlurSigma == 0f &&
        ColorMatrix is null &&
        !LuminanceToAlpha;
}

/// <summary>
/// Framework-neutral video presentation state. The source rectangle is
/// normalized to [0,1] so it remains stable across adaptive-resolution frames.
/// </summary>
public readonly struct MediaVideoPresentationOptions
{
    public MediaVideoPresentationOptions(
        MediaVideoStretch stretch = MediaVideoStretch.Uniform,
        Vector4 normalizedSourceRect = default,
        MediaVideoRotation rotation = MediaVideoRotation.None,
        bool isMirrored = false,
        MediaVideoEffectOptions effects = default,
        MediaSphericalProjectionOptions
            sphericalProjection = default)
    {
        Stretch = stretch;
        NormalizedSourceRect = normalizedSourceRect == default
            ? new Vector4(0f, 0f, 1f, 1f)
            : normalizedSourceRect;
        Rotation = rotation;
        IsMirrored = isMirrored;
        Effects = effects.Equals(default(MediaVideoEffectOptions))
            ? MediaVideoEffectOptions.Identity
            : effects;
        SphericalProjection = sphericalProjection;
    }

    public MediaVideoStretch Stretch { get; }
    public Vector4 NormalizedSourceRect { get; }
    public MediaVideoRotation Rotation { get; }
    public bool IsMirrored { get; }
    public MediaVideoEffectOptions Effects { get; }
    public MediaSphericalProjectionOptions
        SphericalProjection { get; }
}

/// <summary>
/// Records the latest decoded frame as retained GPU work. The decoded frame is
/// leased until the drawing context releases its commands; no readback or CPU
/// pixel conversion occurs in this layer.
/// </summary>
public static class MediaGpuSurfaceDrawingExtensions
{
    public static bool DrawLatestFrame(
        this DrawingContext context,
        MediaGpuSurface surface,
        WgpuContext requiredContext,
        Rect bounds,
        in MediaVideoPresentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(requiredContext);

        if (bounds.Width <= 0f ||
            bounds.Height <= 0f)
        {
            return false;
        }

        MediaGpuFrameDescriptor descriptor =
            surface.CurrentDescriptor;
        GpuTexture? chromaTexture = null;
        bool isPlanar = false;
        GpuTexture texture;
        if (descriptor.PixelFormat is
                MediaVideoPixelFormat.Nv12 or
                MediaVideoPixelFormat.P010 &&
            surface.TryAcquireGpuPlaneTextureLeases(
                requiredContext,
                out IProGpuTextureLease lumaLease,
                out IProGpuTextureLease chromaLease))
        {
            try
            {
                if (!context.TryRetainTextureLease(
                        lumaLease,
                        requiredContext,
                        out texture))
                {
                    chromaLease.Dispose();
                    return false;
                }
            }
            catch
            {
                chromaLease.Dispose();
                throw;
            }
            if (!context.TryRetainTextureLease(
                    chromaLease,
                    requiredContext,
                    out chromaTexture))
            {
                return false;
            }
            isPlanar = true;
        }
        else if (!context.TryRetainTexture(
                     surface,
                     requiredContext,
                     out texture))
        {
            return false;
        }

        Vector4 normalized = ClampNormalizedRect(
            options.NormalizedSourceRect);
        Rect source = new(
            normalized.X * texture.Width,
            normalized.Y * texture.Height,
            normalized.Z * texture.Width,
            normalized.W * texture.Height);
        Vector2 naturalSize = new(source.Width, source.Height);
        if (options.Rotation is
            MediaVideoRotation.Clockwise90Degrees or
            MediaVideoRotation.Clockwise270Degrees)
        {
            naturalSize = new Vector2(
                naturalSize.Y,
                naturalSize.X);
        }

        Rect destination = CalculateDestination(
            bounds,
            naturalSize,
            options.Stretch);
        bool clip =
            options.Stretch == MediaVideoStretch.UniformToFill;
        if (clip)
        {
            context.PushClip(bounds);
        }

        Matrix4x4 transform = CreatePresentationTransform(
            destination,
            options.Rotation,
            options.IsMirrored,
            out Rect unrotatedDestination);
        MediaVideoEffectOptions effects = options.Effects;
        ImageEffectSphericalProjection? sphericalProjection =
            options.SphericalProjection.IsActive
                ? new ImageEffectSphericalProjection(
                    normalized,
                    options.SphericalProjection.ViewOrientation,
                    options.SphericalProjection
                        .HorizontalFieldOfViewInDegrees *
                        (MathF.PI / 180f),
                    MathF.Max(
                        unrotatedDestination.Width /
                        MathF.Max(
                            unrotatedDestination.Height,
                            float.Epsilon),
                        float.Epsilon))
                : null;
        if (isPlanar)
        {
            ImageEffectYuvConversion conversion =
                GetYuvConversion(descriptor);
            context.DrawPlanarImageWithEffect(
                texture,
                chromaTexture!,
                unrotatedDestination,
                in conversion,
                effects.Brightness,
                effects.Contrast,
                effects.Saturation,
                effects.Grayscale,
                effects.Sepia,
                effects.Invert,
                effects.BlurSigma,
                sourceRect: source,
                samplingMode: effects.SamplingMode,
                colorMatrix: effects.ColorMatrix,
                luminanceToAlpha:
                    effects.LuminanceToAlpha,
                transform: transform,
                sphericalProjection:
                    sphericalProjection);
        }
        else if (effects.IsIdentity &&
                 !sphericalProjection.HasValue)
        {
            context.DrawTexture(
                texture,
                unrotatedDestination,
                source,
                transform);
        }
        else
        {
            context.DrawImageWithEffect(
                texture,
                unrotatedDestination,
                effects.Brightness,
                effects.Contrast,
                effects.Saturation,
                effects.Grayscale,
                effects.Sepia,
                effects.Invert,
                effects.BlurSigma,
                sourceRect: source,
                samplingMode: effects.SamplingMode,
                colorMatrix: effects.ColorMatrix,
                luminanceToAlpha: effects.LuminanceToAlpha,
                transform: transform,
                sphericalProjection:
                    sphericalProjection);
        }

        if (clip)
        {
            context.PopClip();
        }
        return true;
    }

    public static ImageEffectYuvConversion GetYuvConversion(
        MediaGpuFrameDescriptor descriptor)
    {
        bool p010 =
            descriptor.PixelFormat ==
            MediaVideoPixelFormat.P010;
        MediaColorInfo color = descriptor.ColorInfo;
        Vector4 range;
        if (p010)
        {
            const float denominator = 65535f;
            if (color.FullRange)
            {
                range = new Vector4(
                    0f,
                    denominator / (1023f * 64f),
                    (512f * 64f) / denominator,
                    denominator / (1023f * 64f));
            }
            else
            {
                range = new Vector4(
                    (64f * 64f) / denominator,
                    denominator / (876f * 64f),
                    (512f * 64f) / denominator,
                    denominator / (896f * 64f));
            }
        }
        else if (color.FullRange)
        {
            range = new Vector4(
                0f,
                1f,
                128f / 255f,
                1f);
        }
        else
        {
            range = new Vector4(
                16f / 255f,
                255f / 219f,
                128f / 255f,
                255f / 224f);
        }

        return color.Matrix switch
        {
            MediaMatrixCoefficients.Bt601 =>
                new ImageEffectYuvConversion(
                    range,
                    new Vector4(1f, 0f, 1.402f, 0f),
                    new Vector4(
                        1f,
                        -0.344136f,
                        -0.714136f,
                        0f),
                    new Vector4(1f, 1.772f, 0f, 0f)),
            MediaMatrixCoefficients
                .Bt2020NonConstantLuminance =>
                new ImageEffectYuvConversion(
                    range,
                    new Vector4(1f, 0f, 1.4746f, 0f),
                    new Vector4(
                        1f,
                        -0.164553f,
                        -0.571353f,
                        0f),
                    new Vector4(1f, 1.8814f, 0f, 0f)),
            _ => new ImageEffectYuvConversion(
                range,
                new Vector4(1f, 0f, 1.5748f, 0f),
                new Vector4(
                    1f,
                    -0.187324f,
                    -0.468124f,
                    0f),
                new Vector4(1f, 1.8556f, 0f, 0f))
        };
    }

    public static Vector2 GetNaturalSize(
        MediaGpuFrameDescriptor descriptor,
        in MediaVideoPresentationOptions options)
    {
        Vector4 normalized = ClampNormalizedRect(
            options.NormalizedSourceRect);
        float width = descriptor.Width * normalized.Z;
        float height = descriptor.Height * normalized.W;
        return options.Rotation is
            MediaVideoRotation.Clockwise90Degrees or
            MediaVideoRotation.Clockwise270Degrees
                ? new Vector2(height, width)
                : new Vector2(width, height);
    }

    private static Vector4 ClampNormalizedRect(Vector4 value)
    {
        float x = Math.Clamp(value.X, 0f, 1f);
        float y = Math.Clamp(value.Y, 0f, 1f);
        float width = Math.Clamp(value.Z, 0f, 1f - x);
        float height = Math.Clamp(value.W, 0f, 1f - y);
        if (width <= 0f || height <= 0f)
        {
            return new Vector4(0f, 0f, 1f, 1f);
        }
        return new Vector4(x, y, width, height);
    }

    private static Rect CalculateDestination(
        Rect bounds,
        Vector2 naturalSize,
        MediaVideoStretch stretch)
    {
        if (stretch == MediaVideoStretch.Fill)
        {
            return bounds;
        }
        if (stretch == MediaVideoStretch.None)
        {
            return new Rect(
                bounds.X + (bounds.Width - naturalSize.X) * 0.5f,
                bounds.Y + (bounds.Height - naturalSize.Y) * 0.5f,
                naturalSize.X,
                naturalSize.Y);
        }

        float scaleX = bounds.Width / naturalSize.X;
        float scaleY = bounds.Height / naturalSize.Y;
        float scale = stretch == MediaVideoStretch.UniformToFill
            ? Math.Max(scaleX, scaleY)
            : Math.Min(scaleX, scaleY);
        float width = naturalSize.X * scale;
        float height = naturalSize.Y * scale;
        return new Rect(
            bounds.X + (bounds.Width - width) * 0.5f,
            bounds.Y + (bounds.Height - height) * 0.5f,
            width,
            height);
    }

    private static Matrix4x4 CreatePresentationTransform(
        Rect destination,
        MediaVideoRotation rotation,
        bool mirror,
        out Rect unrotatedDestination)
    {
        float centerX =
            destination.X + destination.Width * 0.5f;
        float centerY =
            destination.Y + destination.Height * 0.5f;
        bool quarterTurn = rotation is
            MediaVideoRotation.Clockwise90Degrees or
            MediaVideoRotation.Clockwise270Degrees;
        unrotatedDestination = quarterTurn
            ? new Rect(
                centerX - destination.Height * 0.5f,
                centerY - destination.Width * 0.5f,
                destination.Height,
                destination.Width)
            : destination;

        float radians = rotation switch
        {
            MediaVideoRotation.Clockwise90Degrees =>
                MathF.PI * 0.5f,
            MediaVideoRotation.Clockwise180Degrees =>
                MathF.PI,
            MediaVideoRotation.Clockwise270Degrees =>
                MathF.PI * 1.5f,
            _ => 0f
        };
        Matrix4x4 result = Matrix4x4.Identity;
        if (radians != 0f)
        {
            result =
                Matrix4x4.CreateTranslation(
                    -centerX,
                    -centerY,
                    0f) *
                Matrix4x4.CreateRotationZ(radians) *
                Matrix4x4.CreateTranslation(
                    centerX,
                    centerY,
                    0f);
        }
        if (mirror)
        {
            result *=
                Matrix4x4.CreateTranslation(
                    -centerX,
                    -centerY,
                    0f) *
                Matrix4x4.CreateScale(-1f, 1f, 1f) *
                Matrix4x4.CreateTranslation(
                    centerX,
                    centerY,
                    0f);
        }
        return result;
    }
}
