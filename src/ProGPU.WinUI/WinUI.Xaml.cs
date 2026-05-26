using System;

namespace Microsoft.UI.Xaml
{
    public struct Thickness
    {
        public float Left { get; set; }
        public float Top { get; set; }
        public float Right { get; set; }
        public float Bottom { get; set; }

        public float Horizontal => Left + Right;
        public float Vertical => Top + Bottom;

        public Thickness(float uniformLength)
        {
            Left = Top = Right = Bottom = uniformLength;
        }

        public Thickness(float horizontal, float vertical)
        {
            Left = Right = horizontal;
            Top = Bottom = vertical;
        }

        public Thickness(float left, float top, float right, float bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public static implicit operator ProGPU.Layout.Thickness(Thickness t)
        {
            return new ProGPU.Layout.Thickness(t.Left, t.Top, t.Right, t.Bottom);
        }

        public static implicit operator Thickness(ProGPU.Layout.Thickness t)
        {
            return new Thickness(t.Left, t.Top, t.Right, t.Bottom);
        }

        public static Thickness operator +(Thickness a, Thickness b)
        {
            return new Thickness(a.Left + b.Left, a.Top + b.Top, a.Right + b.Right, a.Bottom + b.Bottom);
        }
    }


    public class UIElement : DependencyObject
    {
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
