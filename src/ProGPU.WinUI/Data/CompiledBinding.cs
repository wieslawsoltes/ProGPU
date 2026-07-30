using Microsoft.UI.Xaml.Markup;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;

namespace Microsoft.UI.Xaml.Data;

/// <summary>
/// One statically generated member-access step. Generated code supplies typed delegates and,
/// when applicable, the exact dependency-property identifier. No runtime reflection or member
/// name lookup is performed.
/// </summary>
public interface ICompiledBindingPathSegment
{
    string MemberName { get; }
    Type SourceType { get; }
    Type ValueType { get; }
    bool CanWrite { get; }
    object? GetValue(object source);
    void SetValue(object source, object? value);
    Action? Subscribe(object source, Action changed);
}

public sealed class CompiledBindingPathSegment<TSource, TValue> : ICompiledBindingPathSegment
{
    private readonly Func<TSource, TValue> _getter;
    private readonly Action<TSource, TValue>? _setter;
    private readonly DependencyProperty? _dependencyProperty;

    public CompiledBindingPathSegment(
        string memberName,
        Func<TSource, TValue> getter,
        Action<TSource, TValue>? setter = null,
        DependencyProperty? dependencyProperty = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentNullException.ThrowIfNull(getter);
        MemberName = memberName;
        _getter = getter;
        _setter = setter;
        _dependencyProperty = dependencyProperty;
    }

    public string MemberName { get; }
    public Type SourceType => typeof(TSource);
    public Type ValueType => typeof(TValue);
    public bool CanWrite => _setter != null;
    public object? GetValue(object source) => _getter((TSource)source);

    public void SetValue(object source, object? value)
    {
        if (_setter == null)
            throw new InvalidOperationException(
                $"Compiled-binding member '{typeof(TSource).FullName}.{MemberName}' is read-only.");
        _setter((TSource)source, (TValue)XamlValueConverter.ConvertTo(typeof(TValue), value)!);
    }

    public Action? Subscribe(object source, Action changed)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(changed);
        if (_dependencyProperty != null && source is DependencyObject dependencyObject)
        {
            var token = dependencyObject.RegisterPropertyChangedCallback(
                _dependencyProperty,
                (_, _) => changed());
            return () => dependencyObject.UnregisterPropertyChangedCallback(
                _dependencyProperty,
                token);
        }
        if (source is not INotifyPropertyChanged notifier) return null;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (string.IsNullOrEmpty(args.PropertyName) ||
                string.Equals(args.PropertyName, MemberName, StringComparison.Ordinal))
                changed();
        };
        notifier.PropertyChanged += handler;
        return () => notifier.PropertyChanged -= handler;
    }
}

/// <summary>
/// One statically validated explicit cast. Cast steps do not own notifications; the member or
/// indexer before and after the cast owns the relevant source change contract.
/// </summary>
public sealed class CompiledBindingCastPathSegment<TSource, TValue> :
    ICompiledBindingPathSegment
{
    private readonly Func<TSource, TValue> _getter;

    public CompiledBindingCastPathSegment(
        string typeName,
        Func<TSource, TValue> getter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(getter);
        MemberName = "(" + typeName + ")";
        _getter = getter;
    }

    public string MemberName { get; }
    public Type SourceType => typeof(TSource);
    public Type ValueType => typeof(TValue);
    public bool CanWrite => false;
    public object? GetValue(object source) => _getter((TSource)source);

    public void SetValue(object source, object? value) =>
        throw new InvalidOperationException(
            $"Compiled-binding cast '{MemberName}' is not writable.");

    public Action? Subscribe(object source, Action changed) => null;
}

