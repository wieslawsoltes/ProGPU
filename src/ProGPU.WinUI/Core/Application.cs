using System;

namespace Microsoft.UI.Xaml;

public class LaunchActivatedEventArgs : EventArgs
{
    public string[] Arguments { get; }

    public LaunchActivatedEventArgs(string[] args)
    {
        Arguments = args;
    }
}

public class Application
{
    public static Application Current { get; internal set; } = null!;

    public ResourceDictionary Resources { get; } = new();

    public event EventHandler<UnhandledExceptionEventArgs>? UnhandledException;

    protected virtual void OnLaunched(LaunchActivatedEventArgs args)
    {
    }

    internal void Launch(LaunchActivatedEventArgs args)
    {
        try
        {
            OnLaunched(args);
        }
        catch (Exception ex)
        {
            UnhandledException?.Invoke(this, new UnhandledExceptionEventArgs { Exception = ex });
            Console.WriteLine($"[Application] Unhandled exception during launch: {ex.Message}");
        }
    }
}
