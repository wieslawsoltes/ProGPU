namespace Microsoft.UI.Xaml.Controls;

/// <summary>Attached metadata used by the media transport control template.</summary>
public sealed class MediaTransportControlsHelper : DependencyObject
{
    private MediaTransportControlsHelper()
    {
    }

    public static readonly DependencyProperty DropoutOrderProperty = DependencyProperty.RegisterAttached(
        "DropoutOrder", typeof(int?), typeof(MediaTransportControlsHelper), new PropertyMetadata(null));

    public static int? GetDropoutOrder(UIElement element) =>
        (int?)element.GetValue(DropoutOrderProperty);

    public static void SetDropoutOrder(
        UIElement element,
        int? value)
    {
        element.SetValue(DropoutOrderProperty, value);
        if (element.Parent is
            MediaTransportControls controls)
        {
            controls.InvalidateMeasure();
            controls.Invalidate();
        }
    }
}
