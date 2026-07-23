using Microsoft.UI.Xaml.Markup;
using System.Runtime.CompilerServices;

namespace Microsoft.UI.Xaml.Data;

public abstract class BindingBase : MarkupExtension
{
}

public enum BindingMode
{
    OneWay,
    OneTime,
    TwoWay
}

public enum UpdateSourceTrigger
{
    Default,
    PropertyChanged,
    Explicit,
    LostFocus
}

public enum RelativeSourceMode
{
    None,
    TemplatedParent,
    Self
}

/// <summary>Typed, reflection-free description of a WinUI relative binding source.</summary>
public sealed class RelativeSource : MarkupExtension
{
    public RelativeSourceMode Mode { get; set; }

    protected override object ProvideValue() => this;
}

public interface IValueConverter
{
    object? Convert(object? value, Type targetType, object? parameter, string language);
    object? ConvertBack(object? value, Type targetType, object? parameter, string language);
}

/// <summary>
/// Typed binding description retained by generated XAML. Runtime activation is owned by the
/// dependency-property binding engine; the descriptor itself contains no reflective access.
/// </summary>
public sealed class Binding : BindingBase
{
    public string? Path { get; set; }
    public BindingMode Mode { get; set; } = BindingMode.OneWay;
    public string? ElementName { get; set; }
    public RelativeSource? RelativeSource { get; set; }
    public object? Source { get; set; }
    public object? Converter { get; set; }
    public object? ConverterParameter { get; set; }
    public string? ConverterLanguage { get; set; }
    public UpdateSourceTrigger UpdateSourceTrigger { get; set; }
    public object? FallbackValue { get; set; }
    public object? TargetNullValue { get; set; }

    protected override object ProvideValue() => this;
}

/// <summary>
/// Owns binding descriptors without reflecting over CLR properties. Dependency-property paths
/// are activated through the framework's typed property registry. CLR paths use explicitly
/// registered typed accessors, allowing framework profiles and application generators to extend
/// the engine without runtime reflection.
/// </summary>
public static class BindingOperations
{
    private static readonly ConditionalWeakTable<DependencyObject, BindingStore> Stores = new();
    private static readonly ConditionalWeakTable<object, ContextBindingStore> ContextStores = new();
    private static readonly ConditionalWeakTable<
        BindingExpression,
        BindingLifetimeStore> ExpressionOwners = new();

    public static BindingExpression SetBinding(
        DependencyObject target,
        string targetPropertyName,
        Binding binding,
        object? context = null,
        object? lookupRoot = null,
        IXamlTemplateLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(targetPropertyName);
        ArgumentNullException.ThrowIfNull(binding);

        var targetProperty = DependencyProperty.Lookup(target.GetType(), targetPropertyName);
        if (targetProperty == null)
            throw new InvalidOperationException(
                $"Dependency property '{targetPropertyName}' was not registered for '{target.GetType().FullName}'.");

        return SetBinding(target, targetProperty, binding, context, lookupRoot, lifetime);
    }

    public static BindingExpression SetBinding(
        DependencyObject target,
        DependencyProperty targetProperty,
        Binding binding,
        object? context = null,
        object? lookupRoot = null,
        IXamlTemplateLifetime? lifetime = null) =>
        SetBindingCore(
            target,
            targetProperty,
            binding,
            pathSegments: null,
            context,
            lookupRoot,
            lifetime);

    /// <summary>
    /// Activates generated ordinary binding using compiler-owned immutable path steps. The
    /// runtime consumes the steps directly and does not tokenize or reinterpret generated text.
    /// </summary>
    public static BindingExpression SetBindingWithPath(
        DependencyObject target,
        string targetPropertyName,
        Binding binding,
        IReadOnlyList<BindingPathSegment> pathSegments,
        object? context = null,
        object? lookupRoot = null,
        IXamlTemplateLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(targetPropertyName);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(pathSegments);

        var targetProperty = DependencyProperty.Lookup(
            target.GetType(),
            targetPropertyName);
        if (targetProperty == null)
            throw new InvalidOperationException(
                $"Dependency property '{targetPropertyName}' was not registered for " +
                $"'{target.GetType().FullName}'.");

        return SetBindingCore(
            target,
            targetProperty,
            binding,
            pathSegments,
            context,
            lookupRoot,
            lifetime);
    }

