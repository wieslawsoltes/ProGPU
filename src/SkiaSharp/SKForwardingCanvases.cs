using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;

namespace SkiaSharp;

/// <summary>
/// Records canvas state without attaching it to a raster destination.
/// </summary>
public class SKNoDrawCanvas : SKCanvas
{
    public SKNoDrawCanvas(int width, int height)
        : base(
            new DrawingContext(),
            ValidateExtent(width, nameof(width)),
            ValidateExtent(height, nameof(height)),
            isPictureRecording: false,
            deferMaskFilters: true)
    {
    }

    private static int ValidateExtent(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        return value;
    }
}

/// <summary>
/// Fans one retained command stream out to multiple canvases.
/// </summary>
public class SKNWayCanvas : SKNoDrawCanvas
{
    private readonly List<SKCanvas> _canvases = new();
    private readonly bool _overdraw;

    public SKNWayCanvas(int width, int height)
        : this(width, height, overdraw: false)
    {
    }

    internal SKNWayCanvas(int width, int height, bool overdraw)
        : base(width, height)
    {
        _overdraw = overdraw;
        DrawingContext.SubscribeCommandAdded(ForwardCommand);
    }

    public void AddCanvas(SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (ReferenceEquals(canvas, this))
        {
            throw new ArgumentException(
                "A forwarding canvas cannot target itself.",
                nameof(canvas));
        }

        if (!_canvases.Contains(canvas))
        {
            _canvases.Add(canvas);
        }
    }

    public void RemoveCanvas(SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        _canvases.Remove(canvas);
    }

    public void RemoveAll() => _canvases.Clear();

    private void ForwardCommand(int commandIndex)
    {
        for (var index = 0; index < _canvases.Count; index++)
        {
            var destination = _canvases[index].DrawingContext;
            if (_overdraw)
            {
                destination.PushBlendMode(GpuBlendMode.Plus);
                AppendOverdrawCommand(destination, commandIndex);
                destination.PopBlendMode();
            }
            else
            {
                destination.AppendCommand(DrawingContext, commandIndex);
            }
        }
    }

    private void AppendOverdrawCommand(DrawingContext destination, int commandIndex)
    {
        var command = DrawingContext.Commands[commandIndex];
        var coverageBrush = new SolidColorBrush(
            new Vector4(1f, 1f, 1f, 1f / 255f));
        command.Brush = command.Brush switch
        {
            SKMaskFilterBrush mask => new SKMaskFilterBrush(coverageBrush, mask.Filter),
            null => null,
            _ => coverageBrush,
        };
        if (command.Pen != null)
        {
            Brush penBrush = command.Pen.Brush is SKMaskFilterBrush mask
                ? new SKMaskFilterBrush(coverageBrush, mask.Filter)
                : coverageBrush;
            command.Pen = new Pen(
                penBrush,
                command.Pen.Thickness,
                command.Pen.LineJoin,
                command.Pen.MiterLimit,
                command.Pen.StartLineCap,
                command.Pen.EndLineCap,
                command.Pen.DashCap,
                command.Pen.DashArray,
                command.Pen.DashOffset,
                command.Pen.StrokeTransformMode);
        }

        destination.AppendCommand(DrawingContext, commandIndex, command);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DrawingContext.UnsubscribeCommandAdded(ForwardCommand);
            _canvases.Clear();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Forwards coverage to a target canvas with additive alpha accumulation.
/// </summary>
public class SKOverdrawCanvas : SKNWayCanvas
{
    public SKOverdrawCanvas(SKCanvas canvas)
        : base(
            checked((int)MathF.Ceiling(
                (canvas ?? throw new ArgumentNullException(nameof(canvas))).CanvasWidth)),
            checked((int)MathF.Ceiling(canvas.CanvasHeight)),
            overdraw: true)
    {
        AddCanvas(canvas);
    }
}
