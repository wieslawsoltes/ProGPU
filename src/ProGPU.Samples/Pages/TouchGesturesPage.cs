using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.Samples;

public static class TouchGesturesPage
{
    public static FrameworkElement Create()
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(16),
            Spacing = 12
        };
        var title = new RichTextBlock { FontSize = 24f };
        title.Inlines.Add(new Bold(new Run("Touch, gestures & mobile input")));
        content.AddChild(title);

        var description = new RichTextBlock { FontSize = 13f };
        description.Inlines.Add(new Run("Use one or two contacts on the gesture surface. Drag pans, pinch scales, twist rotates, tap/double-tap/hold are routed independently, and the cards reflow at the WinUI adaptive breakpoint."));
        content.AddChild(description);

        var responsiveRow = new StackPanel
        {
            Name = "ResponsiveGestureRow",
            Orientation = Orientation.Vertical,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var gesturePad = new GesturePad
        {
            Height = 260f,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        responsiveRow.AddChild(CreateCard("DIRECT MANIPULATION", gesturePad));

        var inputStack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 10 };
        var email = new TextBox
        {
            PlaceholderText = "Email address",
            Width = float.NaN,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            EnterKeyHint = "next",
            AutoCapitalization = "off"
        };
        email.InputScope.Names.Add(new InputScopeName { NameValue = InputScopeNameValue.EmailSmtpAddress });
        var number = new TextBox
        {
            PlaceholderText = "Decimal number",
            Width = float.NaN,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            EnterKeyHint = "done",
            IsSpellCheckEnabled = false
        };
        number.InputScope.Names.Add(new InputScopeName { NameValue = InputScopeNameValue.Number });
        var multiline = new TextBox
        {
            PlaceholderText = "Multiline mobile input",
            Width = float.NaN,
            Height = 72f,
            AcceptsReturn = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        inputStack.AddChild(email);
        inputStack.AddChild(number);
        inputStack.AddChild(multiline);
        inputStack.AddChild(new PasswordBox { Width = float.NaN, HorizontalAlignment = HorizontalAlignment.Stretch });
        responsiveRow.AddChild(CreateCard("SOFTWARE KEYBOARD SCOPES", inputStack));
        content.AddChild(responsiveRow);

        var states = new VisualStateGroup { Name = "ResponsiveColumns" };
        var narrow = new VisualState { Name = "Narrow" };
        narrow.StateTriggers.Add(new AdaptiveTrigger { MinWindowWidth = 0 });
        narrow.Setters.Add(new Setter("Orientation", Orientation.Vertical));
        var wide = new VisualState { Name = "Wide" };
        wide.StateTriggers.Add(new AdaptiveTrigger { MinWindowWidth = 720 });
        wide.Setters.Add(new Setter("Orientation", Orientation.Horizontal));
        states.States.Add(narrow);
        states.States.Add(wide);
        VisualStateManager.GetVisualStateGroups(responsiveRow).Add(states);

        for (var index = 0; index < 8; index++)
        {
            var line = new RichTextBlock { FontSize = 13f, Margin = new Thickness(4) };
            line.Inlines.Add(new Run($"Scrollable touch row {index + 1} — pan anywhere outside the gesture surface."));
            content.AddChild(line);
        }

        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    private static Border CreateCard(string header, FrameworkElement child)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8 };
        var title = new RichTextBlock { FontSize = 12f };
        title.Inlines.Add(new Bold(new Run(header)));
        stack.AddChild(title);
        stack.AddChild(child);
        return new Border
        {
            Child = stack,
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 10f,
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private sealed class GesturePad : Control
    {
        private readonly Brush _background = new ThemeResourceBrush("ControlBackground");
        private readonly Brush _accent = new ThemeResourceBrush("SystemAccentColor");
        private readonly Brush _foreground = new ThemeResourceBrush("TextPrimary");
        private readonly Brush _contact = new ThemeResourceBrush("SelectionHighlight");
        private readonly Pen _border = new(new ThemeResourceBrush("ControlBorder"), 1f);
        private readonly Pen _direction = new(new ThemeResourceBrush("TextPrimary"), 3f);
        private readonly Dictionary<uint, Vector2> _contacts = new();
        private string _status = "Ready — tap, hold, drag, pinch, or rotate";
        private Vector2 _translation;
        private float _scale = 1f;
        private float _rotation;

        public GesturePad()
        {
            ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY |
                ManipulationModes.Rotate | ManipulationModes.Scale |
                ManipulationModes.TranslateInertia | ManipulationModes.RotateInertia | ManipulationModes.ScaleInertia;
            CornerRadius = 12f;
            IsTabStop = true;
        }

        public override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            _contacts[e.Pointer.PointerId] = e.Position;
            CapturePointer(e.Pointer);
            _status = $"Pointer {e.Pointer.PointerId} · {e.Pointer.PointerDeviceType} · pressure {e.Pressure:0.00}";
            Invalidate();
            base.OnPointerPressed(e);
        }

        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            if (_contacts.ContainsKey(e.Pointer.PointerId)) _contacts[e.Pointer.PointerId] = e.Position;
            Invalidate();
            base.OnPointerMoved(e);
        }

        public override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            _contacts.Remove(e.Pointer.PointerId);
            ReleasePointerCapture(e.Pointer);
            Invalidate();
            base.OnPointerReleased(e);
        }

        public override void OnPointerCanceled(PointerRoutedEventArgs e)
        {
            _contacts.Remove(e.Pointer.PointerId);
            Invalidate();
            base.OnPointerCanceled(e);
        }

        public override void OnTapped(TappedRoutedEventArgs e)
        {
            _status = $"Tapped with {e.PointerDeviceType}";
            Invalidate();
            base.OnTapped(e);
        }

        public override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
        {
            _translation = Vector2.Zero;
            _scale = 1f;
            _rotation = 0f;
            _status = "Double tapped — transform reset";
            Invalidate();
            base.OnDoubleTapped(e);
        }

        public override void OnHolding(HoldingRoutedEventArgs e)
        {
            _status = $"Holding {e.HoldingState}";
            Invalidate();
            base.OnHolding(e);
        }

        public override void OnManipulationDelta(ManipulationDeltaRoutedEventArgs e)
        {
            _translation += e.Delta.Translation;
            _scale = Math.Clamp(_scale * e.Delta.Scale, 0.5f, 2.5f);
            _rotation += e.Delta.Rotation;
            _status = $"Δ {e.Delta.Translation.X:0.0},{e.Delta.Translation.Y:0.0} · scale {_scale:0.00} · rotation {_rotation:0.0}°";
            Invalidate();
            e.Handled = true;
            base.OnManipulationDelta(e);
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRoundedRectangle(_background, _border, new Rect(Vector2.Zero, Size), (float)CornerRadius.TopLeft);

            var center = Size * 0.5f + _translation * 0.2f;
            var radius = 34f * _scale;
            context.DrawEllipse(_accent, null, center, radius, radius);
            var angle = _rotation * MathF.PI / 180f;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            context.DrawLine(_direction, center - direction, center + direction);
            foreach (var contact in _contacts.Values)
                context.DrawEllipse(_contact, null, contact, 14f, 14f);
            if (Font != null)
                context.DrawText(_status, Font, 12f, _foreground, new Vector2(14f, Math.Max(14f, Size.Y - 30f)));
            base.OnRender(context);
        }
    }
}
