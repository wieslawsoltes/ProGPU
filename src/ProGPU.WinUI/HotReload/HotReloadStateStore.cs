using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.HotReload;

internal sealed class HotReloadStateSnapshot
{
    private readonly List<ElementState> _states = [];
    private ElementIdentity? _focusedElement;

    public static HotReloadStateSnapshot Capture(
        FrameworkElement root,
        Action<HotReloadDiagnostic> diagnostic)
    {
        var snapshot = new HotReloadStateSnapshot();
        snapshot.CaptureElement(root, string.Empty, diagnostic);

        var focused = InputSystem.FocusedElement;
        if (focused != null && IsDescendantOrSelf(root, focused))
        {
            snapshot._focusedElement = snapshot.CreateIdentity(root, focused);
        }

        return snapshot;
    }

    public void RestoreImmediate(FrameworkElement root, Action<HotReloadDiagnostic> diagnostic)
    {
        foreach (var state in _states)
        {
            var element = FindElement(root, state.Identity);
            if (element == null)
            {
                continue;
            }

            try
            {
                RestoreImmediateState(element, state);
            }
            catch (Exception exception)
            {
                diagnostic(new HotReloadDiagnostic(
                    HotReloadDiagnosticSeverity.Warning,
                    $"Could not restore immediate state for '{state.Identity.DisplayName}'.",
                    exception));
            }
        }
    }

    public void ReleaseFocusBeforeReplacement()
    {
        if (_focusedElement != null)
        {
            InputSystem.SetFocus(null);
        }
    }

    public void RestoreDeferred(FrameworkElement root, Action<HotReloadDiagnostic> diagnostic)
    {
        foreach (var state in _states)
        {
            var element = FindElement(root, state.Identity);
            if (element == null)
            {
                continue;
            }

            try
            {
                RestoreDeferredState(element, state);
            }
            catch (Exception exception)
            {
                diagnostic(new HotReloadDiagnostic(
                    HotReloadDiagnosticSeverity.Warning,
                    $"Could not restore deferred state for '{state.Identity.DisplayName}'.",
                    exception));
            }
        }

        if (_focusedElement is { } focusedIdentity && FindElement(root, focusedIdentity) is { } focused)
        {
            InputSystem.SetFocus(focused);
        }
    }

