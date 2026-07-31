using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class AutomationElementIdentifiers
{
    private AutomationElementIdentifiers()
    {
    }

    public static AutomationProperty AcceleratorKeyProperty { get; } = new(30006);
    public static AutomationProperty AccessKeyProperty { get; } = new(30007);
    public static AutomationProperty AnnotationsProperty { get; } = new(30156);
    public static AutomationProperty AutomationIdProperty { get; } = new(30011);
    public static AutomationProperty BoundingRectangleProperty { get; } = new(30001);
    public static AutomationProperty ClassNameProperty { get; } = new(30012);
    public static AutomationProperty ClickablePointProperty { get; } = new(30014);
    public static AutomationProperty ControlTypeProperty { get; } = new(30003);
    public static AutomationProperty ControlledPeersProperty { get; } = new(30104);
    public static AutomationProperty CultureProperty { get; } = new(30015);
    public static AutomationProperty DescribedByProperty { get; } = new(30105);
    public static AutomationProperty FlowsFromProperty { get; } = new(30148);
    public static AutomationProperty FlowsToProperty { get; } = new(30106);
    public static AutomationProperty FullDescriptionProperty { get; } = new(30159);
    public static AutomationProperty HasKeyboardFocusProperty { get; } = new(30008);
    public static AutomationProperty HeadingLevelProperty { get; } = new(30173);
    public static AutomationProperty HelpTextProperty { get; } = new(30013);
    public static AutomationProperty IsContentElementProperty { get; } = new(30017);
    public static AutomationProperty IsControlElementProperty { get; } = new(30016);
    public static AutomationProperty IsDataValidForFormProperty { get; } = new(30103);
    public static AutomationProperty IsDialogProperty { get; } = new(30174);
    public static AutomationProperty IsEnabledProperty { get; } = new(30010);
    public static AutomationProperty IsKeyboardFocusableProperty { get; } = new(30009);
    public static AutomationProperty IsOffscreenProperty { get; } = new(30022);
    public static AutomationProperty IsPasswordProperty { get; } = new(30019);
    public static AutomationProperty IsPeripheralProperty { get; } = new(30150);
    public static AutomationProperty IsRequiredForFormProperty { get; } = new(30025);
    public static AutomationProperty ItemStatusProperty { get; } = new(30026);
    public static AutomationProperty ItemTypeProperty { get; } = new(30021);
    public static AutomationProperty LabeledByProperty { get; } = new(30018);
    public static AutomationProperty LandmarkTypeProperty { get; } = new(30157);
    public static AutomationProperty LevelProperty { get; } = new(30154);
    public static AutomationProperty LiveSettingProperty { get; } = new(30135);
    public static AutomationProperty LocalizedControlTypeProperty { get; } = new(30004);
    public static AutomationProperty LocalizedLandmarkTypeProperty { get; } = new(30158);
    public static AutomationProperty NameProperty { get; } = new(30005);
    public static AutomationProperty OrientationProperty { get; } = new(30023);
    public static AutomationProperty PositionInSetProperty { get; } = new(30152);
    public static AutomationProperty SizeOfSetProperty { get; } = new(30153);
}
