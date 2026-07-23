using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.Samples;

public partial class XamlCompilerFunctionsPage : Page
{
    private XamlCompilerFunctionItem _item =
        new("Function source", " / tracked");
    private int _replacement;

    public XamlCompilerFunctionsPage() => InitializeComponent();

    public static FrameworkElement Create() => new XamlCompilerFunctionsPage();

    public XamlCompilerFunctionItem Item
    {
        get => _item;
        private set
        {
            if (ReferenceEquals(_item, value)) return;
            _item = value;
            OnPropertyChanged(nameof(Item));
        }
    }

    public XamlCompilerFunctionFormatter Formatter { get; } = new();

    public string Compose(
        string title,
        string suffix,
        int count,
        bool includeCount) =>
        title + suffix + (includeCount
            ? " × " + count.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty);

    private void ReplaceItem(object? sender, EventArgs args)
    {
        _replacement++;
        Item = new XamlCompilerFunctionItem(
            "Replacement " + _replacement.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            " / rewired");
    }

    private void StopCompiledBindings(object? sender, EventArgs args)
    {
        Bindings.StopTracking();
        LifecycleStatus.Text =
            "Tracking stopped. Edit the source, then call Bindings.Update().";
    }

    private void UpdateCompiledBindings(object? sender, EventArgs args)
    {
        Bindings.Update();
        LifecycleStatus.Text =
            "Bindings updated and OneWay/TwoWay tracking reattached.";
    }
}

public sealed class XamlCompilerFunctionItem : INotifyPropertyChanged
{
    private string _title;
    private string _suffix;

    public XamlCompilerFunctionItem(string title, string suffix)
    {
        _title = title;
        _suffix = suffix;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string Suffix
    {
        get => _suffix;
        set => Set(ref _suffix, value);
    }

    private void Set(
        ref string field,
        string value,
        [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class XamlCompilerFunctionFormatter
{
    public string Compose(string title, string suffix) =>
        "Owner path: " + title + suffix;
}

public static class XamlCompilerFunctionStatics
{
    public static string Compose(string title, string suffix) =>
        "Static: " + title + suffix;
}
