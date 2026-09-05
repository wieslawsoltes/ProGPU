using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using CSMath;
using System.Globalization;

namespace ProGPU.CAD;

public sealed partial class CadSnapshotCompiler
{
    private static CadEntityHeader CompileWipeout(
        Wipeout wipeout,
        WipeoutFrameType frameType,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        CadSnapshotOptions options,
        List<CadWipeoutPrimitive> destination,
        List<CadWipeoutClipPoint> clipPoints)
    {
        const ImageDisplayFlags knownFlags =
            ImageDisplayFlags.ShowImage |
            ImageDisplayFlags.ShowNotAlignedImage |
            ImageDisplayFlags.UseClippingBoundary |
            ImageDisplayFlags.TransparencyIsOn;
        if ((wipeout.Flags & ~knownFlags) != 0)
        {
            throw new ArgumentException("A WIPEOUT contains undefined image-display flags.");
        }
        if (!double.IsFinite(wipeout.Size.X) || !double.IsFinite(wipeout.Size.Y) ||
            wipeout.Size.X <= 0.0 || wipeout.Size.Y <= 0.0)
        {
            throw new ArgumentException("A WIPEOUT image size must be finite and positive.");
        }
        if (wipeout.ClipMode is not (ClipMode.Outside or ClipMode.Inside))
        {
            throw new ArgumentException("A WIPEOUT clipping mode is not defined.");
        }

        CadPoint3D origin = ToPoint(wipeout.InsertPoint);
        CadPoint3D uVector = ToPoint(wipeout.UVector);
        CadPoint3D vVector = ToPoint(wipeout.VVector);
        if (hasTransform)
        {
            origin = transform.TransformPoint(origin);
            uVector = transform.TransformVector(uVector);
            vVector = transform.TransformVector(vVector);
        }
        EnsureFinite(origin);
        EnsureFinite(uVector);
        EnsureFinite(vVector);
        CadPoint3D plane = CadPoint3D.Cross(uVector, vVector);
        if (!double.IsFinite(plane.Length) || plane.Length <= 0.0)
        {
            throw new ArgumentException("A WIPEOUT image frame must span a finite nonzero plane.");
        }

        bool isClipped = wipeout.ClippingState &&
            wipeout.Flags.HasFlag(ImageDisplayFlags.UseClippingBoundary);
        int clipPointOffset = clipPoints.Count;
        int clipPointCount = 0;
        if (isClipped)
        {
            clipPointCount = AppendWipeoutClipPoints(
                wipeout,
                options,
                clipPoints);
        }

        try
        {
            bool drawFrame = frameType switch
            {
                WipeoutFrameType.NoDisplayOrPlotted => false,
                WipeoutFrameType.DisplayAndPlotted => true,
                WipeoutFrameType.DisplayNoPlotted =>
                    options.DrawOrderPurpose == CadDrawOrderPurpose.Regeneration,
                _ => throw new ArgumentException("The persisted WIPEOUTFRAME value is not defined."),
            };
            CadColor32 background = options.DrawingBackgroundColor with
            {
                Alpha = byte.MaxValue,
            };
            CadBounds3D bounds = GetWipeoutBounds(
                origin,
                uVector,
                vVector,
                wipeout.Size.X,
                wipeout.Size.Y,
                clipPoints,
                clipPointOffset,
                isClipped && wipeout.ClipMode == ClipMode.Outside
                    ? clipPointCount
                    : 0);
            int primitiveIndex = destination.Count;
            destination.Add(new CadWipeoutPrimitive(
                origin,
                uVector,
                vVector,
                wipeout.Size.X,
                wipeout.Size.Y,
                clipPointOffset,
                clipPointCount,
                isClipped,
                isClipped && wipeout.ClipMode == ClipMode.Inside,
                wipeout.Flags.HasFlag(ImageDisplayFlags.ShowImage),
                wipeout.Flags.HasFlag(ImageDisplayFlags.ShowNotAlignedImage),
                drawFrame,
                background));
            return new CadEntityHeader(
                handle,
                CadEntityKind.Wipeout,
                layerIndex,
                styleIndex,
                primitiveIndex,
                bounds);
        }
        catch
        {
            clipPoints.RemoveRange(
                clipPointOffset,
                clipPoints.Count - clipPointOffset);
            throw;
        }
    }

