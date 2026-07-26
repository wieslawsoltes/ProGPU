using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform;
#if AVALONIA11
using Avalonia.Controls.Platform.Surfaces;
#else
using Avalonia.Platform.Surfaces;
#endif
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Core;
using ProGPU.Backend;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Avalonia.SilkNet
{
    public class WindowImpl : IWindowImpl
    {
        private const string ShareWebGpuDeviceVariable =
            "PROGPU_AVALONIA_SHARE_WGPU_DEVICE";
        private Silk.NET.Windowing.IWindow _silkWindow;
        private readonly IMouseDevice _mouseDevice;
        private IInputContext? _inputContext;
        private IInputRoot? _owner;
        private WindowImpl? _nativeParent;
        private double _scaling = 1.0;
        private Size _clientSize = new Size(1280, 800);
        private string? _title = "Avalonia Silk.NET Window";
        private PixelPoint _position = new PixelPoint(100, 100);
        private Avalonia.Controls.WindowState _windowState = Avalonia.Controls.WindowState.Normal;
        private Silk.NET.Windowing.WindowState? _lastNativeWindowState;
        private Avalonia.Controls.WindowState? _pendingInitialWindowState;
        private SilkNetFramebufferManager _framebuffer;
        private bool _isShown;
        private bool _isLoaded;
        private bool _isEnabled = true;
        private bool _paintQueued;
        private bool _isRenderingLiveResize;
        private ulong _paintGeneration;
        private char? _pendingHighSurrogate;
        private SilkNetCursorImpl? _cursor;
        private IWindowIconImpl? _icon;
        private WgpuContext? _wgpuContext;
        private readonly SilkWindowController _windowController;
        private bool _canResize = true;
        private bool _canMinimize = true;
        private bool _canMaximize = true;
        private
#if AVALONIA11
            SystemDecorations
#else
            WindowDecorations
#endif
            _windowDecorations =
#if AVALONIA11
                SystemDecorations.Full;
#else
                WindowDecorations.Full;
#endif
        private Size _minimumSize;
        private Size _maximumSize = new(double.PositiveInfinity, double.PositiveInfinity);
        private WindowTransparencyLevel _transparencyLevel = WindowTransparencyLevel.None;
        private IReadOnlyList<WindowTransparencyLevel> _transparencyLevels = new[] { WindowTransparencyLevel.None };
        private double _titleBarHeight = -1d;
        private PlatformThemeVariant _themeVariant = PlatformThemeVariant.Light;

        public WindowImpl()
        {
            _mouseDevice = new MouseDevice();

            _scaling = 1.0;

            var options = WindowOptions.Default;
            options.Size = new Vector2D<int>((int)_clientSize.Width, (int)_clientSize.Height);
            options.Title = _title ?? "Avalonia Silk.NET Window";
            options.API = GraphicsAPI.None; // We use WebGPU manually
            options.VSync = false;
            options.Position = new Vector2D<int>((int)(_position.X / _scaling), (int)(_position.Y / _scaling));
            options.WindowBorder = WindowBorder.Resizable;
            options.TransparentFramebuffer = true;

            _silkWindow = Silk.NET.Windowing.Window.Create(options);
            _windowController = new SilkWindowController(_silkWindow);
            _silkWindow.Load += OnLoad;
            _silkWindow.Render += OnRender;
            _silkWindow.Resize += OnResize;
            _silkWindow.FramebufferResize += OnFramebufferResize;
            _silkWindow.Move += OnMove;
            _silkWindow.Closing += OnClosing;
            _silkWindow.FocusChanged += OnFocusChanged;
            _silkWindow.StateChanged += OnStateChanged;
            WgpuContext.OnWebGpuDeviceLost += OnWebGpuDeviceLost;

            _framebuffer = new SilkNetFramebufferManager(_silkWindow);

            Handle = new PlatformHandle(IntPtr.Zero, "SilkWindow");

            SilkNetPlatform.Instance.RegisterWindow(this);
        }

        public Silk.NET.Windowing.IWindow SilkWindow => _silkWindow;
        public IInputRoot Owner => _owner ?? throw new InvalidOperationException("Owner not set");

        /// <summary>
        /// Gets whether this window currently owns a healthy initialized
        /// WebGPU presentation context.
        /// </summary>
        public bool HasActiveWebGpuContext
        {
            get
            {
                WgpuContext? context = Volatile.Read(ref _wgpuContext);
                return context is
                {
                    IsDisposed: false,
                    IsDeviceLost: false,
                    IsInitialized: true
                };
            }
        }

        /// <summary>
        /// Returns whether this window and <paramref name="other"/> share the
        /// same typed WebGPU device-resource ownership domain.
        /// </summary>
        /// <remarks>
        /// This is a constant-time diagnostics contract. It does not expose
        /// native handles, probe assemblies, or use reflection.
        /// </remarks>
        public bool SharesWebGpuDeviceWith(WindowImpl? other)
        {
            if (other == null)
                return false;

            WgpuContext? context = Volatile.Read(ref _wgpuContext);
            WgpuContext? otherContext = Volatile.Read(ref other._wgpuContext);
            return context is
                {
                    IsDisposed: false,
                    IsDeviceLost: false,
                    IsInitialized: true
                } &&
                otherContext is
                {
                    IsDisposed: false,
                    IsDeviceLost: false,
                    IsInitialized: true
                } &&
                context.SharesDeviceWith(otherContext);
        }

        public void SetInputRoot(IInputRoot inputRoot)
        {
            _owner = inputRoot;
        }

        private void OnLoad()
        {
            _windowController.Attach();
            if (_nativeParent is { IsDisposed: false } nativeParent)
            {
                _windowController.SetParent(nativeParent._windowController.Handle);
            }
            ApplyWindowCustomizationState();
            if (_pendingInitialWindowState is { } initialWindowState)
            {
                ApplyNativeWindowState(initialWindowState);
            }
            ApplyTransparencyLevelHints();
            ExtendClientAreaToDecorationsChanged?.Invoke(_isClientAreaExtended);
            var nativeHandle = _windowController.Handle;
            if (nativeHandle.IsValid)
            {
                Handle = new PlatformHandle(nativeHandle.Handle, nativeHandle.Descriptor);
            }
            ApplyIcon();

            var oldScaling = _scaling;
            _scaling = GetWindowScaling();
            if (oldScaling != _scaling)
            {
                ScalingChanged?.Invoke(_scaling);
                _clientSize = new Size(_silkWindow.Size.X, _silkWindow.Size.Y);
                Resized?.Invoke(_clientSize, WindowResizeReason.Layout);
            }

            _wgpuContext = CreateWebGpuContext();

            _inputContext = _silkWindow.CreateInput();
            foreach (var keyboard in _inputContext.Keyboards)
            {
                keyboard.KeyDown += OnKeyDown;
                keyboard.KeyUp += OnKeyUp;
                keyboard.KeyChar += OnKeyChar;
            }
            foreach (var mouse in _inputContext.Mice)
            {
                mouse.MouseMove += OnMouseMove;
                mouse.MouseDown += OnMouseDown;
                mouse.MouseUp += OnMouseUp;
                mouse.Scroll += OnMouseScroll;
                ApplyCursor(mouse.Cursor, _cursor);
            }

            _isLoaded = true;
        }

        private WgpuContext CreateWebGpuContext()
        {
            var context = new WgpuContext();
            if (ShouldShareWebGpuDevice() &&
                WgpuContext.TryGetFirstActiveContext(out var sharedDeviceOwner) &&
                sharedDeviceOwner.BackendKind == WgpuBackendKind.SilkNative)
            {
                context.InitializeSharedDevice(
                    _silkWindow,
                    sharedDeviceOwner);
            }
            else
            {
                context.Initialize(_silkWindow);
            }

            return context;
        }

        private bool EnsureWebGpuContextReady()
        {
            WgpuContext? current = _wgpuContext;
            if (current is { IsDeviceLost: false })
            {
                return true;
            }

            if (!_isLoaded || _disposed)
            {
                return false;
            }

            try
            {
                WgpuContext replacement = CreateWebGpuContext();
                _wgpuContext = replacement;
                try
                {
                    current?.Dispose();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"[Avalonia.SilkNet] Lost WebGPU device cleanup failed: {exception.Message}");
                }
                return true;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[Avalonia.SilkNet] WebGPU device recovery failed: {exception.Message}");
                return false;
            }
        }

        private void OnWebGpuDeviceLost(
            Silk.NET.WebGPU.DeviceLostReason reason,
            string message)
        {
            if (_disposed)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || !_isLoaded)
                {
                    return;
                }

                _paintQueued = true;
                PaintNow();
            }, DispatcherPriority.Render);
        }

        internal static bool ShouldShareWebGpuDevice()
        {
            string? configured = Environment.GetEnvironmentVariable(
                ShareWebGpuDeviceVariable);
            return !string.Equals(
                configured,
                "0",
                StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    configured,
                    "false",
                    StringComparison.OrdinalIgnoreCase);
        }

        private void OnRender(double delta)
        {
            if (_paintQueued)
            {
                PaintNow();
            }
        }

        private void OnResize(Vector2D<int> size)
        {
            UpdateClientSize(size);
        }

        private void OnFramebufferResize(Vector2D<int> size)
        {
            if (size.X <= 0 || size.Y <= 0 || !_isLoaded || _disposed || _isRenderingLiveResize)
            {
                return;
            }

            UpdateClientSize(_silkWindow.Size);
            _isRenderingLiveResize = true;
            try
            {
                var paintGeneration = _paintGeneration;
                Dispatcher.UIThread.RunJobs(DispatcherPriority.UiThreadRender);
                if (paintGeneration == _paintGeneration)
                {
                    PaintNow();
                }
            }
            finally
            {
                _isRenderingLiveResize = false;
            }
        }

        private void UpdateClientSize(Vector2D<int> size)
        {
            var clientSize = new Size(size.X, size.Y);
            if (_clientSize == clientSize)
            {
                return;
            }

            _clientSize = clientSize;
            Resized?.Invoke(_clientSize, WindowResizeReason.Layout);
        }

        private void PaintNow()
        {
            if (!EnsureWebGpuContextReady())
            {
                _paintQueued = true;
                return;
            }

            _paintQueued = false;
            _paintGeneration++;
            using var currentContext = WgpuContext.PushCurrent(_wgpuContext);
            Paint?.Invoke(new Rect(0, 0, ClientSize.Width, ClientSize.Height));
        }

        private void OnMove(Vector2D<int> position)
        {
            var oldScaling = _scaling;
            _scaling = GetWindowScaling();
            _position = new PixelPoint((int)(position.X * _scaling), (int)(position.Y * _scaling));
            PositionChanged?.Invoke(_position);
            if (oldScaling != _scaling)
            {
                ScalingChanged?.Invoke(_scaling);
                _clientSize = new Size(_silkWindow.Size.X, _silkWindow.Size.Y);
                Resized?.Invoke(_clientSize, WindowResizeReason.Layout);
            }
        }

        private void OnClosing()
        {
            if (Closing?.Invoke(WindowCloseReason.WindowClosing) == true)
            {
                _silkWindow.IsClosing = false;
                return;
            }

            Closed?.Invoke();
            SilkNetPlatform.Instance.UnregisterWindow(this);
        }

        private void OnStateChanged(Silk.NET.Windowing.WindowState state)
        {
            var mapped = state switch
            {
                Silk.NET.Windowing.WindowState.Maximized => Avalonia.Controls.WindowState.Maximized,
                Silk.NET.Windowing.WindowState.Minimized => Avalonia.Controls.WindowState.Minimized,
                Silk.NET.Windowing.WindowState.Fullscreen => Avalonia.Controls.WindowState.FullScreen,
                _ => Avalonia.Controls.WindowState.Normal
            };
            if (_pendingInitialWindowState is { } pendingInitialState)
            {
                if (mapped != pendingInitialState)
                {
                    return;
                }
                _pendingInitialWindowState = null;
            }
            if (_lastNativeWindowState == state)
            {
                return;
            }
            _lastNativeWindowState = state;
            _windowState = mapped;
            WindowStateChanged?.Invoke(mapped);
            _windowController.Reapply();
        }

        private void OnFocusChanged(bool focused)
        {
            if (focused)
            {
                Activated?.Invoke();
            }
            else
            {
                Deactivated?.Invoke();
                LostFocus?.Invoke();
            }
        }

        private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 pos)
        {
            if (!_isEnabled)
            {
                return;
            }
            _windowController.UpdateDrag(ToNativeScreenPoint(pos));
            var p = new Point(pos.X, pos.Y);
            var args = new RawPointerEventArgs(
                _mouseDevice,
                GetTimestamp(),
                Owner,
                RawPointerEventType.Move,
                p,
                SilkNetInputMappings.GetPointerModifiers(_inputContext, mouse)
            );
            Input?.Invoke(args);
        }

        private void OnMouseDown(IMouse mouse, Silk.NET.Input.MouseButton button)
        {
            if (!_isEnabled)
            {
                GotInputWhenDisabled?.Invoke();
                return;
            }
            var pos = mouse.Position;
            var p = new Point(pos.X, pos.Y);
            var type = button switch {
                Silk.NET.Input.MouseButton.Left => RawPointerEventType.LeftButtonDown,
                Silk.NET.Input.MouseButton.Right => RawPointerEventType.RightButtonDown,
                Silk.NET.Input.MouseButton.Middle => RawPointerEventType.MiddleButtonDown,
                Silk.NET.Input.MouseButton.Button4 => RawPointerEventType.XButton1Down,
                Silk.NET.Input.MouseButton.Button5 => RawPointerEventType.XButton2Down,
                _ => (RawPointerEventType?)null
            };
            if (type == null) return;
            var args = new RawPointerEventArgs(
                _mouseDevice,
                GetTimestamp(),
                Owner,
                type.Value,
                p,
                SilkNetInputMappings.GetPointerModifiers(_inputContext, mouse, button, eventButtonIsDown: true)
            );
            Input?.Invoke(args);
        }

        private void OnMouseUp(IMouse mouse, Silk.NET.Input.MouseButton button)
        {
            if (!_isEnabled)
            {
                return;
            }
            var pos = mouse.Position;
            if (button == Silk.NET.Input.MouseButton.Left)
            {
                _windowController.UpdateDrag(ToNativeScreenPoint(pos));
                _windowController.EndDrag();
            }
            var p = new Point(pos.X, pos.Y);
            var type = button switch {
                Silk.NET.Input.MouseButton.Left => RawPointerEventType.LeftButtonUp,
                Silk.NET.Input.MouseButton.Right => RawPointerEventType.RightButtonUp,
                Silk.NET.Input.MouseButton.Middle => RawPointerEventType.MiddleButtonUp,
                Silk.NET.Input.MouseButton.Button4 => RawPointerEventType.XButton1Up,
                Silk.NET.Input.MouseButton.Button5 => RawPointerEventType.XButton2Up,
                _ => (RawPointerEventType?)null
            };
            if (type == null) return;
            var args = new RawPointerEventArgs(
                _mouseDevice,
                GetTimestamp(),
                Owner,
                type.Value,
                p,
                SilkNetInputMappings.GetPointerModifiers(_inputContext, mouse, button)
            );
            Input?.Invoke(args);
        }

        private NativeWindowPoint ToNativeScreenPoint(System.Numerics.Vector2 clientPoint)
        {
            return new NativeWindowPoint(
                _silkWindow.Position.X + (int)MathF.Round(clientPoint.X),
                _silkWindow.Position.Y + (int)MathF.Round(clientPoint.Y));
        }

        private NativeWindowPoint GetCurrentNativePointerPoint()
        {
            var position = _inputContext?.Mice.FirstOrDefault()?.Position ?? default;
            return ToNativeScreenPoint(position);
        }

        private void OnMouseScroll(IMouse mouse, ScrollWheel scroll)
        {
            if (!_isEnabled)
            {
                GotInputWhenDisabled?.Invoke();
                return;
            }
            var pos = mouse.Position;
            var p = new Point(pos.X, pos.Y);
            var args = new RawMouseWheelEventArgs(
                _mouseDevice,
                GetTimestamp(),
                Owner,
                p,
                new Avalonia.Vector(scroll.X, scroll.Y),
                SilkNetInputMappings.GetPointerModifiers(_inputContext, mouse)
            );
            Input?.Invoke(args);
        }

        private void OnKeyDown(IKeyboard keyboard, Silk.NET.Input.Key key, int keyCode)
        {
            if (!_isEnabled)
            {
                GotInputWhenDisabled?.Invoke();
                return;
            }
            var mapping = SilkNetInputMappings.MapKey(key);
            var args = new RawKeyEventArgs(
                SilkNetKeyboardDevice.Instance,
                GetTimestamp(),
                Owner,
                RawKeyEventType.KeyDown,
                mapping.Key,
                SilkNetInputMappings.GetKeyboardModifiers(keyboard, key, eventKeyIsDown: true),
                mapping.PhysicalKey,
                null
            );
            Input?.Invoke(args);
        }

        private void OnKeyUp(IKeyboard keyboard, Silk.NET.Input.Key key, int keyCode)
        {
            if (!_isEnabled)
            {
                return;
            }
            var mapping = SilkNetInputMappings.MapKey(key);
            var args = new RawKeyEventArgs(
                SilkNetKeyboardDevice.Instance,
                GetTimestamp(),
                Owner,
                RawKeyEventType.KeyUp,
                mapping.Key,
                SilkNetInputMappings.GetKeyboardModifiers(keyboard),
                mapping.PhysicalKey,
                null
            );
            Input?.Invoke(args);
        }

        private void OnKeyChar(IKeyboard keyboard, char character)
        {
            if (!_isEnabled)
            {
                return;
            }
            if (char.IsHighSurrogate(character))
            {
                FlushPendingHighSurrogate();
                _pendingHighSurrogate = character;
                return;
            }

            string text;
            if (char.IsLowSurrogate(character) && _pendingHighSurrogate.HasValue)
            {
                text = string.Concat(_pendingHighSurrogate.Value, character);
                _pendingHighSurrogate = null;
            }
            else
            {
                FlushPendingHighSurrogate();
                text = character.ToString();
            }

            RaiseTextInput(text);
        }

        private void FlushPendingHighSurrogate()
        {
            if (_pendingHighSurrogate is not { } highSurrogate)
            {
                return;
            }

            _pendingHighSurrogate = null;
            RaiseTextInput(highSurrogate.ToString());
        }

        private void RaiseTextInput(string text)
        {
            var args = new RawTextInputEventArgs(
                SilkNetKeyboardDevice.Instance,
                GetTimestamp(),
                Owner,
                text
            );
            Input?.Invoke(args);
        }

        private static ulong GetTimestamp() => unchecked((ulong)Environment.TickCount64);

        public Size ClientSize => _clientSize;
        public Size? FrameSize
        {
            get
            {
                var insets = _windowController.FrameInsets;
                return new Size(
                    _clientSize.Width + insets.Left + insets.Right,
                    _clientSize.Height + insets.Top + insets.Bottom);
            }
        }
        public double RenderScaling => _scaling;
        public double DesktopScaling => _scaling;
        public IPlatformHandle Handle { get; private set; }
        public Size MaxAutoSizeHint => new Size(1920, 1080);
        public IMouseDevice MouseDevice => _mouseDevice;

        public Avalonia.Controls.WindowState WindowState
        {
            get => _windowState;
            set
            {
                _windowState = value;
                if (!_silkWindow.IsInitialized)
                {
                    _pendingInitialWindowState = value == Avalonia.Controls.WindowState.Normal
                        ? null
                        : value;
                    return;
                }

                ApplyNativeWindowState(value);
            }
        }

        private void ApplyNativeWindowState(Avalonia.Controls.WindowState value)
        {
            var targetState = value switch
            {
                Avalonia.Controls.WindowState.Maximized => Silk.NET.Windowing.WindowState.Maximized,
                Avalonia.Controls.WindowState.Minimized => Silk.NET.Windowing.WindowState.Minimized,
                Avalonia.Controls.WindowState.FullScreen => Silk.NET.Windowing.WindowState.Fullscreen,
                _ => Silk.NET.Windowing.WindowState.Normal
            };

            if (value is Avalonia.Controls.WindowState.Maximized or
                Avalonia.Controls.WindowState.FullScreen)
            {
                _windowController.PrepareForStateTransition();
            }

            _silkWindow.WindowState = targetState;
        }

        public WindowTransparencyLevel TransparencyLevel => _transparencyLevel;

