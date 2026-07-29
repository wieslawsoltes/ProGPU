using System.Numerics;

namespace ProGPU.Backend;

/// <summary>
/// Backend-neutral affine straight-RGB transform consumed by retained WebGPU
/// blit resources. Alpha is preserved by the shader.
/// </summary>
public readonly record struct GpuTextureColorTransform
{
    public GpuTextureColorTransform(
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
                "GPU texture color-transform values must be finite.");
        }

        Red = red;
        Green = green;
        Blue = blue;
    }

    public static GpuTextureColorTransform Identity =>
        new(
            new Vector4(1f, 0f, 0f, 0f),
            new Vector4(0f, 1f, 0f, 0f),
            new Vector4(0f, 0f, 1f, 0f));

    public Vector4 Red { get; }

    public Vector4 Green { get; }

    public Vector4 Blue { get; }

    public static GpuTextureColorTransform
        CreateSaturationGrayscale(
            float saturation,
            float grayscale)
    {
        if (!float.IsFinite(saturation) ||
            saturation is < 0f or > 2f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(saturation));
        }
        if (!float.IsFinite(grayscale) ||
            grayscale is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(grayscale));
        }

        const float red = 0.2126f;
        const float green = 0.7152f;
        const float blue = 0.0722f;
        float inverseSaturation = 1f - saturation;
        var saturationTransform =
            new GpuTextureColorTransform(
                new Vector4(
                    red * inverseSaturation + saturation,
                    green * inverseSaturation,
                    blue * inverseSaturation,
                    0f),
                new Vector4(
                    red * inverseSaturation,
                    green * inverseSaturation + saturation,
                    blue * inverseSaturation,
                    0f),
                new Vector4(
                    red * inverseSaturation,
                    green * inverseSaturation,
                    blue * inverseSaturation + saturation,
                    0f));
        float inverseGrayscale = 1f - grayscale;
        var grayscaleTransform =
            new GpuTextureColorTransform(
                new Vector4(
                    inverseGrayscale + red * grayscale,
                    green * grayscale,
                    blue * grayscale,
                    0f),
                new Vector4(
                    red * grayscale,
                    inverseGrayscale + green * grayscale,
                    blue * grayscale,
                    0f),
                new Vector4(
                    red * grayscale,
                    green * grayscale,
                    inverseGrayscale + blue * grayscale,
                    0f));
        return Then(
            saturationTransform,
            grayscaleTransform);
    }

    private static GpuTextureColorTransform Then(
        GpuTextureColorTransform current,
        GpuTextureColorTransform next) =>
        new(
            ComposeRow(current, next.Red),
            ComposeRow(current, next.Green),
            ComposeRow(current, next.Blue));

    private static Vector4 ComposeRow(
        GpuTextureColorTransform current,
        Vector4 next) =>
        new(
            next.X * current.Red.X +
                next.Y * current.Green.X +
                next.Z * current.Blue.X,
            next.X * current.Red.Y +
                next.Y * current.Green.Y +
                next.Z * current.Blue.Y,
            next.X * current.Red.Z +
                next.Y * current.Green.Z +
                next.Z * current.Blue.Z,
            next.X * current.Red.W +
                next.Y * current.Green.W +
                next.Z * current.Blue.W +
                next.W);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}
