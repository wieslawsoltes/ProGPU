using System.Numerics;
using ProGPU.Scene;
using Windows.Foundation.Metadata;

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
using Microsoft.UI.Xaml.Input;
using Windows.Devices.Input;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class PointerPointProperties
{
    internal PointerPointProperties(
        Windows.Foundation.Rect contactRect = default,
        bool isBarrelButtonPressed = false,
        bool isHorizontalMouseWheel = false,
        bool isInRange = true,
        bool isInverted = false,
        bool isLeftButtonPressed = false,
        bool isMiddleButtonPressed = false,
        bool isRightButtonPressed = false,
        bool isXButton1Pressed = false,
        bool isXButton2Pressed = false,
        bool isPrimary = false,
        bool isCanceled = false,
        bool isEraser = false,
        float orientation = 0f,
        PointerUpdateKind pointerUpdateKind =
            PointerUpdateKind.Other,
        float pressure = 0f,
        bool touchConfidence = true,
        float twist = 0f,
        float xTilt = 0f,
        float yTilt = 0f,
        int mouseWheelDelta = 0)
    {
        ContactRect = contactRect;
        IsBarrelButtonPressed =
            isBarrelButtonPressed;
        IsHorizontalMouseWheel =
            isHorizontalMouseWheel;
        IsInRange = isInRange;
        IsInverted = isInverted;
        IsLeftButtonPressed =
            isLeftButtonPressed;
        IsMiddleButtonPressed =
            isMiddleButtonPressed;
        IsRightButtonPressed =
            isRightButtonPressed;
        IsXButton1Pressed =
            isXButton1Pressed;
        IsXButton2Pressed =
            isXButton2Pressed;
        IsPrimary = isPrimary;
        IsCanceled = isCanceled;
        IsEraser = isEraser;
        Orientation = orientation;
        PointerUpdateKind = pointerUpdateKind;
        Pressure = pressure;
        TouchConfidence = touchConfidence;
        Twist = twist;
        XTilt = xTilt;
        YTilt = yTilt;
        MouseWheelDelta = mouseWheelDelta;
    }

    public Windows.Foundation.Rect ContactRect { get; }
    public bool IsBarrelButtonPressed { get; }
    public bool IsHorizontalMouseWheel { get; }
    public bool IsInRange { get; }
    public bool IsInverted { get; }
    public bool IsLeftButtonPressed { get; }
    public bool IsMiddleButtonPressed { get; }
    public bool IsRightButtonPressed { get; }
    public bool IsXButton1Pressed { get; }
    public bool IsXButton2Pressed { get; }
    public bool IsPrimary { get; }
    public bool IsCanceled { get; }
    public bool IsEraser { get; }
    public float Orientation { get; }
    public PointerUpdateKind PointerUpdateKind { get; }
    public float Pressure { get; }
    public bool TouchConfidence { get; }
    public float Twist { get; }
    public float XTilt { get; }
    public float YTilt { get; }
    public int MouseWheelDelta { get; }

    internal PointerPointProperties
        WithContactRect(
            Windows.Foundation.Rect contactRect) =>
        new(
            contactRect,
            IsBarrelButtonPressed,
            IsHorizontalMouseWheel,
            IsInRange,
            IsInverted,
            IsLeftButtonPressed,
            IsMiddleButtonPressed,
            IsRightButtonPressed,
            IsXButton1Pressed,
            IsXButton2Pressed,
            IsPrimary,
            IsCanceled,
            IsEraser,
            Orientation,
            PointerUpdateKind,
            Pressure,
            TouchConfidence,
            Twist,
            XTilt,
            YTilt,
            MouseWheelDelta);

    internal PointerPointProperties
        WithPrediction(
            float pressure,
            float xTilt,
            float yTilt) =>
        new(
            ContactRect,
            IsBarrelButtonPressed,
            IsHorizontalMouseWheel,
            IsInRange,
            IsInverted,
            IsLeftButtonPressed,
            IsMiddleButtonPressed,
            IsRightButtonPressed,
            IsXButton1Pressed,
            IsXButton2Pressed,
            IsPrimary,
            IsCanceled,
            IsEraser,
            Orientation,
            PointerUpdateKind,
            pressure,
            TouchConfidence,
            Twist,
            xTilt,
            yTilt,
            MouseWheelDelta);
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class PointerPoint
{
    internal PointerPoint(
        uint pointerId,
        ulong timestamp,
        Vector2 position,
        Vector2 rawPosition,
        Windows.Devices.Input.PointerDeviceType deviceType,
        bool isInContact,
        PointerPointProperties properties)
        : this(pointerId, timestamp, position, rawPosition, deviceType, deviceType switch
        {
            Windows.Devices.Input.PointerDeviceType.Touch => Microsoft.UI.Input.PointerDeviceType.Touch,
            Windows.Devices.Input.PointerDeviceType.Pen => Microsoft.UI.Input.PointerDeviceType.Pen,
            _ => Microsoft.UI.Input.PointerDeviceType.Mouse
        }, isInContact, properties)
    {
    }

    private PointerPoint(
        uint pointerId,
        ulong timestamp,
        Vector2 position,
        Vector2 rawPosition,
        Windows.Devices.Input.PointerDeviceType legacyDeviceType,
        Microsoft.UI.Input.PointerDeviceType deviceType,
        bool isInContact,
        PointerPointProperties properties)
        : this(
            pointerId,
            unchecked((uint)timestamp),
            timestamp,
            position,
            rawPosition,
            Windows.Devices.Input.PointerDevice
                .GetPointerDevice(legacyDeviceType),
            deviceType,
            isInContact,
            properties)
    {
    }

    private PointerPoint(
        uint pointerId,
        uint frameId,
        ulong timestamp,
        Vector2 position,
        Vector2 rawPosition,
        Windows.Devices.Input.PointerDevice
            pointerDevice,
        Microsoft.UI.Input.PointerDeviceType deviceType,
        bool isInContact,
        PointerPointProperties properties)
    {
        PointerId = pointerId;
        Timestamp = timestamp;
        FrameId = frameId;
        Position = new Windows.Foundation.Point(position.X, position.Y);
        RawPosition = rawPosition;
        PointerDevice = pointerDevice;
        PointerDeviceType = deviceType;
        IsInContact = isInContact;
        Properties = properties;
    }

    public uint FrameId { get; }
    public uint PointerId { get; }
    public ulong Timestamp { get; }
    public Windows.Foundation.Point Position { get; }
    internal Vector2 RawPosition { get; }
    public Microsoft.UI.Input.PointerDeviceType PointerDeviceType { get; }
    // Kept as a source-compatibility extension for existing ProGPU XAML code.
    public Windows.Devices.Input.PointerDevice PointerDevice { get; }
    public bool IsInContact { get; }
    public PointerPointProperties Properties { get; }

    public static PointerPoint GetCurrentPoint(
        uint pointerId)
    {
        if (!InputSystem.TryGetCurrentPointerInput(
                pointerId,
                out PointerInputEvent input))
        {
            return null!;
        }

        return FromInput(input);
    }

    public PointerPoint? GetTransformedPoint(IPointerPointTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!transform.TryTransform(Position, out var transformedPosition) ||
            !transform.TryTransformBounds(Properties.ContactRect, out var transformedContactRect)) return null;
        PointerPointProperties transformedProperties =
            Properties.WithContactRect(
                transformedContactRect);
        var transformed = new Vector2((float)transformedPosition.X, (float)transformedPosition.Y);
        return new PointerPoint(
            PointerId,
            FrameId,
            Timestamp,
            transformed,
            transformed,
            PointerDevice,
            PointerDeviceType,
            IsInContact,
            transformedProperties);
    }

    internal PointerPoint WithPrediction(
        ulong timestamp,
        Vector2 position,
        PointerPointProperties properties) =>
        new(
            PointerId,
            FrameId,
            timestamp,
            position,
            position,
            PointerDevice,
            PointerDeviceType,
            IsInContact,
            properties);

    internal static PointerPoint FromInput(
        in PointerInputEvent input) =>
        new(
            input.PointerId,
            input.Timestamp,
            input.Position,
            input.Position,
            input.DeviceType,
            input.IsInContact,
            new PointerPointProperties(
                contactRect:
                    new Windows.Foundation.Rect(
                        input.ContactRect.X,
                        input.ContactRect.Y,
                        input.ContactRect.Width,
                        input.ContactRect.Height),
                isLeftButtonPressed:
                    input.IsLeftButtonPressed,
                isMiddleButtonPressed:
                    input.IsMiddleButtonPressed,
                isRightButtonPressed:
                    input.IsRightButtonPressed,
                isHorizontalMouseWheel:
                    input.WheelDeltaX != 0f &&
                    input.WheelDeltaY == 0f,
                isPrimary: input.IsPrimary,
                isCanceled:
                    input.Kind ==
                    PointerInputKind.Canceled,
                pressure: input.Pressure,
                pointerUpdateKind:
                    input.UpdateKind,
                mouseWheelDelta:
                    (int)(input.WheelDeltaY != 0f
                        ? input.WheelDeltaY
                        : input.WheelDeltaX)));
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class PointerEventArgs
{
    private const int MaximumPointCount = 64;
    private static readonly IList<PointerPoint>
        EmptyPoints = Array.Empty<PointerPoint>();
    private readonly IList<PointerPoint>
        _intermediatePoints;

    internal PointerEventArgs(
        PointerPoint currentPoint,
        Windows.System.VirtualKeyModifiers keyModifiers =
            Windows.System.VirtualKeyModifiers.None,
        IReadOnlyList<PointerPoint>?
            historyBeforeCurrentPoint = null)
    {
        ArgumentNullException.ThrowIfNull(currentPoint);

        CurrentPoint = currentPoint;
        KeyModifiers = keyModifiers;

        int availableHistoryCount =
            historyBeforeCurrentPoint?.Count ?? 0;
        int historyCount = Math.Min(
            availableHistoryCount,
            MaximumPointCount - 1);
        int historyStart =
            availableHistoryCount - historyCount;
        var points =
            new PointerPoint[historyCount + 1];
        for (int index = 0;
             index < historyCount;
             index++)
        {
            points[index] =
                historyBeforeCurrentPoint![
                    historyStart + index];
        }
        points[^1] = currentPoint;
        _intermediatePoints =
            Array.AsReadOnly(points);
    }

    public PointerPoint CurrentPoint { get; }

    public bool Handled { get; set; }

    public Windows.System.VirtualKeyModifiers
        KeyModifiers { get; }

    public IList<PointerPoint>
        GetIntermediatePoints() =>
        _intermediatePoints;

    public IList<PointerPoint>
        GetIntermediateTransformedPoints(
            IPointerPointTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        var transformedPoints =
            new PointerPoint[_intermediatePoints.Count];
        for (int index = 0;
             index < transformedPoints.Length;
             index++)
        {
            PointerPoint? transformed =
                _intermediatePoints[index]
                    .GetTransformedPoint(transform);
            if (transformed is null)
                return EmptyPoints;
            transformedPoints[index] =
                transformed;
        }

        return Array.AsReadOnly(
            transformedPoints);
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public interface IPointerPointTransform
{
    IPointerPointTransform Inverse { get; }
    bool TryTransform(Windows.Foundation.Point inPoint, out Windows.Foundation.Point outPoint);
    bool TryTransformBounds(Windows.Foundation.Rect inRect, out Windows.Foundation.Rect outRect);
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum PointerDeviceType
{
    Touch = 0,
    Pen = 1,
    Mouse = 2,
    Touchpad = 3
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum PointerUpdateKind
{
    Other = 0,
    LeftButtonPressed = 1,
    LeftButtonReleased = 2,
    RightButtonPressed = 3,
    RightButtonReleased = 4,
    MiddleButtonPressed = 5,
    MiddleButtonReleased = 6,
    XButton1Pressed = 7,
    XButton1Released = 8,
    XButton2Pressed = 9,
    XButton2Released = 10
}
}

namespace Microsoft.UI.Xaml.Input
{
using InputPointerDeviceType = Microsoft.UI.Input.PointerDeviceType;
using LegacyPointerDeviceType = Windows.Devices.Input.PointerDeviceType;
using Microsoft.UI.Xaml.Markup;

public sealed class Pointer
{
    internal Pointer(uint pointerId, LegacyPointerDeviceType pointerDeviceType, bool isInContact, bool isInRange = true)
    {
        PointerId = pointerId;
        LegacyPointerDeviceType = pointerDeviceType;
        PointerDeviceType = pointerDeviceType switch
        {
            LegacyPointerDeviceType.Touch => InputPointerDeviceType.Touch,
            LegacyPointerDeviceType.Pen => InputPointerDeviceType.Pen,
            _ => InputPointerDeviceType.Mouse
        };
        IsInContact = isInContact;
        IsInRange = isInRange;
    }

    public uint PointerId { get; }
    public InputPointerDeviceType PointerDeviceType { get; }
    internal LegacyPointerDeviceType LegacyPointerDeviceType { get; }
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

internal static class GesturePosition
{
    public static Windows.Foundation.Point Get(
        Microsoft.UI.Xaml.UIElement? relativeTo,
        Vector2 screenPosition) =>
        InputSystem.GetLocalPosition(relativeTo as Microsoft.UI.Xaml.FrameworkElement, screenPosition);
}

public sealed class TappedRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    internal Vector2 ScreenPosition { get; init; }
    public InputPointerDeviceType PointerDeviceType { get; internal init; }
    public Windows.Foundation.Point GetPosition(Microsoft.UI.Xaml.UIElement? relativeTo) =>
        GesturePosition.Get(relativeTo, ScreenPosition);
}

public sealed class DoubleTappedRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    internal Vector2 ScreenPosition { get; init; }
    public InputPointerDeviceType PointerDeviceType { get; internal init; }
    public Windows.Foundation.Point GetPosition(Microsoft.UI.Xaml.UIElement? relativeTo) =>
        GesturePosition.Get(relativeTo, ScreenPosition);
}

public sealed class RightTappedRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    internal Vector2 ScreenPosition { get; init; }
    public InputPointerDeviceType PointerDeviceType { get; internal init; }
    public Windows.Foundation.Point GetPosition(Microsoft.UI.Xaml.UIElement? relativeTo) =>
        GesturePosition.Get(relativeTo, ScreenPosition);
}

public sealed class HoldingRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    internal Vector2 ScreenPosition { get; init; }
    public Microsoft.UI.Input.HoldingState HoldingState { get; internal init; }
    public InputPointerDeviceType PointerDeviceType { get; internal init; }
    public Windows.Foundation.Point GetPosition(Microsoft.UI.Xaml.UIElement? relativeTo) =>
        GesturePosition.Get(relativeTo, ScreenPosition);
}

public sealed class ManipulationPivot
{
    public ManipulationPivot()
    {
    }

    public ManipulationPivot(Windows.Foundation.Point center, double radius)
    {
        Center = center;
        Radius = radius;
    }

    public Windows.Foundation.Point Center { get; set; }
    public double Radius { get; set; }
}

public sealed class InertiaExpansionBehavior
{
    public double DesiredDeceleration { get; set; } = float.NaN;
    public double DesiredExpansion { get; set; } = float.NaN;
}

public sealed class InertiaRotationBehavior
{
    public double DesiredDeceleration { get; set; } = float.NaN;
    public double DesiredRotation { get; set; } = float.NaN;
}

public sealed class InertiaTranslationBehavior
{
    public double DesiredDeceleration { get; set; } = float.NaN;
    public double DesiredDisplacement { get; set; } = float.NaN;
}

public sealed class ManipulationStartingRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public ManipulationModes Mode { get; set; } = ManipulationModes.All;
    public Microsoft.UI.Xaml.UIElement? Container { get; set; }
    public ManipulationPivot? Pivot { get; set; }

    // Source-compatible aliases retained for early ProGPU callers.
    public Vector2 PivotCenter
    {
        get => Pivot?.Center ?? default;
        set => (Pivot ??= new ManipulationPivot()).Center = value;
    }
    public float PivotRadius
    {
        get => (float)(Pivot?.Radius ?? 0d);
        set => (Pivot ??= new ManipulationPivot()).Radius = value;
    }
}

public class ManipulationStartedRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public Microsoft.UI.Xaml.UIElement? Container { get; internal init; }
    public Microsoft.UI.Input.ManipulationDelta Cumulative { get; internal init; } = Microsoft.UI.Input.ManipulationDelta.Identity;
    public InputPointerDeviceType PointerDeviceType { get; internal init; }
    public Windows.Foundation.Point Position { get; internal init; }
    internal bool IsCompleteRequested { get; private set; }
    public void Complete() => IsCompleteRequested = true;
}