#if AVALONIA11
        public IEnumerable<object> Surfaces => new object[] { _framebuffer };
#else
        public IPlatformRenderSurface[] Surfaces => new IPlatformRenderSurface[] { _framebuffer };
#endif

        public PixelPoint Position
        {
            get => _position;
            set
            {
                _position = value;
                if (_silkWindow != null)
                {
                    _silkWindow.Position = new Vector2D<int>((int)(value.X / _scaling), (int)(value.Y / _scaling));
                }
            }
        }

        public Action? Activated { get; set; }
        public Action? Deactivated { get; set; }
        public Func<WindowCloseReason, bool>? Closing { get; set; }
        public Action? Closed { get; set; }
        public Action<RawInputEventArgs>? Input { get; set; }
        public Action<Rect>? Paint { get; set; }
        public Action<Size, WindowResizeReason>? Resized { get; set; }
        public Action<double>? ScalingChanged { get; set; }
        public Action<PixelPoint>? PositionChanged { get; set; }
        public Action? LostFocus { get; set; }
        public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }

        public void Activate()
        {
            if (_silkWindow != null && _silkWindow.IsInitialized)
            {
                _silkWindow.Focus();
            }
        }

        public virtual void Show(bool activate, bool isDialog)
        {
            if (!_isShown)
            {
                _isShown = true;
                _silkWindow.Initialize();
            }
            else
            {
                _silkWindow.IsVisible = true;
            }
            if (activate)
            {
                Activate();
            }
        }

        public void Hide()
        {
            if (_silkWindow.IsInitialized)
            {
                _silkWindow.IsVisible = false;
            }
        }

        public void Close()
        {
            _silkWindow.Close();
        }

        public void SetTitle(string? title)
        {
            _title = title;
            if (_silkWindow != null)
            {
                _silkWindow.Title = title ?? "Avalonia Silk.NET Window";
            }
        }

        public void SetCursor(ICursorImpl? cursor)
        {
            _cursor = cursor as SilkNetCursorImpl;

            if (_inputContext is null)
            {
                return;
            }

            foreach (var mouse in _inputContext.Mice)
            {
                ApplyCursor(mouse.Cursor, _cursor);
            }
        }

        internal static void ApplyCursor(Silk.NET.Input.ICursor cursor, SilkNetCursorImpl? requestedCursor)
        {
            requestedCursor ??= new SilkNetCursorImpl(StandardCursorType.Arrow);

            var mode = requestedCursor.CursorMode;
            cursor.CursorMode = cursor.IsSupported(mode) ? mode : CursorMode.Normal;
            if (mode == CursorMode.Hidden)
            {
                return;
            }

            if (requestedCursor.CursorType == CursorType.Custom && requestedCursor.Image is { } image)
            {
                cursor.HotspotX = requestedCursor.HotSpot.X;
                cursor.HotspotY = requestedCursor.HotSpot.Y;
                cursor.Image = image;
                cursor.Type = CursorType.Custom;
                return;
            }

            var standardCursor = requestedCursor.StandardCursor;
            if (!cursor.IsSupported(standardCursor))
            {
                standardCursor = cursor.IsSupported(StandardCursor.Arrow)
                    ? StandardCursor.Arrow
                    : StandardCursor.Default;
            }

            cursor.StandardCursor = standardCursor;
            cursor.Type = CursorType.Standard;
        }

        public void SetIcon(IWindowIconImpl? icon)
        {
            _icon = icon;
            ApplyIcon();
        }

        private void ApplyIcon()
        {
            if (!_silkWindow.IsInitialized)
            {
                return;
            }

            if (_icon == null)
            {
                _silkWindow.SetWindowIcon(ReadOnlySpan<RawImage>.Empty);
                return;
            }

            if (_icon is SilkNetIconData iconData)
            {
                var images = new RawImage[iconData.Frames.Count];
                for (var index = 0; index < images.Length; index++)
                {
                    var frame = iconData.Frames[index];
                    images[index] = new RawImage(
                        frame.Width,
                        frame.Height,
                        frame.DecodePixels());
                }
                _silkWindow.SetWindowIcon(images);
                return;
            }

            using var stream = new MemoryStream();
            _icon.Save(stream);
            stream.Position = 0;
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(stream);
            var pixels = new byte[checked(image.Width * image.Height * 4)];
            image.CopyPixelDataTo(pixels);
            _silkWindow.SetWindowIcon(new[] { new RawImage(image.Width, image.Height, pixels) });
        }

        public void Invalidate(Rect rect)
        {
            if (_paintQueued) return;
            _paintQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    PaintNow();
                }
                else
                {
                    _paintQueued = false;
                }
            }, DispatcherPriority.Render);
        }

        public Point PointToClient(PixelPoint point)
        {
            return new Point(point.X - Position.X, point.Y - Position.Y) / _scaling;
        }

        public Point PointToClient(Point point)
        {
            var posLogical = new Point(Position.X / _scaling, Position.Y / _scaling);
            return point - posLogical;
        }

        public PixelPoint PointToScreen(Point point)
        {
            var p = point * _scaling;
            return new PixelPoint(Position.X + (int)p.X, Position.Y + (int)p.Y);
        }

        public void SetEnabled(bool enable)
        {
            _isEnabled = enable;
            _windowController.SetEnabled(enable);
        }

        public void SetTopmost(bool value)
        {
            _windowController.SetTopMost(value);
        }

        public void SetMinMaxSize(Size minSize, Size maxSize)
        {
            _minimumSize = minSize;
            _maximumSize = maxSize;
            _windowController.SetSizeConstraints(
                ToNativeMinimumSize(minSize),
                ToNativeMaximumSize(maxSize));
        }

        public void SetCanMinimize(bool value)
        {
            _canMinimize = value;
            _windowController.SetCanMinimize(value);
#if !AVALONIA11
            RaiseAllowedWindowActionsChanged();
#endif
        }

        public void SetCanMaximize(bool value)
        {
            _canMaximize = value;
            _windowController.SetCanMaximize(value);
#if !AVALONIA11
            RaiseAllowedWindowActionsChanged();
#endif
        }

        public void CanResize(bool value)
        {
            _canResize = value;
            _windowController.SetCanResize(value);
#if !AVALONIA11
            RaiseAllowedWindowActionsChanged();
#endif
        }

        public void
