using System;
using System.Drawing.Drawing2D;
using System.Numerics;

namespace System.Drawing;

public partial class Graphics
{
    [Obsolete(
        "Use the Graphics.GetContextInfo overloads that accept arguments for better performance and fewer allocations.",
        DiagnosticId = "SYSLIB0016",
        UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public object GetContextInfo()
    {
        ThrowIfDisposed();
        Matrix3x2 cumulativeTransform = GetCumulativeContextTransform();
        Region cumulativeClip = GetCumulativeContextClip(cumulativeTransform) ?? new Region();
        return new object[] { cumulativeClip, new Matrix(cumulativeTransform) };
    }

    public void GetContextInfo(out PointF offset)
    {
        ThrowIfDisposed();
        Matrix3x2 cumulativeTransform = GetCumulativeContextTransform();
        offset = new PointF(cumulativeTransform.M31, cumulativeTransform.M32);
    }

    public void GetContextInfo(out PointF offset, out Region? clip)
    {
        ThrowIfDisposed();
        Matrix3x2 cumulativeTransform = GetCumulativeContextTransform();
        offset = new PointF(cumulativeTransform.M31, cumulativeTransform.M32);
        clip = GetCumulativeContextClip(cumulativeTransform);
    }

    private Matrix3x2 GetCumulativeContextTransform()
    {
        Matrix3x2 cumulative = _transform.Value;
        for (int index = _savedStates.Count - 1; index >= 0; index--)
        {
            cumulative *= _savedStates[index].Transform;
        }

        return cumulative;
    }

    private Region? GetCumulativeContextClip(Matrix3x2 cumulativeTransform)
    {
        Region? cumulativeClip = null;
        Matrix3x2 currentTransform = _transform.Value;
        AccumulateContextClip(
            ref cumulativeClip,
            _clip,
            _clipContextTransform,
            currentTransform);

        for (int index = _savedStates.Count - 1; index >= 0; index--)
        {
            SavedGraphicsContext saved = _savedStates[index];
            currentTransform *= saved.Transform;
            AccumulateContextClip(
                ref cumulativeClip,
                saved.Clip,
                saved.ClipContextTransform,
                currentTransform);
        }

        // Keep the transform walk shared with the offset/matrix result. This also
        // guards accidental divergence if the saved-context composition changes.
        if (!NearlyEqual(currentTransform, cumulativeTransform))
        {
            cumulativeClip?.Dispose();
            throw new InvalidOperationException("The cumulative graphics context is inconsistent.");
        }

        return cumulativeClip;
    }

    private static void AccumulateContextClip(
        ref Region? cumulativeClip,
        Region? contextClip,
        Matrix3x2 clipContextTransform,
        Matrix3x2 cumulativeTransform)
    {
        if (contextClip is null || contextClip.IsInfiniteForContext())
        {
            return;
        }

        if (!Matrix3x2.Invert(cumulativeTransform, out Matrix3x2 inverseCumulative))
        {
            cumulativeClip?.Dispose();
            cumulativeClip = new Region();
            cumulativeClip.MakeEmpty();
            return;
        }

        Region transformed = contextClip.Clone();
        using (var matrix = new Matrix(clipContextTransform * inverseCumulative))
        {
            transformed.Transform(matrix);
        }

        if (cumulativeClip is null)
        {
            cumulativeClip = transformed;
            return;
        }

        cumulativeClip.Intersect(transformed);
        transformed.Dispose();
    }

    private static bool NearlyEqual(Matrix3x2 left, Matrix3x2 right) =>
        NearlyEqual(left.M11, right.M11)
        && NearlyEqual(left.M12, right.M12)
        && NearlyEqual(left.M21, right.M21)
        && NearlyEqual(left.M22, right.M22)
        && NearlyEqual(left.M31, right.M31)
        && NearlyEqual(left.M32, right.M32);

    private static bool NearlyEqual(float left, float right) =>
        MathF.Abs(left - right) <= 1e-5f * MathF.Max(1f, MathF.Max(MathF.Abs(left), MathF.Abs(right)));
}
