using Microsoft.UI.Xaml.Markup;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Microsoft.UI.Xaml.Data;

public enum BindingPathSegmentKind
{
    Member,
    IntegerIndexer,
    StringIndexer
}

/// <summary>
/// One immutable ordinary-binding path step. Generated XAML supplies these steps from the
/// framework-neutral compiler syntax tree, so the runtime never reparses generated paths.
/// </summary>
public readonly struct BindingPathSegment : IEquatable<BindingPathSegment>
{
    private BindingPathSegment(
        BindingPathSegmentKind kind,
        string? text,
        int integerIndex)
    {
        Kind = kind;
        Text = text;
        IntegerIndex = integerIndex;
    }

    public BindingPathSegmentKind Kind { get; }
    public string? Text { get; }
    public int IntegerIndex { get; }

    public static BindingPathSegment Member(string memberName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        return new BindingPathSegment(
            BindingPathSegmentKind.Member,
            memberName,
            0);
    }

    public static BindingPathSegment Indexer(int index) =>
        new(BindingPathSegmentKind.IntegerIndexer, null, index);

    public static BindingPathSegment Indexer(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new BindingPathSegment(
            BindingPathSegmentKind.StringIndexer,
            key,
            0);
    }

    public bool Equals(BindingPathSegment other) =>
        Kind == other.Kind &&
        IntegerIndex == other.IntegerIndex &&
        string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is BindingPathSegment other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        (int)Kind,
        IntegerIndex,
        Text == null ? 0 : StringComparer.Ordinal.GetHashCode(Text));

    public override string ToString() => Kind switch
    {
        BindingPathSegmentKind.IntegerIndexer =>
            "[" + IntegerIndex.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + "]",
        BindingPathSegmentKind.StringIndexer => "['" + Text + "']",
        _ => Text ?? string.Empty
    };
}

/// <summary>
/// A reflection-free accessor for one CLR binding-path segment. Source generators and
/// applications register these descriptors once, then the runtime composes them with
/// dependency-property segments.
/// </summary>
public interface IBindingMemberAccessor
{
    Type SourceType { get; }
    Type ValueType { get; }
    bool CanWrite { get; }
    BindingPathSegment Segment { get; }
    object? GetValue(object source);
    void SetValue(object source, object? value);
    Action? Subscribe(object source, Action changed);
}

/// <summary>
/// Process-wide typed binding accessor registry. Lookup is exact first, then base types and
/// interfaces in deterministic name order. Registration performs no object activation.
/// </summary>
public static class BindingMemberAccessorRegistry
{
    private static readonly ConcurrentDictionary<
        (Type SourceType, BindingPathSegment Segment),
        IBindingMemberAccessor>
        Accessors = new();

    public static void Register<TSource, TValue>(
        string memberName,
        Func<TSource, TValue> getter,
        Action<TSource, TValue>? setter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentNullException.ThrowIfNull(getter);
        var segment = BindingPathSegment.Member(memberName);
        Accessors[(typeof(TSource), segment)] =
            new BindingMemberAccessor<TSource, TValue>(
                segment,
                getter,
                setter);
    }

    public static void RegisterIndexer<TSource, TValue>(
        int index,
        Func<TSource, TValue> getter,
        Action<TSource, TValue>? setter = null)
    {
        ArgumentNullException.ThrowIfNull(getter);
        var segment = BindingPathSegment.Indexer(index);
        Accessors[(typeof(TSource), segment)] =
            new BindingIndexerAccessor<TSource, TValue>(
                segment,
                getter,
                setter);
    }

    public static void RegisterIndexer<TSource, TValue>(
        string key,
        Func<TSource, TValue> getter,
        Action<TSource, TValue>? setter = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(getter);
        var segment = BindingPathSegment.Indexer(key);
        Accessors[(typeof(TSource), segment)] =
            new BindingIndexerAccessor<TSource, TValue>(
                segment,
                getter,
                setter);
    }

    public static bool TryGetAccessor(
        Type sourceType,
        string memberName,
        out IBindingMemberAccessor accessor) =>
        TryGetAccessor(
            sourceType,
            BindingPathSegment.Member(memberName),
            out accessor);

