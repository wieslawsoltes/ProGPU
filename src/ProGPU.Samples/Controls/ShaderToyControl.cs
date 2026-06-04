using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Numerics;
using System.Diagnostics;
using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using ProGPU.Vector;
using ProGPU.Backend;
using ProGPU.Samples;

namespace Microsoft.UI.Xaml.Controls
{
    public class ShaderToyControl : FrameworkElement, IAnimatedElement
    {
        public static readonly DependencyProperty ShaderSourceProperty =
            DependencyProperty.Register(
                "ShaderSource",
                typeof(string),
                typeof(ShaderToyControl),
                new PropertyMetadata(string.Empty, OnShaderSourceChanged) { AffectsRender = true });

        public string ShaderSource
        {
            get => GetValue(ShaderSourceProperty) as string ?? string.Empty;
            set => SetValue(ShaderSourceProperty, value);
        }

        public static readonly DependencyProperty TimeScaleProperty =
            DependencyProperty.Register(
                "TimeScale",
                typeof(float),
                typeof(ShaderToyControl),
                new PropertyMetadata(1.0f) { AffectsRender = true });

        public float TimeScale
        {
            get => (float)(GetValue(TimeScaleProperty) ?? 1.0f);
            set => SetValue(TimeScaleProperty, value);
        }

        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(
                "IsPlaying",
                typeof(bool),
                typeof(ShaderToyControl),
                new PropertyMetadata(true) { AffectsRender = true });

        public bool IsPlaying
        {
            get => (bool)(GetValue(IsPlayingProperty) ?? true);
            set => SetValue(IsPlayingProperty, value);
        }

        public event Action<string>? CompilationFailed;
        public event Action? CompilationSucceeded;

        private readonly ShaderToyParams _params = new ShaderToyParams();
        private readonly object _errorLock = new();
        
        private string? _compileError;
        private bool _compilePending;
        private bool _renderedThisKey;
        
        private float _time;
        private float _frame;
        private Vector4 _mouse;
        private Vector2? _clickPos;
        private Vector2 _currentMousePos;
        private bool _isMouseDown;

        public string? CompileError
        {
            get { lock (_errorLock) return _compileError; }
            private set { lock (_errorLock) _compileError = value; }
        }

        public float Time => _time;
        public float Frame => _frame;

        public ShaderToyControl()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            IsTabStop = true;

            WgpuContext.OnWebGpuError += HandleWebGpuError;
            Unloaded += (s, e) =>
            {
                WgpuContext.OnWebGpuError -= HandleWebGpuError;
            };
        }

