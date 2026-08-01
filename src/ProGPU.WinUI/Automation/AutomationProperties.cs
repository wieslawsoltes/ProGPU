using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml.Automation.Peers;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation;

/// <summary>Hosts UI Automation values as typed WinUI attached dependency properties.</summary>
[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class AutomationProperties
{
    private static readonly DependencyProperty s_acceleratorKeyProperty =
        Register(nameof(AcceleratorKeyProperty), typeof(string), string.Empty);
    private static readonly DependencyProperty s_accessKeyProperty =
        Register(nameof(AccessKeyProperty), typeof(string), string.Empty);
    private static readonly DependencyProperty s_accessibilityViewProperty =
        Register(nameof(AccessibilityViewProperty), typeof(AccessibilityView), AccessibilityView.Content);
    private static readonly DependencyProperty s_annotationsProperty =
        Register(nameof(AnnotationsProperty), typeof(IList<AutomationAnnotation>), null);
    private static readonly DependencyProperty s_automationControlTypeProperty =
        Register(nameof(AutomationControlTypeProperty), typeof(AutomationControlType), AutomationControlType.Button);
    private static readonly DependencyProperty s_automationIdProperty =
        Register(nameof(AutomationIdProperty), typeof(string), string.Empty);
    private static readonly DependencyProperty s_controlledPeersProperty =
        Register(nameof(ControlledPeersProperty), typeof(IList<UIElement>), null);
    private static readonly DependencyProperty s_cultureProperty =
        Register(nameof(CultureProperty), typeof(int), CultureInfo.CurrentUICulture.LCID);
    private static readonly DependencyProperty s_describedByProperty =
        Register(nameof(DescribedByProperty), typeof(IList<DependencyObject>), null);
    private static readonly DependencyProperty s_flowsFromProperty =
        Register(nameof(FlowsFromProperty), typeof(IList<DependencyObject>), null);
    private static readonly DependencyProperty s_flowsToProperty =
        Register(nameof(FlowsToProperty), typeof(IList<DependencyObject>), null);
    private static readonly DependencyProperty s_fullDescriptionProperty =
        Register(nameof(FullDescriptionProperty), typeof(string), string.Empty);
    private static readonly DependencyProperty s_headingLevelProperty =
        Register(nameof(HeadingLevelProperty), typeof(AutomationHeadingLevel), AutomationHeadingLevel.None);
    private static readonly DependencyProperty s_helpTextProperty =
        Register(nameof(HelpTextProperty), typeof(string), string.Empty);
    private static readonly DependencyProperty s_isDataValidForFormProperty =
        Register(nameof(IsDataValidForFormProperty), typeof(bool), false);
    private static readonly DependencyProperty s_isDialogProperty =
        Register(nameof(IsDialogProperty), typeof(bool), false);
    private static readonly DependencyProperty s_isPeripheralProperty =
        Register(nameof(IsPeripheralProperty), typeof(bool), false);
    private static readonly DependencyProperty s_isRequiredForFormProperty =
        Register(nameof(IsRequiredForFormProperty), typeof(bool), false);
    private static readonly DependencyProperty s_itemStatusProperty =
        Register(nameof(ItemStatusProperty), typeof(string), string.Empty);
    private static readonly DependencyProperty s_itemTypeProperty =
        Register(nameof(ItemTypeProperty), typeof(string), string.Empty);
    private static readonly DependencyProperty s_labeledByProperty =
        Register(nameof(LabeledByProperty), typeof(UIElement), null);
    private static readonly DependencyProperty s_landmarkTypeProperty =
        Register(nameof(LandmarkTypeProperty), typeof(AutomationLandmarkType), AutomationLandmarkType.None);
    private static readonly DependencyProperty s_levelProperty =
        Register(nameof(LevelProperty), typeof(int), -1);
    private static readonly DependencyProperty s_liveSettingProperty =
        Register(nameof(LiveSettingProperty), typeof(AutomationLiveSetting), AutomationLiveSetting.Off);
    private static readonly DependencyProperty s_localizedControlTypeProperty =
        Register(nameof(LocalizedControlTypeProperty), typeof(string), string.Empty);
    private static readonly DependencyProperty s_localizedLandmarkTypeProperty =
        Register(nameof(LocalizedLandmarkTypeProperty), typeof(string), string.Empty);
    private static readonly DependencyProperty s_nameProperty =
        Register(nameof(NameProperty), typeof(string), string.Empty);
    private static readonly DependencyProperty s_positionInSetProperty =
        Register(nameof(PositionInSetProperty), typeof(int), -1);
    private static readonly DependencyProperty s_sizeOfSetProperty =
        Register(nameof(SizeOfSetProperty), typeof(int), -1);

    private AutomationProperties()
    {
    }

    public static DependencyProperty AcceleratorKeyProperty => s_acceleratorKeyProperty;
    public static DependencyProperty AccessKeyProperty => s_accessKeyProperty;
    public static DependencyProperty AccessibilityViewProperty => s_accessibilityViewProperty;
    public static DependencyProperty AnnotationsProperty => s_annotationsProperty;
    public static DependencyProperty AutomationControlTypeProperty => s_automationControlTypeProperty;
    public static DependencyProperty AutomationIdProperty => s_automationIdProperty;
    public static DependencyProperty ControlledPeersProperty => s_controlledPeersProperty;
    public static DependencyProperty CultureProperty => s_cultureProperty;
    public static DependencyProperty DescribedByProperty => s_describedByProperty;
    public static DependencyProperty FlowsFromProperty => s_flowsFromProperty;
    public static DependencyProperty FlowsToProperty => s_flowsToProperty;
    public static DependencyProperty FullDescriptionProperty => s_fullDescriptionProperty;
    public static DependencyProperty HeadingLevelProperty => s_headingLevelProperty;
    public static DependencyProperty HelpTextProperty => s_helpTextProperty;
    public static DependencyProperty IsDataValidForFormProperty => s_isDataValidForFormProperty;
    public static DependencyProperty IsDialogProperty => s_isDialogProperty;
    public static DependencyProperty IsPeripheralProperty => s_isPeripheralProperty;
    public static DependencyProperty IsRequiredForFormProperty => s_isRequiredForFormProperty;
    public static DependencyProperty ItemStatusProperty => s_itemStatusProperty;
    public static DependencyProperty ItemTypeProperty => s_itemTypeProperty;
    public static DependencyProperty LabeledByProperty => s_labeledByProperty;
    public static DependencyProperty LandmarkTypeProperty => s_landmarkTypeProperty;
    public static DependencyProperty LevelProperty => s_levelProperty;
    public static DependencyProperty LiveSettingProperty => s_liveSettingProperty;
    public static DependencyProperty LocalizedControlTypeProperty => s_localizedControlTypeProperty;
    public static DependencyProperty LocalizedLandmarkTypeProperty => s_localizedLandmarkTypeProperty;
    public static DependencyProperty NameProperty => s_nameProperty;
    public static DependencyProperty PositionInSetProperty => s_positionInSetProperty;
    public static DependencyProperty SizeOfSetProperty => s_sizeOfSetProperty;

    public static string GetAcceleratorKey(DependencyObject element) =>
        GetString(element, s_acceleratorKeyProperty);

    public static void SetAcceleratorKey(DependencyObject element, string value) =>
        SetString(element, s_acceleratorKeyProperty, value);

    public static string GetAccessKey(DependencyObject element) =>
        GetString(element, s_accessKeyProperty);

    public static void SetAccessKey(DependencyObject element, string value) =>
        SetString(element, s_accessKeyProperty, value);

    public static AccessibilityView GetAccessibilityView(DependencyObject element) =>
        GetValue(element, s_accessibilityViewProperty, AccessibilityView.Content);

    public static void SetAccessibilityView(DependencyObject element, AccessibilityView value) =>
        SetValue(element, s_accessibilityViewProperty, value);

    public static IList<AutomationAnnotation> GetAnnotations(DependencyObject element) =>
        GetOrCreateList<AutomationAnnotation>(element, s_annotationsProperty);

    public static AutomationControlType GetAutomationControlType(UIElement element) =>
        GetValue(element, s_automationControlTypeProperty, AutomationControlType.Button);

    public static void SetAutomationControlType(UIElement element, AutomationControlType value) =>
        SetValue(element, s_automationControlTypeProperty, value);

    public static string GetAutomationId(DependencyObject element) =>
        GetString(element, s_automationIdProperty);

    public static void SetAutomationId(DependencyObject element, string value) =>
        SetString(element, s_automationIdProperty, value);

    public static IList<UIElement> GetControlledPeers(DependencyObject element) =>
        GetOrCreateList<UIElement>(element, s_controlledPeersProperty);

    public static int GetCulture(DependencyObject element) =>
        GetValue(element, s_cultureProperty, CultureInfo.CurrentUICulture.LCID);

    public static void SetCulture(DependencyObject element, int value) =>
        SetValue(element, s_cultureProperty, value);

    public static IList<DependencyObject> GetDescribedBy(DependencyObject element) =>
        GetOrCreateList<DependencyObject>(element, s_describedByProperty);

    public static IList<DependencyObject> GetFlowsFrom(DependencyObject element) =>
        GetOrCreateList<DependencyObject>(element, s_flowsFromProperty);

    public static IList<DependencyObject> GetFlowsTo(DependencyObject element) =>
        GetOrCreateList<DependencyObject>(element, s_flowsToProperty);

    public static string GetFullDescription(DependencyObject element) =>
        GetString(element, s_fullDescriptionProperty);

    public static void SetFullDescription(DependencyObject element, string value) =>
        SetString(element, s_fullDescriptionProperty, value);

    public static AutomationHeadingLevel GetHeadingLevel(DependencyObject element) =>
        GetValue(element, s_headingLevelProperty, AutomationHeadingLevel.None);

    public static void SetHeadingLevel(DependencyObject element, AutomationHeadingLevel value) =>
        SetValue(element, s_headingLevelProperty, value);

    public static string GetHelpText(DependencyObject element) =>
        GetString(element, s_helpTextProperty);

    public static void SetHelpText(DependencyObject element, string value) =>
        SetString(element, s_helpTextProperty, value);

    public static bool GetIsDataValidForForm(DependencyObject element) =>
        GetValue(element, s_isDataValidForFormProperty, false);

    public static void SetIsDataValidForForm(DependencyObject element, bool value) =>
        SetValue(element, s_isDataValidForFormProperty, value);

    public static bool GetIsDialog(DependencyObject element) =>
        GetValue(element, s_isDialogProperty, false);

    public static void SetIsDialog(DependencyObject element, bool value) =>
        SetValue(element, s_isDialogProperty, value);

    public static bool GetIsPeripheral(DependencyObject element) =>
        GetValue(element, s_isPeripheralProperty, false);

    public static void SetIsPeripheral(DependencyObject element, bool value) =>
        SetValue(element, s_isPeripheralProperty, value);

    public static bool GetIsRequiredForForm(DependencyObject element) =>
        GetValue(element, s_isRequiredForFormProperty, false);

    public static void SetIsRequiredForForm(DependencyObject element, bool value) =>
        SetValue(element, s_isRequiredForFormProperty, value);

    public static string GetItemStatus(DependencyObject element) =>
        GetString(element, s_itemStatusProperty);

    public static void SetItemStatus(DependencyObject element, string value) =>
        SetString(element, s_itemStatusProperty, value);

    public static string GetItemType(DependencyObject element) =>
        GetString(element, s_itemTypeProperty);

    public static void SetItemType(DependencyObject element, string value) =>
        SetString(element, s_itemTypeProperty, value);

    public static UIElement GetLabeledBy(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (UIElement)element.GetValue(s_labeledByProperty)!;
    }

    public static void SetLabeledBy(DependencyObject element, UIElement value) =>
        SetValue(element, s_labeledByProperty, value);

    public static AutomationLandmarkType GetLandmarkType(DependencyObject element) =>
        GetValue(element, s_landmarkTypeProperty, AutomationLandmarkType.None);

    public static void SetLandmarkType(DependencyObject element, AutomationLandmarkType value) =>
        SetValue(element, s_landmarkTypeProperty, value);

    public static int GetLevel(DependencyObject element) =>
        GetValue(element, s_levelProperty, -1);

    public static void SetLevel(DependencyObject element, int value) =>
        SetValue(element, s_levelProperty, value);

    public static AutomationLiveSetting GetLiveSetting(DependencyObject element) =>
        GetValue(element, s_liveSettingProperty, AutomationLiveSetting.Off);

    public static void SetLiveSetting(DependencyObject element, AutomationLiveSetting value) =>
        SetValue(element, s_liveSettingProperty, value);

    public static string GetLocalizedControlType(DependencyObject element) =>
        GetString(element, s_localizedControlTypeProperty);

    public static void SetLocalizedControlType(DependencyObject element, string value) =>
        SetString(element, s_localizedControlTypeProperty, value);

    public static string GetLocalizedLandmarkType(DependencyObject element) =>
        GetString(element, s_localizedLandmarkTypeProperty);

    public static void SetLocalizedLandmarkType(DependencyObject element, string value) =>
        SetString(element, s_localizedLandmarkTypeProperty, value);

    public static string GetName(DependencyObject element) =>
        GetString(element, s_nameProperty);

    public static void SetName(DependencyObject element, string value) =>
        SetString(element, s_nameProperty, value);

    public static int GetPositionInSet(DependencyObject element) =>
        GetValue(element, s_positionInSetProperty, -1);

    public static void SetPositionInSet(DependencyObject element, int value) =>
        SetValue(element, s_positionInSetProperty, value);

    public static int GetSizeOfSet(DependencyObject element) =>
        GetValue(element, s_sizeOfSetProperty, -1);

    public static void SetSizeOfSet(DependencyObject element, int value) =>
        SetValue(element, s_sizeOfSetProperty, value);

    private static DependencyProperty Register(string propertyName, Type propertyType, object? defaultValue) =>
        DependencyProperty.RegisterAttached(
            propertyName[..^"Property".Length],
            propertyType,
            typeof(AutomationProperties),
            new PropertyMetadata(defaultValue));

    private static T GetValue<T>(DependencyObject element, DependencyProperty property, T defaultValue)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(property) is T value ? value : defaultValue;
    }

    private static void SetValue<T>(DependencyObject element, DependencyProperty property, T value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(property, value);
    }

    private static string GetString(DependencyObject element, DependencyProperty property)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(property) as string ?? string.Empty;
    }

    private static void SetString(DependencyObject element, DependencyProperty property, string value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(property, value ?? string.Empty);
    }

    private static IList<T> GetOrCreateList<T>(DependencyObject element, DependencyProperty property)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.GetValue(property) is IList<T> existing)
            return existing;

        IList<T> created = new List<T>();
        element.SetValue(property, created);
        return created;
    }
}
