namespace Microsoft.UI.Xaml;

/// <summary>
/// Platform window lifecycle used when a ProGPU application is hosted without Silk windowing,
/// such as a browser canvas. Rendering remains owned by <see cref="Window"/> and its compositor.
/// </summary>
public interface IWindowHost
{
    void Activate(Window window);
    void Close(Window window);
    void Hide(Window window);
    Task RunAsync(CancellationToken cancellationToken = default);
}

public static class WindowHostServices
{
    public static IWindowHost? Current { get; set; }
}
