using System.Numerics;
using ProGPU.Scene;

namespace Windows.Devices.Input
{
    public enum PointerDeviceType
    {
        Touch = 0,
        Pen = 1,
        Mouse = 2
    }

    public sealed class PointerDevice
    {
        internal PointerDevice(PointerDeviceType pointerDeviceType)
        {
            PointerDeviceType = pointerDeviceType;
        }

        public PointerDeviceType PointerDeviceType { get; }

        public static PointerDevice GetPointerDevice(PointerDeviceType pointerDeviceType) => new(pointerDeviceType);
    }
}

namespace Microsoft.UI.Input
{
using Windows.Devices.Input;

public sealed class PointerPointProperties
{
    public bool IsLeftButtonPressed { get; internal set; }
    public bool IsMiddleButtonPressed { get; internal set; }
    public bool IsRightButtonPressed { get; internal set; }
    public bool IsPrimary { get; internal set; }
    public bool IsCanceled { get; internal set; }
    public bool IsEraser { get; internal set; }
    public float Pressure { get; internal set; }
    public Rect ContactRect { get; internal set; }
    public int MouseWheelDelta { get; internal set; }
}

public sealed class PointerPoint
{
    internal PointerPoint(
        uint pointerId,
        ulong timestamp,
        Vector2 position,
        Vector2 rawPosition,
        PointerDeviceType deviceType,
        bool isInContact,
        PointerPointProperties properties)
    {
        PointerId = pointerId;
        Timestamp = timestamp;
        Position = position;
        RawPosition = rawPosition;
        PointerDevice = PointerDevice.GetPointerDevice(deviceType);
        IsInContact = isInContact;
        Properties = properties;
    }

    public uint PointerId { get; }
    public ulong Timestamp { get; }
    public Vector2 Position { get; }
    public Vector2 RawPosition { get; }
    public PointerDevice PointerDevice { get; }
    public bool IsInContact { get; }
    public PointerPointProperties Properties { get; }
}
}

namespace Microsoft.UI.Xaml.Input
{
using Windows.Devices.Input;

public sealed class Pointer
{
    internal Pointer(uint pointerId, PointerDeviceType pointerDeviceType, bool isInContact, bool isInRange = true)
    {
        PointerId = pointerId;
        PointerDeviceType = pointerDeviceType;
        IsInContact = isInContact;
        IsInRange = isInRange;
    }

    public uint PointerId { get; }
    public PointerDeviceType PointerDeviceType { get; }
    public bool IsInContact { get; internal set; }
    public bool IsInRange { get; internal set; }
}

[Flags]
public enum ManipulationModes : uint
{
    None = 0,
    TranslateX = 1,
    TranslateY = 2,
    TranslateRailsX = 4,
    TranslateRailsY = 8,
    Rotate = 16,
    Scale = 32,
    TranslateInertia = 64,
    RotateInertia = 128,
    ScaleInertia = 256,
    All = 65535,
    System = 65536
}

public enum HoldingState
{
    Started = 0,
    Completed = 1,
    Canceled = 2
}

public readonly record struct ManipulationDelta(
    Vector2 Translation,
    float Scale,
    float Rotation,
    float Expansion)
{
    public static ManipulationDelta Identity => new(Vector2.Zero, 1f, 0f, 0f);
}

public readonly record struct ManipulationVelocities(
    Vector2 Linear,
    float Angular,
    float Expansion);

public abstract class GestureRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    internal Vector2 ScreenPosition { get; init; }
    public PointerDeviceType PointerDeviceType { get; internal init; }

    public Vector2 GetPosition(Microsoft.UI.Xaml.FrameworkElement? relativeTo) =>
        InputSystem.GetLocalPosition(relativeTo, ScreenPosition);
}

public sealed class TappedRoutedEventArgs : GestureRoutedEventArgs
{
    public uint PointerId { get; internal init; }
}

