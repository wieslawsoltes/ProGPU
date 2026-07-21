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

    public class UIElement : DependencyObject
    {
        private Automation.Peers.AutomationPeer? _automationPeer;

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

    public class UnhandledExceptionEventArgs : EventArgs
    {
        public Exception Exception { get; set; } = new Exception("Unhandled XAML exception");
    }

    public partial class FrameworkElement : UIElement, System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;


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
    public delegate void TypedEventHandler<TSender, TResult>(TSender sender, TResult args);

    public interface IAsyncOperation<TResult>
    {
        System.Threading.Tasks.Task<TResult> AsTask();
        System.Runtime.CompilerServices.TaskAwaiter<TResult> GetAwaiter();
    }

    internal sealed class CompletedAsyncOperation<TResult> : IAsyncOperation<TResult>
    {
        private readonly System.Threading.Tasks.Task<TResult> _task;
        public CompletedAsyncOperation(TResult result) => _task = System.Threading.Tasks.Task.FromResult(result);
        public System.Threading.Tasks.Task<TResult> AsTask() => _task;
        public System.Runtime.CompilerServices.TaskAwaiter<TResult> GetAwaiter() => _task.GetAwaiter();
    }

    public readonly record struct Point(double X, double Y);

    public readonly record struct Rect(double X, double Y, double Width, double Height);
}
