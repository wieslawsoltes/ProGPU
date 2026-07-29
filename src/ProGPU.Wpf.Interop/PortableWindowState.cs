namespace ProGPU.Wpf.Interop;

public interface IPortableWindowStateSource
{
    bool TryGetPortableWindowState(out PortableWindowState state);
}

public sealed class PortableWindowState
{
    public bool HasTitle { get; set; }

    public string? Title { get; set; }

    public bool HasWidth { get; set; }

    public double Width { get; set; }

    public bool HasHeight { get; set; }

    public double Height { get; set; }

    public bool HasActualWidth { get; set; }

    public double ActualWidth { get; set; }

    public bool HasActualHeight { get; set; }

    public double ActualHeight { get; set; }

    public bool HasLeft { get; set; }

    public double Left { get; set; }

    public bool HasTop { get; set; }

    public double Top { get; set; }

    public bool HasWindowState { get; set; }

    public int WindowState { get; set; }

    public bool HasTopmost { get; set; }

    public bool Topmost { get; set; }

    public bool HasResizeMode { get; set; }

    public int ResizeMode { get; set; }

    public bool HasWindowStyle { get; set; }

    public int WindowStyle { get; set; }

    public bool HasShowActivated { get; set; }

    public bool ShowActivated { get; set; }

    public bool HasAllowsTransparency { get; set; }

    public bool AllowsTransparency { get; set; }

    public bool HasOwner { get; set; }

    public object? Owner { get; set; }
}
