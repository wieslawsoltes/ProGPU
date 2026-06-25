using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;
using ProGPU.Scene;
using ProGPU.Vector;

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
}

public static class InputSystem
{
    [ThreadStatic]
    private static WindowInputState? _currentState;

    public static WindowInputState Current
    {
        get => _currentState ??= new WindowInputState();
        set => _currentState = value;
    }

    // Public event injection entry points for external host containers (e.g. Avalonia)
    public static void InjectMouseMove(Vector2 screenPos) => OnMouseMove(screenPos);
    public static void InjectMouseDown(MouseButton button) => OnMouseDown(button);
    public static void InjectMouseUp(MouseButton button) => OnMouseUp(button);
    public static void InjectMouseScroll(Vector2 scroll) => OnMouseScroll(scroll);
    public static void InjectKeyDown(Key key) => OnKeyDown(key);
    public static void InjectKeyUp(Key key) => OnKeyUp(key);
    public static void InjectKeyChar(char c) => OnKeyChar(c);

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
        _capturedElement = element;
    }

    public static void ReleasePointerCapture()
    {
        _capturedElement = null;
        DevToolsInputSystem.ReleasePointerCapture();
    }

    public static void SetMouseCursor(StandardCursor cursor)
    {
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
                OnMouseDown(btn);
            };
            mouse.MouseUp += (m, btn) => {
                _currentState = state;
                OnMouseUp(btn);
            };
            mouse.Scroll += (m, scroll) => {
                _currentState = state;
                OnMouseScroll(new Vector2(scroll.X, scroll.Y));
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

    public static void SetFocus(FrameworkElement? element)
    {
        if (element != null && IsInsideDevTools(element))
        {
            DevToolsInputSystem.SetFocus(element);
            return;
        }

        if (_focusedElement == element) return;

        var oldFocus = _focusedElement;
        _focusedElement = element;

        if (oldFocus is Control oldControl)
        {
            oldControl.IsFocused = false;
        }

        if (_focusedElement is Control newControl)
        {
            newControl.IsFocused = true;
        }
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
        var prop = fe.GetType().GetProperty("Background");
        if (prop != null)
        {
            return prop.GetValue(fe) != null;
        }
        return false;
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
            if (visual.GetType().Name == "SelectionAdorner")
            {
                float zoomScale = 1.0f;
                var zoomProp = visual.GetType().GetProperty("ZoomScale");
                if (zoomProp != null)
                {
                    zoomScale = (float)(zoomProp.GetValue(visual) ?? 1.0f);
                }
                float expandY = 32f / zoomScale;
                float expandX = 12f / zoomScale;
                localBounds = new Rect(-expandX, -expandY, visual.Size.X + 2f * expandX, visual.Size.Y + expandY + expandX);
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

    private static Matrix4x4 GetGlobalTransform(Visual visual)
    {
        var local = visual.GetLocalTransform();
        if (visual.Parent == null) return local;
        return local * GetGlobalTransform(visual.Parent);
    }

    public static Vector2 GetLocalPosition(Visual? visual, Vector2 screenPoint)
    {
        if (visual == null) return screenPoint;

        Matrix4x4 globalTransform = GetGlobalTransform(visual);
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

    private static void OnMouseMove(Vector2 screenPos)
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
        Console.WriteLine($"[InputSystem] MouseDown at {_lastMousePos}. Hit: {hit?.GetType().Name} (Name: {hit?.Name}, Size: {hit?.Size}, Offset: {hit?.Offset}, IsFocused: {(hit is Control ctrl ? ctrl.IsFocused.ToString() : "N/A")})");
        if (hit != null)
        {
            var path = new List<string>();
            var current = hit;
            while (current != null)
            {
                path.Add($"{current.GetType().Name} (Name: {current.Name}, Size: {current.Size}, Offset: {current.Offset})");
                current = current.Parent as FrameworkElement;
            }
            Console.WriteLine($"[InputSystem] Hit Path: {string.Join(" -> ", path)}");
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