    private static int AppendWipeoutClipPoints(
        Wipeout wipeout,
        CadSnapshotOptions options,
        List<CadWipeoutClipPoint> destination)
    {
        List<XY> source = wipeout.ClipBoundaryVertices;
        int sourceCount = source.Count;
        bool closesPolygon = sourceCount > 3 && source[0] == source[^1];
        int retainedCount = sourceCount - (closesPolygon ? 1 : 0);
        int requiredCount = retainedCount == 2 ? 4 : retainedCount;
        if (retainedCount < 2)
        {
            throw new ArgumentException("An active WIPEOUT clip requires two rectangle corners or at least three polygon vertices.");
        }
        if (retainedCount != 2 && retainedCount < 3)
        {
            throw new ArgumentException("A polygonal WIPEOUT clip requires at least three vertices.");
        }
        if (requiredCount > options.MaxWipeoutClipVerticesPerEntity)
        {
            throw new CadUnsupportedEntityException(
                $"WIPEOUT clipping exceeds the configured {options.MaxWipeoutClipVerticesPerEntity}-vertex per-entity limit.");
        }
        if (requiredCount > options.MaxWipeoutClipVertices - destination.Count)
        {
            throw new CadSnapshotExpansionLimitException(
                $"Retained WIPEOUT clipping exceeds the configured document limit of {options.MaxWipeoutClipVertices} vertices.");
        }

        int start = destination.Count;
        if (retainedCount == 2)
        {
            XY first = source[0];
            XY second = source[1];
            ValidateClipCoordinate(first, wipeout.Size);
            ValidateClipCoordinate(second, wipeout.Size);
            double minU = Math.Min(first.X, second.X) + 0.5;
            double minV = Math.Min(first.Y, second.Y) + 0.5;
            double maxU = Math.Max(first.X, second.X) + 0.5;
            double maxV = Math.Max(first.Y, second.Y) + 0.5;
            if (minU == maxU || minV == maxV)
            {
                throw new ArgumentException("A rectangular WIPEOUT clip must have nonzero width and height.");
            }
            destination.Add(new CadWipeoutClipPoint(minU, minV));
            destination.Add(new CadWipeoutClipPoint(maxU, minV));
            destination.Add(new CadWipeoutClipPoint(maxU, maxV));
            destination.Add(new CadWipeoutClipPoint(minU, maxV));
        }
        else
        {
            try
            {
                double twiceArea = 0.0;
                for (int i = 0; i < retainedCount; i++)
                {
                    XY current = source[i];
                    XY next = source[(i + 1) % retainedCount];
                    ValidateClipCoordinate(current, wipeout.Size);
                    if (current == next)
                    {
                        throw new ArgumentException("A polygonal WIPEOUT clip contains a collapsed edge.");
                    }
                    twiceArea += (current.X * next.Y) - (next.X * current.Y);
                    destination.Add(new CadWipeoutClipPoint(
                        current.X + 0.5,
                        current.Y + 0.5));
                }
                if (!double.IsFinite(twiceArea) || twiceArea == 0.0)
                {
                    throw new ArgumentException("A polygonal WIPEOUT clip must enclose a finite nonzero area.");
                }
            }
            catch
            {
                destination.RemoveRange(start, destination.Count - start);
                throw;
            }
        }
        return destination.Count - start;
    }

    private static void ValidateClipCoordinate(XY point, XY size)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X < -0.5 || point.Y < -0.5 ||
            point.X > size.X - 0.5 || point.Y > size.Y - 0.5)
        {
            throw new ArgumentException(
                "A WIPEOUT clip vertex must be finite and lie within the image extents.");
        }
    }

    private static CadBounds3D GetWipeoutBounds(
        CadPoint3D origin,
        CadPoint3D uVector,
        CadPoint3D vVector,
        double width,
        double height,
        List<CadWipeoutClipPoint> clipPoints,
        int clipPointOffset,
        int clipPointCount)
    {
        CadBounds3D bounds = CadBounds3D.Empty;
        if (clipPointCount != 0)
        {
            for (int i = 0; i < clipPointCount; i++)
            {
                CadWipeoutClipPoint point = clipPoints[clipPointOffset + i];
                bounds = bounds.Include(
                    origin + (uVector * point.U) + (vVector * point.V));
            }
            return bounds;
        }

        bounds = bounds.Include(origin);
        bounds = bounds.Include(origin + (uVector * width));
        bounds = bounds.Include(origin + (uVector * width) + (vVector * height));
        return bounds.Include(origin + (vVector * height));
    }

    private static WipeoutFrameType ResolveWipeoutFrame(CadDocument document)
    {
        string? value = document.DictionaryVariables.GetValue(
            DictionaryVariable.WipeoutFrame);
        if (value is not null)
        {
            if (int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsed) &&
                parsed is >= (int)WipeoutFrameType.NoDisplayOrPlotted and
                    <= (int)WipeoutFrameType.DisplayNoPlotted)
            {
                return (WipeoutFrameType)parsed;
            }
            throw new ArgumentException(
                $"Persisted WIPEOUTFRAME value '{value}' is not 0, 1, or 2.");
        }

        WipeoutVariables? legacy = document.GetCadObjects<WipeoutVariables>().FirstOrDefault();
        return legacy?.DisplayImageFrame == false
            ? WipeoutFrameType.NoDisplayOrPlotted
            : WipeoutFrameType.DisplayAndPlotted;
    }
}
