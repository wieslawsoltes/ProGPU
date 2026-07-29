namespace ProGPU.Media.Effects;

using System.Numerics;

/// <summary>
/// An affine straight-RGB transform. Each row stores three channel
/// coefficients and one additive offset. Alpha is preserved by contract.
/// </summary>
public readonly record struct MediaVideoColorTransform
{
    public MediaVideoColorTransform(
        Vector4 red,
        Vector4 green,
        Vector4 blue)
    {
        if (!IsFinite(red) ||
            !IsFinite(green) ||
            !IsFinite(blue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(red),
                "Video color-transform values must be finite.");
        }

        Red = red;
        Green = green;
        Blue = blue;
    }

    public static MediaVideoColorTransform Identity =>
        new(
            new Vector4(1f, 0f, 0f, 0f),
            new Vector4(0f, 1f, 0f, 0f),
            new Vector4(0f, 0f, 1f, 0f));

    public Vector4 Red { get; }

    public Vector4 Green { get; }

    public Vector4 Blue { get; }

    public bool IsIdentity =>
        this == Identity;

    /// <summary>
    /// Returns a transform that applies this transform first and
    /// <paramref name="next"/> second. Work and storage are fixed O(1).
    /// </summary>
    public MediaVideoColorTransform Then(
        in MediaVideoColorTransform next) =>
        new(
            ComposeRow(next.Red),
            ComposeRow(next.Green),
            ComposeRow(next.Blue));

    public Vector3 Transform(Vector3 color) =>
        new(
            Dot(Red, color),
            Dot(Green, color),
            Dot(Blue, color));

    private Vector4 ComposeRow(Vector4 next) =>
        new(
            next.X * Red.X +
                next.Y * Green.X +
                next.Z * Blue.X,
            next.X * Red.Y +
                next.Y * Green.Y +
                next.Z * Blue.Y,
            next.X * Red.Z +
                next.Y * Green.Z +
                next.Z * Blue.Z,
            next.X * Red.W +
                next.Y * Green.W +
                next.Z * Blue.W +
                next.W);

    private static float Dot(
        Vector4 row,
        Vector3 color) =>
        row.X * color.X +
        row.Y * color.Y +
        row.Z * color.Z +
        row.W;

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}

public enum MediaVideoGraphEffectKind
{
    ColorTransform = 1
}

/// <summary>
/// Allocation-free snapshot of a portable GPU video-effect node.
/// </summary>
public readonly record struct MediaVideoGraphEffectState
{
    public MediaVideoGraphEffectState(
        MediaVideoGraphEffectKind kind,
        MediaVideoColorTransform colorTransform)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind));
        }

        Kind = kind;
        ColorTransform = colorTransform;
    }

    public MediaVideoGraphEffectKind Kind { get; }

    public MediaVideoColorTransform ColorTransform { get; }
}

/// <summary>
/// A typed video effect that can be snapshotted into a provider-neutral GPU
/// node without reflection, assembly scanning, or managed pixel callbacks.
/// </summary>
public interface IMediaVideoGraphEffect :
    IMediaEffect
{
    MediaVideoGraphEffectState CaptureState();
}