/// <summary>
/// One statically bound terminal function call. Generated dependency paths describe the
/// instance owner and every path-valued argument, allowing OneWay reevaluation and nested
/// rewiring without reflection.
/// </summary>
public sealed class CompiledBindingFunctionPathSegment<TSource, TValue> :
    ICompiledBindingPathSegment
{
    private readonly Func<TSource, TValue> _getter;
    private readonly ICompiledBindingPathSegment[] _ownerPath;
    private readonly ICompiledBindingPathSegment[][] _dependencies;

    public CompiledBindingFunctionPathSegment(
        string methodName,
        Func<TSource, TValue> getter,
        ICompiledBindingPathSegment[] ownerPath,
        ICompiledBindingPathSegment[][] dependencies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(ownerPath);
        ArgumentNullException.ThrowIfNull(dependencies);
        MemberName = methodName;
        _getter = getter;
        _ownerPath = ownerPath;
        _dependencies = dependencies;
    }

    public string MemberName { get; }
    public Type SourceType => typeof(TSource);
    public Type ValueType => typeof(TValue);
    public bool CanWrite => false;
    public object? GetValue(object source) => _getter((TSource)source);

    public void SetValue(object source, object? value) =>
        throw new InvalidOperationException(
            $"Compiled-binding function '{typeof(TSource).FullName}.{MemberName}' is not writable.");

    public Action? Subscribe(object source, Action changed)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(changed);
        var unsubscribe = new List<Action>();
        object? methodOwner = source;
        foreach (var segment in _ownerPath)
        {
            if (methodOwner == null) break;
            var segmentUnsubscribe = segment.Subscribe(methodOwner, changed);
            if (segmentUnsubscribe != null)
                unsubscribe.Add(segmentUnsubscribe);
            methodOwner = segment.GetValue(methodOwner);
        }
        if (methodOwner is INotifyPropertyChanged notifier)
        {
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) ||
                    string.Equals(args.PropertyName, MemberName, StringComparison.Ordinal))
                    changed();
            };
            notifier.PropertyChanged += handler;
            unsubscribe.Add(() => notifier.PropertyChanged -= handler);
        }

        foreach (var dependency in _dependencies)
        {
            object? current = source;
            foreach (var segment in dependency)
            {
                if (current == null) break;
                var segmentUnsubscribe = segment.Subscribe(current, changed);
                if (segmentUnsubscribe != null)
                    unsubscribe.Add(segmentUnsubscribe);
                current = segment.GetValue(current);
            }
        }
        if (unsubscribe.Count == 0) return null;
        return () =>
        {
            for (var index = unsubscribe.Count - 1; index >= 0; index--)
                unsubscribe[index]();
        };
    }
}

/// <summary>
/// One statically generated constant-index step. The generated delegates contain the exact
/// IList&lt;T&gt; or IDictionary&lt;string,T&gt; access and therefore preserve explicit interface
/// implementations without reflection. Collection notifications invalidate the compiled path.
/// </summary>
public sealed class CompiledBindingIndexerPathSegment<TSource, TValue> :
    ICompiledBindingPathSegment
{
    private readonly Func<TSource, TValue> _getter;
    private readonly Action<TSource, TValue>? _setter;

    public CompiledBindingIndexerPathSegment(
        int index,
        Func<TSource, TValue> getter,
        Action<TSource, TValue>? setter = null)
    {
        ArgumentNullException.ThrowIfNull(getter);
        MemberName =
            "[" + index.ToString(
                global::System.Globalization.CultureInfo.InvariantCulture) +
            "]";
        _getter = getter;
        _setter = setter;
    }

    public CompiledBindingIndexerPathSegment(
        string key,
        Func<TSource, TValue> getter,
        Action<TSource, TValue>? setter = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(getter);
        MemberName = "['" + key + "']";
        _getter = getter;
        _setter = setter;
    }

    public string MemberName { get; }
    public Type SourceType => typeof(TSource);
    public Type ValueType => typeof(TValue);
    public bool CanWrite => _setter != null;
    public object? GetValue(object source) => _getter((TSource)source);

    public void SetValue(object source, object? value)
    {
        if (_setter == null)
            throw new InvalidOperationException(
                $"Compiled-binding indexer '{typeof(TSource).FullName}{MemberName}' is read-only.");
        _setter((TSource)source, (TValue)XamlValueConverter.ConvertTo(typeof(TValue), value)!);
    }

    public Action? Subscribe(object source, Action changed)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(changed);
        if (source is INotifyCollectionChanged collection)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => changed();
            collection.CollectionChanged += handler;
            return () => collection.CollectionChanged -= handler;
        }
        if (source is not INotifyPropertyChanged notifier) return null;
        PropertyChangedEventHandler propertyHandler = (_, args) =>
        {
            if (string.IsNullOrEmpty(args.PropertyName) ||
                string.Equals(args.PropertyName, "Item[]", StringComparison.Ordinal) ||
                string.Equals(args.PropertyName, MemberName, StringComparison.Ordinal))
                changed();
        };
        notifier.PropertyChanged += propertyHandler;
        return () => notifier.PropertyChanged -= propertyHandler;
    }
}

