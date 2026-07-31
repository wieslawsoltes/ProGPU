using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum ExpandCollapseState
{
    Collapsed = 0,
    Expanded = 1,
    PartiallyExpanded = 2,
    LeafNode = 3
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum RowOrColumnMajor
{
    RowMajor = 0,
    ColumnMajor = 1,
    Indeterminate = 2
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum ToggleState
{
    Off = 0,
    On = 1,
    Indeterminate = 2
}
