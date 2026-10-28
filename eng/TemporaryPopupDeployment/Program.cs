using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProGPU.Backend;

internal static partial class Program
{
    private static int Main(string[] args)
    {
        nint owner = 0, popup = 0, originalContext = 0, afterContext = 0;
        nint originalForeground = 0;
        var receipt = new Receipt();
        var clock = Stopwatch.StartNew();
        int exit = 1;
        try
        {
            Require(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
                "Actual native Windows x64 required.");
            Require(args.Length == 1 && args[0] is "regular" or "single-file" or "native-aot", "Explicit deployment mode required.");
            receipt.Mode = args[0];
#pragma warning disable IL3000 // Empty location is the specific deployment branch being validated.
            receipt.BackendLocation = typeof(NativePopupWindow).Assembly.Location;
#pragma warning restore IL3000
            Require((receipt.BackendLocation.Length == 0) == (args[0] != "regular"), "The required product deployment branch was not selected.");
            receipt.DynamicCodeSupported = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
            Require(receipt.DynamicCodeSupported == (args[0] != "native-aot"), "Runtime mode does not match the required deployment.");
            using (var manifest = typeof(NativePopupWindow).Assembly.GetManifestResourceStream("ProGPU.Backend.Win32PopupCommonControls.manifest"))
            {
                Require(manifest is not null, "Original product manifest resource missing.");
                receipt.ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifest!));
                receipt.ExpectedManifestSha256 = Environment.GetEnvironmentVariable("PROBE_MANIFEST_SHA256") ?? "";
                Require(receipt.ExpectedManifestSha256.Length == 64 &&
                    receipt.ManifestSha256 == receipt.ExpectedManifestSha256, "Embedded manifest differs from the validated original checkout bytes.");
            }
            string temporary = Path.GetFullPath(Path.GetTempPath());
            Require(temporary.TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetFullPath(Environment.GetEnvironmentVariable("PROBE_OWNED_TEMP") ?? "").TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase), "Private per-process temporary directory required.");
            Require(Directory.GetFiles(temporary, "progpu-popup-*.manifest").Length == 0, "Temporary manifest already present.");
            originalForeground = GetForegroundWindow();
            receipt.ForegroundBefore = originalForeground;
            owner = CreateWindowExW(0, "STATIC", "ProGPU deployment owner", 0, 0, 0, 32, 32, 0, 0, 0, 0);
            Require(owner != 0, "Original hidden owner creation failed.");
            popup = CreateWindowExW(0x00040000, "STATIC", "ProGPU deployment popup", 0x00cf0000,
                0, 0, 16, 16, 0, 0, 0, 0);
            Require(popup != 0, "Original hidden popup creation failed.");
            Require(IsWindowVisible(owner) == 0 && IsWindowVisible(popup) == 0, "Original fixture became visible.");
            receipt.StyleBefore = unchecked((uint)GetWindowLongPtrW(popup, -16));
            receipt.ExtendedBefore = unchecked((uint)GetWindowLongPtrW(popup, -20));
            Require(GetCurrentActCtx(out originalContext) != 0, "Original calling activation context is unavailable.");
            receipt.Accepted = NativePopupWindow.TryConfigureOwner(
                new NativeWindowHandle(NativeWindowKind.Win32, owner, 0, "HWND"),
                new NativeWindowHandle(NativeWindowKind.Win32, popup, 0, "HWND"));
            Require(GetCurrentActCtx(out afterContext) != 0, "Post-configuration activation context is unavailable.");
            receipt.ActivationContextPreserved = originalContext == afterContext;
            Require(receipt.ActivationContextPreserved, "Product changed the calling activation context.");
            Require(receipt.Accepted, "Public original popup configuration rejected this deployment.");
            Require(GetWindow(popup, 4) == owner, "Original native owner was not retained.");
            receipt.StyleAfter = unchecked((uint)GetWindowLongPtrW(popup, -16));
            receipt.ExtendedAfter = unchecked((uint)GetWindowLongPtrW(popup, -20));
            Require(receipt.StyleAfter == ((receipt.StyleBefore | 0x80000000u) & ~0x00cf0000u), "Full original popup style changed.");
            Require(receipt.ExtendedAfter == ((receipt.ExtendedBefore | 0x08000080u) & ~0x00040100u), "Full original borderless extended style changed.");
            Require(IsWindowVisible(owner) == 0 && IsWindowVisible(popup) == 0, "Configured fixture became visible.");
            // Original hidden-fixture query only. No mouse/key event, pointer motion or activation.
            receipt.MouseActivateResponse = SendMessageW(popup, 0x0021, (nuint)owner, 0x02010001);
            Require(receipt.MouseActivateResponse == 3, "Original MA_NOACTIVATE response changed.");
            Require(GetForegroundWindow() == originalForeground, "Foreground changed during the hidden native check.");
            receipt.ManifestFilesAfter = Directory.GetFiles(temporary, "progpu-popup-*.manifest").Length;
            Require(receipt.ManifestFilesAfter == 0, "Owned temporary manifest did not retire.");
            receipt.Passed = true;
            exit = 0;
        }
        catch (Exception error) { receipt.Error = error.ToString(); }
        finally
        {
            if (afterContext != 0) ReleaseActCtx(afterContext);
            if (originalContext != 0) ReleaseActCtx(originalContext);
            if (popup != 0)
            {
                receipt.PopupDestroyed = DestroyWindow(popup) != 0 && IsWindow(popup) == 0;
                if (!receipt.PopupDestroyed) { receipt.Passed = false; exit = 1; }
            }
            if (owner != 0)
            {
                receipt.OwnerDestroyed = DestroyWindow(owner) != 0 && IsWindow(owner) == 0;
                if (!receipt.OwnerDestroyed) { receipt.Passed = false; exit = 1; }
            }
            if (OperatingSystem.IsWindows())
            {
                receipt.ForegroundAfter = GetForegroundWindow();
                if (owner != 0 && receipt.ForegroundAfter != originalForeground) { receipt.Passed = false; exit = 1; }
            }
            receipt.ElapsedSeconds = clock.Elapsed.TotalSeconds;
            Console.WriteLine(JsonSerializer.Serialize(receipt, ProbeJsonContext.Default.Receipt));
        }
        return exit;
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint extended, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(nint window);
    [LibraryImport("user32.dll")] private static partial int IsWindow(nint window);
    [LibraryImport("user32.dll")] private static partial int IsWindowVisible(nint window);
    [LibraryImport("user32.dll")] private static partial nint GetWindow(nint window, uint command);
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial nint GetWindowLongPtrW(nint window, int index);
    [LibraryImport("user32.dll")] private static partial nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("kernel32.dll")] private static partial int GetCurrentActCtx(out nint context);
    [LibraryImport("kernel32.dll")] private static partial void ReleaseActCtx(nint context);
}

internal sealed class Receipt
{
    public string Mode { get; set; } = "";
    public string BackendLocation { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string ExpectedManifestSha256 { get; set; } = "";
    public bool DynamicCodeSupported { get; set; }
    public bool Accepted { get; set; }
    public bool ActivationContextPreserved { get; set; }
    public bool PopupDestroyed { get; set; }
    public bool OwnerDestroyed { get; set; }
    public bool Passed { get; set; }
    public int ManifestFilesAfter { get; set; } = -1;
    public long ForegroundBefore { get; set; }
    public long ForegroundAfter { get; set; }
    public long MouseActivateResponse { get; set; }
    public uint StyleBefore { get; set; }
    public uint ExtendedBefore { get; set; }
    public uint StyleAfter { get; set; }
    public uint ExtendedAfter { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? Error { get; set; }
}

[JsonSerializable(typeof(Receipt))]
internal partial class ProbeJsonContext : JsonSerializerContext { }