/// <summary>
/// Runtime options for generated compiled binding. Member access remains wholly represented by
/// typed generated delegates; this descriptor contains only binding policy and literal values.
/// </summary>
public sealed class CompiledBindingOptions
{
    public BindingMode Mode { get; set; } = BindingMode.OneTime;
    public UpdateSourceTrigger UpdateSourceTrigger { get; set; }
    public object? Converter { get; set; }
    public object? ConverterParameter { get; set; }
    public string? ConverterLanguage { get; set; }
    public object? FallbackValue { get; set; }
    public object? TargetNullValue { get; set; }
    public Action<object, object?>? BindBack { get; set; }
}

/// <summary>
/// Reflection-free compiled-binding state. Initialization and path rewiring are O(P) for P
/// segments. A steady source notification reevaluates O(P) typed delegates and uses bounded
/// subscription storage.
/// </summary>
public sealed class CompiledBindingExpression : IDisposable
{
    private readonly DependencyObject _target;
    private readonly DependencyProperty _targetProperty;
    private readonly object? _source;
    private readonly IReadOnlyList<ICompiledBindingPathSegment> _segments;
    private readonly CompiledBindingOptions _options;
    private readonly List<Action> _sourceUnsubscribe = new();
    private long _targetCallbackToken;
    private long _focusCallbackToken;
    private object? _leafSource;
    private bool _updating;
    private bool _disposed;
    private bool _tracking;

    internal CompiledBindingExpression(
        DependencyObject target,
        DependencyProperty targetProperty,
        object? source,
        IReadOnlyList<ICompiledBindingPathSegment> segments,
        CompiledBindingOptions options,
        bool initialize = true)
    {
        _target = target;
        _targetProperty = targetProperty;
        _source = source;
        _segments = segments;
        _options = options;
        Status = BindingExpressionStatus.Inactive;
        if (initialize)
            Initialize();
    }

    internal DependencyObject Target => _target;
    internal DependencyProperty TargetProperty => _targetProperty;
    internal object? Source => _source;
    public BindingExpressionStatus Status { get; private set; }
    public string? Error { get; private set; }

    public void Initialize()
    {
        ThrowIfDisposed();
        if (!_tracking)
        {
            _tracking = true;
            RegisterTargetTracking();
        }
        Update();
    }

    public void Update()
    {
        ThrowIfDisposed();
        if (_updating) return;
        _updating = true;
        try
        {
            ResetSourceSubscriptions();
            var subscribe = _tracking && _options.Mode != BindingMode.OneTime;
            if (!TryEvaluatePath(subscribe, out var value))
            {
                value = _options.FallbackValue ?? _targetProperty.Metadata?.DefaultValue;
                Status = BindingExpressionStatus.PathError;
            }
            else
            {
                if (value == null && _options.TargetNullValue != null)
                    value = _options.TargetNullValue;
                Status = BindingExpressionStatus.Active;
                Error = null;
            }
            if (_options.Converter is IValueConverter converter)
            {
                value = converter.Convert(
                    value,
                    _targetProperty.PropertyType,
                    _options.ConverterParameter,
                    _options.ConverterLanguage ?? string.Empty);
            }
            _target.SetValue(
                _targetProperty,
                XamlValueConverter.ConvertTo(_targetProperty.PropertyType, value));
        }
        catch (Exception exception)
        {
            Status = BindingExpressionStatus.UpdateError;
            Error = exception.Message;
            var fallback = _options.FallbackValue ?? _targetProperty.Metadata?.DefaultValue;
            _target.SetValue(
                _targetProperty,
                XamlValueConverter.ConvertTo(_targetProperty.PropertyType, fallback));
        }
        finally
        {
            _updating = false;
        }
    }

    public void UpdateSource()
    {
        ThrowIfDisposed();
        if (_updating || _options.Mode != BindingMode.TwoWay) return;
        _updating = true;
        try
        {
            if (_leafSource == null && !TryEvaluatePath(subscribe: true, out _))
            {
                Status = BindingExpressionStatus.PathError;
                return;
            }
            var value = _target.GetValue(_targetProperty);
            var sourceType = _segments.Count == 0
                ? _source?.GetType() ?? typeof(object)
                : _segments[_segments.Count - 1].ValueType;
            if (_options.Converter is IValueConverter converter)
            {
                value = converter.ConvertBack(
                    value,
                    sourceType,
                    _options.ConverterParameter,
                    _options.ConverterLanguage ?? string.Empty);
            }
            if (_options.BindBack != null)
            {
                if (_source == null)
                    throw new InvalidOperationException(
                        "A null compiled-binding source cannot be passed to BindBack.");
                _options.BindBack(_source, value);
            }
            else if (_segments.Count != 0 && _leafSource != null)
                _segments[_segments.Count - 1].SetValue(_leafSource, value);
            else
                throw new InvalidOperationException(
                    "The compiled-binding path is not writable and has no BindBack delegate.");
            Status = BindingExpressionStatus.Active;
            Error = null;
        }
        catch (Exception exception)
        {
            Status = BindingExpressionStatus.UpdateError;
            Error = exception.Message;
        }
        finally
        {
            _updating = false;
        }
    }

