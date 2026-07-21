using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Diagnostics;
using Silk.NET.Input;
using ProGPU.Scene;
using ProGPU.Vector;
using Windows.Devices.Input;

namespace Microsoft.UI.Xaml.Input;

public class WindowInputState
{
    public FrameworkElement? Root;
    public FrameworkElement? HoveredElement;
    public FrameworkElement? FocusedElement;
    public Vector2 LastMousePos;
    public Func<Vector2, Vector2>? PointerPositionTransform;
    public FrameworkElement? CapturedElement;
    public bool IsShiftPressed;
    public bool IsControlPressed;
    public bool IsAltPressed;
    public bool IsKeyboardFocusActive;
    public IInputContext? InputContext;
    public System.Threading.CancellationTokenSource? HoverCancellation;
    public ToolTip? ActiveToolTip;
    public FrameworkElement? HoveredElementForTimer;
    public bool IsLeftButtonPressed;
    public bool IsMiddleButtonPressed;
    public bool IsRightButtonPressed;
    public Action<StandardCursor>? CursorChanged;
    internal FrameworkElement? ComposingElement;
    internal Dictionary<uint, PointerContactState> PointerContacts { get; } = new();
    internal Dictionary<uint, FrameworkElement> CapturedElements { get; } = new();
    internal Dictionary<FrameworkElement, ManipulationSession> Manipulations { get; } = new();
    internal uint CurrentDispatchPointerId;
    internal FrameworkElement? LastTappedElement;
    internal Vector2 LastTapPosition;
    internal ulong LastTapTimestamp;
    public Action<FrameworkElement?>? FocusChanged;
}

internal sealed class PointerContactState
{
    public required Pointer Pointer { get; init; }
    public required FrameworkElement? Target { get; init; }
    public required PointerInputEvent LastEvent { get; set; }
    public Vector2 DownPosition { get; init; }
    public ulong DownTimestamp { get; init; }
    public Vector2 Velocity { get; set; }
    public bool ExceededTapThreshold { get; set; }
    public bool HoldingStarted { get; set; }
    public CancellationTokenSource? HoldingCancellation { get; set; }
    public FrameworkElement? ManipulationTarget { get; set; }
    public FrameworkElement? OverTarget { get; set; }
    public bool CanceledByManipulation { get; set; }
    public bool StartedWithRightButton { get; init; }
}

internal sealed class ManipulationSession
{
    public required FrameworkElement Target { get; init; }
    public required ManipulationModes Mode { get; set; }
    public HashSet<uint> PointerIds { get; } = new();
    public Vector2 PreviousCentroid { get; set; }
    public float PreviousDistance { get; set; }
    public float PreviousAngle { get; set; }
    public Vector2 CumulativeTranslation { get; set; }
    public float CumulativeScale { get; set; } = 1f;
    public float CumulativeRotation { get; set; }
    public float CumulativeExpansion { get; set; }
    public ManipulationVelocities Velocities { get; set; }
    public ulong PreviousTimestamp { get; set; }
    public bool Started { get; set; }
}

public static class InputSystem
{
    private static readonly long s_timestampOrigin = Stopwatch.GetTimestamp();

    [ThreadStatic]
    private static WindowInputState? _currentState;

    public static WindowInputState Current
    {
        get => _currentState ??= new WindowInputState();
        set => _currentState = value;
    }

    // Public event injection entry points for external host containers (e.g. Avalonia)
    public static void InjectMouseMove(Vector2 screenPos) => OnMouseMove(screenPos);
    public static void InjectMouseDown(MouseButton button) => InjectPointer(CreateMouseButtonEvent(PointerInputKind.Pressed, button));
    public static void InjectMouseUp(MouseButton button)
    {
        InjectPointer(CreateMouseButtonEvent(PointerInputKind.Released, button));
        // Designer/toolbox drags can begin without a preceding press in this input state.
        // Preserve the legacy release-to-drop contract even when no pointer contact exists.
        if (button == MouseButton.Left && DragDropManager.IsDragging) DragDropManager.CompleteDrop(_lastMousePos);
    }
    public static void InjectMouseScroll(Vector2 scroll) => InjectPointer(CreateMouseEvent(PointerInputKind.Wheel, _lastMousePos) with { WheelDeltaX = scroll.X, WheelDeltaY = scroll.Y });
    public static void InjectKeyDown(Key key) => OnKeyDown(key);
    public static void InjectKeyUp(Key key) => OnKeyUp(key);
    public static void InjectKeyChar(char c) => OnKeyChar(c);
    public static void InjectFocusLost() => OnFocusLost();
    public static void InjectPointer(PointerInputEvent pointerEvent) => OnPointer(pointerEvent);
    public static void InjectTextInput(TextInputEventKind kind, string? text = null, bool isComposing = false)
    {
        if (_focusedElement == null) return;
        FrameworkElement target = _focusedElement;
        if (kind == TextInputEventKind.CompositionStarted)
        {
            Current.ComposingElement = target;
        }
        target.OnTextInput(new TextInputRoutedEventArgs
        {
            Kind = kind,
            Text = text ?? string.Empty,
            IsComposing = isComposing
        });
        if (kind is TextInputEventKind.CompositionCompleted or TextInputEventKind.CompositionCanceled)
        {
            Current.ComposingElement = null;
        }
    }

    public static void InjectTextReplacement(
        string text,
        int replacementStart,
        int replacementLength,
        int selectionStart,
        int selectionLength)
    {
        if (_focusedElement == null) return;
        _focusedElement.OnTextInput(new TextInputRoutedEventArgs
        {
            Kind = TextInputEventKind.ReplaceText,
            Text = text ?? string.Empty,
            ReplacementStart = replacementStart,
            ReplacementLength = replacementLength,
            SelectionStart = selectionStart,
            SelectionLength = selectionLength
        });
    }

    public static void InjectTextSelection(int selectionStart, int selectionLength)
    {
        if (_focusedElement == null) return;
        _focusedElement.OnTextInput(new TextInputRoutedEventArgs
        {
            Kind = TextInputEventKind.SelectionChanged,
            SelectionStart = selectionStart,
            SelectionLength = selectionLength
        });
    }

    public static Action<Action>? DispatcherQueue { get; set; }

    private static FrameworkElement? _root { get => Current.Root; set => Current.Root = value; }
    private static FrameworkElement? _hoveredElement { get => Current.HoveredElement; set => Current.HoveredElement = value; }
    private static FrameworkElement? _focusedElement { get => Current.FocusedElement; set => Current.FocusedElement = value; }
    private static Vector2 _lastMousePos { get => Current.LastMousePos; set => Current.LastMousePos = value; }
    private static FrameworkElement? _capturedElement { get => Current.CapturedElement; set => Current.CapturedElement = value; }
    private static bool _isShiftPressed { get => Current.IsShiftPressed; set => Current.IsShiftPressed = value; }
    private static bool _isControlPressed { get => Current.IsControlPressed; set => Current.IsControlPressed = value; }
    private static bool _isAltPressed { get => Current.IsAltPressed; set => Current.IsAltPressed = value; }
    private static System.Threading.CancellationTokenSource? _hoverCancellation { get => Current.HoverCancellation; set => Current.HoverCancellation = value; }
    private static ToolTip? _activeToolTip { get => Current.ActiveToolTip; set => Current.ActiveToolTip = value; }
    private static FrameworkElement? _hoveredElementForTimer { get => Current.HoveredElementForTimer; set => Current.HoveredElementForTimer = value; }

