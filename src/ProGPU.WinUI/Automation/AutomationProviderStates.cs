using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AnnotationType
{
    Unknown = 60000,
    SpellingError = 60001,
    GrammarError = 60002,
    Comment = 60003,
    FormulaError = 60004,
    TrackChanges = 60005,
    Header = 60006,
    Footer = 60007,
    Highlighted = 60008,
    Endnote = 60009,
    Footnote = 60010,
    InsertionChange = 60011,
    DeletionChange = 60012,
    MoveChange = 60013,
    FormatChange = 60014,
    UnsyncedChange = 60015,
    EditingLockedChange = 60016,
    ExternalChange = 60017,
    ConflictingChange = 60018,
    Author = 60019,
    AdvancedProofingIssue = 60020,
    DataValidationError = 60021,
    CircularReferenceError = 60022
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum DockPosition
{
    Top = 0,
    Left = 1,
    Bottom = 2,
    Right = 3,
    Fill = 4,
    None = 5
}

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
public enum ScrollAmount
{
    LargeDecrement = 0,
    SmallDecrement = 1,
    NoAmount = 2,
    LargeIncrement = 3,
    SmallIncrement = 4
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum SynchronizedInputType
{
    KeyUp = 1,
    KeyDown = 2,
    LeftMouseUp = 4,
    LeftMouseDown = 8,
    RightMouseUp = 16,
    RightMouseDown = 32
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

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum WindowInteractionState
{
    Running = 0,
    Closing = 1,
    ReadyForUserInteraction = 2,
    BlockedByModalWindow = 3,
    NotResponding = 4
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum WindowVisualState
{
    Normal = 0,
    Maximized = 1,
    Minimized = 2
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum ZoomUnit
{
    NoAmount = 0,
    LargeDecrement = 1,
    SmallDecrement = 2,
    LargeIncrement = 3,
    SmallIncrement = 4
}
