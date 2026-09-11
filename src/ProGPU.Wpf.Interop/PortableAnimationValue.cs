namespace ProGPU.Wpf.Interop;

/// <summary>
/// Publishes the current value of a WPF double animation-clock resource.
/// </summary>
public interface IPortableDoubleAnimationValueSource
{
    bool TryGetPortableDoubleAnimationValue(out double value);
}

/// <summary>
/// Publishes the current value of a WPF point animation-clock resource.
/// </summary>
public interface IPortablePointAnimationValueSource
{
    bool TryGetPortablePointAnimationValue(out PortablePoint value);
}

/// <summary>
/// Publishes the current value of a WPF size animation-clock resource.
/// </summary>
public interface IPortableSizeAnimationValueSource
{
    bool TryGetPortableSizeAnimationValue(out PortableSize value);
}

/// <summary>
/// Publishes the current value of a WPF rectangle animation-clock resource.
/// </summary>
public interface IPortableRectAnimationValueSource
{
    bool TryGetPortableRectAnimationValue(out PortableRect value);
}
