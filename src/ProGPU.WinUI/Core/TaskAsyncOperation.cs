namespace Windows.Foundation;

internal sealed class TaskAsyncOperation<TResult> :
    IAsyncOperation<TResult>
{
    private readonly Task<TResult> _task;

    internal TaskAsyncOperation(
        Task<TResult> task)
    {
        _task = task;
    }

    public Task<TResult> AsTask() => _task;

    public global::System.Runtime.CompilerServices
        .TaskAwaiter<TResult> GetAwaiter() =>
        _task.GetAwaiter();
}
