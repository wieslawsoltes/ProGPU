using System.Collections.ObjectModel;

namespace Microsoft.UI.Xaml.Controls;

public sealed class ColumnDefinition : DependencyObject
{
    public static readonly DependencyProperty WidthProperty =
        DependencyProperty.Register(
            nameof(Width),
            typeof(GridLength),
            typeof(ColumnDefinition),
            new PropertyMetadata(GridLength.Star()));
    public static readonly DependencyProperty MinWidthProperty =
        DependencyProperty.Register(
            nameof(MinWidth),
            typeof(double),
            typeof(ColumnDefinition),
            new PropertyMetadata(0d));
    public static readonly DependencyProperty MaxWidthProperty =
        DependencyProperty.Register(
            nameof(MaxWidth),
            typeof(double),
            typeof(ColumnDefinition),
            new PropertyMetadata(double.PositiveInfinity));

    public GridLength Width
    {
        get => (GridLength)(GetValue(WidthProperty) ?? GridLength.Star());
        set => SetValue(WidthProperty, value);
    }
    public double MinWidth
    {
        get => (double)(GetValue(MinWidthProperty) ?? 0d);
        set => SetValue(MinWidthProperty, value);
    }
    public double MaxWidth
    {
        get => (double)(
            GetValue(MaxWidthProperty) ??
            double.PositiveInfinity);
        set => SetValue(MaxWidthProperty, value);
    }
    public double ActualWidth { get; internal set; }

    internal float Value => Width.Value;
    internal GridUnitType UnitType => Width.UnitType;

    public static implicit operator ColumnDefinition(GridLength width) => new() { Width = width };
}

public sealed class RowDefinition : DependencyObject
{
    public static readonly DependencyProperty HeightProperty =
        DependencyProperty.Register(
            nameof(Height),
            typeof(GridLength),
            typeof(RowDefinition),
            new PropertyMetadata(GridLength.Star()));
    public static readonly DependencyProperty MinHeightProperty =
        DependencyProperty.Register(
            nameof(MinHeight),
            typeof(double),
            typeof(RowDefinition),
            new PropertyMetadata(0d));
    public static readonly DependencyProperty MaxHeightProperty =
        DependencyProperty.Register(
            nameof(MaxHeight),
            typeof(double),
            typeof(RowDefinition),
            new PropertyMetadata(double.PositiveInfinity));

    public GridLength Height
    {
        get => (GridLength)(GetValue(HeightProperty) ?? GridLength.Star());
        set => SetValue(HeightProperty, value);
    }
    public double MinHeight
    {
        get => (double)(GetValue(MinHeightProperty) ?? 0d);
        set => SetValue(MinHeightProperty, value);
    }
    public double MaxHeight
    {
        get => (double)(
            GetValue(MaxHeightProperty) ??
            double.PositiveInfinity);
        set => SetValue(MaxHeightProperty, value);
    }
    public double ActualHeight { get; internal set; }

    internal float Value => Height.Value;
    internal GridUnitType UnitType => Height.UnitType;

    public static implicit operator RowDefinition(GridLength height) => new() { Height = height };
}

public sealed class ColumnDefinitionCollection : Collection<ColumnDefinition>
{
}

public sealed class RowDefinitionCollection : Collection<RowDefinition>
{
}