#if AVALONIA11
            SetSystemDecorations(SystemDecorations value)
#else
            SetWindowDecorations(WindowDecorations value)
#endif
        {
            _windowDecorations = value;
            _windowController.SetDecorations(value switch
            {
#if AVALONIA11
                SystemDecorations.None => NativeWindowDecorations.None,
                SystemDecorations.BorderOnly => NativeWindowDecorations.BorderOnly,
#else
                WindowDecorations.None => NativeWindowDecorations.None,
                WindowDecorations.BorderOnly => NativeWindowDecorations.BorderOnly,
#endif
                _ => NativeWindowDecorations.Full
            });
            ExtendClientAreaToDecorationsChanged?.Invoke(_isClientAreaExtended);
        }

        public void BeginMoveDrag(PointerPressedEventArgs e)
        {
            _windowController.BeginMove(GetCurrentNativePointerPoint());
        }

        public void BeginResizeDrag(WindowEdge edge, PointerPressedEventArgs e)
        {
            _windowController.BeginResize(edge switch
            {
                WindowEdge.West => NativeResizeEdge.Left,
                WindowEdge.North => NativeResizeEdge.Top,
                WindowEdge.East => NativeResizeEdge.Right,
                WindowEdge.South => NativeResizeEdge.Bottom,
                WindowEdge.NorthWest => NativeResizeEdge.TopLeft,
                WindowEdge.NorthEast => NativeResizeEdge.TopRight,
                WindowEdge.SouthWest => NativeResizeEdge.BottomLeft,
                _ => NativeResizeEdge.BottomRight
            }, GetCurrentNativePointerPoint());
        }

        public virtual IPopupImpl CreatePopup() => new SilkNetPopupImpl(this);

        public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> transparencyLevels)
        {
            _transparencyLevels = transparencyLevels.ToArray();
            ApplyTransparencyLevelHints();
        }

        private void ApplyTransparencyLevelHints()
        {
            var selected = WindowTransparencyLevel.None;
            var selectedBackdrop = false;
            var capabilities = GetEffectiveCapabilities();
            for (var index = 0; index < _transparencyLevels.Count; index++)
            {
                var candidate = _transparencyLevels[index];
                var backdrop = candidate == WindowTransparencyLevel.Mica
                    ? NativeWindowBackdrop.Mica
                    : candidate == WindowTransparencyLevel.AcrylicBlur
                        ? NativeWindowBackdrop.Acrylic
                        : candidate == WindowTransparencyLevel.Blur
                            ? NativeWindowBackdrop.Blur
                            : candidate == WindowTransparencyLevel.Transparent
                                ? NativeWindowBackdrop.Transparent
                                : NativeWindowBackdrop.None;
                var requiredFeature = backdrop switch
                {
                    NativeWindowBackdrop.Mica => NativeWindowFeatures.Mica,
                    NativeWindowBackdrop.Acrylic => NativeWindowFeatures.Acrylic,
                    NativeWindowBackdrop.Blur => NativeWindowFeatures.Blur,
                    NativeWindowBackdrop.Transparent => NativeWindowFeatures.Transparent,
                    _ => NativeWindowFeatures.None
                };
                if (requiredFeature != NativeWindowFeatures.None && !capabilities.Supports(requiredFeature))
                {
                    continue;
                }

                var applied = _windowController.SetBackdrop(backdrop);
                if (applied || !_windowController.IsAttached)
                {
                    selected = candidate;
                    selectedBackdrop = true;
                    break;
                }
            }

            if (!selectedBackdrop)
            {
                _windowController.SetBackdrop(NativeWindowBackdrop.None);
            }

            if (_transparencyLevel != selected)
            {
                _transparencyLevel = selected;
                TransparencyLevelChanged?.Invoke(selected);
            }
        }

        private NativeWindowCapabilities GetEffectiveCapabilities()
        {
            if (_windowController.IsAttached)
            {
                return _windowController.Capabilities;
            }

            return NativeWindowCapabilities.ForKind(
                NativeWindowCapabilities.DetectCurrentKind());
        }

        public object? TryGetFeature(Type featureType)
        {
            if (featureType == typeof(IScreenImpl))
            {
                return AvaloniaLocator.Current.GetService<IScreenImpl>();
            }
            return null;
        }

        private readonly TaskCompletionSource _disposedTcs = new();
        public Task DisposedTask => _disposedTcs.Task;
        private bool _disposed;
        public bool IsDisposed => _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _isLoaded = false;
            SilkNetPlatform.Instance.UnregisterWindow(this);

            try
            {
                _silkWindow.Load -= OnLoad;
                _silkWindow.Render -= OnRender;
                _silkWindow.Resize -= OnResize;
                _silkWindow.FramebufferResize -= OnFramebufferResize;
                _silkWindow.Move -= OnMove;
                _silkWindow.Closing -= OnClosing;
                _silkWindow.FocusChanged -= OnFocusChanged;
                _silkWindow.StateChanged -= OnStateChanged;
                WgpuContext.OnWebGpuDeviceLost -= OnWebGpuDeviceLost;
            }
            catch {}

            _windowController.Dispose();
            _framebuffer.Dispose();

            var windowToDispose = _silkWindow;
            var inputContextToDispose = _inputContext;
            var wgpuContextToDispose = _wgpuContext;
            _inputContext = null;
            _wgpuContext = null;

            var tcs = _disposedTcs;
            if (!windowToDispose.IsInitialized)
            {
                try
                {
                    DisposeRuntimeResources(
                        wgpuContextToDispose,
                        inputContextToDispose);
                    windowToDispose.Dispose();
                }
                catch {}
                finally
                {
                    tcs.TrySetResult();
                }
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    DisposeRuntimeResources(
                        wgpuContextToDispose,
                        inputContextToDispose);

                    try
                    {
                        windowToDispose.Dispose();
                    }
                    catch {}
                }
                catch {}
                finally
                {
                    tcs.TrySetResult();
                }
            });
        }

        private static void DisposeRuntimeResources(
            WgpuContext? wgpuContext,
            IInputContext? inputContext)
        {
            try
            {
                wgpuContext?.Dispose();
            }
            catch {}

            try
            {
                inputContext?.Dispose();
            }
            catch {}
        }

        // Missing interface members of IWindowImpl and ITopLevelImpl
        public void SetParent(IWindowImpl? parent)
        {
            if (parent is WindowImpl silkParent)
            {
                _nativeParent = silkParent;
                _windowController.SetParent(silkParent._windowController.Handle);
            }
            else if (parent is null)
            {
                _nativeParent = null;
                _windowController.SetParent(NativeWindowHandle.Empty);
            }
        }

        internal WindowImpl? NativeParent => _nativeParent;

        public void ShowTaskbarIcon(bool value)
        {
            _windowController.SetShowInTaskbar(value);
        }
        public void Resize(Size value, WindowResizeReason reason)
        {
            _clientSize = value;
            if (_silkWindow != null)
            {
                _silkWindow.Size = new Vector2D<int>((int)value.Width, (int)value.Height);
            }
        }
        public void Move(PixelPoint point) => Position = point;
        private bool _isClientAreaExtended;

        public void SetExtendClientAreaToDecorationsHint(bool extend)
        {
            _isClientAreaExtended = extend;
            _windowController.SetClientAreaExtension(extend, _titleBarHeight);
            ExtendClientAreaToDecorationsChanged?.Invoke(extend);
        }