    private static bool IsControlPressedDynamic()
    {
        if (_isControlPressed) return true;
        if (Current.InputContext != null)
        {
            foreach (var keyboard in Current.InputContext.Keyboards)
            {
                if (keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsShiftPressedDynamic()
    {
        if (_isShiftPressed) return true;
        if (Current.InputContext != null)
        {
            foreach (var keyboard in Current.InputContext.Keyboards)
            {
                if (keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsAltPressedDynamic()
    {
        if (_isAltPressed) return true;
        if (Current.InputContext != null)
        {
            foreach (var keyboard in Current.InputContext.Keyboards)
            {
                if (keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static FrameworkElement? Root
    {
        get => Current.Root;
        set => Current.Root = value;
    }

    public static FrameworkElement? HoveredElement => _hoveredElement;
    public static FrameworkElement? FocusedElement => _focusedElement;
    public static Vector2 LastMousePosition => _lastMousePos;

    public static Vector2 NormalizePointerPositionForDpi(Vector2 pointerPosition, float dpiScale)
    {
        return float.IsFinite(dpiScale) && dpiScale > 0f
            ? pointerPosition / dpiScale
            : pointerPosition;
    }

    public static bool IsKeyboardFocusActive
    {
        get => Current.IsKeyboardFocusActive;
        set => Current.IsKeyboardFocusActive = value;
    }

    public static ToolTip? ActiveToolTip
    {
        get => _activeToolTip;
        private set
        {
            if (_activeToolTip != value)
            {
                _activeToolTip = value;
                _root?.Invalidate();
            }
        }
    }

    private static bool IsInsideDevTools(FrameworkElement? element)
    {
        var current = element;
        while (current != null)
        {
            if (current.Name == "DevToolsPanel") return true;
            current = current.Parent as FrameworkElement;
        }
        return false;
    }

    public static void CapturePointer(FrameworkElement? element)
    {
        if (element != null && IsInsideDevTools(element))
        {
            DevToolsInputSystem.CapturePointer(element);
            return;
        }
        var pointerId = Current.CurrentDispatchPointerId;
        if (pointerId != 0 && Current.PointerContacts.TryGetValue(pointerId, out var contact))
        {
            CapturePointer(element!, contact.Pointer);
            return;
        }
        _capturedElement = element;
    }

    public static bool CapturePointer(FrameworkElement element, Pointer pointer)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(pointer);
        if (!pointer.IsInContact || !Current.PointerContacts.ContainsKey(pointer.PointerId)) return false;
        if (Current.CapturedElements.ContainsKey(pointer.PointerId)) return false;
        Current.CapturedElements[pointer.PointerId] = element;
        if (pointer.PointerId == 1) _capturedElement = element;
        return true;
    }

    public static void ReleasePointerCapture()
    {
        var pointerId = Current.CurrentDispatchPointerId;
        if (pointerId != 0 && Current.CapturedElements.TryGetValue(pointerId, out var element) &&
            Current.PointerContacts.TryGetValue(pointerId, out var contact))
        {
            ReleasePointerCapture(element, contact.Pointer);
            return;
        }
        _capturedElement = null;
        DevToolsInputSystem.ReleasePointerCapture();
    }

    public static void ReleasePointerCapture(FrameworkElement element, Pointer pointer)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(pointer);
        if (!Current.CapturedElements.TryGetValue(pointer.PointerId, out var captured) || !ReferenceEquals(captured, element)) return;
        Current.CapturedElements.Remove(pointer.PointerId);
        if (pointer.PointerId == 1) _capturedElement = null;
        RaiseCaptureLost(element, Current.PointerContacts.TryGetValue(pointer.PointerId, out var contact) ? contact.LastEvent : default, pointer);
    }

    public static void ReleasePointerCaptures(FrameworkElement element)
    {
        var ids = Current.CapturedElements.Where(pair => ReferenceEquals(pair.Value, element)).Select(pair => pair.Key).ToArray();
        foreach (var pointerId in ids)
        {
            if (Current.PointerContacts.TryGetValue(pointerId, out var contact)) ReleasePointerCapture(element, contact.Pointer);
            else Current.CapturedElements.Remove(pointerId);
        }
    }

    public static void SetMouseCursor(StandardCursor cursor)
    {
        Current.CursorChanged?.Invoke(cursor);
        if (Current.InputContext != null)
        {
            foreach (var mouse in Current.InputContext.Mice)
            {
                try
                {
                    mouse.Cursor.StandardCursor = cursor;
                }
                catch
                {
                    // Ignore if cursor configuration is unsupported in the current platform/context
                }
            }
        }
    }

    /// <summary>
    /// Creates input state for hosts that inject platform events without a Silk input context.
    /// </summary>
    public static WindowInputState CreateExternalState(
        FrameworkElement? root = null,
        Func<Vector2, Vector2>? pointerPositionTransform = null,
        Action<StandardCursor>? cursorChanged = null)
    {
        return new WindowInputState
        {
            Root = root,
            PointerPositionTransform = pointerPositionTransform,
            CursorChanged = cursorChanged
        };
    }

    public static WindowInputState Initialize(
        IInputContext input,
        FrameworkElement? root = null,
        Func<Vector2, Vector2>? pointerPositionTransform = null)
    {
        var state = new WindowInputState
        {
            Root = root,
            InputContext = input,
            PointerPositionTransform = pointerPositionTransform
        };

        foreach (var mouse in input.Mice)
        {
            mouse.MouseMove += (m, pos) => {
                _currentState = state;
                OnMouseMove(NormalizeInputPosition(state, new Vector2(pos.X, pos.Y)));
            };
            mouse.MouseDown += (m, btn) => {
                _currentState = state;
                InjectMouseDown(btn);
            };
            mouse.MouseUp += (m, btn) => {
                _currentState = state;
                InjectMouseUp(btn);
            };
            mouse.Scroll += (m, scroll) => {
                _currentState = state;
                InjectMouseScroll(new Vector2(scroll.X, scroll.Y));
            };
        }

        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += (kb, key, code) => {
                _currentState = state;
                if (key == Key.ShiftLeft || key == Key.ShiftRight) state.IsShiftPressed = true;
                if (key == Key.ControlLeft || key == Key.ControlRight) state.IsControlPressed = true;
                if (key == Key.AltLeft || key == Key.AltRight) state.IsAltPressed = true;
                OnKeyDown(key);
            };
            keyboard.KeyUp += (kb, key, code) => {
                _currentState = state;
                if (key == Key.ShiftLeft || key == Key.ShiftRight) state.IsShiftPressed = false;
                if (key == Key.ControlLeft || key == Key.ControlRight) state.IsControlPressed = false;
                if (key == Key.AltLeft || key == Key.AltRight) state.IsAltPressed = false;
                OnKeyUp(key);
            };
            keyboard.KeyChar += (kb, c) => {
                _currentState = state;
                OnKeyChar(c);
            };
        }

        return state;
    }

    private static Vector2 NormalizeInputPosition(WindowInputState state, Vector2 pointerPosition)
    {
        return state.PointerPositionTransform?.Invoke(pointerPosition) ?? pointerPosition;
    }

    private static ulong GetTimestamp()
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - s_timestampOrigin;
        long wholeSeconds = elapsedTicks / Stopwatch.Frequency;
        long remainingTicks = elapsedTicks % Stopwatch.Frequency;
        var fractionalMicros = (ulong)((UInt128)(ulong)remainingTicks * 1_000_000UL / (ulong)Stopwatch.Frequency);
        return (ulong)wholeSeconds * 1_000_000UL + fractionalMicros;
    }

    private static PointerInputEvent CreateMouseEvent(PointerInputKind kind, Vector2 position) => new(
        kind,
        1,
        PointerDeviceType.Mouse,
        position,
        GetTimestamp(),
        IsPrimary: true,
        IsInContact: Current.IsLeftButtonPressed || Current.IsMiddleButtonPressed || Current.IsRightButtonPressed,
        IsLeftButtonPressed: Current.IsLeftButtonPressed,
        IsMiddleButtonPressed: Current.IsMiddleButtonPressed,
        IsRightButtonPressed: Current.IsRightButtonPressed,
        Pressure: Current.IsLeftButtonPressed ? 0.5f : 0f,
        Modifiers: GetCurrentModifiers());

    private static PointerInputEvent CreateMouseButtonEvent(PointerInputKind kind, MouseButton button)
    {
        var left = Current.IsLeftButtonPressed;
        var middle = Current.IsMiddleButtonPressed;
        var right = Current.IsRightButtonPressed;
        var pressed = kind == PointerInputKind.Pressed;
        if (button == MouseButton.Left) left = pressed;
        else if (button == MouseButton.Middle) middle = pressed;
        else if (button == MouseButton.Right) right = pressed;
        return CreateMouseEvent(kind, _lastMousePos) with
        {
            IsInContact = left || middle || right,
            IsLeftButtonPressed = left,
            IsMiddleButtonPressed = middle,
            IsRightButtonPressed = right,
            Pressure = left ? 0.5f : 0f
        };
    }

    private static VirtualKeyModifiers GetCurrentModifiers()
    {
        var modifiers = VirtualKeyModifiers.None;
        if (IsShiftPressedDynamic()) modifiers |= VirtualKeyModifiers.Shift;
        if (IsControlPressedDynamic()) modifiers |= VirtualKeyModifiers.Control;
        if (IsAltPressedDynamic()) modifiers |= VirtualKeyModifiers.Menu;
        return modifiers;
    }

    private static void OnPointer(PointerInputEvent input)
    {
        _lastMousePos = input.Position;
        Current.IsShiftPressed = input.Modifiers.HasFlag(VirtualKeyModifiers.Shift);
        Current.IsControlPressed = input.Modifiers.HasFlag(VirtualKeyModifiers.Control);
        Current.IsAltPressed = input.Modifiers.HasFlag(VirtualKeyModifiers.Menu);
        Current.IsLeftButtonPressed = input.IsLeftButtonPressed;
        Current.IsMiddleButtonPressed = input.IsMiddleButtonPressed;
        Current.IsRightButtonPressed = input.IsRightButtonPressed;
        Current.CurrentDispatchPointerId = input.PointerId;

        try
        {
            switch (input.Kind)
            {
                case PointerInputKind.Pressed:
                    OnPointerPressedCore(input);
                    break;
                case PointerInputKind.Moved:
                    OnPointerMovedCore(input);
                    break;
                case PointerInputKind.Released:
                    OnPointerReleasedCore(input, canceled: false);
                    break;
                case PointerInputKind.Canceled:
                    OnPointerReleasedCore(input, canceled: true);
                    break;
                case PointerInputKind.Wheel:
                    OnPointerWheelCore(input);
                    break;
            }
        }
        finally
        {
            Current.CurrentDispatchPointerId = 0;
        }
    }

    private static void OnPointerPressedCore(PointerInputEvent input)
    {
        if (Current.PointerContacts.TryGetValue(input.PointerId, out var previousContact))
        {
            OnPointerReleasedCore(previousContact.LastEvent with
            {
                Kind = PointerInputKind.Canceled,
                IsInContact = false,
                Timestamp = input.Timestamp
            }, canceled: true);
        }
        IsKeyboardFocusActive = false;
        DismissToolTip();
        var target = HitTest(input.Position);
        ProGpuWinUiDiagnostics.WriteLine($"[InputSystem] PointerDown {input.PointerId} ({input.DeviceType}) at {input.Position}. Hit: {target?.GetType().Name} (Name: {target?.Name})");

        if (DevToolsService.IsInspectModeActive || (IsControlPressedDynamic() && IsShiftPressedDynamic()))
        {
            if (!IsDescendantOf(target, "DevToolsPanel"))
            {
                if (target != null)
                {
                    if (!DevToolsService.IsDevToolsActive) DevToolsService.IsDevToolsActive = true;
                    DevToolsService.InspectedElement = target;
                    DevToolsService.IsInspectModeActive = false;
                }
                return;
            }
        }

        if (PopupService.ActivePopups.Count > 0)
        {
            var topmostPopup = PopupService.ActivePopups[^1];
            if (!IsDescendantOf(target, topmostPopup) && topmostPopup is not ContentDialog)
            {
                var owner = PopupService.GetOwner(topmostPopup);
                PopupService.HidePopup(topmostPopup);
                target = HitTest(input.Position);
                if (target != null && owner != null && IsDescendantOf(target, owner))
                {
                    SetFocus(target);
                    return;
                }
            }
        }
        var pointer = new Pointer(input.PointerId, input.DeviceType, true);
        var contact = new PointerContactState
        {
            Pointer = pointer,
            Target = target,
            LastEvent = input,
            DownPosition = input.Position,
            DownTimestamp = input.Timestamp,
            StartedWithRightButton = input.IsRightButtonPressed
        };
        Current.PointerContacts[input.PointerId] = contact;

        if (target != null)
        {
            if (input.DeviceType == PointerDeviceType.Touch)
            {
                UpdateContactOver(contact, target, input);
            }
            else
            {
                UpdateHover(input);
            }
            target.OnPointerPressed(CreatePointerArgs(target, input, pointer));
            BeginHolding(contact);
            BeginManipulation(contact);
        }
        else
        {
            SetFocus(null);
        }
    }

    private static void OnPointerMovedCore(PointerInputEvent input)
    {
        if (DragDropManager.IsDragging && DragDropManager.IsPointerOwner(input.PointerId))
        {
            if (Current.PointerContacts.TryGetValue(input.PointerId, out var dragContact)) dragContact.LastEvent = input;
            DragDropManager.UpdateDrag(input.Position);
            return;
        }

        if (input.DeviceType == PointerDeviceType.Mouse &&
            (DevToolsService.IsInspectModeActive || (IsControlPressedDynamic() && IsShiftPressedDynamic())))
        {
            var inspectHit = HitTest(input.Position);
            var insideDevTools = IsDescendantOf(inspectHit, "DevToolsPanel");
            DevToolsService.HoveredElement = insideDevTools ? null : inspectHit;
            if (!insideDevTools) return;
        }

        if (!Current.PointerContacts.TryGetValue(input.PointerId, out var contact))
        {
            if (input.DeviceType is PointerDeviceType.Mouse or PointerDeviceType.Pen) UpdateHover(input);
            var hoverTarget = HitTest(input.Position);
            hoverTarget?.OnPointerMoved(CreatePointerArgs(hoverTarget, input, new Pointer(input.PointerId, input.DeviceType, input.IsInContact)));
            return;
        }

        var elapsedMicros = input.Timestamp > contact.LastEvent.Timestamp
            ? input.Timestamp - contact.LastEvent.Timestamp
            : 1UL;
        contact.Velocity = (input.Position - contact.LastEvent.Position) * (1_000_000f / elapsedMicros);
        contact.LastEvent = input;
        var threshold = GetTapThreshold(input.DeviceType);
        if (Vector2.DistanceSquared(contact.DownPosition, input.Position) > threshold * threshold)
        {
            contact.ExceededTapThreshold = true;
            CancelHolding(contact, raiseCanceled: true);
        }

        var hit = HitTest(input.Position);
        if (input.DeviceType == PointerDeviceType.Touch) UpdateContactOver(contact, hit, input);
        else UpdateHover(input);
        var target = Current.CapturedElements.GetValueOrDefault(input.PointerId) ?? hit;
        target?.OnPointerMoved(CreatePointerArgs(target, input, contact.Pointer));
        UpdateManipulation(contact);
    }

    private static void OnPointerReleasedCore(PointerInputEvent input, bool canceled)
    {
        if (!Current.PointerContacts.TryGetValue(input.PointerId, out var contact))
        {
            return;
        }

        var completesDrag = !canceled && DragDropManager.IsDragging && DragDropManager.IsPointerOwner(input.PointerId);
        var cancelsDrag = canceled && DragDropManager.IsDragging && DragDropManager.IsPointerOwner(input.PointerId);
        contact.LastEvent = input;
        contact.Pointer.IsInContact = false;
        var hit = HitTest(input.Position);
        if (!canceled)
        {
            if (input.DeviceType == PointerDeviceType.Touch) UpdateContactOver(contact, hit, input);
            else UpdateHover(input);
        }
        var target = Current.CapturedElements.GetValueOrDefault(input.PointerId) ?? (canceled ? contact.Target : hit);
        var args = CreatePointerArgs(target, input, contact.Pointer, canceled);
        if (completesDrag)
        {
            DragDropManager.CompleteDrop(input.Position);
            contact.Target?.OnPointerCanceled(CreatePointerArgs(contact.Target, input, contact.Pointer, canceled: true));
        }
        else if (cancelsDrag)
        {
            DragDropManager.CancelDrag();
            contact.Target?.OnPointerCanceled(CreatePointerArgs(contact.Target, input, contact.Pointer, canceled: true));
        }
        else if (canceled && !contact.CanceledByManipulation) target?.OnPointerCanceled(args);
        else if (!canceled && !contact.CanceledByManipulation) target?.OnPointerReleased(args);

        EndManipulation(contact, canceled || completesDrag || cancelsDrag);
        if (!canceled && !completesDrag && !cancelsDrag && !contact.CanceledByManipulation) CompleteGesture(contact, input);
        else CancelHolding(contact, raiseCanceled: true);

        if (Current.CapturedElements.Remove(input.PointerId, out var captured)) RaiseCaptureLost(captured, input, contact.Pointer);
        if (input.PointerId == 1) _capturedElement = null;
        contact.HoldingCancellation?.Cancel();
        Current.PointerContacts.Remove(input.PointerId);

        if (input.DeviceType is PointerDeviceType.Mouse or PointerDeviceType.Pen) UpdateHover(input);
        else UpdateContactOver(contact, null, input, canceled);
    }

    private static void OnPointerWheelCore(PointerInputEvent input)
    {
        DismissToolTip();
        var target = HitTest(input.Position);
        if (target == null) return;
        var pointer = new Pointer(input.PointerId, input.DeviceType, false);
        target.OnPointerWheelChanged(CreatePointerArgs(target, input, pointer));
    }

    private static PointerRoutedEventArgs CreatePointerArgs(
        FrameworkElement? target,
        PointerInputEvent input,
        Pointer pointer,
        bool canceled = false) => new()
    {
        Position = GetLocalPosition(target, input.Position),
        ScreenPosition = input.Position,
        Pointer = pointer,
        Timestamp = input.Timestamp,
        IsPrimary = input.IsPrimary,
        IsLeftButtonPressed = input.IsLeftButtonPressed,
        IsMiddleButtonPressed = input.IsMiddleButtonPressed,
        IsRightButtonPressed = input.IsRightButtonPressed,
        Pressure = input.Pressure,
        ContactRect = input.ContactRect,
        WheelDelta = input.WheelDeltaY,
        WheelDeltaX = input.WheelDeltaX,
        IsPreciseScrolling = input.IsPreciseWheel,
        KeyModifiers = input.Modifiers,
        IsCanceled = canceled
    };

    private static void RaiseCaptureLost(FrameworkElement element, PointerInputEvent input, Pointer pointer)
    {
        element.OnPointerCaptureLost(CreatePointerArgs(element, input, pointer, input.Kind == PointerInputKind.Canceled));
    }

    private static void UpdateHover(PointerInputEvent input)
    {
        var hit = HitTest(input.Position);
        if (ReferenceEquals(hit, _hoveredElement)) return;
        var oldPath = GetVisualPath(_hoveredElement);
        var newPath = GetVisualPath(hit);
        var common = -1;
        for (var index = 0; index < Math.Min(oldPath.Count, newPath.Count) && ReferenceEquals(oldPath[index], newPath[index]); index++) common = index;
        var pointer = new Pointer(input.PointerId, input.DeviceType, input.IsInContact);
        for (var index = oldPath.Count - 1; index > common; index--) oldPath[index].OnPointerExited(CreatePointerArgs(oldPath[index], input, pointer));
        for (var index = common + 1; index < newPath.Count; index++) newPath[index].OnPointerEntered(CreatePointerArgs(newPath[index], input, pointer));
        _hoveredElement = hit;
        ResetHoverTimer(hit);
    }

    private static void UpdateContactOver(
        PointerContactState contact,
        FrameworkElement? hit,
        PointerInputEvent input,
        bool canceled = false)
    {
        if (ReferenceEquals(hit, contact.OverTarget)) return;
        var oldPath = GetVisualPath(contact.OverTarget);
        var newPath = GetVisualPath(hit);
        var common = -1;
        for (var index = 0; index < Math.Min(oldPath.Count, newPath.Count) && ReferenceEquals(oldPath[index], newPath[index]); index++) common = index;
        for (var index = oldPath.Count - 1; index > common; index--)
        {
            oldPath[index].OnPointerExited(CreatePointerArgs(oldPath[index], input, contact.Pointer, canceled));
        }
        for (var index = common + 1; index < newPath.Count; index++)
        {
            newPath[index].OnPointerEntered(CreatePointerArgs(newPath[index], input, contact.Pointer, canceled));
        }
        contact.OverTarget = hit;
    }

    private static bool IsDescendantOf(FrameworkElement? element, FrameworkElement ancestor)
    {
        for (var current = element; current != null; current = current.Parent as FrameworkElement)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static bool IsDescendantOf(FrameworkElement? element, string ancestorName)
    {
        for (var current = element; current != null; current = current.Parent as FrameworkElement)
            if (string.Equals(current.Name, ancestorName, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void BeginHolding(PointerContactState contact)
    {
        if (contact.Target == null || !contact.Target.IsHoldingEnabled || contact.Pointer.PointerDeviceType == PointerDeviceType.Mouse) return;
        var cancellation = new CancellationTokenSource();
        contact.HoldingCancellation = cancellation;
        var state = Current;
        Task.Delay(TimeSpan.FromMilliseconds(800), cancellation.Token).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully || cancellation.IsCancellationRequested) return;
            void Raise()
            {
                InputSystem.Current = state;
                if (!state.PointerContacts.TryGetValue(contact.Pointer.PointerId, out var active) || active.ExceededTapThreshold) return;
                active.HoldingStarted = true;
                active.Target?.OnHolding(new HoldingRoutedEventArgs
                {
                    PointerDeviceType = active.Pointer.PointerDeviceType,
                    ScreenPosition = active.LastEvent.Position,
                    HoldingState = HoldingState.Started
                });
            }
            if (DispatcherQueue != null) DispatcherQueue(Raise); else Raise();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void CancelHolding(PointerContactState contact, bool raiseCanceled)
    {
        contact.HoldingCancellation?.Cancel();
        if (!contact.HoldingStarted || !raiseCanceled || contact.Target == null) return;
        contact.HoldingStarted = false;
        contact.Target.OnHolding(new HoldingRoutedEventArgs
        {
            PointerDeviceType = contact.Pointer.PointerDeviceType,
            ScreenPosition = contact.LastEvent.Position,
            HoldingState = HoldingState.Canceled
        });
    }

    private static void CompleteGesture(PointerContactState contact, PointerInputEvent input)
    {
        contact.HoldingCancellation?.Cancel();
        if (contact.Target == null) return;
        if (contact.HoldingStarted)
        {
            contact.Target.OnHolding(new HoldingRoutedEventArgs
            {
                PointerDeviceType = contact.Pointer.PointerDeviceType,
                ScreenPosition = input.Position,
                HoldingState = HoldingState.Completed
            });
            return;
        }

        var contactDuration = input.Timestamp >= contact.DownTimestamp
            ? input.Timestamp - contact.DownTimestamp
            : ulong.MaxValue;
        var tapThreshold = GetTapThreshold(input.DeviceType);
        var releaseExceededTapThreshold =
            Vector2.DistanceSquared(contact.DownPosition, input.Position) > tapThreshold * tapThreshold;
        if (contact.ExceededTapThreshold || releaseExceededTapThreshold || contactDuration > 800_000UL) return;
        if (input.DeviceType == PointerDeviceType.Mouse && contact.StartedWithRightButton)
        {
            if (contact.Target.IsRightTapEnabled)
            {
                contact.Target.OnRightTapped(new RightTappedRoutedEventArgs
                {
                    PointerId = input.PointerId,
                    PointerDeviceType = input.DeviceType,
                    ScreenPosition = input.Position
                });
            }
            return;
        }

        if (!contact.Target.IsTapEnabled) return;
        var tapped = new TappedRoutedEventArgs
        {
            PointerId = input.PointerId,
            PointerDeviceType = input.DeviceType,
            ScreenPosition = input.Position
        };
        contact.Target.OnTapped(tapped);

        var isDouble = contact.Target.IsDoubleTapEnabled &&
            ReferenceEquals(Current.LastTappedElement, contact.Target) &&
            input.Timestamp >= Current.LastTapTimestamp &&
            input.Timestamp - Current.LastTapTimestamp <= 500_000UL &&
            Vector2.DistanceSquared(Current.LastTapPosition, input.Position) <= 24f * 24f;
        if (isDouble)
        {
            contact.Target.OnDoubleTapped(new DoubleTappedRoutedEventArgs
            {
                PointerId = input.PointerId,
                PointerDeviceType = input.DeviceType,
                ScreenPosition = input.Position
            });
            Current.LastTappedElement = null;
            Current.LastTapTimestamp = 0;
        }
        else
        {
            Current.LastTappedElement = contact.Target;
            Current.LastTapPosition = input.Position;
            Current.LastTapTimestamp = input.Timestamp;
        }
    }

    private static float GetTapThreshold(PointerDeviceType deviceType) =>
        deviceType == PointerDeviceType.Touch ? 12f : 4f;

    private static FrameworkElement? FindManipulationTarget(FrameworkElement? element)
    {
        var current = element;
        while (current != null)
        {
            if (current.ManipulationMode is not (ManipulationModes.None or ManipulationModes.System)) return current;
            current = current.Parent as FrameworkElement;
        }
        return null;
    }

    private static void BeginManipulation(PointerContactState contact)
    {
        if (contact.Pointer.PointerDeviceType == PointerDeviceType.Mouse) return;
        if (DragDropManager.IsDragging && DragDropManager.IsPointerOwner(contact.Pointer.PointerId)) return;
        var target = FindManipulationTarget(contact.Target);
        if (target == null) return;
        if (Current.CapturedElements.ContainsKey(contact.Pointer.PointerId)) return;
        contact.ManipulationTarget = target;
        if (!Current.Manipulations.TryGetValue(target, out var session))
        {
            var starting = new ManipulationStartingRoutedEventArgs
            {
                OriginalSource = target,
                Mode = target.ManipulationMode,
                Container = target,
                PivotCenter = contact.LastEvent.Position
            };
            target.OnManipulationStarting(starting);
            if (starting.Mode == ManipulationModes.None) return;
            session = new ManipulationSession
            {
                Target = target,
                Mode = starting.Mode,
                PreviousTimestamp = contact.LastEvent.Timestamp
            };
            Current.Manipulations[target] = session;
        }
        session.PointerIds.Add(contact.Pointer.PointerId);
        if (session.Started)
        {
            CancelContactForManipulation(contact);
        }
        ResetManipulationBaseline(session);
    }

    private static void CancelContactForManipulation(PointerContactState contact)
    {
        if (contact.CanceledByManipulation) return;
        contact.ExceededTapThreshold = true;
        contact.CanceledByManipulation = true;
        contact.Target?.OnPointerCanceled(CreatePointerArgs(contact.Target, contact.LastEvent, contact.Pointer, canceled: true));
    }

    private static void ResetManipulationBaseline(ManipulationSession session)
    {
        var points = session.PointerIds
            .Select(id => Current.PointerContacts.TryGetValue(id, out var state) ? state.LastEvent.Position : (Vector2?)null)
            .Where(point => point.HasValue)
            .Select(point => point!.Value)
            .ToArray();
        if (points.Length == 0) return;
        session.PreviousCentroid = points.Aggregate(Vector2.Zero, static (sum, point) => sum + point) / points.Length;
        if (points.Length >= 2)
        {
            var vector = points[1] - points[0];
            session.PreviousDistance = Math.Max(0.0001f, vector.Length());
            session.PreviousAngle = MathF.Atan2(vector.Y, vector.X);
        }
    }

    private static void UpdateManipulation(PointerContactState contact)
    {
        if (contact.ManipulationTarget == null || !Current.Manipulations.TryGetValue(contact.ManipulationTarget, out var session)) return;
        var points = session.PointerIds
            .Select(id => Current.PointerContacts.TryGetValue(id, out var state) ? state.LastEvent.Position : (Vector2?)null)
            .Where(point => point.HasValue)
            .Select(point => point!.Value)
            .ToArray();
        if (points.Length == 0) return;

        var centroid = points.Aggregate(Vector2.Zero, static (sum, point) => sum + point) / points.Length;
        var translation = centroid - session.PreviousCentroid;
        if (!session.Mode.HasFlag(ManipulationModes.TranslateX) && !session.Mode.HasFlag(ManipulationModes.TranslateRailsX)) translation.X = 0;
        if (!session.Mode.HasFlag(ManipulationModes.TranslateY) && !session.Mode.HasFlag(ManipulationModes.TranslateRailsY)) translation.Y = 0;

        var scale = 1f;
        var rotation = 0f;
        var expansion = 0f;
        if (points.Length >= 2)
        {
            var vector = points[1] - points[0];
            var distance = Math.Max(0.0001f, vector.Length());
            var angle = MathF.Atan2(vector.Y, vector.X);
            if (session.Mode.HasFlag(ManipulationModes.Scale))
            {
                scale = distance / Math.Max(0.0001f, session.PreviousDistance);
                expansion = distance - session.PreviousDistance;
            }
            if (session.Mode.HasFlag(ManipulationModes.Rotate)) rotation = (angle - session.PreviousAngle) * (180f / MathF.PI);
            session.PreviousDistance = distance;
            session.PreviousAngle = angle;
        }

        var meaningful = session.PointerIds.Any(id =>
            Current.PointerContacts.TryGetValue(id, out var state) && state.ExceededTapThreshold);
        if (!session.Started && !meaningful) return;
        if (!session.Started)
        {
            session.Started = true;
            foreach (var id in session.PointerIds)
            {
                if (!Current.PointerContacts.TryGetValue(id, out var state)) continue;
                CancelContactForManipulation(state);
            }
            session.Target.OnManipulationStarted(new ManipulationStartedRoutedEventArgs
            {
                OriginalSource = session.Target,
                Position = centroid
            });
        }

        session.CumulativeTranslation += translation;
        session.CumulativeScale *= scale;
        session.CumulativeRotation += rotation;
        session.CumulativeExpansion += expansion;
        var elapsedMicros = contact.LastEvent.Timestamp > session.PreviousTimestamp
            ? contact.LastEvent.Timestamp - session.PreviousTimestamp
            : 1UL;
        var seconds = elapsedMicros / 1_000_000f;
        session.Velocities = new ManipulationVelocities(translation / seconds, rotation / seconds, expansion / seconds);
        session.PreviousTimestamp = contact.LastEvent.Timestamp;
        session.PreviousCentroid = centroid;
        var delta = new ManipulationDelta(translation, scale, rotation, expansion);
        var cumulative = new ManipulationDelta(session.CumulativeTranslation, session.CumulativeScale, session.CumulativeRotation, session.CumulativeExpansion);
        var args = new ManipulationDeltaRoutedEventArgs
        {
            OriginalSource = session.Target,
            Delta = delta,
            Cumulative = cumulative,
            Velocities = session.Velocities
        };
        session.Target.OnManipulationDelta(args);
        if (args.Complete) CompleteManipulation(session, inertial: false);
    }

    private static void EndManipulation(PointerContactState contact, bool canceled)
    {
        if (contact.ManipulationTarget == null || !Current.Manipulations.TryGetValue(contact.ManipulationTarget, out var session)) return;
        session.PointerIds.Remove(contact.Pointer.PointerId);
        if (session.PointerIds.Count != 0)
        {
            ResetManipulationBaseline(session);
            return;
        }
        CompleteManipulation(session, inertial: !canceled &&
            (session.Mode.HasFlag(ManipulationModes.TranslateInertia) || session.Mode.HasFlag(ManipulationModes.RotateInertia) || session.Mode.HasFlag(ManipulationModes.ScaleInertia)));
    }

    private static void CompleteManipulation(ManipulationSession session, bool inertial)
    {
        var cumulative = new ManipulationDelta(session.CumulativeTranslation, session.CumulativeScale, session.CumulativeRotation, session.CumulativeExpansion);
        if (session.Started && inertial)
        {
            session.Target.OnManipulationInertiaStarting(new ManipulationInertiaStartingRoutedEventArgs
            {
                OriginalSource = session.Target,
                Cumulative = cumulative,
                Velocities = session.Velocities
            });
        }
        if (session.Started)
        {
            session.Target.OnManipulationCompleted(new ManipulationCompletedRoutedEventArgs
            {
                OriginalSource = session.Target,
                Cumulative = cumulative,
                Velocities = session.Velocities,
                IsInertial = inertial
            });
        }
        Current.Manipulations.Remove(session.Target);
    }

    public static void SetFocus(FrameworkElement? element)
    {
        if (element != null && IsInsideDevTools(element))
        {
            DevToolsInputSystem.SetFocus(element);
            return;
        }

        if (_focusedElement == element) return;

        var oldFocus = _focusedElement;
        if (oldFocus != null && ReferenceEquals(Current.ComposingElement, oldFocus))
        {
            oldFocus.OnTextInput(new TextInputRoutedEventArgs
            {
                Kind = TextInputEventKind.CompositionCanceled
            });
            Current.ComposingElement = null;
        }
        _focusedElement = element;

        if (oldFocus is Control oldControl)
        {
            oldControl.IsFocused = false;
        }

        if (_focusedElement is Control newControl)
        {
            newControl.IsFocused = true;
        }
        Current.FocusChanged?.Invoke(_focusedElement);
    }

    public static FrameworkElement? HitTest(Vector2 screenPoint)
    {
        // First, check active popups in reverse order (topmost first)
        for (int i = PopupService.ActivePopups.Count - 1; i >= 0; i--)
        {
            var popup = PopupService.ActivePopups[i];
            var hit = HitTestInternal(popup, screenPoint, Matrix4x4.Identity);
            if (hit != null) return hit;
        }

        if (_root == null) return null;
        return HitTestInternal(_root, screenPoint, Matrix4x4.Identity);
    }

    private static bool HasBackground(FrameworkElement fe)
    {
        return fe switch
        {
            IHitTestBackgroundProvider provider => provider.HasHitTestBackground,
            Control control => control.Background != null,
            Border border => border.Background != null,
            ContentPresenter presenter => presenter.Background != null,
            _ => false
        };
    }

    private static FrameworkElement? HitTestInternal(Visual visual, Vector2 screenPoint, Matrix4x4 parentTransform)
    {
        if (visual is not FrameworkElement fe || !fe.IsHitTestVisible || !fe.IsEnabled)
            return null;

        var localTransform = visual.GetLocalTransform();
        var globalTransform = localTransform * parentTransform;

        if (Matrix4x4.Invert(globalTransform, out Matrix4x4 invGlobal))
        {
            Vector3 screenPt3 = new Vector3(screenPoint.X, screenPoint.Y, 0f);
            Vector3 localPt3 = Vector3.Transform(screenPt3, invGlobal);
            Vector2 localPoint = new Vector2(localPt3.X, localPt3.Y);

            Rect localBounds = new Rect(Vector2.Zero, visual.Size);
            if (visual is IHitTestBoundsProvider hitTestBoundsProvider)
            {
                localBounds = hitTestBoundsProvider.GetHitTestBounds(localBounds);
            }
            if (!localBounds.Contains(localPoint))
                return null;

            // Traverse children in reverse order (topmost first)
            if (visual is ContainerVisual container)
            {
                for (int i = container.Children.Count - 1; i >= 0; i--)
                {
                    var child = container.Children[i];
                    var hit = HitTestInternal(child, screenPoint, globalTransform);
                    if (hit != null)
                        return hit;
                }
            }

            // If we are a container and have no background, do not intercept hit-test
            if (fe is Panel || fe is Border || fe is ContentPresenter)
            {
                if (!HasBackground(fe))
                    return null;
            }

            return fe;
        }

        return null;
    }

    public static Vector2 GetLocalPosition(Visual? visual, Vector2 screenPoint)
    {
        if (visual == null) return screenPoint;

        Matrix4x4 globalTransform = visual.GetGlobalCoordinateTransformMatrix();
        if (Matrix4x4.Invert(globalTransform, out Matrix4x4 invGlobal))
        {
            Vector3 screenPt3 = new Vector3(screenPoint.X, screenPoint.Y, 0f);
            Vector3 localPt3 = Vector3.Transform(screenPt3, invGlobal);
            return new Vector2(localPt3.X, localPt3.Y);
        }

        Vector2 globalOffset = Vector2.Zero;
        Visual? current = visual;
        while (current != null)
        {
            globalOffset += current.Offset;
            current = current.Parent;
        }
        return screenPoint - globalOffset;
    }

    /// <summary>
    /// Converts a point in an element's public coordinate frame to the physical
    /// local frame used by unreflected text and retained drawing commands.
    /// </summary>
    internal static Vector2 GetVisualLocalPosition(FrameworkElement element, Vector2 coordinatePoint) =>
        element.FlowDirection == FlowDirection.RightToLeft
            ? new Vector2(element.Size.X - coordinatePoint.X, coordinatePoint.Y)
            : coordinatePoint;

    /// <summary>
    /// Converts a root point directly to the physical local frame used by
    /// unreflected text and retained drawing commands.
    /// </summary>
    internal static Vector2 GetPhysicalLocalPosition(Visual? visual, Vector2 screenPoint)
    {
        if (visual == null) return screenPoint;

        Matrix4x4 globalTransform = visual.GetGlobalTransformMatrix();
        if (Matrix4x4.Invert(globalTransform, out Matrix4x4 invGlobal))
        {
            Vector3 local = Vector3.Transform(new Vector3(screenPoint.X, screenPoint.Y, 0f), invGlobal);
            return new Vector2(local.X, local.Y);
        }

        return GetLocalPosition(visual, screenPoint);
    }

    private static List<FrameworkElement> GetVisualPath(FrameworkElement? element)
    {
        var path = new List<FrameworkElement>();
        var current = element;
        while (current != null)
        {
            path.Insert(0, current);
            current = current.Parent as FrameworkElement;
        }
        return path;
    }

    private static void OnMouseMove(Vector2 screenPos) =>
        InjectPointer(CreateMouseEvent(PointerInputKind.Moved, screenPos));

    private static void OnMouseMoveLegacy(Vector2 screenPos)
    {
        _lastMousePos = screenPos;

        if (DragDropManager.IsDragging)
        {
            DragDropManager.UpdateDrag(screenPos);
            return;
        }

        if (DevToolsService.IsInspectModeActive || (IsControlPressedDynamic() && IsShiftPressedDynamic()))
        {
            var inspectHit = HitTest(screenPos);
            bool isInsideDevTools = false;
            var current = inspectHit;
            while (current != null)
            {
                if (current.Name == "DevToolsPanel")
                {
                    isInsideDevTools = true;
                    break;
                }
                current = current.Parent as FrameworkElement;
            }
            DevToolsService.HoveredElement = isInsideDevTools ? null : inspectHit;
            if (!isInsideDevTools)
            {
                return;
            }
        }

        if (_capturedElement != null)
        {
            _capturedElement.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(_capturedElement, screenPos),
                ScreenPosition = screenPos,
                IsLeftButtonPressed = Current.IsLeftButtonPressed,
                IsMiddleButtonPressed = Current.IsMiddleButtonPressed,
                IsRightButtonPressed = Current.IsRightButtonPressed
            });
            return;
        }

        var hit = HitTest(screenPos);

        if (hit != _hoveredElement)
        {
            var oldPath = GetVisualPath(_hoveredElement);
            var newPath = GetVisualPath(hit);

            int commonIndex = -1;
            for (int i = 0; i < Math.Min(oldPath.Count, newPath.Count); i++)
            {
                if (oldPath[i] == newPath[i])
                {
                    commonIndex = i;
                }
                else
                {
                    break;
                }
            }

            // PointerExited events: leaf to common ancestor exclusive
            for (int i = oldPath.Count - 1; i > commonIndex; i--)
            {
                oldPath[i].OnPointerExited(new PointerRoutedEventArgs
                {
                    Position = GetLocalPosition(oldPath[i], screenPos),
                    ScreenPosition = screenPos,
                    IsLeftButtonPressed = Current.IsLeftButtonPressed,
                    IsMiddleButtonPressed = Current.IsMiddleButtonPressed,
                    IsRightButtonPressed = Current.IsRightButtonPressed
                });
            }

            // PointerEntered events: common ancestor exclusive to leaf
            for (int i = commonIndex + 1; i < newPath.Count; i++)
            {
                newPath[i].OnPointerEntered(new PointerRoutedEventArgs
                {
                    Position = GetLocalPosition(newPath[i], screenPos),
                    ScreenPosition = screenPos,
                    IsLeftButtonPressed = Current.IsLeftButtonPressed,
                    IsMiddleButtonPressed = Current.IsMiddleButtonPressed,
                    IsRightButtonPressed = Current.IsRightButtonPressed
                });
            }

            _hoveredElement = hit;
            ResetHoverTimer(hit);
        }

        if (_hoveredElement != null)
        {
            _hoveredElement.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(_hoveredElement, screenPos),
                ScreenPosition = screenPos,
                IsLeftButtonPressed = Current.IsLeftButtonPressed,
                IsMiddleButtonPressed = Current.IsMiddleButtonPressed,
                IsRightButtonPressed = Current.IsRightButtonPressed
            });
        }
    }

    private static void OnMouseDown(MouseButton button)
    {
        IsKeyboardFocusActive = false;
        _hoverCancellation?.Cancel();
        _hoverCancellation = null;
        DismissToolTip();

        if (button != MouseButton.Left && button != MouseButton.Middle && button != MouseButton.Right) return;

        if (button == MouseButton.Left) Current.IsLeftButtonPressed = true;
        if (button == MouseButton.Middle) Current.IsMiddleButtonPressed = true;
        if (button == MouseButton.Right) Current.IsRightButtonPressed = true;

        var hit = HitTest(_lastMousePos);
        ProGpuWinUiDiagnostics.WriteLine($"[InputSystem] MouseDown at {_lastMousePos}. Hit: {hit?.GetType().Name} (Name: {hit?.Name}, Size: {hit?.Size}, Offset: {hit?.Offset}, IsFocused: {(hit is Control ctrl ? ctrl.IsFocused.ToString() : "N/A")})");
        if (hit != null && ProGpuWinUiDiagnostics.IsEnabled)
        {
            var path = new List<string>();
            var current = hit;
            while (current != null)
            {
                path.Add($"{current.GetType().Name} (Name: {current.Name}, Size: {current.Size}, Offset: {current.Offset})");
                current = current.Parent as FrameworkElement;
            }
            ProGpuWinUiDiagnostics.WriteLine($"[InputSystem] Hit Path: {string.Join(" -> ", path)}");
        }

        if (DevToolsService.IsInspectModeActive || (IsControlPressedDynamic() && IsShiftPressedDynamic()))
        {
            bool isInsideDevTools = false;
            var current = hit;
            while (current != null)
            {
                if (current.Name == "DevToolsPanel")
                {
                    isInsideDevTools = true;
                    break;
                }
                current = current.Parent as FrameworkElement;
            }

            if (!isInsideDevTools)
            {
                if (hit != null)
                {
                    if (!DevToolsService.IsDevToolsActive)
                    {
                        DevToolsService.IsDevToolsActive = true;
                    }
                    DevToolsService.InspectedElement = hit;
                    DevToolsService.IsInspectModeActive = false;
                }
                return;
            }
        }

        // Click-outside auto-dismissal for popups
        if (PopupService.ActivePopups.Count > 0)
        {
            var topmostPopup = PopupService.ActivePopups[^1];
            bool isInsidePopup = false;
            var current = hit;
            while (current != null)
            {
                if (current == topmostPopup)
                {
                    isInsidePopup = true;
                    break;
                }
                current = current.Parent as FrameworkElement;
            }

            if (!isInsidePopup && topmostPopup is not ContentDialog)
            {
                var owner = PopupService.GetOwner(topmostPopup);
                PopupService.HidePopup(topmostPopup);
                // Re-hit-test since the popup has been closed
                var newHit = HitTest(_lastMousePos);
                
                if (newHit != null && owner != null)
                {
                    var ancestor = newHit;
                    bool hitOwner = false;
                    while (ancestor != null)
                    {
                        if (ancestor == owner)
                        {
                            hitOwner = true;
                            break;
                        }
                        ancestor = ancestor.Parent as FrameworkElement;
                    }
                    
                    if (hitOwner)
                    {
                        SetFocus(newHit);
                        return;
                    }
                }
                
                hit = newHit;
            }
        }

        if (hit != null)
        {
            hit.OnPointerPressed(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(hit, _lastMousePos),
                ScreenPosition = _lastMousePos,
                IsLeftButtonPressed = Current.IsLeftButtonPressed,
                IsMiddleButtonPressed = Current.IsMiddleButtonPressed,
                IsRightButtonPressed = Current.IsRightButtonPressed
            });
        }
        else
        {
            // Clicked outside any element: clear focus
            SetFocus(null);
        }
    }

    private static void OnMouseUp(MouseButton button)
    {
        if (button != MouseButton.Left && button != MouseButton.Middle && button != MouseButton.Right) return;

        if (button == MouseButton.Left) Current.IsLeftButtonPressed = false;
        if (button == MouseButton.Middle) Current.IsMiddleButtonPressed = false;
        if (button == MouseButton.Right) Current.IsRightButtonPressed = false;

        if (DragDropManager.IsDragging && button == MouseButton.Left)
        {
            DragDropManager.CompleteDrop(_lastMousePos);
            return;
        }

        if (_capturedElement != null)
        {
            _capturedElement.OnPointerReleased(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(_capturedElement, _lastMousePos),
                ScreenPosition = _lastMousePos,
                IsLeftButtonPressed = Current.IsLeftButtonPressed,
                IsMiddleButtonPressed = Current.IsMiddleButtonPressed,
                IsRightButtonPressed = Current.IsRightButtonPressed
            });
            ReleasePointerCapture();
            return;
        }

        var hit = HitTest(_lastMousePos);
        if (hit != null)
        {
            hit.OnPointerReleased(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(hit, _lastMousePos),
                ScreenPosition = _lastMousePos,
                IsLeftButtonPressed = Current.IsLeftButtonPressed,
                IsMiddleButtonPressed = Current.IsMiddleButtonPressed,
                IsRightButtonPressed = Current.IsRightButtonPressed
            });
        }
    }

    private static void OnMouseScroll(Vector2 scroll)
    {
        _hoverCancellation?.Cancel();
        _hoverCancellation = null;
        DismissToolTip();

        var hit = HitTest(_lastMousePos);
        if (hit != null)
        {
            hit.OnPointerWheelChanged(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(hit, _lastMousePos),
                ScreenPosition = _lastMousePos,
                WheelDelta = scroll.Y
            });
        }
    }

    public static bool TryInvokeKeyboardAccelerator(Key key, VirtualKeyModifiers modifiers)
    {
        var current = _focusedElement;
        bool visitedRoot = false;
        while (current != null)
        {
            if (current == _root)
            {
                visitedRoot = true;
            }
            foreach (var accelerator in current.KeyboardAccelerators)
            {
                if (accelerator.Key == key && accelerator.Modifiers == modifiers)
                {
                    if (accelerator.Invoke(current))
                    {
                        return true;
                    }
                }
            }
            current = current.Parent as FrameworkElement;
        }

        // fallback to root itself if we haven't checked it
        if (!visitedRoot && _root != null)
        {
            foreach (var accelerator in _root.KeyboardAccelerators)
            {
                if (accelerator.Key == key && accelerator.Modifiers == modifiers)
                {
                    if (accelerator.Invoke(_root))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void OnKeyDown(Key key)
    {
        IsKeyboardFocusActive = true;
        _hoverCancellation?.Cancel();
        _hoverCancellation = null;
        DismissToolTip();

        if (key == Key.F12 || (key == Key.D && IsControlPressedDynamic() && IsShiftPressedDynamic()))
        {
            DevToolsService.ToggleDevTools();
            return;
        }

        if (key == Key.ShiftLeft || key == Key.ShiftRight)
        {
            _isShiftPressed = true;
        }
        if (key == Key.ControlLeft || key == Key.ControlRight)
        {
            _isControlPressed = true;
        }
        if (key == Key.AltLeft || key == Key.AltRight)
        {
            _isAltPressed = true;
        }

        // Assemble active VirtualKeyModifiers
        VirtualKeyModifiers modifiers = VirtualKeyModifiers.None;
        if (IsShiftPressedDynamic()) modifiers |= VirtualKeyModifiers.Shift;
        if (IsControlPressedDynamic()) modifiers |= VirtualKeyModifiers.Control;
        if (IsAltPressedDynamic()) modifiers |= VirtualKeyModifiers.Menu;

        // Try keyboard accelerators
        if (TryInvokeKeyboardAccelerator(key, modifiers))
        {
            return;
        }

        if (key == Key.Tab)
        {
            if (_focusedElement != null)
            {
                var tabArgs = new KeyRoutedEventArgs { Key = key };
                _focusedElement.OnKeyDown(tabArgs);
                if (tabArgs.Handled) return;
            }
            CycleFocus(_isShiftPressed);
            return;
        }

        bool handled = false;
        if (_focusedElement != null)
        {
            var e = new KeyRoutedEventArgs { Key = key };
            _focusedElement.OnKeyDown(e);
            handled = e.Handled;
        }

        if (!handled)
        {
            if (key == Key.Up)
            {
                FocusManager.TryMoveFocus(FocusNavigationDirection.Up);
            }
            else if (key == Key.Down)
            {
                FocusManager.TryMoveFocus(FocusNavigationDirection.Down);
            }
            else if (key == Key.Left)
            {
                FocusManager.TryMoveFocus(FocusNavigationDirection.Left);
            }
            else if (key == Key.Right)
            {
                FocusManager.TryMoveFocus(FocusNavigationDirection.Right);
            }
        }
    }

    private static void OnKeyUp(Key key)
    {
        if (key == Key.ShiftLeft || key == Key.ShiftRight)
        {
            _isShiftPressed = false;
        }
        if (key == Key.ControlLeft || key == Key.ControlRight)
        {
            _isControlPressed = false;
        }
        if (key == Key.AltLeft || key == Key.AltRight)
        {
            _isAltPressed = false;
        }

        if (key == Key.ShiftLeft || key == Key.ShiftRight || key == Key.ControlLeft || key == Key.ControlRight)
        {
            if (!DevToolsService.IsInspectModeActive)
            {
                DevToolsService.HoveredElement = null;
            }
        }

        if (_focusedElement != null)
        {
            _focusedElement.OnKeyUp(new KeyRoutedEventArgs { Key = key });
        }
    }

    private static void OnKeyChar(char c)
    {
        if (_focusedElement != null)
        {
            _focusedElement.OnCharacterReceived(new CharacterReceivedRoutedEventArgs { Character = c });
        }
    }

    private static void OnFocusLost()
    {
        foreach (var contact in Current.PointerContacts.Values.ToArray())
        {
            InjectPointer(contact.LastEvent with
            {
                Kind = PointerInputKind.Canceled,
                IsInContact = false,
                IsLeftButtonPressed = false,
                IsMiddleButtonPressed = false,
                IsRightButtonPressed = false,
                Timestamp = GetTimestamp()
            });
        }
        if (Current.IsLeftButtonPressed) OnMouseUp(MouseButton.Left);
        if (Current.IsMiddleButtonPressed) OnMouseUp(MouseButton.Middle);
        if (Current.IsRightButtonPressed) OnMouseUp(MouseButton.Right);
        if (_isShiftPressed) OnKeyUp(Key.ShiftLeft);
        if (_isControlPressed) OnKeyUp(Key.ControlLeft);
        if (_isAltPressed) OnKeyUp(Key.AltLeft);

        if (_hoveredElement != null)
        {
            _hoveredElement.OnPointerExited(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(_hoveredElement, _lastMousePos),
                ScreenPosition = _lastMousePos
            });
            _hoveredElement = null;
        }

        ReleasePointerCapture();
        IsKeyboardFocusActive = false;
        _hoverCancellation?.Cancel();
        _hoverCancellation = null;
        DismissToolTip();
    }

    public static void CycleFocus(bool reverse)
    {
        IsKeyboardFocusActive = true;
        if (_root == null) return;

        var list = new List<FrameworkElement>();
        GatherFocusableElements(_root, list);

        if (list.Count == 0) return;

        int index = list.IndexOf(_focusedElement!);
        int nextIndex;

        if (reverse)
        {
            if (index <= 0)
            {
                nextIndex = list.Count - 1;
            }
            else
            {
                nextIndex = index - 1;
            }
        }
        else
        {
            if (index < 0 || index >= list.Count - 1)
            {
                nextIndex = 0;
            }
            else
            {
                nextIndex = index + 1;
            }
        }

        SetFocus(list[nextIndex]);
    }

    private static void GatherFocusableElements(Visual visual, List<FrameworkElement> list)
    {
        if (visual is not FrameworkElement fe || !fe.IsEnabled)
            return;

        if (fe is Control ctrl && ctrl.IsTabStop)
        {
            list.Add(fe);
        }

        if (visual is ContainerVisual container)
        {
            if (visual is SplitView splitView)
            {
                if (splitView.Pane != null)
                {
                    GatherFocusableElements(splitView.Pane, list);
                }
                if (splitView.Content != null)
                {
                    GatherFocusableElements(splitView.Content, list);
                }
                return;
            }

            foreach (var child in container.Children)
            {
                GatherFocusableElements(child, list);
            }
        }
    }

    private static void ResetHoverTimer(FrameworkElement? element)
    {
        _hoverCancellation?.Cancel();
        _hoverCancellation = null;
        
        _hoveredElementForTimer = element;

        if (element == null || element.ToolTip == null)
        {
            DismissToolTip();
            return;
        }

        if (ActiveToolTip != null && _hoveredElementForTimer == element)
        {
            return;
        }

        DismissToolTip();

        var cts = new System.Threading.CancellationTokenSource();
        _hoverCancellation = cts;
        
        System.Threading.Tasks.Task.Delay(500, cts.Token).ContinueWith(t =>
        {
            if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && !cts.IsCancellationRequested)
            {
                ShowToolTip(element);
            }
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    private static void ShowToolTip(FrameworkElement element)
    {
        var rawToolTip = element.ToolTip;
        if (rawToolTip == null) return;

        ToolTip tooltip;
        if (rawToolTip is ToolTip tooltipInstance)
        {
            tooltip = tooltipInstance;
        }
        else
        {
            tooltip = new ToolTip { Content = rawToolTip };
        }

        ActiveToolTip = tooltip;
    }

    public static void DismissToolTip()
    {
        if (ActiveToolTip != null)
        {
            ActiveToolTip = null;
        }
    }
}
