using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
#pragma warning disable CS0169 // The field is never used
#pragma warning disable CS0414 // The field is assigned but its value is never used

using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.UI.Input;
using ProGPU.Virtualization;

namespace Microsoft.UI.Xaml.Controls;

public class DataGridColumn
{
    public string Header { get; set; } = string.Empty;
    public DataGridLength Width { get; set; }
    public float ActualWidth { get; internal set; }
    public string PropertyName { get; set; } = string.Empty;
    public bool IsAscending { get; set; } = true;
    public TextWrapping? TextWrapping { get; set; }

    public DataGridColumn(string header, DataGridLength width, string propName)
    {
        Header = header;
        Width = width;
        ActualWidth = width.IsPixel ? width.Value : 120f;
        PropertyName = propName;
    }
}

public interface IDataGridValueProvider
{
    bool TryGetDataGridValue(string propertyName, out object? value);
    bool TrySetDataGridValue(string propertyName, object? value);
    Type? GetDataGridValueType(string propertyName);
}

public class DataGrid : Control
{
    private static readonly ConcurrentDictionary<DataGridValueAccessorKey, DataGridValueAccessor> s_registeredValueAccessors = new();

    private float _fontSize = 13f;
    private float _rowHeight = 28f;
    private float _headerHeight = 32f;
    private int _selectedIndex = -1;
    private float _scrollOffset;
    private bool _isDraggingScroll;
    private uint _scrollbarPointerId;
    private float _dragStartOffset;
    private float _dragStartMouseY;
    private int _hoveredRowIndex = -1;
    private bool _isPointerOverScrollbar;
    private int _resizingColumnIndex = -1;
    private float _resizeStartMouseX;
    private float _resizeStartWidth;
    private int _hoveredSeparatorIndex = -1;

    private DataGridColumn? _sortingColumn;
    private readonly List<object> _itemsSource = new();

    private int _editingRow = -1;
    private int _editingCol = -1;
    private FrameworkElement? _cellEditor;
    private DateTime _lastClickTime = DateTime.MinValue;
    private int _lastClickRow = -1;
    private int _lastClickCol = -1;
    private uint _pendingTouchPointerId;
    private int _pendingTouchRow = -1;
    private int _pendingTouchColumn = -1;
    private int _pendingTouchSortColumn = -1;
    private float _touchInertiaVelocity;
    private readonly VariableSizeIndex _rowSizes = new();
    private int _indexedRowCount = -1;
    private int _rowLayoutSignature;
    private int _indexedRowLayoutSignature = -1;
    private float _minRowHeight = 28f;
    private float _estimatedRowHeight = 40f;
    private TextWrapping _cellTextWrapping = TextWrapping.NoWrap;

    public List<DataGridColumn> Columns { get; } = new();