#if AVALONIA11
        public void SetExtendClientAreaChromeHints(ExtendClientAreaChromeHints hints)
        {
        }

        public void GetWindowsZOrder(Span<Avalonia.Controls.Window> windows, Span<long> zOrder)
        {
            for (var i = 0; i < windows.Length && i < zOrder.Length; i++)
            {
                zOrder[i] = i;
            }
        }
#endif

        public void SetExtendClientAreaTitleBarHeightHint(double titleBarHeight)
        {
            _titleBarHeight = double.IsFinite(titleBarHeight) && titleBarHeight >= 0d
                ? titleBarHeight
                : -1d;
            _windowController.SetTitleBarHeight(_titleBarHeight);
            ExtendClientAreaToDecorationsChanged?.Invoke(_isClientAreaExtended);
        }

        public void SetFrameThemeVariant(PlatformThemeVariant themeVariant)
        {
            _themeVariant = themeVariant;
            _windowController.SetTheme(
                themeVariant == PlatformThemeVariant.Dark
                    ? NativeWindowTheme.Dark
                    : NativeWindowTheme.Light);
        }
        public bool WindowStateGetterIsUsable => true;
        public Action<Avalonia.Controls.WindowState>? WindowStateChanged { get; set; }
        public Action? GotInputWhenDisabled { get; set; }
        public bool IsClientAreaExtendedToDecorations => _isClientAreaExtended;
        public Action<bool>? ExtendClientAreaToDecorationsChanged { get; set; }
        public bool NeedsManagedDecorations => _windowController.RequiresManagedDecorations;
        public Thickness ExtendedMargins =>
            _isClientAreaExtended &&
            !NeedsManagedDecorations &&
            _windowDecorations ==
