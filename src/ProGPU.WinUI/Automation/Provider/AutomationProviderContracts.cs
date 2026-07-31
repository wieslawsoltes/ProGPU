using Microsoft.UI.Xaml.Automation;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Provider;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IExpandCollapseProvider
{
    ExpandCollapseState ExpandCollapseState { get; }

    void Collapse();

    void Expand();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IGridItemProvider
{
    int Column { get; }

    int ColumnSpan { get; }

    IRawElementProviderSimple ContainingGrid { get; }

    int Row { get; }

    int RowSpan { get; }
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IGridProvider
{
    int ColumnCount { get; }

    int RowCount { get; }

    IRawElementProviderSimple GetItem(
        int row,
        int column);
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IDropTargetProvider
{
    string DropEffect { get; }

    string[] DropEffects { get; }
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IInvokeProvider
{
    void Invoke();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IObjectModelProvider
{
    object GetUnderlyingObjectModel();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IRangeValueProvider
{
    bool IsReadOnly { get; }

    double LargeChange { get; }

    double Maximum { get; }

    double Minimum { get; }

    double SmallChange { get; }

    double Value { get; }

    void SetValue(double value);
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IScrollItemProvider
{
    void ScrollIntoView();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IScrollProvider
{
    bool HorizontallyScrollable { get; }

    double HorizontalScrollPercent { get; }

    double HorizontalViewSize { get; }

    bool VerticallyScrollable { get; }

    double VerticalScrollPercent { get; }

    double VerticalViewSize { get; }

    void Scroll(
        ScrollAmount horizontalAmount,
        ScrollAmount verticalAmount);

    void SetScrollPercent(
        double horizontalPercent,
        double verticalPercent);
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ISelectionItemProvider
{
    bool IsSelected { get; }

    IRawElementProviderSimple SelectionContainer { get; }

    void AddToSelection();

    void RemoveFromSelection();

    void Select();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ISelectionProvider
{
    bool CanSelectMultiple { get; }

    bool IsSelectionRequired { get; }

    IRawElementProviderSimple[] GetSelection();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ITableItemProvider
{
    IRawElementProviderSimple[] GetColumnHeaderItems();

    IRawElementProviderSimple[] GetRowHeaderItems();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ITableProvider
{
    RowOrColumnMajor RowOrColumnMajor { get; }

    IRawElementProviderSimple[] GetColumnHeaders();

    IRawElementProviderSimple[] GetRowHeaders();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ITextChildProvider
{
    IRawElementProviderSimple TextContainer { get; }

    ITextRangeProvider TextRange { get; }
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IToggleProvider
{
    ToggleState ToggleState { get; }

    void Toggle();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ITransformProvider
{
    bool CanMove { get; }

    bool CanResize { get; }

    bool CanRotate { get; }

    void Move(double x, double y);

    void Resize(double width, double height);

    void Rotate(double degrees);
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ITransformProvider2 :
    ITransformProvider
{
    bool CanZoom { get; }

    double MaxZoom { get; }

    double MinZoom { get; }

    double ZoomLevel { get; }

    void Zoom(double zoom);

    void ZoomByUnit(ZoomUnit zoomUnit);
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IVirtualizedItemProvider
{
    void Realize();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IWindowProvider
{
    WindowInteractionState InteractionState { get; }

    WindowVisualState VisualState { get; }

    bool IsModal { get; }

    bool IsTopmost { get; }

    bool Maximizable { get; }

    bool Minimizable { get; }

    void Close();

    void SetVisualState(WindowVisualState state);

    bool WaitForInputIdle(int milliseconds);
}
