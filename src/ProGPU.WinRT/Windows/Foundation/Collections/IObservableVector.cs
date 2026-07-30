namespace Windows.Foundation.Collections;

/// <summary>
/// Describes the action that changed an observable vector.
/// </summary>
public enum CollectionChange
{
    Reset = 0,
    ItemInserted = 1,
    ItemRemoved = 2,
    ItemChanged = 3
}

/// <summary>
/// Provides the kind and zero-based position of an observable-vector change.
/// </summary>
public interface IVectorChangedEventArgs
{
    CollectionChange CollectionChange { get; }
    uint Index { get; }
}

/// <summary>
/// Handles a change notification from an observable vector.
/// </summary>
public delegate void VectorChangedEventHandler<T>(
    IObservableVector<T> sender,
    IVectorChangedEventArgs @event);

/// <summary>
/// WinRT-shaped observable vector projected onto the .NET list contract.
/// </summary>
public interface IObservableVector<T> : IList<T>
{
    event VectorChangedEventHandler<T>? VectorChanged;
}
