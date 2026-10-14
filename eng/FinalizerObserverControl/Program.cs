using System.Runtime.InteropServices;

internal static class FinalizerObserverControl
{
    [DllImport("libc", EntryPoint = "gettid")]
    private static extern int GetThreadId();

    private static void WaitForRelease(string nameFile)
    {
        File.WriteAllText(nameFile, File.ReadAllText($"/proc/self/task/{GetThreadId()}/comm"));
        Thread.Sleep(TimeSpan.FromSeconds(8));
    }

    private static void Main(string[] args)
    {
        var thread = new Thread(() => WaitForRelease(args[0]))
        {
            Name = "Cache sampler finalizer transfer"
        };
        thread.Start();
        thread.Join();
    }
}
