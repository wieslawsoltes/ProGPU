using System.Numerics;

namespace ProGPU.CAD;

internal sealed class CadUnsupportedEntityException : Exception
{
    public CadUnsupportedEntityException(string message)
        : base(message)
    {
    }
}

internal readonly record struct CadMesh3DFaceSource(
    CadPoint3D[] Points,
    Vector2[] TextureCoordinates,
    int LayerIndex,
    int StyleIndex,
    bool AllowNonPlanarQuad);

internal sealed class CadMesh3DBuildResult
{
    public required CadMesh3DVertex[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public required CadMesh3DDrawRange[] DrawRanges { get; init; }
    public required CadBounds3D Bounds { get; init; }
}

/// <summary>
/// Deterministic clean-room CAD face validation and flat-shaded triangulation.
/// </summary>
/// <remarks>
/// A triangle or non-planar quadrilateral has fixed O(1) work. A simple planar
/// N-gon uses dominant-axis projection and deterministic ear removal in O(N^2)
/// worst-case time and O(N) scratch. Output uses three vertices per triangle so
/// every triangle retains its exact flat normal without smoothing across a CAD
/// face boundary.
/// </remarks>
internal static class CadMesh3DTopology
{
    private const double RelativeTolerance = 1e-11;
    private readonly record struct ProjectedPoint(double X, double Y);

    public static CadMesh3DBuildResult Build(
        IReadOnlyList<CadMesh3DFaceSource> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        var vertices = new List<CadMesh3DVertex>();
        var indices = new List<uint>();
        var ranges = new List<CadMesh3DDrawRange>(faces.Count);
        CadBounds3D bounds = CadBounds3D.Empty;

        for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
        {
            CadMesh3DFaceSource face = faces[faceIndex];
            CadPoint3D[] points = face.Points ?? throw new ArgumentException(
                "A mesh face has no point array.",
                nameof(faces));
            Vector2[] textureCoordinates = face.TextureCoordinates ??
                throw new ArgumentException(
                    "A mesh face has no texture-coordinate array.",
                    nameof(faces));
            if (points.Length < 3)
            {
                throw new ArgumentException(
                    "A retained mesh face requires at least three vertices.",
                    nameof(faces));
            }
            if (textureCoordinates.Length != 0 &&
                textureCoordinates.Length != points.Length)
            {
                throw new ArgumentException(
                    "A mesh face texture-coordinate count must be zero or match its vertex count.",
                    nameof(faces));
            }

            int[] triangles = Triangulate(points, face.AllowNonPlanarQuad);
            int vertexOffset = vertices.Count;
            int indexOffset = indices.Count;
            for (int triangle = 0; triangle < triangles.Length; triangle += 3)
            {
                CadPoint3D first = points[triangles[triangle]];
                CadPoint3D second = points[triangles[triangle + 1]];
                CadPoint3D third = points[triangles[triangle + 2]];
                if (!TryComputeFlatNormal(first, second, third, out CadPoint3D normal))
                {
                    throw new ArgumentException(
                        "A retained mesh triangle is geometrically degenerate.",
                        nameof(faces));
                }

                AppendVertex(triangles[triangle], first, normal);
                AppendVertex(triangles[triangle + 1], second, normal);
                AppendVertex(triangles[triangle + 2], third, normal);
            }

            int vertexCount = vertices.Count - vertexOffset;
            for (int index = 0; index < vertexCount; index++)
            {
                indices.Add(checked((uint)index));
            }
            ranges.Add(new CadMesh3DDrawRange(
                face.LayerIndex,
                face.StyleIndex,
                vertexOffset,
                vertexCount,
                indexOffset,
                vertexCount));

            void AppendVertex(
                int sourceIndex,
                CadPoint3D position,
                CadPoint3D normal)
            {
                Vector2 textureCoordinate = textureCoordinates.Length == 0
                    ? Vector2.Zero
                    : textureCoordinates[sourceIndex];
                if (!float.IsFinite(textureCoordinate.X) ||
                    !float.IsFinite(textureCoordinate.Y))
                {
                    throw new ArgumentException(
                        "A mesh texture coordinate must be finite.",
                        nameof(faces));
                }
                vertices.Add(new CadMesh3DVertex(
                    position,
                    normal,
                    textureCoordinate));
                bounds = bounds.Include(position);
            }
        }

        if (ranges.Count == 0 || bounds.IsEmpty)
        {
            throw new ArgumentException(
                "A retained mesh requires at least one drawable face.",
                nameof(faces));
        }

        return new CadMesh3DBuildResult
        {
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray(),
            DrawRanges = ranges.ToArray(),
            Bounds = bounds,
        };
    }

    internal static bool TryComputeFlatNormal(
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third,
        out CadPoint3D normal)
    {
        normal = CadPoint3D.Cross(second - first, third - first);
        double length = normal.Length;
        double scale = FaceScale(first, second, third);
        if (!double.IsFinite(length) ||
            length <= Math.Max(1.0, scale * scale) * RelativeTolerance)
        {
            normal = default;
            return false;
        }
        normal /= length;
        return true;
    }

