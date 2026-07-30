using System;
using System.ComponentModel;

namespace Microsoft.UI.Xaml;

public static class BindingExtensions
{
    private class ActionDisposable : IDisposable
    {
        private Action? _action;

        public ActionDisposable(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            var action = global::System.Threading.Interlocked.Exchange(ref _action, null);
            action?.Invoke();
        }
    }

    /// <summary>
    /// Binds a DependencyProperty on a DependencyObject to a property on a ViewModel implementing INotifyPropertyChanged.
    /// Fully NativeAOT and Trim-safe.
    /// </summary>
    public static IDisposable Bind<TTarget, TViewModel, TProp>(
        this TTarget target,
        DependencyProperty targetProperty,
        TViewModel viewModel,
        Func<TViewModel, TProp> getter,
        string propertyName)
        where TTarget : DependencyObject
        where TViewModel : INotifyPropertyChanged
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (targetProperty == null) throw new ArgumentNullException(nameof(targetProperty));
        if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));
        if (getter == null) throw new ArgumentNullException(nameof(getter));
        if (string.IsNullOrEmpty(propertyName)) throw new ArgumentException("Property name must be specified.", nameof(propertyName));

        // Define the update action
        Action updateAction = () =>
        {
            try
            {
                var val = getter(viewModel);
                target.SetValue(targetProperty, val);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Binding] Error evaluating binding for '{propertyName}': {ex.Message}");
            }
        };

        // Run the initial update to sync target with source
        updateAction();

        // Register the event handler for change notifications
        PropertyChangedEventHandler handler = (sender, e) =>
        {
            if (e.PropertyName == propertyName)
            {
                updateAction();
            }
        };

        viewModel.PropertyChanged += handler;

        // Return a disposable that safely unsubscribes
        return new ActionDisposable(() => viewModel.PropertyChanged -= handler);
    }

    /// <summary>
    /// Binds a DependencyProperty on a DependencyObject to a standard IObservable stream.
    /// Fully NativeAOT and Trim-safe.
    /// </summary>
    public static IDisposable Bind<TTarget, TProp>(
        this TTarget target,
        DependencyProperty targetProperty,
        IObservable<TProp> observable)
        where TTarget : DependencyObject
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (targetProperty == null) throw new ArgumentNullException(nameof(targetProperty));
        if (observable == null) throw new ArgumentNullException(nameof(observable));

        var subscription = observable.Subscribe(new Observer<TProp>(
            value =>
            {
                target.SetValue(targetProperty, value);
            },
            ex =>
            {
                Console.WriteLine($"[Binding] Observable binding error: {ex.Message}");
            }
        ));

        return subscription;
    }

    private class Observer<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;
        private readonly Action<Exception> _onError;

        public Observer(Action<T> onNext, Action<Exception> onError)
        {
            _onNext = onNext;
            _onError = onError;
        }

        public void OnCompleted() { }

        public void OnError(Exception error)
        {
            _onError(error);
        }

        public void OnNext(T value)
        {
            _onNext(value);
        }
    }
}
