using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

public class DragStartedEventArgs : EventArgs
{
    public float HorizontalOffset { get; }
    public float VerticalOffset { get; }

    public DragStartedEventArgs(float horizontalOffset, float verticalOffset)
    {
        HorizontalOffset = horizontalOffset;
        VerticalOffset = verticalOffset;
    }
}

public class DragDeltaEventArgs : EventArgs
{
    public float HorizontalChange { get; }
    public float VerticalChange { get; }

    public DragDeltaEventArgs(float horizontalChange, float verticalChange)
    {
        HorizontalChange = horizontalChange;
        VerticalChange = verticalChange;
    }
}

public class DragCompletedEventArgs : EventArgs
{
    public float HorizontalOffset { get; }
    public float VerticalOffset { get; }
    public bool Canceled { get; }

    public DragCompletedEventArgs(float horizontalOffset, float verticalOffset, bool canceled = false)
    {
        HorizontalOffset = horizontalOffset;
        VerticalOffset = verticalOffset;
        Canceled = canceled;
    }
}

public delegate void DragStartedEventHandler(object sender, DragStartedEventArgs e);
public delegate void DragDeltaEventHandler(object sender, DragDeltaEventArgs e);
public delegate void DragCompletedEventHandler(object sender, DragCompletedEventArgs e);

public class Thumb : Control
{
    public event DragStartedEventHandler? DragStarted;
    public event DragDeltaEventHandler? DragDelta;
    public event DragCompletedEventHandler? DragCompleted;

    private bool _isDragging;
    private Vector2 _startPos;
    private Vector2 _lastPos;

    public Thumb()
    {
        IsTabStop = false;
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            _isDragging = true;
            InputSystem.CapturePointer(this);
            _startPos = e.ScreenPosition;
            _lastPos = e.ScreenPosition;
            DragStarted?.Invoke(this, new DragStartedEventArgs(e.ScreenPosition.X, e.ScreenPosition.Y));
            base.OnPointerPressed(e);
        }
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (_isDragging && IsEnabled)
        {
            var delta = e.ScreenPosition - _lastPos;
            _lastPos = e.ScreenPosition;
            DragDelta?.Invoke(this, new DragDeltaEventArgs(delta.X, delta.Y));
        }
        base.OnPointerMoved(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            InputSystem.ReleasePointerCapture();
            DragCompleted?.Invoke(this, new DragCompletedEventArgs(e.ScreenPosition.X, e.ScreenPosition.Y));
        }
        base.OnPointerReleased(e);
     }

    public override void OnRender(DrawingContext context)
    {
        var bg = GetCurrentBackground();
        var border = GetCurrentBorderBrush();
        var thickness = BorderThickness;

        if (bg != null || (border != null && thickness.Left > 0f))
        {
            var pen = border != null && thickness.Left > 0f ? new Pen(border, thickness.Left) : null;
            context.DrawRoundedRectangle(bg, pen, new Rect(Vector2.Zero, Size), CornerRadius);
        }

        base.OnRender(context);
    }
}

