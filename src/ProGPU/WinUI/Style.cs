using System;
using System.Collections.Generic;

namespace ProGPU.WinUI;

public class Style
{
    public Type TargetType { get; set; }
    public List<Setter> Setters { get; } = new();

    public Style()
    {
        TargetType = typeof(FrameworkElement);
    }

    public Style(Type targetType)
    {
        TargetType = targetType;
    }
}

public class Setter
{
    public string Property { get; set; } = string.Empty;
    public object? Value { get; set; }

    public Setter()
    {
    }

    public Setter(string property, object? value)
    {
        Property = property;
        Value = value;
    }
}