    public static bool TryGetAccessor(
        Type sourceType,
        BindingPathSegment segment,
        out IBindingMemberAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        for (var current = sourceType; current != null; current = current.BaseType)
        {
            if (Accessors.TryGetValue((current, segment), out accessor!))
                return true;
        }

        var interfaces = sourceType.GetInterfaces();
        Array.Sort(
            interfaces,
            static (left, right) =>
                string.CompareOrdinal(left.FullName, right.FullName));
        for (var index = 0; index < interfaces.Length; index++)
        {
            if (Accessors.TryGetValue((interfaces[index], segment), out accessor!))
                return true;
        }

        accessor = null!;
        return false;
    }

    private sealed class BindingMemberAccessor<TSource, TValue> : IBindingMemberAccessor
    {
        private readonly Func<TSource, TValue> _getter;
        private readonly Action<TSource, TValue>? _setter;

        public BindingMemberAccessor(
            BindingPathSegment segment,
            Func<TSource, TValue> getter,
            Action<TSource, TValue>? setter)
        {
            Segment = segment;
            _getter = getter;
            _setter = setter;
        }

        public Type SourceType => typeof(TSource);
        public Type ValueType => typeof(TValue);
        public bool CanWrite => _setter != null;
        public BindingPathSegment Segment { get; }

        public object? GetValue(object source) => _getter((TSource)source);

        public void SetValue(object source, object? value)
        {
            if (_setter == null)
                throw new InvalidOperationException(
                    $"Binding member on '{typeof(TSource).FullName}' is read-only.");
            var converted = XamlValueConverter.ConvertTo(typeof(TValue), value);
            _setter((TSource)source, (TValue)converted!);
        }

        public Action? Subscribe(object source, Action changed)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(changed);
            if (source is not INotifyPropertyChanged notifier) return null;
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) ||
                    string.Equals(
                        args.PropertyName,
                        Segment.Text,
                        StringComparison.Ordinal))
                    changed();
            };
            notifier.PropertyChanged += handler;
            return () => notifier.PropertyChanged -= handler;
        }
    }

    private sealed class BindingIndexerAccessor<TSource, TValue> :
        IBindingMemberAccessor
    {
        private readonly Func<TSource, TValue> _getter;
        private readonly Action<TSource, TValue>? _setter;

        public BindingIndexerAccessor(
            BindingPathSegment segment,
            Func<TSource, TValue> getter,
            Action<TSource, TValue>? setter)
        {
            Segment = segment;
            _getter = getter;
            _setter = setter;
        }

        public Type SourceType => typeof(TSource);
        public Type ValueType => typeof(TValue);
        public bool CanWrite => _setter != null;
        public BindingPathSegment Segment { get; }

        public object? GetValue(object source) => _getter((TSource)source);

        public void SetValue(object source, object? value)
        {
            if (_setter == null)
                throw new InvalidOperationException(
                    $"Binding indexer on '{typeof(TSource).FullName}' is read-only.");
            var converted = XamlValueConverter.ConvertTo(typeof(TValue), value);
            _setter((TSource)source, (TValue)converted!);
        }

        public Action? Subscribe(object source, Action changed)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(changed);
            if (source is INotifyCollectionChanged collection)
            {
                NotifyCollectionChangedEventHandler collectionHandler =
                    (_, _) => changed();
                collection.CollectionChanged += collectionHandler;
                return () => collection.CollectionChanged -= collectionHandler;
            }
            if (source is not INotifyPropertyChanged notifier) return null;
            PropertyChangedEventHandler propertyHandler = (_, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) ||
                    string.Equals(args.PropertyName, "Item[]", StringComparison.Ordinal) ||
                    string.Equals(
                        args.PropertyName,
                        Segment.ToString(),
                        StringComparison.Ordinal))
                    changed();
            };
            notifier.PropertyChanged += propertyHandler;
            return () => notifier.PropertyChanged -= propertyHandler;
        }
    }
}

public enum BindingExpressionStatus
{
    Inactive,
    Active,
    PathError,
    UpdateError,
    Detached
}

/// <summary>
/// Live binding state for one target dependency property. Path evaluation and subscription
/// rebuilding are O(P), where P is the number of path segments; steady updates allocate only
/// when an intermediate source instance changes.
/// </summary>
public sealed class BindingExpression : IDisposable
{
    private readonly DependencyObject _target;
    private readonly DependencyProperty _targetProperty;
    private readonly object? _context;
    private readonly object? _lookupRoot;
    private readonly IReadOnlyList<BindingPathSegment> _pathSegments;
    private readonly List<Action> _sourceUnsubscribe = new();
    private long _targetCallbackToken;
    private bool _updating;
    private bool _initialized;
    private bool _disposed;
    private object? _leafSource;
    private DependencyProperty? _leafProperty;
    private IBindingMemberAccessor? _leafAccessor;
    private long _focusCallbackToken;

