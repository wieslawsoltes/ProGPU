using CoreAnimation;
using CoreGraphics;
using Foundation;
using Microsoft.UI.Xaml.Input;
using System.Numerics;
using UIKit;
using Windows.Devices.Input;

namespace ProGPU.iOS;

internal readonly record struct MetalViewMetrics(
    uint Width,
    uint Height,
    float DpiScale,
    Microsoft.UI.Xaml.Thickness SafeAreaInsets);

internal sealed class MetalRenderView : UIView
{
    // Keep the framework's conventional low IDs available to simultaneous direct
    // touches while presenting every hover/click/scroll phase as one mouse pointer.
    private const uint IndirectPointerId = uint.MaxValue;
    private const float PinchWheelUnitsPerNaturalLog = 120f;
    private readonly CAMetalLayer _metalLayer;
    private readonly UIScreen _screen;
    private readonly Dictionary<nint, uint> _pointerIds = [];
    private readonly UIPanGestureRecognizer _indirectScrollRecognizer;
    private readonly UIPinchGestureRecognizer _indirectPinchRecognizer;
    private readonly UIHoverGestureRecognizer _hoverRecognizer;
    private readonly IndirectGestureDelegate _scrollGestureDelegate;
    private readonly IndirectGestureDelegate _transformGestureDelegate;
    private readonly Dictionary<nint, UIEventButtonMask> _indirectButtons = [];
    private CGRect _configuredBounds;
    private CGSize _configuredDrawableSize;
    private float _configuredScale;
    private uint _nextPointerId = 1;
    private Vector2 _lastIndirectPointerPosition;

    public MetalRenderView(CGRect frame, UIScreen screen) : base(frame)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        MultipleTouchEnabled = true;
        Opaque = true;
        BackgroundColor = UIColor.Black;

