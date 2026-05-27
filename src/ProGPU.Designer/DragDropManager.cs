namespace ProGPU.Designer;

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

public class DataPackage
{
    public Dictionary<string, object> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetText(string text) => Properties["Text"] = text;
    public string? GetText() => Properties.TryGetValue("Text", out var val) ? val as string : null;
}

public static class DragDropManager
{
    public static bool IsDragging { get; private set; }
    public static DataPackage? CurrentPackage { get; private set; }
    public static FrameworkElement? DragVisual { get; private set; }

    public static void StartDrag(FrameworkElement source, DataPackage dataPackage, FrameworkElement? visual = null)
    {
        IsDragging = true;
        CurrentPackage = dataPackage;
        DragVisual = visual;
        Microsoft.UI.Xaml.Input.InputSystem.CapturePointer(source);
    }

    public static void EndDrag()
    {
        IsDragging = false;
        CurrentPackage = null;
        DragVisual = null;
        Microsoft.UI.Xaml.Input.InputSystem.ReleasePointerCapture();
    }
}