    internal BindingExpression(
        DependencyObject target,
        DependencyProperty targetProperty,
        Binding binding,
        object? context,
        object? lookupRoot,
        IReadOnlyList<BindingPathSegment>? pathSegments = null,
        bool initialize = true)
    {
        _target = target;
        _targetProperty = targetProperty;
        ParentBinding = binding;
        _context = context;
        _lookupRoot = lookupRoot;
        _pathSegments = pathSegments ?? CreateLegacyMemberPath(binding.Path);
        Status = BindingExpressionStatus.Inactive;

        if (initialize)
            Initialize();
    }

    internal void Initialize()
    {
        ThrowIfDisposed();
        if (_initialized) return;
        _initialized = true;
        if (ParentBinding.Mode == BindingMode.TwoWay &&
            ParentBinding.UpdateSourceTrigger is UpdateSourceTrigger.Default or
                UpdateSourceTrigger.PropertyChanged)
        {
            _targetCallbackToken = _target.RegisterPropertyChangedCallback(
                _targetProperty,
                OnTargetPropertyChanged);
        }
        else if (ParentBinding.Mode == BindingMode.TwoWay &&
                 ParentBinding.UpdateSourceTrigger == UpdateSourceTrigger.LostFocus &&
                 _target is global::Microsoft.UI.Xaml.Controls.Control control)
        {
            _focusCallbackToken = control.RegisterPropertyChangedCallback(
                global::Microsoft.UI.Xaml.Controls.Control.IsFocusedProperty,
                OnTargetFocusChanged);
        }

        UpdateTarget();
    }

    public Binding ParentBinding { get; }
    internal DependencyObject Target => _target;
    internal DependencyProperty TargetProperty => _targetProperty;
    internal object? Context => _context;
    public BindingExpressionStatus Status { get; private set; }
    public string? Error { get; private set; }

