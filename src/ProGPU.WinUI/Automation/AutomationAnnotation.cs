using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class AutomationAnnotation : DependencyObject
{
    private static readonly DependencyProperty s_elementProperty =
        DependencyProperty.Register(
            nameof(Element),
            typeof(UIElement),
            typeof(AutomationAnnotation),
            new PropertyMetadata(null));
    private static readonly DependencyProperty s_typeProperty =
        DependencyProperty.Register(
            nameof(Type),
            typeof(AnnotationType),
            typeof(AutomationAnnotation),
            new PropertyMetadata(AnnotationType.Unknown));

    public AutomationAnnotation()
    {
    }

    public AutomationAnnotation(AnnotationType type) => Type = type;

    public AutomationAnnotation(AnnotationType type, UIElement element)
    {
        Type = type;
        Element = element;
    }

    public static DependencyProperty ElementProperty => s_elementProperty;

    public static DependencyProperty TypeProperty => s_typeProperty;

    public UIElement Element
    {
        get => (UIElement)GetValue(s_elementProperty)!;
        set => SetValue(s_elementProperty, value);
    }

    public AnnotationType Type
    {
        get => (AnnotationType)(GetValue(s_typeProperty) ?? AnnotationType.Unknown);
        set => SetValue(s_typeProperty, value);
    }
}
