using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Peers;

/// <summary>Value and virtualized text-pattern bridge for the retained rich editor.</summary>
[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public class RichEditBoxAutomationPeer : FrameworkElementAutomationPeer, IValueProvider, ITextProvider
{
    private RichEditBox RichOwner => (RichEditBox)Owner;

    protected internal RichEditBoxAutomationPeer(
        WinRT.IObjectReference objRef)
        : base(objRef)
    {
    }

    protected RichEditBoxAutomationPeer(
        WinRT.DerivedComposed _)
        : base(_)
    {
    }

    public RichEditBoxAutomationPeer(RichEditBox owner)
        : base(owner)
    {
    }

    public bool IsReadOnly => RichOwner.IsReadOnly;
    public string Value => RichOwner.Text;
    public ITextRangeProvider DocumentRange =>
        new RichEditTextRangeProvider(RichOwner, 0, RichOwner.Text.Length);
    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    internal object GetPatternValue(
        PatternInterface patternInterface) =>
        patternInterface switch
        {
            PatternInterface.Value or PatternInterface.Text or PatternInterface.Text2 => this,
            _ => null!
        };

    internal AutomationControlType
        GetAutomationControlTypeValue() =>
        AutomationControlType.Document;

    internal string GetNameValue() =>
        RichOwner.Header?.ToString() ??
        RichOwner.Description?.ToString() ??
        string.Empty;

    public void SetValue(string value)
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("A read-only RichEditBox cannot be changed through automation.");
        }

        RichOwner.TextDocument.SetText(Microsoft.UI.Text.TextSetOptions.None, value ?? string.Empty);
    }

    public ITextRangeProvider[] GetSelection() =>
        [new RichEditTextRangeProvider(
            RichOwner,
            RichOwner.SelectionStart,
            RichOwner.SelectionStart + RichOwner.SelectionLength)];

    public ITextRangeProvider[] GetVisibleRanges()
    {
        (int start, int end) = RichOwner.GetVisibleDocumentRange();
        return [new RichEditTextRangeProvider(RichOwner, start, end)];
    }

    public ITextRangeProvider RangeFromPoint(Windows.Foundation.Point screenLocation)
    {
        int position = RichOwner.GetDocumentPositionFromPoint(screenLocation, clientCoordinates: false);
        return new RichEditTextRangeProvider(RichOwner, position, position);
    }

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement)
    {
        ArgumentNullException.ThrowIfNull(childElement);
        if (childElement.Peer is FrameworkElementAutomationPeer { Owner: FrameworkElement child } &&
            RichOwner.TryGetDocumentRangeForChild(child, out int start, out int end))
        {
            return new RichEditTextRangeProvider(RichOwner, start, end);
        }

        throw new ArgumentException(
            "The automation element is not an embedded child of this document.",
            nameof(childElement));
    }
}