        private static void OnShaderSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (ShaderToyControl)d;
            control.UpdateShaderSource(e.NewValue as string ?? string.Empty);
        }

        private void UpdateShaderSource(string source)
        {
            var oldKey = _params.ShaderKey;
            _params.ShaderSource = source;
            _params.ShaderKey = Guid.NewGuid().ToString("N");
            _params.OldShaderKey = oldKey;
            
            CompileError = null;
            _compilePending = true;
            _renderedThisKey = false;
            
            Invalidate();
        }

        private void HandleWebGpuError(Silk.NET.WebGPU.ErrorType type, string message)
        {
            if (_compilePending)
            {
                lock (_errorLock)
                {
                    if (_compileError == null)
                        _compileError = message;
                    else
                        _compileError += "\n" + message;
                }
            }
        }

        public void Reset()
        {
            _time = 0f;
            _frame = 0f;
            _mouse = Vector4.Zero;
            _clickPos = null;
            Invalidate();
        }

        public void Update(float delta)
        {
            if (IsPlaying)
            {
                _time += delta * TimeScale;
                _frame += 1.0f;
                Invalidate();
            }

            // Heuristic check to see if compilation succeeded/failed after rendering
            if (_compilePending && _renderedThisKey)
            {
                var error = CompileError;
                if (error != null)
                {
                    Microsoft.UI.Xaml.Input.InputSystem.DispatcherQueue?.Invoke(() =>
                    {
                        CompilationFailed?.Invoke(error);
                    });
                }
                else
                {
                    Microsoft.UI.Xaml.Input.InputSystem.DispatcherQueue?.Invoke(() =>
                    {
                        CompilationSucceeded?.Invoke();
                    });
                }
                _compilePending = false;
            }
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            float w = float.IsInfinity(availableSize.X) ? 400f : availableSize.X;
            float h = float.IsInfinity(availableSize.Y) ? 300f : availableSize.Y;
            return new Vector2(w, h);
        }

        private WgpuContext? GetActiveWgpuContext()
        {
            var activeWindows = WindowManager.ActiveWindows;
            if (activeWindows.Count == 0) return null;
            if (activeWindows.Count == 1) return activeWindows[0].WgpuContext;

            Visual? current = this;
            while (current != null)
            {
                for (int i = 0; i < activeWindows.Count; i++)
                {
                    if (activeWindows[i].Content == current)
                    {
                        return activeWindows[i].WgpuContext;
                    }
                }
                current = current.Parent;
            }

            return activeWindows[0].WgpuContext;
        }

        public override void OnRender(DrawingContext context)
        {
            if (Size.X <= 0 || Size.Y <= 0 || string.IsNullOrEmpty(_params.ShaderSource)) return;

            var wgpuContext = GetActiveWgpuContext();
            if (wgpuContext == null) return;

            float dpiScale = 1.0f;
            if (wgpuContext.Window != null && wgpuContext.Window.Size.X > 0)
            {
                dpiScale = (float)wgpuContext.Window.FramebufferSize.X / wgpuContext.Window.Size.X;
            }

            // 1. Calculate resolution uniform (in physical pixels)
            float width = Size.X * dpiScale;
            float height = Size.Y * dpiScale;
            _params.Resolution = new Vector3(width, height, 1.0f);
            _params.Rect = new Rect(Vector2.Zero, Size);

            // 2. Calculate iMouse uniform inputs in standard ShaderToy bottom-left y-up space
            if (_isMouseDown)
            {
                _mouse.X = _currentMousePos.X * dpiScale;
                _mouse.Y = (Size.Y - _currentMousePos.Y) * dpiScale;
                if (_clickPos.HasValue)
                {
                    _mouse.Z = _clickPos.Value.X * dpiScale;
                    _mouse.W = (Size.Y - _clickPos.Value.Y) * dpiScale;
                }
            }
            else
            {
                // When mouse is up, Z and W are negated in ShaderToy to signal mouse button up
                if (_clickPos.HasValue)
                {
                    _mouse.Z = -MathF.Abs(_clickPos.Value.X * dpiScale);
                    _mouse.W = -MathF.Abs((Size.Y - _clickPos.Value.Y) * dpiScale);
                }
            }
            _params.Mouse = _mouse;

            // 3. iDate uniform inputs
            var now = DateTime.Now;
            float timeOfDaySeconds = (float)(now.TimeOfDay.TotalSeconds);
            _params.Date = new Vector4(now.Year, now.Month - 1, now.Day, timeOfDaySeconds);

            // 4. Time parameters
            _params.Time = _time;
            _params.Frame = _frame;

            // 5. Build and enqueue custom drawing extension command
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawExtension,
                ExtensionId = CompositorBuiltInExtensions.ShaderToy,
                Rect = _params.Rect,
                DataParam = _params
            });

            _renderedThisKey = true;

            base.OnRender(context);
        }

        public override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (IsEnabled && e.IsLeftButtonPressed)
            {
                e.Handled = true;
                InputSystem.SetFocus(this);
                InputSystem.CapturePointer(this);

                _isMouseDown = true;
                _clickPos = e.Position;
                _currentMousePos = e.Position;
                Invalidate();
            }
            base.OnPointerPressed(e);
        }

        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            if (IsEnabled && _isMouseDown)
            {
                e.Handled = true;
                _currentMousePos = e.Position;
                Invalidate();
            }
            base.OnPointerMoved(e);
        }

        public override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            if (IsEnabled && _isMouseDown)
            {
                e.Handled = true;
                InputSystem.ReleasePointerCapture();
                _isMouseDown = false;
                Invalidate();
            }
            base.OnPointerReleased(e);
        }
    }
}
