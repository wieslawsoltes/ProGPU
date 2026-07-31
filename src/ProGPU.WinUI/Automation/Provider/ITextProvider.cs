using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Provider;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ITextProvider
{
    ITextRangeProvider DocumentRange { get; }
    SupportedTextSelection SupportedTextSelection { get; }
    ITextRangeProvider[] GetSelection();
    ITextRangeProvider[] GetVisibleRanges();
    ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement);
    ITextRangeProvider RangeFromPoint(Windows.Foundation.Point screenLocation);
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ITextEditProvider : ITextProvider
{
    ITextRangeProvider GetActiveComposition();

    ITextRangeProvider GetConversionTarget();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ITextProvider2 : ITextProvider
{
    ITextRangeProvider GetCaretRange(
        out bool isActive);

    ITextRangeProvider RangeFromAnnotation(
        IRawElementProviderSimple annotationElement);
}
