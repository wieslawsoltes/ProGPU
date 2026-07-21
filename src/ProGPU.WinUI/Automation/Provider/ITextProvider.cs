namespace Microsoft.UI.Xaml.Automation.Provider;

public interface ITextProvider
{
    ITextRangeProvider DocumentRange { get; }
    SupportedTextSelection SupportedTextSelection { get; }
    ITextRangeProvider[] GetSelection();
    ITextRangeProvider[] GetVisibleRanges();
    ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement);
    ITextRangeProvider RangeFromPoint(Windows.Foundation.Point screenLocation);
}
