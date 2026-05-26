using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public static class RatingControlPage
{
    private static double _rating1 = 3.0;
    private static double _rating2 = 7.0;

    public static FrameworkElement Create()
    {
        var scrollViewer = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        scrollViewer.Content = stack;

        // Page Header
        var title = new RichTextBlock { Font = AppState._font, FontSize = 22f, Margin = new Thickness(0, 0, 0, 6) };
        title.Inlines.Add(new Bold(new Run("RatingControl Widget")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 24) };
        description.Inlines.Add(new Run("RatingControl provides an interactive 5-star or custom star count rating UX. It supports custom polar-vector star path rendering, mouse hover glows, and incremental arrow-key adjustments."));
        stack.AddChild(description);

        var ratingCard = CreateShowcaseCard("Interactive Rating Showcase");
        var ratingStack = new StackPanel { Orientation = Orientation.Vertical };

        var ratingDesc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 16) };
        ratingDesc.Inlines.Add(new Run("Move the mouse to hover, click to select, or focus and use "));
        ratingDesc.Inlines.Add(new Bold(new Run("Arrow Keys")));
        ratingDesc.Inlines.Add(new Run(" to increment/decrement rating value."));
        ratingStack.AddChild(ratingDesc);

        // Rating A: Standard 5-star
        var rc1Title = new RichTextBlock { Font = AppState._font, FontSize = 13f, Margin = new Thickness(0, 0, 0, 8) };
        rc1Title.Inlines.Add(new Bold(new Run("Standard 5-Star Interactive Rating:")));
        ratingStack.AddChild(rc1Title);

        var rc1Container = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
        var rc1 = new RatingControl { Value = _rating1, MaxRating = 5 };
        var rc1Status = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(15, 6, 0, 0) };
        rc1Status.Inlines.Add(new Run($"Rating: {rc1.Value:F1} / 5.0"));

        rc1.ValueChanged += (s, val) =>
        {
            _rating1 = val;
            rc1Status.Inlines.Clear();
            rc1Status.Inlines.Add(new Run($"Rating: {_rating1:F1} / 5.0"));
            rc1Status.Invalidate();
        };

        rc1Container.AddChild(rc1);
        rc1Container.AddChild(rc1Status);
        ratingStack.AddChild(rc1Container);

        // Rating B: 10-star rating
        var rc2Title = new RichTextBlock { Font = AppState._font, FontSize = 13f, Margin = new Thickness(0, 0, 0, 8) };
        rc2Title.Inlines.Add(new Bold(new Run("High Capacity 10-Star Rating:")));
        ratingStack.AddChild(rc2Title);

        var rc2Container = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
        var rc2 = new RatingControl { Value = _rating2, MaxRating = 10 };
        var rc2Status = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(15, 6, 0, 0) };
        rc2Status.Inlines.Add(new Run($"Score: {rc2.Value:F1} / 10.0"));

        rc2.ValueChanged += (s, val) =>
        {
            _rating2 = val;
            rc2Status.Inlines.Clear();
            rc2Status.Inlines.Add(new Run($"Score: {_rating2:F1} / 10.0"));
            rc2Status.Invalidate();
        };

        rc2Container.AddChild(rc2);
        rc2Container.AddChild(rc2Status);
        ratingStack.AddChild(rc2Container);

        // Rating C: Read-only and placeholder
        var rc3Title = new RichTextBlock { Font = AppState._font, FontSize = 13f, Margin = new Thickness(0, 0, 0, 8) };
        rc3Title.Inlines.Add(new Bold(new Run("Read-only fractional rating with Placeholder stars:")));
        ratingStack.AddChild(rc3Title);

        var rc3 = new RatingControl { IsReadOnly = true, PlaceholderValue = 3.5, Value = 0.0, Margin = new Thickness(0, 0, 0, 8) };
        var rc3Desc = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 8) };
        rc3Desc.Inlines.Add(new Run("Shows PlaceholderValue = 3.5. Disallows mouse clicks and keyboard focus."));

        ratingStack.AddChild(rc3);
        ratingStack.AddChild(rc3Desc);

        ratingCard.Child = ratingStack;
        stack.AddChild(ratingCard);

        return scrollViewer;
    }

    private static Border CreateShowcaseCard(string headerText)
    {
        var border = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var mainStack = new StackPanel { Orientation = Orientation.Vertical };
        var header = new RichTextBlock { Font = AppState._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 16) };
        header.Inlines.Add(new Bold(new Run(headerText)));
        
        mainStack.AddChild(header);
        
        // Horizontal divider stripe
        var divider = new Border
        {
            Height = 1f,
            Background = new ThemeResourceBrush("ControlBorder"),
            Margin = new Thickness(0, 0, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        mainStack.AddChild(divider);

        // Content placeholder panel
        var contentPanel = new Border { HorizontalAlignment = HorizontalAlignment.Stretch };
        mainStack.AddChild(contentPanel);

        border.Child = mainStack;

        // Custom content wrapper
        border.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Border.Child) && border.Child != mainStack)
            {
                var content = border.Child;
                border.Child = mainStack;
                contentPanel.Child = content;
            }
        };

        return border;
    }
}
