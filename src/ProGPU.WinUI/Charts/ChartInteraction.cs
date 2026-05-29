using System;
using System.Collections.Generic;

namespace ProGPU.WinUI.Charts
{
    public class NearestPointMatch
    {
        public int SeriesIndex { get; set; }
        public int DataIndex { get; set; }
        public DataPoint Point { get; set; }
        public double Distance { get; set; }
    }

    public class PointsAtXMatch
    {
        public int SeriesIndex { get; set; }
        public int DataIndex { get; set; }
        public DataPoint Point { get; set; }
    }

    public class CandlestickMatch
    {
        public int SeriesIndex { get; set; }
        public int DataIndex { get; set; }
        public OHLCDataPoint Point { get; set; }
    }

    public class PieSliceMatch
    {
        public int SeriesIndex { get; set; }
        public int DataIndex { get; set; }
        public PieDataItem Slice { get; set; } = null!;
    }

    public struct BarBounds
    {
        public double Left { get; set; }
        public double Right { get; set; }
        public double Top { get; set; }
        public double Bottom { get; set; }
    }

    public class BarClusterSlots
    {
        public int[] ClusterIndexBySeries { get; set; } = Array.Empty<int>();
        public int ClusterCount { get; set; }
        public string[] StackIdBySeries { get; set; } = Array.Empty<string>();
    }

    public class BarLayoutPx
    {
        public double CategoryStep { get; set; }
        public double CategoryWidthPx { get; set; }
        public double BarWidthPx { get; set; }
        public double GapPx { get; set; }
        public double ClusterWidthPx { get; set; }
        public BarClusterSlots ClusterSlots { get; set; } = new BarClusterSlots();
    }

    public class BarHitTestLayout
    {
        public double BarWidth { get; set; }
        public double Gap { get; set; }
        public double ClusterWidth { get; set; }
        public Dictionary<int, int> ClusterIndexByGlobalSeriesIndex { get; set; } = new Dictionary<int, int>();
    }

    /// <summary>
    /// Premium C# Chart hit-testing, alignment point matching, and LTTB/nearest index selector logic.
    /// Perfectly mirrors the ChartGPU interaction modules with zero dynamic alloc.
    /// </summary>
    public static class ChartInteraction
    {
        private const double DEFAULT_MAX_DISTANCE_PX = 20.0;
        private const double DEFAULT_BAR_GAP = 0.01;
        private const double DEFAULT_BAR_CATEGORY_GAP = 0.2;
        private const double DEFAULT_SCATTER_RADIUS_CSS_PX = 4.0;

        /// <summary>
        /// Validates if Cartesian X coordinate data is monotonic non-decreasing with finite numbers.
        /// </summary>
        public static bool IsMonotonicNonDecreasingFiniteX(CartesianSeriesData data)
        {
            int n = data.PointCount;
            if (n == 0) return true;

            double prevX = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                double x = data.GetX(i);
                if (!double.IsFinite(x) || x < prevX)
                {
                    return false;
                }
                prevX = x;
            }
            return true;
        }

