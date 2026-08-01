using System.Numerics;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Input;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class PointerPredictor :
    IDisposable
{
    private const int MinimumSampleCount = 10;
    private const int HistoryCapacity = 16;
    private const int MaximumPredictionCount = 64;
    private const double MicrosecondsPerMillisecond =
        1_000d;
    private static readonly PointerPoint[]
        EmptyPoints = [];

    private readonly object _gate = new();
    private readonly Sample[] _samples =
        new Sample[HistoryCapacity];
    private InputPointerSource? _inputPointerSource;
    private TimeSpan _predictionTime =
        TimeSpan.FromMilliseconds(15);
    private int _sampleStart;
    private int _sampleCount;

    private PointerPredictor(
        InputPointerSource inputPointerSource)
    {
        _inputPointerSource =
            inputPointerSource;
    }

    public TimeSpan PredictionTime
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _predictionTime;
            }
        }
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                _predictionTime = value;
            }
        }
    }

    public static PointerPredictor
        CreateForInputPointerSource(
            InputPointerSource inputPointerSource)
    {
        ArgumentNullException.ThrowIfNull(
            inputPointerSource);
        return new PointerPredictor(
            inputPointerSource);
    }

    public PointerPoint[] GetPredictedPoints(
        PointerPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        lock (_gate)
        {
            ThrowIfDisposed();
            Append(point);
            if (_sampleCount <
                MinimumSampleCount ||
                _predictionTime <= TimeSpan.Zero)
            {
                return EmptyPoints;
            }

            Sample first = GetSample(0);
            Sample last =
                GetSample(_sampleCount - 1);
            ulong timestampSpan =
                last.Timestamp - first.Timestamp;
            if (timestampSpan == 0)
                return EmptyPoints;

            double sampleInterval =
                (double)timestampSpan /
                (_sampleCount - 1);
            double horizon =
                _predictionTime.TotalMilliseconds *
                MicrosecondsPerMillisecond;
            double requestedCount =
                Math.Ceiling(
                    horizon / sampleInterval);
            if (!double.IsFinite(
                    requestedCount) ||
                requestedCount <= 0)
            {
                return EmptyPoints;
            }

            int predictionCount =
                requestedCount >=
                    MaximumPredictionCount
                    ? MaximumPredictionCount
                    : (int)requestedCount;
            if (!TryFit(
                    first.Timestamp,
                    out Trend trend))
            {
                return EmptyPoints;
            }

            var predicted =
                new PointerPoint[predictionCount];
            double lastTime =
                timestampSpan;
            for (int index = 0;
                 index < predictionCount;
                 index++)
            {
                double futureOffset =
                    (index + 1) *
                    sampleInterval;
                double predictionTime =
                    lastTime + futureOffset;
                float x = ToFiniteFloat(
                    trend.XAt(predictionTime));
                float y = ToFiniteFloat(
                    trend.YAt(predictionTime));
                float pressure = Math.Clamp(
                    ToFiniteFloat(
                        trend.PressureAt(
                            predictionTime)),
                    0f,
                    1f);
                float xTilt = Math.Clamp(
                    ToFiniteFloat(
                        trend.XTiltAt(
                            predictionTime)),
                    -90f,
                    90f);
                float yTilt = Math.Clamp(
                    ToFiniteFloat(
                        trend.YTiltAt(
                            predictionTime)),
                    -90f,
                    90f);
                ulong timestamp =
                    AddTimestamp(
                        last.Timestamp,
                        futureOffset);
                var position =
                    new Vector2(x, y);
                predicted[index] =
                    point.WithPrediction(
                        timestamp,
                        position,
                        point.Properties
                            .WithPrediction(
                                pressure,
                                xTilt,
                                yTilt));
            }

            return predicted;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_inputPointerSource is null)
                return;
            _inputPointerSource = null;
            Array.Clear(_samples);
            _sampleStart = 0;
            _sampleCount = 0;
        }
    }

    private void Append(
        PointerPoint point)
    {
        Sample sample = Sample.From(point);
        if (_sampleCount > 0)
        {
            Sample last =
                GetSample(_sampleCount - 1);
            if (last.PointerId !=
                    sample.PointerId ||
                sample.Timestamp <
                    last.Timestamp)
            {
                _sampleStart = 0;
                _sampleCount = 0;
            }
            else if (sample.Timestamp ==
                     last.Timestamp)
            {
                int lastIndex =
                    (_sampleStart +
                     _sampleCount - 1) %
                    HistoryCapacity;
                _samples[lastIndex] = sample;
                return;
            }
        }

        if (_sampleCount < HistoryCapacity)
        {
            int index =
                (_sampleStart + _sampleCount) %
                HistoryCapacity;
            _samples[index] = sample;
            _sampleCount++;
            return;
        }

        _samples[_sampleStart] = sample;
        _sampleStart =
            (_sampleStart + 1) %
            HistoryCapacity;
    }

    private bool TryFit(
        ulong firstTimestamp,
        out Trend trend)
    {
        double sumTime = 0;
        double sumTimeSquared = 0;
        double sumX = 0;
        double sumY = 0;
        double sumPressure = 0;
        double sumXTilt = 0;
        double sumYTilt = 0;
        double sumTimeX = 0;
        double sumTimeY = 0;
        double sumTimePressure = 0;
        double sumTimeXTilt = 0;
        double sumTimeYTilt = 0;

        for (int index = 0;
             index < _sampleCount;
             index++)
        {
            Sample sample = GetSample(index);
            if (!sample.IsFinite)
            {
                trend = default;
                return false;
            }

            double time =
                sample.Timestamp -
                firstTimestamp;
            sumTime += time;
            sumTimeSquared += time * time;
            sumX += sample.X;
            sumY += sample.Y;
            sumPressure += sample.Pressure;
            sumXTilt += sample.XTilt;
            sumYTilt += sample.YTilt;
            sumTimeX += time * sample.X;
            sumTimeY += time * sample.Y;
            sumTimePressure +=
                time * sample.Pressure;
            sumTimeXTilt +=
                time * sample.XTilt;
            sumTimeYTilt +=
                time * sample.YTilt;
        }

        double count = _sampleCount;
        double denominator =
            count * sumTimeSquared -
            sumTime * sumTime;
        if (!double.IsFinite(denominator) ||
            denominator <= 0)
        {
            trend = default;
            return false;
        }

        trend = new Trend(
            Fit(
                count,
                sumTime,
                denominator,
                sumX,
                sumTimeX),
            Fit(
                count,
                sumTime,
                denominator,
                sumY,
                sumTimeY),
            Fit(
                count,
                sumTime,
                denominator,
                sumPressure,
                sumTimePressure),
            Fit(
                count,
                sumTime,
                denominator,
                sumXTilt,
                sumTimeXTilt),
            Fit(
                count,
                sumTime,
                denominator,
                sumYTilt,
                sumTimeYTilt));
        return trend.IsFinite;
    }

    private static Line Fit(
        double count,
        double sumTime,
        double denominator,
        double sumValue,
        double sumTimeValue)
    {
        double slope =
            (count * sumTimeValue -
             sumTime * sumValue) /
            denominator;
        double intercept =
            (sumValue -
             slope * sumTime) /
            count;
        return new Line(intercept, slope);
    }

    private Sample GetSample(
        int index) =>
        _samples[
            (_sampleStart + index) %
            HistoryCapacity];

    private static ulong AddTimestamp(
        ulong timestamp,
        double offset)
    {
        if (offset >= ulong.MaxValue)
            return ulong.MaxValue;
        ulong roundedOffset =
            (ulong)Math.Round(offset);
        return timestamp >
               ulong.MaxValue -
               roundedOffset
            ? ulong.MaxValue
            : timestamp + roundedOffset;
    }

    private static float ToFiniteFloat(
        double value)
    {
        if (double.IsNaN(value))
            return 0f;
        return (float)Math.Clamp(
            value,
            -float.MaxValue,
            float.MaxValue);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _inputPointerSource is null,
            this);
    }

    private readonly record struct Sample(
        uint PointerId,
        ulong Timestamp,
        float X,
        float Y,
        float Pressure,
        float XTilt,
        float YTilt)
    {
        public bool IsFinite =>
            float.IsFinite(X) &&
            float.IsFinite(Y) &&
            float.IsFinite(Pressure) &&
            float.IsFinite(XTilt) &&
            float.IsFinite(YTilt);

        public static Sample From(
            PointerPoint point) =>
            new(
                point.PointerId,
                point.Timestamp,
                (float)point.Position.X,
                (float)point.Position.Y,
                point.Properties.Pressure,
                point.Properties.XTilt,
                point.Properties.YTilt);
    }

    private readonly record struct Line(
        double Intercept,
        double Slope)
    {
        public bool IsFinite =>
            double.IsFinite(Intercept) &&
            double.IsFinite(Slope);

        public double At(double time) =>
            Intercept + Slope * time;
    }

    private readonly record struct Trend(
        Line X,
        Line Y,
        Line Pressure,
        Line XTilt,
        Line YTilt)
    {
        public bool IsFinite =>
            X.IsFinite &&
            Y.IsFinite &&
            Pressure.IsFinite &&
            XTilt.IsFinite &&
            YTilt.IsFinite;

        public double XAt(double time) =>
            X.At(time);

        public double YAt(double time) =>
            Y.At(time);

        public double PressureAt(double time) =>
            Pressure.At(time);

        public double XTiltAt(double time) =>
            XTilt.At(time);

        public double YTiltAt(double time) =>
            YTilt.At(time);
    }
}
