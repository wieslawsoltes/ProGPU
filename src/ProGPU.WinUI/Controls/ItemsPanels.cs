using System;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Microsoft.UI.Xaml.Controls;

public enum PanelScrollingDirection
{
    None = 0,
    Forward = 1,
    Backward = 2
}

public enum ItemsUpdatingScrollMode
{
    KeepItemsInView = 0,
    KeepScrollOffset = 1,
    KeepLastItemInView = 2
}

/// <summary>
/// WinUI-compatible items stack panel backed by ProGPU's variable-size indexed
/// virtualization engine. Group-specific properties are retained for grouped hosts.
/// </summary>
public sealed class ItemsStackPanel : VirtualizingStackPanel
{
    public ItemsStackPanel()
    {
        base.CacheLength = 2f;
        base.Orientation = Orientation.Vertical;
    }

    public static readonly DependencyProperty GroupPaddingProperty = Register(nameof(GroupPadding), typeof(Thickness), default(Thickness));
    public static readonly DependencyProperty OrientationProperty = Register(nameof(Orientation), typeof(Orientation), Orientation.Vertical, OnOrientationChanged);
    public static readonly DependencyProperty GroupHeaderPlacementProperty = Register(nameof(GroupHeaderPlacement), typeof(GroupHeaderPlacement), GroupHeaderPlacement.Top);
    public static readonly DependencyProperty ItemsUpdatingScrollModeProperty = Register(nameof(ItemsUpdatingScrollMode), typeof(ItemsUpdatingScrollMode), ItemsUpdatingScrollMode.KeepItemsInView);
    public static readonly DependencyProperty CacheLengthProperty = Register(nameof(CacheLength), typeof(double), 4d, OnCacheLengthChanged);
    public static readonly DependencyProperty AreStickyGroupHeadersEnabledProperty = Register(nameof(AreStickyGroupHeadersEnabled), typeof(bool), true);

    public Thickness GroupPadding
    {
        get => (Thickness)(GetValue(GroupPaddingProperty) ?? default(Thickness));
        set => SetValue(GroupPaddingProperty, value);
    }

    public new Orientation Orientation
    {
        get => (Orientation)(GetValue(OrientationProperty) ?? Orientation.Vertical);
        set => SetValue(OrientationProperty, value);
    }

    public GroupHeaderPlacement GroupHeaderPlacement
    {
        get => (GroupHeaderPlacement)(GetValue(GroupHeaderPlacementProperty) ?? GroupHeaderPlacement.Top);
        set => SetValue(GroupHeaderPlacementProperty, value);
    }

    public ItemsUpdatingScrollMode ItemsUpdatingScrollMode
    {
        get => (ItemsUpdatingScrollMode)(GetValue(ItemsUpdatingScrollModeProperty) ?? ItemsUpdatingScrollMode.KeepItemsInView);
        set => SetValue(ItemsUpdatingScrollModeProperty, value);
    }

    public new double CacheLength
    {
        get => (double)(GetValue(CacheLengthProperty) ?? 4d);
        set
        {
            if (!double.IsFinite(value) || value < 0d) throw new ArgumentOutOfRangeException(nameof(value));
            SetValue(CacheLengthProperty, value);
        }
    }

    public bool AreStickyGroupHeadersEnabled
    {
        get => (bool)(GetValue(AreStickyGroupHeadersEnabledProperty) ?? true);
        set => SetValue(AreStickyGroupHeadersEnabledProperty, value);
    }

    private static void OnOrientationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((VirtualizingStackPanel)(ItemsStackPanel)sender).Orientation = (Orientation)(args.NewValue ?? Orientation.Vertical);

    private static void OnCacheLengthChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((VirtualizingStackPanel)(ItemsStackPanel)sender).CacheLength = (float)((double)(args.NewValue ?? 4d) / 2d);

    private static DependencyProperty Register(
        string name,
        Type type,
        object defaultValue,
        PropertyChangedCallback? callback = null) =>
        DependencyProperty.Register(
            name,
            type,
            typeof(ItemsStackPanel),
            new PropertyMetadata(defaultValue, callback) { AffectsMeasure = true, AffectsArrange = true });
}

/// <summary>
/// WinUI-compatible wrapping items panel backed by bounded uniform-cell virtualization.
/// Only visible and cache-range containers are materialized, for O(V + B) active storage.
/// </summary>
public sealed class ItemsWrapGrid : UniformVirtualizingGridPanel
{
    public ItemsWrapGrid()
    {
        base.Orientation = Orientation.Vertical;
        base.ItemWidth = float.NaN;
        base.ItemHeight = float.NaN;
        base.CacheLength = 2f;
    }

