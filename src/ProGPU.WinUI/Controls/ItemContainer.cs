using Microsoft.UI.Xaml.Controls.Primitives;

namespace Microsoft.UI.Xaml.Controls;

public enum ItemContainerUserSelectMode
{
    Auto = 0,
    UserCanSelect = 1,
    UserCannotSelect = 2
}

public enum ItemContainerUserInvokeMode
{
    Auto = 0,
    UserCanInvoke = 1,
    UserCannotInvoke = 2
}

/// <summary>
/// Hosts an item and exposes the WinUI selection and invocation state used by
/// controls such as <see cref="SelectorBar"/>.
/// </summary>
public class ItemContainer : ContentControl
{
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(
            nameof(IsSelected),
            typeof(bool),
            typeof(ItemContainer),
            new PropertyMetadata(false, OnIsSelectedPropertyChanged)
            {
                AffectsRender = true
            });

    public static readonly DependencyProperty CanUserSelectProperty =
        DependencyProperty.Register(
            nameof(CanUserSelect),
            typeof(ItemContainerUserSelectMode),
            typeof(ItemContainer),
            new PropertyMetadata(ItemContainerUserSelectMode.Auto));

    public static readonly DependencyProperty CanUserInvokeProperty =
        DependencyProperty.Register(
            nameof(CanUserInvoke),
            typeof(ItemContainerUserInvokeMode),
            typeof(ItemContainer),
            new PropertyMetadata(ItemContainerUserInvokeMode.Auto));

    public bool IsSelected
    {
        get => (bool)(GetValue(IsSelectedProperty) ?? false);
        set => SetValue(IsSelectedProperty, value);
    }

    public ItemContainerUserSelectMode CanUserSelect
    {
        get => (ItemContainerUserSelectMode)(
            GetValue(CanUserSelectProperty) ?? ItemContainerUserSelectMode.Auto);
        set => SetValue(CanUserSelectProperty, value);
    }

    public ItemContainerUserInvokeMode CanUserInvoke
    {
        get => (ItemContainerUserInvokeMode)(
            GetValue(CanUserInvokeProperty) ?? ItemContainerUserInvokeMode.Auto);
        set => SetValue(CanUserInvokeProperty, value);
    }

    protected virtual void OnIsSelectedChanged(bool oldValue, bool newValue)
    {
    }

    private static void OnIsSelectedPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((ItemContainer)dependencyObject).OnIsSelectedChanged(
            (bool)(args.OldValue ?? false),
            (bool)(args.NewValue ?? false));
    }
}
