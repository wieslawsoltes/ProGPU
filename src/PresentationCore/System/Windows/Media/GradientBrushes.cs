using System.Collections;
using System.Collections.Specialized;
using System.Numerics;
using System.Windows;
using ProGpuBrush = ProGPU.Vector.Brush;
using ProGpuColorInterpolationMode = ProGPU.Vector.GradientColorInterpolationMode;
using ProGpuGradientSpreadMethod = ProGPU.Vector.GradientSpreadMethod;
using ProGpuLinearGradientBrush = ProGPU.Vector.LinearGradientBrush;
using ProGpuRadialGradientBrush = ProGPU.Vector.RadialGradientBrush;
using ProGpuStop = ProGPU.Vector.GradientStop;

namespace System.Windows.Media;

public enum BrushMappingMode
{
    Absolute = 0,
    RelativeToBoundingBox = 1
}

public enum ColorInterpolationMode
{
    ScRgbLinearInterpolation = 0,
    SRgbLinearInterpolation = 1
}

public enum GradientSpreadMethod
{
    Pad = 0,
    Reflect = 1,
    Repeat = 2
}

public sealed class GradientStop
{
    private Color _color;
    private double _offset;
    private uint _changeVersion;

    public GradientStop()
    {
    }

    public GradientStop(Color color, double offset)
    {
        _color = color;
        _offset = offset;
    }

    public event EventHandler? Changed;

    public Color Color
    {
        get => _color;
        set
        {
            if (Equals(_color, value))
            {
                return;
            }

            _color = value;
            OnChanged();
        }
    }

    public double Offset
    {
        get => _offset;
        set
        {
            if (_offset.Equals(value))
            {
                return;
            }

            _offset = value;
            OnChanged();
        }
    }

    public uint ChangeVersion => _changeVersion;

    private void OnChanged()
    {
        unchecked
        {
            _changeVersion++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class GradientStopCollection : IList<GradientStop>, INotifyCollectionChanged
{
    private readonly List<GradientStop> _items;
    private uint _changeVersion;

    public GradientStopCollection()
    {
        _items = new List<GradientStop>();
    }

    public GradientStopCollection(int capacity)
    {
        _items = new List<GradientStop>(capacity);
    }

    public GradientStopCollection(IEnumerable<GradientStop> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        _items = new List<GradientStop>();
        foreach (var item in collection)
        {
            Add(item);
        }
    }

    public event EventHandler? Changed;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public static GradientStopCollection Empty { get; } = new();

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public uint ChangeVersion => _changeVersion;

    public GradientStop this[int index]
    {
        get => _items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var oldItem = _items[index];
            if (ReferenceEquals(oldItem, value))
            {
                return;
            }

            Detach(oldItem);
            _items[index] = value;
            Attach(value);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Replace,
                value,
                oldItem,
                index));
        }
    }

