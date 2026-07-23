using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.Samples;

public partial class XamlCompilerBindingsPage : Page
{
    private int _replacementNumber;

    public XamlCompilerBindingsPage()
    {
        Grid.SetRow(AttachedSource, 2);
        InitializeComponent();
    }

    public static FrameworkElement Create() => new XamlCompilerBindingsPage();

    public TextBlock? FirstMaterializedTemplate =>
        FirstTemplateHost.ContentTemplateRoot as TextBlock;

    public TextBlock? SecondMaterializedTemplate =>
        SecondTemplateHost.ContentTemplateRoot as TextBlock;

    public ObservableCollection<XamlCompilerBindingItem> Items { get; } =
        new() { new XamlCompilerBindingItem("Editable indexed item") };

    public object SelectedItem => Items[0];

    public TextBlock AttachedSource { get; } = new();

    public Dictionary<string, XamlCompilerBindingItem> Entries { get; } =
        new(StringComparer.Ordinal)
        {
            ["primary"] = new XamlCompilerBindingItem("Dictionary key: primary")
        };

    public string FormatBindingSummary(
        string title,
        string suffix,
        int repeat,
        bool includeCount) =>
        title + suffix + (includeCount
            ? " × " + repeat.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty);

    private void ReplaceFirstItem(object? sender, EventArgs args)
    {
        _replacementNumber++;
        Items[0] = new XamlCompilerBindingItem(
            "Replacement " + _replacementNumber.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        BindingStatus.Text =
            "ObservableCollection replacement propagated through the generated indexer.";
    }
}

public static class XamlCompilerBindingFormatter
{
    public static string Format(string title, string suffix) =>
        title + suffix;
}

public sealed class XamlCompilerBindingItem : INotifyPropertyChanged
{
    private string _title;

    public XamlCompilerBindingItem(string title) => _title = title;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title
    {
        get => _title;
        set
        {
            if (string.Equals(_title, value, StringComparison.Ordinal)) return;
            _title = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(Title)));
        }
    }
}
