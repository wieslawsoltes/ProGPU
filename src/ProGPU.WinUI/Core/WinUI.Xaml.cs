using System;

namespace Microsoft.UI.Xaml
{
    public enum Visibility
    {
        Visible = 0,
        Collapsed = 1
    }

    public enum FlowDirection
    {
        LeftToRight = 0,
        RightToLeft = 1
    }

    public enum TextAlignment
    {
        Center = 0,
        Left = 1,
        Start = 1,
        Right = 2,
        End = 2,
        Justify = 3,
        DetectFromContent = 4
    }

    public enum TextReadingOrder
    {
        Default = 0,
        UseFlowDirection = 0,
        DetectFromContent = 1
    }

    public enum TextWrapping
    {
        NoWrap = 1,
        Wrap = 2,
        WrapWholeWords = 3
    }

    public enum TextTrimming
    {
        None = 0,
        CharacterEllipsis = 1,
        WordEllipsis = 2,
        Clip = 3
    }

    public enum OpticalMarginAlignment
    {
        None = 0,
        TrimSideBearings = 1
    }

    public enum ElementSoundMode
    {
        Default = 0,
        FocusOnly = 1,
        Off = 2
    }

    public partial class UIElement : DependencyObject
    {
        private Automation.Peers.AutomationPeer? _automationPeer;

        public static readonly DependencyProperty UseSystemFocusVisualsProperty = DependencyProperty.Register(
            nameof(UseSystemFocusVisuals), typeof(bool), typeof(UIElement), new PropertyMetadata(false));

        public bool UseSystemFocusVisuals
        {
            get => (bool)(GetValue(UseSystemFocusVisualsProperty) ?? false);
            set => SetValue(UseSystemFocusVisualsProperty, value);
        }

        public static readonly DependencyProperty RenderTransformProperty = DependencyProperty.Register(
            nameof(RenderTransform),
            typeof(Media.Transform),
            typeof(UIElement),
            new PropertyMetadata(null, static (d, e) =>
            {
                var element = (UIElement)d;
                element.Transform = (e.NewValue as Media.Transform)?.Value ?? global::System.Numerics.Matrix4x4.Identity;
            }) { AffectsRender = true });

        public Media.Transform? RenderTransform
        {
            get => GetValue(RenderTransformProperty) as Media.Transform;
            set => SetValue(RenderTransformProperty, value);
        }

        protected virtual Automation.Peers.AutomationPeer? OnCreateAutomationPeer() => null;

        internal Automation.Peers.AutomationPeer? GetOrCreateAutomationPeer() =>
            _automationPeer ??= OnCreateAutomationPeer();

        public static readonly DependencyProperty VisibilityProperty =
            DependencyProperty.Register(
                "Visibility",
                typeof(Visibility),
                typeof(UIElement),
                new PropertyMetadata(Visibility.Visible, (d, e) => {
                    var element = (UIElement)d;
                    var val = (Visibility)(e.NewValue ?? Visibility.Visible);
                    element.SetVisibilityLayout(val);
                }));

        private void SetVisibilityLayout(Visibility val)
        {
            this.IsVisible = val == Visibility.Visible;
            this.IsCollapsed = val == Visibility.Collapsed;
        }

        public Visibility Visibility
        {
            get => (Visibility)(GetValue(VisibilityProperty) ?? Visibility.Visible);
            set => SetValue(VisibilityProperty, value);
        }
    }

    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);
    public delegate void UnhandledExceptionEventHandler(object sender, UnhandledExceptionEventArgs e);

    public class UnhandledExceptionEventArgs : EventArgs
    {
        public Exception Exception { get; internal set; } = new Exception("Unhandled XAML exception");
        public bool Handled { get; set; }
        public string Message => Exception.Message;
    }

    public partial class FrameworkElement : UIElement, global::System.ComponentModel.INotifyPropertyChanged
    {
        public event global::System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;


        public event Windows.Foundation.TypedEventHandler<Microsoft.UI.Xaml.FrameworkElement, object>? Loading;
        public event RoutedEventHandler? Unloaded;
        public event EventHandler<UnhandledExceptionEventArgs>? UnhandledException;

        public void FireLoading() => Loading?.Invoke(this, new object());
        public void FireUnloaded() => Unloaded?.Invoke(this, new Microsoft.UI.Xaml.RoutedEventArgs());
        public void FireUnhandledException(Exception ex) => UnhandledException?.Invoke(this, new UnhandledExceptionEventArgs { Exception = ex });
    }

    
}

namespace Windows.Foundation
{
    internal sealed class CompletedAsyncOperation<TResult> : IAsyncOperation<TResult>
    {
        private readonly global::System.Threading.Tasks.Task<TResult> _task;
        public CompletedAsyncOperation(TResult result) => _task = global::System.Threading.Tasks.Task.FromResult(result);
        public global::System.Threading.Tasks.Task<TResult> AsTask() => _task;
        public global::System.Runtime.CompilerServices.TaskAwaiter<TResult> GetAwaiter() => _task.GetAwaiter();
    }
}
