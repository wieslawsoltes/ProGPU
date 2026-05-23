using System;

namespace ProGPU.WinUI;

public static class DevToolsService
{
    private static bool _isDevToolsActive;
    private static FrameworkElement? _inspectedElement;
    private static FrameworkElement? _hoveredElement;
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
                    HoveredElement = null;
                }
                StateChanged?.Invoke(null, EventArgs.Empty);
            }
        }
    }

    public static FrameworkElement? InspectedElement
    {
        get => _inspectedElement;
        set
        {
            if (_inspectedElement != value)
            {
                _inspectedElement = value;
                InspectedElementChanged?.Invoke(null, EventArgs.Empty);
                InputSystem.Root?.Invalidate();
            }
        }
    }

    public static FrameworkElement? HoveredElement
    {
        get => _hoveredElement;
        set
        {
            if (_hoveredElement != value)
            {
                _hoveredElement = value;
                InputSystem.Root?.Invalidate();
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
                    HoveredElement = null;
                }
                InputSystem.Root?.Invalidate();
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