#if AVALONIA11
            SystemDecorations.Full &&
#else
            WindowDecorations.Full &&
#endif
            _windowState != Avalonia.Controls.WindowState.FullScreen
                ? new Thickness(0, _windowController.ExtendedTitleBarHeight, 0, 0)
                : default;
        public Thickness OffScreenMargin => new Thickness();
        public Avalonia.Rendering.Composition.Compositor Compositor => SilkNetPlatform.Compositor;
        public AcrylicPlatformCompensationLevels AcrylicCompensationLevels => new(1.0, 0.8, 0.0);
#if !AVALONIA11
        public Avalonia.Controls.Platform.PlatformRequestedDrawnDecoration RequestedDrawnDecorations
        {
            get
            {
                var requested = _windowController.RequestedDrawnDecorations;
                var result = Avalonia.Controls.Platform.PlatformRequestedDrawnDecoration.None;
                if (requested.HasFlag(NativeDrawnDecorationParts.TitleBar))
                    result |= Avalonia.Controls.Platform.PlatformRequestedDrawnDecoration.TitleBar;
                if (requested.HasFlag(NativeDrawnDecorationParts.Border))
                    result |= Avalonia.Controls.Platform.PlatformRequestedDrawnDecoration.Border;
                if (requested.HasFlag(NativeDrawnDecorationParts.ResizeGrips))
                    result |= Avalonia.Controls.Platform.PlatformRequestedDrawnDecoration.ResizeGrips;
                if (requested.HasFlag(NativeDrawnDecorationParts.Shadow))
                    result |= Avalonia.Controls.Platform.PlatformRequestedDrawnDecoration.Shadow;
                return result;
            }
        }
        public PlatformAllowedWindowActions AllowedWindowActions
        {
            get
            {
                var actions = PlatformAllowedWindowActions.Fullscreen;
                if (_canMinimize)
                    actions |= PlatformAllowedWindowActions.Minimize;
                if (_canMaximize && _canResize)
                    actions |= PlatformAllowedWindowActions.Maximize;
                return actions;
            }
        }

        public Action<PlatformAllowedWindowActions>? AllowedWindowActionsChanged { get; set; }

        private void RaiseAllowedWindowActionsChanged()
        {
            AllowedWindowActionsChanged?.Invoke(AllowedWindowActions);
        }
