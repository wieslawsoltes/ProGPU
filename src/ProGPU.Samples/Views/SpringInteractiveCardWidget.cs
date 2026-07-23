using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public class SpringInteractiveCardWidget : Border, IAnimatedElement
{
    private readonly SpringScalarNaturalMotionAnimation _springX;
    private readonly SpringScalarNaturalMotionAnimation _springY;
    private readonly Border _widgetCard;

    public SpringInteractiveCardWidget(TtfFont font)
    {
        _springX = new SpringScalarNaturalMotionAnimation
        {
            CurrentValue = 1.0f,
            TargetValue = 1.0f,
            Stiffness = 180f,
            Damping = 12f,
            Mass = 1.0f
        };
        _springY = new SpringScalarNaturalMotionAnimation
        {
            CurrentValue = 1.0f,
            TargetValue = 1.0f,
            Stiffness = 180f,
            Damping = 12f,
            Mass = 1.0f
        };

        Height = 120f;
        Background = new SolidColorBrush(0x0C0C12FF);
        CornerRadius = 6f;
        Padding = new Thickness(12);

        var grid = new Microsoft.UI.Xaml.Controls.Grid();
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new GridLength(100f, GridUnitType.Absolute));

        var controlsStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        
        var sliderLabel = new RichTextBlock { Font = font, FontSize = 10f, Margin = new Thickness(0, 0, 0, 4) };
        sliderLabel.Inlines.Add(new Run("Spring Scale Multiplier:"));
        controlsStack.AddChild(sliderLabel);

        var slider = new Slider { Minimum = 0.5f, Maximum = 2.0f, Value = 1.0f, Margin = new Thickness(0, 0, 0, 8f) };
        slider.ValueChanged += (s, e) =>
        {
            _springX.TargetValue = (float)slider.Value;
            _springY.TargetValue = (float)slider.Value;
        };
        controlsStack.AddChild(slider);

        var triggerBtn = new Button { Height = 24f, CornerRadius = 4f };
        var btnText = new RichTextBlock { Font = font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        btnText.Inlines.Add(new Bold(new Run("Trigger Wobble")));
        triggerBtn.Content = btnText;
        triggerBtn.Click += (s, e) =>
        {
            _springX.CurrentValue = 0.4f;
            _springY.CurrentValue = 1.8f;
            _springX.Velocity = 15f;
            _springY.Velocity = -15f;
        };
        controlsStack.AddChild(triggerBtn);

        grid.AddChild(controlsStack);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(controlsStack, 0);

        _widgetCard = new Border
        {
            Width = 60f,
            Height = 60f,
            Background = new SolidColorBrush(0x0078D4FF),
            BorderBrush = new SolidColorBrush(0xFFFFFFFF),
            BorderThickness = new Thickness(1.5f),
            CornerRadius = 30f, // Perfect circle!
            HorizontalAlignment = AlignmentCenterHelper(),
            VerticalAlignment = VerticalAlignment.Center
        };
        var widgetText = new RichTextBlock { Font = font, FontSize = 9f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        widgetText.Inlines.Add(new Bold(new Run("COMP")));
        _widgetCard.Child = widgetText;

        grid.AddChild(_widgetCard);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(_widgetCard, 1);

        Child = grid;
    }

    private static HorizontalAlignment AlignmentCenterHelper() => HorizontalAlignment.Center;

    public void Update(float delta)
    {
        _springX.Update(delta);
        _springY.Update(delta);

        Vector2 size = _widgetCard.Size;
        if (size.X <= 0f || size.Y <= 0f || float.IsNaN(size.X) || float.IsNaN(size.Y))
        {
            float w = _widgetCard.Width;
            float h = _widgetCard.Height;
            if (float.IsNaN(w) || w <= 0f) w = 60f;
            if (float.IsNaN(h) || h <= 0f) h = 60f;
            size = new Vector2(w, h);
        }
        Vector2 center = size / 2f;

        float sx = _springX.CurrentValue;
        float sy = _springY.CurrentValue;

        sx = Math.Max(0.1f, Math.Min(3.0f, sx));
        sy = Math.Max(0.1f, Math.Min(3.0f, sy));

        // Create 2D scaling transform around card center
        var transform = Matrix4x4.CreateTranslation(-center.X, -center.Y, 0)
                        * Matrix4x4.CreateScale(sx, sy, 1f)
                        * Matrix4x4.CreateTranslation(center.X, center.Y, 0);
        _widgetCard.Transform = transform;
    }
}