    private static BindingExpression SetBindingCore(
        DependencyObject target,
        DependencyProperty targetProperty,
        Binding binding,
        IReadOnlyList<BindingPathSegment>? pathSegments,
        object? context,
        object? lookupRoot,
        IXamlTemplateLifetime? lifetime)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetProperty);
        ArgumentNullException.ThrowIfNull(binding);

        var lifetimeStore = GetLifetimeStore(lifetime);
        CompiledBindingOperations.ClearBinding(target, targetProperty);
        var store = Stores.GetOrCreateValue(target);
        if (store.Expressions.Remove(targetProperty, out var previous))
        {
            UnregisterOwner(previous);
            previous.Dispose();
        }
        var expression = new BindingExpression(
            target,
            targetProperty,
            binding,
            context,
            lookupRoot,
            pathSegments,
            initialize: lifetimeStore == null || !lifetimeStore.DeferInitialization);
        store.Expressions.Add(targetProperty, expression);
        if (lifetimeStore != null)
        {
            lifetimeStore.Expressions.Add(expression);
            ExpressionOwners.Add(expression, lifetimeStore);
        }
        else if (context != null)
            ContextStores.GetOrCreateValue(context).Expressions.Add(expression);
        return expression;
    }

    /// <summary>
    /// Begins an ownership group independent from the binding source or data context. Generated
    /// deferred factories use one group per materialization and publish it only after the
    /// complete template root has been constructed.
    /// </summary>
    public static IXamlTemplateLifetime BeginBindings() =>
        new BindingLifetime(new BindingLifetimeStore
        {
            DeferInitialization = true
        });

    public static Binding? GetBinding(DependencyObject target, DependencyProperty property)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        return Stores.TryGetValue(target, out var store) &&
               store.Expressions.TryGetValue(property, out var expression)
            ? expression.ParentBinding
            : null;
    }

    public static BindingExpression? GetBindingExpression(
        DependencyObject target,
        DependencyProperty property)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        return Stores.TryGetValue(target, out var store) &&
               store.Expressions.TryGetValue(property, out var expression)
            ? expression
            : null;
    }

    public static void ClearBinding(
        DependencyObject target,
        DependencyProperty property)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        if (!Stores.TryGetValue(target, out var store) ||
            !store.Expressions.Remove(property, out var expression))
            return;
        UnregisterOwner(expression);
        expression.Dispose();
        target.ClearValue(property);
    }

    internal static void ClearBindingsForContext(object context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ContextStores.TryGetValue(context, out var contextStore))
            return;
        ContextStores.Remove(context);
        var expressions = contextStore.Expressions.ToArray();
        contextStore.Expressions.Clear();
        for (var index = 0; index < expressions.Length; index++)
        {
            var expression = expressions[index];
            if (Stores.TryGetValue(expression.Target, out var targetStore) &&
                targetStore.Expressions.TryGetValue(expression.TargetProperty, out var current) &&
                ReferenceEquals(current, expression))
                targetStore.Expressions.Remove(expression.TargetProperty);
            expression.Dispose();
        }
    }

    private static BindingLifetimeStore? GetLifetimeStore(
        IXamlTemplateLifetime? lifetime)
    {
        if (lifetime == null)
            return null;
        if (lifetime is not BindingLifetime bindingLifetime)
            throw new ArgumentException(
                "The binding lifetime was not created by this runtime.",
                nameof(lifetime));
        if (bindingLifetime.Store.IsDetached)
            throw new InvalidOperationException(
                "The binding lifetime has already been detached.");
        return bindingLifetime.Store;
    }

    private static void InitializeLifetime(BindingLifetimeStore store)
    {
        if (store.IsDetached)
            return;
        store.DeferInitialization = false;
        var expressions = store.Expressions.ToArray();
        for (var index = 0; index < expressions.Length; index++)
            expressions[index].Initialize();
    }

    private static void DisposeLifetime(BindingLifetimeStore store)
    {
        if (store.IsDetached)
            return;
        store.IsDetached = true;
        store.DeferInitialization = true;
        var expressions = store.Expressions.ToArray();
        store.Expressions.Clear();
        for (var index = 0; index < expressions.Length; index++)
        {
            var expression = expressions[index];
            if (Stores.TryGetValue(expression.Target, out var targetStore) &&
                targetStore.Expressions.TryGetValue(expression.TargetProperty, out var current) &&
                ReferenceEquals(current, expression))
                targetStore.Expressions.Remove(expression.TargetProperty);
            ExpressionOwners.Remove(expression);
            expression.Dispose();
        }
    }

    private static void UnregisterOwner(BindingExpression expression)
    {
        if (ExpressionOwners.TryGetValue(expression, out var lifetimeStore))
        {
            lifetimeStore.Expressions.Remove(expression);
            ExpressionOwners.Remove(expression);
        }
        else if (expression.Context != null &&
            ContextStores.TryGetValue(expression.Context, out var contextStore))
            contextStore.Expressions.Remove(expression);
    }

    private sealed class BindingStore
    {
        public Dictionary<DependencyProperty, BindingExpression> Expressions { get; } = new();
    }

    private sealed class ContextBindingStore
    {
        public HashSet<BindingExpression> Expressions { get; } = new();
    }

    private sealed class BindingLifetimeStore
    {
        public HashSet<BindingExpression> Expressions { get; } = new();
        public bool DeferInitialization { get; set; }
        public bool IsDetached { get; set; }
    }

    private sealed class BindingLifetime : IXamlTemplateLifetime
    {
        private bool _initialized;

        public BindingLifetime(BindingLifetimeStore store) =>
            Store = store;

        public BindingLifetimeStore Store { get; }

        public void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;
            InitializeLifetime(Store);
        }

        public void Dispose() =>
            DisposeLifetime(Store);
    }
}
