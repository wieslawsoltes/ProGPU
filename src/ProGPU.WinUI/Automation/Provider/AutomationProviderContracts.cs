using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Provider;

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
public interface ITableItemProvider
{
    IRawElementProviderSimple[] GetColumnHeaderItems();

    IRawElementProviderSimple[] GetRowHeaderItems();
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface ITextChildProvider
{
    IRawElementProviderSimple TextContainer { get; }

    ITextRangeProvider TextRange { get; }
}
