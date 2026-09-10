using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using CSMath;

namespace ProGPU.CAD;

public sealed partial class CadSnapshotCompiler
{
    private readonly record struct CadRasterImageDisplaySettings(
        ImageFrameType FrameType,
        ImageDisplayQuality DisplayQuality);

    /// <remarks>
    /// Original ProGPU lowering derived from the repository-owned WIPEOUT
    /// compiler. IMAGE adds immutable IMAGEDEF identity and persisted display
    /// controls while retaining the same Autodesk half-pixel clip convention.
    /// </remarks>
    private static CadEntityHeader CompileRasterImage(
        RasterImage image,
        CadRasterImageDisplaySettings displaySettings,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        CadSnapshotOptions options,
        List<CadRasterImagePrimitive> destination,
        List<CadRasterImageResource> resources,
        Dictionary<ImageDefinition, int> resourceIndices,
        List<CadWipeoutClipPoint> clipPoints)
    {
        const ImageDisplayFlags knownFlags =
            ImageDisplayFlags.ShowImage |
            ImageDisplayFlags.ShowNotAlignedImage |
            ImageDisplayFlags.UseClippingBoundary |
            ImageDisplayFlags.TransparencyIsOn;
        if ((image.Flags & ~knownFlags) != 0)
        {
            throw new ArgumentException("A raster IMAGE contains undefined display flags.");
        }
        if (!double.IsFinite(image.Size.X) || !double.IsFinite(image.Size.Y) ||
            image.Size.X <= 0.0 || image.Size.Y <= 0.0)
        {
            throw new ArgumentException("A raster IMAGE size must be finite and positive.");
        }
        if (image.ClipMode is not (ClipMode.Outside or ClipMode.Inside))
        {
            throw new ArgumentException("A raster IMAGE clipping mode is not defined.");
        }
        if (displaySettings.DisplayQuality is not
            (ImageDisplayQuality.Draft or ImageDisplayQuality.High))
        {
            throw new ArgumentException("RASTERVARIABLES image quality is not defined.");
        }

        ImageDefinition definition = image.Definition ??
            throw new ArgumentException("A raster IMAGE has no IMAGEDEF resource.");
        string fileName = definition.FileName ?? string.Empty;
        if (fileName.Length > options.MaxRasterImagePathCodeUnits)
        {
            throw new CadUnsupportedEntityException(
                $"IMAGEDEF path length exceeds the configured {options.MaxRasterImagePathCodeUnits}-code-unit limit.");
        }
        if (!double.IsFinite(definition.Size.X) || !double.IsFinite(definition.Size.Y) ||
            definition.Size.X <= 0.0 || definition.Size.Y <= 0.0)
        {
            throw new ArgumentException("A raster IMAGE definition size must be finite and positive.");
        }

        CadPoint3D origin = ToPoint(image.InsertPoint);
        CadPoint3D uVector = ToPoint(image.UVector);
        CadPoint3D vVector = ToPoint(image.VVector);
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
            throw new ArgumentException("A raster IMAGE frame must span a finite nonzero plane.");
        }

        bool isClipped = image.ClippingState &&
            image.Flags.HasFlag(ImageDisplayFlags.UseClippingBoundary);
        int clipPointOffset = clipPoints.Count;
        int clipPointCount = 0;
        if (isClipped)
        {
            clipPointCount = AppendRasterImageClipPoints(image, options, clipPoints);
        }