public sealed class DoubleTappedRoutedEventArgs : GestureRoutedEventArgs
{
    public uint PointerId { get; internal init; }
}

public sealed class RightTappedRoutedEventArgs : GestureRoutedEventArgs
{
    public uint PointerId { get; internal init; }
}

public sealed class HoldingRoutedEventArgs : GestureRoutedEventArgs
{
    public HoldingState HoldingState { get; internal init; }
}

public sealed class ManipulationStartingRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public ManipulationModes Mode { get; set; }
    public Microsoft.UI.Xaml.FrameworkElement? Container { get; set; }
    public Vector2 PivotCenter { get; set; }
    public float PivotRadius { get; set; }
}

public sealed class ManipulationStartedRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public Vector2 Position { get; internal init; }
    public ManipulationDelta Cumulative { get; internal init; } = ManipulationDelta.Identity;
}

public sealed class ManipulationDeltaRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public ManipulationDelta Delta { get; internal init; } = ManipulationDelta.Identity;
    public ManipulationDelta Cumulative { get; internal init; } = ManipulationDelta.Identity;
    public ManipulationVelocities Velocities { get; internal init; }
    public bool IsInertial { get; internal init; }
    public bool Complete { get; set; }
}

public sealed class ManipulationInertiaStartingRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public ManipulationDelta Cumulative { get; internal init; } = ManipulationDelta.Identity;
    public ManipulationVelocities Velocities { get; internal init; }
    public float TranslationDeceleration { get; set; } = 0.001f;
    public float RotationDeceleration { get; set; } = 0.0001f;
    public float ExpansionDeceleration { get; set; } = 0.001f;
}

public sealed class ManipulationCompletedRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public ManipulationDelta Cumulative { get; internal init; } = ManipulationDelta.Identity;
    public ManipulationVelocities Velocities { get; internal init; }
    public bool IsInertial { get; internal init; }
}

public enum InputScopeNameValue
{
    Default = 0,
    Url,
    EmailSmtpAddress,
    Number,
    TelephoneNumber,
    Search,
    Chat,
    NameOrPhoneNumber,
    Password,
    NumericPin
}

public sealed class InputScopeName
{
    public InputScopeNameValue NameValue { get; set; }
}

public sealed class InputScope
{
    public IList<InputScopeName> Names { get; } = new List<InputScopeName>();
}

public enum TextInputEventKind
{
    InsertText,
    DeleteContentBackward,
    DeleteContentForward,
    InsertLineBreak,
    CompositionStarted,
    CompositionUpdated,
    CompositionCompleted,
    CompositionCanceled,
    Paste
}

public sealed class TextInputRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public TextInputEventKind Kind { get; internal init; }
    public string Text { get; internal init; } = string.Empty;
    public bool IsComposing { get; internal init; }
}

public readonly record struct TextInputOptions(
    InputScopeNameValue InputScope,
    string EnterKeyHint,
    string AutoCapitalize,
    bool IsSpellCheckEnabled,
    bool IsPassword,
    bool AcceptsReturn,
    string Text,
    int SelectionStart,
    int SelectionLength,
    Rect Bounds);

public interface ITextInputClient
{
    TextInputOptions GetTextInputOptions();
    void OnTextInput(TextInputRoutedEventArgs args);
}

public enum PointerInputKind
{
    Moved,
    Pressed,
    Released,
    Canceled,
    Wheel
}

public readonly record struct PointerInputEvent(
    PointerInputKind Kind,
    uint PointerId,
    PointerDeviceType DeviceType,
    Vector2 Position,
    ulong Timestamp,
    bool IsPrimary = true,
    bool IsInContact = false,
    bool IsLeftButtonPressed = false,
    bool IsMiddleButtonPressed = false,
    bool IsRightButtonPressed = false,
    float Pressure = 0f,
    Rect ContactRect = default,
    float WheelDeltaX = 0f,
    float WheelDeltaY = 0f,
    VirtualKeyModifiers Modifiers = VirtualKeyModifiers.None);
}
