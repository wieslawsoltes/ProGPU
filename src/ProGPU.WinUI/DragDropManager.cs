using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Scene;
using Microsoft.UI.Xaml.Input;

namespace Microsoft.UI.Xaml;

public static class DragDropManager
{
    public static bool IsDragging { get; set; }
    public static FrameworkElement? Source { get; set; }
    public static DataPackage Data { get; set; } = new();
    public static DragDropEffects AllowedOperations { get; set; }
    public static FrameworkElement? CurrentTarget { get; set; }
    public static FrameworkElement? DragVisual { get; set; }

    public static void StartDrag(FrameworkElement source, DataPackage data, DragDropEffects allowedOperations, FrameworkElement? dragVisual = null)
    {
        IsDragging = true;
        Source = source;
        Data = data;
        AllowedOperations = allowedOperations;
        DragVisual = dragVisual;
        CurrentTarget = null;

        // Force a layout pass and invalidation to arrange the initial state
        InputSystem.Root?.Invalidate();
    }

    public static void CancelDrag()
    {
        if (!IsDragging) return;

        if (CurrentTarget != null)
        {
            var e = new DragEventArgs(
                InputSystem.GetLocalPosition(CurrentTarget, InputSystem.LastMousePosition),
                InputSystem.LastMousePosition,
                Data,
                AllowedOperations,
                GetModifiers()
            );
            CurrentTarget.OnDragLeave(e);
        }

        ResetState();
    }

    private static void ResetState()
    {
        IsDragging = false;
        Source = null;
        Data = new DataPackage();
        AllowedOperations = DragDropEffects.None;
        DragVisual = null;
        CurrentTarget = null;

        InputSystem.Root?.Invalidate();
    }

    public static void UpdateDrag(Vector2 screenPos)
    {
        if (!IsDragging) return;

        var hitElement = InputSystem.HitTest(screenPos);
        var target = FindDropTarget(hitElement);
        var modifiers = GetModifiers();

        if (target != CurrentTarget)
        {
            if (CurrentTarget != null)
            {
                var leaveArgs = new DragEventArgs(
                    InputSystem.GetLocalPosition(CurrentTarget, screenPos),
                    screenPos,
                    Data,
                    AllowedOperations,
                    modifiers
                );
                CurrentTarget.OnDragLeave(leaveArgs);
            }

            CurrentTarget = target;

            if (CurrentTarget != null)
            {
                var enterArgs = new DragEventArgs(
                    InputSystem.GetLocalPosition(CurrentTarget, screenPos),
                    screenPos,
                    Data,
                    AllowedOperations,
                    modifiers
                );
                CurrentTarget.OnDragEnter(enterArgs);
            }
        }

        if (CurrentTarget != null)
        {
            var overArgs = new DragEventArgs(
                InputSystem.GetLocalPosition(CurrentTarget, screenPos),
                screenPos,
                Data,
                AllowedOperations,
                modifiers
            );
            CurrentTarget.OnDragOver(overArgs);
        }

        // Invalidate root to ensure the visual overlays and drag visuals follow the mouse
        InputSystem.Root?.Invalidate();
    }

    public static void CompleteDrop(Vector2 screenPos)
    {
        if (!IsDragging) return;

        var modifiers = GetModifiers();
        var hitElement = InputSystem.HitTest(screenPos);
        var target = FindDropTarget(hitElement);

        if (target != null)
        {
            var dropArgs = new DragEventArgs(
                InputSystem.GetLocalPosition(target, screenPos),
                screenPos,
                Data,
                AllowedOperations,
                modifiers
            );
            target.OnDrop(dropArgs);
        }

        ResetState();
    }

    private static FrameworkElement? FindDropTarget(FrameworkElement? element)
    {
        var current = element;
        while (current != null)
        {
            if (current.AllowDrop)
            {
                return current;
            }
            current = current.Parent as FrameworkElement;
        }
        return null;
    }

    private static DragDropModifiers GetModifiers()
    {
        var mod = DragDropModifiers.None;
        if (InputSystem.Current.IsShiftPressed) mod |= DragDropModifiers.Shift;
        if (InputSystem.Current.IsControlPressed) mod |= DragDropModifiers.Control;
        if (InputSystem.Current.IsAltPressed) mod |= DragDropModifiers.Alt;
        mod |= DragDropModifiers.LeftButton;
        return mod;
    }

    public static void RenderDragVisual(DrawingContext context, float screenWidth, float screenHeight)
    {
        if (!IsDragging || DragVisual == null) return;

        var mousePos = InputSystem.LastMousePosition;

        // Measure and arrange the DragVisual container in logical pixels
        DragVisual.Measure(new Vector2(screenWidth, screenHeight));
        DragVisual.Arrange(new Rect(mousePos, DragVisual.DesiredSize));

        // Recursively render and compile commands offset starting from Vector2.Zero because DragVisual's own Offset is already mousePos!
        RenderElementRecursively(context, DragVisual, Vector2.Zero, screenWidth, screenHeight);
    }

    private static void RenderElementRecursively(DrawingContext context, FrameworkElement element, Vector2 offset, float screenWidth, float screenHeight)
    {
        if (element == null) return;

        var elementContext = new DrawingContext();
        element.OnRender(elementContext);

        Vector2 currentOffset = offset + element.Offset;

        context.Append(elementContext, currentOffset);

        if (element is ContainerVisual container)
        {
            foreach (var child in container.Children)
            {
                if (child is FrameworkElement childFe)
                {
                    RenderElementRecursively(context, childFe, currentOffset, screenWidth, screenHeight);
                }
            }
        }
    }
}
