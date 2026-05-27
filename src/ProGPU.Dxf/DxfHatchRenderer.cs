using System;
using System.Collections.Generic;
using System.Numerics;
using netDxf.Entities;
using ProGPU.Vector;

namespace ProGPU.Dxf;

public class DxfHatchRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not Hatch hatch) return;
        if (hatch.BoundaryPaths == null) return;

        var combined = DxfDocumentRenderer.GetOcsMatrix(hatch.Normal) * transform;

        bool isSolid = hatch.Pattern == null || 
                       string.Equals(hatch.Pattern.Name, "SOLID", StringComparison.OrdinalIgnoreCase);

        var brush = context.GetCachedBrush(hatch);
        var brushColor = (brush is SolidColorBrush solidBrush) ? solidBrush.Color : new Vector4(1f, 1f, 1f, 1f);
        var pen = context.GetCachedPen(hatch, 1f);

        // Render solid fill
        if (isSolid)
        {
            foreach (var bp in hatch.BoundaryPaths)
            {
                var pts = GetBoundaryPoints(bp, context, combined);
                if (pts.Count >= 3)
                {
                    var p0 = pts[0];
                    for (int i = 1; i < pts.Count - 1; i++)
                    {
                        context.DrawingContext.FillTriangle(brush, p0, pts[i], pts[i + 1]);
                    }
                }
            }
            return;
        }

        // Render pattern fill
        bool useGpuShader = false;
        if (hatch.Pattern.LineDefinitions.Count == 1)
        {
            var lineFam = hatch.Pattern.LineDefinitions[0];
            if (lineFam.DashPattern == null || lineFam.DashPattern.Count == 0)
            {
                useGpuShader = true;
            }
        }

        if (useGpuShader)
        {
            // Upgrade to GPU procedural hatch brush
            var lineFam = hatch.Pattern.LineDefinitions[0];
            float angleRad = (float)(lineFam.Angle * Math.PI / 180.0);
            
            // In DXF, Delta.Y is the perpendicular distance between lines if Delta.X is 0
            float spacing = (float)Math.Sqrt(lineFam.Delta.X * lineFam.Delta.X + lineFam.Delta.Y * lineFam.Delta.Y);
            if (spacing < 1e-4f) spacing = 5.0f; // Default spacing

            float lineThickness = 1.0f; // Default pixel line thickness
            var hatchBrush = new HatchPatternBrush(angleRad, spacing, lineThickness, brushColor)
            {
                Opacity = brush.Opacity
            };

            foreach (var bp in hatch.BoundaryPaths)
            {
                var pts = GetBoundaryPoints(bp, context, combined);
                if (pts.Count >= 3)
                {
                    var p0 = pts[0];
                    for (int i = 1; i < pts.Count - 1; i++)
                    {
                        context.DrawingContext.FillTriangle(hatchBrush, p0, pts[i], pts[i + 1]);
                    }
                }
            }
        }
        else
        {
            // CPU Line Pattern Generator Fallback
            foreach (var bp in hatch.BoundaryPaths)
            {
                var pts = GetBoundaryPoints(bp, context, combined);
                if (pts.Count < 3) continue;

                // Collect local model-space polygon vertices
                var localPts = new List<Vector2>();
                if (bp.Entities != null)
                {
                    foreach (var ent in bp.Entities)
                    {
                        if (ent is Line line)
                        {
                            localPts.Add(new Vector2((float)line.StartPoint.X, (float)line.StartPoint.Y));
                        }
                        else if (ent is Arc arc)
                        {
                            var center = new Vector2((float)arc.Center.X, (float)arc.Center.Y);
                            float radius = (float)arc.Radius;
                            float startAng = (float)(arc.StartAngle * Math.PI / 180.0);
                            float endAng = (float)(arc.EndAngle * Math.PI / 180.0);
                            if (endAng < startAng) endAng += 2f * MathF.PI;

                            int steps = 16;
                            for (int i = 0; i <= steps; i++)
                            {
                                float angle = startAng + (endAng - startAng) * (i / (float)steps);
                                localPts.Add(center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius));
                            }
                        }
                        else if (ent is LwPolyline lw)
                        {
                            foreach (var v in lw.Vertexes)
                            {
                                localPts.Add(new Vector2((float)v.Position.X, (float)v.Position.Y));
                            }
                        }
                        else if (ent is Polyline poly)
                        {
                            foreach (var v in poly.Vertexes)
                            {
                                localPts.Add(new Vector2((float)v.Position.X, (float)v.Position.Y));
                            }
                        }
                    }
                }

                // If localPts is empty, fall back to transformed points (mapped back)
                if (localPts.Count < 3)
                {
                    Matrix4x4.Invert(combined, out var inv);
                    foreach (var p in pts)
                    {
                        localPts.Add(Vector2.Transform(p, inv));
                    }
                }

                // Remove duplicate vertices
                for (int i = localPts.Count - 1; i > 0; i--)
                {
                    if (Vector2.Distance(localPts[i], localPts[i - 1]) < 1e-4f)
                    {
                        localPts.RemoveAt(i);
                    }
                }
                if (localPts.Count >= 3 && Vector2.Distance(localPts[localPts.Count - 1], localPts[0]) < 1e-4f)
                {
                    localPts.RemoveAt(localPts.Count - 1);
                }

                if (localPts.Count < 3) continue;

                foreach (var lineFam in hatch.Pattern.LineDefinitions)
                {
                    double angleRad = lineFam.Angle * Math.PI / 180.0;
                    var d = new Vector2((float)Math.Cos(angleRad), (float)Math.Sin(angleRad));
                    var n = new Vector2(-d.Y, d.X); // Perpendicular

                    // Project all polygon vertices onto the normal to find k range
                    var basePt = new Vector2((float)lineFam.Origin.X, (float)lineFam.Origin.Y);
                    var offset = new Vector2((float)lineFam.Delta.X, (float)lineFam.Delta.Y);

                    // Compute denominator for projection
                    float denomProj = Vector2.Dot(offset, n);
                    if (Math.Abs(denomProj) < 1e-5f) denomProj = 5.0f; // Default spacing fallback

                    float minK = float.MaxValue;
                    float maxK = float.MinValue;

                    foreach (var pt in localPts)
                    {
                        float kVal = Vector2.Dot(pt - basePt, n) / denomProj;
                        minK = Math.Min(minK, kVal);
                        maxK = Math.Max(maxK, kVal);
                    }

                    int startK = (int)Math.Floor(minK) - 1;
                    int endK = (int)Math.Ceiling(maxK) + 1;

                    for (int k = startK; k <= endK; k++)
                    {
                        var pBase = basePt + k * offset;
                        var intersections = new List<float>();

                        // Intersect line P(t) = pBase + t * d with all polygon segments
                        for (int i = 0; i < localPts.Count; i++)
                        {
                            var A = localPts[i];
                            var B = localPts[(i + 1) % localPts.Count];
                            var v = B - A;

                            float det = d.X * (-v.Y) - d.Y * (-v.X);
                            if (Math.Abs(det) < 1e-6f) continue; // Parallel

                            float t = ((A.X - pBase.X) * (-v.Y) - (A.Y - pBase.Y) * (-v.X)) / det;
                            float u = (d.X * (A.Y - pBase.Y) - d.Y * (A.X - pBase.X)) / det;

                            if (u >= 0f && u <= 1f)
                            {
                                intersections.Add(t);
                            }
                        }

                        if (intersections.Count < 2) continue;

                        intersections.Sort();

                        // Pairwise intersections represent inside segments
                        for (int i = 0; i < intersections.Count - 1; i += 2)
                        {
                            float t0 = intersections[i];
                            float t1 = intersections[i + 1];

                            if (lineFam.DashPattern == null || lineFam.DashPattern.Count == 0)
                            {
                                // Draw solid line family segment
                                var startPt = context.Transform(pBase + t0 * d, combined);
                                var endPt = context.Transform(pBase + t1 * d, combined);
                                context.DrawingContext.DrawLine(pen, startPt, endPt);
                            }
                            else
                            {
                                // Draw dashed line family segment
                                DrawDashedSegment(pBase, d, t0, t1, lineFam.DashPattern, pen, context, combined);
                            }
                        }
                    }
                }
            }
        }

        // Draw boundaries outline as thin lines
        foreach (var bp in hatch.BoundaryPaths)
        {
            if (bp.Entities == null) continue;
            foreach (var childEntity in bp.Entities)
            {
                if (childEntity.Layer == null)
                {
                    childEntity.Layer = hatch.Layer;
                }
                DxfDocumentRenderer.RenderEntity(childEntity, context, transform);
            }
        }
    }

    private void DrawDashedSegment(Vector2 pBase, Vector2 d, float t0, float t1, IList<double> dashes, Pen pen, DxfRenderContext context, Matrix4x4 combined)
    {
        float totalLength = 0f;
        foreach (var dash in dashes)
        {
            totalLength += (float)Math.Abs(dash);
        }
        if (totalLength < 1e-4f) return;

        float segmentStart = t0;
        float segmentEnd = t1;

        // Pattern cycle alignment: find how many cycles before segmentStart
        float currentT = segmentStart;
        
        while (currentT < segmentEnd)
        {
            // Determine relative position inside pattern cycle
            float relativeT = currentT >= 0f ? (currentT % totalLength) : (totalLength + (currentT % totalLength));
            if (relativeT >= totalLength) relativeT -= totalLength;

            // Find which dash segment we are in
            float accum = 0f;
            int dashIdx = 0;
            float currentDashLen = 0f;
            bool isPenDown = true;

            for (int i = 0; i < dashes.Count; i++)
            {
                float len = (float)Math.Abs(dashes[i]);
                if (relativeT >= accum && relativeT < accum + len)
                {
                    dashIdx = i;
                    currentDashLen = len;
                    isPenDown = dashes[i] >= 0.0; // Positive is dash (pen down), negative is space (pen up)
                    break;
                }
                accum += len;
            }

            float remainingInDash = (accum + currentDashLen) - relativeT;
            float step = Math.Min(remainingInDash, segmentEnd - currentT);

            if (isPenDown)
            {
                var startPt = context.Transform(pBase + currentT * d, combined);
                var endPt = context.Transform(pBase + (currentT + step) * d, combined);
                context.DrawingContext.DrawLine(pen, startPt, endPt);
            }

            currentT += step;
        }
    }

    private List<Vector2> GetBoundaryPoints(HatchBoundaryPath bp, DxfRenderContext context, Matrix4x4 combined)
    {
        var points = new List<Vector2>();
        if (bp.Entities == null) return points;

        foreach (var ent in bp.Entities)
        {
            if (ent is Line line)
            {
                var p1 = context.Transform(new Vector2((float)line.StartPoint.X, (float)line.StartPoint.Y), combined);
                points.Add(p1);
            }
            else if (ent is Arc arc)
            {
                var center = new Vector2((float)arc.Center.X, (float)arc.Center.Y);
                float radius = (float)arc.Radius;
                float startAng = (float)(arc.StartAngle * Math.PI / 180.0);
                float endAng = (float)(arc.EndAngle * Math.PI / 180.0);
                if (endAng < startAng) endAng += 2f * MathF.PI;

                int steps = 16;
                for (int i = 0; i <= steps; i++)
                {
                    float angle = startAng + (endAng - startAng) * (i / (float)steps);
                    var p = context.Transform(center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius), combined);
                    points.Add(p);
                }
            }
            else if (ent is LwPolyline lw)
            {
                foreach (var vertex in lw.Vertexes)
                {
                    var p = context.Transform(new Vector2((float)vertex.Position.X, (float)vertex.Position.Y), combined);
                    points.Add(p);
                }
            }
            else if (ent is Polyline poly)
            {
                foreach (var vertex in poly.Vertexes)
                {
                    var p = context.Transform(new Vector2((float)vertex.Position.X, (float)vertex.Position.Y), combined);
                    points.Add(p);
                }
            }
        }

        // Deduplicate adjacent vertices
        for (int i = points.Count - 1; i > 0; i--)
        {
            if (Vector2.Distance(points[i], points[i - 1]) < 1e-3f)
            {
                points.RemoveAt(i);
            }
        }
        return points;
    }
}
