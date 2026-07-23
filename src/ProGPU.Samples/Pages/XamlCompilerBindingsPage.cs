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

    public StackPanel? FirstMaterializedTemplate =>
        FirstTemplateHost.ContentTemplateRoot as StackPanel;

    public StackPanel? SecondMaterializedTemplate =>
        SecondTemplateHost.ContentTemplateRoot as StackPanel;

    public TextBlock? FirstCompiledTemplateText =>
        GetTemplateText(FirstMaterializedTemplate, 0);

    public TextBlock? FirstOrdinaryTemplateText =>
        GetTemplateText(FirstMaterializedTemplate, 1);

    public TextBlock? SecondCompiledTemplateText =>
        GetTemplateText(SecondMaterializedTemplate, 0);

    public TextBlock? SecondOrdinaryTemplateText =>
        GetTemplateText(SecondMaterializedTemplate, 1);

    public string? SelfSourceTextValue => SelfSourceText.Text;

    public string? StaticSourceTextValue => StaticSourceText.Text;

    public string? ElementNameTextValue => ElementNameText.Text;

    public string? NamedElementSourceValue
    {
        get => NamedElementSource.Text;
        set => NamedElementSource.Text = value ?? string.Empty;
    }

    public string? TemplatedParentTextValue =>
        (TemplatedParentHost.GetTemplateChild(
            "TemplatedParentText") as TextBlock)?.Text;

    public string TemplatedParentCaptionValue
    {
        get => TemplatedParentHost.Caption;
        set => TemplatedParentHost.Caption = value;
    }

    public string? OrdinaryIndexerTextValue
    {
        get => OrdinaryIndexerText.Text;
        set => OrdinaryIndexerText.Text = value ?? string.Empty;
    }

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

    private static TextBlock? GetTemplateText(
        StackPanel? root,
        int index) =>
        root != null &&
        index >= 0 &&
        index < root.Children.Count
            ? root.Children[index] as TextBlock
            : null;
}

public static class XamlCompilerBindingFormatter
{
    public static string Format(string title, string suffix) =>
        title + suffix;
}

public static class XamlCompilerBindingSources
{
    public static XamlCompilerBindingItem Current { get; } =
        new("Static source item");

    public static ObservableCollection<XamlCompilerBindingItem> Items { get; } =
        new() { new XamlCompilerBindingItem("Ordinary indexed item") };
}

public sealed class XamlCompilerTemplateHost : Control
{
    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(
            nameof(Caption),
            typeof(string),
            typeof(XamlCompilerTemplateHost),
            new PropertyMetadata(string.Empty));

    public string Caption
    {
        get => GetValue(CaptionProperty) as string ?? string.Empty;
        set => SetValue(CaptionProperty, value ?? string.Empty);
    }
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