    public List<object> ItemsSource => _itemsSource;

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty)
        {
            InvalidateRowMeasurements();
            Invalidate();
        }
    }

    public new float FontSize
    {
        get => _fontSize;
        set { _fontSize = value; InvalidateRowMeasurements(); Invalidate(); }
    }

    public float RowHeight
    {
        get => _rowHeight;
        set
        {
            if (!float.IsNaN(value) && (!float.IsFinite(value) || value <= 0f))
                throw new ArgumentOutOfRangeException(nameof(value));
            _rowHeight = value;
            InvalidateRowMeasurements();
            Invalidate();
        }
    }

    public float MinRowHeight
    {
        get => _minRowHeight;
        set
        {
            if (!float.IsFinite(value) || value <= 0f) throw new ArgumentOutOfRangeException(nameof(value));
            _minRowHeight = value;
            InvalidateRowMeasurements();
            Invalidate();
        }
    }

    /// <summary>Estimated auto-row height used until a row enters the realization window.</summary>
    public float EstimatedRowHeight
    {
        get => _estimatedRowHeight;
        set
        {
            if (!float.IsFinite(value) || value <= 0f) throw new ArgumentOutOfRangeException(nameof(value));
            if (_estimatedRowHeight == value) return;
            _estimatedRowHeight = value;
            InvalidateRowMeasurements();
            Invalidate();
        }
    }

    public TextWrapping CellTextWrapping
    {
        get => _cellTextWrapping;
        set
        {
            if (_cellTextWrapping == value) return;
            _cellTextWrapping = value;
            InvalidateRowMeasurements();
            Invalidate();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex != value)
            {
                _selectedIndex = Math.Clamp(value, -1, _itemsSource.Count - 1);
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public DataGridColumn? SortingColumn
    {
        get => _sortingColumn;
        set { _sortingColumn = value; Invalidate(); }
    }

    public float ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            float maxScroll = Math.Max(0f, TotalBodyHeight - ViewportHeight);
            float clamped = Math.Clamp(value, 0f, maxScroll);
            if (_scrollOffset != clamped)
            {
                _scrollOffset = clamped;
                if (_editingRow != -1)
                {
                    UpdateCellEditorLayout();
                }
                PopupService.DismissNonDialogPopups();
                Invalidate();
            }
        }
    }

    public float TotalBodyHeight => IsVariableRowHeight
        ? EnsureRowSizeIndex().TotalSize
        : _itemsSource.Count * _rowHeight;
    public float ViewportHeight => Size.Y - _headerHeight;
    public int EditingRow => _editingRow;
    public int EditingCol => _editingCol;

    public Func<object, string, string>? CellValueBinding { get; set; }

    public event EventHandler? SelectionChanged;

    public DataGrid()
    {
        Padding = new Thickness(0);
        ManipulationMode = ManipulationModes.TranslateY |
            ManipulationModes.TranslateRailsY |
            ManipulationModes.TranslateInertia;

        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    public static void RegisterValueAccessor<TItem, TValue>(
        string propertyName,
        Func<TItem, TValue> getter,
        Action<TItem, TValue>? setter = null)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new ArgumentException("Property name must be non-empty.", nameof(propertyName));
        }
        ArgumentNullException.ThrowIfNull(getter);

        RegisterValueAccessor(
            typeof(TItem),
            propertyName,
            typeof(TValue),
            item => getter((TItem)item),
            setter == null
                ? null
                : (item, value) => setter((TItem)item, CastRegisteredValue<TValue>(value)));
    }

    public static void RegisterValueAccessor<TItem>(
        string propertyName,
        Type valueType,
        Func<TItem, object?> getter,
        Action<TItem, object?>? setter = null)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new ArgumentException("Property name must be non-empty.", nameof(propertyName));
        }
        ArgumentNullException.ThrowIfNull(valueType);
        ArgumentNullException.ThrowIfNull(getter);

        RegisterValueAccessor(
            typeof(TItem),
            propertyName,
            valueType,
            item => getter((TItem)item),
            setter == null
                ? null
                : (item, value) => setter((TItem)item, value));
    }

    public static bool UnregisterValueAccessor<TItem>(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return s_registeredValueAccessors.TryRemove(new DataGridValueAccessorKey(typeof(TItem), propertyName), out _);
    }

    private static void RegisterValueAccessor(
        Type itemType,
        string propertyName,
        Type valueType,
        Func<object, object?> getter,
        Action<object, object?>? setter)
    {
        var key = new DataGridValueAccessorKey(itemType, propertyName);
        s_registeredValueAccessors[key] = new DataGridValueAccessor(valueType, getter, setter);
    }

    private static TValue CastRegisteredValue<TValue>(object? value)
    {
        return value == null ? default! : (TValue)value;
    }

    public void AddItem(object item)
    {
        _itemsSource.Add(item);
        InvalidateRowMeasurements();
        Invalidate();
    }

    public void ClearItems()
    {
        CancelEdit();
        _itemsSource.Clear();
        _selectedIndex = -1;
        _scrollOffset = 0f;
        InvalidateRowMeasurements();
        Invalidate();
    }

    private string GetCellValue(object item, string propName)
    {
        if (CellValueBinding != null) return CellValueBinding(item, propName);
        return TryGetCellRawValue(item, propName, out object? value)
            ? Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
            : string.Empty;
    }

    private static bool TryGetCellRawValue(object item, string propName, out object? value)
    {
        if (item is IDataGridValueProvider provider)
        {
            return provider.TryGetDataGridValue(propName, out value);
        }

        DataGridValueAccessor accessor = GetValueAccessor(item.GetType(), propName);
        if (!accessor.CanRead)
        {
            value = null;
            return false;
        }

        value = accessor.Get(item);
        return true;
    }

    private static Type? GetCellValueType(object item, string propName)
    {
        if (item is IDataGridValueProvider provider)
        {
            return provider.GetDataGridValueType(propName);
        }

        DataGridValueAccessor accessor = GetValueAccessor(item.GetType(), propName);
        return accessor.ValueType;
    }

    private static bool TryGetWritableCellValueType(object item, string propName, out Type valueType)
    {
        Type? providerType = item is IDataGridValueProvider provider
            ? provider.GetDataGridValueType(propName)
            : null;
        if (providerType != null)
        {
            valueType = providerType;
            return true;
        }

        DataGridValueAccessor accessor = GetValueAccessor(item.GetType(), propName);
        if (accessor.CanWrite && accessor.ValueType != null)
        {
            valueType = accessor.ValueType;
            return true;
        }

        valueType = typeof(string);
        return false;
    }

    private static string? ResolveWritableCellPropertyName(object item, string preferredPropertyName)
    {
        if (TryGetWritableCellValueType(item, preferredPropertyName, out _))
        {
            return preferredPropertyName;
        }

        return !preferredPropertyName.Equals("Value", StringComparison.Ordinal)
            && TryGetWritableCellValueType(item, "Value", out _)
                ? "Value"
                : null;
    }

    private static bool TrySetCellRawValue(object item, string propName, object? value)
    {
        if (item is IDataGridValueProvider provider)
        {
            return provider.TrySetDataGridValue(propName, value);
        }

        DataGridValueAccessor accessor = GetValueAccessor(item.GetType(), propName);
        if (!accessor.CanWrite)
        {
            return false;
        }

        accessor.Set(item, value);
        return true;
    }

    private static bool TrySetEditedCellValue(object item, string propName, string text)
    {
        if (!TryGetWritableCellValueType(item, propName, out Type valueType) ||
            !TryConvertEditedValue(text, valueType, out object? convertedValue))
        {
            return false;
        }

        return TrySetCellRawValue(item, propName, convertedValue);
    }

    private static bool TryConvertEditedValue(string text, Type valueType, out object? value)
    {
        Type targetType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (targetType == typeof(string))
        {
            value = text;
            return true;
        }

        if (targetType == typeof(bool))
        {
            if (bool.TryParse(text, out bool boolValue))
            {
                value = boolValue;
                return true;
            }

            value = null;
            return false;
        }

        if (targetType == typeof(double))
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double doubleValue) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleValue))
            {
                value = doubleValue;
                return true;
            }

            value = null;
            return false;
        }

        if (targetType == typeof(float))
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out float floatValue) ||
                float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out floatValue))
            {
                value = floatValue;
                return true;
            }

            value = null;
            return false;
        }

        if (targetType == typeof(int))
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int intValue) ||
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
            {
                value = intValue;
                return true;
            }

            value = null;
            return false;
        }

        if (targetType.IsEnum)
        {
            try
            {
                value = Enum.Parse(targetType, text, true);
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        try
        {
            value = Convert.ChangeType(text, targetType, CultureInfo.CurrentCulture);
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static DataGridValueAccessor GetValueAccessor(Type itemType, string propName)
    {
        if (string.IsNullOrWhiteSpace(propName))
        {
            return DataGridValueAccessor.Missing;
        }

        return s_registeredValueAccessors.TryGetValue(new DataGridValueAccessorKey(itemType, propName), out var accessor)
            ? accessor
            : DataGridValueAccessor.Missing;
    }

    private readonly record struct DataGridValueAccessorKey(Type ItemType, string PropertyName);

    private sealed class DataGridValueAccessor
    {
        public static readonly DataGridValueAccessor Missing = new(null, null, null);

        private readonly Func<object, object?>? _getter;
        private readonly Action<object, object?>? _setter;

        public DataGridValueAccessor(Type? valueType, Func<object, object?>? getter, Action<object, object?>? setter)
        {
            ValueType = valueType;
            _getter = getter;
            _setter = setter;
        }

        public Type? ValueType { get; }
        public bool CanRead => _getter != null;
        public bool CanWrite => _setter != null;

        public object? Get(object item)
        {
            return _getter!(item);
        }

        public void Set(object item, object? value)
        {
            _setter!(item, value);
        }
    }

    public void SortItems(DataGridColumn column)
    {
        string prop = column.PropertyName;
        bool asc = column.IsAscending;

        _itemsSource.Sort((x, y) =>
        {
            string valX = GetCellValue(x, prop);
            string valY = GetCellValue(y, prop);

            if (double.TryParse(valX, out double dX) && double.TryParse(valY, out double dY))
            {
                return asc ? dX.CompareTo(dY) : dY.CompareTo(dX);
            }
            return asc ? string.Compare(valX, valY, StringComparison.Ordinal) : string.Compare(valY, valX, StringComparison.Ordinal);
        });

        SortingColumn = column;
        InvalidateRowMeasurements();
        Invalidate();
    }

    public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            float maxScroll = Math.Max(0f, TotalBodyHeight - ViewportHeight);
            if (maxScroll > 0f)
            {
                float line = IsVariableRowHeight ? MinRowHeight : _rowHeight;
                float delta = e.IsPreciseScrolling ? -e.WheelDelta : -e.WheelDelta * line;
                float targetOffset = Math.Clamp(_scrollOffset + delta, 0f, maxScroll);
                if (targetOffset != _scrollOffset)
                {
                    ScrollOffset = targetOffset;
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnPointerWheelChanged(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        Vector2 position = e.GetCurrentPoint(this).Position;
        if (IsEnabled && TryStartScrollbarInteraction(e, position))
        {
            return;
        }

        if (IsEnabled && e.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen)
        {
            ClearPendingTouchAction();
            _pendingTouchPointerId = e.Pointer.PointerId;
            if (position.Y <= _headerHeight)
            {
                _pendingTouchSortColumn = GetColumnIndexAt(position.X);
            }
            else if (position.X < Size.X - 12f)
            {
                _pendingTouchRow = GetRowIndexAt(position.Y);
                _pendingTouchColumn = GetColumnIndexAt(position.X);
            }

            if (_pendingTouchSortColumn >= 0 || _pendingTouchRow >= 0)
            {
                e.Handled = true;
            }
            base.OnPointerPressed(e);
            return;
        }

        if (IsEnabled)
        {
            // Click inside Header: Column Sorting / Resizing
            if (e.Position.Y <= _headerHeight)
            {
                // First check if within 4px of any column separator for resizing
                float runningX = Padding.Left;
                for (int i = 0; i < Columns.Count; i++)
                {
                    float separatorX = runningX + Columns[i].ActualWidth;
                    if (Math.Abs(e.Position.X - separatorX) <= 4f)
                    {
                        _resizingColumnIndex = i;
                        _resizeStartMouseX = e.Position.X;
                        _resizeStartWidth = Columns[i].ActualWidth;
                        InputSystem.CapturePointer(this);
                        e.Handled = true;
                        return;
                    }
                    runningX = separatorX;
                }

                // Otherwise, sorting
                runningX = Padding.Left;
                foreach (var col in Columns)
                {
                    if (e.Position.X >= runningX && e.Position.X <= runningX + col.ActualWidth)
                    {
                        if (SortingColumn == col)
                        {
                            col.IsAscending = !col.IsAscending;
                        }
                        else
                        {
                            col.IsAscending = true;
                        }
                        SortItems(col);
                        e.Handled = true;
                        return;
                    }
                    runningX += col.ActualWidth;
                }
            }
            // Click in Body rows
            else
            {
                int r = GetRowIndexAt(e.Position.Y);
                if (r >= 0 && r < _itemsSource.Count)
                {
                    SelectedIndex = r;

                    float runningX = Padding.Left;
                    int colIndex = -1;
                    foreach (var col in Columns)
                    {
                        if (e.Position.X >= runningX && e.Position.X <= runningX + col.ActualWidth)
                        {
                            colIndex = Columns.IndexOf(col);
                            break;
                        }
                        runningX += col.ActualWidth;
                    }

                    if (colIndex != -1)
                    {
                        DateTime now = DateTime.UtcNow;
                        if ((now - _lastClickTime).TotalMilliseconds < 300 && r == _lastClickRow && colIndex == _lastClickCol)
                        {
                            BeginEdit(r, colIndex);
                        }
                        else
                        {
                            _lastClickTime = now;
                            _lastClickRow = r;
                            _lastClickCol = colIndex;
                        }
                    }

                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_scrollbarPointerId == e.Pointer.PointerId)
        {
            _scrollbarPointerId = 0;
            _isDraggingScroll = false;
            ScrollBarInteraction.ReleasePointer(this, e);
            e.Handled = true;
            Invalidate();
        }

        if (_pendingTouchPointerId == e.Pointer.PointerId)
        {
            Vector2 position = e.GetCurrentPoint(this).Position;
            if (IsEnabled && IsPointerPressed && IsPointerOver)
            {
                if (_pendingTouchSortColumn >= 0 && position.Y <= _headerHeight &&
                    GetColumnIndexAt(position.X) == _pendingTouchSortColumn)
                {
                    SortColumn(_pendingTouchSortColumn);
                    e.Handled = true;
                }
                else if (_pendingTouchRow >= 0 && GetRowIndexAt(position.Y) == _pendingTouchRow &&
                    GetColumnIndexAt(position.X) == _pendingTouchColumn)
                {
                    SelectRow(_pendingTouchRow, _pendingTouchColumn);
                    e.Handled = true;
                }
            }
            ClearPendingTouchAction();
        }
        if (_resizingColumnIndex != -1)
        {
            _resizingColumnIndex = -1;
            InputSystem.ReleasePointerCapture();
        }
        base.OnPointerReleased(e);
    }

    public override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        ClearPendingTouchAction();
        if (_scrollbarPointerId == e.Pointer.PointerId)
        {
            _scrollbarPointerId = 0;
            _isDraggingScroll = false;
            Invalidate();
        }
        _resizingColumnIndex = -1;
        base.OnPointerCanceled(e);
    }

    public override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        if (_scrollbarPointerId == e.Pointer.PointerId)
        {
            _scrollbarPointerId = 0;
            _isDraggingScroll = false;
            Invalidate();
        }
        base.OnPointerCaptureLost(e);
    }

    private bool TryStartScrollbarInteraction(PointerRoutedEventArgs e, Vector2 position)
    {
        if (!ScrollBarInteraction.TryCreateMetrics(
                _headerHeight, ViewportHeight, TotalBodyHeight, ScrollOffset, out var metrics) ||
            !ScrollBarInteraction.IsVerticalTrackHit(position.X, Size.X, e.Pointer.PointerDeviceType) ||
            position.Y < metrics.TrackStart || position.Y > metrics.TrackStart + metrics.TrackLength ||
            !ScrollBarInteraction.CapturePointer(this, e))
        {
            return false;
        }

        _scrollbarPointerId = e.Pointer.PointerId;
        _touchInertiaVelocity = 0f;
        if (ScrollBarInteraction.IsThumbHit(position.Y, metrics, e.Pointer.PointerDeviceType))
        {
            _isDraggingScroll = true;
            _dragStartOffset = ScrollOffset;
            _dragStartMouseY = position.Y;
        }
        else
        {
            _isDraggingScroll = false;
            ScrollOffset = ScrollBarInteraction.ValueFromTrackPress(
                ScrollOffset, position.Y, metrics, ViewportHeight);
        }

        ClearPendingTouchAction();
        _isPointerOverScrollbar = true;
        e.Handled = true;
        Invalidate();
        return true;
    }

    public override void OnManipulationStarted(ManipulationStartedRoutedEventArgs e)
    {
        _touchInertiaVelocity = 0f;
        base.OnManipulationStarted(e);
    }

    public override void OnManipulationDelta(ManipulationDeltaRoutedEventArgs e)
    {
        var oldOffset = ScrollOffset;
        ScrollOffset -= (float)e.Delta.Translation.Y;
        if (oldOffset != ScrollOffset) e.Handled = true;
        base.OnManipulationDelta(e);
    }

    public override void OnManipulationCompleted(ManipulationCompletedRoutedEventArgs e)
    {
        _touchInertiaVelocity = e.IsInertial ? -(float)e.Velocities.Linear.Y : 0f;
        base.OnManipulationCompleted(e);
    }

    protected override void OnUpdateAnimations(float elapsedSeconds)
    {
        base.OnUpdateAnimations(elapsedSeconds);
        if (elapsedSeconds <= 0f || MathF.Abs(_touchInertiaVelocity) < 2f)
        {
            _touchInertiaVelocity = 0f;
            return;
        }

        var oldOffset = ScrollOffset;
        ScrollOffset += _touchInertiaVelocity * elapsedSeconds;
        if (oldOffset == ScrollOffset)
        {
            _touchInertiaVelocity = 0f;
            return;
        }
        _touchInertiaVelocity *= MathF.Pow(0.88f, elapsedSeconds * 60f);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        _hoveredRowIndex = -1;
        _hoveredSeparatorIndex = -1;
        _isPointerOverScrollbar = false;
        base.OnPointerExited(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        Vector2 position = e.ScreenPosition == Vector2.Zero && e.Position != Vector2.Zero
            ? e.Position
            : e.GetCurrentPoint(this).Position;
        if (IsEnabled)
        {
            _isPointerOverScrollbar = TotalBodyHeight > ViewportHeight &&
                ScrollBarInteraction.IsVerticalTrackHit(position.X, Size.X, e.Pointer.PointerDeviceType);

            if (_resizingColumnIndex != -1)
            {
                float deltaX = position.X - _resizeStartMouseX;
                float newWidth = Math.Max(20f, _resizeStartWidth + deltaX);
                Columns[_resizingColumnIndex].Width = newWidth;

                if (_editingRow != -1)
                {
                    UpdateCellEditorLayout();
                }

                InvalidateMeasure();
                Invalidate();
                e.Handled = true;
                return;
            }

            // Detect mouse movement in column headers for resize zones
            int hoveredSep = -1;
            if (position.Y <= _headerHeight)
            {
                float runningX = Padding.Left;
                for (int i = 0; i < Columns.Count; i++)
                {
                    float separatorX = runningX + Columns[i].ActualWidth;
                    if (Math.Abs(position.X - separatorX) <= 4f)
                    {
                        hoveredSep = i;
                        break;
                    }
                    runningX = separatorX;
                }
            }

            if (_hoveredSeparatorIndex != hoveredSep)
            {
                _hoveredSeparatorIndex = hoveredSep;
                Invalidate();
            }

            if (position.Y > _headerHeight &&
                !ScrollBarInteraction.IsVerticalTrackHit(position.X, Size.X, e.Pointer.PointerDeviceType))
            {
                int r = GetRowIndexAt(position.Y);
                if (r >= 0 && r < _itemsSource.Count)
                {
                    if (_hoveredRowIndex != r)
                    {
                        _hoveredRowIndex = r;
                        Invalidate();
                    }
                }
                else
                {
                    if (_hoveredRowIndex != -1)
                    {
                        _hoveredRowIndex = -1;
                        Invalidate();
                    }
                }
            }
            else
            {
                if (_hoveredRowIndex != -1)
                {
                    _hoveredRowIndex = -1;
                    Invalidate();
                }
            }
        }

        if (_isDraggingScroll && _scrollbarPointerId == e.Pointer.PointerId && IsEnabled)
        {
            if (ScrollBarInteraction.TryCreateMetrics(
                    _headerHeight, ViewportHeight, TotalBodyHeight, _dragStartOffset, out var metrics))
            {
                ScrollOffset = ScrollBarInteraction.ValueFromDrag(
                    _dragStartOffset, position.Y - _dragStartMouseY, metrics);
            }
            e.Handled = true;
            return;
        }
        base.OnPointerMoved(e);
    }

    private int GetRowIndexAt(float y)
    {
        if (y <= _headerHeight) return -1;
        float offset = y - _headerHeight + ScrollOffset;
        int row = IsVariableRowHeight
            ? EnsureRowSizeIndex().GetIndexAtOffset(offset)
            : (int)(offset / _rowHeight);
        return row >= 0 && row < _itemsSource.Count ? row : -1;
    }

    private int GetColumnIndexAt(float x)
    {
        var runningX = Padding.Left;
        for (var index = 0; index < Columns.Count; index++)
        {
            if (x >= runningX && x <= runningX + Columns[index].ActualWidth) return index;
            runningX += Columns[index].ActualWidth;
        }
        return -1;
    }

    private Rect LogicalToPhysical(Rect rect) =>
        FlowDirection == FlowDirection.RightToLeft
            ? new Rect(Size.X - rect.Right, rect.Y, rect.Width, rect.Height)
            : rect;

    private float LogicalToPhysicalX(float x) =>
        FlowDirection == FlowDirection.RightToLeft ? Size.X - x : x;

    private TextShapingOptions GetTextShapingOptions() =>
        TextShapingOptions.Default.WithDirection(
            FlowDirection == FlowDirection.RightToLeft
                ? ProGPU.Text.Shaping.ShapingDirection.RightToLeft
                : ProGPU.Text.Shaping.ShapingDirection.LeftToRight);

    private void SortColumn(int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= Columns.Count) return;
        var column = Columns[columnIndex];
        if (SortingColumn == column) column.IsAscending = !column.IsAscending;
        else column.IsAscending = true;
        SortItems(column);
    }

    private void SelectRow(int row, int columnIndex)
    {
        if (row < 0 || row >= _itemsSource.Count) return;
        SelectedIndex = row;
        if (columnIndex < 0) return;

        var now = DateTime.UtcNow;
        if ((now - _lastClickTime).TotalMilliseconds < 300 && row == _lastClickRow && columnIndex == _lastClickCol)
        {
            BeginEdit(row, columnIndex);
        }
        else
        {
            _lastClickTime = now;
            _lastClickRow = row;
            _lastClickCol = columnIndex;
        }
    }

    private void ClearPendingTouchAction()
    {
        _pendingTouchPointerId = 0;
        _pendingTouchRow = -1;
        _pendingTouchColumn = -1;
        _pendingTouchSortColumn = -1;
    }

    private float MeasureTextWidth(string text, TtfFont font, float fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float width = 0f;
        foreach (char c in text)
        {
            ushort gIdx = font.GetGlyphIndex(c);
            width += font.GetAdvanceWidth(gIdx, fontSize);
        }
        return width;
    }

    private void ResolveColumnWidths(float availableWidth)
    {
        if (Columns.Count == 0) return;

        var activeFont = GetActiveFont();
        if (activeFont == null) return;

        float totalStars = 0f;
        float allocatedWidth = 0f;
        var starColumns = new List<DataGridColumn>();

        // 1. First pass: Resolve Pixel and Auto columns
        for (int i = 0; i < Columns.Count; i++)
        {
            var col = Columns[i];
            if (col.Width.IsPixel)
            {
                col.ActualWidth = Math.Max(20f, col.Width.Value);
                allocatedWidth += col.ActualWidth;
            }
            else if (col.Width.IsAuto)
            {
                // Measure header text
                float minWidth = MeasureTextWidth(col.Header, activeFont, FontSize) + 24f; // padding + indicator space

                // High-performance Auto sizing: measure up to 100 sample items
                float maxCellW = 0f;
                int sampleCount = Math.Min(100, _itemsSource.Count);
                for (int j = 0; j < sampleCount; j++)
                {
                    var item = _itemsSource[j];
                    string val = GetCellValue(item, col.PropertyName);
                    if (!string.IsNullOrEmpty(val))
                    {
                        float cellW = MeasureTextWidth(val, activeFont, FontSize) + 20f; // 10px padding on each side
                        maxCellW = Math.Max(maxCellW, cellW);
                    }
                }

                col.ActualWidth = Math.Max(minWidth, maxCellW);
                allocatedWidth += col.ActualWidth;
            }
            else if (col.Width.IsStar)
            {
                totalStars += col.Width.Value;
                starColumns.Add(col);
            }
        }

        // 2. Second pass: Distribute remaining space to Star columns
        if (starColumns.Count > 0)
        {
            float remainingWidth = Math.Max(0f, availableWidth - allocatedWidth);
            if (remainingWidth > 0f && totalStars > 0f)
            {
                float extraPerStar = remainingWidth / totalStars;
                foreach (var col in starColumns)
                {
                    col.ActualWidth = Math.Max(30f, col.Width.Value * extraPerStar);
                }
            }
            else
            {
                foreach (var col in starColumns)
                {
                    col.ActualWidth = 50f;
                }
            }
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (_cellEditor != null)
        {
            _cellEditor.Measure(availableSize);
        }
        float w = WidthConstraint ?? availableSize.X;
        float h = HeightConstraint ?? availableSize.Y;
        if (float.IsInfinity(w)) w = 500f;
        if (float.IsInfinity(h)) h = 300f;

        ResolveColumnWidths(w);

        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        ResolveColumnWidths(arrangeRect.Width);
        UpdateRowLayoutSignature();
        UpdateCellEditorLayout();
    }

    public override void OnRender(DrawingContext context)
    {
        var activeFont = GetActiveFont();
        if (activeFont == null) return;

        // 1. Draw DataGrid outer card background & border
        Pen outerPen = IsKeyboardFocusVisualVisible
            ? new Pen(BorderBrush ?? ThemeManager.GetBrush("SystemAccentColor"), 2f) // Glowing Segoe Blue active focus ring
            : new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), 1f); // Thin outline

        var bg = Background ?? ThemeManager.GetBrush("CardBackground");
        context.DrawRectangle(bg, outerPen, new Rect(Vector2.Zero, Size));

        // 2. Draw Column Headers
        float runningX = Padding.Left;
        Brush headerBg = ThemeManager.GetBrush("HeaderBackground"); // Fluent Header plate
        Pen colBorder = new Pen(ThemeManager.GetBrush("ControlBorder"), 1f);

        context.DrawRectangle(headerBg, null, new Rect(0, 0, Size.X, _headerHeight));

        for (int i = 0; i < Columns.Count; i++)
        {
            var col = Columns[i];
            Rect logicalColRect = new Rect(runningX, 0, col.ActualWidth, _headerHeight);
            Rect colRect = LogicalToPhysical(logicalColRect);
            context.DrawRectangle(null, colBorder, colRect);

            // Draw Header Text
            float textY = (_headerHeight - FontSize) / 2f;
            Rect headerTextBounds = LogicalToPhysical(new Rect(
                runningX + 8f,
                textY,
                Math.Max(0f, col.ActualWidth - 16f),
                FontSize));
            context.DrawText(
                col.Header,
                activeFont,
                FontSize,
                ThemeManager.GetBrush("TextPrimary"),
                new Vector2(headerTextBounds.X, textY),
                Matrix4x4.Identity,
                headerTextBounds,
                textShapingOptions: GetTextShapingOptions(),
                textAlignment: FlowDirection == FlowDirection.RightToLeft
                    ? ProGPU.Text.TextAlignment.Right
                    : ProGPU.Text.TextAlignment.Left);

            // Draw Sorting indicator if active sorting
            if (SortingColumn == col)
            {
                float headerTextW = col.Header.Length * (FontSize * 0.6f); // approximate width
                float sortX = LogicalToPhysicalX(runningX + 12f + headerTextW);
                float sortY = _headerHeight * 0.5f;
                float direction = col.IsAscending ? -1f : 1f;
                var sortPen = new Pen(ThemeManager.GetBrush("SystemAccentColor"), 1.5f);
                context.DrawLine(sortPen, new Vector2(sortX - 3f, sortY - direction * 1.5f), new Vector2(sortX, sortY + direction * 1.5f));
                context.DrawLine(sortPen, new Vector2(sortX, sortY + direction * 1.5f), new Vector2(sortX + 3f, sortY - direction * 1.5f));
            }

            // Draw highlight if this separator is hovered or being resized
            if (i == _hoveredSeparatorIndex || i == _resizingColumnIndex)
            {
                float separatorX = runningX + col.ActualWidth;
                context.DrawRectangle(
                    ThemeManager.GetBrush("SystemAccentColor"),
                    null,
                    LogicalToPhysical(new Rect(separatorX - 1f, 0f, 2f, _headerHeight)));
            }

            runningX += col.ActualWidth;
        }

        // 3. Draw Body Row Cells (Virtualized recycling viewport loop)
        if (_itemsSource.Count > 0)
        {
            VariableSizeIndex? rowSizes = IsVariableRowHeight ? EnsureRowSizeIndex() : null;
            int startRow = rowSizes?.GetIndexAtOffset(ScrollOffset) ??
                (int)Math.Floor(ScrollOffset / _rowHeight);
            int endRow = rowSizes?.GetIndexAtOffset(ScrollOffset + ViewportHeight) ??
                (int)Math.Ceiling((ScrollOffset + ViewportHeight) / _rowHeight);

            startRow = Math.Clamp(startRow, 0, _itemsSource.Count - 1);
            endRow = Math.Clamp(endRow, 0, _itemsSource.Count - 1);

            context.PushClip(new Rect(0, _headerHeight, Size.X, ViewportHeight));

            for (int r = startRow; r <= endRow; r++)
            {
                float currentRowHeight = ResolveRowHeight(r, activeFont);
                float rowY = _headerHeight + (rowSizes?.GetOffset(r) ?? r * _rowHeight) - ScrollOffset;
                var item = _itemsSource[r];

                // Alternate, Hover & Selection backgrounds
                Brush? rowBg = null;
                if (r == SelectedIndex)
                {
                    rowBg = ThemeManager.GetBrush("SelectionHighlight"); // Premium selection
                }
                else if (r == _hoveredRowIndex)
                {
                    rowBg = ThemeManager.GetBrush("ControlBackgroundHover"); // Hover state row highlight
                }
                else if (r % 2 == 1)
                {
                    rowBg = ThemeManager.GetBrush("ControlBackground"); // Subtle alternate rows
                }

                Rect rowRect = new Rect(0, rowY, Size.X, currentRowHeight);
                if (rowBg != null)
                {
                    context.DrawRectangle(rowBg, null, rowRect);
                }

                // Draw active selection vertical indicator stripe on far-left
                if (r == SelectedIndex)
                {
                    Rect selectionStripe = LogicalToPhysical(new Rect(0f, rowY + 2f, 3f, currentRowHeight - 4f));
                    context.DrawRectangle(ThemeManager.GetBrush("SystemAccentColor"), null, selectionStripe);
                }

                // Draw cell text grid columns
                float colX = Padding.Left;
                for (int c = 0; c < Columns.Count; c++)
                {
                    var col = Columns[c];
                    float colWidth = col.ActualWidth;

                    if (r == _editingRow && c == _editingCol)
                    {
                        // Do not draw text under editor
                    }
                    else
                    {
                        string val = GetCellValue(item, col.PropertyName);
                        TextWrapping wrapping = col.TextWrapping ?? CellTextWrapping;
                        float cellTextY = wrapping == TextWrapping.NoWrap
                            ? rowY + Math.Max(4f, (currentRowHeight - FontSize) * 0.5f)
                            : rowY + 4f;
                        Rect cellTextBounds = LogicalToPhysical(new Rect(
                            colX + 8f,
                            cellTextY,
                            wrapping == TextWrapping.NoWrap ? 10000f : Math.Max(0f, colWidth - 16f),
                            Math.Max(FontSize, currentRowHeight - 8f)));
                        context.PushClip(LogicalToPhysical(new Rect(colX, rowY, colWidth, currentRowHeight)));
                        context.DrawText(
                            val,
                            activeFont,
                            FontSize,
                            ThemeManager.GetBrush("TextPrimary"),
                            new Vector2(cellTextBounds.X, cellTextY),
                            Matrix4x4.Identity,
                            cellTextBounds,
                            textShapingOptions: GetTextShapingOptions(),
                            textAlignment: wrapping != TextWrapping.NoWrap && FlowDirection == FlowDirection.RightToLeft
                                ? ProGPU.Text.TextAlignment.Right
                                : ProGPU.Text.TextAlignment.Left);
                        context.PopClip();
                    }
                    colX += colWidth;
                }

                // Draw thin grid lines
                context.DrawRectangle(null, new Pen(ThemeManager.GetBrush("ControlBorder"), 0.5f), new Rect(0, rowY, Size.X, currentRowHeight));

                if (rowSizes != null && r == endRow && r + 1 < _itemsSource.Count &&
                    rowY + currentRowHeight < _headerHeight + ViewportHeight)
                {
                    endRow++;
                }
            }

            context.PopClip();
        }

        // 4. Draw Scrollbar track
        if (TotalBodyHeight > ViewportHeight)
        {
            float scrollbarWidth = (_isPointerOverScrollbar || _isDraggingScroll) ? 8f : 3f;
            float padding = (_isPointerOverScrollbar || _isDraggingScroll) ? 2f : 4f;
            float viewportH = ViewportHeight;
            float thumbHeight = Math.Max(24f, (viewportH / TotalBodyHeight) * viewportH);
            float scrollableHeight = TotalBodyHeight - viewportH;
            float thumbY = _headerHeight + (ScrollOffset / scrollableHeight) * (viewportH - thumbHeight);

            float scrollbarX = FlowDirection == FlowDirection.RightToLeft
                ? padding
                : Size.X - scrollbarWidth - padding;
            Rect trackRect = new Rect(scrollbarX, _headerHeight, scrollbarWidth, viewportH);
            Rect thumbRect = new Rect(scrollbarX, thumbY, scrollbarWidth, thumbHeight);

            // Draw track (subtle translucent backdrop line)
            Brush trackBg = (_isPointerOverScrollbar || _isDraggingScroll) 
                ? ThemeManager.GetBrush("ControlBackgroundHover") 
                : ThemeManager.GetBrush("ControlBackground");
            context.DrawRectangle(trackBg, null, trackRect);

            // Draw thumb (glassmorphic capsule)
            Brush thumbBg = (_isPointerOverScrollbar || _isDraggingScroll)
                ? ThemeManager.GetBrush("ScrollbarThumbHover")
                : ThemeManager.GetBrush("ScrollbarThumb");
            
            context.DrawRoundedRectangle(thumbBg, null, thumbRect, scrollbarWidth / 2f);
        }
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            if (e.Key == Key.Enter)
            {
                if (SelectedIndex >= 0 && SelectedIndex < _itemsSource.Count && _editingRow == -1)
                {
                    BeginEdit(SelectedIndex, 0);
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnKeyDown(e);
    }

    public void BeginEdit(int row, int col)
    {
        if (row < 0 || row >= _itemsSource.Count || col < 0 || col >= Columns.Count)
            return;

        _editingRow = row;
        _editingCol = col;

        var item = _itemsSource[row];
        var column = Columns[col];
        string val = GetCellValue(item, column.PropertyName);

        if (_cellEditor != null)
        {
            RemoveChild(_cellEditor);
            _cellEditor = null;
        }

        Type cellType = typeof(string);
        if (TryGetCellRawValue(item, "PropertyType", out object? propertyTypeValue) &&
            propertyTypeValue is Type providerType)
        {
            cellType = providerType;
        }
        else
        {
            cellType = GetCellValueType(item, column.PropertyName) ?? typeof(string);
        }

        if (cellType == typeof(bool))
        {
            var cb = new CheckBox
            {
                IsChecked = val.Equals("True", StringComparison.OrdinalIgnoreCase),
                Font = GetActiveFont()
            };
            cb.Checked += (s, ev) => { CommitValue("True"); };
            cb.Unchecked += (s, ev) => { CommitValue("False"); };
            _cellEditor = cb;
        }
        else if (cellType.IsEnum)
        {
            var combo = new ComboBox
            {
                Font = GetActiveFont(),
                FontSize = FontSize,
                CornerRadius = 0f
            };
            foreach (var name in Enum.GetNames(cellType))
            {
                combo.Items.Add(new ComboBoxItem { Text = name });
            }
            foreach (var itemNode in combo.Items)
            {
                if (itemNode is ComboBoxItem selectedComboItem &&
                    selectedComboItem.Text.Equals(val, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = selectedComboItem;
                    break;
                }
            }
            combo.SelectionChanged += (s, ev) =>
            {
                if (combo.SelectedItem != null)
                {
                    CommitValue(((ComboBoxItem)combo.SelectedItem).Text);
                }
            };
            _cellEditor = combo;
        }
        else if (cellType == typeof(Brush) || cellType == typeof(Vector4))
        {
            _cellEditor = new BrushCellEditor(this, val);
        }
        else
        {
            var tb = new CellEditorTextBox(this)
            {
                Text = val,
                Font = GetActiveFont(),
                FontSize = FontSize,
                Padding = new Thickness(8f, 0f, 8f, 0f),
                CornerRadius = 0f
            };
            _cellEditor = tb;
        }

        if (_cellEditor.Parent == null)
        {
            AddChild(_cellEditor);
        }

        UpdateCellEditorLayout();
        Invalidate();

        InputSystem.SetFocus(_cellEditor);
        if (_cellEditor is CellEditorTextBox cet)
        {
            cet.CaretIndex = val.Length;
        }
    }

    public void CommitValue(string val)
    {
        if (_editingRow != -1 && _cellEditor != null)
        {
            if (_cellEditor is TextBox tb) tb.Text = val;
            else if (_cellEditor is CheckBox cb) cb.IsChecked = val.Equals("True", StringComparison.OrdinalIgnoreCase);
            else if (_cellEditor is ComboBox combo)
            {
                foreach (var item in combo.Items)
                {
                    if (item is ComboBoxItem comboItem &&
                        comboItem.Text.Equals(val, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedItem = comboItem;
                        break;
                    }
                }
            }
            CommitEdit();
        }
    }

    public void CommitEdit()
    {
        if (_editingRow != -1 && _cellEditor != null)
        {
            int row = _editingRow;
            int col = _editingCol;
            var item = _itemsSource[row];
            var column = Columns[col];

            string newValueText = "";
            if (_cellEditor is TextBox tb)
            {
                newValueText = tb.Text;
            }
            else if (_cellEditor is CheckBox cb)
            {
                newValueText = cb.IsChecked.ToString();
            }
            else if (_cellEditor is ComboBox combo)
            {
                newValueText = (combo.SelectedItem as ComboBoxItem)?.Text ?? "";
            }
            else if (_cellEditor is BrushCellEditor bce)
            {
                newValueText = bce.Value;
            }

            _editingRow = -1;
            _editingCol = -1;

            _cellEditor.WidthConstraint = 0f;
            _cellEditor.HeightConstraint = 0f;
            _cellEditor.Measure(new Vector2(0, 0));
            _cellEditor.Arrange(new Rect(0, 0, 0, 0));
            RemoveChild(_cellEditor);

            if (InputSystem.FocusedElement == _cellEditor)
            {
                InputSystem.SetFocus(this);
            }

            try
            {
                string? propertyName = ResolveWritableCellPropertyName(item, column.PropertyName);
                if (propertyName != null)
                {
                    TrySetEditedCellValue(item, propertyName, newValueText);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error committing edit: {ex.Message}");
            }

            if (IsVariableRowHeight &&
                _indexedRowCount == _itemsSource.Count &&
                _indexedRowLayoutSignature == _rowLayoutSignature)
            {
                _rowSizes.InvalidateMeasurement(row);
            }

            Invalidate();
        }
    }

    public void CancelEdit()
    {
        if (_editingRow != -1)
        {
            _editingRow = -1;
            _editingCol = -1;

            if (_cellEditor != null)
            {
                _cellEditor.WidthConstraint = 0f;
                _cellEditor.HeightConstraint = 0f;
                _cellEditor.Measure(new Vector2(0, 0));
                _cellEditor.Arrange(new Rect(0, 0, 0, 0));
                RemoveChild(_cellEditor);

                if (InputSystem.FocusedElement == _cellEditor)
                {
                    InputSystem.SetFocus(this);
                }
            }

            Invalidate();
        }
    }

    private void UpdateCellEditorLayout()
    {
        if (_cellEditor == null) return;

        if (_editingRow != -1)
        {
            float editorRowHeight = IsVariableRowHeight
                ? ResolveRowHeight(_editingRow, GetActiveFont()!)
                : _rowHeight;
            float rowY = _headerHeight +
                (IsVariableRowHeight ? EnsureRowSizeIndex().GetOffset(_editingRow) : _editingRow * _rowHeight) -
                _scrollOffset;
            float colX = Padding.Left;
            for (int i = 0; i < _editingCol; i++)
            {
                colX += Columns[i].ActualWidth;
            }
            float colWidth = Columns[_editingCol].ActualWidth;

            if (rowY + editorRowHeight <= _headerHeight || rowY >= Size.Y)
            {
                _cellEditor.WidthConstraint = 0f;
                _cellEditor.HeightConstraint = 0f;
                _cellEditor.Measure(new Vector2(0, 0));
                _cellEditor.Arrange(new Rect(0, 0, 0, 0));
                _cellEditor.ClipBounds = null;
            }
            else
            {
                _cellEditor.WidthConstraint = colWidth;
                _cellEditor.HeightConstraint = editorRowHeight;
                _cellEditor.Measure(new Vector2(colWidth, editorRowHeight));
                _cellEditor.Arrange(new Rect(colX, rowY, colWidth, editorRowHeight));

                if (rowY < _headerHeight)
                {
                    float clipY = _headerHeight - rowY;
                    _cellEditor.ClipBounds = new Rect(0f, clipY, colWidth, editorRowHeight - clipY);
                }
                else if (rowY + editorRowHeight > Size.Y)
                {
                    float clipH = Size.Y - rowY;
                    _cellEditor.ClipBounds = new Rect(0f, 0f, colWidth, clipH);
                }
                else
                {
                    _cellEditor.ClipBounds = null;
                }
            }
        }
        else
        {
            _cellEditor.WidthConstraint = 0f;
            _cellEditor.HeightConstraint = 0f;
            _cellEditor.Measure(new Vector2(0, 0));
            _cellEditor.Arrange(new Rect(0, 0, 0, 0));
            _cellEditor.ClipBounds = null;
        }
    }

    private bool IsVariableRowHeight => float.IsNaN(_rowHeight);

    private void UpdateRowLayoutSignature()
    {
        var hash = new HashCode();
        hash.Add(FontSize);
        hash.Add(MinRowHeight);
        hash.Add(CellTextWrapping);
        hash.Add(GetActiveFont());
        hash.Add(Columns.Count);
        foreach (DataGridColumn column in Columns)
        {
            hash.Add(column.ActualWidth);
            hash.Add(column.TextWrapping);
        }

        int signature = hash.ToHashCode();
        if (_rowLayoutSignature == signature) return;
        _rowLayoutSignature = signature;
        InvalidateRowMeasurements();
    }

    private void InvalidateRowMeasurements()
    {
        _indexedRowCount = -1;
        _indexedRowLayoutSignature = -1;
    }

    private VariableSizeIndex EnsureRowSizeIndex()
    {
        if (_indexedRowCount != _itemsSource.Count ||
            _indexedRowLayoutSignature != _rowLayoutSignature)
        {
            _rowSizes.Reset(_itemsSource.Count, Math.Max(MinRowHeight, EstimatedRowHeight));
            _indexedRowCount = _itemsSource.Count;
            _indexedRowLayoutSignature = _rowLayoutSignature;
        }
        return _rowSizes;
    }

    private float ResolveRowHeight(int row, TtfFont activeFont)
    {
        if (!IsVariableRowHeight) return _rowHeight;
        VariableSizeIndex sizes = EnsureRowSizeIndex();
        if (sizes.IsMeasured(row)) return sizes.GetSize(row);

        float height = MinRowHeight;
        object item = _itemsSource[row];
        foreach (DataGridColumn column in Columns)
        {
            string value = GetCellValue(item, column.PropertyName);
            if (string.IsNullOrEmpty(value)) continue;
            TextWrapping wrapping = column.TextWrapping ?? CellTextWrapping;
            float layoutWidth = wrapping == TextWrapping.NoWrap
                ? float.PositiveInfinity
                : Math.Max(1f, column.ActualWidth - 16f);
            var layout = new TextLayout(
                value,
                activeFont,
                FontSize,
                layoutWidth,
                FlowDirection == FlowDirection.RightToLeft
                    ? ProGPU.Text.TextAlignment.Right
                    : ProGPU.Text.TextAlignment.Left,
                shapingOptions: GetTextShapingOptions());
            height = Math.Max(height, layout.ContentSize.Y + 8f);
        }

        sizes.SetMeasuredSize(row, height);
        return height;
    }

    private class CellEditorTextBox : TextBox
    {
        private readonly DataGrid _owner;

        public CellEditorTextBox(DataGrid owner)
        {
            _owner = owner;
        }

        public override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _owner.CommitEdit();
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.Escape)
            {
                _owner.CancelEdit();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        public override void OnVisualStateChanged()
        {
            base.OnVisualStateChanged();
            // TextBox construction can apply its default style and invoke this virtual
            // callback before this derived constructor assigns the owner.
            if (!IsFocused && _owner is { _editingRow: not -1 } owner)
            {
                owner.CancelEdit();
            }
        }
    }

    private class BrushCellEditor : Grid
    {
        private readonly DataGrid _owner;
        private readonly TextBox _textBox;
        private readonly Button _colorBtn;
        private Border? _pickerPopup;

        public BrushCellEditor(DataGrid owner, string initialVal)
        {
            _owner = owner;
            ColumnDefinitions.Add(GridLength.Star(1f));
            ColumnDefinitions.Add(new GridLength(24f, GridUnitType.Absolute));

            _textBox = new TextBox
            {
                Text = initialVal,
                Font = owner.GetActiveFont(),
                FontSize = owner.FontSize,
                Padding = new Thickness(4f, 0f, 4f, 0f),
                CornerRadius = 0f
            };
            _textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    _owner.CommitEdit();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    _owner.CancelEdit();
                    e.Handled = true;
                }
            };
            Grid.SetColumn(_textBox, 0);
            AddChild(_textBox);

            _colorBtn = new Button
            {
                WidthConstraint = 20f,
                HeightConstraint = 20f,
                CornerRadius = 2f,
                Margin = new Thickness(2),
                Background = GetBrushFromText(initialVal)
            };
            _colorBtn.Click += (s, e) => { ShowColorPickerPopup(); };
            Grid.SetColumn(_colorBtn, 1);
            AddChild(_colorBtn);

            _textBox.TextChanged += (s, e) =>
            {
                var val = _textBox.Text;
                _colorBtn.Background = GetBrushFromText(val);
                UpdateLiveValue(val);
            };
        }

        public string Value => _textBox.Text;

        private Brush GetBrushFromText(string txt)
        {
            if (string.IsNullOrEmpty(txt)) return new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f));
            if (txt.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f));
            }
            if (txt.StartsWith("#"))
            {
                try
                {
                    var hex = txt.Substring(1);
                    if (hex.Length == 6) hex = "FF" + hex;
                    if (hex.Length == 8)
                    {
                        uint rgba = Convert.ToUInt32(hex, 16);
                        float a = ((rgba >> 24) & 0xFF) / 255.0f;
                        float r = ((rgba >> 16) & 0xFF) / 255.0f;
                        float g = ((rgba >> 8) & 0xFF) / 255.0f;
                        float b = (rgba & 0xFF) / 255.0f;
                        return new SolidColorBrush(new Vector4(r, g, b, a));
                    }
                }
                catch {}
            }
            return new ThemeResourceBrush(txt);
        }

        private void UpdateLiveValue(string val)
        {
            if (_owner._editingRow != -1)
            {
                var item = _owner._itemsSource[_owner._editingRow];
                var column = _owner.Columns[_owner._editingCol];
                string? propertyName = ResolveWritableCellPropertyName(item, column.PropertyName);
                if (propertyName != null &&
                    TryGetWritableCellValueType(item, propertyName, out Type valueType))
                {
                    if (valueType == typeof(Brush) || typeof(Brush).IsAssignableFrom(valueType))
                    {
                        TrySetCellRawValue(item, propertyName, GetBrushFromText(val));
                    }
                    else if (valueType == typeof(Vector4))
                    {
                        var brush = GetBrushFromText(val);
                        if (brush is SolidColorBrush scb)
                        {
                            TrySetCellRawValue(item, propertyName, scb.Color);
                        }
                    }
                    else if (valueType == typeof(string))
                    {
                        TrySetCellRawValue(item, propertyName, val);
                    }
                    else
                    {
                        if (!TrySetEditedCellValue(item, propertyName, val))
                        {
                            TrySetCellRawValue(item, propertyName, val);
                        }
                    }
                }
                // Force redraw of the designer workspace
                _owner.Invalidate();
            }
        }

        private void ShowColorPickerPopup()
        {
            if (_pickerPopup == null)
            {
                var cp = new ColorPicker
                {
                    WidthConstraint = 280f,
                    HeightConstraint = 330f
                };

                // Extract vector4 color from TextBox value
                var currentBrush = GetBrushFromText(_textBox.Text);
                if (currentBrush is SolidColorBrush scb)
                {
                    cp.Color = scb.Color;
                }
                else
                {
                    cp.Color = new Vector4(1f, 1f, 1f, 1f); // default white
                }

                cp.ColorChanged += (s, e) =>
                {
                    var newCol = e.NewColor;
                    byte r = (byte)Math.Clamp(Math.Round(newCol.X * 255f), 0, 255);
                    byte g = (byte)Math.Clamp(Math.Round(newCol.Y * 255f), 0, 255);
                    byte b = (byte)Math.Clamp(Math.Round(newCol.Z * 255f), 0, 255);
                    byte a = (byte)Math.Clamp(Math.Round(newCol.W * 255f), 0, 255);
                    
                    string hex = $"#{a:X2}{r:X2}{g:X2}{b:X2}";
                    if (a == 0 && r == 0 && g == 0 && b == 0) hex = "Transparent";

                    _textBox.Text = hex;
                    _colorBtn.Background = new SolidColorBrush(newCol);

                    // Update the value on the selected designer element in real-time!
                    UpdateLiveValue(hex);
                };

                _pickerPopup = new Border
                {
                    Background = new ThemeResourceBrush("CardBackground"),
                    BorderBrush = new ThemeResourceBrush("ControlBorder"),
                    BorderThickness = new Thickness(1f),
                    CornerRadius = 8f,
                    Padding = new Thickness(10),
                    Child = cp
                };
            }

            Vector2 absPos = Offset;
            Visual? current = Parent;
            while (current != null)
            {
                absPos += current.Offset;
                current = current.Parent;
            }

            _pickerPopup.Width = 300f;
            _pickerPopup.Height = 350f;
            // Force theme synchronization right before showing the popup
            _pickerPopup.NotifyThemeChanged();
            // Place it nicely (shifted to the left so it doesn't clip off the right edge of property grid)
            PopupService.ShowPopup(_pickerPopup, new Vector2(absPos.X - 260f, absPos.Y + Size.Y + 2f), _colorBtn);
        }
    }
}
