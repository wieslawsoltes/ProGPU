namespace Windows.Foundation;

public delegate void TypedEventHandler<TSender, TResult>(
    TSender sender,
    TResult args);

public interface IAsyncAction
{
    Task AsTask();

    System.Runtime.CompilerServices.TaskAwaiter GetAwaiter();
}

public interface IAsyncOperation<TResult>
{
    Task<TResult> AsTask();

    System.Runtime.CompilerServices.TaskAwaiter<TResult>
        GetAwaiter();
}

public readonly record struct Point(
    double X,
    double Y)
{
    public static implicit operator System.Numerics.Vector2(
        Point value) =>
        new((float)value.X, (float)value.Y);

    public static implicit operator Point(
        System.Numerics.Vector2 value) =>
        new(value.X, value.Y);
}

public readonly record struct Size(
    double Width,
    double Height);

public readonly record struct Rect(
    double X,
    double Y,
    double Width,
    double Height);
