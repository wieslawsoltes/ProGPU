using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml.Navigation;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Typed page-navigation host with reusable page instances and deterministic back/forward
/// stacks. Registered factories are reflection-free and are the supported NativeAOT path.
/// </summary>
public class Frame : ContentControl
{
    private sealed record Entry(Type SourcePageType, object? Parameter, Page Page);

    private static readonly object FactoryGate = new();
    private static readonly Dictionary<Type, Func<Page>> PageFactories = new();
    private readonly List<Entry> _backEntries = new();
    private readonly List<Entry> _forwardEntries = new();
    private readonly Dictionary<Type, Page> _pageCache = new();
    private Entry? _current;
    private int _cacheSize = 10;

    public int CacheSize
    {
        get => _cacheSize;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _cacheSize = value;
            TrimEnabledCache();
        }
    }

    public bool CanGoBack => _backEntries.Count != 0;
    public bool CanGoForward => _forwardEntries.Count != 0;
    public Type? CurrentSourcePageType => _current?.SourcePageType;
    public int BackStackDepth => _backEntries.Count;
    public bool IsNavigationStackEnabled { get; set; } = true;

    public Type? SourcePageType
    {
        get => CurrentSourcePageType;
        set
        {
            if (value is not null)
                Navigate(value);
        }
    }

    public ObservableCollection<PageStackEntry> BackStack { get; } = new();
    public ObservableCollection<PageStackEntry> ForwardStack { get; } = new();

    public event NavigatedEventHandler? Navigated;
    public event NavigatingCancelEventHandler? Navigating;
    public event NavigationFailedEventHandler? NavigationFailed;
    public event NavigationStoppedEventHandler? NavigationStopped;

    public static void RegisterPageFactory<TPage>(Func<TPage> factory)
        where TPage : Page
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (FactoryGate)
            PageFactories[typeof(TPage)] = () => factory();
    }

    public static void RegisterPageFactory<TPage>()
        where TPage : Page, new() => RegisterPageFactory(static () => new TPage());

    public bool Navigate(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type sourcePageType) =>
        Navigate(sourcePageType, null);

    public bool Navigate(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type sourcePageType,
        object? parameter)
    {
        ArgumentNullException.ThrowIfNull(sourcePageType);
        return NavigateNew(sourcePageType, parameter);
    }

    public void GoBack()
    {
        if (!CanGoBack) throw new InvalidOperationException("The frame has no back-stack entry.");
        Entry target = _backEntries[^1];
        if (!TryNavigateExisting(target, NavigationMode.Back)) return;

        _backEntries.RemoveAt(_backEntries.Count - 1);
        BackStack.RemoveAt(BackStack.Count - 1);
        if (_currentBeforeTransition is { } previous && IsNavigationStackEnabled)
        {
            _forwardEntries.Add(previous);
            ForwardStack.Add(new PageStackEntry(previous.SourcePageType, previous.Parameter));
        }
    }

    public void GoForward()
    {
        if (!CanGoForward) throw new InvalidOperationException("The frame has no forward-stack entry.");
        Entry target = _forwardEntries[^1];
        if (!TryNavigateExisting(target, NavigationMode.Forward)) return;

        _forwardEntries.RemoveAt(_forwardEntries.Count - 1);
        ForwardStack.RemoveAt(ForwardStack.Count - 1);
        if (_currentBeforeTransition is { } previous && IsNavigationStackEnabled)
        {
            _backEntries.Add(previous);
            BackStack.Add(new PageStackEntry(previous.SourcePageType, previous.Parameter));
        }
    }

    private Entry? _currentBeforeTransition;

    private bool NavigateNew(Type sourcePageType, object? parameter)
    {
        try
        {
            Page page = ResolvePage(sourcePageType);
            var target = new Entry(sourcePageType, parameter, page);
            if (!TryNavigateExisting(target, NavigationMode.New)) return false;

            if (_currentBeforeTransition is { } previous && IsNavigationStackEnabled)
            {
                _backEntries.Add(previous);
                BackStack.Add(new PageStackEntry(previous.SourcePageType, previous.Parameter));
            }
            _forwardEntries.Clear();
            ForwardStack.Clear();
            TrimEnabledCache();
            return true;
        }
        catch (Exception exception)
        {
            var args = new NavigationFailedEventArgs(exception, sourcePageType);
            NavigationFailed?.Invoke(this, args);
            if (args.Handled) return false;
            throw;
        }
    }

    private bool TryNavigateExisting(Entry target, NavigationMode mode)
    {
        var navigating = new NavigatingCancelEventArgs(target.Parameter, target.SourcePageType, mode);
        _current?.Page.RaiseNavigatingFrom(navigating);
        Navigating?.Invoke(this, navigating);
        if (navigating.Cancel)
        {
            NavigationStopped?.Invoke(
                this,
                new NavigationEventArgs(_current?.Page, target.Parameter, target.SourcePageType, mode));
            return false;
        }

        Entry? previous = _current;
        _currentBeforeTransition = previous;
        _current = target;
        target.Page.SetFrame(this);
        Content = target.Page;

        if (previous is not null)
        {
            previous.Page.RaiseNavigatedFrom(
                new NavigationEventArgs(target.Page, target.Parameter, target.SourcePageType, mode));
        }

        var navigated = new NavigationEventArgs(target.Page, target.Parameter, target.SourcePageType, mode);
        target.Page.RaiseNavigatedTo(navigated);
        Navigated?.Invoke(this, navigated);
        return true;
    }

    private static Page CreatePage(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type sourcePageType)
    {
        if (!typeof(Page).IsAssignableFrom(sourcePageType))
            throw new ArgumentException("The source page type must derive from Page.", nameof(sourcePageType));

        Func<Page>? factory;
        lock (FactoryGate)
            PageFactories.TryGetValue(sourcePageType, out factory);
        if (factory is not null)
            return factory();

        // Compatibility fallback for existing desktop code. NativeAOT applications should
        // register a typed factory so trimming and activation stay reflection-free.
        return Activator.CreateInstance(sourcePageType) as Page ??
            throw new InvalidOperationException($"Unable to construct page type '{sourcePageType.FullName}'.");
    }

    private Page ResolvePage(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type sourcePageType)
    {
        if (_pageCache.TryGetValue(sourcePageType, out Page? cached))
            return cached;

        Page page = CreatePage(sourcePageType);
        if (page.NavigationCacheMode != NavigationCacheMode.Disabled)
            _pageCache[sourcePageType] = page;
        return page;
    }

    private void TrimEnabledCache()
    {
        if (_pageCache.Count <= CacheSize) return;

        var retained = new HashSet<Page>();
        if (_current is not null) retained.Add(_current.Page);
        foreach (Entry entry in _backEntries) retained.Add(entry.Page);
        foreach (Entry entry in _forwardEntries) retained.Add(entry.Page);

        var removable = new List<Type>();
        foreach ((Type type, Page page) in _pageCache)
        {
            if (_pageCache.Count - removable.Count <= CacheSize) break;
            if (page.NavigationCacheMode == NavigationCacheMode.Enabled && !retained.Contains(page))
                removable.Add(type);
        }
        foreach (Type type in removable)
            _pageCache.Remove(type);
    }
}
