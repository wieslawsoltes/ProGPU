namespace ProGPU.Hmi;

/// <summary>Checks the reviewed session generation under the same transport gate that admits the write.</summary>
public interface IHmiConditionalWriteConnection : IHmiConnection, IHmiConnectionGeneration
{
    ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, long expectedConnectionGeneration, CancellationToken cancellationToken);
}
