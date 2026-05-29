using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Backend;

namespace ProGPU.WinUI.Charts.Renderers
{
    public static class LineRenderer
    {
        public static void Draw(DrawingContext context, LineSeriesConfig ls, LinearScale xScale, LinearScale yScale,
                                Brush brush, Vector4 color, Rect plotArea)
        {
            if (ls.Data == null) return;
            int count = ls.Data.PointCount;
            if (count < 2) return;

            var data = ls.Data!;
            if (ls.Sampling == "lttb" && count > ls.SamplingThreshold)
            {
                data = ChartInteraction.LttbSample(data, ls.SamplingThreshold);
                count = data.PointCount;
            }

            float thickness = (float)(ls.LineStyle?.Width ?? 2.0);

            // Use high performance GPGPU route if count > 5000
            if (count > 5000)
            {
                if (ls.GpuBuffer == null) ls.GpuBuffer = new GpuSeriesBuffer();

                if (ls.GpuBuffer.PointsCount != count || ls.GpuBuffer.Buffer == null ||
                    ls.GpuBuffer.AssociatedData != data || ls.GpuBuffer.AssociatedDataVersion != data.Version)
                {
                    int requiredLength = count * 2;
                    float[] interleaved = ls.GpuBuffer.CachedInterleaved;
                    if (interleaved == null || interleaved.Length != requiredLength)
                    {
                        interleaved = new float[requiredLength];
                        ls.GpuBuffer.CachedInterleaved = interleaved;
                    }

                    int idx = 0;
                    for (int i = 0; i < count; i++)
                    {
                        double x = data.GetX(i);
                        double y = data.GetY(i);
                        if (!double.IsFinite(x) || !double.IsFinite(y))
                        {
                            interleaved[idx++] = float.NaN;
                            interleaved[idx++] = float.NaN;
                        }
                        else
                        {
                            interleaved[idx++] = (float)x;
                            interleaved[idx++] = (float)y;
                        }
                    }
                    ls.GpuBuffer.Upload(interleaved, count);
                    ls.GpuBuffer.AssociatedData = data;
                    ls.GpuBuffer.AssociatedDataVersion = data.Version;
                }

                // Compute GPU scale and translate parameters
                double scaleX = 0.0;
                double translateX = 0.0;
                if (xScale.DomainMax != xScale.DomainMin)
                {
                    scaleX = (xScale.RangeMax - xScale.RangeMin) / (xScale.DomainMax - xScale.DomainMin);
                    translateX = xScale.RangeMin - xScale.DomainMin * scaleX;
                }
                else
                {
                    translateX = (xScale.RangeMin + xScale.RangeMax) / 2.0;
                }

                double scaleY = 0.0;
                double translateY = 0.0;
                if (yScale.DomainMax != yScale.DomainMin)
                {
                    scaleY = (yScale.RangeMax - yScale.RangeMin) / (yScale.DomainMax - yScale.DomainMin);
                    translateY = yScale.RangeMin - yScale.DomainMin * scaleY;
                }
                else
                {
                    translateY = (yScale.RangeMin + yScale.RangeMax) / 2.0;
                }

                var scale = new Vector2((float)scaleX, (float)scaleY);
                var translate = new Vector2((float)translateX, (float)translateY);

                context.DrawGpuLineSeries(ls.GpuBuffer, thickness, brush, scale, translate);
                return;
            }

            var pen = new Pen(brush, thickness);

            var points = new List<Vector2>(count);
            var areaBrush = ls.AreaStyle != null 
                ? new SolidColorBrush(new Vector4(color.X, color.Y, color.Z, (float)(ls.AreaStyle.Opacity * color.W))) 
                : null;
            float baselineY = (float)yScale.Scale(yScale.DomainMin);
            if (!double.IsFinite(baselineY)) baselineY = plotArea.Y + plotArea.Height;

            for (int i = 0; i < count; i++)
            {
                double x = data.GetX(i);
                double y = data.GetY(i);

                if (!double.IsFinite(x) || !double.IsFinite(y))
                {
                    if (!ls.ConnectNulls)
                    {
                        RenderPolylineChunk(context, points, pen);
                        if (areaBrush != null)
                        {
                            RenderAreaOverlayChunk(context, points, baselineY, areaBrush);
                        }
                        points.Clear();
                    }
                    continue;
                }

                float px = (float)xScale.Scale(x);
                float py = (float)yScale.Scale(y);
                points.Add(new Vector2(px, py));
            }

            RenderPolylineChunk(context, points, pen);
            if (areaBrush != null)
            {
                RenderAreaOverlayChunk(context, points, baselineY, areaBrush);
            }
        }

        private static void RenderAreaOverlayChunk(DrawingContext context, List<Vector2> points, float baselineY, SolidColorBrush areaBrush)
        {
            if (points.Count < 2) return;
            for (int i = 0; i < points.Count - 1; i++)
            {
                var p1 = points[i];
                var p2 = points[i + 1];
                var b1 = new Vector2(p1.X, baselineY);
                var b2 = new Vector2(p2.X, baselineY);
                context.FillQuad(areaBrush, p1, p2, b2, b1);
            }
        }

        public static void DrawArea(DrawingContext context, AreaSeriesConfig aes, LinearScale xScale, LinearScale yScale,
                                    Brush brush, Vector4 color, Rect plotArea)
        {
            if (aes.Data == null) return;
            int count = aes.Data.PointCount;
            if (count < 2) return;

            var data = aes.Data!;
            if (aes.Sampling == "lttb" && count > aes.SamplingThreshold)
            {
                data = ChartInteraction.LttbSample(data, aes.SamplingThreshold);
                count = data.PointCount;
            }

            float thickness = 1.5f;
            var pen = new Pen(brush, thickness);

            float baselineY = (float)yScale.Scale(aes.Baseline ?? yScale.DomainMin);
            if (!double.IsFinite(baselineY)) baselineY = plotArea.Y + plotArea.Height;

            var points = new List<Vector2>(count);
            for (int i = 0; i < count; i++)
            {
                double x = data.GetX(i);
                double y = data.GetY(i);

                if (!double.IsFinite(x) || !double.IsFinite(y))
                {
                    if (!aes.ConnectNulls)
                    {
                        RenderAreaChunk(context, points, pen, baselineY, color, aes.AreaStyle?.Opacity ?? 0.2);
                        points.Clear();
                    }
                    continue;
                }

                float px = (float)xScale.Scale(x);
                float py = (float)yScale.Scale(y);
                points.Add(new Vector2(px, py));
            }

            RenderAreaChunk(context, points, pen, baselineY, color, aes.AreaStyle?.Opacity ?? 0.2);
        }

        private static void RenderPolylineChunk(DrawingContext context, List<Vector2> points, Pen pen)
        {
            if (points.Count < 2) return;
            context.DrawPolyline(pen, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points), false);
        }

        private static void RenderAreaChunk(DrawingContext context, List<Vector2> points, Pen pen, float baselineY, Vector4 color, double op)
        {
            if (points.Count < 2) return;

            // Draw line border
            context.DrawPolyline(pen, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points), false);

            // Construct closed area coordinates
            var areaColors = new Vector4(color.X, color.Y, color.Z, (float)(op * color.W));
            var areaBrush = new SolidColorBrush(areaColors);

            for (int i = 0; i < points.Count - 1; i++)
            {
                var p1 = points[i];
                var p2 = points[i + 1];
                var b1 = new Vector2(p1.X, baselineY);
                var b2 = new Vector2(p2.X, baselineY);
                context.FillQuad(areaBrush, p1, p2, b2, b1);
            }
        }
    }
}