/// <summary>
/// Built-in typed factory for one ordered color-adjustment node. The factory
/// consumes a WinUI-shaped effect property set and emits one affine transform
/// that executes in a provider's retained GPU pass.
/// </summary>
public sealed class MediaVideoColorEffectFactory :
    IMediaEffectFactory
{
    public const string BrightnessPropertyName =
        "Brightness";
    public const string ContrastPropertyName =
        "Contrast";
    public const string SaturationPropertyName =
        "Saturation";
    public const string GrayscalePropertyName =
        "Grayscale";
    public const string SepiaPropertyName =
        "Sepia";
    public const string InvertPropertyName =
        "Invert";

    public MediaVideoColorEffectFactory(
        string activatableClassId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            activatableClassId);
        ActivatableClassId = activatableClassId;
    }

    public string ActivatableClassId { get; }

    public IMediaEffect Create(
        in MediaEffectDescriptor descriptor)
    {
        if (descriptor.Kind != MediaEffectKind.Video)
        {
            throw new ArgumentException(
                "The color effect can be activated only as a video effect.",
                nameof(descriptor));
        }

        float brightness = Read(
            descriptor.Properties,
            BrightnessPropertyName,
            0f,
            -1f,
            1f);
        float contrast = Read(
            descriptor.Properties,
            ContrastPropertyName,
            1f,
            0f,
            2f);
        float saturation = Read(
            descriptor.Properties,
            SaturationPropertyName,
            1f,
            0f,
            2f);
        float grayscale = Read(
            descriptor.Properties,
            GrayscalePropertyName,
            0f,
            0f,
            1f);
        float sepia = Read(
            descriptor.Properties,
            SepiaPropertyName,
            0f,
            0f,
            1f);
        float invert = Read(
            descriptor.Properties,
            InvertPropertyName,
            0f,
            0f,
            1f);

        MediaVideoColorTransform transform =
            CreateTransform(
                brightness,
                contrast,
                saturation,
                grayscale,
                sepia,
                invert);
        return new MediaVideoColorEffect(
            ActivatableClassId,
            transform);
    }

    public static MediaVideoColorTransform
        CreateTransform(
            float brightness = 0f,
            float contrast = 1f,
            float saturation = 1f,
            float grayscale = 0f,
            float sepia = 0f,
            float invert = 0f)
    {
        ValidateRange(
            brightness,
            nameof(brightness),
            -1f,
            1f);
        ValidateRange(
            contrast,
            nameof(contrast),
            0f,
            2f);
        ValidateRange(
            saturation,
            nameof(saturation),
            0f,
            2f);
        ValidateRange(
            grayscale,
            nameof(grayscale),
            0f,
            1f);
        ValidateRange(
            sepia,
            nameof(sepia),
            0f,
            1f);
        ValidateRange(
            invert,
            nameof(invert),
            0f,
            1f);

        MediaVideoColorTransform result =
            MediaVideoColorTransform.Identity;
        result = result.Then(
            Offset(brightness));
        result = result.Then(
            ScaleAroundHalf(contrast));
        result = result.Then(
            Saturation(saturation));
        result = result.Then(
            Grayscale(grayscale));
        result = result.Then(
            Sepia(sepia));
        result = result.Then(
            Invert(invert));
        return result;
    }

    private static MediaVideoColorTransform Offset(
        float value) =>
        new(
            new Vector4(1f, 0f, 0f, value),
            new Vector4(0f, 1f, 0f, value),
            new Vector4(0f, 0f, 1f, value));

    private static MediaVideoColorTransform
        ScaleAroundHalf(float value)
    {
        float offset = 0.5f * (1f - value);
        return new(
            new Vector4(value, 0f, 0f, offset),
            new Vector4(0f, value, 0f, offset),
            new Vector4(0f, 0f, value, offset));
    }

    private static MediaVideoColorTransform Saturation(
        float value)
    {
        const float red = 0.2126f;
        const float green = 0.7152f;
        const float blue = 0.0722f;
        float inverse = 1f - value;
        return new(
            new Vector4(
                red * inverse + value,
                green * inverse,
                blue * inverse,
                0f),
            new Vector4(
                red * inverse,
                green * inverse + value,
                blue * inverse,
                0f),
            new Vector4(
                red * inverse,
                green * inverse,
                blue * inverse + value,
                0f));
    }

    private static MediaVideoColorTransform Grayscale(
        float value)
    {
        const float red = 0.2126f;
        const float green = 0.7152f;
        const float blue = 0.0722f;
        float inverse = 1f - value;
        return new(
            new Vector4(
                inverse + red * value,
                green * value,
                blue * value,
                0f),
            new Vector4(
                red * value,
                inverse + green * value,
                blue * value,
                0f),
            new Vector4(
                red * value,
                green * value,
                inverse + blue * value,
                0f));
    }

    private static MediaVideoColorTransform Sepia(
        float value)
    {
        float inverse = 1f - value;
        return new(
            new Vector4(
                inverse + 0.393f * value,
                0.769f * value,
                0.189f * value,
                0f),
            new Vector4(
                0.349f * value,
                inverse + 0.686f * value,
                0.168f * value,
                0f),
            new Vector4(
                0.272f * value,
                0.534f * value,
                inverse + 0.131f * value,
                0f));
    }

    private static MediaVideoColorTransform Invert(
        float value)
    {
        float scale = 1f - 2f * value;
        return new(
            new Vector4(scale, 0f, 0f, value),
            new Vector4(0f, scale, 0f, value),
            new Vector4(0f, 0f, scale, value));
    }

    private static float Read(
        IReadOnlyDictionary<string, object?> properties,
        string name,
        float fallback,
        float minimum,
        float maximum)
    {
        if (!properties.TryGetValue(
                name,
                out object? value))
        {
            return fallback;
        }

        float number = value switch
        {
            byte typed => typed,
            sbyte typed => typed,
            short typed => typed,
            ushort typed => typed,
            int typed => typed,
            uint typed => typed,
            long typed => typed,
            ulong typed => typed,
            float typed => typed,
            double typed => checked((float)typed),
            decimal typed => checked((float)typed),
            _ => throw new ArgumentException(
                $"'{name}' must be a numeric value.")
        };
        ValidateRange(
            number,
            name,
            minimum,
            maximum);
        return number;
    }

    private static void ValidateRange(
        float value,
        string name,
        float minimum,
        float maximum)
    {
        if (!float.IsFinite(value) ||
            value < minimum ||
            value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private sealed class MediaVideoColorEffect :
        IMediaVideoGraphEffect
    {
        private readonly MediaVideoColorTransform
            _transform;

        public MediaVideoColorEffect(
            string id,
            MediaVideoColorTransform transform)
        {
            Id = id;
            _transform = transform;
        }

        public string Id { get; }

        public MediaEffectKind Kind =>
            MediaEffectKind.Video;

        public MediaVideoGraphEffectState CaptureState() =>
            new(
                MediaVideoGraphEffectKind.ColorTransform,
                _transform);

        public void Dispose()
        {
        }
    }
}
