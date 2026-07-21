using System;
using Microsoft.UI.Xaml;

namespace Microsoft.UI.Xaml.Navigation;

public enum NavigationMode
{
    New = 0,
    Back = 1,
    Forward = 2,
    Refresh = 3
}

public enum NavigationCacheMode
{
    Disabled = 0,
    Required = 1,
    Enabled = 2
}

public sealed class NavigationEventArgs : EventArgs
{
    internal NavigationEventArgs(
        object? content,
        object? parameter,
        Type sourcePageType,
        NavigationMode navigationMode)
    {
        Content = content;
        Parameter = parameter;
        SourcePageType = sourcePageType;
        NavigationMode = navigationMode;
    }

    public object? Content { get; }
    public object? Parameter { get; }
    public Type SourcePageType { get; }
    public NavigationMode NavigationMode { get; }
}

public sealed class NavigatingCancelEventArgs : EventArgs
{
    internal NavigatingCancelEventArgs(
        object? parameter,
        Type sourcePageType,
        NavigationMode navigationMode)
    {
        Parameter = parameter;
        SourcePageType = sourcePageType;
        NavigationMode = navigationMode;
    }

    public bool Cancel { get; set; }
    public NavigationMode NavigationMode { get; }
    public Type SourcePageType { get; }
    public object? Parameter { get; }
}

public sealed class NavigationFailedEventArgs : EventArgs
{
    internal NavigationFailedEventArgs(Exception exception, Type sourcePageType)
    {
        Exception = exception;
        SourcePageType = sourcePageType;
    }

    public Exception Exception { get; }
    public bool Handled { get; set; }
    public Type SourcePageType { get; }
}

public sealed class PageStackEntry : DependencyObject
{
    public PageStackEntry(Type sourcePageType, object? parameter = null)
    {
        SourcePageType = sourcePageType ?? throw new ArgumentNullException(nameof(sourcePageType));
        Parameter = parameter;
    }

    public Type SourcePageType { get; }
    public object? Parameter { get; }
}

public delegate void NavigatedEventHandler(object sender, NavigationEventArgs e);
public delegate void NavigatingCancelEventHandler(object sender, NavigatingCancelEventArgs e);
public delegate void NavigationFailedEventHandler(object sender, NavigationFailedEventArgs e);
public delegate void NavigationStoppedEventHandler(object sender, NavigationEventArgs e);
