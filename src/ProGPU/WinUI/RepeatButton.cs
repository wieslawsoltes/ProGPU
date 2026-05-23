using System;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ProGPU.Layout;
using ProGPU.Scene;

namespace ProGPU.WinUI;

public class RepeatButton : Button
{
    private int _delay = 250;
    private int _interval = 50;
    private CancellationTokenSource? _cts;

    public int Delay
    {
        get => _delay;
        set => _delay = value;
    }

    public int Interval
    {
        get => _interval;
        set => _interval = value;
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (IsEnabled)
        {
            TriggerClick();

            CancelRepeat();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Delay, token);
                    while (!token.IsCancellationRequested && IsEnabled && IsPointerPressed && IsPointerOver)
                    {
                        TriggerClick();
                        await Task.Delay(Interval, token);
                    }
                }
                catch (TaskCanceledException)
                {
                }
            }, token);
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        CancelRepeat();
        
        if (IsEnabled)
        {
            IsPointerPressed = false;
        }

        base.OnPointerReleased(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        CancelRepeat();
        base.OnPointerExited(e);
    }

    private void CancelRepeat()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private void TriggerClick()
    {
        var field = typeof(Button).GetField("Click", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field != null && field.GetValue(this) is EventHandler handler)
        {
            handler.Invoke(this, EventArgs.Empty);
        }
    }
}
