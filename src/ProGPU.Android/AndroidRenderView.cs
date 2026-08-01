using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Microsoft.UI.Xaml.Input;
using System.Numerics;
using Windows.Devices.Input;

namespace ProGPU.Android;

internal readonly record struct AndroidRenderMetrics(
    uint Width,
    uint Height,
    float DpiScale,
    Microsoft.UI.Xaml.Thickness SafeAreaInsets,
    Windows.Foundation.Rect InputPaneOccludedRect);

internal sealed class AndroidRenderView : SurfaceView, ISurfaceHolderCallback
{
    private const uint IndirectPointerId = uint.MaxValue;
    private const float PinchWheelUnitsPerNaturalLog = 120f;
    private readonly float _horizontalScrollFactor;
    private readonly float _verticalScrollFactor;
    private Microsoft.UI.Xaml.Thickness _safeAreaInsets;
    private Windows.Foundation.Rect _inputPaneOccludedRect;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private Vector2 _lastIndirectPosition;
    private Microsoft.UI.Xaml.FrameworkElement? _externalDragTarget;
    private Microsoft.UI.Xaml.DataPackage? _externalDragData;
    private DragAndDropPermissions? _externalDragPermissions;
    private readonly Activity _activity;

    public AndroidRenderView(Activity activity) : base(activity)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        Holder?.AddCallback(this);
        Focusable = true;
        FocusableInTouchMode = true;
        // Android's default non-touch focus drawable is applied to the entire
        // edge-to-edge SurfaceView after the first mouse or trackpad event. That
        // translucent overlay would alter every presented color, including the
        // content rendered beneath the system bars. ProGPU draws its own focus
        // visuals, so the platform-wide highlight must remain disabled.
        DefaultFocusHighlightEnabled = false;
        // SurfaceView presents through a separate SurfaceFlinger layer below the
        // activity's view layer. An opaque View background here would cover that
        // Vulkan layer even though WebGPU continues to acquire and present frames.
        SetBackgroundColor(Color.Transparent);
        var configuration = ViewConfiguration.Get(activity);
        _horizontalScrollFactor = configuration?.ScaledHorizontalScrollFactor ?? 48f;
        _verticalScrollFactor = configuration?.ScaledVerticalScrollFactor ?? 48f;
    }

    public event Action<Surface, int, int>? SurfaceAvailable;
    public event Action? SurfaceUnavailable;
    public event Action<AndroidRenderMetrics>? MetricsChanged;

    public Func<WindowInputState?>? InputStateProvider { get; set; }

    public bool HasValidSurface => Holder?.Surface is { IsValid: true };

    public AndroidRenderMetrics Metrics => new(
        checked((uint)Math.Max(1, _surfaceWidth > 0 ? _surfaceWidth : Width)),
        checked((uint)Math.Max(1, _surfaceHeight > 0 ? _surfaceHeight : Height)),
        ResolveDensity(),
        _safeAreaInsets,
        _inputPaneOccludedRect);

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        if (holder.Surface is { IsValid: true } surface)
            SurfaceAvailable?.Invoke(surface, Math.Max(1, _surfaceWidth), Math.Max(1, _surfaceHeight));
    }

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
    {
        _surfaceWidth = Math.Max(1, width);
        _surfaceHeight = Math.Max(1, height);
        if (holder.Surface is { IsValid: true } surface)
            SurfaceAvailable?.Invoke(surface, _surfaceWidth, _surfaceHeight);
        NotifyMetricsChanged();
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        _surfaceWidth = 0;
        _surfaceHeight = 0;
        SurfaceUnavailable?.Invoke();
    }

    public void RefreshMetrics()
    {
        RequestApplyInsets();
        NotifyMetricsChanged();
    }

    public override WindowInsets? OnApplyWindowInsets(WindowInsets? insets)
    {
        if (insets == null) return null;
        float density = ResolveDensity();
        int safeTypes = WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout();
        Insets safe = insets.GetInsets(safeTypes);
        _safeAreaInsets = new Microsoft.UI.Xaml.Thickness(
            safe.Left / density,
            safe.Top / density,
            safe.Right / density,
            safe.Bottom / density);

        Insets ime = insets.GetInsets(WindowInsets.Type.Ime());
        bool imeVisible = insets.IsVisible(WindowInsets.Type.Ime());
        float logicalWidth = Math.Max(1, Width) / density;
        float logicalHeight = Math.Max(1, Height) / density;
        float imeHeight = imeVisible ? Math.Clamp(ime.Bottom / density, 0f, logicalHeight) : 0f;
        _inputPaneOccludedRect = imeHeight > 0f
            ? new Windows.Foundation.Rect(0f, logicalHeight - imeHeight, logicalWidth, imeHeight)
            : default;
        NotifyMetricsChanged();
        return insets;
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || !TrySelectInputState()) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                DispatchPointer(e, e.ActionIndex, PointerInputKind.Pressed, inContact: true);
                return true;
            case MotionEventActions.Move:
                for (int index = 0; index < e.PointerCount; index++)
                    DispatchPointer(e, index, PointerInputKind.Moved, inContact: true);
                return true;
            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                DispatchPointer(e, e.ActionIndex, PointerInputKind.Released, inContact: false);
                return true;
            case MotionEventActions.Cancel:
                for (int index = 0; index < e.PointerCount; index++)
                    DispatchPointer(e, index, PointerInputKind.Canceled, inContact: false);
                return true;
            case MotionEventActions.ButtonPress:
                DispatchPointer(e, Math.Max(0, e.ActionIndex), PointerInputKind.Pressed, inContact: true);
                return true;
            case MotionEventActions.ButtonRelease:
                DispatchPointer(e, Math.Max(0, e.ActionIndex), PointerInputKind.Released, inContact: false);
                return true;
            default:
                return base.OnTouchEvent(e);
        }
    }

    public override bool OnHoverEvent(MotionEvent? e)
    {
        if (e == null || !TrySelectInputState()) return false;
        if (e.ActionMasked is not (MotionEventActions.HoverEnter or MotionEventActions.HoverMove or MotionEventActions.HoverExit))
            return base.OnHoverEvent(e);
        DispatchPointer(e, 0, PointerInputKind.Moved, inContact: false);
        return true;
    }

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e == null || !TrySelectInputState()) return false;
        if (e.ActionMasked == MotionEventActions.Scroll)
        {
            DispatchScroll(e);
            return true;
        }
        if (e.ActionMasked is MotionEventActions.HoverEnter or MotionEventActions.HoverMove or MotionEventActions.HoverExit)
        {
            DispatchPointer(e, 0, PointerInputKind.Moved, inContact: false);
            return true;
        }
        if (e.ActionMasked is MotionEventActions.ButtonPress or MotionEventActions.ButtonRelease)
        {
            DispatchPointer(
                e,
                Math.Max(0, e.ActionIndex),
                e.ActionMasked == MotionEventActions.ButtonPress ? PointerInputKind.Pressed : PointerInputKind.Released,
                e.ActionMasked == MotionEventActions.ButtonPress);
            return true;
        }
        return base.OnGenericMotionEvent(e);
    }

    public override bool OnDragEvent(global::Android.Views.DragEvent? e)
    {
        if (e == null || !TrySelectInputState()) return false;
        switch (e.Action)
        {
            case DragAction.Started:
                ReleaseExternalDragPermissions();
                _externalDragData = CreateDataPackage(e.ClipData);
                LeaveExternalDragTarget(_lastIndirectPosition);
                return InputSystem.Root != null;
            case DragAction.Entered:
                return true;
            case DragAction.Location:
                UpdateExternalDragTarget(GetDragPosition(e), isDrop: false);
                return true;
            case DragAction.Exited:
                LeaveExternalDragTarget(_lastIndirectPosition);
                return true;
            case DragAction.Drop:
                AcquireExternalDragPermissions(e);
                _externalDragData = CreateDataPackage(e.ClipData) ?? _externalDragData;
                try
                {
                    UpdateExternalDragTarget(GetDragPosition(e), isDrop: true);
                    return true;
                }
                catch
                {
                    ReleaseExternalDragPermissions();
                    throw;
                }
                finally
                {
                    _externalDragData = null;
                }
            case DragAction.Ended:
                LeaveExternalDragTarget(_lastIndirectPosition);
                _externalDragData = null;
                ReleaseExternalDragPermissions();
                return true;
            default:
                return base.OnDragEvent(e);
        }
    }

    private void DispatchPointer(MotionEvent e, int index, PointerInputKind kind, bool inContact)
    {
        if (index < 0 || index >= e.PointerCount) return;
        float density = ResolveDensity();
        var deviceType = MapDeviceType(e.GetToolType(index));
        bool isMouse = deviceType == PointerDeviceType.Mouse;
        uint pointerId = isMouse ? IndirectPointerId : checked((uint)e.GetPointerId(index) + 1u);
        var position = new Vector2(e.GetX(index) / density, e.GetY(index) / density);
        if (isMouse) _lastIndirectPosition = position;
        MotionEventButtonState buttons = e.ButtonState;
        if (!isMouse && inContact) buttons |= MotionEventButtonState.Primary;
        bool effectiveContact = inContact && (!isMouse || buttons != 0);
        float diameter = Math.Max(1f / density, e.GetTouchMajor(index) / density);

        InputSystem.InjectPointer(new PointerInputEvent(
            kind,
            pointerId,
            deviceType,
            position,
            checked((ulong)Math.Max(0L, e.EventTime) * 1_000UL),
            IsPrimary: isMouse || index == 0,
            IsInContact: effectiveContact,
            IsLeftButtonPressed: buttons.HasFlag(MotionEventButtonState.Primary),
            IsMiddleButtonPressed: buttons.HasFlag(MotionEventButtonState.Tertiary),
            IsRightButtonPressed: buttons.HasFlag(MotionEventButtonState.Secondary),
            Pressure: effectiveContact ? Math.Clamp(e.GetPressure(index), 0f, 1f) : 0f,
            ContactRect: new ProGPU.Scene.Rect(
                position.X - diameter * 0.5f,
                position.Y - diameter * 0.5f,
                diameter,
                diameter),
            Modifiers: ReadModifiers(e.MetaState),
            UpdateKind: isMouse
                ? GetPointerUpdateKind(
                    kind,
                    e.ActionButton == 0
                        ? MotionEventButtonState.Primary
                        : e.ActionButton)
                : Microsoft.UI.Input.PointerUpdateKind.Other));
    }

    private static Microsoft.UI.Input.PointerUpdateKind
        GetPointerUpdateKind(
            PointerInputKind kind,
            MotionEventButtonState button) =>
        (kind, button) switch
        {
            (PointerInputKind.Pressed, MotionEventButtonState.Primary) =>
                Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed,
            (PointerInputKind.Released, MotionEventButtonState.Primary) =>
                Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased,
            (PointerInputKind.Pressed, MotionEventButtonState.Secondary) =>
                Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed,
            (PointerInputKind.Released, MotionEventButtonState.Secondary) =>
                Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased,
            (PointerInputKind.Pressed, MotionEventButtonState.Tertiary) =>
                Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed,
            (PointerInputKind.Released, MotionEventButtonState.Tertiary) =>
                Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased,
            _ => Microsoft.UI.Input.PointerUpdateKind.Other
        };

    private void DispatchScroll(MotionEvent e)
    {
        float density = ResolveDensity();
        var position = new Vector2(e.GetX() / density, e.GetY() / density);
        if (float.IsFinite(position.X) && float.IsFinite(position.Y)) _lastIndirectPosition = position;
        else position = _lastIndirectPosition;

        float gestureX = 0f;
        float gestureY = 0f;
        float pinchScale = 1f;
        float rawHorizontal = ReadAccumulatedAxis(e, Axis.Hscroll);
        float rawVertical = ReadAccumulatedAxis(e, Axis.Vscroll);
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            gestureX = ReadAccumulatedAxis(e, Axis.GestureScrollXDistance);
            gestureY = ReadAccumulatedAxis(e, Axis.GestureScrollYDistance);
            pinchScale = ReadAccumulatedPinchScale(e);
        }

        AndroidScrollDelta scroll = AndroidScrollDeltaPolicy.Convert(
            density,
            _horizontalScrollFactor,
            _verticalScrollFactor,
            rawHorizontal,
            rawVertical,
            gestureX,
            gestureY);
        float deltaX = scroll.X;
        float deltaY = scroll.Y;
        bool precise = scroll.IsPrecise;

        VirtualKeyModifiers modifiers = ReadModifiers(e.MetaState);
        if (float.IsFinite(pinchScale) && pinchScale > 0f && MathF.Abs(pinchScale - 1f) > 0.0001f)
        {
            deltaX = 0f;
            deltaY = MathF.Log(pinchScale) * PinchWheelUnitsPerNaturalLog;
            modifiers |= VirtualKeyModifiers.Control;
        }
        if (deltaX == 0f && deltaY == 0f) return;

        InputSystem.InjectPointer(new PointerInputEvent(
            PointerInputKind.Wheel,
            IndirectPointerId,
            PointerDeviceType.Mouse,
            position,
            checked((ulong)Math.Max(0L, e.EventTime) * 1_000UL),
            WheelDeltaX: deltaX,
            WheelDeltaY: deltaY,
            IsPreciseWheel: precise,
            Modifiers: modifiers));
    }

    private static float ReadAccumulatedAxis(MotionEvent e, Axis axis)
    {
        float total = 0f;
        for (int historyIndex = 0; historyIndex < e.HistorySize; historyIndex++)
        {
            float historical = e.GetHistoricalAxisValue(axis, historyIndex);
            if (float.IsFinite(historical)) total += historical;
        }

        float current = e.GetAxisValue(axis);
        if (float.IsFinite(current)) total += current;
        return total;
    }

    private static float ReadAccumulatedPinchScale(MotionEvent e)
    {
        float scale = 1f;
        for (int historyIndex = 0; historyIndex < e.HistorySize; historyIndex++)
        {
            float historical = e.GetHistoricalAxisValue(Axis.GesturePinchScaleFactor, historyIndex);
            if (float.IsFinite(historical) && historical > 0f) scale *= historical;
        }

        float current = e.GetAxisValue(Axis.GesturePinchScaleFactor);
        if (float.IsFinite(current) && current > 0f) scale *= current;
        return scale;
    }

    private Vector2 GetDragPosition(global::Android.Views.DragEvent e)
    {
        float density = ResolveDensity();
        var position = new Vector2(e.GetX() / density, e.GetY() / density);
        if (float.IsFinite(position.X) && float.IsFinite(position.Y)) _lastIndirectPosition = position;
        return _lastIndirectPosition;
    }

    private void UpdateExternalDragTarget(Vector2 screenPosition, bool isDrop)
    {
        Microsoft.UI.Xaml.FrameworkElement? target = FindDropTarget(InputSystem.HitTest(screenPosition));
        Microsoft.UI.Xaml.DataPackage data = _externalDragData ?? new Microsoft.UI.Xaml.DataPackage();
        if (!ReferenceEquals(target, _externalDragTarget))
        {
            LeaveExternalDragTarget(screenPosition);
            _externalDragTarget = target;
            if (target != null)
            {
                target.OnDragEnter(CreateExternalDragArgs(target, screenPosition, data));
            }
        }
        if (target == null) return;
        if (isDrop)
        {
            target.OnDrop(CreateExternalDragArgs(target, screenPosition, data));
            _externalDragTarget = null;
        }
        else
        {
            target.OnDragOver(CreateExternalDragArgs(target, screenPosition, data));
        }
        InputSystem.Root?.Invalidate();
    }

    private void LeaveExternalDragTarget(Vector2 screenPosition)
    {
        if (_externalDragTarget is not { } target) return;
        target.OnDragLeave(CreateExternalDragArgs(
            target,
            screenPosition,
            _externalDragData ?? new Microsoft.UI.Xaml.DataPackage()));
        _externalDragTarget = null;
        InputSystem.Root?.Invalidate();
    }

    private static Microsoft.UI.Xaml.FrameworkElement? FindDropTarget(Microsoft.UI.Xaml.FrameworkElement? element)
    {
        for (Microsoft.UI.Xaml.FrameworkElement? current = element;
             current != null;
             current = current.Parent as Microsoft.UI.Xaml.FrameworkElement)
        {
            if (current.AllowDrop) return current;
        }
        return null;
    }

    private static Microsoft.UI.Xaml.DragEventArgs CreateExternalDragArgs(
        Microsoft.UI.Xaml.FrameworkElement target,
        Vector2 screenPosition,
        Microsoft.UI.Xaml.DataPackage data) =>
        new(
            InputSystem.GetLocalPosition(target, screenPosition),
            screenPosition,
            data,
            Microsoft.UI.Xaml.DragDropEffects.Copy,
            Microsoft.UI.Xaml.DragDropModifiers.None);

    private Microsoft.UI.Xaml.DataPackage? CreateDataPackage(ClipData? clip)
    {
        if (clip == null) return null;
        var data = new Microsoft.UI.Xaml.DataPackage();
        var text = new List<string>();
        var uris = new List<string>();
        for (int index = 0; index < clip.ItemCount; index++)
        {
            ClipData.Item? item = clip.GetItemAt(index);
            if (item == null) continue;
            string? uri = item.Uri?.ToString();
            if (!string.IsNullOrWhiteSpace(uri)) uris.Add(uri);
            string? itemText = item.Text?.ToString() ?? item.CoerceToText(Context)?.ToString();
            if (!string.IsNullOrEmpty(itemText)) text.Add(itemText);
        }
        if (text.Count > 0) data.SetText(string.Join(System.Environment.NewLine, text));
        if (uris.Count > 0)
        {
            string[] values = [.. uris];
            data.SetData(Microsoft.UI.Xaml.StandardDataFormats.StorageItems, values);
            data.SetData(Microsoft.UI.Xaml.StandardDataFormats.FileNames, values);
        }
        return data;
    }

    private void AcquireExternalDragPermissions(global::Android.Views.DragEvent dragEvent)
    {
        ReleaseExternalDragPermissions();
        try
        {
            // Android grants transient URI access only from ACTION_DROP. Retain the
            // token through ACTION_DRAG_ENDED so synchronous framework drop handlers
            // can consume ClipData, then release it at the native session boundary.
            _externalDragPermissions = _activity.RequestDragAndDropPermissions(dragEvent);
        }
        catch (Java.Lang.SecurityException)
        {
            // Plain-text drags and providers that do not offer URI grants must still
            // reach the framework drop contract.
        }
    }

    private void ReleaseExternalDragPermissions()
    {
        DragAndDropPermissions? permissions = _externalDragPermissions;
        _externalDragPermissions = null;
        if (permissions == null) return;
        permissions.Release();
        permissions.Dispose();
    }

    private bool TrySelectInputState()
    {
        if (InputStateProvider?.Invoke() is not { } inputState) return false;
        InputSystem.Current = inputState;
        return true;
    }

    private void NotifyMetricsChanged() => MetricsChanged?.Invoke(Metrics);

    private float ResolveDensity()
    {
        float density = Resources?.DisplayMetrics?.Density ?? 1f;
        return float.IsFinite(density) && density > 0f ? density : 1f;
    }

    private static PointerDeviceType MapDeviceType(MotionEventToolType toolType) => toolType switch
    {
        MotionEventToolType.Stylus or MotionEventToolType.Eraser => PointerDeviceType.Pen,
        MotionEventToolType.Mouse => PointerDeviceType.Mouse,
        _ => PointerDeviceType.Touch
    };

    internal static VirtualKeyModifiers ReadModifiers(MetaKeyStates state)
    {
        var result = VirtualKeyModifiers.None;
        if ((state & MetaKeyStates.ShiftMask) != 0) result |= VirtualKeyModifiers.Shift;
        if ((state & MetaKeyStates.CtrlMask) != 0) result |= VirtualKeyModifiers.Control;
        if ((state & MetaKeyStates.AltMask) != 0) result |= VirtualKeyModifiers.Menu;
        if ((state & MetaKeyStates.MetaMask) != 0) result |= VirtualKeyModifiers.Windows;
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseExternalDragPermissions();
            Holder?.RemoveCallback(this);
        }
        base.Dispose(disposing);
    }
}