#endif

        private void ApplyWindowCustomizationState()
        {
            _windowController.SetDecorations(_windowDecorations switch
            {
#if AVALONIA11
                SystemDecorations.None => NativeWindowDecorations.None,
                SystemDecorations.BorderOnly => NativeWindowDecorations.BorderOnly,
#else
                WindowDecorations.None => NativeWindowDecorations.None,
                WindowDecorations.BorderOnly => NativeWindowDecorations.BorderOnly,
#endif
                _ => NativeWindowDecorations.Full
            });
            _windowController.SetCanResize(_canResize);
            _windowController.SetCanMinimize(_canMinimize);
            _windowController.SetCanMaximize(_canMaximize);
            _windowController.SetSizeConstraints(
                ToNativeMinimumSize(_minimumSize),
                ToNativeMaximumSize(_maximumSize));
            _windowController.SetClientAreaExtension(_isClientAreaExtended, _titleBarHeight);
            _windowController.SetTheme(
                _themeVariant == PlatformThemeVariant.Dark
                    ? NativeWindowTheme.Dark
                    : NativeWindowTheme.Light);
        }

        private static NativeWindowSize ToNativeMinimumSize(Size size)
        {
            return new NativeWindowSize(
                double.IsFinite(size.Width) ? Math.Max(0, (int)Math.Ceiling(size.Width)) : 0,
                double.IsFinite(size.Height) ? Math.Max(0, (int)Math.Ceiling(size.Height)) : 0);
        }

        private static NativeWindowSize ToNativeMaximumSize(Size size)
        {
            return new NativeWindowSize(
                double.IsFinite(size.Width) ? Math.Max(1, (int)Math.Ceiling(size.Width)) : int.MaxValue,
                double.IsFinite(size.Height) ? Math.Max(1, (int)Math.Ceiling(size.Height)) : int.MaxValue);
        }

        private double GetPrimaryMonitorScale()
        {
            try
            {
                var glfw = Silk.NET.GLFW.Glfw.GetApi();
                unsafe
                {
                    bool initialized = glfw.Init();
                    if (initialized)
                    {
                        var monitors = glfw.GetMonitors(out int count);
                        if (count > 0)
                        {
                            float xscale, yscale;
                            glfw.GetMonitorContentScale(monitors[0], out xscale, out yscale);
                            return xscale;
                        }
                    }
                }
            }
            catch {}
            return 1.0;
        }

        private double GetWindowScaling()
        {
            try
            {
                var glfw = Silk.NET.GLFW.Glfw.GetApi();
                unsafe
                {
                    var monitors = glfw.GetMonitors(out int count);
                    if (count > 0)
                    {
                        var winX = _silkWindow.Position.X;
                        var winY = _silkWindow.Position.Y;

                        var bestMonitor = monitors[0];
                        var minDistanceSq = double.MaxValue;

                        for (int i = 0; i < count; i++)
                        {
                            var m = monitors[i];
                            glfw.GetMonitorPos(m, out int mx, out int my);
                            var vm = glfw.GetVideoMode(m);
                            if (vm != null)
                            {
                                int mw = vm->Width;
                                int mh = vm->Height;

                                if (winX >= mx && winX < mx + mw && winY >= my && winY < my + mh)
                                {
                                    float xscale, yscale;
                                    glfw.GetMonitorContentScale(m, out xscale, out yscale);
                                    return xscale;
                                }

                                var cx = mx + mw / 2.0;
                                var cy = my + mh / 2.0;
                                var dx = winX - cx;
                                var dy = winY - cy;
                                var distSq = dx * dx + dy * dy;
                                if (distSq < minDistanceSq)
                                {
                                    minDistanceSq = distSq;
                                    bestMonitor = m;
                                }
                            }
                        }

                        float bxscale, byscale;
                        glfw.GetMonitorContentScale(bestMonitor, out bxscale, out byscale);
                        return bxscale;
                    }
                }
            }
            catch {}
            return 1.0;
        }
    }

    internal sealed class SilkNetKeyboardDevice : KeyboardDevice
    {
#pragma warning disable CS0108 // KeyboardDevice.Instance is internal in the Avalonia 12.0.5 package.
        public static SilkNetKeyboardDevice Instance { get; } = new();
#pragma warning restore CS0108
        private SilkNetKeyboardDevice() {}
    }
}
