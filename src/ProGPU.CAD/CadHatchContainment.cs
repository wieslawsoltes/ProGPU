namespace ProGPU.CAD;

internal enum CadHatchPointContainment : byte
{
    Outside = 0,
    Inside = 1,
    Boundary = 2,
    Unsupported = 3,
}

/// <summary>
/// Exact direction-aware half-open containment for one retained HATCH loop.
/// </summary>
internal static class CadHatchContainment
{
    private const double TwoPi = Math.PI * 2.0;

    public static CadHatchPointContainment Classify(
        ReadOnlySpan<CadHatchSegment> segments,
        double queryX,
        double queryY)
    {
        bool parity = false;
        bool boundary = false;
        for (int i = 0; i < segments.Length; i++)
        {
            CadHatchSegment segment = segments[i];
            if (segment.Kind == CadHatchSegmentKind.Line)
            {
                AccumulateLineCrossing(
                    segment.StartX,
                    segment.StartY,
                    segment.EndX,
                    segment.EndY,
                    queryX,
                    queryY,
                    ref parity,
                    ref boundary);
                continue;
            }
            if (!TryAccumulateArcCrossings(
                segment.CenterX,
                segment.CenterY,
                segment.CosineAxisX,
                segment.CosineAxisY,
                segment.SineAxisX,
                segment.SineAxisY,
                segment.StartParameter,
                segment.SweepParameter,
                queryX,
                queryY,
                ref parity,
                ref boundary))
            {
                return CadHatchPointContainment.Unsupported;
            }
        }
        return boundary
            ? CadHatchPointContainment.Boundary
            : parity
                ? CadHatchPointContainment.Inside
                : CadHatchPointContainment.Outside;
    }

    private static void AccumulateLineCrossing(
        double startX,
        double startY,
        double endX,
        double endY,
        double queryX,
        double queryY,
        ref bool parity,
        ref bool boundary)
    {
        double scale = Math.Max(
            1.0,
            Math.Max(
                Math.Max(Math.Abs(startX), Math.Abs(startY)),
                Math.Max(
                    Math.Max(Math.Abs(endX), Math.Abs(endY)),
                    Math.Max(Math.Abs(queryX), Math.Abs(queryY)))));
        double tolerance = 1e-12 * scale;
        double dx = endX - startX;
        double dy = endY - startY;
        double cross = ((queryX - startX) * dy) - ((queryY - startY) * dx);
        double dot = ((queryX - startX) * dx) + ((queryY - startY) * dy);
        double squaredLength = (dx * dx) + (dy * dy);
        if (Math.Abs(cross) <= tolerance * Math.Max(1.0, Math.Abs(dx) + Math.Abs(dy)) &&
            dot >= -tolerance && dot <= squaredLength + tolerance)
        {
            boundary = true;
            return;
        }

        bool upward = dy > 0.0;
        bool crosses = upward
            ? queryY >= startY && queryY < endY
            : dy < 0.0 && queryY > endY && queryY <= startY;
        if (!crosses)
        {
            return;
        }
        double intersectionX = startX + ((queryY - startY) * dx / dy);
        if (intersectionX > queryX)
        {
            parity = !parity;
        }
    }

    private static bool TryAccumulateArcCrossings(
        double centerX,
        double centerY,
        double cosineAxisX,
        double cosineAxisY,
        double sineAxisX,
        double sineAxisY,
        double start,
        double sweep,
        double queryX,
        double queryY,
        ref bool parity,
        ref bool boundary)
    {
        double amplitude = Math.Sqrt(
            (cosineAxisY * cosineAxisY) +
            (sineAxisY * sineAxisY));
        if (!double.IsFinite(amplitude) || amplitude == 0.0)
        {
            return false;
        }
        double normalized = (queryY - centerY) / amplitude;
        double tolerance = 1e-12 * Math.Max(1.0, Math.Abs(normalized));
        if (normalized < -1.0 - tolerance || normalized > 1.0 + tolerance)
        {
            return true;
        }

        normalized = Math.Clamp(normalized, -1.0, 1.0);
        double phase = Math.Atan2(sineAxisY, cosineAxisY);
        double delta = Math.Acos(normalized);
        double first = phase + delta;
        double second = phase - delta;
        if (!TryAccumulateArcRoot(
            centerX,
            centerY,
            cosineAxisX,
            cosineAxisY,
            sineAxisX,
            sineAxisY,
            start,
            sweep,
            first,
            queryX,
            queryY,
            ref parity,
            ref boundary))
        {
            return false;
        }
        if (NormalizePositive(first - second) <= 1e-12 ||
            NormalizePositive(second - first) <= 1e-12)
        {
            return true;
        }
        return TryAccumulateArcRoot(
            centerX,
            centerY,
            cosineAxisX,
            cosineAxisY,
            sineAxisX,
            sineAxisY,
            start,
            sweep,
            second,
            queryX,
            queryY,
            ref parity,
            ref boundary);
    }

    private static bool TryAccumulateArcRoot(
        double centerX,
        double centerY,
        double cosineAxisX,
        double cosineAxisY,
        double sineAxisX,
        double sineAxisY,
        double start,
        double sweep,
        double parameter,
        double queryX,
        double queryY,
        ref bool parity,
        ref bool boundary)
    {
        if (!TryGetProgress(parameter, start, sweep, out double progress))
        {
            return true;
        }
        double cosine = Math.Cos(parameter);
        double sine = Math.Sin(parameter);
        double x = centerX + (cosineAxisX * cosine) + (sineAxisX * sine);
        double y = centerY + (cosineAxisY * cosine) + (sineAxisY * sine);
        double scale = Math.Max(
            1.0,
            Math.Max(
                Math.Max(Math.Abs(x), Math.Abs(y)),
                Math.Max(Math.Abs(queryX), Math.Abs(queryY))));
        double tolerance = 1e-11 * scale;
        if (Math.Abs(x - queryX) <= tolerance && Math.Abs(y - queryY) <= tolerance)
        {
            boundary = true;
            return true;
        }
        double derivativeY =
            (-cosineAxisY * sine) + (sineAxisY * cosine);
        derivativeY *= Math.CopySign(1.0, sweep);
        if (Math.Abs(derivativeY) <= tolerance)
        {
            return true;
        }
        double span = Math.Min(Math.Abs(sweep), TwoPi);
        if (span >= TwoPi - 1e-12 && progress <= 1e-12 && derivativeY < 0.0)
        {
            progress = span;
        }
        bool include = derivativeY > 0.0
            ? progress < span - 1e-12
            : progress > 1e-12;
        if (include && x > queryX)
        {
            parity = !parity;
        }
        return true;
    }

    private static bool TryGetProgress(
        double parameter,
        double start,
        double sweep,
        out double progress)
    {
        double span = Math.Min(Math.Abs(sweep), TwoPi);
        progress = sweep >= 0.0
            ? NormalizePositive(parameter - start)
            : NormalizePositive(start - parameter);
        if (progress <= span + 1e-12)
        {
            progress = Math.Clamp(progress, 0.0, span);
            return true;
        }
        return false;
    }

    private static double NormalizePositive(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }
}
