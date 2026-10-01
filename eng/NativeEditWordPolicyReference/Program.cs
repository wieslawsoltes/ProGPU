using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || args.Length != 2)
            throw new ArgumentException("Expected original Windows X64, mode and CreateNew receipt path.");
        return args[0] switch
        {
            "original24" => WordSelectionReference.Run(args[1], "PerMonitorV2", "true"),
            "contexts" => WordSelectionReference.RunPolicyContexts(args[1]),
            "symbol-attributes" => WordSelectionReference.RunSymbolAttributes(args[1]),
            _ => throw new ArgumentOutOfRangeException(nameof(args))
        };
    }
}