    private static int[] Triangulate(
        ReadOnlySpan<CadPoint3D> points,
        bool allowNonPlanarQuad)
    {
        if (points.Length == 3)
        {
            return [0, 1, 2];
        }

        CadPoint3D newell = ComputeNewellNormal(points);
        double scale = FaceScale(points);
        double normalLength = newell.Length;
        if (!double.IsFinite(normalLength) ||
            normalLength <= Math.Max(1.0, scale * scale) * RelativeTolerance)
        {
            throw new ArgumentException("A mesh face has zero projected area.");
        }
        CadPoint3D unitNormal = newell / normalLength;
        double planeTolerance = Math.Max(1.0, scale) * RelativeTolerance;
        bool planar = true;
        for (int i = 1; i < points.Length; i++)
        {
            double distance = Math.Abs(CadPoint3D.Dot(
                points[i] - points[0],
                unitNormal));
            if (!double.IsFinite(distance) || distance > planeTolerance)
            {
                planar = false;
                break;
            }
        }

        if (!planar)
        {
            if (points.Length == 4 && allowNonPlanarQuad)
            {
                return [0, 1, 2, 0, 2, 3];
            }
            throw new CadUnsupportedEntityException(
                "A non-planar mesh face with more than three vertices has no unambiguous persisted triangulation.");
        }

        ProjectedPoint[] projected = ProjectDominantAxis(points, unitNormal);
        ValidateSimplePolygon(projected, scale);
        return EarClip(projected, scale);
    }

    private static CadPoint3D ComputeNewellNormal(
        ReadOnlySpan<CadPoint3D> points)
    {
        double x = 0.0;
        double y = 0.0;
        double z = 0.0;
        CadPoint3D origin = points[0];
        for (int i = 0; i < points.Length; i++)
        {
            CadPoint3D current = points[i] - origin;
            CadPoint3D next = points[(i + 1) % points.Length] - origin;
            x += (current.Y - next.Y) * (current.Z + next.Z);
            y += (current.Z - next.Z) * (current.X + next.X);
            z += (current.X - next.X) * (current.Y + next.Y);
        }
        return new CadPoint3D(x, y, z);
    }

    private static ProjectedPoint[] ProjectDominantAxis(
        ReadOnlySpan<CadPoint3D> points,
        CadPoint3D normal)
    {
        double x = Math.Abs(normal.X);
        double y = Math.Abs(normal.Y);
        double z = Math.Abs(normal.Z);
        var projected = new ProjectedPoint[points.Length];
        CadPoint3D origin = points[0];
        for (int i = 0; i < points.Length; i++)
        {
            CadPoint3D point = points[i] - origin;
            projected[i] = x >= y && x >= z
                ? new ProjectedPoint(point.Y, point.Z)
                : y >= z
                    ? new ProjectedPoint(point.X, point.Z)
                    : new ProjectedPoint(point.X, point.Y);
        }
        return projected;
    }

    private static void ValidateSimplePolygon(
        ReadOnlySpan<ProjectedPoint> points,
        double scale)
    {
        double areaEpsilon = Math.Max(1.0, scale * scale) * RelativeTolerance;
        double coordinateEpsilon = Math.Max(1.0, scale) * RelativeTolerance;
        double distanceSquaredEpsilon = coordinateEpsilon * coordinateEpsilon;
        for (int first = 0; first < points.Length; first++)
        {
            int firstNext = (first + 1) % points.Length;
            double deltaX = points[first].X - points[firstNext].X;
            double deltaY = points[first].Y - points[firstNext].Y;
            if ((deltaX * deltaX) + (deltaY * deltaY) <= distanceSquaredEpsilon)
            {
                throw new ArgumentException(
                    "A mesh face contains a collapsed projected edge.");
            }
            for (int second = first + 1; second < points.Length; second++)
            {
                int secondNext = (second + 1) % points.Length;
                if (first == second || firstNext == second ||
                    secondNext == first)
                {
                    continue;
                }
                if (SegmentsIntersect(
                        points[first],
                        points[firstNext],
                        points[second],
                        points[secondNext],
                        areaEpsilon,
                        coordinateEpsilon))
                {
                    throw new ArgumentException(
                        "A mesh face contains a self-intersecting projected boundary.");
                }
            }
        }
    }

