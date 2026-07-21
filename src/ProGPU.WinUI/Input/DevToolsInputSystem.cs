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

public static class DevToolsInputSystem
{
    private static FrameworkElement? _root;
    private static FrameworkElement? _hoveredElement;
    private static FrameworkElement? _focusedElement;
    private static Vector2 _lastMousePos;
    private static FrameworkElement? _capturedElement;
    private static Func<Vector2, Vector2>? _pointerPositionTransform;

    public static void Initialize(
        IInputContext input,
        FrameworkElement root,
        Func<Vector2, Vector2>? pointerPositionTransform = null)
    {
        _root = root;
        _hoveredElement = null;
        _focusedElement = null;
        _capturedElement = null;
        _pointerPositionTransform = pointerPositionTransform;

        foreach (var mouse in input.Mice)
        {
            mouse.MouseMove += (m, pos) => OnMouseMove(NormalizeInputPosition(new Vector2(pos.X, pos.Y)));
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

    private static Vector2 NormalizeInputPosition(Vector2 pointerPosition)
    {
        return _pointerPositionTransform?.Invoke(pointerPosition) ?? pointerPosition;
    }

    public static void CapturePointer(FrameworkElement? element)
    {
        _capturedElement = element;
    }

    public static void ReleasePointerCapture()
    {
        _capturedElement = null;
    }

    public static void SetFocus(FrameworkElement? element)
    {
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

    private static FrameworkElement? HitTest(Vector2 screenPoint)
    {
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

        if (visual is ContainerVisual container)
        {
            for (int i = container.Children.Count - 1; i >= 0; i--)
            {
                var child = container.Children[i];
                var hit = HitTestInternal(child, screenPoint, localOffset);
                if (hit != null) return hit;
            }
        }

        return fe;
    }

    private static Vector2 GetLocalPosition(Visual? visual, Vector2 screenPoint)
    {
        return InputSystem.GetLocalPosition(visual, screenPoint);
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

            for (int i = oldPath.Count - 1; i > commonIndex; i--)
            {
                oldPath[i].OnPointerExited(new PointerRoutedEventArgs
                {
                    Position = GetLocalPosition(oldPath[i], screenPos),
                    ScreenPosition = screenPos
                });
            }

            for (int i = commonIndex + 1; i < newPath.Count; i++)
            {
                newPath[i].OnPointerEntered(new PointerRoutedEventArgs
                {
                    Position = GetLocalPosition(newPath[i], screenPos),
                    ScreenPosition = screenPos
                });
            }

            _hoveredElement = hit;
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
        if (_capturedElement != null)
        {
            _capturedElement.OnPointerPressed(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(_capturedElement, _lastMousePos),
                ScreenPosition = _lastMousePos
            });
            return;
        }

        var hit = HitTest(_lastMousePos);
        if (hit != null)
        {
            SetFocus(hit);
            hit.OnPointerPressed(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(hit, _lastMousePos),
                ScreenPosition = _lastMousePos
            });
        }
        else
        {
            SetFocus(null);
        }
    }

    private static void OnMouseUp(MouseButton button)
    {
        if (_capturedElement != null)
        {
            _capturedElement.OnPointerReleased(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(_capturedElement, _lastMousePos),
                ScreenPosition = _lastMousePos
            });
            return;
        }

        var hit = HitTest(_lastMousePos);
        if (hit != null)
        {
            hit.OnPointerReleased(new PointerRoutedEventArgs
            {
                Position = GetLocalPosition(hit, _lastMousePos),
                ScreenPosition = _lastMousePos
            });
        }
    }

    private static void OnMouseScroll(Vector2 scroll)
    {
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


        if (_focusedElement != null)
        {
            _focusedElement.OnKeyDown(new KeyRoutedEventArgs { Key = key });
        }
    }

    private static void OnKeyUp(Key key)
    {


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
}
