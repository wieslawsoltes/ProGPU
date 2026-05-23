using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI;

public static class InputSystem
{
    private static FrameworkElement? _root;
    private static FrameworkElement? _hoveredElement;
    private static FrameworkElement? _focusedElement;
    private static Vector2 _lastMousePos;
    private static FrameworkElement? _capturedElement;
    private static bool _isShiftPressed;

    public static bool IsKeyboardFocusActive { get; set; } = false;

    private static System.Threading.CancellationTokenSource? _hoverCancellation;
    private static ToolTip? _activeToolTip;
    private static FrameworkElement? _hoveredElementForTimer;

    public static FrameworkElement? Root
    {
        get => _root;
        set => _root = value;
    }

    public static FrameworkElement? HoveredElement => _hoveredElement;
    public static FrameworkElement? FocusedElement => _focusedElement;
    public static Vector2 LastMousePosition => _lastMousePos;

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

    public static void Initialize(IInputContext input, FrameworkElement? root = null)
    {
        _root = root;

        foreach (var mouse in input.Mice)
        {
            mouse.MouseMove += (m, pos) => OnMouseMove(new Vector2(pos.X, pos.Y));
            mouse.MouseDown += (m, btn) => OnMouseDown(btn);
            mouse.MouseUp += (m, btn) => OnMouseUp(btn);
            mouse.Scroll += (m, scroll) => OnMouseScroll(new Vector2(scroll.X, scroll.Y));
        }

        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += (kb, key, code) => OnKeyDown(key);
            keyboard.KeyUp += (kb, key, code) => OnKeyUp(key);
            keyboard.KeyChar += (kb, c) => OnKeyChar(c);
        }
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
            var hit = HitTestInternal(popup, screenPoint, Vector2.Zero);
            if (hit != null) return hit;
        }

        if (_root == null) return null;
        return HitTestInternal(_root, screenPoint, Vector2.Zero);
    }

    private static FrameworkElement? HitTestInternal(Visual visual, Vector2 screenPoint, Vector2 parentOffset)
    {
        if (visual is not FrameworkElement fe || !fe.IsHitTestVisible || !fe.IsEnabled)
            return null;

        Vector2 localOffset = parentOffset + visual.Offset;
        Rect bounds = new Rect(localOffset, visual.Size);

        if (!bounds.Contains(screenPoint))
            return null;

        // Traverse children in reverse order (topmost first)
        if (visual is ContainerVisual container)
        {
            for (int i = container.Children.Count - 1; i >= 0; i--)
            {
                var child = container.Children[i];
                var hit = HitTestInternal(child, screenPoint, localOffset);
                if (hit != null)
                    return hit;
            }
        }

        return fe;
    }

    public static Vector2 GetLocalPosition(Visual? visual, Vector2 screenPoint)
    {
        if (visual == null) return screenPoint;
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

        if (DevToolsService.IsInspectModeActive)
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
                ScreenPosition = screenPos
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
                    ScreenPosition = screenPos
                });
            }

            // PointerEntered events: common ancestor exclusive to leaf
            for (int i = commonIndex + 1; i < newPath.Count; i++)
            {
                newPath[i].OnPointerEntered(new PointerRoutedEventArgs
                {
                    Position = GetLocalPosition(newPath[i], screenPos),
                    ScreenPosition = screenPos
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
                ScreenPosition = screenPos
            });
        }
    }

    private static void OnMouseDown(MouseButton button)
    {
        IsKeyboardFocusActive = false;
        _hoverCancellation?.Cancel();
        _hoverCancellation = null;
        DismissToolTip();

        if (button != MouseButton.Left) return;

        var hit = HitTest(_lastMousePos);

        if (DevToolsService.IsInspectModeActive)
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
                PopupService.HidePopup(topmostPopup);
                // Re-hit-test since the popup has been closed
                hit = HitTest(_lastMousePos);
            }
        }

        if (hit != null)
        {
            hit.OnPointerPressed(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(hit, _lastMousePos),
                ScreenPosition = _lastMousePos,
                IsLeftButtonPressed = true
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
        if (button != MouseButton.Left) return;

        if (_capturedElement != null)
        {
            _capturedElement.OnPointerReleased(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(_capturedElement, _lastMousePos),
                ScreenPosition = _lastMousePos,
                IsLeftButtonPressed = false
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
                IsLeftButtonPressed = false
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

    private static void OnKeyDown(Key key)
    {
        _hoverCancellation?.Cancel();
        _hoverCancellation = null;
        DismissToolTip();

        if (key == Key.F12)
        {
            DevToolsService.ToggleDevTools();
            return;
        }

        if (key == Key.ShiftLeft || key == Key.ShiftRight)
        {
            _isShiftPressed = true;
        }

        if (key == Key.Tab)
        {
            CycleFocus(_isShiftPressed);
            return;
        }

        if (_focusedElement != null)
        {
            _focusedElement.OnKeyDown(new KeyRoutedEventArgs { Key = key });
        }
    }

    private static void OnKeyUp(Key key)
    {
        if (key == Key.ShiftLeft || key == Key.ShiftRight)
        {
            _isShiftPressed = false;
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