    private static int[] EarClip(
        ReadOnlySpan<ProjectedPoint> points,
        double scale)
    {
        double signedArea = 0.0;
        for (int i = 0; i < points.Length; i++)
        {
            ProjectedPoint current = points[i];
            ProjectedPoint next = points[(i + 1) % points.Length];
            signedArea += ((double)current.X * next.Y) -
                ((double)current.Y * next.X);
        }
        double epsilon = Math.Max(1.0, scale * scale) * RelativeTolerance;
        if (!double.IsFinite(signedArea) || Math.Abs(signedArea) <= epsilon)
        {
            throw new ArgumentException("A mesh face has zero projected area.");
        }
        double winding = Math.Sign(signedArea);
        var remaining = new List<int>(points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            remaining.Add(i);
        }
        var triangles = new int[checked((points.Length - 2) * 3)];
        int output = 0;

        while (remaining.Count > 3)
        {
            bool removed = false;
            for (int cursor = 0; cursor < remaining.Count; cursor++)
            {
                int previous = remaining[(cursor + remaining.Count - 1) % remaining.Count];
                int current = remaining[cursor];
                int next = remaining[(cursor + 1) % remaining.Count];
                double corner = Cross(points[previous], points[current], points[next]);
                if ((corner * winding) <= epsilon)
                {
                    continue;
                }

                bool contains = false;
                for (int candidateIndex = 0;
                     candidateIndex < remaining.Count;
                     candidateIndex++)
                {
                    int candidate = remaining[candidateIndex];
                    if (candidate == previous || candidate == current || candidate == next)
                    {
                        continue;
                    }
                    if (PointInTriangle(
                            points[candidate],
                            points[previous],
                            points[current],
                            points[next],
                            winding,
                            epsilon))
                    {
                        contains = true;
                        break;
                    }
                }
                if (contains)
                {
                    continue;
                }

                triangles[output++] = previous;
                triangles[output++] = current;
                triangles[output++] = next;
                remaining.RemoveAt(cursor);
                removed = true;
                break;
            }
            if (!removed)
            {
                throw new ArgumentException(
                    "A mesh face cannot be triangulated as a simple planar polygon.");
            }
        }

        triangles[output++] = remaining[0];
        triangles[output++] = remaining[1];
        triangles[output] = remaining[2];
        return triangles;
    }

    private static bool SegmentsIntersect(
        ProjectedPoint first,
        ProjectedPoint firstEnd,
        ProjectedPoint second,
        ProjectedPoint secondEnd,
        double areaEpsilon,
        double coordinateEpsilon)
    {
        double a = Cross(first, firstEnd, second);
        double b = Cross(first, firstEnd, secondEnd);
        double c = Cross(second, secondEnd, first);
        double d = Cross(second, secondEnd, firstEnd);
        if (((a > areaEpsilon && b < -areaEpsilon) ||
             (a < -areaEpsilon && b > areaEpsilon)) &&
            ((c > areaEpsilon && d < -areaEpsilon) ||
             (c < -areaEpsilon && d > areaEpsilon)))
        {
            return true;
        }
        return (Math.Abs(a) <= areaEpsilon &&
                OnSegment(first, firstEnd, second, coordinateEpsilon)) ||
            (Math.Abs(b) <= areaEpsilon &&
                OnSegment(first, firstEnd, secondEnd, coordinateEpsilon)) ||
            (Math.Abs(c) <= areaEpsilon &&
                OnSegment(second, secondEnd, first, coordinateEpsilon)) ||
            (Math.Abs(d) <= areaEpsilon &&
                OnSegment(second, secondEnd, firstEnd, coordinateEpsilon));
    }

    private static bool OnSegment(
        ProjectedPoint start,
        ProjectedPoint end,
        ProjectedPoint point,
        double epsilon) =>
        point.X >= Math.Min(start.X, end.X) - epsilon &&
        point.X <= Math.Max(start.X, end.X) + epsilon &&
        point.Y >= Math.Min(start.Y, end.Y) - epsilon &&
        point.Y <= Math.Max(start.Y, end.Y) + epsilon;

    private static bool PointInTriangle(
        ProjectedPoint point,
        ProjectedPoint first,
        ProjectedPoint second,
        ProjectedPoint third,
        double winding,
        double epsilon) =>
        Cross(first, second, point) * winding >= -epsilon &&
        Cross(second, third, point) * winding >= -epsilon &&
        Cross(third, first, point) * winding >= -epsilon;

    private static double Cross(
        ProjectedPoint first,
        ProjectedPoint second,
        ProjectedPoint third) =>
        ((double)second.X - first.X) * ((double)third.Y - first.Y) -
        ((double)second.Y - first.Y) * ((double)third.X - first.X);

    private static double FaceScale(ReadOnlySpan<CadPoint3D> points)
    {
        CadBounds3D bounds = CadBounds3D.Empty;
        for (int i = 0; i < points.Length; i++)
        {
            bounds = bounds.Include(points[i]);
        }
        return Math.Max(
            Math.Max(bounds.Max.X - bounds.Min.X, bounds.Max.Y - bounds.Min.Y),
            bounds.Max.Z - bounds.Min.Z);
    }

    private static double FaceScale(
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third) =>
        Math.Max(
            Math.Max((second - first).Length, (third - first).Length),
            (third - second).Length);
}
