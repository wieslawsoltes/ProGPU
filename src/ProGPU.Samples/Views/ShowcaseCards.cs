using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
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

public class KeyframeShowcaseCard : Border, IAnimatedElement
{
    private readonly TtfFont _font;
    private readonly KeyframeAnimation<Vector2> _offsetAnimation;
    private readonly KeyframeAnimation<float> _opacityAnimation;
    private readonly KeyframeAnimation<float> _rotationAnimation;

    private readonly Border _slidingCard;
    private readonly RichTextBlock _fadingText;
    private readonly GearVisual _gearVisual;

    public KeyframeShowcaseCard(TtfFont font)
    {
        _font = font;
        Background = new ThemeResourceBrush("CardBackground");
        BorderBrush = new ThemeResourceBrush("ControlBorder");
        BorderThickness = new Thickness(1f);
        CornerRadius = 8f;
        Padding = new Thickness(12);
        Margin = new Thickness(6);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var title = new RichTextBlock { Font = font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
        title.Inlines.Add(new Bold(new Run("Keyframe Showcase")));
        stack.AddChild(title);

        var desc = new RichTextBlock { Font = font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        desc.Inlines.Add(new Run("Looping scalar/vector translations. Notice the sliding offset card, fading opacity text, and spinning gear."));
        stack.AddChild(desc);

        // 1. Sliding card
        var slidingContainer = new Border
        {
            Height = 80f,
            Background = new ThemeResourceBrush("ControlBackground"),
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 0, 15),
            Padding = new Thickness(8)
        };
        var canvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        _slidingCard = new Border
        {
            Width = 60f,
            Height = 40f,
            Background = new SolidColorBrush(0x0078D4FF),
            CornerRadius = 4f
        };
        var slidingText = new RichTextBlock { Font = font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        slidingText.Inlines.Add(new Bold(new Run("Slide")));
        _slidingCard.Child = slidingText;
        canvas.AddChild(_slidingCard);
        slidingContainer.Child = canvas;
        stack.AddChild(slidingContainer);

        // 2. Fading opacity text
        var fadingContainer = new Border
        {
            Height = 50f,
            Background = new ThemeResourceBrush("ControlBackground"),
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 0, 15),
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _fadingText = new RichTextBlock
        {
            Font = font,
            FontSize = 14f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _fadingText.Inlines.Add(new Bold(new Run("FADING OPACITY TEXT")));
        fadingContainer.Child = _fadingText;
        stack.AddChild(fadingContainer);

        // 3. Spinning Gear
        var gearContainer = new Border
        {
            Height = 120f,
            Background = new ThemeResourceBrush("ControlBackground"),
            CornerRadius = 6f,
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _gearVisual = new GearVisual
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        gearContainer.Child = _gearVisual;
        stack.AddChild(gearContainer);

        Child = stack;

        // Initialize keyframes
        _offsetAnimation = new KeyframeAnimation<Vector2> { Duration = 3f, Loop = true };
        _offsetAnimation.Keyframes.Add((0f, new Vector2(10f, 10f)));
        _offsetAnimation.Keyframes.Add((0.25f, new Vector2(120f, 10f)));
        _offsetAnimation.Keyframes.Add((0.5f, new Vector2(120f, 30f)));
        _offsetAnimation.Keyframes.Add((0.75f, new Vector2(10f, 30f)));
        _offsetAnimation.Keyframes.Add((1f, new Vector2(10f, 10f)));

        _opacityAnimation = new KeyframeAnimation<float> { Duration = 2.5f, Loop = true };
        _opacityAnimation.Keyframes.Add((0f, 0.1f));
        _opacityAnimation.Keyframes.Add((0.5f, 1.0f));
        _opacityAnimation.Keyframes.Add((1f, 0.1f));

        _rotationAnimation = new KeyframeAnimation<float> { Duration = 4f, Loop = true };
        _rotationAnimation.Keyframes.Add((0f, 0f));
        _rotationAnimation.Keyframes.Add((0.5f, (float)Math.PI));
        _rotationAnimation.Keyframes.Add((1f, (float)(Math.PI * 2f)));
    }

    public void Update(float delta)
    {
        _offsetAnimation.Update(delta);
        _opacityAnimation.Update(delta);
        _rotationAnimation.Update(delta);

        Vector2 currentOffset = _offsetAnimation.Evaluate((a, b, t) => Vector2.Lerp(a, b, t));
        Canvas.SetLeft(_slidingCard, currentOffset.X);
        Canvas.SetTop(_slidingCard, currentOffset.Y);

        float currentOpacity = _opacityAnimation.Evaluate((a, b, t) => a + (b - a) * t);
        _fadingText.Opacity = currentOpacity;

        float currentRotation = _rotationAnimation.Evaluate((a, b, t) => a + (b - a) * t);
        _gearVisual.GearRotation = currentRotation;

        Invalidate();
    }
}

public class SpringWobbleShowcaseCard : Border, IAnimatedElement
{
    private readonly SpringScalarNaturalMotionAnimation _springX;
    private readonly SpringScalarNaturalMotionAnimation _springY;
    private readonly Button _triggerBtn;
    private readonly Border _wobbleCard;

    public SpringWobbleShowcaseCard(TtfFont font)
    {
        _springX = new SpringScalarNaturalMotionAnimation
        {
            CurrentValue = 1.0f,
            TargetValue = 1.0f,
            Stiffness = 180f,
            Damping = 10f,
            Mass = 1.0f
        };
        _springY = new SpringScalarNaturalMotionAnimation
        {
            CurrentValue = 1.0f,
            TargetValue = 1.0f,
            Stiffness = 180f,
            Damping = 10f,
            Mass = 1.0f
        };

        Background = new ThemeResourceBrush("CardBackground");
        BorderBrush = new ThemeResourceBrush("ControlBorder");
        BorderThickness = new Thickness(1f);
        CornerRadius = 8f;
        Padding = new Thickness(12);
        Margin = new Thickness(6);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var title = new RichTextBlock { Font = font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
        title.Inlines.Add(new Bold(new Run("Spring Wobble Showcase")));
        stack.AddChild(title);

        var desc = new RichTextBlock { Font = font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        desc.Inlines.Add(new Run("Natural spring mass-damping physics. Click the button to trigger a high-frequency elastic spring wobble on Scale."));
        stack.AddChild(desc);

        var wobbleContainer = new Border
        {
            Height = 150f,
            Background = new ThemeResourceBrush("ControlBackground"),
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 0, 15),
            Padding = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _wobbleCard = new Border
        {
            Width = 120f,
            Height = 80f,
            Background = new SolidColorBrush(0x0078D4FF),
            BorderBrush = new SolidColorBrush(0xFFFFFFFF),
            BorderThickness = new Thickness(1.5f),
            CornerRadius = 10f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var wobbleText = new RichTextBlock { Font = font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        wobbleText.Inlines.Add(new Bold(new Run("WOBBLE ME!")));
        _wobbleCard.Child = wobbleText;
        wobbleContainer.Child = _wobbleCard;
        stack.AddChild(wobbleContainer);

        _triggerBtn = new Button { Width = 150f, Height = 36f, CornerRadius = 6f, HorizontalAlignment = HorizontalAlignment.Center };
        var btnText = new RichTextBlock { Font = font, FontSize = 12f };
        btnText.Inlines.Add(new Bold(new Run("Trigger Spring")));
        _triggerBtn.Content = btnText;

        _triggerBtn.Click += (s, e) =>
        {
            _springX.CurrentValue = 0.5f;
            _springY.CurrentValue = 1.5f;
            _springX.TargetValue = 1.0f;
            _springY.TargetValue = 1.0f;
            _springX.Velocity = 20f;
            _springY.Velocity = -20f;
        };
        stack.AddChild(_triggerBtn);

        Child = stack;
    }

    public void Update(float delta)
    {
        _springX.Update(delta);
        _springY.Update(delta);

        Vector2 size = _wobbleCard.Size;
        if (size.X <= 0f || size.Y <= 0f || float.IsNaN(size.X) || float.IsNaN(size.Y))
        {
            float w = _wobbleCard.Width;
            float h = _wobbleCard.Height;
            if (float.IsNaN(w) || w <= 0f) w = 120f;
            if (float.IsNaN(h) || h <= 0f) h = 80f;
            size = new Vector2(w, h);
        }
        Vector2 center = size / 2f;

        float sx = _springX.CurrentValue;
        float sy = _springY.CurrentValue;

        sx = Math.Max(0.1f, Math.Min(3.0f, sx));
        sy = Math.Max(0.1f, Math.Min(3.0f, sy));

        var transform = Matrix4x4.CreateTranslation(-center.X, -center.Y, 0)
                        * Matrix4x4.CreateScale(sx, sy, 1f)
                        * Matrix4x4.CreateTranslation(center.X, center.Y, 0);
        _wobbleCard.Transform = transform;
    }
}

public class ExpressionTrackingShowcaseCard : Border, IAnimatedElement
{
    private readonly Slider _slider;
    private readonly ExpressionAnimation _scaleExpression;
    private readonly ExpressionAnimation _rotationExpression;
    private readonly Border _trackingCard;

    public ExpressionTrackingShowcaseCard(TtfFont font)
    {
        Background = new ThemeResourceBrush("CardBackground");
        BorderBrush = new ThemeResourceBrush("ControlBorder");
        BorderThickness = new Thickness(1f);
        CornerRadius = 8f;
        Padding = new Thickness(12);
        Margin = new Thickness(6);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var title = new RichTextBlock { Font = font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
        title.Inlines.Add(new Bold(new Run("Expression Showcase")));
        stack.AddChild(title);

        var desc = new RichTextBlock { Font = font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
        desc.Inlines.Add(new Run("Dynamic ExpressionAnimation binding. Move the slider below to drive the card's Scale and Rotation in real time."));
        stack.AddChild(desc);

        var trackingContainer = new Border
        {
            Height = 150f,
            Background = new ThemeResourceBrush("ControlBackground"),
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 0, 15),
            Padding = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _trackingCard = new Border
        {
            Width = 100f,
            Height = 80f,
            Background = new SolidColorBrush(0x00E5FFFF),
            BorderBrush = new SolidColorBrush(0xFFFFFFFF),
            BorderThickness = new Thickness(1.5f),
            CornerRadius = 10f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var cardText = new RichTextBlock { Font = font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cardText.Inlines.Add(new Bold(new Run("TRACKING")));
        _trackingCard.Child = cardText;
        trackingContainer.Child = _trackingCard;
        stack.AddChild(trackingContainer);

        var sliderTitle = new RichTextBlock { Font = font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 5) };
        sliderTitle.Inlines.Add(new Bold(new Run("Driver Slider: 50%")));
        stack.AddChild(sliderTitle);

        _slider = new Slider { Minimum = 0f, Maximum = 100f, Value = 50f, Width = 220f, HorizontalAlignment = HorizontalAlignment.Center };
        _slider.ValueChanged += (s, e) =>
        {
            sliderTitle.Inlines.Clear();
            sliderTitle.Inlines.Add(new Bold(new Run($"Driver Slider: {_slider.Value:F0}%")));
            sliderTitle.Invalidate();
        };
        stack.AddChild(_slider);

        Child = stack;

        _scaleExpression = new ExpressionAnimation(() => 0.6f + (_slider.Value / 100f) * 0.8f);
        _rotationExpression = new ExpressionAnimation(() => (_slider.Value / 100f) * (float)Math.PI * 2f);
    }

    public void Update(float delta)
    {
        float scale = _scaleExpression.Evaluate();
        float rotation = _rotationExpression.Evaluate();

        Vector2 size = _trackingCard.Size;
        if (size.X <= 0f || size.Y <= 0f || float.IsNaN(size.X) || float.IsNaN(size.Y))
        {
            float w = _trackingCard.Width;
            float h = _trackingCard.Height;
            if (float.IsNaN(w) || w <= 0f) w = 100f;
            if (float.IsNaN(h) || h <= 0f) h = 80f;
            size = new Vector2(w, h);
        }
        Vector2 center = size / 2f;

        var transform = Matrix4x4.CreateTranslation(-center.X, -center.Y, 0)
                        * Matrix4x4.CreateScale(scale, scale, 1f)
                        * Matrix4x4.CreateRotationZ(rotation)
                        * Matrix4x4.CreateTranslation(center.X, center.Y, 0);
        _trackingCard.Transform = transform;
    }
}
