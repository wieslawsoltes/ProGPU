using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.HotReload;

/// <summary>
/// Framework-level .NET Hot Reload coordinator shared by Silk desktop windows and
/// externally hosted windows such as the browser canvas host.
/// </summary>
public static class HotReloadManager
{
    private static readonly object Gate = new();
    private static readonly HashSet<Type> PendingTypes = [];
    private static readonly List<RegisteredRoot> RegisteredRoots = [];
    private static readonly Dictionary<Type, FactoryRegistration> Factories = [];
    private static readonly List<UpdateHandlerRegistration> UpdateHandlers = [];
    private static bool _allTypesPending;
    private static bool _cacheClearPending;
    private static bool _updateQueued;
    private static long _generation;
    private static string _hostName = "Uninitialized";
    private static UpdateCounters? _activeCounters;

    public static event Action<HotReloadContext>? UpdateStarted;

    public static event Action<HotReloadResult>? UpdateCompleted;

    public static event Action<HotReloadDiagnostic>? Diagnostic;

    public static bool IsEnabled { get; set; } = ResolveDefaultEnabled();

    public static bool IsRuntimeSupported => MetadataUpdater.IsSupported;

    public static string HostName => _hostName;

    public static HotReloadResult LastResult { get; private set; } = HotReloadResult.Empty;

    internal static void Initialize(string hostName)
    {
        _hostName = string.IsNullOrWhiteSpace(hostName) ? "Unknown" : hostName;
        Report(new HotReloadDiagnostic(
            HotReloadDiagnosticSeverity.Information,
            $"Hot reload initialized for {_hostName}; runtime metadata updates are {(IsRuntimeSupported ? "available" : "unavailable")}."));
    }

