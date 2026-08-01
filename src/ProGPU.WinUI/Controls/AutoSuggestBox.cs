using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Controls;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AutoSuggestionBoxTextChangeReason
{
    UserInput = 0,
    ProgrammaticChange = 1,
    SuggestionChosen = 2
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class AutoSuggestBoxTextChangedEventArgs : DependencyObject
{
    private readonly AutoSuggestBox? _owner;
    private readonly long _textVersion;

    public static DependencyProperty ReasonProperty { get; } =
        DependencyProperty.Register(
            nameof(Reason),
            typeof(AutoSuggestionBoxTextChangeReason),
            typeof(AutoSuggestBoxTextChangedEventArgs),
            new PropertyMetadata(AutoSuggestionBoxTextChangeReason.UserInput));

    public AutoSuggestBoxTextChangedEventArgs()
    {
    }

    internal AutoSuggestBoxTextChangedEventArgs(
        AutoSuggestBox owner,
        AutoSuggestionBoxTextChangeReason reason,
        long textVersion)
    {
        _owner = owner;
        _textVersion = textVersion;
        Reason = reason;
    }

    public AutoSuggestionBoxTextChangeReason Reason
    {
        get => (AutoSuggestionBoxTextChangeReason)(
            GetValue(ReasonProperty) ??
            AutoSuggestionBoxTextChangeReason.UserInput);
        set => SetValue(ReasonProperty, value);
    }

    public bool CheckCurrent() =>
        _owner is null || _owner.TextVersion == _textVersion;
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class AutoSuggestBoxSuggestionChosenEventArgs : DependencyObject
{
    private object? _selectedItem;

    public AutoSuggestBoxSuggestionChosenEventArgs()
    {
    }

    internal AutoSuggestBoxSuggestionChosenEventArgs(object selectedItem) =>
        _selectedItem = selectedItem;

    public object? SelectedItem => _selectedItem;
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class AutoSuggestBoxQuerySubmittedEventArgs : DependencyObject
{
    public AutoSuggestBoxQuerySubmittedEventArgs()
    {
        QueryText = string.Empty;
    }

    internal AutoSuggestBoxQuerySubmittedEventArgs(
        string queryText,
        object? chosenSuggestion)
    {
        QueryText = queryText;
        ChosenSuggestion = chosenSuggestion;
    }

    public string QueryText { get; }
    public object? ChosenSuggestion { get; }
}

/// <summary>
/// Text input control that exposes a live suggestion collection.
/// </summary>
[InputProperty(Name = nameof(Text))]
[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class AutoSuggestBox : ItemsControl
{
    public static DependencyProperty MaxSuggestionListHeightProperty { get; } = Register(nameof(MaxSuggestionListHeight), double.PositiveInfinity);
    public static DependencyProperty IsSuggestionListOpenProperty { get; } = Register(nameof(IsSuggestionListOpen), false);
    public static DependencyProperty TextMemberPathProperty { get; } = Register(nameof(TextMemberPath), string.Empty);
    public static DependencyProperty TextProperty { get; } = Register(nameof(Text), string.Empty, OnTextChanged);
    public static DependencyProperty UpdateTextOnSelectProperty { get; } = Register(nameof(UpdateTextOnSelect), true);
    public static DependencyProperty PlaceholderTextProperty { get; } = Register(nameof(PlaceholderText), string.Empty);
    public static DependencyProperty HeaderProperty { get; } = Register<object?>(nameof(Header), null);
    public static DependencyProperty AutoMaximizeSuggestionAreaProperty { get; } = Register(nameof(AutoMaximizeSuggestionArea), false);
    public static DependencyProperty TextBoxStyleProperty { get; } = Register<Style?>(nameof(TextBoxStyle), null);
    public static DependencyProperty QueryIconProperty { get; } = Register<IconElement?>(nameof(QueryIcon), null);
    public static DependencyProperty LightDismissOverlayModeProperty { get; } = Register(nameof(LightDismissOverlayMode), LightDismissOverlayMode.Auto);
    public static DependencyProperty DescriptionProperty { get; } = Register<object?>(nameof(Description), null);

    private AutoSuggestionBoxTextChangeReason _pendingChangeReason = AutoSuggestionBoxTextChangeReason.ProgrammaticChange;
    private long _textVersion;

    internal long TextVersion => Volatile.Read(ref _textVersion);

    public double MaxSuggestionListHeight { get => (double)(GetValue(MaxSuggestionListHeightProperty) ?? double.PositiveInfinity); set => SetValue(MaxSuggestionListHeightProperty, value); }
    public bool IsSuggestionListOpen { get => (bool)(GetValue(IsSuggestionListOpenProperty) ?? false); set => SetValue(IsSuggestionListOpenProperty, value); }
    public string TextMemberPath { get => GetValue(TextMemberPathProperty) as string ?? string.Empty; set => SetValue(TextMemberPathProperty, value ?? string.Empty); }
    public string Text { get => GetValue(TextProperty) as string ?? string.Empty; set => SetText(value, AutoSuggestionBoxTextChangeReason.ProgrammaticChange); }
    public bool UpdateTextOnSelect { get => (bool)(GetValue(UpdateTextOnSelectProperty) ?? true); set => SetValue(UpdateTextOnSelectProperty, value); }
    public string PlaceholderText { get => GetValue(PlaceholderTextProperty) as string ?? string.Empty; set => SetValue(PlaceholderTextProperty, value ?? string.Empty); }
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public bool AutoMaximizeSuggestionArea { get => (bool)(GetValue(AutoMaximizeSuggestionAreaProperty) ?? false); set => SetValue(AutoMaximizeSuggestionAreaProperty, value); }
    public Style? TextBoxStyle { get => GetValue(TextBoxStyleProperty) as Style; set => SetValue(TextBoxStyleProperty, value); }
    public IconElement? QueryIcon { get => GetValue(QueryIconProperty) as IconElement; set => SetValue(QueryIconProperty, value); }
    public LightDismissOverlayMode LightDismissOverlayMode { get => (LightDismissOverlayMode)(GetValue(LightDismissOverlayModeProperty) ?? LightDismissOverlayMode.Auto); set => SetValue(LightDismissOverlayModeProperty, value); }
    public object? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public event TypedEventHandler<AutoSuggestBox, AutoSuggestBoxSuggestionChosenEventArgs>? SuggestionChosen;
    public event TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>? TextChanged;
    public event TypedEventHandler<AutoSuggestBox, AutoSuggestBoxQuerySubmittedEventArgs>? QuerySubmitted;

    internal void SetUserText(string? value) => SetText(value, AutoSuggestionBoxTextChangeReason.UserInput);

    internal void ChooseSuggestion(object suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        SuggestionChosen?.Invoke(this, new AutoSuggestBoxSuggestionChosenEventArgs(suggestion));
        if (UpdateTextOnSelect)
            SetText(GetSuggestionText(suggestion), AutoSuggestionBoxTextChangeReason.SuggestionChosen);
    }

    internal void SubmitQuery(object? chosenSuggestion = null) =>
        QuerySubmitted?.Invoke(this, new AutoSuggestBoxQuerySubmittedEventArgs(Text, chosenSuggestion));

    private void SetText(string? value, AutoSuggestionBoxTextChangeReason reason)
    {
        _pendingChangeReason = reason;
        SetValue(TextProperty, value ?? string.Empty);
    }

    private string GetSuggestionText(object suggestion)
    {
        if (string.IsNullOrEmpty(TextMemberPath))
            return suggestion.ToString() ?? string.Empty;

        if (suggestion is IReadOnlyDictionary<string, object?> readOnly &&
            readOnly.TryGetValue(TextMemberPath, out var readOnlyValue))
            return readOnlyValue?.ToString() ?? string.Empty;

        if (suggestion is IDictionary<string, object?> mutable &&
            mutable.TryGetValue(TextMemberPath, out var mutableValue))
            return mutableValue?.ToString() ?? string.Empty;

        return suggestion.ToString() ?? string.Empty;
    }

    private static void OnTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (AutoSuggestBox)dependencyObject;
        var reason = control._pendingChangeReason;
        control._pendingChangeReason = AutoSuggestionBoxTextChangeReason.ProgrammaticChange;
        var version = Interlocked.Increment(ref control._textVersion);
        control.TextChanged?.Invoke(
            control,
            new AutoSuggestBoxTextChangedEventArgs(control, reason, version));
    }

    private static DependencyProperty Register<T>(
        string name,
        T defaultValue,
        PropertyChangedCallback? callback = null) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(AutoSuggestBox),
            new PropertyMetadata(defaultValue, callback) { AffectsMeasure = true, AffectsRender = true });
}
