using Microsoft.UI.Xaml.Automation.Text;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Provider;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ITextRangeProvider
{
    string GetText(int maxLength);
    ITextRangeProvider Clone();
    bool Compare(ITextRangeProvider textRangeProvider);
    int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider textRangeProvider, TextPatternRangeEndpoint targetEndpoint);
    void ExpandToEnclosingUnit(TextUnit unit);
    ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward);
    ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase);
    object GetAttributeValue(int attributeId);
    void GetBoundingRectangles(out double[] returnValue);
    IRawElementProviderSimple GetEnclosingElement();
    int Move(TextUnit unit, int count);
    int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count);
    void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider textRangeProvider, TextPatternRangeEndpoint targetEndpoint);
    void Select();
    void AddToSelection();
    void RemoveFromSelection();
    void ScrollIntoView(bool alignToTop);
    IRawElementProviderSimple[] GetChildren();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ITextRangeProvider2 :
    ITextRangeProvider
{
    void ShowContextMenu();
}
