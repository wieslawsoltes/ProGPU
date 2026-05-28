using System;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.UI.Xaml;

[Flags]
public enum DragDropEffects
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
    Scroll = -2147483648, // 0x80000000
    All = Copy | Move | Link
}

[Flags]
public enum DragDropModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    LeftButton = 8,
    MiddleButton = 16,
    RightButton = 32
}

public static class StandardDataFormats
{
    public static string Text => "Text";
    public static string Bitmap => "Bitmap";
    public static string FileNames => "FileNames";
    public static string Html => "Html";
    public static string Rtf => "Rtf";
    public static string StorageItems => "StorageItems";
}

public class DataPackage
{
    private readonly Dictionary<string, object> _properties = new(StringComparer.OrdinalIgnoreCase);

    public void SetText(string value) => SetData(StandardDataFormats.Text, value);
    public string? GetText() => GetData(StandardDataFormats.Text) as string;

    public void SetData(string formatId, object value)
    {
        _properties[formatId] = value;
    }

    public object? GetData(string formatId)
    {
        return _properties.TryGetValue(formatId, out var value) ? value : null;
    }

    public bool Contains(string formatId) => _properties.ContainsKey(formatId);
}

public class DragEventArgs : RoutedEventArgs
{
    public Vector2 Position { get; set; } // Position relative to the element
    public Vector2 ScreenPosition { get; set; }
    public DataPackage Data { get; set; }
    public DragDropEffects AllowedOperations { get; set; }
    public DragDropEffects AcceptedOperation { get; set; }
    public DragDropModifiers Modifiers { get; set; }

    public DragEventArgs(Vector2 position, Vector2 screenPosition, DataPackage data, DragDropEffects allowedOperations, DragDropModifiers modifiers)
    {
        Position = position;
        ScreenPosition = screenPosition;
        Data = data;
        AllowedOperations = allowedOperations;
        AcceptedOperation = DragDropEffects.None;
        Modifiers = modifiers;
    }

    public Vector2 GetPosition(FrameworkElement? relativeTo)
    {
        if (relativeTo == null) return ScreenPosition;
        return Microsoft.UI.Xaml.Input.InputSystem.GetLocalPosition(relativeTo, ScreenPosition);
    }
}
