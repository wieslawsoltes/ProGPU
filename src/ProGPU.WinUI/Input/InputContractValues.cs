using Windows.Foundation.Metadata;

namespace Microsoft.UI.Input;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010005)]
public enum FocusNavigationReason
{
    Programmatic = 0,
    Restore = 1,
    First = 2,
    Last = 3,
    Left = 4,
    Up = 5,
    Right = 6,
    Down = 7
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010005)]
public enum FocusNavigationResult
{
    NotMoved = 0,
    Moved = 1,
    NoFocusableElements = 2
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010001)]
public enum InputActivationState
{
    None = 0,
    Deactivated = 1,
    Activated = 2
}

[Flags]
[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum InputPointerSourceDeviceKinds : uint
{
    None = 0,
    Touch = 1,
    Pen = 2,
    Mouse = 4
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum InputSystemCursorShape
{
    Arrow = 0,
    Cross = 1,
    Hand = 3,
    Help = 4,
    IBeam = 5,
    SizeAll = 6,
    SizeNortheastSouthwest = 7,
    SizeNorthSouth = 8,
    SizeNorthwestSoutheast = 9,
    SizeWestEast = 10,
    UniversalNo = 11,
    UpArrow = 12,
    Wait = 13,
    Pin = 14,
    Person = 15,
    AppStarting = 16
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010006)]
public enum MoveSizeOperation
{
    Move = 0,
    SizeBottom = 1,
    SizeBottomLeft = 2,
    SizeBottomRight = 3,
    SizeLeft = 4,
    SizeRight = 5,
    SizeTop = 6,
    SizeTopLeft = 7,
    SizeTopRight = 8
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public enum NonClientRegionKind
{
    Close = 0,
    Maximize = 1,
    Minimize = 2,
    Icon = 3,
    Caption = 4,
    TopBorder = 5,
    LeftBorder = 6,
    BottomBorder = 7,
    RightBorder = 8,
    Passthrough = 9
}

[Flags]
[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public enum VirtualKeyStates : uint
{
    None = 0,
    Down = 1,
    Locked = 2
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public struct PhysicalKeyStatus :
    IEquatable<PhysicalKeyStatus>
{
    public uint RepeatCount;
    public uint ScanCode;
    public bool IsExtendedKey;
    public bool IsMenuKeyDown;
    public bool WasKeyDown;
    public bool IsKeyReleased;

    public PhysicalKeyStatus(
        uint _RepeatCount,
        uint _ScanCode,
        bool _IsExtendedKey,
        bool _IsMenuKeyDown,
        bool _WasKeyDown,
        bool _IsKeyReleased)
    {
        RepeatCount = _RepeatCount;
        ScanCode = _ScanCode;
        IsExtendedKey = _IsExtendedKey;
        IsMenuKeyDown = _IsMenuKeyDown;
        WasKeyDown = _WasKeyDown;
        IsKeyReleased = _IsKeyReleased;
    }

    public readonly bool Equals(
        PhysicalKeyStatus other) =>
        RepeatCount == other.RepeatCount &&
        ScanCode == other.ScanCode &&
        IsExtendedKey == other.IsExtendedKey &&
        IsMenuKeyDown == other.IsMenuKeyDown &&
        WasKeyDown == other.WasKeyDown &&
        IsKeyReleased == other.IsKeyReleased;

    public override readonly bool Equals(
        object? obj) =>
        obj is PhysicalKeyStatus other &&
        Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(
            RepeatCount,
            ScanCode,
            IsExtendedKey,
            IsMenuKeyDown,
            WasKeyDown,
            IsKeyReleased);

    public static bool operator ==(
        PhysicalKeyStatus x,
        PhysicalKeyStatus y) =>
        x.Equals(y);

    public static bool operator !=(
        PhysicalKeyStatus x,
        PhysicalKeyStatus y) =>
        !x.Equals(y);
}