        _metalLayer = new CAMetalLayer
        {
            FramebufferOnly = true,
            Opaque = true,
            MaximumDrawableCount = 3,
            PresentsWithTransaction = false
        };
        Layer.AddSublayer(_metalLayer);
        _scrollGestureDelegate = new IndirectGestureDelegate(UIEventType.Scroll);
        _transformGestureDelegate = new IndirectGestureDelegate(UIEventType.Transform);
        _indirectScrollRecognizer = new UIPanGestureRecognizer(HandleIndirectScroll)
        {
            AllowedScrollTypesMask = UIScrollTypeMask.All,
            CancelsTouchesInView = false,
            Delegate = _scrollGestureDelegate
        };
        _indirectPinchRecognizer = new UIPinchGestureRecognizer(HandleIndirectPinch)
        {
            CancelsTouchesInView = false,
            Delegate = _transformGestureDelegate
        };
        _hoverRecognizer = new UIHoverGestureRecognizer(HandleHover)
        {
            CancelsTouchesInView = false
        };
        AddGestureRecognizer(_indirectScrollRecognizer);
        AddGestureRecognizer(_indirectPinchRecognizer);
        AddGestureRecognizer(_hoverRecognizer);
        UpdateDrawableSize();
    }

    public nint MetalLayerHandle => (nint)_metalLayer.Handle;

    public MetalViewMetrics Metrics
    {
        get
        {
            UpdateDrawableSize();
            return new MetalViewMetrics(
                Math.Max(1u, checked((uint)Math.Ceiling((double)_metalLayer.DrawableSize.Width))),
                Math.Max(1u, checked((uint)Math.Ceiling((double)_metalLayer.DrawableSize.Height))),
                ResolveScale(),
                ResolveSafeAreaInsets());
        }
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        UpdateDrawableSize();
    }

    public override void TouchesBegan(NSSet touches, UIEvent? evt) =>
        DispatchTouches(touches, evt, PointerInputKind.Pressed, inContact: true);

    public override void TouchesMoved(NSSet touches, UIEvent? evt) =>
        DispatchTouches(touches, evt, PointerInputKind.Moved, inContact: true);

    public override void TouchesEnded(NSSet touches, UIEvent? evt) =>
        DispatchTouches(touches, evt, PointerInputKind.Released, inContact: false);

    public override void TouchesCancelled(NSSet touches, UIEvent? evt) =>
        DispatchTouches(touches, evt, PointerInputKind.Canceled, inContact: false);

    private void DispatchTouches(NSSet touches, UIEvent? evt, PointerInputKind kind, bool inContact)
    {
        foreach (UITouch touch in touches.ToArray<UITouch>())
        {
            nint handle = (nint)touch.Handle;
            bool isIndirectPointer = touch.Type == UITouchType.IndirectPointer;
            if (!_pointerIds.TryGetValue(handle, out uint pointerId))
            {
                pointerId = isIndirectPointer ? IndirectPointerId : NextTouchPointerId();
                _pointerIds.Add(handle, pointerId);
            }

            CGPoint point = touch.LocationInView(this);
            var position = new Vector2((float)point.X, (float)point.Y);
            UIEventButtonMask buttons = isIndirectPointer
                ? ResolveIndirectButtons(handle, evt, kind)
                : inContact ? UIEventButtonMask.Primary : 0;
            bool effectiveContact = inContact && (!isIndirectPointer || buttons != 0);
            float radius = isIndirectPointer ? 0.5f : Math.Max(1f, (float)touch.MajorRadius);
            float pressure = isIndirectPointer
                ? effectiveContact ? 0.5f : 0f
                : touch.MaximumPossibleForce > 0
                    ? Math.Clamp((float)(touch.Force / touch.MaximumPossibleForce), 0f, 1f)
                    : effectiveContact ? 1f : 0f;
            var deviceType = touch.Type switch
            {
                UITouchType.Stylus => PointerDeviceType.Pen,
                UITouchType.IndirectPointer => PointerDeviceType.Mouse,
                _ => PointerDeviceType.Touch
            };
            if (isIndirectPointer) _lastIndirectPointerPosition = position;

            InputSystem.InjectPointer(new PointerInputEvent(
                kind,
                pointerId,
                deviceType,
                position,
                checked((ulong)Math.Max(0d, touch.Timestamp * 1_000_000d)),
                IsPrimary: isIndirectPointer || pointerId == 1,
                IsInContact: effectiveContact,
                IsLeftButtonPressed: buttons.HasFlag(UIEventButtonMask.Primary),
                IsMiddleButtonPressed: buttons.HasFlag(UIEventButtonMaskExtensions.Convert(3)),
                IsRightButtonPressed: buttons.HasFlag(UIEventButtonMask.Secondary),
                Pressure: pressure,
                ContactRect: new ProGPU.Scene.Rect(
                    (float)point.X - radius,
                    (float)point.Y - radius,
                    radius * 2f,
                    radius * 2f),
                Modifiers: ReadModifiers(evt?.ModifierFlags ?? 0)));

            if (!inContact)
            {
                _pointerIds.Remove(handle);
                _indirectButtons.Remove(handle);
                if (_pointerIds.Count == 0) _nextPointerId = 1;
            }
        }
    }

    private uint NextTouchPointerId()
    {
        while (_nextPointerId == IndirectPointerId || _pointerIds.ContainsValue(_nextPointerId))
        {
            _nextPointerId++;
        }
        return _nextPointerId++;
    }

    private UIEventButtonMask ResolveIndirectButtons(nint handle, UIEvent? evt, PointerInputKind kind)
    {
        if (kind is PointerInputKind.Released or PointerInputKind.Canceled) return 0;

        UIEventButtonMask buttons = evt?.ButtonMask ?? 0;
        if (buttons == 0 && _indirectButtons.TryGetValue(handle, out var previous)) buttons = previous;
        if (buttons == 0 && kind == PointerInputKind.Pressed) buttons = UIEventButtonMask.Primary;
        _indirectButtons[handle] = buttons;
        return buttons;
    }

    private void UpdateDrawableSize()
    {
        float scale = ResolveScale();
        CGRect bounds = Bounds;
        CGSize drawableSize = new(
            Math.Max(1d, (double)bounds.Width * scale),
            Math.Max(1d, (double)bounds.Height * scale));
        if (_configuredScale != scale)
        {
            ContentScaleFactor = scale;
            _metalLayer.ContentsScale = scale;
            _configuredScale = scale;
        }
        if (!_configuredBounds.Equals(bounds))
        {
            _metalLayer.Frame = bounds;
            _configuredBounds = bounds;
        }
        if (!_configuredDrawableSize.Equals(drawableSize))
        {
            _metalLayer.DrawableSize = drawableSize;
            _configuredDrawableSize = drawableSize;
        }
    }

    private void HandleIndirectScroll(UIPanGestureRecognizer recognizer)
    {
        if (recognizer.State is not (UIGestureRecognizerState.Began or
            UIGestureRecognizerState.Changed or
            UIGestureRecognizerState.Ended)) return;
        CGPoint translation = recognizer.TranslationInView(this);
        if (translation.X == 0d && translation.Y == 0d) return;
        Vector2 location = ResolveIndirectLocation(recognizer);
        InputSystem.InjectPointer(new PointerInputEvent(
            PointerInputKind.Wheel,
            IndirectPointerId,
            PointerDeviceType.Mouse,
            location,
            checked((ulong)Math.Max(0d, NSProcessInfo.ProcessInfo.SystemUptime * 1_000_000d)),
            WheelDeltaX: (float)translation.X,
            WheelDeltaY: (float)translation.Y,
            IsPreciseWheel: true,
            Modifiers: ReadModifiers(recognizer.ModifierFlags)));
        recognizer.SetTranslation(CGPoint.Empty, this);
    }

    private void HandleIndirectPinch(UIPinchGestureRecognizer recognizer)
    {
        if (recognizer.State is not (UIGestureRecognizerState.Began or
            UIGestureRecognizerState.Changed or
            UIGestureRecognizerState.Ended)) return;

        double scale = (double)recognizer.Scale;
        if (!double.IsFinite(scale) || scale <= 0d) return;
        recognizer.Scale = 1;
        float zoomDelta = (float)(Math.Log(scale) * PinchWheelUnitsPerNaturalLog);
        if (MathF.Abs(zoomDelta) <= float.Epsilon) return;

        InputSystem.InjectPointer(new PointerInputEvent(
            PointerInputKind.Wheel,
            IndirectPointerId,
            PointerDeviceType.Mouse,
            ResolveIndirectLocation(recognizer),
            checked((ulong)Math.Max(0d, NSProcessInfo.ProcessInfo.SystemUptime * 1_000_000d)),
            WheelDeltaY: zoomDelta,
            IsPreciseWheel: true,
            Modifiers: ReadModifiers(recognizer.ModifierFlags) | VirtualKeyModifiers.Control));
    }

    private void HandleHover(UIHoverGestureRecognizer recognizer)
    {
        if (recognizer.State is not (UIGestureRecognizerState.Began or
            UIGestureRecognizerState.Changed or
            UIGestureRecognizerState.Ended)) return;

        Vector2 position = ResolveIndirectLocation(recognizer);
        InputSystem.InjectPointer(new PointerInputEvent(
            PointerInputKind.Moved,
            IndirectPointerId,
            PointerDeviceType.Mouse,
            position,
            checked((ulong)Math.Max(0d, NSProcessInfo.ProcessInfo.SystemUptime * 1_000_000d)),
            Modifiers: ReadModifiers(recognizer.ModifierFlags)));
    }

    private Vector2 ResolveIndirectLocation(UIGestureRecognizer recognizer)
    {
        CGPoint point = recognizer.LocationInView(this);
        var position = new Vector2((float)point.X, (float)point.Y);
        if (float.IsFinite(position.X) && float.IsFinite(position.Y))
        {
            _lastIndirectPointerPosition = position;
        }
        return _lastIndirectPointerPosition;
    }

    private static VirtualKeyModifiers ReadModifiers(UIKeyModifierFlags flags)
    {
        var result = VirtualKeyModifiers.None;
        if (flags.HasFlag(UIKeyModifierFlags.Shift)) result |= VirtualKeyModifiers.Shift;
        if (flags.HasFlag(UIKeyModifierFlags.Control)) result |= VirtualKeyModifiers.Control;
        if (flags.HasFlag(UIKeyModifierFlags.Alternate)) result |= VirtualKeyModifiers.Menu;
        if (flags.HasFlag(UIKeyModifierFlags.Command)) result |= VirtualKeyModifiers.Windows;
        return result;
    }

    private Microsoft.UI.Xaml.Thickness ResolveSafeAreaInsets()
    {
        UIEdgeInsets insets = SafeAreaInsets;
        return new Microsoft.UI.Xaml.Thickness(
            (float)Math.Max(0d, (double)insets.Left),
            (float)Math.Max(0d, (double)insets.Top),
            (float)Math.Max(0d, (double)insets.Right),
            (float)Math.Max(0d, (double)insets.Bottom));
    }

    private float ResolveScale()
    {
        double nativeScale = (double)_screen.NativeScale;
        return (float)(double.IsFinite(nativeScale) && nativeScale > 0d ? nativeScale : 1d);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            RemoveGestureRecognizer(_hoverRecognizer);
            _hoverRecognizer.Dispose();
            RemoveGestureRecognizer(_indirectPinchRecognizer);
            _indirectPinchRecognizer.Delegate = null!;
            _indirectPinchRecognizer.Dispose();
            RemoveGestureRecognizer(_indirectScrollRecognizer);
            _indirectScrollRecognizer.Delegate = null!;
            _indirectScrollRecognizer.Dispose();
            _transformGestureDelegate.Dispose();
            _scrollGestureDelegate.Dispose();
            _metalLayer.RemoveFromSuperLayer();
            _metalLayer.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class IndirectGestureDelegate(UIEventType eventType) : UIGestureRecognizerDelegate
    {
        public override bool ShouldReceiveTouch(UIGestureRecognizer recognizer, UITouch touch) => false;

        public override bool ShouldReceiveEvent(UIGestureRecognizer gestureRecognizer, UIEvent @event) =>
            @event.Type == eventType;

        public override bool ShouldRecognizeSimultaneously(
            UIGestureRecognizer gestureRecognizer,
            UIGestureRecognizer otherGestureRecognizer) => true;
    }
}
