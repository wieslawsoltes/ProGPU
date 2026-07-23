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

    public GridLength Width
    {
        get => (GridLength)(GetValue(WidthProperty) ?? GridLength.Star());
        set => SetValue(WidthProperty, value);
    }
    public double MinWidth { get; set; }
    public double MaxWidth { get; set; } = double.PositiveInfinity;
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

    public GridLength Height
    {
        get => (GridLength)(GetValue(HeightProperty) ?? GridLength.Star());
        set => SetValue(HeightProperty, value);
    }
    public double MinHeight { get; set; }
    public double MaxHeight { get; set; } = double.PositiveInfinity;
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