        /// <summary>
        /// Validates if OHLC timestamp data is monotonic non-decreasing with finite numbers.
        /// </summary>
        public static bool IsMonotonicNonDecreasingFiniteTimestamp(IReadOnlyList<OHLCDataPoint> data)
        {
            int n = data.Count;
            if (n == 0) return true;

            double prevT = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                double t = data[i].Timestamp;
                if (!double.IsFinite(t) || t < prevT)
                {
                    return false;
                }
                prevT = t;
            }
            return true;
        }

        private static int LowerBoundX(CartesianSeriesData data, double xTarget)
        {
            int lo = 0;
            int hi = data.PointCount;
            while (lo < hi)
            {
                int mid = (int)((uint)(lo + hi) >> 1);
                double x = data.GetX(mid);
                if (x < xTarget) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private static int UpperBoundX(CartesianSeriesData data, double xTarget)
        {
            int lo = 0;
            int hi = data.PointCount;
            while (lo < hi)
            {
                int mid = (int)((uint)(lo + hi) >> 1);
                double x = data.GetX(mid);
                if (x <= xTarget) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private static int LowerBoundTimestamp(IReadOnlyList<OHLCDataPoint> data, double xTarget)
        {
            int lo = 0;
            int hi = data.Count;
            while (lo < hi)
            {
                int mid = (int)((uint)(lo + hi) >> 1);
                double t = data[mid].Timestamp;
                if (t < xTarget) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private static CartesianSeriesData? GetCartesianData(SeriesConfig cfg)
        {
            if (cfg is LineSeriesConfig line) return line.Data;
            if (cfg is AreaSeriesConfig area) return area.Data;
            if (cfg is BarSeriesConfig bar) return bar.Data;
            if (cfg is ScatterSeriesConfig scatter) return scatter.Data;
            return null;
        }

        private static double Clamp01(double v) => Math.Min(1.0, Math.Max(0.0, v));

        private static double? ParsePercent(string value)
        {
            value = value.Trim();
            if (value.EndsWith("%") && double.TryParse(value.Substring(0, value.Length - 1), out double p))
            {
                double ratio = p / 100.0;
                return double.IsFinite(ratio) ? ratio : null;
            }
            return null;
        }

        private static string NormalizeStackId(string? stack)
        {
            if (stack == null) return string.Empty;
            return stack.Trim();
        }

        private static double GetScatterRadiusCssPx(ScatterSeriesConfig seriesCfg, DataPoint p)
        {
            if (p.Size.HasValue) return Math.Max(0.0, p.Size.Value);

            if (seriesCfg.SymbolSizeConstant.HasValue)
            {
                return Math.Max(0.0, seriesCfg.SymbolSizeConstant.Value);
            }

            if (seriesCfg.SymbolSizeFunction != null)
            {
                try
                {
                    double v = seriesCfg.SymbolSizeFunction(p);
                    if (double.IsFinite(v)) return Math.Max(0.0, v);
                }
                catch { }
            }

            return DEFAULT_SCATTER_RADIUS_CSS_PX;
        }

        public static BarClusterSlots ComputeBarClusterSlots(IReadOnlyList<BarSeriesConfig> seriesConfigs)
        {
            var stackIdToClusterIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] clusterIndexBySeries = new int[seriesConfigs.Count];
            string[] stackIdBySeries = new string[seriesConfigs.Count];

            int clusterCount = 0;
            for (int i = 0; i < seriesConfigs.Count; i++)
            {
                string stackId = NormalizeStackId(seriesConfigs[i].Stack);
                stackIdBySeries[i] = stackId;

                if (stackId != string.Empty)
                {
                    if (stackIdToClusterIndex.TryGetValue(stackId, out int existing))
                    {
                        clusterIndexBySeries[i] = existing;
                    }
                    else
                    {
                        int idx = clusterCount++;
                        stackIdToClusterIndex[stackId] = idx;
                        clusterIndexBySeries[i] = idx;
                    }
                }
                else
                {
                    clusterIndexBySeries[i] = clusterCount++;
                }
            }

            return new BarClusterSlots
            {
                ClusterIndexBySeries = clusterIndexBySeries,
                ClusterCount = Math.Max(1, clusterCount),
                StackIdBySeries = stackIdBySeries
            };
        }

        public static double ComputeBarCategoryStep(IReadOnlyList<BarSeriesConfig> seriesConfigs)
        {
            var xs = new List<double>();
            for (int s = 0; s < seriesConfigs.Count; s++)
            {
                var data = seriesConfigs[s].Data;
                if (data == null) continue;
                int n = data.PointCount;
                for (int i = 0; i < n; i++)
                {
                    double x = data.GetX(i);
                    if (double.IsFinite(x)) xs.Add(x);
                }
            }

            if (xs.Count < 2) return 1.0;
            xs.Sort();

            double minStep = double.PositiveInfinity;
            for (int i = 1; i < xs.Count; i++)
            {
                double d = xs[i] - xs[i - 1];
                if (d > 0 && d < minStep) minStep = d;
            }
            return double.IsFinite(minStep) && minStep > 0 ? minStep : 1.0;
        }

        public static double ComputeCategoryWidthPx(IReadOnlyList<BarSeriesConfig> seriesConfigs, LinearScale xScale, double categoryStep)
        {
            if (double.IsFinite(categoryStep) && categoryStep > 0)
            {
                double x0 = 0.0;
                double p0 = xScale.Scale(x0);
                double p1 = xScale.Scale(x0 + categoryStep);
                double w = Math.Abs(p1 - p0);
                if (double.IsFinite(w) && w > 0) return w;
            }

            var sx = new List<double>();
            for (int s = 0; s < seriesConfigs.Count; s++)
            {
                var data = seriesConfigs[s].Data;
                if (data == null) continue;
                int n = data.PointCount;
                for (int i = 0; i < n; i++)
                {
                    double x = data.GetX(i);
                    if (!double.IsFinite(x)) continue;
                    double px = xScale.Scale(x);
                    if (double.IsFinite(px)) sx.Add(px);
                }
            }
            if (sx.Count < 2) return 0.0;
            sx.Sort();

            double minDx = double.PositiveInfinity;
            for (int i = 1; i < sx.Count; i++)
            {
                double d = sx[i] - sx[i - 1];
                if (d > 0 && d < minDx) minDx = d;
            }

            return double.IsFinite(minDx) && minDx > 0 ? minDx : 0.0;
        }

        public static BarLayoutPx ComputeBarLayoutPx(IReadOnlyList<BarSeriesConfig> seriesConfigs, LinearScale xScale)
        {
            var clusterSlots = ComputeBarClusterSlots(seriesConfigs);
            int clusterCount = clusterSlots.ClusterCount;

            double categoryStep = ComputeBarCategoryStep(seriesConfigs);
            double categoryWidthPx = ComputeCategoryWidthPx(seriesConfigs, xScale, categoryStep);

            // Find shared layout options from series configs
            PercentOrReal? barWidth = null;
            double barGap = DEFAULT_BAR_GAP;
            double barCategoryGap = DEFAULT_BAR_CATEGORY_GAP;

            for (int i = 0; i < seriesConfigs.Count; i++)
            {
                var s = seriesConfigs[i];
                if (barWidth == null && s.BarWidth != null) barWidth = s.BarWidth;
                barGap = s.BarGap;
                barCategoryGap = s.BarCategoryGap;
            }

            barGap = Clamp01(barGap);
            barCategoryGap = Clamp01(barCategoryGap);

            double categoryInnerWidthPx = Math.Max(0.0, categoryWidthPx * (1.0 - barCategoryGap));
            double denom = clusterCount + Math.Max(0, clusterCount - 1) * barGap;
            double maxBarWidthPx = denom > 0 ? categoryInnerWidthPx / denom : 0.0;

            double barWidthPx = 0.0;
            if (barWidth.HasValue)
            {
                var val = barWidth.Value;
                if (val.IsPercent)
                {
                    double? p = ParsePercent(val.PercentValue!);
                    barWidthPx = p == null ? 0.0 : maxBarWidthPx * Clamp01(p.Value);
                }
                else
                {
                    barWidthPx = Math.Max(0.0, val.RealValue!.Value);
                    barWidthPx = Math.Min(barWidthPx, maxBarWidthPx);
                }
            }

            if (!(barWidthPx > 0.0))
            {
                barWidthPx = maxBarWidthPx;
            }

            double gapPx = barWidthPx * barGap;
            double clusterWidthPx = clusterCount * barWidthPx + Math.Max(0, clusterCount - 1) * gapPx;

            return new BarLayoutPx
            {
                CategoryStep = categoryStep,
                CategoryWidthPx = categoryWidthPx,
                BarWidthPx = barWidthPx,
                GapPx = gapPx,
                ClusterWidthPx = clusterWidthPx,
                ClusterSlots = clusterSlots
            };
        }

        public static int BucketStackedXKey(double xCenterPx, double categoryWidthPx, double xDomain, double categoryStep)
        {
            if (double.IsFinite(categoryWidthPx) && categoryWidthPx > 0.0 && double.IsFinite(xCenterPx))
            {
                return (int)Math.Round(xCenterPx / categoryWidthPx);
            }
            if (double.IsFinite(categoryStep) && categoryStep > 0.0 && double.IsFinite(xDomain))
            {
                return (int)Math.Round(xDomain / categoryStep);
            }
            return (int)Math.Round(xDomain * 1e6);
        }

        public static NearestPointMatch? FindNearestPoint(
            IReadOnlyList<SeriesConfig> series,
            double x,
            double y,
            LinearScale xScale,
            LinearScale yScale,
            double maxDistance = DEFAULT_MAX_DISTANCE_PX)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) return null;

            double md = double.IsFinite(maxDistance) ? Math.Max(0.0, maxDistance) : DEFAULT_MAX_DISTANCE_PX;
            double maxDistSq = md * md;

            double xTarget = xScale.Invert(x);
            if (!double.IsFinite(xTarget)) return null;

            int bestSeriesIndex = -1;
            int bestDataIndex = -1;
            DataPoint? bestPoint = null;
            double bestDistSq = double.PositiveInfinity;

            // Bar hit-testing first
            var barSeriesConfigs = new List<BarSeriesConfig>();
            var barSeriesIndexByBar = new List<int>();
            for (int s = 0; s < series.Count; s++)
            {
                var cfg = series[s];
                if (cfg is BarSeriesConfig bar && bar.Visible)
                {
                    barSeriesConfigs.Add(bar);
                    barSeriesIndexByBar.Add(s);
                }
            }

            if (barSeriesConfigs.Count > 0)
            {
                var layoutPx = ComputeBarLayoutPx(barSeriesConfigs, xScale);
                if (layoutPx.BarWidthPx > 0.0 && layoutPx.ClusterWidthPx >= 0.0)
                {
                    double plotHeightPx = 0.0;
                    for (int s = 0; s < barSeriesConfigs.Count; s++)
                    {
                        var data = barSeriesConfigs[s].Data;
                        if (data == null) continue;
                        int n = data.PointCount;
                        for (int i = 0; i < n; i++)
                        {
                            double yVal = data.GetY(i);
                            if (!double.IsFinite(yVal)) continue;
                            double py = yScale.Scale(yVal);
                            if (double.IsFinite(py) && py > plotHeightPx) plotHeightPx = py;
                        }
                    }

                    // Invert top/bottom bounds to determine baseline
                    double yDomainA = yScale.Invert(plotHeightPx);
                    double yDomainB = yScale.Invert(0.0);
                    double yMin = Math.Min(yDomainA, yDomainB);
                    double yMax = Math.Max(yDomainA, yDomainB);

                    double baselineDomain = 0.0;
                    if (yMin <= 0.0 && 0.0 <= yMax) baselineDomain = 0.0;
                    else if (yMin > 0.0) baselineDomain = yMin;
                    else if (yMax < 0.0) baselineDomain = yMax;

                    double baselinePx = yScale.Scale(baselineDomain);

                    var clusterSlots = layoutPx.ClusterSlots;
                    double barWidthPx = layoutPx.BarWidthPx;
                    double gapPx = layoutPx.GapPx;
                    double clusterWidthPx = layoutPx.ClusterWidthPx;
                    double categoryWidthPx = layoutPx.CategoryWidthPx;
                    double categoryStep = layoutPx.CategoryStep;

                    var stackSumsByStackId = new Dictionary<string, Dictionary<int, (double posSum, double negSum)>>(StringComparer.Ordinal);
                    int bestBarHitSeriesIndex = -1;
                    int bestBarHitDataIndex = -1;
                    double bestBarHitTop = double.PositiveInfinity;

                    for (int b = 0; b < barSeriesConfigs.Count; b++)
                    {
                        var seriesCfg = barSeriesConfigs[b];
                        int originalSeriesIndex = barSeriesIndexByBar[b];

                        var data = seriesCfg.Data;
                        if (data == null) continue;
                        int n = data.PointCount;
                        int clusterIndex = clusterSlots.ClusterIndexBySeries[b];
                        string stackId = clusterSlots.StackIdBySeries[b];

                        for (int i = 0; i < n; i++)
                        {
                            double xDomain = data.GetX(i);
                            double yDomain = data.GetY(i);
                            if (!double.IsFinite(xDomain) || !double.IsFinite(yDomain)) continue;

                            double xCenterPx = xScale.Scale(xDomain);
                            if (!double.IsFinite(xCenterPx)) continue;

                            double left = xCenterPx - clusterWidthPx / 2.0 + clusterIndex * (barWidthPx + gapPx);
                            double right = left + barWidthPx;

                            double baseDomain = baselineDomain;
                            double topDomain = yDomain;

                            if (stackId != string.Empty)
                            {
                                if (!stackSumsByStackId.TryGetValue(stackId, out var sumsForX))
                                {
                                    sumsForX = new Dictionary<int, (double posSum, double negSum)>();
                                    stackSumsByStackId[stackId] = sumsForX;
                                }

                                int xKey = BucketStackedXKey(xCenterPx, categoryWidthPx, xDomain, categoryStep);
                                if (!sumsForX.TryGetValue(xKey, out var sums))
                                {
                                    sums = (baselineDomain, baselineDomain);
                                }

                                if (yDomain >= 0.0)
                                {
                                    baseDomain = sums.posSum;
                                    topDomain = baseDomain + yDomain;
                                    sumsForX[xKey] = (topDomain, sums.negSum);
                                }
                                else
                                {
                                    baseDomain = sums.negSum;
                                    topDomain = baseDomain + yDomain;
                                    sumsForX[xKey] = (sums.posSum, topDomain);
                                }
                            }

                            double basePx = yScale.Scale(baseDomain);
                            double topPx = yScale.Scale(topDomain);
                            if (!double.IsFinite(basePx) || !double.IsFinite(topPx)) continue;

                            double barTop = Math.Min(basePx, topPx);
                            double barBottom = Math.Max(basePx, topPx);

                            // Point in bar bounds check
                            if (x >= left && x <= right && y >= barTop && y <= barBottom)
                            {
                                bool isBetter = bestBarHitSeriesIndex == -1 ||
                                                barTop < bestBarHitTop ||
                                                (barTop == bestBarHitTop && originalSeriesIndex > bestBarHitSeriesIndex);

                                if (isBetter)
                                {
                                    bestBarHitSeriesIndex = originalSeriesIndex;
                                    bestBarHitDataIndex = i;
                                    bestBarHitTop = barTop;
                                }
                            }
                        }
                    }

                    if (bestBarHitSeriesIndex >= 0)
                    {
                        var targetData = GetCartesianData(series[bestBarHitSeriesIndex]);
                        if (targetData != null)
                        {
                            return new NearestPointMatch
                            {
                                SeriesIndex = bestBarHitSeriesIndex,
                                DataIndex = bestBarHitDataIndex,
                                Point = new DataPoint(targetData.GetX(bestBarHitDataIndex), targetData.GetY(bestBarHitDataIndex), targetData.GetSize(bestBarHitDataIndex)),
                                Distance = 0.0
                            };
                        }
                    }
                }
            }

            // Scatter / Line / Area Cartesian hit-testing
            var cartesianSeriesConfigs = new List<SeriesConfig>();
            var cartesianSeriesIndexMap = new List<int>();
            for (int s = 0; s < series.Count; s++)
            {
                var cfg = series[s];
                if (cfg is PieSeriesConfig || cfg is CandlestickSeriesConfig) continue;
                if (!cfg.Visible) continue;
                cartesianSeriesConfigs.Add(cfg);
                cartesianSeriesIndexMap.Add(s);
            }

            for (int s = 0; s < cartesianSeriesConfigs.Count; s++)
            {
                var seriesCfg = cartesianSeriesConfigs[s];
                int originalSeriesIndex = cartesianSeriesIndexMap[s];

                var data = GetCartesianData(seriesCfg);
                if (data == null) continue;
                int n = data.PointCount;
                if (n == 0) continue;

                var scatterCfg = seriesCfg as ScatterSeriesConfig;
                bool canBinarySearch = IsMonotonicNonDecreasingFiniteX(data);

                if (canBinarySearch)
                {
                    int startIdx = LowerBoundX(data, xTarget);

                    // Scan right
                    for (int i = startIdx; i < n; i++)
                    {
                        double px = data.GetX(i);
                        double py = data.GetY(i);
                        if (!double.IsFinite(px) || !double.IsFinite(py)) continue;

                        double sx = xScale.Scale(px);
                        double sy = yScale.Scale(py);
                        if (!double.IsFinite(sx) || !double.IsFinite(sy)) continue;

                        double dx = sx - x;
                        double dy = sy - y;
                        double distSq = dx * dx + dy * dy;

                        if (dx * dx > bestDistSq) break; // Monotonic early exit

                        double allowedSq = maxDistSq;
                        if (scatterCfg != null)
                        {
                            double r = GetScatterRadiusCssPx(scatterCfg, new DataPoint(px, py, data.GetSize(i)));
                            double allowed = md + r;
                            allowedSq = allowed * allowed;
                        }

                        if (distSq > allowedSq) continue;

                        bool isBetter = distSq < bestDistSq ||
                                        (distSq == bestDistSq &&
                                         (!bestPoint.HasValue ||
                                          originalSeriesIndex < bestSeriesIndex ||
                                          (originalSeriesIndex == bestSeriesIndex && i < bestDataIndex)));

                        if (isBetter)
                        {
                            bestDistSq = distSq;
                            bestSeriesIndex = originalSeriesIndex;
                            bestDataIndex = i;
                            bestPoint = new DataPoint(px, py, data.GetSize(i));
                        }
                    }

                    // Scan left
                    for (int i = startIdx - 1; i >= 0; i--)
                    {
                        double px = data.GetX(i);
                        double py = data.GetY(i);
                        if (!double.IsFinite(px) || !double.IsFinite(py)) continue;

                        double sx = xScale.Scale(px);
                        double sy = yScale.Scale(py);
                        if (!double.IsFinite(sx) || !double.IsFinite(sy)) continue;

                        double dx = sx - x;
                        double dy = sy - y;
                        double distSq = dx * dx + dy * dy;

                        if (dx * dx > bestDistSq) break; // Monotonic early exit

                        double allowedSq = maxDistSq;
                        if (scatterCfg != null)
                        {
                            double r = GetScatterRadiusCssPx(scatterCfg, new DataPoint(px, py, data.GetSize(i)));
                            double allowed = md + r;
                            allowedSq = allowed * allowed;
                        }

                        if (distSq > allowedSq) continue;

                        bool isBetter = distSq < bestDistSq ||
                                        (distSq == bestDistSq &&
                                         (!bestPoint.HasValue ||
                                          originalSeriesIndex < bestSeriesIndex ||
                                          (originalSeriesIndex == bestSeriesIndex && i < bestDataIndex)));

                        if (isBetter)
                        {
                            bestDistSq = distSq;
                            bestSeriesIndex = originalSeriesIndex;
                            bestDataIndex = i;
                            bestPoint = new DataPoint(px, py, data.GetSize(i));
                        }
                    }
                }
                else
                {
                    // Fallback linear scan
                    for (int i = 0; i < n; i++)
                    {
                        double px = data.GetX(i);
                        double py = data.GetY(i);
                        if (!double.IsFinite(px) || !double.IsFinite(py)) continue;

                        double sx = xScale.Scale(px);
                        double sy = yScale.Scale(py);
                        if (!double.IsFinite(sx) || !double.IsFinite(sy)) continue;

                        double dx = sx - x;
                        double dy = sy - y;
                        double distSq = dx * dx + dy * dy;

                        double allowedSq = maxDistSq;
                        if (scatterCfg != null)
                        {
                            double r = GetScatterRadiusCssPx(scatterCfg, new DataPoint(px, py, data.GetSize(i)));
                            double allowed = md + r;
                            allowedSq = allowed * allowed;
                        }

                        if (distSq > allowedSq) continue;

                        bool isBetter = distSq < bestDistSq ||
                                        (distSq == bestDistSq &&
                                         (!bestPoint.HasValue ||
                                          originalSeriesIndex < bestSeriesIndex ||
                                          (originalSeriesIndex == bestSeriesIndex && i < bestDataIndex)));

                        if (isBetter)
                        {
                            bestDistSq = distSq;
                            bestSeriesIndex = originalSeriesIndex;
                            bestDataIndex = i;
                            bestPoint = new DataPoint(px, py, data.GetSize(i));
                        }
                    }
                }
            }

            if (!bestPoint.HasValue || !double.IsFinite(bestDistSq)) return null;

            return new NearestPointMatch
            {
                SeriesIndex = bestSeriesIndex,
                DataIndex = bestDataIndex,
                Point = bestPoint.Value,
                Distance = Math.Sqrt(bestDistSq)
            };
        }

        private static BarHitTestLayout? ComputeBarHitTestLayout(IReadOnlyList<SeriesConfig> series, LinearScale xScale)
        {
            var barSeries = new List<BarSeriesConfig>();
            var globalSeriesIndices = new List<int>();
            for (int i = 0; i < series.Count; i++)
            {
                if (series[i] is BarSeriesConfig bar)
                {
                    barSeries.Add(bar);
                    globalSeriesIndices.Add(i);
                }
            }
            if (barSeries.Count == 0) return null;

            var layout = ComputeBarLayoutPx(barSeries, xScale);
            if (!double.IsFinite(layout.BarWidthPx) || layout.BarWidthPx <= 0.0) return null;

            var map = new Dictionary<int, int>();
            for (int i = 0; i < barSeries.Count; i++)
            {
                map[globalSeriesIndices[i]] = layout.ClusterSlots.ClusterIndexBySeries[i];
            }

            return new BarHitTestLayout
            {
                BarWidth = layout.BarWidthPx,
                Gap = layout.GapPx,
                ClusterWidth = layout.ClusterWidthPx,
                ClusterIndexByGlobalSeriesIndex = map
            };
        }

        public static IReadOnlyList<PointsAtXMatch> FindPointsAtX(
            IReadOnlyList<SeriesConfig> series,
            double xValue,
            LinearScale xScale,
            double? tolerance = null)
        {
            if (!double.IsFinite(xValue)) return Array.Empty<PointsAtXMatch>();

            double maxDx = tolerance == null || !double.IsFinite(tolerance.Value) ? double.PositiveInfinity : Math.Max(0.0, tolerance.Value);
            double maxDxSq = maxDx * maxDx;

            double xTarget = xScale.Invert(xValue);
            if (!double.IsFinite(xTarget)) return Array.Empty<PointsAtXMatch>();

            var matches = new List<PointsAtXMatch>();
            var barLayout = ComputeBarHitTestLayout(series, xScale);

            for (int s = 0; s < series.Count; s++)
            {
                var seriesConfig = series[s];
                if (seriesConfig is PieSeriesConfig || seriesConfig is CandlestickSeriesConfig) continue;
                if (!seriesConfig.Visible) continue;

                var data = GetCartesianData(seriesConfig);
                if (data == null) continue;
                int n = data.PointCount;
                if (n == 0) continue;

                // Special bar series interval logic
                if (seriesConfig is BarSeriesConfig && barLayout != null)
                {
                    if (barLayout.ClusterIndexByGlobalSeriesIndex.TryGetValue(s, out int clusterIndex))
                    {
                        double barWidth = barLayout.BarWidth;
                        double gap = barLayout.Gap;
                        double clusterWidth = barLayout.ClusterWidth;
                        double offsetLeft = -clusterWidth / 2.0 + clusterIndex * (barWidth + gap);

                        double hitTol = tolerance == null || !double.IsFinite(tolerance.Value) ? 0.0 : Math.Max(0.0, tolerance.Value);

                        if (double.IsFinite(barWidth) && barWidth > 0.0 && double.IsFinite(offsetLeft))
                        {
                            int hitIndex = -1;

                            bool IsHit(double xCenterRange)
                            {
                                if (!double.IsFinite(xCenterRange)) return false;
                                double left = xCenterRange + offsetLeft;
                                double right = left + barWidth;
                                return xValue >= left - hitTol && xValue < right + hitTol;
                            }

                            bool hasNaN = false;
                            for (int idx = 0; idx < n; idx++)
                            {
                                if (double.IsNaN(data.GetX(idx)))
                                {
                                    hasNaN = true;
                                    break;
                                }
                            }

                            if (hasNaN)
                            {
                                for (int i = 0; i < n; i++)
                                {
                                    double px = data.GetX(i);
                                    if (!double.IsFinite(px)) continue;
                                    double xCenter = xScale.Scale(px);
                                    if (IsHit(xCenter))
                                    {
                                        hitIndex = hitIndex < 0 ? i : Math.Min(hitIndex, i);
                                    }
                                }
                            }
                            else
                            {
                                double xTargetAdjusted = xScale.Invert(xValue - offsetLeft);
                                if (double.IsFinite(xTargetAdjusted))
                                {
                                    int insertionIndex = LowerBoundX(data, xTargetAdjusted);

                                    // Scan left
                                    for (int i = insertionIndex - 1; i >= 0; i--)
                                    {
                                        double px = data.GetX(i);
                                        if (!double.IsFinite(px)) continue;
                                        double xCenter = xScale.Scale(px);
                                        double left = xCenter + offsetLeft;
                                        double right = left + barWidth;
                                        if (right + hitTol <= xValue) break;
                                        if (xValue >= left - hitTol && xValue < right + hitTol)
                                        {
                                            hitIndex = hitIndex < 0 ? i : Math.Min(hitIndex, i);
                                        }
                                    }

                                    // Scan right
                                    for (int i = insertionIndex; i < n; i++)
                                    {
                                        double px = data.GetX(i);
                                        if (!double.IsFinite(px)) continue;
                                        double xCenter = xScale.Scale(px);
                                        double left = xCenter + offsetLeft;
                                        if (left - hitTol > xValue) break;
                                        double right = left + barWidth;
                                        if (xValue < right + hitTol)
                                        {
                                            hitIndex = hitIndex < 0 ? i : Math.Min(hitIndex, i);
                                        }
                                    }
                                }
                            }

                            if (hitIndex >= 0)
                            {
                                matches.Add(new PointsAtXMatch
                                {
                                    SeriesIndex = s,
                                    DataIndex = hitIndex,
                                    Point = new DataPoint(data.GetX(hitIndex), data.GetY(hitIndex), data.GetSize(hitIndex))
                                });
                                continue;
                            }

                            if (tolerance != null && double.IsFinite(tolerance.Value))
                            {
                                continue;
                            }
                        }
                    }
                }

                // General Cartesian points mapping
                int bestDataIndex = -1;
                double bestDxSq = maxDxSq;

                bool hasNaNX = false;
                for (int idx = 0; idx < n; idx++)
                {
                    if (double.IsNaN(data.GetX(idx)))
                    {
                        hasNaNX = true;
                        break;
                    }
                }

                if (hasNaNX)
                {
                    for (int i = 0; i < n; i++)
                    {
                        double px = data.GetX(i);
                        if (!double.IsFinite(px)) continue;
                        double sx = xScale.Scale(px);
                        if (!double.IsFinite(sx)) continue;
                        double dx = sx - xValue;
                        double dxSq = dx * dx;
                        if (dxSq < bestDxSq)
                        {
                            bestDxSq = dxSq;
                            bestDataIndex = i;
                        }
                    }
                }
                else
                {
                    int insertionIndex = LowerBoundX(data, xTarget);

                    int left = insertionIndex - 1;
                    int right = insertionIndex;

                    while (left >= 0 || right < n)
                    {
                        while (left >= 0 && !double.IsFinite(data.GetX(left))) left--;
                        while (right < n && !double.IsFinite(data.GetX(right))) right++;
                        if (left < 0 && right >= n) break;

                        double dxSqLeft = double.PositiveInfinity;
                        if (left >= 0)
                        {
                            double sx = xScale.Scale(data.GetX(left));
                            if (double.IsFinite(sx)) dxSqLeft = (sx - xValue) * (sx - xValue);
                        }

                        double dxSqRight = double.PositiveInfinity;
                        if (right < n)
                        {
                            double sx = xScale.Scale(data.GetX(right));
                            if (double.IsFinite(sx)) dxSqRight = (sx - xValue) * (sx - xValue);
                        }

                        if (dxSqLeft > bestDxSq && dxSqRight > bestDxSq) break;

                        if (dxSqLeft <= dxSqRight)
                        {
                            if (left >= 0 && dxSqLeft <= bestDxSq)
                            {
                                bestDxSq = dxSqLeft;
                                bestDataIndex = left;
                            }
                            left--;
                        }
                        else
                        {
                            if (right < n && dxSqRight <= bestDxSq)
                            {
                                bestDxSq = dxSqRight;
                                bestDataIndex = right;
                            }
                            right++;
                        }
                    }
                }

                if (bestDataIndex >= 0)
                {
                    matches.Add(new PointsAtXMatch
                    {
                        SeriesIndex = s,
                        DataIndex = bestDataIndex,
                        Point = new DataPoint(data.GetX(bestDataIndex), data.GetY(bestDataIndex), data.GetSize(bestDataIndex))
                    });
                }
            }

            return matches;
        }

        public static CandlestickMatch? FindCandlestick(
            IReadOnlyList<CandlestickSeriesConfig> series,
            double x,
            double y,
            LinearScale xScale,
            LinearScale yScale,
            double barWidthClip)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
            if (!double.IsFinite(barWidthClip) || barWidthClip <= 0.0) return null;

            double xTarget = xScale.Invert(x);
            if (!double.IsFinite(xTarget)) return null;

            double halfW = barWidthClip / 2.0;

            CandlestickMatch? best = null;
            double bestDx = double.PositiveInfinity;

            bool IsBodyHit(OHLCDataPoint p)
            {
                double open = p.Open;
                double close = p.Close;
                if (!double.IsFinite(open) || !double.IsFinite(close)) return false;

                double yOpen = yScale.Scale(open);
                double yClose = yScale.Scale(close);
                if (!double.IsFinite(yOpen) || !double.IsFinite(yClose)) return false;

                double yMin = Math.Min(yOpen, yClose);
                double yMax = Math.Max(yOpen, yClose);
                return y >= yMin && y <= yMax;
            }

            void TryUpdate(int seriesIndex, int dataIndex, OHLCDataPoint point, double dx)
            {
                if (!double.IsFinite(dx)) return;
                if (dx < bestDx)
                {
                    bestDx = dx;
                    best = new CandlestickMatch { SeriesIndex = seriesIndex, DataIndex = dataIndex, Point = point };
                }
                else if (dx == bestDx && best != null)
                {
                    if (dataIndex < best.DataIndex)
                    {
                        best = new CandlestickMatch { SeriesIndex = seriesIndex, DataIndex = dataIndex, Point = point };
                    }
                    else if (dataIndex == best.DataIndex && seriesIndex < best.SeriesIndex)
                    {
                        best = new CandlestickMatch { SeriesIndex = seriesIndex, DataIndex = dataIndex, Point = point };
                    }
                }
            }

            for (int s = 0; s < series.Count; s++)
            {
                var cfg = series[s];
                var data = cfg.Data;
                int n = data.Count;
                if (n == 0) continue;

                bool monotonic = IsMonotonicNonDecreasingFiniteTimestamp(data);

                if (!monotonic)
                {
                    for (int i = 0; i < n; i++)
                    {
                        var p = data[i];
                        double t = p.Timestamp;
                        if (!double.IsFinite(t)) continue;
                        double xCenter = xScale.Scale(t);
                        if (!double.IsFinite(xCenter)) continue;

                        double dx = Math.Abs(x - xCenter);
                        if (dx > halfW) continue;
                        if (!IsBodyHit(p)) continue;

                        TryUpdate(s, i, p, dx);
                    }
                    continue;
                }

                int insertionIndex = LowerBoundTimestamp(data, xTarget);

                // Scan left
                for (int i = insertionIndex - 1; i >= 0; i--)
                {
                    var p = data[i];
                    double t = p.Timestamp;
                    double xCenter = xScale.Scale(t);
                    if (!double.IsFinite(xCenter)) continue;
                    if (xCenter < x - halfW) break;

                    double dx = Math.Abs(x - xCenter);
                    if (dx > halfW) continue;
                    if (!IsBodyHit(p)) continue;

                    TryUpdate(s, i, p, dx);
                }

                // Scan right
                for (int i = insertionIndex; i < n; i++)
                {
                    var p = data[i];
                    double t = p.Timestamp;
                    double xCenter = xScale.Scale(t);
                    if (!double.IsFinite(xCenter)) continue;
                    if (xCenter > x + halfW) break;

                    double dx = Math.Abs(x - xCenter);
                    if (dx > halfW) continue;
                    if (!IsBodyHit(p)) continue;

                    TryUpdate(s, i, p, dx);
                }
            }

            return best;
        }

        public static PieSliceMatch? FindPieSlice(
            double x,
            double y,
            PieSeriesConfig series,
            int seriesIndex,
            double centerX,
            double centerY,
            double innerRadius,
            double outerRadius)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
            if (!double.IsFinite(centerX) || !double.IsFinite(centerY)) return null;
            if (outerRadius <= 0.0) return null;

            double dx = x - centerX;
            double dyUp = centerY - y; // polar angles are +y up in shaders
            double r = Math.Sqrt(dx * dx + dyUp * dyUp);

            if (r <= innerRadius || r > outerRadius) return null;

            double angle = Math.Atan2(dyUp, dx);
            if (angle < 0.0) angle += 2.0 * Math.PI;

            var data = series.Data;
            double total = 0.0;
            int validCount = 0;

            for (int i = 0; i < data.Count; i++)
            {
                var item = data[i];
                if (item != null && item.Value > 0.0 && item.Visible)
                {
                    total += item.Value;
                    validCount++;
                }
            }

            if (total <= 0.0 || validCount == 0) return null;

            double startDeg = series.StartAngle;
            double current = (startDeg * Math.PI) / 180.0;
            if (current < 0.0) current = (current % (2.0 * Math.PI)) + 2.0 * Math.PI;
            current %= 2.0 * Math.PI;

            double accumulated = 0.0;
            int emitted = 0;
            double tau = 2.0 * Math.PI;

            for (int i = 0; i < data.Count; i++)
            {
                var slice = data[i];
                if (slice == null || slice.Value <= 0.0 || !slice.Visible) continue;

                emitted++;
                bool isLast = emitted == validCount;

                double frac = slice.Value / total;
                double span = frac * tau;
                if (isLast)
                {
                    span = Math.Max(0.0, tau - accumulated);
                }
                else
                {
                    span = Math.Max(0.0, Math.Min(tau, span));
                }
                accumulated += span;
                if (span <= 0.0) continue;

                double start = current;
                double end = validCount == 1 ? current + tau : (current + span) % tau;
                current = (current + span) % tau;

                double wedgeSpan = end - start;
                if (wedgeSpan < 0.0) wedgeSpan += tau;

                double rel = angle - start;
                if (rel < 0.0) rel += tau;

                if (rel <= wedgeSpan)
                {
                    return new PieSliceMatch
                    {
                        SeriesIndex = seriesIndex,
                        DataIndex = i,
                        Slice = slice
                    };
                }
            }

            return null;
        }

        public static float[] LttbSample(float[] data, int targetPoints)
        {
            int n = data.Length / 2;
            if (targetPoints <= 0 || n == 0) return Array.Empty<float>();
            if (n <= targetPoints) return data;

            int lastIndex = n - 1;
            var indices = new int[targetPoints];
            indices[0] = 0;
            indices[targetPoints - 1] = lastIndex;

            double bucketSize = (double)(n - 2) / (targetPoints - 2);

            int a = 0;
            int outIdx = 1;

            float lastX = data[lastIndex * 2];
            float lastY = data[lastIndex * 2 + 1];

            for (int bucket = 0; bucket < targetPoints - 2; bucket++)
            {
                int rangeStart = (int)Math.Floor(bucketSize * bucket) + 1;
                int rangeEndExclusive = (int)Math.Min(Math.Floor(bucketSize * (bucket + 1)) + 1, lastIndex);
                if (rangeStart >= rangeEndExclusive)
                {
                    rangeStart = Math.Min(rangeStart, lastIndex - 1);
                    rangeEndExclusive = Math.Min(rangeStart + 1, lastIndex);
                }

                int nextRangeStart = (int)Math.Floor(bucketSize * (bucket + 1)) + 1;
                int nextRangeEndExclusive = (int)Math.Min(Math.Floor(bucketSize * (bucket + 2)) + 1, lastIndex);

                double avgX = lastX;
                double avgY = lastY;
                if (nextRangeStart < nextRangeEndExclusive)
                {
                    double sumX = 0;
                    double sumY = 0;
                    int avgCount = 0;
                    for (int i = nextRangeStart; i < nextRangeEndExclusive; i++)
                    {
                        sumX += data[i * 2];
                        sumY += data[i * 2 + 1];
                        avgCount++;
                    }
                    if (avgCount > 0)
                    {
                        avgX = sumX / avgCount;
                        avgY = sumY / avgCount;
                    }
                }

                double ax = data[a * 2];
                double ay = data[a * 2 + 1];

                double maxArea = -1.0;
                int maxIndex = rangeStart;

                for (int i = rangeStart; i < rangeEndExclusive; i++)
                {
                    double bx = data[i * 2];
                    double by = data[i * 2 + 1];
                    double area2 = (ax - avgX) * (by - ay) - (ax - bx) * (avgY - ay);
                    double absArea2 = Math.Abs(area2);
                    if (absArea2 > maxArea)
                    {
                        maxArea = absArea2;
                        maxIndex = i;
                    }
                }

                indices[outIdx++] = maxIndex;
                a = maxIndex;
            }

            var result = new float[targetPoints * 2];
            for (int i = 0; i < targetPoints; i++)
            {
                int idx = indices[i];
                result[i * 2] = data[idx * 2];
                result[i * 2 + 1] = data[idx * 2 + 1];
            }
            return result;
        }

        public static List<DataPoint> LttbSample(IReadOnlyList<DataPoint> data, int targetPoints)
        {
            int n = data.Count;
            if (targetPoints <= 0 || n == 0) return new List<DataPoint>();
            if (n <= targetPoints) return new List<DataPoint>(data);

            int lastIndex = n - 1;
            var indices = new int[targetPoints];
            indices[0] = 0;
            indices[targetPoints - 1] = lastIndex;

            double bucketSize = (double)(n - 2) / (targetPoints - 2);

            int a = 0;
            int outIdx = 1;

            var pLast = data[lastIndex];
            double lastX = pLast.X;
            double lastY = pLast.Y;

            for (int bucket = 0; bucket < targetPoints - 2; bucket++)
            {
                int rangeStart = (int)Math.Floor(bucketSize * bucket) + 1;
                int rangeEndExclusive = (int)Math.Min(Math.Floor(bucketSize * (bucket + 1)) + 1, lastIndex);
                if (rangeStart >= rangeEndExclusive)
                {
                    rangeStart = Math.Min(rangeStart, lastIndex - 1);
                    rangeEndExclusive = Math.Min(rangeStart + 1, lastIndex);
                }

                int nextRangeStart = (int)Math.Floor(bucketSize * (bucket + 1)) + 1;
                int nextRangeEndExclusive = (int)Math.Min(Math.Floor(bucketSize * (bucket + 2)) + 1, lastIndex);

                double avgX = lastX;
                double avgY = lastY;
                if (nextRangeStart < nextRangeEndExclusive)
                {
                    double sumX = 0;
                    double sumY = 0;
                    int avgCount = 0;
                    for (int i = nextRangeStart; i < nextRangeEndExclusive; i++)
                    {
                        var p = data[i];
                        sumX += p.X;
                        sumY += p.Y;
                        avgCount++;
                    }
                    if (avgCount > 0)
                    {
                        avgX = sumX / avgCount;
                        avgY = sumY / avgCount;
                    }
                }

                var pa = data[a];
                double ax = pa.X;
                double ay = pa.Y;

                double maxArea = -1.0;
                int maxIndex = rangeStart;

                for (int i = rangeStart; i < rangeEndExclusive; i++)
                {
                    var pb = data[i];
                    double bx = pb.X;
                    double by = pb.Y;
                    double area2 = (ax - avgX) * (by - ay) - (ax - bx) * (avgY - ay);
                    double absArea2 = Math.Abs(area2);
                    if (absArea2 > maxArea)
                    {
                        maxArea = absArea2;
                        maxIndex = i;
                    }
                }

                indices[outIdx++] = maxIndex;
                a = maxIndex;
            }

            var result = new List<DataPoint>(targetPoints);
            for (int i = 0; i < targetPoints; i++)
            {
                result.Add(data[indices[i]]);
            }
            return result;
        }

        public static CartesianSeriesData LttbSample(CartesianSeriesData data, int targetPoints)
        {
            if (data.PointCount <= targetPoints) return data;

            if (data.Format == CartesianDataFormat.Interleaved && data.Interleaved != null)
            {
                return new CartesianSeriesData(LttbSample(data.Interleaved, targetPoints));
            }
            else
            {
                var list = new List<DataPoint>(data.PointCount);
                for (int i = 0; i < data.PointCount; i++)
                {
                    list.Add(new DataPoint(data.GetX(i), data.GetY(i), data.GetSize(i)));
                }
                var sampled = LttbSample(list, targetPoints);
                var resultPoints = new DataPoint?[sampled.Count];
                for (int i = 0; i < sampled.Count; i++)
                {
                    resultPoints[i] = sampled[i];
                }
                return new CartesianSeriesData(resultPoints);
            }
        }
    }
}
