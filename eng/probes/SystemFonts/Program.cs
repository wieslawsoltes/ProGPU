using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

if (!OperatingSystem.IsWindows() || args.Length != 2)
    throw new ArgumentException("Expected Windows, a DPI awareness mode, and an output JSON path.");

nint requested = args[0] switch
{
    "unaware" => -1,
    "system" => -2,
    "per-monitor-v2" => -4,
    _ => throw new ArgumentOutOfRangeException(nameof(args))
};
nint previous = SetThreadDpiAwarenessContext(requested);
if (previous == 0) throw new Win32Exception();
try
{
    if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), requested))
        throw new InvalidOperationException("The requested DPI awareness context is not active.");
    string[] roles = ["DefaultFont", "DialogFont", "MenuFont", "MessageBoxFont",
        "StatusFont", "CaptionFont", "IconTitleFont", "SmallCaptionFont"];
    var fonts = new List<object>();
    foreach (string role in roles)
    {
        using Font first = SystemFonts.GetFontByName(role) ?? throw new InvalidOperationException(role);
        using Font second = SystemFonts.GetFontByName(role) ?? throw new InvalidOperationException(role);
        if (ReferenceEquals(first, second) || !first.Equals(second))
            throw new InvalidOperationException($"{role}: system font ownership/value mismatch.");
        first.Dispose();
        using Font clone = (Font)second.Clone();
        if (!clone.Equals(second) || clone.IsSystemFont)
            throw new InvalidOperationException($"{role}: clone value/identity mismatch.");
        fonts.Add(new
        {
            Role = role, second.Name, second.Size, second.SizeInPoints,
            Style = (int)second.Style, Unit = (int)second.Unit, second.GdiCharSet,
            second.GdiVerticalFont, second.SystemFontName, second.IsSystemFont
        });
    }
    nint dc = GetDC(0);
    if (dc == 0) throw new Win32Exception();
    int screenDpi;
    try { screenDpi = GetDeviceCaps(dc, 90); }
    finally { if (ReleaseDC(0, dc) != 1) throw new Win32Exception(); }
    if (screenDpi <= 0) throw new InvalidOperationException("Invalid screen DPI.");
    var metrics = new List<object>();
    foreach (var item in new[] {
        (GraphicsUnit.Point, 9f), (GraphicsUnit.Pixel, 12f), (GraphicsUnit.World, 12f),
        (GraphicsUnit.Inch, .125f), (GraphicsUnit.Document, 37.5f), (GraphicsUnit.Millimeter, 3.175f) })
    {
        using Font font = new("Segoe UI", item.Item2, FontStyle.Regular, item.Item1);
        if (font.Name != "Segoe UI") throw new InvalidOperationException("The metric reference font is unavailable.");
        metrics.Add(new { font.Name, font.Size, Unit = item.Item1.ToString(), font.SizeInPoints,
            font.Height, ImplicitHeight = font.GetHeight(), Explicit96 = font.GetHeight(96),
            Explicit192 = font.GetHeight(192), EmHeight = font.FontFamily.GetEmHeight(font.Style),
            LineSpacing = font.FontFamily.GetLineSpacing(font.Style) });
    }
    var assembly = typeof(SystemFonts).Assembly;
    using var assemblyFile = File.OpenRead(assembly.Location);
    File.WriteAllText(args[1], JsonSerializer.Serialize(new
    {
        Mode = args[0], Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Assembly = assembly.FullName, AssemblyPath = assembly.Location,
        AssemblySha256 = Convert.ToHexString(SHA256.HashData(assemblyFile)), Fonts = fonts,
        ScreenDpi = screenDpi, Metrics = metrics
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally { SetThreadDpiAwarenessContext(previous); }

[DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
static extern nint SetThreadDpiAwarenessContext(nint context);

[DllImport("user32.dll", ExactSpelling = true)]
static extern nint GetThreadDpiAwarenessContext();

[DllImport("user32.dll", ExactSpelling = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

[DllImport("user32.dll", ExactSpelling = true)]
static extern nint GetDC(nint window);

[DllImport("user32.dll", ExactSpelling = true)]
static extern int ReleaseDC(nint window, nint dc);

[DllImport("gdi32.dll", ExactSpelling = true)]
static extern int GetDeviceCaps(nint dc, int index);
