using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Markup;

/// <summary>
/// One independently owned runtime resource associated with a materialized XAML template.
/// Framework profiles and user extensions may implement this contract to join the root's
/// transactional initialization and deterministic teardown without compiler-core coupling.
/// </summary>
public interface IXamlTemplateLifetime : IDisposable
{
    void Initialize();
}

/// <summary>Typed runtime publication seam used by generated deferred XAML factories.</summary>
public static class XamlTemplateFactory
{
    private static readonly ConditionalWeakTable<
        FrameworkElement,
        TemplateInstance> Instances = new();
    private static readonly ConditionalWeakTable<
        object,
        XamlNameScope> NameScopes = new();
    private static readonly ConditionalWeakTable<
        FrameworkElement,
        TemplateContext> TemplateContexts = new();

    public static void SetFactory(FrameworkTemplate template, Func<object?, FrameworkElement> factory)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(factory);
        template.DeferredFactory = factory;
    }

    public static FrameworkElement? Build(
        FrameworkTemplate? template,
        object? context = null)
    {
        var root = template?.DeferredFactory?.Invoke(context);
        if (root != null)
            SetTemplateContext(root, context);
        return root;
    }

    internal static void SetTemplateContext(
        FrameworkElement root,
        object? context)
    {
        ArgumentNullException.ThrowIfNull(root);
        TemplateContexts.Remove(root);
        TemplateContexts.Add(root, new TemplateContext(context));
    }

    internal static object? FindTemplateContext(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        for (FrameworkElement? current = element;
             current != null;
             current = current.Parent as FrameworkElement)
        {
            if (TemplateContexts.TryGetValue(current, out var context))
                return context.Value;
        }
        return null;
    }

    /// <summary>Starts one independent namescope for a generated XAML root.</summary>
    public static void BeginNameScope(object root)
    {
        ArgumentNullException.ThrowIfNull(root);
        NameScopes.Remove(root);
        NameScopes.Add(root, new XamlNameScope());
    }

    /// <summary>Registers one generated name, including non-visual dependency objects.</summary>
    public static void RegisterName(
        object root,
        string name,
        object value)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!NameScopes.TryGetValue(root, out var scope))
            throw new InvalidOperationException(
                "BeginNameScope must be called before registering generated XAML names.");
        scope.Register(name, value);
    }

    /// <summary>Resolves a name only within the generated root's own XAML namescope.</summary>
    public static object? FindName(object root, string name)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return NameScopes.TryGetValue(root, out var scope)
            ? scope.Find(name)
            : null;
    }

    internal static bool HasNameScope(object root) =>
        NameScopes.TryGetValue(root, out _);

    /// <summary>
    /// Commits one generated compiled-binding group to its materialized template root while
    /// preserving other lifetimes already owned by that root. Initialization occurs only after
    /// the complete root has been constructed. If activation fails, every root lifetime is
    /// detached transactionally before the exception escapes.
    /// </summary>
    public static void AttachBindings(
        FrameworkElement root,
        ICompiledBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(bindings);
        AttachLifetime(
            root,
            new CompiledBindingsLifetime(bindings));
    }

    /// <summary>
    /// Adds one lifetime to a materialized root. Multiple profile-owned lifetimes may coexist
    /// on the same root. Initialization is committed only after registration, and any failure
    /// releases every lifetime already attached to that root.
    /// </summary>
    public static void AttachLifetime(
        FrameworkElement root,
        IXamlTemplateLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(lifetime);
        if (!Instances.TryGetValue(root, out var instance))
        {
            instance = new TemplateInstance();
            Instances.Add(root, instance);
            root.Unloaded += OnRootUnloaded;
        }
        for (var index = 0; index < instance.Lifetimes.Count; index++)
        {
            if (ReferenceEquals(instance.Lifetimes[index], lifetime))
                throw new InvalidOperationException(
                    "The template lifetime is already attached to this root.");
        }
        instance.Lifetimes.Add(lifetime);
        try
        {
            lifetime.Initialize();
        }
        catch (Exception initializationException)
        {
            try
            {
                Release(root);
            }
            catch (Exception releaseException)
            {
                throw new AggregateException(
                    initializationException,
                    releaseException);
            }
            ExceptionDispatchInfo.Capture(initializationException).Throw();
            throw;
        }
    }

    /// <summary>
    /// Releases every lifetime owned by one materialized template root in reverse attachment
    /// order. Other roots created for the same data item remain active.
    /// </summary>
    public static void Release(FrameworkElement? root)
    {
        if (root == null)
            return;
        NameScopes.Remove(root);
        TemplateContexts.Remove(root);
        if (!Instances.TryGetValue(root, out var instance))
            return;
        Instances.Remove(root);
        root.Unloaded -= OnRootUnloaded;
        List<Exception>? failures = null;
        for (var index = instance.Lifetimes.Count - 1; index >= 0; index--)
        {
            try
            {
                instance.Lifetimes[index].Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= new List<Exception>()).Add(exception);
            }
        }
        instance.Lifetimes.Clear();
        if (failures?.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures?.Count > 1)
            throw new AggregateException(failures);
    }

    /// <summary>
    /// Releases every materialized template lifetime contained in a visual subtree. Generated
    /// page/control replacement uses this before detaching an old tree so nested data-template
    /// subscriptions and extension resources cannot survive hot reload.
    /// </summary>
    public static void ReleaseSubtree(FrameworkElement? root)
    {
        if (root == null)
            return;
        var pending = new Stack<Visual>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var visual = pending.Pop();
            if (visual is ContainerVisual container)
            {
                var children = container.Children;
                for (var index = children.Count - 1; index >= 0; index--)
                    pending.Push(children[index]);
            }
            if (visual is FrameworkElement element)
                Release(element);
        }
    }

    private static void OnRootUnloaded(object sender, RoutedEventArgs args)
    {
        _ = args;
        Release(sender as FrameworkElement);
    }

    private sealed class TemplateInstance
    {
        public List<IXamlTemplateLifetime> Lifetimes { get; } = new();
    }

    private sealed class TemplateContext
    {
        public TemplateContext(object? value) =>
            Value = value;

        public object? Value { get; }
    }

    private sealed class XamlNameScope
    {
        private readonly Dictionary<string, object> _values =
            new(StringComparer.Ordinal);

        public void Register(string name, object value)
        {
            if (!_values.TryAdd(name, value))
                throw new InvalidOperationException(
                    $"The XAML name '{name}' is already registered in this namescope.");
        }

        public object? Find(string name) =>
            _values.TryGetValue(name, out var value)
                ? value
                : null;
    }

    private sealed class CompiledBindingsLifetime : IXamlTemplateLifetime
    {
        private readonly ICompiledBindings _bindings;

        public CompiledBindingsLifetime(ICompiledBindings bindings) =>
            _bindings = bindings;

        public void Initialize() =>
            _bindings.Initialize();

        public void Dispose() =>
            CompiledBindingOperations.DisposeBindings(_bindings);
    }
}
