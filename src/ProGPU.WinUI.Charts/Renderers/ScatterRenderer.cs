using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Backend;

namespace ProGPU.WinUI.Charts.Renderers
{
    public static class ScatterRenderer
    {
        public static void Draw(DrawingContext context, ScatterSeriesConfig scs, LinearScale xScale, LinearScale yScale,
                                Brush brush, Rect plotArea)
        {
            int count = scs.Data!.PointCount;
            if (count == 0) return;

            if (scs.Mode.Equals("density", StringComparison.OrdinalIgnoreCase))
            {
                DrawDensityHeatmap(context, scs, xScale, yScale, brush, plotArea);
                return;
            }

            // High performance GPGPU Route for high-density plots
            if (count > 5000)
            {
                if (scs.GpuBuffer == null) scs.GpuBuffer = new GpuSeriesBuffer();

                float defaultSize = 6.0f;
                if (scs.SymbolSizeConstant.HasValue)
                {
                    defaultSize = (float)scs.SymbolSizeConstant.Value;
                }

                if (scs.GpuBuffer.PointsCount != count || scs.GpuBuffer.Buffer == null ||
                    scs.GpuBuffer.AssociatedData != scs.Data || scs.GpuBuffer.AssociatedDataVersion != scs.Data.Version)
                {
                    int requiredLength = count * 3;
                    float[]? interleaved = scs.GpuBuffer.CachedInterleaved;
                    if (interleaved == null || interleaved.Length != requiredLength)
                    {
                        interleaved = new float[requiredLength];
                        scs.GpuBuffer.CachedInterleaved = interleaved;
                    }

                    int idx = 0;
                    for (int i = 0; i < count; i++)
                    {
                        double x = scs.Data.GetX(i);
                        double y = scs.Data.GetY(i);

                        if (!double.IsFinite(x) || !double.IsFinite(y))
                        {
                            interleaved[idx++] = float.NaN;
                            interleaved[idx++] = float.NaN;
                            interleaved[idx++] = 0f;
                        }
                        else
                        {
                            interleaved[idx++] = (float)x;
                            interleaved[idx++] = (float)y;

                            float size = defaultSize;
                            if (scs.SymbolSizeFunction != null)
                            {
                                size = (float)scs.SymbolSizeFunction(new DataPoint(x, y, scs.Data.GetSize(i)));
                            }
                            interleaved[idx++] = size / 2f;
                        }
                    }
                    scs.GpuBuffer.Upload(interleaved, count);
                    scs.GpuBuffer.AssociatedData = scs.Data;
                    scs.GpuBuffer.AssociatedDataVersion = scs.Data.Version;
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

                context.DrawGpuScatterSeries(scs.GpuBuffer, defaultSize / 2f, brush, scale, translate);
                return;
            }

            for (int i = 0; i < count; i++)
            {
                double x = scs.Data.GetX(i);
                double y = scs.Data.GetY(i);

                if (!double.IsFinite(x) || !double.IsFinite(y)) continue;

                float px = (float)xScale.Scale(x);
                float py = (float)yScale.Scale(y);

                float size = 6.0f; // Default symbolSize
                if (scs.SymbolSizeConstant.HasValue)
                {
                    size = (float)scs.SymbolSizeConstant.Value;
                }
                else if (scs.SymbolSizeFunction != null)
                {
                    size = (float)scs.SymbolSizeFunction(new DataPoint(x, y, scs.Data.GetSize(i)));
                }

                if (scs.Symbol.Equals("rect", StringComparison.OrdinalIgnoreCase))
                {
                    context.DrawRectangle(brush, null, new Rect(px - size / 2f, py - size / 2f, size, size));
                }
                else if (scs.Symbol.Equals("triangle", StringComparison.OrdinalIgnoreCase))
                {
                    var p1 = new Vector2(px, py - size / 2f);
                    var p2 = new Vector2(px - size / 2f, py + size / 2f);
                    var p3 = new Vector2(px + size / 2f, py + size / 2f);
                    context.FillTriangle(brush, p1, p2, p3);
                }
                else
                {
                    context.FillCircle(brush, new Vector2(px, py), size / 2f);
                }
            }
        }

        private static void DrawDensityHeatmap(DrawingContext context, ScatterSeriesConfig scs, LinearScale xScale, LinearScale yScale,
                                               Brush brush, Rect plotArea)
        {
            int count = scs.Data!.PointCount;
            double binSize = scs.BinSize;
            if (binSize <= 1.0) binSize = 4.0;

            // Bins layout grid
            int cols = (int)Math.Ceiling(plotArea.Width / binSize);
            int rows = (int)Math.Ceiling(plotArea.Height / binSize);
            if (cols <= 0 || rows <= 0) return;

            int[,] bins = new int[cols, rows];
            int maxCount = 0;

            for (int i = 0; i < count; i++)
            {
                double x = scs.Data.GetX(i);
                double y = scs.Data.GetY(i);

                if (!double.IsFinite(x) || !double.IsFinite(y)) continue;

                float px = (float)xScale.Scale(x);
                float py = (float)yScale.Scale(y);

                int c = (int)Math.Floor((px - plotArea.X) / binSize);
                int r = (int)Math.Floor((py - plotArea.Y) / binSize);

                if (c >= 0 && c < cols && r >= 0 && r < rows)
                {
                    bins[c, r]++;
                    if (bins[c, r] > maxCount)
                    {
                        maxCount = bins[c, r];
                    }
                }
            }

            if (maxCount == 0) return;

            // Extract base color components to apply density transparency alpha values
            Vector4 baseColor = new Vector4(0f, 0.47f, 0.83f, 1f); // Segoe Blue standard fallback
            if (brush is SolidColorBrush scb)
            {
                baseColor = scb.Color;
            }

            // Draw non-empty binned squares with opacity mapped to density
            for (int c = 0; c < cols; c++)
            {
                for (int r = 0; r < rows; r++)
                {
                    int val = bins[c, r];
                    if (val == 0) continue;

                    // Density Normalization (linear, sqrt, or log)
                    float ratio = 0f;
                    if (scs.DensityNormalization.Equals("sqrt", StringComparison.OrdinalIgnoreCase))
                    {
                        ratio = (float)(Math.Sqrt(val) / Math.Sqrt(maxCount));
                    }
                    else if (scs.DensityNormalization.Equals("log", StringComparison.OrdinalIgnoreCase))
                    {
                        ratio = (float)(Math.Log(val + 1) / Math.Log(maxCount + 1));
                    }
                    else
                    {
                        ratio = (float)val / maxCount;
                    }

                    float px = plotArea.X + c * (float)binSize;
                    float py = plotArea.Y + r * (float)binSize;

                    // Render heatmap binned block with alpha opacity proportional to density ratio
                    var binColor = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, ratio * baseColor.W);
                    var binBrush = new SolidColorBrush(binColor);

                    context.DrawRectangle(binBrush, null, new Rect(px, py, (float)binSize, (float)binSize));
                }
            }
        }
    }
}