    public void StopTracking()
    {
        ThrowIfDisposed();
        _tracking = false;
        ResetSourceSubscriptions();
        UnregisterTargetTracking();
        Status = BindingExpressionStatus.Inactive;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopTracking();
        _disposed = true;
        Status = BindingExpressionStatus.Detached;
    }

    private bool TryEvaluatePath(bool subscribe, out object? value)
    {
        _leafSource = null;
        object? current = _source;
        for (var index = 0; index < _segments.Count; index++)
        {
            if (current == null)
            {
                value = null;
                Error =
                    $"Compiled-binding path reached null before '{_segments[index].MemberName}'.";
                return false;
            }
            var segment = _segments[index];
            if (subscribe)
            {
                var unsubscribe = segment.Subscribe(current, Update);
                if (unsubscribe != null) _sourceUnsubscribe.Add(unsubscribe);
            }
            if (index == _segments.Count - 1) _leafSource = current;
            current = segment.GetValue(current);
        }
        value = current;
        return true;
    }

    private void RegisterTargetTracking()
    {
        if (_options.Mode != BindingMode.TwoWay) return;
        if (_options.UpdateSourceTrigger is UpdateSourceTrigger.Default or
            UpdateSourceTrigger.PropertyChanged)
        {
            _targetCallbackToken = _target.RegisterPropertyChangedCallback(
                _targetProperty,
                (_, _) =>
                {
                    if (!_disposed) UpdateSource();
                });
        }
        else if (_options.UpdateSourceTrigger == UpdateSourceTrigger.LostFocus &&
                 _target is global::Microsoft.UI.Xaml.Controls.Control control)
        {
            _focusCallbackToken = control.RegisterPropertyChangedCallback(
                global::Microsoft.UI.Xaml.Controls.Control.IsFocusedProperty,
                (_, args) =>
                {
                    if (!_disposed && args.NewValue is false) UpdateSource();
                });
        }
    }

    private void UnregisterTargetTracking()
    {
        if (_targetCallbackToken != 0)
        {
            _target.UnregisterPropertyChangedCallback(
                _targetProperty,
                _targetCallbackToken);
            _targetCallbackToken = 0;
        }
        if (_focusCallbackToken != 0 &&
            _target is global::Microsoft.UI.Xaml.Controls.Control control)
        {
            control.UnregisterPropertyChangedCallback(
                global::Microsoft.UI.Xaml.Controls.Control.IsFocusedProperty,
                _focusCallbackToken);
            _focusCallbackToken = 0;
        }
    }

    private void ResetSourceSubscriptions()
    {
        for (var index = _sourceUnsubscribe.Count - 1; index >= 0; index--)
            _sourceUnsubscribe[index]();
        _sourceUnsubscribe.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CompiledBindingExpression));
    }
}

/// <summary>
/// Public lifecycle surface generated for every page or control containing x:Bind.
/// </summary>
public interface ICompiledBindings
{
    void Initialize();
    void Update();
    void StopTracking();
}

public static class CompiledBindingOperations
{
    private static readonly ConditionalWeakTable<DependencyObject, ExpressionStore> Stores = new();
    private static readonly ConditionalWeakTable<object, SourceExpressionStore> SourceStores = new();
    private static readonly ConditionalWeakTable<
        CompiledBindingExpression,
        SourceExpressionStore> ExpressionOwners = new();

