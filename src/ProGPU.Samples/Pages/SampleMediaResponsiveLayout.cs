using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.Samples;

internal static class SampleMediaResponsiveLayout
{
    private const float MediumWidth = 560f;
    private const float WideWidth = 900f;

    internal static void AttachPreviewStates(
        FrameworkElement stateRoot,
        FrameworkElement header,
        float compactHeight,
        float mediumHeight,
        float wideHeight,
        params FrameworkElement[] previewElements)
    {
        static Setter Setter(
            DependencyObject target,
            DependencyProperty property,
            object value) =>
            new()
            {
                Target = new TargetPropertyPath(property)
                {
                    Target = target
                },
                Value = value
            };

        static void AddPreviewHeightSetters(
            VisualState state,
            FrameworkElement[] elements,
            float height)
        {
            for (int index = 0; index < elements.Length; index++)
            {
                state.Setters.Add(
                    Setter(
                        elements[index],
                        FrameworkElement.HeightProperty,
                        height));
            }
        }

        var group = new VisualStateGroup
        {
            Name = "MediaPageWidthStates"
        };

        var compact = new VisualState { Name = "Compact" };
        compact.StateTriggers.Add(
            new AdaptiveTrigger { MinWindowWidth = 0f });
        compact.Setters.Add(
            Setter(
                header,
                FrameworkElement.MarginProperty,
                new Thickness(52f, 0f, 0f, 10f)));
        AddPreviewHeightSetters(
            compact,
            previewElements,
            compactHeight);

        var medium = new VisualState { Name = "Medium" };
        medium.StateTriggers.Add(
            new AdaptiveTrigger { MinWindowWidth = MediumWidth });
        medium.Setters.Add(
            Setter(
                header,
                FrameworkElement.MarginProperty,
                new Thickness(0f, 0f, 0f, 10f)));
        AddPreviewHeightSetters(
            medium,
            previewElements,
            mediumHeight);

        var wide = new VisualState { Name = "Wide" };
        wide.StateTriggers.Add(
            new AdaptiveTrigger { MinWindowWidth = WideWidth });
        wide.Setters.Add(
            Setter(
                header,
                FrameworkElement.MarginProperty,
                new Thickness(0f, 0f, 0f, 10f)));
        AddPreviewHeightSetters(
            wide,
            previewElements,
            wideHeight);

        group.States.Add(compact);
        group.States.Add(medium);
        group.States.Add(wide);
        VisualStateManager.GetVisualStateGroups(stateRoot).Add(group);
    }

    internal static WrapPanel CreateActionPanel()
    {
        return new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalSpacing = 6f,
            VerticalSpacing = 6f,
            Margin = new Thickness(0f, 0f, 0f, 8f)
        };
    }

    internal static ScrollViewer CreateTimelineScroller(
        FrameworkElement timeline,
        float height)
    {
        return new ScrollViewer
        {
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = timeline
        };
    }
}
