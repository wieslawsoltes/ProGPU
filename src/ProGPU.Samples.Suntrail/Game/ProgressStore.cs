using System.Globalization;

namespace ProGPU.Samples.Suntrail.Game;

/// <summary>Small versioned local save. Browser hosts replace these delegates with localStorage.</summary>
public static class ProgressStore
{
    private static string SavePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProGPU", "Suntrail", "progress-v1.txt");
    public static int Load()
    {
        if (OperatingSystem.IsBrowser()) return 0;
        try { return int.TryParse(File.ReadAllText(SavePath), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? Math.Clamp(value,0,7) : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
    public static void Save(int unlocked)
    {
        if (OperatingSystem.IsBrowser()) return;
        try
        {
            string path=SavePath;Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path+".tmp",Math.Clamp(unlocked,0,7).ToString(CultureInfo.InvariantCulture));
            File.Move(path+".tmp",path,true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