    public static readonly DependencyProperty GroupPaddingProperty = Register(nameof(GroupPadding), typeof(Thickness), default(Thickness));
    public static readonly DependencyProperty OrientationProperty = Register(nameof(Orientation), typeof(Orientation), Orientation.Vertical, OnOrientationChanged);
    public static readonly DependencyProperty MaximumRowsOrColumnsProperty = Register(nameof(MaximumRowsOrColumns), typeof(int), -1, OnMaximumChanged);
    public static readonly DependencyProperty ItemWidthProperty = Register(nameof(ItemWidth), typeof(double), double.NaN, OnItemWidthChanged);
    public static readonly DependencyProperty ItemHeightProperty = Register(nameof(ItemHeight), typeof(double), double.NaN, OnItemHeightChanged);
    public static readonly DependencyProperty GroupHeaderPlacementProperty = Register(nameof(GroupHeaderPlacement), typeof(GroupHeaderPlacement), GroupHeaderPlacement.Top);
    public static readonly DependencyProperty CacheLengthProperty = Register(nameof(CacheLength), typeof(double), 4d, OnCacheLengthChanged);
    public static readonly DependencyProperty AreStickyGroupHeadersEnabledProperty = Register(nameof(AreStickyGroupHeadersEnabled), typeof(bool), true);

    public Thickness GroupPadding
    {
        get => (Thickness)(GetValue(GroupPaddingProperty) ?? default(Thickness));
        set => SetValue(GroupPaddingProperty, value);
    }

    public new Orientation Orientation
    {
        get => (Orientation)(GetValue(OrientationProperty) ?? Orientation.Vertical);
        set => SetValue(OrientationProperty, value);
    }

    public new int MaximumRowsOrColumns
    {
        get => (int)(GetValue(MaximumRowsOrColumnsProperty) ?? -1);
        set
        {
            if (value == 0 || value < -1) throw new ArgumentOutOfRangeException(nameof(value));
            SetValue(MaximumRowsOrColumnsProperty, value);
        }
    }

    public new double ItemWidth
    {
        get => (double)(GetValue(ItemWidthProperty) ?? double.NaN);
        set
        {
            ValidateItemSize(value, nameof(value));
            SetValue(ItemWidthProperty, value);
        }
    }

    public new double ItemHeight
    {
        get => (double)(GetValue(ItemHeightProperty) ?? double.NaN);
        set
        {
            ValidateItemSize(value, nameof(value));
            SetValue(ItemHeightProperty, value);
        }
    }

    public GroupHeaderPlacement GroupHeaderPlacement
    {
        get => (GroupHeaderPlacement)(GetValue(GroupHeaderPlacementProperty) ?? GroupHeaderPlacement.Top);
        set => SetValue(GroupHeaderPlacementProperty, value);
    }

    public new double CacheLength
    {
        get => (double)(GetValue(CacheLengthProperty) ?? 4d);
        set
        {
            if (!double.IsFinite(value) || value < 0d) throw new ArgumentOutOfRangeException(nameof(value));
            SetValue(CacheLengthProperty, value);
        }
    }

    public bool AreStickyGroupHeadersEnabled
    {
        get => (bool)(GetValue(AreStickyGroupHeadersEnabledProperty) ?? true);
        set => SetValue(AreStickyGroupHeadersEnabledProperty, value);
    }

    private static void OnOrientationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((UniformVirtualizingGridPanel)(ItemsWrapGrid)sender).Orientation = (Orientation)(args.NewValue ?? Orientation.Vertical);

    private static void OnMaximumChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((UniformVirtualizingGridPanel)(ItemsWrapGrid)sender).MaximumRowsOrColumns = (int)(args.NewValue ?? -1);

    private static void OnItemWidthChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((UniformVirtualizingGridPanel)(ItemsWrapGrid)sender).ItemWidth = (float)(double)(args.NewValue ?? double.NaN);

    private static void OnItemHeightChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((UniformVirtualizingGridPanel)(ItemsWrapGrid)sender).ItemHeight = (float)(double)(args.NewValue ?? double.NaN);

    private static void OnCacheLengthChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((UniformVirtualizingGridPanel)(ItemsWrapGrid)sender).CacheLength = (float)((double)(args.NewValue ?? 4d) / 2d);

    private static DependencyProperty Register(
        string name,
        Type type,
        object defaultValue,
        PropertyChangedCallback? callback = null) =>
        DependencyProperty.Register(
            name,
            type,
            typeof(ItemsWrapGrid),
            new PropertyMetadata(defaultValue, callback) { AffectsMeasure = true, AffectsArrange = true });

    private static void ValidateItemSize(double value, string parameterName)
    {
        if (!double.IsNaN(value) && (!double.IsFinite(value) || value <= 0d))
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