    public void UpdateTarget()
    {
        ThrowIfDisposed();
        if (_updating) return;
        _updating = true;
        try
        {
            ResetSourceSubscriptions();
            if (!TryEvaluatePath(out var value, subscribe: ParentBinding.Mode != BindingMode.OneTime))
            {
                value = ParentBinding.FallbackValue ?? _targetProperty.Metadata?.DefaultValue;
                Status = BindingExpressionStatus.PathError;
            }
            else
            {
                if (value == null && ParentBinding.TargetNullValue != null)
                    value = ParentBinding.TargetNullValue;
                Status = BindingExpressionStatus.Active;
                Error = null;
            }

            if (ParentBinding.Converter is IValueConverter converter)
            {
                value = converter.Convert(
                    value,
                    _targetProperty.PropertyType,
                    ParentBinding.ConverterParameter,
                    ParentBinding.ConverterLanguage ?? string.Empty);
            }

            _target.SetValue(
                _targetProperty,
                XamlValueConverter.ConvertTo(_targetProperty.PropertyType, value));
        }
        catch (Exception exception)
        {
            Status = BindingExpressionStatus.UpdateError;
            Error = exception.Message;
            var fallback = ParentBinding.FallbackValue ?? _targetProperty.Metadata?.DefaultValue;
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
        if (_updating || ParentBinding.Mode != BindingMode.TwoWay) return;
        _updating = true;
        try
        {
            if (_leafSource == null && !TryEvaluatePath(out _, subscribe: true))
            {
                Status = BindingExpressionStatus.PathError;
                return;
            }

            var value = _target.GetValue(_targetProperty);
            if (ParentBinding.Converter is IValueConverter converter)
            {
                var sourceType = _leafProperty?.PropertyType ??
                                 _leafAccessor?.ValueType ??
                                 typeof(object);
                value = converter.ConvertBack(
                    value,
                    sourceType,
                    ParentBinding.ConverterParameter,
                    ParentBinding.ConverterLanguage ?? string.Empty);
            }

            if (_leafSource is DependencyObject dependencySource &&
                _leafProperty is not null)
            {
                dependencySource.SetValue(
                    _leafProperty,
                    XamlValueConverter.ConvertTo(_leafProperty.PropertyType, value));
                Status = BindingExpressionStatus.Active;
                Error = null;
                return;
            }
            if (_leafSource != null && _leafAccessor?.CanWrite == true)
            {
                _leafAccessor.SetValue(_leafSource, value);
                Status = BindingExpressionStatus.Active;
                Error = null;
                return;
            }

            Status = BindingExpressionStatus.PathError;
            Error = "The binding path does not end in a writable member.";
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetSourceSubscriptions();
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
        Status = BindingExpressionStatus.Detached;
    }

    private bool TryEvaluatePath(out object? value, bool subscribe)
    {
        _leafSource = null;
        _leafProperty = null;
        _leafAccessor = null;

        object? current = ResolveSource(subscribe);
        var path = ParentBinding.Path?.Trim();
        if (string.IsNullOrEmpty(path) || path == ".")
        {
            value = current;
            return current != null;
        }

        for (var index = 0; index < _pathSegments.Count; index++)
        {
            var segment = _pathSegments[index];
            if (current == null)
            {
                value = null;
                Error = $"Binding path '{path}' reached null before '{segment}'.";
                return false;
            }

            var isLeaf = index == _pathSegments.Count - 1;
            if (segment.Kind == BindingPathSegmentKind.Member &&
                current is DependencyObject dependencySource &&
                DependencyProperty.Lookup(
                    dependencySource.GetType(),
                    segment.Text!) is { } property)
            {
                if (subscribe)
                {
                    var token = dependencySource.RegisterPropertyChangedCallback(
                        property,
                        OnSourcePropertyChanged);
                    _sourceUnsubscribe.Add(
                        () => dependencySource.UnregisterPropertyChangedCallback(property, token));
                }
                if (isLeaf)
                {
                    _leafSource = dependencySource;
                    _leafProperty = property;
                }
                current = dependencySource.GetValue(property);
                continue;
            }

            if (!BindingMemberAccessorRegistry.TryGetAccessor(
                    current.GetType(),
                    segment,
                    out var accessor))
            {
                value = null;
                Error =
                    $"No typed binding accessor is registered for " +
                    $"'{current.GetType().FullName}.{segment}'.";
                return false;
            }
            if (subscribe && accessor.Subscribe(current, UpdateTarget) is { } unsubscribe)
                _sourceUnsubscribe.Add(unsubscribe);
            if (isLeaf)
            {
                _leafSource = current;
                _leafAccessor = accessor;
            }
            current = accessor.GetValue(current);
        }

        value = current;
        return true;
    }

    private static IReadOnlyList<BindingPathSegment> CreateLegacyMemberPath(
        string? path)
    {
        path = path?.Trim();
        if (string.IsNullOrEmpty(path) || path == ".")
            return Array.Empty<BindingPathSegment>();
        var names = path.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        var result = new BindingPathSegment[names.Length];
        for (var index = 0; index < names.Length; index++)
            result[index] = BindingPathSegment.Member(names[index]);
        return result;
    }

    private object? ResolveSource(bool subscribe)
    {
        if (ParentBinding.Source != null)
            return ParentBinding.Source;
        switch (ParentBinding.RelativeSource?.Mode)
        {
            case RelativeSourceMode.Self:
                return _target;
            case RelativeSourceMode.TemplatedParent:
                return _context;
        }
        if (!string.IsNullOrWhiteSpace(ParentBinding.ElementName))
        {
            return (_lookupRoot as FrameworkElement)?.FindName(ParentBinding.ElementName!) ??
                   (_target as FrameworkElement)?.FindName(ParentBinding.ElementName!);
        }
        if (_target is FrameworkElement targetElement)
        {
            if (subscribe)
            {
                var token = targetElement.RegisterPropertyChangedCallback(
                    FrameworkElement.DataContextProperty,
                    OnSourcePropertyChanged);
                _sourceUnsubscribe.Add(
                    () => targetElement.UnregisterPropertyChangedCallback(
                        FrameworkElement.DataContextProperty,
                        token));
            }
            return targetElement.DataContext ?? _context;
        }
        return _context;
    }

    private void OnSourcePropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (!_disposed)
            UpdateTarget();
    }

    private void OnTargetPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (!_disposed)
            UpdateSource();
    }

    private void OnTargetFocusChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (!_disposed && args.NewValue is false)
            UpdateSource();
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
            throw new ObjectDisposedException(nameof(BindingExpression));
    }
}