public sealed class ManipulationDeltaRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public Microsoft.UI.Xaml.UIElement? Container { get; internal init; }
    public Microsoft.UI.Input.ManipulationDelta Delta { get; internal init; } = Microsoft.UI.Input.ManipulationDelta.Identity;
    public Microsoft.UI.Input.ManipulationDelta Cumulative { get; internal init; } = Microsoft.UI.Input.ManipulationDelta.Identity;
    public Microsoft.UI.Input.ManipulationVelocities Velocities { get; internal init; }
    public bool IsInertial { get; internal init; }
    public InputPointerDeviceType PointerDeviceType { get; internal init; }
    public Windows.Foundation.Point Position { get; internal init; }
    internal bool IsCompleteRequested { get; private set; }
    public void Complete() => IsCompleteRequested = true;
}

public sealed class ManipulationInertiaStartingRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public Microsoft.UI.Xaml.UIElement? Container { get; internal init; }
    public Microsoft.UI.Input.ManipulationDelta Cumulative { get; internal init; } = Microsoft.UI.Input.ManipulationDelta.Identity;
    public Microsoft.UI.Input.ManipulationDelta Delta { get; internal init; } = Microsoft.UI.Input.ManipulationDelta.Identity;
    public Microsoft.UI.Input.ManipulationVelocities Velocities { get; internal init; }
    public InertiaExpansionBehavior ExpansionBehavior { get; set; } = new();
    public InertiaRotationBehavior RotationBehavior { get; set; } = new();
    public InertiaTranslationBehavior TranslationBehavior { get; set; } = new();
    public InputPointerDeviceType PointerDeviceType { get; internal init; }

    public float TranslationDeceleration
    {
        get => double.IsNaN(TranslationBehavior.DesiredDeceleration) ? 0f : (float)TranslationBehavior.DesiredDeceleration;
        set => TranslationBehavior.DesiredDeceleration = value;
    }
    public float RotationDeceleration
    {
        get => double.IsNaN(RotationBehavior.DesiredDeceleration) ? 0f : (float)RotationBehavior.DesiredDeceleration;
        set => RotationBehavior.DesiredDeceleration = value;
    }
    public float ExpansionDeceleration
    {
        get => double.IsNaN(ExpansionBehavior.DesiredDeceleration) ? 0f : (float)ExpansionBehavior.DesiredDeceleration;
        set => ExpansionBehavior.DesiredDeceleration = value;
    }
}