    public static CompiledBindingExpression SetBinding(
        DependencyObject target,
        DependencyProperty targetProperty,
        object? source,
        IReadOnlyList<ICompiledBindingPathSegment> path,
        CompiledBindingOptions? options = null,
        ICompiledBindings? bindings = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetProperty);
        ArgumentNullException.ThrowIfNull(path);
        BindingOperations.ClearBinding(target, targetProperty);
        var store = Stores.GetOrCreateValue(target);
        if (store.Expressions.Remove(targetProperty, out var previous))
        {
            UnregisterSource(previous);
            previous.Dispose();
        }
        var sourceStore = GetBindingStore(source, bindings);
        var expression = new CompiledBindingExpression(
            target,
            targetProperty,
            source,
            path,
            options ?? new CompiledBindingOptions(),
            initialize: !sourceStore.DeferInitialization);
        store.Expressions.Add(targetProperty, expression);
        sourceStore.Expressions.Add(expression);
        ExpressionOwners.Add(expression, sourceStore);
        return expression;
    }

    /// <summary>
    /// Begins one ownership group that is independent from the binding source. Generated
    /// deferred factories use this overload so repeated materializations of the same data item
    /// never share tracking or disposal state.
    /// </summary>
    public static ICompiledBindings BeginBindings()
    {
        var sourceStore = new SourceExpressionStore
        {
            DeferInitialization = true
        };
        return new CompiledBindings(sourceStore);
    }

    public static ICompiledBindings BeginBindings(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sourceStore = SourceStores.GetOrCreateValue(source);
        sourceStore.DeferInitialization = true;
        return new CompiledBindings(sourceStore);
    }

    public static CompiledBindingExpression? GetBindingExpression(
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
        UnregisterSource(expression);
        expression.Dispose();
        target.ClearValue(property);
    }

    public static void ClearBindingsForSource(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!SourceStores.TryGetValue(source, out var sourceStore))
            return;
        SourceStores.Remove(source);
        DisposeStore(sourceStore);
    }

    internal static void DisposeBindings(ICompiledBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings is not CompiledBindings compiledBindings)
            throw new ArgumentException(
                "The compiled-binding controller was not created by this runtime.",
                nameof(bindings));
        DisposeStore(compiledBindings.SourceStore);
    }

    private static SourceExpressionStore GetBindingStore(
        object? source,
        ICompiledBindings? bindings)
    {
        if (bindings == null)
        {
            ArgumentNullException.ThrowIfNull(source);
            return SourceStores.GetOrCreateValue(source);
        }
        if (bindings is not CompiledBindings compiledBindings)
            throw new ArgumentException(
                "The compiled-binding controller was not created by this runtime.",
                nameof(bindings));
        if (compiledBindings.SourceStore.IsDetached)
            throw new InvalidOperationException(
                "The compiled-binding ownership group has already been detached.");
        return compiledBindings.SourceStore;
    }

    private static void DisposeStore(SourceExpressionStore sourceStore)
    {
        if (sourceStore.IsDetached)
            return;
        sourceStore.IsDetached = true;
        sourceStore.DeferInitialization = true;
        var expressions = sourceStore.Expressions.ToArray();
        sourceStore.Expressions.Clear();
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

    private static void InitializeBindings(SourceExpressionStore sourceStore)
    {
        if (sourceStore.IsDetached)
            return;
        sourceStore.DeferInitialization = false;
        var expressions = sourceStore.Expressions.ToArray();
        for (var index = 0; index < expressions.Length; index++)
            expressions[index].Initialize();
    }

    private static void StopBindings(SourceExpressionStore sourceStore)
    {
        if (sourceStore.IsDetached)
            return;
        sourceStore.DeferInitialization = true;
        var expressions = sourceStore.Expressions.ToArray();
        for (var index = 0; index < expressions.Length; index++)
            expressions[index].StopTracking();
    }

    private static void UnregisterSource(CompiledBindingExpression expression)
    {
        if (ExpressionOwners.TryGetValue(expression, out var sourceStore))
        {
            sourceStore.Expressions.Remove(expression);
            ExpressionOwners.Remove(expression);
        }
    }

    private sealed class ExpressionStore
    {
        public Dictionary<DependencyProperty, CompiledBindingExpression> Expressions { get; } = new();
    }

    private sealed class SourceExpressionStore
    {
        public HashSet<CompiledBindingExpression> Expressions { get; } = new();
        public bool DeferInitialization { get; set; }
        public bool IsDetached { get; set; }
    }

    private sealed class CompiledBindings : ICompiledBindings
    {
        private readonly SourceExpressionStore _sourceStore;
        private bool _initialized;

        public CompiledBindings(SourceExpressionStore sourceStore) =>
            _sourceStore = sourceStore;

        public SourceExpressionStore SourceStore => _sourceStore;

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            InitializeBindings(_sourceStore);
        }

        public void Update()
        {
            _initialized = true;
            InitializeBindings(_sourceStore);
        }

        public void StopTracking()
        {
            StopBindings(_sourceStore);
            _initialized = false;
        }
    }
}