    public void Add(GradientStop item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var index = _items.Count;
        _items.Add(item);
        Attach(item);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add,
            item,
            index));
    }

    public void Clear()
    {
        if (_items.Count == 0)
        {
            return;
        }

        foreach (var item in _items)
        {
            Detach(item);
        }

        _items.Clear();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public bool Contains(GradientStop item)
    {
        return _items.Contains(item);
    }

    public void CopyTo(GradientStop[] array, int arrayIndex)
    {
        _items.CopyTo(array, arrayIndex);
    }

    public IEnumerator<GradientStop> GetEnumerator()
    {
        return _items.GetEnumerator();
    }

    public int IndexOf(GradientStop item)
    {
        return _items.IndexOf(item);
    }

    public void Insert(int index, GradientStop item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Insert(index, item);
        Attach(item);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add,
            item,
            index));
    }

    public bool Remove(GradientStop item)
    {
        var index = _items.IndexOf(item);
        if (index < 0)
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index)
    {
        var oldItem = _items[index];
        Detach(oldItem);
        _items.RemoveAt(index);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove,
            oldItem,
            index));
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private void Attach(GradientStop item)
    {
        item.Changed += OnStopChanged;
    }

    private void Detach(GradientStop item)
    {
        item.Changed -= OnStopChanged;
    }

    private void OnStopChanged(object? sender, EventArgs e)
    {
        OnChanged();
    }

    private void OnCollectionChanged(NotifyCollectionChangedEventArgs args)
    {
        OnChanged();
        CollectionChanged?.Invoke(this, args);
    }

    private void OnChanged()
    {
        unchecked
        {
            _changeVersion++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public abstract class GradientBrush : Brush
{
    private ColorInterpolationMode _colorInterpolationMode = ColorInterpolationMode.SRgbLinearInterpolation;
    private BrushMappingMode _mappingMode = BrushMappingMode.RelativeToBoundingBox;
    private GradientSpreadMethod _spreadMethod = GradientSpreadMethod.Pad;
    private GradientStopCollection _gradientStops;

    protected GradientBrush()
    {
        _gradientStops = new GradientStopCollection();
        _gradientStops.Changed += OnGradientStopsChanged;
    }

    public ColorInterpolationMode ColorInterpolationMode
    {
        get => _colorInterpolationMode;
        set
        {
            if (_colorInterpolationMode == value)
            {
                return;
            }

            _colorInterpolationMode = value;
            OnChanged();
        }
    }

    public BrushMappingMode MappingMode
    {
        get => _mappingMode;
        set
        {
            if (_mappingMode == value)
            {
                return;
            }

            _mappingMode = value;
            OnChanged();
        }
    }

    public GradientSpreadMethod SpreadMethod
    {
        get => _spreadMethod;
        set
        {
            if (_spreadMethod == value)
            {
                return;
            }

            _spreadMethod = value;
            OnChanged();
        }
    }

    public GradientStopCollection GradientStops
    {
        get => _gradientStops;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_gradientStops, value))
            {
                return;
            }

            _gradientStops.Changed -= OnGradientStopsChanged;
            _gradientStops = value;
            _gradientStops.Changed += OnGradientStopsChanged;
            OnChanged();
        }
    }

    protected ProGpuStop[] ToNativeStops()
    {
        var stops = new ProGpuStop[GradientStops.Count];
        for (var i = 0; i < stops.Length; i++)
        {
            var stop = GradientStops[i];
            stops[i] = new ProGpuStop(ToVector(stop.Color), (float)stop.Offset);
        }

        return stops;
    }

    protected void ApplyGradientState(ProGpuBrush brush)
    {
        brush.Opacity = (float)Math.Clamp(Opacity, 0.0, 1.0);
        switch (brush)
        {
            case ProGpuLinearGradientBrush linear:
                linear.SpreadMethod = ToNativeSpreadMethod(SpreadMethod);
                linear.ColorInterpolationMode = ToNativeColorInterpolationMode(ColorInterpolationMode);
                break;
            case ProGpuRadialGradientBrush radial:
                radial.SpreadMethod = ToNativeSpreadMethod(SpreadMethod);
                radial.ColorInterpolationMode = ToNativeColorInterpolationMode(ColorInterpolationMode);
                break;
        }
    }

    protected static Vector4 ToVector(Color color)
    {
        return new Vector4(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);
    }

    protected static Point MapRelativePoint(Point point, Rect bounds)
    {
        if (bounds.IsEmpty)
        {
            return point;
        }

        return new Point(
            bounds.X + point.X * bounds.Width,
            bounds.Y + point.Y * bounds.Height);
    }

    private void OnGradientStopsChanged(object? sender, EventArgs e)
    {
        OnChanged();
    }

    private static ProGpuGradientSpreadMethod ToNativeSpreadMethod(GradientSpreadMethod spreadMethod)
    {
        return spreadMethod switch
        {
            GradientSpreadMethod.Reflect => ProGpuGradientSpreadMethod.Reflect,
            GradientSpreadMethod.Repeat => ProGpuGradientSpreadMethod.Repeat,
            _ => ProGpuGradientSpreadMethod.Pad
        };
    }

    private static ProGpuColorInterpolationMode ToNativeColorInterpolationMode(ColorInterpolationMode colorInterpolationMode)
    {
        return colorInterpolationMode == ColorInterpolationMode.ScRgbLinearInterpolation
            ? ProGpuColorInterpolationMode.ScRgbLinearInterpolation
            : ProGpuColorInterpolationMode.SRgbLinearInterpolation;
    }
}

public sealed class LinearGradientBrush : GradientBrush
{
    private Point _startPoint = new(0, 0);
    private Point _endPoint = new(1, 1);

    public LinearGradientBrush()
    {
    }

    public LinearGradientBrush(Color startColor, Color endColor, double angle)
    {
        GradientStops.Add(new GradientStop(startColor, 0));
        GradientStops.Add(new GradientStop(endColor, 1));
        SetPointsFromAngle(angle);
    }

    public LinearGradientBrush(Color startColor, Color endColor, Point startPoint, Point endPoint)
    {
        GradientStops.Add(new GradientStop(startColor, 0));
        GradientStops.Add(new GradientStop(endColor, 1));
        _startPoint = startPoint;
        _endPoint = endPoint;
    }