    /// <summary>
    /// Standard metadata update cache callback. The runtime invokes all cache callbacks
    /// before invoking any application update callbacks.
    /// </summary>
    public static void ClearCache(Type[]? updatedTypes)
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (Gate)
        {
            _cacheClearPending = true;
            _allTypesPending |= updatedTypes == null;
            AddPendingTypes(updatedTypes);
        }
    }

    /// <summary>
    /// Standard metadata update UI callback used by dotnet watch and IDE Hot Reload.
    /// </summary>
    public static void UpdateApplication(Type[]? updatedTypes)
    {
        if (!IsEnabled)
        {
            return;
        }

        QueueUpdate(updatedTypes, clearCaches: false, allTypes: updatedTypes == null);
    }

    /// <summary>
    /// Queues the same framework refresh used by a runtime metadata update. This is
    /// useful for tooling, tests, and explicit reconciliation after an external delta.
    /// </summary>
    public static void RequestUpdate(params Type[] updatedTypes)
    {
        ArgumentNullException.ThrowIfNull(updatedTypes);
        QueueUpdate(updatedTypes, clearCaches: true, allTypes: updatedTypes.Length == 0);
    }

    /// <summary>
    /// Registers a strongly typed replacement factory. It is preferred for controls
    /// with constructor arguments and is safe for trimmed applications because no
    /// runtime activation is needed.
    /// </summary>
    public static IDisposable RegisterFactory<TElement>(Func<TElement> factory)
        where TElement : FrameworkElement
    {
        ArgumentNullException.ThrowIfNull(factory);
        var type = HotReloadTypeMappings.Normalize(typeof(TElement));
        var registration = new FactoryRegistration(type, () => factory());
        lock (Gate)
        {
            Factories[type] = registration;
        }

        return new Registration(() =>
        {
            lock (Gate)
            {
                if (Factories.TryGetValue(type, out var current) && ReferenceEquals(current, registration))
                {
                    Factories.Remove(type);
                }
            }
        });
    }

    /// <summary>
    /// Adds a non-window visual root. Active Window content and popups are discovered
    /// automatically; embedded hosts can use this registration for their own roots.
    /// </summary>
    public static IDisposable RegisterRoot(FrameworkElement root)
    {
        return RegisterRootCore(root, replaceRoot: null);
    }

    /// <summary>
    /// Adds a replaceable non-window visual root. Use this overload when the registered
    /// root itself belongs to an embedded host and may need to be recreated after a
    /// metadata update.
    /// </summary>
    public static IDisposable RegisterRoot(
        FrameworkElement root,
        Action<FrameworkElement> replaceRoot)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(replaceRoot);
        return RegisterRootCore(root, replaceRoot);
    }

    private static IDisposable RegisterRootCore(
        FrameworkElement root,
        Action<FrameworkElement>? replaceRoot)
    {
        ArgumentNullException.ThrowIfNull(root);
        var registration = new RegisteredRoot(
            new WeakReference<FrameworkElement>(root),
            replaceRoot);
        lock (Gate)
        {
            RegisteredRoots.Add(registration);
        }

        return new Registration(() =>
        {
            lock (Gate)
            {
                RegisteredRoots.Remove(registration);
            }
        });
    }

    /// <summary>
    /// Registers an application-level callback that runs on the UI thread before the
    /// framework scans active roots. Exceptions are isolated and reported.
    /// </summary>
    public static IDisposable RegisterUpdateHandler(Action<HotReloadContext> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var registration = new UpdateHandlerRegistration(handler);
        lock (Gate)
        {
            UpdateHandlers.Add(registration);
        }

        return new Registration(() =>
        {
            lock (Gate)
            {
                UpdateHandlers.Remove(registration);
            }
        });
    }

    /// <summary>
    /// Rebuilds a window's complete content tree using the same state-transfer and
    /// lifecycle pipeline as an automatic element replacement. Call this from an
    /// application update handler when a static shell builder owns the root tree.
    /// </summary>
    public static bool ReloadWindowContent(Window window, Func<FrameworkElement> contentFactory)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(contentFactory);
        if (window.Content is not { } oldContent)
        {
            window.Content = contentFactory();
            FireLoading(window.Content);
            return true;
        }

        FrameworkElement newContent;
        try
        {
            newContent = contentFactory() ?? throw new InvalidOperationException("The window content factory returned null.");
        }
        catch (Exception exception)
        {
            Report(new HotReloadDiagnostic(
                HotReloadDiagnosticSeverity.Error,
                "The window content factory failed. Existing content was retained.",
                exception));
            return false;
        }

        var counters = _activeCounters ?? new UpdateCounters();
        return TryReplaceElement(oldContent, newContent, replacement => window.Content = replacement, counters);
    }

    private static void QueueUpdate(Type[]? updatedTypes, bool clearCaches, bool allTypes)
    {
        lock (Gate)
        {
            AddPendingTypes(updatedTypes);
            _allTypesPending |= allTypes;
            _cacheClearPending |= clearCaches;
            if (_updateQueued || (PendingTypes.Count == 0 && !_allTypesPending))
            {
                return;
            }

            _updateQueued = true;
        }

        UIThread.Post(ProcessPendingUpdate);
    }

    private static void AddPendingTypes(Type[]? updatedTypes)
    {
        if (updatedTypes == null)
        {
            return;
        }

        foreach (var type in updatedTypes)
        {
            if (type != null)
            {
                PendingTypes.Add(type);
            }
        }
    }

    private static void ProcessPendingUpdate()
    {
        Type[] updatedTypes;
        bool allTypes;
        bool clearCaches;
        lock (Gate)
        {
            updatedTypes = PendingTypes.ToArray();
            PendingTypes.Clear();
            allTypes = _allTypesPending;
            _allTypesPending = false;
            clearCaches = _cacheClearPending;
            _cacheClearPending = false;
            _updateQueued = false;
        }

        if ((updatedTypes.Length == 0 && !allTypes) || !IsEnabled)
        {
            return;
        }

        ApplyUpdate(updatedTypes, clearCaches, allTypes);

        lock (Gate)
        {
            if ((PendingTypes.Count != 0 || _allTypesPending) && !_updateQueued)
            {
                _updateQueued = true;
                UIThread.Post(ProcessPendingUpdate);
            }
        }
    }

    private static void ApplyUpdate(Type[] updatedTypes, bool clearCaches, bool allTypes)
    {
        var stopwatch = Stopwatch.StartNew();
        var distinctUpdated = updatedTypes
            .Where(type => type != null)
            .Distinct()
            .ToArray();
        var originalTypes = HotReloadTypeMappings.RegisterAndGetOriginalTypes(distinctUpdated);
        var context = new HotReloadContext(
            Interlocked.Increment(ref _generation),
            distinctUpdated,
            originalTypes,
            allTypes);
        var counters = new UpdateCounters();

        Report(new HotReloadDiagnostic(
            HotReloadDiagnosticSeverity.Information,
            $"Applying hot reload generation {context.Generation} on {_hostName}: " +
            (allTypes
                ? "the runtime requested a conservative all-types refresh."
                : $"{string.Join(", ", distinctUpdated.Select(type => type.FullName ?? type.Name))}.")));

        if (clearCaches)
        {
            ThemeManager.ClearHotReloadCaches();
        }
        var refreshThemes = clearCaches && RequiresThemeRefresh(context);

        _activeCounters = counters;
        try
        {
            InvokeSafely(UpdateStarted, context, "UpdateStarted callback");
            if (Application.Current is IHotReloadable reloadableApplication &&
                context.IsTypeUpdated(Application.Current.GetType()))
            {
                try
                {
                    reloadableApplication.Reload(context);
                    counters.Reloaded++;
                }
                catch (Exception exception)
                {
                    counters.Failed++;
                    Report(new HotReloadDiagnostic(
                        HotReloadDiagnosticSeverity.Error,
                        $"Application reload failed for '{Application.Current.GetType().FullName}'. Active windows were retained.",
                        exception));
                }
            }
            InvokeRegisteredHandlers(context, counters);

            var roots = CollectRoots();
            foreach (var root in roots)
            {
                if (refreshThemes)
                {
                    root.Element.NotifyThemeChanged();
                }
                ProcessElement(root.Element, context, root.Replace, counters);
            }
        }
        finally
        {
            _activeCounters = null;
        }

        stopwatch.Stop();
        LastResult = new HotReloadResult(
            context.Generation,
            distinctUpdated,
            counters.Replaced,
            counters.Reloaded,
            counters.RefreshedFactories,
            counters.Invalidated,
            counters.Failed,
            stopwatch.Elapsed);

        InvokeSafely(UpdateCompleted, LastResult, "UpdateCompleted callback");
        Report(new HotReloadDiagnostic(
            counters.Failed == 0 ? HotReloadDiagnosticSeverity.Information : HotReloadDiagnosticSeverity.Warning,
            $"Hot reload generation {context.Generation} completed in {stopwatch.Elapsed.TotalMilliseconds:F1} ms: " +
            $"{counters.Replaced} replaced, {counters.Reloaded} rebuilt in place, " +
            $"{counters.RefreshedFactories} factories refreshed, {counters.Invalidated} invalidated, {counters.Failed} failed."));
    }

    private static void ProcessElement(
        FrameworkElement element,
        HotReloadContext context,
        Action<FrameworkElement>? replaceElement,
        UpdateCounters counters)
    {
        if (element is NavigationView navigationView)
        {
            RefreshNavigationFactories(navigationView, context, counters);
        }

        if (context.IsAllTypes && element is not IHotReloadable)
        {
            // A null runtime type list means "unknown", not that every framework type
            // acquired a new constructor. Rebuild explicit application factories and
            // IHotReloadable elements, then conservatively invalidate the remaining tree.
            element.InvalidateMeasure();
            element.InvalidateArrange();
            element.Invalidate();
            counters.Invalidated++;
        }
        else if (context.IsTypeUpdated(element.GetType()))
        {
            if (element is IHotReloadable reloadable)
            {
                try
                {
                    reloadable.Reload(context);
                    element.InvalidateMeasure();
                    element.InvalidateArrange();
                    element.Invalidate();
                    counters.Reloaded++;
                }
                catch (Exception exception)
                {
                    counters.Failed++;
                    Report(new HotReloadDiagnostic(
                        HotReloadDiagnosticSeverity.Error,
                        $"In-place reload failed for '{element.GetType().FullName}'. The existing element was retained.",
                        exception));
                }
            }
            else if (TryCreateReplacement(element.GetType(), out var replacement, out var activationError))
            {
                if (TryReplaceElement(element, replacement!, replaceElement, counters))
                {
                    return;
                }
            }
            else
            {
                counters.Failed++;
                Report(new HotReloadDiagnostic(
                    HotReloadDiagnosticSeverity.Warning,
                    $"Could not recreate '{element.GetType().FullName}'. The existing element was invalidated and retained.",
                    activationError));
            }
        }
        else if (context.IsTypeOrBaseUpdated(element.GetType()))
        {
            element.InvalidateMeasure();
            element.InvalidateArrange();
            element.Invalidate();
            counters.Invalidated++;
        }

        var children = element.Children.OfType<FrameworkElement>().ToArray();
        foreach (var child in children)
        {
            ProcessElement(child, context, replacement => ReplaceDirectChild(element, child, replacement), counters);
        }
    }

    private static bool TryReplaceElement(
        FrameworkElement oldElement,
        FrameworkElement newElement,
        Action<FrameworkElement>? replaceElement,
        UpdateCounters counters)
    {
        if (replaceElement == null)
        {
            counters.Failed++;
            Report(new HotReloadDiagnostic(
                HotReloadDiagnosticSeverity.Warning,
                $"'{oldElement.GetType().FullName}' is a registered root without a replacement callback. Its descendants can reload, but the root itself was retained."));
            oldElement.Invalidate();
            return false;
        }

        HotReloadStateSnapshot? state = null;
        try
        {
            CopyParentOwnedState(oldElement, newElement);
            state = HotReloadStateSnapshot.Capture(oldElement, Report);
            state.ReleaseFocusBeforeReplacement();
            replaceElement(newElement);
            FireUnloaded(oldElement);
            state.RestoreImmediate(newElement, Report);
            FireLoading(newElement);
            UIThread.Post(() => state.RestoreDeferred(newElement, Report));
            newElement.InvalidateMeasure();
            newElement.InvalidateArrange();
            newElement.Invalidate();
            counters.Replaced++;
            return true;
        }
        catch (Exception exception)
        {
            if (state != null)
            {
                state.RestoreDeferred(oldElement, Report);
            }
            counters.Failed++;
            Report(new HotReloadDiagnostic(
                HotReloadDiagnosticSeverity.Error,
                $"Could not replace '{oldElement.GetType().FullName}'. The update was isolated from the remaining visual tree.",
                exception));
            return false;
        }
    }

    private static void RefreshNavigationFactories(
        NavigationView navigationView,
        HotReloadContext context,
        UpdateCounters counters)
    {
        foreach (var item in EnumerateNavigationItems(navigationView))
        {
            var ownerType = item.PageFactoryOwnerType;
            if (ownerType == null || !context.IsTypeUpdated(ownerType))
            {
                continue;
            }

            var oldPage = item.CachedPage;
            if (oldPage == null)
            {
                item.ClearPageForHotReload();
                counters.RefreshedFactories++;
                continue;
            }

            var isActive = ReferenceEquals(navigationView.Content, oldPage) || ReferenceEquals(navigationView.SelectedItem, item);
            if (!isActive)
            {
                FireUnloaded(oldPage);
                item.ClearPageForHotReload();
                counters.RefreshedFactories++;
                continue;
            }

            HotReloadStateSnapshot? state = null;
            try
            {
                state = HotReloadStateSnapshot.Capture(oldPage, Report);
                var newPage = item.RecreatePageForHotReload();
                if (newPage == null)
                {
                    throw new InvalidOperationException($"Page factory '{ownerType.FullName}' returned null.");
                }

                CopyParentOwnedState(oldPage, newPage);
                state.ReleaseFocusBeforeReplacement();
                navigationView.Content = newPage;
                FireUnloaded(oldPage);
                state.RestoreImmediate(newPage, Report);
                FireLoading(newPage);
                UIThread.Post(() => state.RestoreDeferred(newPage, Report));
                counters.RefreshedFactories++;
            }
            catch (Exception exception)
            {
                counters.Failed++;
                item.RestorePageForHotReload(oldPage);
                navigationView.Content = oldPage;
                state?.RestoreDeferred(oldPage, Report);
                Report(new HotReloadDiagnostic(
                    HotReloadDiagnosticSeverity.Error,
                    $"Page factory refresh failed for '{ownerType.FullName}'. The previous page was restored.",
                    exception));
            }
        }
    }

    private static IEnumerable<NavigationViewItem> EnumerateNavigationItems(NavigationView navigationView)
    {
        foreach (var item in navigationView.MenuItems)
        {
            foreach (var descendant in EnumerateNavigationItems(item))
            {
                yield return descendant;
            }
        }

        if (navigationView.SettingsItem != null)
        {
            yield return navigationView.SettingsItem;
        }
    }

    private static IEnumerable<NavigationViewItem> EnumerateNavigationItems(NavigationViewItem item)
    {
        yield return item;
        foreach (var child in item.Items)
        {
            foreach (var descendant in EnumerateNavigationItems(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool ReplaceDirectChild(
        FrameworkElement parent,
        FrameworkElement oldChild,
        FrameworkElement newChild)
    {
        if (parent is IHotReloadChildReplacer custom && custom.TryReplaceHotReloadChild(oldChild, newChild))
        {
            return true;
        }

        switch (parent)
        {
            case ScrollViewer scrollViewer when ReferenceEquals(scrollViewer.Content, oldChild):
                scrollViewer.Content = newChild;
                return true;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, oldChild):
                contentControl.Content = newChild;
                return true;
            case ContentPresenter contentPresenter when ReferenceEquals(contentPresenter.Content, oldChild):
                contentPresenter.Content = newChild;
                return true;
            case Border border when ReferenceEquals(border.Child, oldChild):
                border.Child = newChild;
                return true;
            case SplitView splitView when ReferenceEquals(splitView.Content, oldChild):
                splitView.Content = newChild;
                return true;
            case SplitView splitView when ReferenceEquals(splitView.Pane, oldChild):
                splitView.Pane = newChild;
                return true;
            case NavigationView navigationView when ReferenceEquals(navigationView.Content, oldChild):
                navigationView.Content = newChild;
                return true;
        }

        var index = -1;
        for (var childIndex = 0; childIndex < parent.Children.Count; childIndex++)
        {
            if (ReferenceEquals(parent.Children[childIndex], oldChild))
            {
                index = childIndex;
                break;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException("The element is no longer a child of the expected parent.");
        }

        parent.RemoveChild(oldChild);
        parent.InsertChild(index, newChild);
        return true;
    }

    private static void CopyParentOwnedState(FrameworkElement oldElement, FrameworkElement newElement)
    {
        foreach (var pair in oldElement.GetLocalAttachedValues())
        {
            newElement.SetValue(pair.Key, pair.Value);
        }

        newElement.Offset = oldElement.Offset;
        newElement.Size = oldElement.Size;
    }

    private static bool TryCreateReplacement(
        Type currentType,
        [NotNullWhen(true)] out FrameworkElement? replacement,
        out Exception? error)
    {
        var latestType = HotReloadTypeMappings.GetLatestType(currentType);
        FactoryRegistration? registration;
        lock (Gate)
        {
            Factories.TryGetValue(HotReloadTypeMappings.Normalize(currentType), out registration);
            if (registration == null)
            {
                Factories.TryGetValue(HotReloadTypeMappings.Normalize(latestType), out registration);
            }
        }

        try
        {
            if (registration != null)
            {
                replacement = registration.Factory();
            }
            else
            {
                replacement = CreateWithRuntimeActivator(latestType);
            }

            if (replacement == null)
            {
                error = new InvalidOperationException($"Type '{latestType.FullName}' did not produce a FrameworkElement.");
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception exception)
        {
            replacement = null;
            error = exception;
            return false;
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2067",
        Justification = "Runtime activation is used only in modifiable, untrimmed development processes. Trimmed/AOT applications have MetadataUpdater.IsSupported=false and can use registered factories for explicit requests.")]
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification = "Runtime activation is unreachable for normal AOT hot reload because metadata updates are unsupported.")]
    private static FrameworkElement? CreateWithRuntimeActivator(Type type)
    {
        if (!IsRuntimeSupported)
        {
            return null;
        }

        return Activator.CreateInstance(type) as FrameworkElement;
    }

    private static List<RootTarget> CollectRoots()
    {
        var roots = new List<RootTarget>();
        var seen = new HashSet<FrameworkElement>(ReferenceEqualityComparer.Instance);

        foreach (var window in WindowManager.ActiveWindows)
        {
            if (window.Content is { } content && seen.Add(content))
            {
                roots.Add(new RootTarget(content, replacement => window.Content = replacement));
            }
        }

        for (var index = 0; index < PopupService.ActivePopups.Count; index++)
        {
            var popup = PopupService.ActivePopups[index];
            if (seen.Add(popup))
            {
                roots.Add(new RootTarget(popup, replacement => PopupService.ReplaceForHotReload(popup, replacement)));
            }
        }

        lock (Gate)
        {
            for (var index = RegisteredRoots.Count - 1; index >= 0; index--)
            {
                var registration = RegisteredRoots[index];
                if (!registration.Root.TryGetTarget(out var root))
                {
                    RegisteredRoots.RemoveAt(index);
                    continue;
                }

                if (seen.Add(root))
                {
                    roots.Add(new RootTarget(root, registration.Replacement));
                }
            }
        }

        return roots;
    }

    private static void FireLoading(FrameworkElement element)
    {
        try
        {
            element.FireLoading();
        }
        catch (Exception exception)
        {
            Report(new HotReloadDiagnostic(
                HotReloadDiagnosticSeverity.Warning,
                $"A Loading handler failed for '{element.GetType().FullName}' during hot reload.",
                exception));
        }
        var children = element.Children.OfType<FrameworkElement>().ToArray();
        foreach (var child in children)
        {
            FireLoading(child);
        }
    }

    private static void FireUnloaded(FrameworkElement element)
    {
        try
        {
            element.FireUnloaded();
        }
        catch (Exception exception)
        {
            Report(new HotReloadDiagnostic(
                HotReloadDiagnosticSeverity.Warning,
                $"An Unloaded handler failed for '{element.GetType().FullName}' during hot reload.",
                exception));
        }
        var children = element.Children.OfType<FrameworkElement>().ToArray();
        foreach (var child in children)
        {
            FireUnloaded(child);
        }
    }

    private static void InvokeRegisteredHandlers(HotReloadContext context, UpdateCounters counters)
    {
        UpdateHandlerRegistration[] handlers;
        lock (Gate)
        {
            handlers = UpdateHandlers.ToArray();
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler.Handler(context);
            }
            catch (Exception exception)
            {
                counters.Failed++;
                Report(new HotReloadDiagnostic(
                    HotReloadDiagnosticSeverity.Error,
                    "An application hot reload handler failed. Visual-tree processing will continue.",
                    exception));
            }
        }
    }

    private static bool RequiresThemeRefresh(HotReloadContext context)
    {
        if (context.IsAllTypes)
        {
            return true;
        }

        foreach (var updatedType in context.UpdatedTypes)
        {
            for (var type = updatedType; type != null; type = type.DeclaringType)
            {
                if (type == typeof(ThemeManager) ||
                    typeof(Style).IsAssignableFrom(type) ||
                    typeof(ResourceDictionary).IsAssignableFrom(type) ||
                    typeof(ThemeResource).IsAssignableFrom(type))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void InvokeSafely<T>(Action<T>? handlers, T value, string description)
    {
        if (handlers == null)
        {
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception)
            {
                Report(new HotReloadDiagnostic(
                    HotReloadDiagnosticSeverity.Error,
                    $"{description} failed and was isolated.",
                    exception));
            }
        }
    }

    private static void Report(HotReloadDiagnostic diagnostic)
    {
        if (diagnostic.Severity >= HotReloadDiagnosticSeverity.Warning || ProGpuWinUiDiagnostics.IsEnabled)
        {
            Console.WriteLine($"[HotReload:{diagnostic.Severity}] {diagnostic.Message}{(diagnostic.Exception == null ? string.Empty : $" {diagnostic.Exception.Message}")}");
        }

        if (Diagnostic == null)
        {
            return;
        }

        foreach (Action<HotReloadDiagnostic> handler in Diagnostic.GetInvocationList())
        {
            try
            {
                handler(diagnostic);
            }
            catch
            {
                // Diagnostics must never interfere with the reload pipeline.
            }
        }
    }

    private static bool ResolveDefaultEnabled()
    {
        var value = Environment.GetEnvironmentVariable("PROGPU_HOT_RELOAD");
        return !string.Equals(value, "0", StringComparison.Ordinal) &&
            !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UpdateCounters
    {
        public int Replaced;
        public int Reloaded;
        public int RefreshedFactories;
        public int Invalidated;
        public int Failed;
    }

    private sealed record RootTarget(FrameworkElement Element, Action<FrameworkElement>? Replace);

    private sealed class RegisteredRoot
    {
        private readonly Action<FrameworkElement>? _replace;

        public RegisteredRoot(
            WeakReference<FrameworkElement> root,
            Action<FrameworkElement>? replace)
        {
            Root = root;
            _replace = replace;
            Replacement = replace == null ? null : Replace;
        }

        public WeakReference<FrameworkElement> Root { get; }

        public Action<FrameworkElement>? Replacement { get; }

        private void Replace(FrameworkElement replacement)
        {
            _replace!(replacement);
            Root.SetTarget(replacement);
        }
    }

    private sealed record FactoryRegistration(Type Type, Func<FrameworkElement> Factory);

    private sealed record UpdateHandlerRegistration(Action<HotReloadContext> Handler);

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
