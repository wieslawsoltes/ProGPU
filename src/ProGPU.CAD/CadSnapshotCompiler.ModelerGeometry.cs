using ACadSharp.Entities;
using CSMath;

namespace ProGPU.CAD;

public sealed partial class CadSnapshotCompiler
{
    /// <summary>
    /// Retains ACadSharp modeler payloads and DWG display wires without
    /// interpreting proprietary face records. Display-wire points are normalized
    /// to WCS once; payload bytes remain byte-exact for later bounded tessellation.
    /// </summary>
    /// <remarks>
    /// This original ProGPU lowering uses only ACadSharp's public
    /// ModelerGeometry/Wire contract. Work and additional storage are O(P + B)
    /// for P display-wire points and B payload bytes. The destination streams are
    /// rolled back transactionally if validation or a configured bound fails.
    /// </remarks>
    private static CadEntityHeader CompileModelerGeometry(
        ModelerGeometry geometry,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        CadSnapshotOptions options,
        List<CadModelerGeometryPrimitive> primitives,
        List<CadModelerGeometryWire> wires,
        List<CadPoint3D> points,
        List<byte> payloadBytes)
    {
        CadModelerGeometryKind kind = geometry switch
        {
            CadBody => CadModelerGeometryKind.Body,
            Region => CadModelerGeometryKind.Region,
            Solid3D => CadModelerGeometryKind.Solid3D,
            _ => throw new CadUnsupportedEntityException(
                $"Modeler geometry type {geometry.GetType().Name} is not defined."),
        };

        byte[] payload = geometry.AcisData ?? Array.Empty<byte>();
        if (payload.Length > options.MaxModelerGeometryPayloadBytesPerEntity)
        {
            throw new CadUnsupportedEntityException(
                $"Modeler payload length {payload.Length} exceeds the configured " +
                $"{options.MaxModelerGeometryPayloadBytesPerEntity}-byte per-entity limit.");
        }
        if (payload.Length > options.MaxModelerGeometryPayloadBytes - payloadBytes.Count)
        {
            throw new CadSnapshotExpansionLimitException(
                $"Retained modeler payloads exceed the configured document limit of " +
                $"{options.MaxModelerGeometryPayloadBytes} bytes.");
        }
        if (geometry.Wires.Count > options.MaxModelerGeometryWires - wires.Count)
        {
            throw new CadSnapshotExpansionLimitException(
                $"Retained modeler display wires exceed the configured document limit of " +
                $"{options.MaxModelerGeometryWires}.");
        }

        int primitiveStart = primitives.Count;
        int wireStart = wires.Count;
        int pointStart = points.Count;
        int payloadStart = payloadBytes.Count;
        CadBounds3D bounds = CadBounds3D.Empty;
        try
        {
            foreach (ModelerGeometry.Wire wire in geometry.Wires)
            {
                if (wire.Points.Count > options.MaxModelerGeometryPoints - points.Count)
                {
                    throw new CadSnapshotExpansionLimitException(
                        $"Retained modeler display-wire points exceed the configured document limit of " +
                        $"{options.MaxModelerGeometryPoints}.");
                }

                int wirePointOffset = points.Count;
                foreach (XYZ source in wire.Points)
                {
                    CadPoint3D point = new(source.X, source.Y, source.Z);
                    if (wire.ApplyTransformPresent)
                    {
                        point = TransformWirePoint(wire, point);
                    }
                    if (hasTransform)
                    {
                        point = transform.TransformPoint(point);
                    }
                    EnsureFinite(point);
                    points.Add(point);
                    bounds = bounds.Include(point);
                }
                wires.Add(new CadModelerGeometryWire(
                    wirePointOffset,
                    wire.Points.Count,
                    wire.SelectionMarker,
                    wire.AcisIndex,
                    wire.Type));
            }

            payloadBytes.AddRange(payload);
            int primitiveIndex = primitives.Count;
            primitives.Add(new CadModelerGeometryPrimitive(
                kind,
                geometry.ModelerFormatVersion,
                wireStart,
                geometry.Wires.Count,
                payloadStart,
                payload.Length,
                geometry.IsBinaryAcisData));
            return new CadEntityHeader(
                handle,
                CadEntityKind.ModelerGeometry,
                layerIndex,
                styleIndex,
                primitiveIndex,
                bounds);
        }
        catch
        {
            primitives.RemoveRange(primitiveStart, primitives.Count - primitiveStart);
            wires.RemoveRange(wireStart, wires.Count - wireStart);
            points.RemoveRange(pointStart, points.Count - pointStart);
            payloadBytes.RemoveRange(payloadStart, payloadBytes.Count - payloadStart);
            throw;
        }
    }

    private static CadPoint3D TransformWirePoint(
        ModelerGeometry.Wire wire,
        CadPoint3D point)
    {
        CadPoint3D xAxis = ToPoint(wire.XAxis);
        CadPoint3D yAxis = ToPoint(wire.YAxis);
        CadPoint3D zAxis = ToPoint(wire.ZAxis);
        CadPoint3D translation = ToPoint(wire.Translation);
        EnsureFinite(xAxis);
        EnsureFinite(yAxis);
        EnsureFinite(zAxis);
        EnsureFinite(translation);
        if (!double.IsFinite(wire.Scale) || wire.Scale == 0.0)
        {
            throw new ArgumentException(
                "A modeler display-wire transform must have finite nonzero scale.");
        }

        return translation +
            (((xAxis * point.X) + (yAxis * point.Y) + (zAxis * point.Z)) * wire.Scale);
    }
}
