namespace System.Drawing.Printing;

public class PrintDocument : IDisposable
{
    public string DocumentName { get; set; } = string.Empty;

    public void Print()
    {
        throw new PlatformNotSupportedException("System.Drawing.Printing is not available on this ProGPU System.Drawing target.");
    }

    public void Dispose()
    {
    }
}