    public LinearGradientBrush(GradientStopCollection gradientStopCollection)
    {
        GradientStops = gradientStopCollection;
    }

    public LinearGradientBrush(GradientStopCollection gradientStopCollection, Point startPoint, Point endPoint)
    {
        GradientStops = gradientStopCollection;
        _startPoint = startPoint;
        _endPoint = endPoint;
    }

    public Point StartPoint
    {
        get => _startPoint;
        set
        {
            if (Equals(_startPoint, value))
            {
                return;
            }

            _startPoint = value;
            OnChanged();
        }
    }

    public Point EndPoint
    {
        get => _endPoint;
        set
        {
            if (Equals(_endPoint, value))
            {
                return;
            }

            _endPoint = value;
            OnChanged();
        }
    }

    public override ProGpuBrush ToNative()
    {
        return CreateNativeBrush(StartPoint, EndPoint);
    }

    public override ProGpuBrush ToNative(Rect targetBounds)
    {
        if (MappingMode != BrushMappingMode.RelativeToBoundingBox)
        {
            return ToNative();
        }

        return CreateNativeBrush(
            MapRelativePoint(StartPoint, targetBounds),
            MapRelativePoint(EndPoint, targetBounds));
    }

    private ProGpuBrush CreateNativeBrush(Point startPoint, Point endPoint)
    {
        var brush = new ProGpuLinearGradientBrush(
            new Vector2((float)startPoint.X, (float)startPoint.Y),
            new Vector2((float)endPoint.X, (float)endPoint.Y),
            ToNativeStops());
        ApplyGradientState(brush);
        return brush;
    }

    private void SetPointsFromAngle(double angle)
    {
        var radians = angle * Math.PI / 180.0;
        var dx = Math.Cos(radians) * 0.5;
        var dy = Math.Sin(radians) * 0.5;
        _startPoint = new Point(0.5 - dx, 0.5 - dy);
        _endPoint = new Point(0.5 + dx, 0.5 + dy);
    }
}

public sealed class RadialGradientBrush : GradientBrush
{
    private Point _center = new(0.5, 0.5);
    private Point _gradientOrigin = new(0.5, 0.5);
    private double _radiusX = 0.5;
    private double _radiusY = 0.5;

    public RadialGradientBrush()
    {
    }

    public RadialGradientBrush(Color startColor, Color endColor)
    {
        GradientStops.Add(new GradientStop(startColor, 0));
        GradientStops.Add(new GradientStop(endColor, 1));
    }

    public RadialGradientBrush(GradientStopCollection gradientStopCollection)
    {
        GradientStops = gradientStopCollection;
    }

    public Point Center
    {
        get => _center;
        set
        {
            if (Equals(_center, value))
            {
                return;
            }

            _center = value;
            OnChanged();
        }
    }

    public Point GradientOrigin
    {
        get => _gradientOrigin;
        set
        {
            if (Equals(_gradientOrigin, value))
            {
                return;
            }

            _gradientOrigin = value;
            OnChanged();
        }
    }

    public double RadiusX
    {
        get => _radiusX;
        set
        {
            if (_radiusX.Equals(value))
            {
                return;
            }

            _radiusX = value;
            OnChanged();
        }
    }

    public double RadiusY
    {
        get => _radiusY;
        set
        {
            if (_radiusY.Equals(value))
            {
                return;
            }

            _radiusY = value;
            OnChanged();
        }
    }

    public override ProGpuBrush ToNative()
    {
        return CreateNativeBrush(Center, GradientOrigin, RadiusX, RadiusY);
    }

    public override ProGpuBrush ToNative(Rect targetBounds)
    {
        if (MappingMode != BrushMappingMode.RelativeToBoundingBox)
        {
            return ToNative();
        }

        return CreateNativeBrush(
            MapRelativePoint(Center, targetBounds),
            MapRelativePoint(GradientOrigin, targetBounds),
            RadiusX * targetBounds.Width,
            RadiusY * targetBounds.Height);
    }

    private ProGpuBrush CreateNativeBrush(Point center, Point gradientOrigin, double radiusX, double radiusY)
    {
        var brush = new ProGpuRadialGradientBrush(
            new Vector2((float)center.X, (float)center.Y),
            new Vector2((float)gradientOrigin.X, (float)gradientOrigin.Y),
            (float)radiusX,
            (float)radiusY,
            ToNativeStops());
        ApplyGradientState(brush);
        return brush;
    }
}