public sealed class ManipulationCompletedRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public Microsoft.UI.Xaml.UIElement? Container { get; internal init; }
    public Microsoft.UI.Input.ManipulationDelta Cumulative { get; internal init; } = Microsoft.UI.Input.ManipulationDelta.Identity;
    public Microsoft.UI.Input.ManipulationVelocities Velocities { get; internal init; }
    public bool IsInertial { get; internal init; }
    public InputPointerDeviceType PointerDeviceType { get; internal init; }
    public Windows.Foundation.Point Position { get; internal init; }
}

public delegate void TappedEventHandler(object sender, TappedRoutedEventArgs e);
public delegate void DoubleTappedEventHandler(object sender, DoubleTappedRoutedEventArgs e);
public delegate void RightTappedEventHandler(object sender, RightTappedRoutedEventArgs e);
public delegate void HoldingEventHandler(object sender, HoldingRoutedEventArgs e);
public delegate void ManipulationStartingEventHandler(object sender, ManipulationStartingRoutedEventArgs e);
public delegate void ManipulationStartedEventHandler(object sender, ManipulationStartedRoutedEventArgs e);
public delegate void ManipulationDeltaEventHandler(object sender, ManipulationDeltaRoutedEventArgs e);
public delegate void ManipulationInertiaStartingEventHandler(object sender, ManipulationInertiaStartingRoutedEventArgs e);
public delegate void ManipulationCompletedEventHandler(object sender, ManipulationCompletedRoutedEventArgs e);

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum InputScopeNameValue
{
    Default = 0,
    Url = 1,
    EmailSmtpAddress = 5,
    PersonalFullName = 7,
    CurrencyAmountAndSymbol = 20,
    CurrencyAmount = 21,
    DateMonthNumber = 23,
    DateDayNumber = 24,
    DateYear = 25,
    Digits = 28,
    Number = 29,
    Password = 31,
    TelephoneNumber = 32,
    TelephoneCountryCode = 33,
    TelephoneAreaCode = 34,
    TelephoneLocalNumber = 35,
    TimeHour = 37,
    TimeMinutesOrSeconds = 38,
    NumberFullWidth = 39,
    AlphanumericHalfWidth = 40,
    AlphanumericFullWidth = 41,
    Hiragana = 44,
    KatakanaHalfWidth = 45,
    KatakanaFullWidth = 46,
    Hanja = 47,
    HangulHalfWidth = 48,
    HangulFullWidth = 49,
    Search = 50,
    Formula = 51,
    SearchIncremental = 52,
    ChineseHalfWidth = 53,
    ChineseFullWidth = 54,
    NativeScript = 55,
    Text = 57,
    Chat = 58,
    NameOrPhoneNumber = 59,
    EmailNameOrAddress = 60,
    Maps = 62,
    NumericPassword = 63,
    NumericPin = 64,
    AlphanumericPin = 65,
    FormulaNumber = 67,
    ChatWithoutEmoji = 68
}

