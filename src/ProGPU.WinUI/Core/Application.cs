using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Microsoft.UI.Xaml;

#pragma warning disable CS0618
public delegate void ApplicationInitializationCallback(ApplicationInitializationCallbackParams parameters);
public delegate void SuspendingEventHandler(object sender, SuspendingEventArgs e);
public delegate void EnteredBackgroundEventHandler(object sender, EnteredBackgroundEventArgs e);
public delegate void LeavingBackgroundEventHandler(object sender, LeavingBackgroundEventArgs e);
#pragma warning restore CS0618

[Obsolete("ApplicationInitializationCallbackParams is retained for WinUI API compatibility.")]
public sealed class ApplicationInitializationCallbackParams
{
    internal ApplicationInitializationCallbackParams()
    {
    }
}

public class LaunchActivatedEventArgs : EventArgs
{
    public string Arguments { get; }

    public LaunchActivatedEventArgs(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Arguments = string.Join(' ', args);
    }
}

public class Application
{
    public static Application Current { get; internal set; } = null!;

    public ResourceDictionary Resources { get; } = new();

    public event UnhandledExceptionEventHandler? UnhandledException;
    public event SuspendingEventHandler? Suspending;
    public event EventHandler<object>? Resuming;
    public event EnteredBackgroundEventHandler? EnteredBackground;
    public event LeavingBackgroundEventHandler? LeavingBackground;

    /// <summary>
    /// Invokes the framework initialization callback on the current UI thread.
    /// Platform hosts remain responsible for installing their dispatcher before calling this API.
    /// </summary>
    public static void Start(ApplicationInitializationCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
#pragma warning disable CS0618
        callback(new ApplicationInitializationCallbackParams());
#pragma warning restore CS0618
    }

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
            var eventArgs = new UnhandledExceptionEventArgs { Exception = ex };
            UnhandledException?.Invoke(this, eventArgs);
            if (!eventArgs.Handled)
            {
                Console.Error.WriteLine($"[Application] Unhandled exception during launch: {ex}");
                ExceptionDispatchInfo.Capture(ex).Throw();
            }
        }
    }

    /// <summary>
    /// Raises the WinUI entered-background event and waits for all event deferrals.
    /// This is a ProGPU platform-host extension.
    /// </summary>
    public Task NotifyHostEnteredBackgroundAsync()
    {
        var deferrals = new DeferralTracker();
        var args = new EnteredBackgroundEventArgs(deferrals);
        EnteredBackground?.Invoke(this, args);
        return deferrals.SealAndWaitAsync();
    }

    /// <summary>
    /// Raises the WinUI suspension event and waits for event deferrals until its
    /// platform-supplied deadline. This is a ProGPU platform-host extension.
    /// </summary>
    public async Task NotifyHostSuspendingAsync(DateTimeOffset? deadline = null)
    {
        DateTimeOffset effectiveDeadline = deadline ?? DateTimeOffset.UtcNow.AddSeconds(5);
        var deferrals = new DeferralTracker();
        var operation = new SuspendingOperation(effectiveDeadline, deferrals);
        Suspending?.Invoke(this, new SuspendingEventArgs(operation));
        Task completion = deferrals.SealAndWaitAsync();
        TimeSpan remaining = effectiveDeadline - DateTimeOffset.UtcNow;
        if (completion.IsCompleted)
        {
            await completion.ConfigureAwait(false);
            return;
        }

        if (remaining <= TimeSpan.Zero)
            return;

        await Task.WhenAny(completion, Task.Delay(remaining)).ConfigureAwait(false);
    }

    /// <summary>Raises the WinUI resumed event. This is a ProGPU platform-host extension.</summary>
    public void NotifyHostResuming() => Resuming?.Invoke(this, new object());

    /// <summary>
    /// Raises the WinUI leaving-background event and waits for all event deferrals.
    /// This is a ProGPU platform-host extension.
    /// </summary>
    public Task NotifyHostLeavingBackgroundAsync()
    {
        var deferrals = new DeferralTracker();
        var args = new LeavingBackgroundEventArgs(deferrals);
        LeavingBackground?.Invoke(this, args);
        return deferrals.SealAndWaitAsync();
    }
}
