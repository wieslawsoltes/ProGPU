using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Peers;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AutomationEvents
{
    ToolTipOpened = 0,
    ToolTipClosed = 1,
    MenuOpened = 2,
    MenuClosed = 3,
    AutomationFocusChanged = 4,
    InvokePatternOnInvoked = 5,
    SelectionItemPatternOnElementAddedToSelection = 6,
    SelectionItemPatternOnElementRemovedFromSelection = 7,
    SelectionItemPatternOnElementSelected = 8,
    SelectionPatternOnInvalidated = 9,
    TextPatternOnTextSelectionChanged = 10,
    TextPatternOnTextChanged = 11,
    AsyncContentLoaded = 12,
    PropertyChanged = 13,
    StructureChanged = 14,
    DragStart = 15,
    DragCancel = 16,
    DragComplete = 17,
    DragEnter = 18,
    DragLeave = 19,
    Dropped = 20,
    LiveRegionChanged = 21,
    InputReachedTarget = 22,
    InputReachedOtherElement = 23,
    InputDiscarded = 24,
    WindowClosed = 25,
    WindowOpened = 26,
    ConversionTargetChanged = 27,
    TextEditTextChanged = 28,
    LayoutInvalidated = 29
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AutomationHeadingLevel
{
    None = 0,
    Level1 = 1,
    Level2 = 2,
    Level3 = 3,
    Level4 = 4,
    Level5 = 5,
    Level6 = 6,
    Level7 = 7,
    Level8 = 8,
    Level9 = 9
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AutomationLandmarkType
{
    None = 0,
    Custom = 1,
    Form = 2,
    Main = 3,
    Navigation = 4,
    Search = 5
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AutomationLiveSetting
{
    Off = 0,
    Polite = 1,
    Assertive = 2
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AutomationNotificationKind
{
    ItemAdded = 0,
    ItemRemoved = 1,
    ActionCompleted = 2,
    ActionAborted = 3,
    Other = 4
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AutomationNotificationProcessing
{
    ImportantAll = 0,
    ImportantMostRecent = 1,
    All = 2,
    MostRecent = 3,
    CurrentThenMostRecent = 4
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AutomationOrientation
{
    None = 0,
    Horizontal = 1,
    Vertical = 2
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AutomationStructureChangeType
{
    ChildAdded = 0,
    ChildRemoved = 1,
    ChildrenInvalidated = 2,
    ChildrenBulkAdded = 3,
    ChildrenBulkRemoved = 4,
    ChildrenReordered = 5
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public struct RawElementProviderRuntimeId :
    IEquatable<RawElementProviderRuntimeId>
{
    public RawElementProviderRuntimeId(
        uint _Part1,
        uint _Part2)
    {
        Part1 = _Part1;
        Part2 = _Part2;
    }

    public uint Part1;
    public uint Part2;

    public readonly bool Equals(
        RawElementProviderRuntimeId other) =>
        Part1 == other.Part1 &&
        Part2 == other.Part2;

    public override readonly bool Equals(object? obj) =>
        obj is RawElementProviderRuntimeId other &&
        Equals(other);

    public override readonly int GetHashCode() =>
        unchecked(((int)Part1 * 397) ^ (int)Part2);

    public static bool operator ==(
        RawElementProviderRuntimeId x,
        RawElementProviderRuntimeId y) =>
        x.Equals(y);

    public static bool operator !=(
        RawElementProviderRuntimeId x,
        RawElementProviderRuntimeId y) =>
        !x.Equals(y);
}