[ContentProperty(Name = nameof(NameValue))]
[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class InputScopeName :
    Microsoft.UI.Xaml.DependencyObject
{
    public InputScopeName()
    {
    }

    public InputScopeName(InputScopeNameValue nameValue)
    {
        NameValue = nameValue;
    }

    public InputScopeNameValue NameValue { get; set; }
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class InputScope :
    Microsoft.UI.Xaml.DependencyObject
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
    ReplaceText,
    SelectionChanged,
    Paste
}

public sealed class TextInputRoutedEventArgs : Microsoft.UI.Xaml.RoutedEventArgs
{
    public TextInputEventKind Kind { get; internal init; }
    public string Text { get; internal init; } = string.Empty;
    public bool IsComposing { get; internal init; }
    public int ReplacementStart { get; internal init; } = -1;
    public int ReplacementLength { get; internal init; }
    public int SelectionStart { get; internal init; } = -1;
    public int SelectionLength { get; internal init; }
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
    LegacyPointerDeviceType DeviceType,
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
    bool IsPreciseWheel = false,
    VirtualKeyModifiers Modifiers = VirtualKeyModifiers.None,
    Microsoft.UI.Input.PointerUpdateKind UpdateKind =
        Microsoft.UI.Input.PointerUpdateKind.Other);
}
