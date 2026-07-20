using System;

namespace Microsoft.UI.Xaml
{
    public enum Visibility
    {
        Visible = 0,
        Collapsed = 1
    }

    public enum TextWrapping
    {
        NoWrap = 1,
        Wrap = 2,
        WrapWholeWords = 3
    }

    public class UIElement : DependencyObject
    {
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
}
