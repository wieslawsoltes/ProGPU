namespace ProGPU.CAD;

/// <summary>Bounded real-root isolation for one double-precision Bernstein polynomial.</summary>
/// <remarks>
/// The implementation applies Descartes sign variation and scalar de Casteljau
/// bisection on [0,1]. Degree is capped at 29, which covers the stationary-point
/// polynomial of a degree-10 rational Bezier curve. It uses O(D^2 log(1/e))
/// arithmetic in the ordinary isolated-root case, O(D log(1/e)) stack storage,
/// and an explicit node cap for clustered or numerically unresolved roots.
/// </remarks>
internal static class CadBernsteinPolynomial
{
    public const int MaximumDegree = 29;
    private const int MaximumDepth = 52;
    private const int MaximumNodeCount = 16_384;
    private const double ParameterTolerance = 2.220446049250313e-16 * 16.0;
    private const double RelativeZeroTolerance = 2.220446049250313e-16 * 2.0;

    public static bool TryCollectRoots(
        ReadOnlySpan<double> coefficients,
        Span<double> destination,
        out int rootCount)
    {
        rootCount = 0;
        int degree = coefficients.Length - 1;
        if (degree < 0 || degree > MaximumDegree || destination.Length < degree)
        {
            return false;
        }

        double scale = 0.0;
        for (int i = 0; i < coefficients.Length; i++)
        {
            if (!double.IsFinite(coefficients[i]))
            {
                return false;
            }
            scale = Math.Max(scale, Math.Abs(coefficients[i]));
        }
        if (degree == 0 || scale == 0.0)
        {
            return true;
        }

        double zeroTolerance = scale * RelativeZeroTolerance * coefficients.Length;
        if (Math.Abs(coefficients[0]) <= zeroTolerance &&
            !TryAddRoot(0.0, destination, ref rootCount))
        {
            return false;
        }
        if (Math.Abs(coefficients[^1]) <= zeroTolerance &&
            !TryAddRoot(1.0, destination, ref rootCount))
        {
            return false;
        }

        int nodeCount = 0;
        if (!CollectRoots(
                coefficients,
                0.0,
                1.0,
                zeroTolerance,
                depth: 0,
                destination,
                ref rootCount,
                ref nodeCount))
        {
            rootCount = 0;
            return false;
        }

        InsertionSort(destination[..rootCount]);
        return true;
    }

    private static bool CollectRoots(
        ReadOnlySpan<double> coefficients,
        double start,
        double end,
        double zeroTolerance,
        int depth,
        Span<double> destination,
        ref int rootCount,
        ref int nodeCount)
    {
        if (++nodeCount > MaximumNodeCount)
        {
            return false;
        }

        int variations = CountSignVariations(coefficients, zeroTolerance);
        if (variations == 0)
        {
            return true;
        }

        if (depth >= MaximumDepth || end - start <= ParameterTolerance)
        {
            return variations == 1 &&
                TryAddRoot(
                    (start * 0.5) + (end * 0.5),
                    destination,
                    ref rootCount);
        }

        int coefficientCount = coefficients.Length;
        Span<double> left = stackalloc double[MaximumDegree + 1];
        Span<double> right = stackalloc double[MaximumDegree + 1];
        SubdivideHalf(
            coefficients,
            left[..coefficientCount],
            right[..coefficientCount]);
        double middle = (start * 0.5) + (end * 0.5);
        int leftVariations = CountSignVariations(
            left[..coefficientCount],
            zeroTolerance);
        int rightVariations = CountSignVariations(
            right[..coefficientCount],
            zeroTolerance);

        if (!CollectRoots(
                left[..coefficientCount],
                start,
                middle,
                zeroTolerance,
                depth + 1,
                destination,
                ref rootCount,
                ref nodeCount))
        {
            return false;
        }

        // A root exactly on the subdivision boundary disappears from both open
        // child intervals. Descartes variation drops by its multiplicity there.
        if (leftVariations + rightVariations < variations &&
            Math.Abs(left[coefficientCount - 1]) <= zeroTolerance &&
            !TryAddRoot(middle, destination, ref rootCount))
        {
            return false;
        }

        return CollectRoots(
            right[..coefficientCount],
            middle,
            end,
            zeroTolerance,
            depth + 1,
            destination,
            ref rootCount,
            ref nodeCount);
    }

    private static int CountSignVariations(
        ReadOnlySpan<double> coefficients,
        double zeroTolerance)
    {
        int previousSign = 0;
        int variations = 0;
        for (int i = 0; i < coefficients.Length; i++)
        {
            double value = coefficients[i];
            int sign = value > zeroTolerance
                ? 1
                : value < -zeroTolerance
                    ? -1
                    : 0;
            if (sign == 0)
            {
                continue;
            }
            if (previousSign != 0 && sign != previousSign)
            {
                variations++;
            }
            previousSign = sign;
        }
        return variations;
    }

    private static void SubdivideHalf(
        ReadOnlySpan<double> source,
        Span<double> left,
        Span<double> right)
    {
        int degree = source.Length - 1;
        Span<double> work = stackalloc double[MaximumDegree + 1];
        source.CopyTo(work);
        left[0] = work[0];
        right[degree] = work[degree];
        for (int level = 1; level <= degree; level++)
        {
            for (int i = 0; i <= degree - level; i++)
            {
                work[i] = (work[i] * 0.5) + (work[i + 1] * 0.5);
            }
            left[level] = work[0];
            right[degree - level] = work[degree - level];
        }
    }

    private static bool TryAddRoot(
        double root,
        Span<double> destination,
        ref int rootCount)
    {
        double mergeTolerance = ParameterTolerance * 8.0;
        for (int i = 0; i < rootCount; i++)
        {
            if (Math.Abs(destination[i] - root) <= mergeTolerance)
            {
                destination[i] = (destination[i] * 0.5) + (root * 0.5);
                return true;
            }
        }
        if (rootCount >= destination.Length)
        {
            return false;
        }
        destination[rootCount++] = Math.Clamp(root, 0.0, 1.0);
        return true;
    }

    private static void InsertionSort(Span<double> values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            double value = values[i];
            int destination = i;
            while (destination > 0 && values[destination - 1] > value)
            {
                values[destination] = values[destination - 1];
                destination--;
            }
            values[destination] = value;
        }
    }
}
