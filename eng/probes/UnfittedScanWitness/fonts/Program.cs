using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

internal static class Program
{
    private const string Text = "Name Age 123 Hello Windows Forms";
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || args.Length != 1 ||
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
                throw new InvalidOperationException("Disposable x64 original font-only capture required.");
            string temporary=Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")!);
            string output=Path.GetFullPath(args[0]);
            if(output!=args[0] || !output.StartsWith(temporary+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || Directory.Exists(output) || File.Exists(output))
                throw new InvalidOperationException("Fresh owned metadata output required.");
            foreach(Type type in new[]{typeof(Form),typeof(Graphics)})
                if(!type.Assembly.Location.Contains("Microsoft.WindowsDesktop.App",StringComparison.Ordinal))throw new InvalidOperationException("Original Microsoft source required.");
            string fontDirectory=Path.Combine(temporary,"text-raster-font-inputs");
            if(Directory.Exists(fontDirectory) || File.Exists(fontDirectory))throw new InvalidOperationException("Private font transfer already exists.");
            Directory.CreateDirectory(output);Directory.CreateDirectory(fontDirectory);
            Stopwatch clock=Stopwatch.StartNew();List<object> fonts=[];
            foreach(var s in new[]{(Name:"Microsoft Sans Serif",Points:9.75f),(Name:"Segoe UI",Points:9f)})
            {
                using Font font=new(s.Name,s.Points,FontStyle.Regular,GraphicsUnit.Point,0);
                if(font.Name!=s.Name)throw new InvalidOperationException("Original font substituted.");
                fonts.Add(ObserveFont(font,fontDirectory));
                if(clock.Elapsed.TotalSeconds>45)throw new TimeoutException("Original font-source45-second bound exceeded.");
            }
            var smoothing=new[]{0x4au,0x200au,0x200cu,0x2012u}.Select(action=>{
                bool success=SystemParametersInfoW(action,0,out uint value,0);return new{action,success,value,error=success?0:Marshal.GetLastWin32Error()};}).ToArray();
            var identity=OriginalModules.Capture();
            if(clock.Elapsed.TotalSeconds>45)throw new TimeoutException("Original font-source45-second bound exceeded.");
            File.WriteAllText(Path.Combine(output,"fonts.json"),JsonSerializer.Serialize(new{
                schema="original-font-only-v1",qualified=false,inputEvents=0,desktopWindows=0,
                text=Text,fonts,smoothing,loadedIdentity=identity,elapsedSeconds=clock.Elapsed.TotalSeconds},new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }

    private static object ObserveFont(Font font, string fontDirectory)
    {
        nint dc = CreateCompatibleDC(0);
        if (dc == 0) throw new InvalidOperationException("Owned font DC creation failed.");
        nint selected = 0, old = 0;
        try
        {
            selected = font.ToHfont();
            old = SelectObject(dc, selected);
            if (old == 0 || old == -1) throw new InvalidOperationException("Owned font selection failed.");
            StringBuilder face = new(128);
            if (GetTextFaceW(dc, face.Capacity, face) == 0) throw new InvalidOperationException("Native font identity unavailable.");
            uint length = GetFontData(dc, 0, 0, null, 0);
            if (length == uint.MaxValue || length == 0 || length > 32 * 1024 * 1024)
                throw new InvalidOperationException("Invalid original font byte extent.");
            byte[] data = new byte[checked((int)length)];
            if (GetFontData(dc, 0, 0, data, length) != length) throw new InvalidOperationException("Incomplete original font bytes.");
            // Exact selected-face bytes are shared only with the paired process
            // on this disposable runner; the workflow must not upload font files.
            string fontPath = Path.Combine(fontDirectory, Hash(data) + ".ttf");
            using (var stream = new FileStream(fontPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(data);
            return new { requested = font.OriginalFontName, managed = font.Name, native = face.ToString(),
                points = font.SizeInPoints, charset = font.GdiCharSet, bytes = length, sha256 = Hash(data),
                gasp = ObserveFontTable(dc, "gasp"), outlines = GdiOutlineCapture.Capture(dc, selected, Text), bitmaps = GdiBitmapCapture.Capture(dc, selected, Text) };
        }
        finally
        {
            bool restored = old == 0 || old == -1 || SelectObject(dc, old) != 0;
            bool dcReleased = DeleteDC(dc);
            bool fontReleased = selected == 0 || DeleteObject(selected);
            if (!restored || !fontReleased || !dcReleased) throw new InvalidOperationException("Owned font native cleanup failed.");
        }
    }

    // Read-only metadata from the same selected native face. An unavailable
    // query is recorded as its raw GDI result, never interpreted as an empty table.
    private static object ObserveFontTable(nint dc, string tag)
    {
        uint nativeTag = (uint)tag[0] | (uint)tag[1] << 8 | (uint)tag[2] << 16 | (uint)tag[3] << 24;
        uint length = GetFontData(dc, nativeTag, 0, null, 0);
        if (length == uint.MaxValue)
            return new { tag, queryResult = length, available = false };
        if (length == 0 || length > 16384)
            throw new InvalidOperationException("Original font policy table exceeds its metadata bound.");
        byte[] table = new byte[checked((int)length)];
        if (GetFontData(dc, nativeTag, 0, table, length) != length)
            throw new InvalidOperationException("Original font policy table changed during observation.");
        return new { tag, queryResult = length, available = true, sha256 = Hash(table),
            hex = Convert.ToHexString(table).ToLowerInvariant() };
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int GetTextFaceW(nint dc, int count, StringBuilder face);
    [DllImport("gdi32.dll")] private static extern uint GetFontData(nint dc, uint table, uint offset, [Out] byte[]? data, uint length);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, out uint value, uint flags);
}
