using System;
using System.Numerics;

#nullable enable
#pragma warning disable IDE0057, IDE0059, IDE0078, IDE0300, IDE0301, IDE0305

namespace ProGPU.Vector;

#if PROGPU_VECTOR_INTERNAL
internal
#else
public
#endif
static class TransformMetrics
{
    public static float GetStrokeScale(Matrix4x4 transform)
    {
        return TryGetStrokeScale(transform, out var scale) ? scale : 1f;
    }

    public static bool TryGetStrokeScale(Matrix4x4 transform, out float scale)
    {
        return TryGetStrokeScales(transform, out scale, out _);
    }

    /// <summary>
    /// Computes the maximum and minimum singular values of the transform's
    /// two-dimensional linear component.
    /// </summary>
    /// <remarks>
    /// The singular values are the exact maximum and minimum scale factors of
    /// the affine transform over all unit directions. The calculation performs
    /// fixed <c>O(1)</c> work without allocation and uses double-precision
    /// intermediates to avoid overflow and cancellation in finite float input.
    /// </remarks>
    public static bool TryGetStrokeScales(
        Matrix4x4 transform,
        out float maximumScale,
        out float minimumScale)
    {
        var a = (double)transform.M11;
        var b = (double)transform.M12;
        var c = (double)transform.M21;
        var d = (double)transform.M22;
        if (!double.IsFinite(a) ||
            !double.IsFinite(b) ||
            !double.IsFinite(c) ||
            !double.IsFinite(d))
        {
            maximumScale = 0f;
            minimumScale = 0f;
            return false;
        }

        var sum = a * a + b * b + c * c + d * d;
        var determinant = (a * d) - (b * c);
        var discriminant = Math.Max(
            0d,
            (sum * sum) - (4d * determinant * determinant));
        var maximum = Math.Sqrt(Math.Max(
            0d,
            (sum + Math.Sqrt(discriminant)) * 0.5d));
        var minimum = Math.Abs(determinant) / maximum;

        maximumScale = (float)maximum;
        minimumScale = (float)minimum;
        if (float.IsFinite(maximumScale) &&
            maximumScale > 0f &&
            float.IsFinite(minimumScale) &&
            minimumScale > 0f)
        {
            return true;
        }

        maximumScale = 0f;
        minimumScale = 0f;
        return false;
    }
}