    private void CaptureElement(
        FrameworkElement element,
        string path,
        Action<HotReloadDiagnostic> diagnostic)
    {
        var state = new ElementState(new ElementIdentity(element.Name, path));

        if (element.IsPropertySetLocally(FrameworkElement.DataContextProperty))
        {
            state.Values.Set("DataContext", element.DataContext);
        }

        switch (element)
        {
            case TextBox textBox:
                state.Values.Set("Text", textBox.Text);
                state.Values.Set("CaretIndex", textBox.CaretIndex);
                state.Values.Set("SelectionStart", textBox.SelectionStart);
                state.Values.Set("SelectionLength", textBox.SelectionLength);
                break;
            case PasswordBox passwordBox:
                state.Values.Set("Password", passwordBox.Password);
                state.Values.Set("CaretIndex", passwordBox.CaretIndex);
                state.Values.Set("SelectionStart", passwordBox.SelectionStart);
                state.Values.Set("SelectionLength", passwordBox.SelectionLength);
                break;
        }

        switch (element)
        {
            case CheckBox checkBox:
                state.Values.Set("IsChecked", checkBox.IsChecked);
                break;
            case RadioButton radioButton:
                state.Values.Set("IsChecked", radioButton.IsChecked);
                break;
            case ToggleButton toggleButton:
                state.Values.Set("IsChecked", toggleButton.IsChecked);
                break;
            case ToggleSwitch toggleSwitch:
                state.Values.Set("IsOn", toggleSwitch.IsOn);
                break;
        }

        if (element is Selector selector)
        {
            state.Values.Set("SelectedIndex", selector.SelectedIndex);
        }

        if (element is Pivot pivot)
        {
            state.Values.Set("PivotSelectedIndex", pivot.SelectedIndex);
        }

        if (element is ScrollViewer scrollViewer)
        {
            state.Values.Set("HorizontalOffset", scrollViewer.HorizontalOffset);
            state.Values.Set("VerticalOffset", scrollViewer.VerticalOffset);
        }

        if (element is VirtualizingPanel virtualizingPanel)
        {
            state.Values.Set("VirtualScrollOffset", virtualizingPanel.ScrollOffset);
        }

        if (element is DataGrid dataGrid)
        {
            state.Values.Set("DataGridSelectedIndex", dataGrid.SelectedIndex);
            state.Values.Set("DataGridScrollOffset", dataGrid.ScrollOffset);
        }

        if (element is NavigationView navigationView)
        {
            state.Values.Set("IsPaneOpen", navigationView.IsPaneOpen);
            state.Values.Set("SelectedNavigationItem", navigationView.SelectedItem?.Text);
        }

        if (element is IHotReloadStateful stateful)
        {
            try
            {
                state.CustomState = stateful.CaptureHotReloadState();
                state.HasCustomState = true;
            }
            catch (Exception exception)
            {
                diagnostic(new HotReloadDiagnostic(
                    HotReloadDiagnosticSeverity.Warning,
                    $"Could not capture custom state for '{state.Identity.DisplayName}'.",
                    exception));
            }
        }

        _states.Add(state);

        var children = element.Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is FrameworkElement child)
            {
                var childPath = path.Length == 0 ? index.ToString() : $"{path}/{index}";
                CaptureElement(child, childPath, diagnostic);
            }
        }
    }

    private static void RestoreImmediateState(FrameworkElement element, ElementState state)
    {
        if (state.Values.TryGet<object>("DataContext", out var dataContext))
        {
            element.DataContext = dataContext;
        }

        if (element is TextBox textBox && state.Values.TryGet<string>("Text", out var text))
        {
            textBox.Text = text ?? string.Empty;
            if (state.Values.TryGet<int>("CaretIndex", out var caretIndex)) textBox.CaretIndex = caretIndex;
            if (state.Values.TryGet<int>("SelectionStart", out var selectionStart)) textBox.SelectionStart = selectionStart;
            if (state.Values.TryGet<int>("SelectionLength", out var selectionLength)) textBox.SelectionLength = selectionLength;
        }

        if (element is PasswordBox passwordBox && state.Values.TryGet<string>("Password", out var password))
        {
            passwordBox.Password = password ?? string.Empty;
            if (state.Values.TryGet<int>("CaretIndex", out var caretIndex)) passwordBox.CaretIndex = caretIndex;
            if (state.Values.TryGet<int>("SelectionStart", out var selectionStart)) passwordBox.SelectionStart = selectionStart;
            if (state.Values.TryGet<int>("SelectionLength", out var selectionLength)) passwordBox.SelectionLength = selectionLength;
        }

        if (state.Values.TryGet<bool>("IsChecked", out var isChecked))
        {
            switch (element)
            {
                case CheckBox checkBox:
                    checkBox.IsChecked = isChecked;
                    break;
                case RadioButton radioButton:
                    radioButton.IsChecked = isChecked;
                    break;
                case ToggleButton toggleButton:
                    toggleButton.IsChecked = isChecked;
                    break;
            }
        }

        if (element is ToggleSwitch toggleSwitch && state.Values.TryGet<bool>("IsOn", out var isOn))
        {
            toggleSwitch.IsOn = isOn;
        }

        if (element is Selector selector && state.Values.TryGet<int>("SelectedIndex", out var selectedIndex))
        {
            selector.SelectedIndex = selectedIndex;
        }

        if (element is Pivot pivot && state.Values.TryGet<int>("PivotSelectedIndex", out var pivotIndex))
        {
            pivot.SelectedIndex = pivotIndex;
        }

        if (element is DataGrid dataGrid && state.Values.TryGet<int>("DataGridSelectedIndex", out var gridIndex))
        {
            dataGrid.SelectedIndex = gridIndex;
        }

        if (element is NavigationView navigationView)
        {
            if (state.Values.TryGet<bool>("IsPaneOpen", out var isPaneOpen))
            {
                navigationView.IsPaneOpen = isPaneOpen;
            }

            if (state.Values.TryGet<string>("SelectedNavigationItem", out var itemText) && !string.IsNullOrEmpty(itemText))
            {
                SelectNavigationItem(navigationView, itemText);
            }
        }

        if (state.HasCustomState && element is IHotReloadStateful stateful)
        {
            stateful.RestoreHotReloadState(state.CustomState);
        }
    }

    private static void RestoreDeferredState(FrameworkElement element, ElementState state)
    {
        if (element is ScrollViewer scrollViewer)
        {
            if (state.Values.TryGet<float>("HorizontalOffset", out var horizontalOffset)) scrollViewer.HorizontalOffset = horizontalOffset;
            if (state.Values.TryGet<float>("VerticalOffset", out var verticalOffset)) scrollViewer.VerticalOffset = verticalOffset;
        }

        if (element is VirtualizingPanel virtualizingPanel &&
            state.Values.TryGet<float>("VirtualScrollOffset", out var virtualOffset))
        {
            virtualizingPanel.ScrollOffset = virtualOffset;
        }

        if (element is DataGrid dataGrid && state.Values.TryGet<float>("DataGridScrollOffset", out var gridOffset))
        {
            dataGrid.ScrollOffset = gridOffset;
        }
    }

    private ElementIdentity? CreateIdentity(FrameworkElement root, FrameworkElement target)
    {
        foreach (var state in _states)
        {
            var candidate = FindElement(root, state.Identity);
            if (ReferenceEquals(candidate, target))
            {
                return state.Identity;
            }
        }

        return null;
    }

    private static FrameworkElement? FindElement(FrameworkElement root, ElementIdentity identity)
    {
        if (!string.IsNullOrEmpty(identity.Name))
        {
            var named = FindByName(root, identity.Name);
            if (named != null)
            {
                return named;
            }
        }

        if (identity.Path.Length == 0)
        {
            return root;
        }

        FrameworkElement current = root;
        foreach (var component in identity.Path.Split('/'))
        {
            if (!int.TryParse(component, out var index) ||
                index < 0 ||
                index >= current.Children.Count ||
                current.Children[index] is not FrameworkElement child)
            {
                return null;
            }

            current = child;
        }

        return current;
    }

    private static FrameworkElement? FindByName(FrameworkElement element, string name)
    {
        if (string.Equals(element.Name, name, StringComparison.Ordinal))
        {
            return element;
        }

        var children = element.Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is FrameworkElement child && FindByName(child, name) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static bool IsDescendantOrSelf(FrameworkElement root, FrameworkElement element)
    {
        for (Visual? current = element; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    private static void SelectNavigationItem(NavigationView navigationView, string text)
    {
        foreach (var item in navigationView.MenuItems)
        {
            if (FindNavigationItem(item, text) is { } match)
            {
                navigationView.SelectedItem = match;
                return;
            }
        }

        if (string.Equals(navigationView.SettingsItem?.Text, text, StringComparison.Ordinal))
        {
            navigationView.SelectedItem = navigationView.SettingsItem;
        }
    }

    private static NavigationViewItem? FindNavigationItem(NavigationViewItem item, string text)
    {
        if (string.Equals(item.Text, text, StringComparison.Ordinal))
        {
            return item;
        }

        foreach (var child in item.Items)
        {
            if (FindNavigationItem(child, text) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private sealed class ElementState(ElementIdentity identity)
    {
        public ElementIdentity Identity { get; } = identity;
        public HotReloadStateBag Values { get; } = new();
        public bool HasCustomState { get; set; }
        public object? CustomState { get; set; }
    }

    private readonly record struct ElementIdentity(string Name, string Path)
    {
        public string DisplayName => Name.Length != 0 ? Name : Path.Length != 0 ? Path : "root";
    }
}
