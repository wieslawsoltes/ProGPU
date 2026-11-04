using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;

internal static class OriginalModules
{
    // Observation after all original draws; never loads a native module.
    internal static object Capture()
    {
        using Process process = Process.GetCurrentProcess();
        var native = new List<object>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessModule module in process.Modules)
        {
            if (!string.Equals(module.ModuleName, "gdi32.dll", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(module.ModuleName, "gdiplus.dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (!found.Add(module.ModuleName)) throw new InvalidOperationException("Duplicate original native image.");
            native.Add(Describe(module.FileName));
        }
        if (found.Count != 2) throw new InvalidOperationException("Both actual original native images are required.");
        return new { architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            managed = new[] { Describe(typeof(Form).Assembly.Location), Describe(typeof(Graphics).Assembly.Location),
                Describe(typeof(object).Assembly.Location) }, native };
    }

    private static object Describe(string path)
    {
        string full = Path.GetFullPath(path);
        using FileStream stream = File.OpenRead(full);
        long bytes = stream.Length;
        string sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new { path = full, bytes, sha256, version = FileVersionInfo.GetVersionInfo(full).FileVersion };
    }
}