        bool addedResource = false;
        int resourceIndex = -1;
        try
        {
            if (!resourceIndices.TryGetValue(definition, out resourceIndex))
            {
                if (resources.Count >= options.MaxRasterImageResources)
                {
                    throw new CadSnapshotExpansionLimitException(
                        $"Raster IMAGE resources exceed the configured limit of {options.MaxRasterImageResources}.");
                }
                resourceIndex = resources.Count;
                resources.Add(new CadRasterImageResource(
                    definition.Handle,
                    fileName,
                    definition.Size.X,
                    definition.Size.Y,
                    definition.IsLoaded));
                resourceIndices.Add(definition, resourceIndex);
                addedResource = true;
            }

            bool drawFrame = displaySettings.FrameType switch
            {
                ImageFrameType.NoDisplayOrPlotted => false,
                ImageFrameType.DisplayAndPlotted => true,
                ImageFrameType.DisplayNoPlotted =>
                    options.DrawOrderPurpose == CadDrawOrderPurpose.Regeneration,
                _ => throw new ArgumentException(
                    "The persisted RASTERVARIABLES image-frame value is not 0, 1, or 3."),
            };
            CadBounds3D bounds = GetRasterImageBounds(
                origin,
                uVector,
                vVector,
                image.Size.X,
                image.Size.Y,
                clipPoints,
                clipPointOffset,
                isClipped && image.ClipMode == ClipMode.Outside
                    ? clipPointCount
                    : 0);
            int primitiveIndex = destination.Count;
            destination.Add(new CadRasterImagePrimitive(
                origin,
                uVector,
                vVector,
                image.Size.X,
                image.Size.Y,
                clipPointOffset,
                clipPointCount,
                resourceIndex,
                isClipped,
                isClipped && image.ClipMode == ClipMode.Inside,
                image.Flags.HasFlag(ImageDisplayFlags.ShowImage),
                image.Flags.HasFlag(ImageDisplayFlags.ShowNotAlignedImage),
                drawFrame,
                image.Flags.HasFlag(ImageDisplayFlags.TransparencyIsOn),
                displaySettings.DisplayQuality == ImageDisplayQuality.High,
                image.Brightness,
                image.Contrast,
                image.Fade,
                options.DrawingBackgroundColor with { Alpha = byte.MaxValue }));
            return new CadEntityHeader(
                handle,
                CadEntityKind.RasterImage,
                layerIndex,
                styleIndex,
                primitiveIndex,
                bounds);
        }
        catch
        {
            clipPoints.RemoveRange(clipPointOffset, clipPoints.Count - clipPointOffset);
            if (addedResource)
            {
                resourceIndices.Remove(definition);
                resources.RemoveAt(resourceIndex);
            }
            throw;
        }
    }

    private static int AppendRasterImageClipPoints(
        RasterImage image,
        CadSnapshotOptions options,
        List<CadWipeoutClipPoint> destination)
    {
        List<XY> source = image.ClipBoundaryVertices;
        int sourceCount = source.Count;
        bool closesPolygon = sourceCount > 3 && source[0] == source[^1];
        int retainedCount = sourceCount - (closesPolygon ? 1 : 0);
        int requiredCount = retainedCount == 2 ? 4 : retainedCount;
        if (retainedCount < 2)
        {
            throw new ArgumentException(
                "An active raster IMAGE clip requires two rectangle corners or at least three polygon vertices.");
        }
        if (requiredCount > options.MaxRasterImageClipVerticesPerEntity)
        {
            throw new CadUnsupportedEntityException(
                $"Raster IMAGE clipping exceeds the configured {options.MaxRasterImageClipVerticesPerEntity}-vertex per-entity limit.");
        }
        if (requiredCount > options.MaxRasterImageClipVertices - destination.Count)
        {
            throw new CadSnapshotExpansionLimitException(
                $"Retained raster IMAGE clipping exceeds the configured document limit of {options.MaxRasterImageClipVertices} vertices.");
        }

        int start = destination.Count;
        if (retainedCount == 2)
        {
            XY first = source[0];
            XY second = source[1];
            ValidateRasterImageClipCoordinate(first, image.Size);
            ValidateRasterImageClipCoordinate(second, image.Size);
            double minU = Math.Min(first.X, second.X) + 0.5;
            double minV = Math.Min(first.Y, second.Y) + 0.5;
            double maxU = Math.Max(first.X, second.X) + 0.5;
            double maxV = Math.Max(first.Y, second.Y) + 0.5;
            if (minU == maxU || minV == maxV)
            {
                throw new ArgumentException(
                    "A rectangular raster IMAGE clip must have nonzero width and height.");
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
                    ValidateRasterImageClipCoordinate(current, image.Size);
                    if (current == next)
                    {
                        throw new ArgumentException(
                            "A polygonal raster IMAGE clip contains a collapsed edge.");
                    }
                    twiceArea += (current.X * next.Y) - (next.X * current.Y);
                    destination.Add(new CadWipeoutClipPoint(
                        current.X + 0.5,
                        current.Y + 0.5));
                }
                if (!double.IsFinite(twiceArea) || twiceArea == 0.0)
                {
                    throw new ArgumentException(
                        "A polygonal raster IMAGE clip must enclose a finite nonzero area.");
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

    private static void ValidateRasterImageClipCoordinate(XY point, XY size)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X < -0.5 || point.Y < -0.5 ||
            point.X > size.X - 0.5 || point.Y > size.Y - 0.5)
        {
            throw new ArgumentException(
                "A raster IMAGE clip vertex must be finite and lie within the image extents.");
        }
    }

    private static CadBounds3D GetRasterImageBounds(
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

    private static CadRasterImageDisplaySettings ResolveRasterImageDisplaySettings(
        CadDocument document)
    {
        RasterVariables? variables = document.GetCadObjects<RasterVariables>().FirstOrDefault();
        return variables is null
            ? new CadRasterImageDisplaySettings(
                ImageFrameType.DisplayAndPlotted,
                ImageDisplayQuality.High)
            : new CadRasterImageDisplaySettings(
                variables.FrameType,
                variables.DisplayQuality);
    }
}
