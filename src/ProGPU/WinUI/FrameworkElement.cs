using System;
using System.Numerics;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Scene;

namespace ProGPU.WinUI;

public class RoutedEventArgs : EventArgs
{
    public bool Handled { get; set; }
    public object? OriginalSource { get; set; }
}

public class KeyRoutedEventArgs : RoutedEventArgs
{
    public Key Key { get; set; }
}

public class CharacterReceivedRoutedEventArgs : RoutedEventArgs
{
    public char Character { get; set; }
}

public class PointerRoutedEventArgs : RoutedEventArgs
{
    public Vector2 Position { get; set; }       // Position relative to the element
    public Vector2 ScreenPosition { get; set; } // Position relative to the screen
    public bool IsLeftButtonPressed { get; set; }
    public float WheelDelta { get; set; }
}

public class FrameworkElement : LayoutNode
{
    public string Name { get; set; } = string.Empty;
    public object? Tag { get; set; }
    public bool IsHitTestVisible { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public object? ToolTip { get; set; }

    private Style? _style;
    private EffectBase? _effect;
    public EffectBase? Effect
    {
        get => _effect;
        set
        {
            if (_effect != value)
            {
                _effect = value;
                Invalidate();
            }
        }
    }
    public Style? Style
    {
        get => _style;
        set
        {
            if (_style != value)
            {
                _style = value;
                ApplyStyle();
            }
        }
    }

    private void ApplyStyle()
    {
        if (_style == null) return;
        if (!_style.TargetType.IsAssignableFrom(GetType())) return;

        foreach (var setter in _style.Setters)
        {
            if (string.IsNullOrEmpty(setter.Property)) continue;

            var prop = GetType().GetProperty(setter.Property);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    prop.SetValue(this, setter.Value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error applying style property '{setter.Property}' on {GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                var field = GetType().GetField(setter.Property);
                if (field != null)
                {
                    try
                    {
                        field.SetValue(this, setter.Value);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error applying style field '{setter.Property}' on {GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
    }

    public float Width
    {
        get => WidthConstraint ?? float.NaN;
        set => WidthConstraint = float.IsNaN(value) ? null : value;
    }

    public float Height
    {
        get => HeightConstraint ?? float.NaN;
        set => HeightConstraint = float.IsNaN(value) ? null : value;
    }

    // Routed Events
    public event EventHandler<PointerRoutedEventArgs>? PointerPressed;
    public event EventHandler<PointerRoutedEventArgs>? PointerReleased;
    public event EventHandler<PointerRoutedEventArgs>? PointerMoved;
    public event EventHandler<PointerRoutedEventArgs>? PointerEntered;
    public event EventHandler<PointerRoutedEventArgs>? PointerExited;
    public event EventHandler<PointerRoutedEventArgs>? PointerWheelChanged;

    public event EventHandler<KeyRoutedEventArgs>? KeyDown;
    public event EventHandler<KeyRoutedEventArgs>? KeyUp;
    public event EventHandler<CharacterReceivedRoutedEventArgs>? CharacterReceived;

    // Helper methods to trigger routed events
    public virtual void OnPointerPressed(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        PointerPressed?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnPointerPressed(e);
        }
    }

    public virtual void OnPointerReleased(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        PointerReleased?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnPointerReleased(e);
        }
    }

    public virtual void OnPointerMoved(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        PointerMoved?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnPointerMoved(e);
        }
    }

    public virtual void OnPointerEntered(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        PointerEntered?.Invoke(this, e);
    }

    public virtual void OnPointerExited(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        PointerExited?.Invoke(this, e);
    }

    public virtual void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        PointerWheelChanged?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnPointerWheelChanged(e);
        }
    }

    public virtual void OnKeyDown(KeyRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        KeyDown?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnKeyDown(e);
        }
    }

    public virtual void OnKeyUp(KeyRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        KeyUp?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnKeyUp(e);
        }
    }

    public virtual void OnCharacterReceived(CharacterReceivedRoutedEventArgs e)
    {
        e.OriginalSource ??= this;
        CharacterReceived?.Invoke(this, e);
        if (!e.Handled && Parent is FrameworkElement parentFe)
        {
            parentFe.OnCharacterReceived(e);
        }
    }
}

public abstract class EffectBase
{
}

public class BlurEffect : EffectBase
{
    public float BlurRadius { get; set; }

    public BlurEffect(float blurRadius = 5f)
    {
        BlurRadius = blurRadius;
    }
}

public class DropShadowEffect : EffectBase
{
    public float BlurRadius { get; set; }
    public Vector2 Offset { get; set; }
    public Vector4 Color { get; set; }

    public DropShadowEffect(float blurRadius = 5f, Vector2 offset = default, Vector4 color = default)
    {
        BlurRadius = blurRadius;
        Offset = offset;
        Color = color == default ? new Vector4(0f, 0f, 0f, 0.5f) : color;
    }
}
