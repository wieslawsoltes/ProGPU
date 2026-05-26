using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

public static class DevToolsService
{
    private static bool _isDevToolsActive;
    private static Visual? _inspectedElement;
    private static Visual? _hoveredElement;
    private static bool _isInspectModeActive;

    public static bool IsDevToolsActive
    {
        get => _isDevToolsActive;
        set
        {
            if (_isDevToolsActive != value)
            {
                _isDevToolsActive = value;
                if (!_isDevToolsActive)
                {
                    IsInspectModeActive = false;
                    _hoveredElement = null;
                    _inspectedElement = null;
                }
                StateChanged?.Invoke(null, EventArgs.Empty);
                InvalidateAllMainWindows();
            }
        }
    }

    public static Visual? InspectedElement
    {
        get => _inspectedElement;
        set
        {
            if (_inspectedElement != value)
            {
                _inspectedElement = value;
                InspectedElementChanged?.Invoke(null, EventArgs.Empty);
                InvalidateAllMainWindows();
            }
        }
    }

    public static Visual? HoveredElement
    {
        get => _hoveredElement;
        set
        {
            if (_hoveredElement != value)
            {
                _hoveredElement = value;
                InvalidateAllMainWindows();
            }
        }
    }

    public static bool IsInspectModeActive
    {
        get => _isInspectModeActive;
        set
        {
            if (_isInspectModeActive != value)
            {
                _isInspectModeActive = value;
                if (!_isInspectModeActive)
                {
                    _hoveredElement = null;
                }
                InvalidateAllMainWindows();
            }
            else if (!value)
            {
                _hoveredElement = null;
                InvalidateAllMainWindows();
            }
        }
    }

    public static void InvalidateAllMainWindows()
    {
        var windows = WindowManager.ActiveWindows;
        for (int i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (window.Content != null && !window.Title.Contains("Developer Tools"))
            {
                window.Content.Invalidate();
            }
        }
    }

    public static event EventHandler? StateChanged;
    public static event EventHandler? InspectedElementChanged;

    public static void ToggleDevTools()
    {
        IsDevToolsActive = !IsDevToolsActive;
    }
}
