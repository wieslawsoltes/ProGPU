using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

internal static partial class WordSelectionReference
{
    private const uint SetSelection = 0x00B1, GetEditRectangle = 0x00B2,
        ScrollCaret = 0x00B7, FirstVisibleLine = 0x00CE, GetFont = 0x0031, PrintClient = 0x0318;
    private const int MaximumGeometryWidth = 512, MaximumGeometryHeight = 160;
    private const long MaximumGeometryReceiptBytes = 128L * 1024 * 1024;

    private sealed record GeometryInput(Case Source, int First, int Middle, int Last, int Anchor);
    private static GeometryInput[] GeometryInputs() =>
    [
        new(Cases.Single(c => c.Name == "emoji-context"), 4, 7, 9, 4),
        // Exact pre-existing WordPolicyContexts supplementary-emoji-zwj input.
        new(new Case("supplementary-emoji-zwj", "x\U0001F469\u200D\U0001F4BBy ",
            Enumerable.Range(0, 8).ToArray()), 1, 4, 6, 1),
        new(Cases.Single(c => c.Name == "surrogate-combining"), 7, 8, 9, 7),
        new(Cases.Single(c => c.Name == "bidi-ltr"), 4, 5, 7, 4),
        new(Cases.Single(c => c.Name == "bidi-rtl"), 4, 5, 7, 4)
    ];

    internal static int RunSelectionGeometry(string path)
    {
        using FileStream file = new(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write);
        Stopwatch budget = Stopwatch.StartNew();
        var cases = new List<object>();
        var modules = new List<object>();
        object? identity = null;
        string? failure = null;
        bool shown = false;
        int samples = 0, unavailableCarets = 0;
        try
        {
            string formsPath = typeof(Control).Assembly.Location;
            if (!OperatingSystem.IsWindows() || !formsPath.Contains("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase)
                || typeof(Control).Assembly.GetName().Name != "System.Windows.Forms")
                throw new InvalidOperationException("Original Microsoft Windows EDIT is required.");
            identity = new
            {
                framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                osBuild = Environment.OSVersion.Version.ToString(), architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processId = Environment.ProcessId, locale = CultureInfo.CurrentCulture.Name,
                uiLocale = CultureInfo.CurrentUICulture.Name, forms = typeof(Control).Assembly.FullName, formsPath,
                formsSha256 = GeometryHash(File.ReadAllBytes(formsPath)),
                probeSha256 = GeometryHash(File.ReadAllBytes(typeof(WordSelectionReference).Assembly.Location)),
                nativeTextModules = modules
            };
            if (!Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)) throw new InvalidOperationException("DPI policy rejected.");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using Form form = new()
            {
                AutoScaleMode = AutoScaleMode.None, ClientSize = new Size(520, 180),
                StartPosition = FormStartPosition.Manual, Location = new Point(16, 16),
                ShowInTaskbar = false, Text = "Owned EDIT selection geometry reference"
            };
            form.Show();
            Application.DoEvents();
            shown = form.Visible;
            if (!shown) throw new InvalidOperationException("Owned reference form was not shown.");

            foreach (GeometryInput input in GeometryInputs())
            {
                CheckBudget(budget);
                Case item = input.Source;
                var observations = new List<object>();
                var classification = new Dictionary<string, object?>();
                var metadata = new Dictionary<string, object?>();
                cases.Add(new { item.Name, requestedText = item.Text, requestedUtf16 = Utf16(item.Text),
                    item.RightToLeft, item.Multiline, item.Indices, scriptBreak = classification,
                    requestedSeams = new[] { input.First, input.Middle, input.Last }, metadata, observations });
                using Probe editor = new()
                {
                    AutoSize = false, BorderStyle = BorderStyle.FixedSingle, Bounds = new Rectangle(10, 10, 480, 140),
                    Multiline = item.Multiline, WordWrap = item.Wrap, ReadOnly = item.ReadOnly,
                    RightToLeft = item.RightToLeft ? RightToLeft.Yes : RightToLeft.No,
                    Font = SystemFonts.DefaultFont, Text = item.Text
                };
                form.Controls.Add(editor);
                nint handle = editor.Handle;
                form.Activate();
                bool focusRequested = editor.Focus();
                Application.DoEvents();
                RequireGeometryOwner(editor, handle, item.Text);
                if (!focusRequested || !editor.Focused || !form.Visible)
                    throw new InvalidOperationException("Owned EDIT focus/visibility is unavailable.");
                ObserveScriptBreak(item.Text, item.RightToLeft, false, classification);
                if (modules.Count == 0) ObserveNativeTextModules(modules);
                metadata["handle"] = handle.ToInt64();
                metadata["nativeClass"] = ClassName(handle);
                metadata["deviceDpi"] = editor.DeviceDpi;
                metadata["clientSize"] = editor.ClientSize;
                metadata["baseFont"] = ReadGeometryFont(editor, handle);
                metadata["fallbackFontIdentityQualified"] = false;
                metadata["systemColors"] = new[] { 5, 8, 13, 14, 15, 18 }.Select(index =>
                    new { index, colorRef = GetSysColor(index) }).ToArray();
                int caretCount = 0;

                // Explicit directed inputs retain both anchor/active orders;
                // EM_GETSEL still reports sorted public UTF-16 endpoints.
                foreach (int position in new[] { 0, input.First, input.Middle, input.Last, item.Text.Length })
                    Select("collapsed-" + position, position, position);
                foreach ((int a, int b) in new[] { (input.First, input.Middle), (input.Middle, input.Last), (input.First, input.Last) })
                {
                    Select($"forward-{a}-{b}", a, b);
                    Select($"reverse-{b}-{a}", b, a);
                }

                // Same owned native gesture sequence as the unchanged observer.
                SendMessageW(handle, SetSelection, 0, 0);
                SendMessageW(handle, ScrollCaret, 0, 0);
                Hit anchor = ResolveHit(editor, handle, ReadPositions(handle, item.Text.Length), input.Anchor, 3)
                    ?? throw new InvalidOperationException("Required original anchor coordinate is unavailable.");
                editor.BeginObservation();
                Gesture("first-down", LeftDown, anchor);
                Gesture("first-up", LeftUp, anchor);
                Gesture("double-down", LeftDouble, anchor);
                if (editor.DoubleDownCount != 1 || GetCapture() != handle)
                    throw new InvalidOperationException("Original EDIT double-down/capture was not delivered.");
                Position[] dragPositions = ReadPositions(handle, item.Text.Length);
                Hit[] available = Enumerable.Range(0, item.Text.Length)
                    .Select(i => ResolveHit(editor, handle, dragPositions, i, 1)).OfType<Hit>().ToArray();
                if (available.Length == 0) throw new InvalidOperationException("Native drag geometry is unavailable.");
                Gesture("drag-last", MouseMove, available[^1]);
                Gesture("drag-first-reversal", MouseMove, available[0]);
                Gesture("drag-anchor-reversal", MouseMove, anchor);
                Gesture("drag-last-again", MouseMove, available[^1]);
                Gesture("final-up", LeftUp, available[^1]);
                if (GetCapture() == handle || editor.CallbackOverflow)
                    throw new InvalidOperationException("Original capture retirement/callback bound failed.");
                metadata["ownedCaretSamples"] = caretCount;
                if (caretCount == 0) throw new InvalidOperationException("No actual owned native caret was observed for this case.");

                void Select(string name, int requestedAnchor, int requestedActive)
                {
                    CheckBudget(budget);
                    RequireGeometryOwner(editor, handle, item.Text);
                    long result = SendMessageW(handle, SetSelection, requestedAnchor, requestedActive).ToInt64();
                    Selection selection = ReadSelection(editor, handle);
                    if (selection.Start != Math.Min(requestedAnchor, requestedActive) ||
                        selection.End != Math.Max(requestedAnchor, requestedActive))
                        throw new InvalidOperationException("Original EDIT changed requested UTF-16 selection endpoints.");
                    Observe(name, new { kind = "set-selection", requestedAnchor, requestedActive, result });
                }

                void Gesture(string name, uint message, Hit planned)
                {
                    CheckBudget(budget);
                    RequireGeometryOwner(editor, handle, item.Text);
                    Hit current = ResolveHit(editor, handle, ReadPositions(handle, item.Text.Length), planned.Index, planned.Quarter)
                        ?? throw new InvalidOperationException("Native gesture coordinate retired.");
                    editor.Step = name;
                    int firstCallback = editor.Callbacks.Count;
                    Selection before = ReadSelection(editor, handle);
                    long result = SendMessageW(handle, message, message == LeftUp ? 0 : 1, Pack(current.Point)).ToInt64();
                    Observe(name, new { kind = "gesture", message, hit = current, result, before,
                        callbacks = editor.Callbacks.Skip(firstCallback).ToArray() });
                }

                void Observe(string name, object request)
                {
                    CheckBudget(budget);
                    GeometryState before = ReadGeometryState(editor, handle, item.Text);
                    long scrollResult = SendMessageW(handle, ScrollCaret, 0, 0).ToInt64();
                    GeometryState after = ReadGeometryState(editor, handle, item.Text);
                    GeometryPixels pixels = ReadGeometryPixels(editor, handle, item.Text);
                    observations.Add(new { name, request, beforeScroll = before, scrollCaretResult = scrollResult,
                        afterScroll = after, pixels });
                    foreach (GeometryState state in new[] { before, after })
                    {
                        samples++;
                        if (state.Caret.Available) caretCount++; else unavailableCarets++;
                    }
                    if (!pixels.RgbIndependentOfClear)
                        throw new InvalidOperationException("WM_PRINTCLIENT did not completely paint the owned client RGB target.");
                    if (observations.Count > 19) throw new InvalidOperationException("Geometry observation count exceeded its bound.");
                }
            }
        }
        catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(failure); }
        var receipt = new
        {
            schema = "native-edit-selection-geometry-v1", completed = failure is null, error = failure,
            desktopQualified = false, physicalInputQualified = false, hostShown = shown, ownedWindowOnly = true,
            geometryComplete = failure is null && unavailableCarets == 0, caretSamples = samples,
            unavailableCaretSamples = unavailableCarets, fallbackFontIdentityQualified = false,
            dpiMode = "PerMonitorV2", themeFlag = "true", expectedCases = 5, cases, identity,
            transport = "owned shown/focused HWND messages; no physical input; explicit EM_SCROLLCARET after each selection",
            elapsedMilliseconds = budget.ElapsedMilliseconds
        };
        using var bounded = new GeometryReceiptStream(file, budget);
        JsonSerializer.Serialize(bounded, receipt, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        bounded.Flush();
        return failure is null ? 0 : 1;
    }

    private sealed record GeometryCaret(bool Available, string? UnavailableReason, bool GuiQuerySucceeded,
        int GuiError, long CaretOwner, long FocusOwner, uint Flags, GeometryRect? Rectangle,
        bool PointQuerySucceeded, int PointError, Point? Point);
    private sealed record GeometryScroll(bool Available, int Error, int Minimum, int Maximum,
        uint Page, int Position, int TrackPosition);
    private sealed record GeometryHit(int X, int Y, long Raw, int? Character, int? Line);
    private sealed record GeometryState(Selection Selection, GeometryCaret Caret, GeometryRect Client,
        GeometryRect Format, long FirstVisibleRaw, GeometryScroll HorizontalScroll,
        GeometryScroll VerticalScroll, Position[] Positions, GeometryHit[] Hits);
    private sealed record GeometryPixels(int Width, int Height, int Stride, string Format,
        byte[] Bytes, string Sha256, byte[] ClearControlBytes, string ClearControlSha256, bool RgbIndependentOfClear,
        int RgbClearDifferences, long FirstPrintResult, long SecondPrintResult);

    private static void RequireGeometryOwner(Probe editor, nint handle, string source)
    {
        RequireOwner(editor, handle);
        if (GetWindowThreadProcessId(handle, out _) != GetCurrentThreadId() || !editor.Focused ||
            !editor.Visible || editor.Text != source || editor.ClientSize.Width is <= 0 or > MaximumGeometryWidth ||
            editor.ClientSize.Height is <= 0 or > MaximumGeometryHeight)
            throw new InvalidOperationException("Owned focused EDIT/source/target identity changed.");
    }

    private static GeometryState ReadGeometryState(Probe editor, nint handle, string source)
    {
        RequireGeometryOwner(editor, handle, source);
        var gui = new GeometryGuiThreadInfo { Size = (uint)Marshal.SizeOf<GeometryGuiThreadInfo>() };
        bool guiSuccess = GetGUIThreadInfo(GetCurrentThreadId(), ref gui);
        int guiError = guiSuccess ? 0 : Marshal.GetLastWin32Error();
        bool pointSuccess = GetCaretPos(out Point point);
        int pointError = pointSuccess ? 0 : Marshal.GetLastWin32Error();
        bool caretAvailable = guiSuccess && pointSuccess && gui.Caret == handle && gui.Focus == handle &&
            gui.CaretRectangle.Right > gui.CaretRectangle.Left && gui.CaretRectangle.Bottom > gui.CaretRectangle.Top;
        var caret = new GeometryCaret(caretAvailable, caretAvailable ? null : "Owned native caret rectangle/point is unavailable.",
            guiSuccess, guiError, gui.Caret.ToInt64(), gui.Focus.ToInt64(), gui.Flags,
            caretAvailable ? gui.CaretRectangle : null, pointSuccess, pointError, caretAvailable ? point : null);
        if (!GetClientRect(handle, out GeometryRect client)) throw new Win32Exception();
        var format = new GeometryRect { Left = int.MinValue, Top = int.MinValue, Right = int.MinValue, Bottom = int.MinValue };
        SendGeometryRectangle(handle, GetEditRectangle, 0, ref format);
        if (format.Left == int.MinValue || format.Top == int.MinValue || format.Right <= format.Left || format.Bottom <= format.Top)
            throw new InvalidOperationException("Original EDIT formatting rectangle is unavailable.");
        Position[] positions = ReadPositions(handle, source.Length);
        int[] rows = positions.Where(p => p.Point is not null).Select(p => p.Point!.Value.Y)
            .Where(y => y >= client.Top && y < client.Bottom).Distinct().Order().ToArray();
        if (rows.Length is 0 or > 8) throw new InvalidOperationException("Native row scan is unavailable or exceeds its bound.");
        var hits = new List<GeometryHit>();
        foreach (int y in rows)
        for (int x = client.Left; x < client.Right; x++)
        {
            long raw = SendMessageW(handle, CharacterFromPosition, 0, Pack(new Point(x, y))).ToInt64();
            bool available = unchecked((int)raw) != -1;
            hits.Add(new(x, y, raw, available ? (int)(raw & 0xFFFF) : null,
                available ? (int)((raw >> 16) & 0xFFFF) : null));
        }
        Selection selection = ReadSelection(editor, handle);
        if (selection.Start < 0 || selection.End < selection.Start || selection.End > source.Length ||
            selection.ManagedStart != selection.Start || selection.ManagedLength != selection.End - selection.Start ||
            selection.SelectedText != source.Substring(selection.Start, selection.End - selection.Start))
            throw new InvalidOperationException("Original native/public selection identity changed.");
        RequireGeometryOwner(editor, handle, source);
        return new(selection, caret, client, format, SendMessageW(handle, FirstVisibleLine, 0, 0).ToInt64(),
            ReadGeometryScroll(handle, 0), ReadGeometryScroll(handle, 1), positions, hits.ToArray());
    }

    private static GeometryScroll ReadGeometryScroll(nint handle, int bar)
    {
        var info = new GeometryScrollInfo { Size = (uint)Marshal.SizeOf<GeometryScrollInfo>(), Mask = 0x17 };
        bool success = GetScrollInfo(handle, bar, ref info);
        return new(success, success ? 0 : Marshal.GetLastWin32Error(), info.Minimum, info.Maximum,
            info.Page, info.Position, info.TrackPosition);
    }

    private static object ReadGeometryFont(Probe editor, nint handle)
    {
        nint font = SendMessageW(handle, GetFont, 0, 0);
        if (font == 0 || GetObjectW(font, Marshal.SizeOf<GeometryLogFont>(), out GeometryLogFont descriptor) != Marshal.SizeOf<GeometryLogFont>())
            throw new InvalidOperationException("Original borrowed EDIT HFONT/LOGFONT is unavailable.");
        nint dc = GetDC(handle);
        if (dc == 0) throw new Win32Exception();
        nint previous = 0;
        Exception? primary = null;
        try
        {
            previous = SelectObject(dc, font);
            if (previous == 0 || previous == -1) throw new Win32Exception();
            uint length = GetFontData(dc, 0, 0, null, 0);
            if (length is 0 or > 32 * 1024 * 1024) throw new InvalidOperationException("Original base-font bytes are unavailable or exceed the bound.");
            byte[] bytes = new byte[checked((int)length)];
            if (GetFontData(dc, 0, 0, bytes, length) != length) throw new Win32Exception();
            if (SendMessageW(handle, GetFont, 0, 0) != font) throw new InvalidOperationException("Original EDIT font changed during capture.");
            return new { handle = font.ToInt64(), borrowed = true, descriptor, byteCount = length,
                sha256 = GeometryHash(bytes), fallbackIdentityQualified = false,
                managed = new { editor.Font.Name, editor.Font.Size, unit = editor.Font.Unit.ToString(),
                    style = editor.Font.Style.ToString() }, editor.DeviceDpi };
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            nint restoredObject = previous == 0 || previous == -1 ? 0 : SelectObject(dc, previous);
            bool restored = previous == 0 || previous == -1 || (restoredObject != 0 && restoredObject != -1);
            bool released = ReleaseDC(handle, dc) == 1;
            if (!restored || !released) GeometryCleanupFailure(primary, "Borrowed font DC restoration/release failed.");
        }
    }

    private static GeometryPixels ReadGeometryPixels(Probe editor, nint handle, string source)
    {
        RequireGeometryOwner(editor, handle, source);
        int width = editor.ClientSize.Width, height = editor.ClientSize.Height;
        int stride = checked(width * 4), byteCount = checked(stride * height);
        var info = new GeometryBitmapInfo
        {
            Header = new GeometryBitmapHeader { Size = 40, Width = width, Height = -height,
                Planes = 1, BitCount = 32, SizeImage = (uint)byteCount }
        };
        nint dc = CreateCompatibleDC(0), bitmap = 0;
        if (dc == 0) throw new Win32Exception();
        Exception? primary = null;
        try
        {
            bitmap = CreateDIBSection(dc, in info, 0, out nint pixels, 0, 0);
            if (bitmap == 0 || pixels == 0) throw new Win32Exception();
            nint previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1) throw new Win32Exception();
            byte[] first = Print(0x11, 0x53, 0x97, out long firstResult);
            byte[] second = Print(0xE1, 0xAD, 0x49, out long secondResult);
            int differences = 0;
            for (int index = 0; index < first.Length; index++)
                if ((index & 3) != 3 && first[index] != second[index]) differences++;
            RequireGeometryOwner(editor, handle, source);
            return new(width, height, stride, "BGRX8-top-down-original-bytes", first, GeometryHash(first),
                second, GeometryHash(second), differences == 0, differences, firstResult, secondResult);

            byte[] Print(byte blue, byte green, byte red, out long result)
            {
                var bytes = new byte[byteCount];
                for (int index = 0; index < bytes.Length; index += 4)
                { bytes[index] = blue; bytes[index + 1] = green; bytes[index + 2] = red; bytes[index + 3] = 0xA5; }
                Marshal.Copy(bytes, 0, pixels, bytes.Length);
                result = SendMessageW(handle, PrintClient, dc, 0x0C).ToInt64(); // PRF_CLIENT | PRF_ERASEBKGND
                if (!GdiFlush()) throw new Win32Exception();
                Marshal.Copy(pixels, bytes, 0, bytes.Length);
                return bytes;
            }
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            // This DC is ours, not borrowed. Delete it first to end the bitmap
            // selection even on failure; never delete an original stock bitmap.
            bool dcDeleted = DeleteDC(dc);
            bool bitmapDeleted = bitmap == 0 || DeleteObject(bitmap);
            if (!dcDeleted || !bitmapDeleted) GeometryCleanupFailure(primary, "Owned print DIB/DC retirement failed.");
        }
    }

    private static void GeometryCleanupFailure(Exception? primary, string message)
    {
        if (primary is null) throw new InvalidOperationException(message);
        Console.Error.WriteLine(message); // Preserve the original failure identity.
    }
    private static string GeometryHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class GeometryReceiptStream(Stream target, Stopwatch budget) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckBudget(budget);
            if (buffer.Length > MaximumGeometryReceiptBytes - _written) throw new InvalidOperationException("Geometry receipt exceeds 128 MiB.");
            target.Write(buffer);
            _written += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GeometryRect { public int Left { get; set; } public int Top { get; set; } public int Right { get; set; } public int Bottom { get; set; } }
    [StructLayout(LayoutKind.Sequential)]
    private struct GeometryGuiThreadInfo
    { public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public GeometryRect CaretRectangle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GeometryScrollInfo { public uint Size, Mask; public int Minimum, Maximum; public uint Page; public int Position, TrackPosition; }
    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct GeometryLogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct GeometryBitmapHeader
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GeometryBitmapInfo { public GeometryBitmapHeader Header; public uint Colors; }

    [DllImport("kernel32", ExactSpelling = true)] private static extern uint GetCurrentThreadId();
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GeometryGuiThreadInfo info);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCaretPos(out Point point);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint handle, out GeometryRect rectangle);
    [DllImport("user32", EntryPoint = "SendMessageW")] private static extern nint SendGeometryRectangle(nint handle, uint message, nint wParam, ref GeometryRect rectangle);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetScrollInfo(nint handle, int bar, ref GeometryScrollInfo info);
    [DllImport("user32")] private static extern uint GetSysColor(int index);
    [DllImport("user32", SetLastError = true)] private static extern nint GetDC(nint handle);
    [DllImport("user32")] private static extern int ReleaseDC(nint handle, nint dc);
    [DllImport("gdi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetObjectW(nint value, int size, out GeometryLogFont result);
    [DllImport("gdi32", SetLastError = true)] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32", SetLastError = true)] private static extern uint GetFontData(nint dc, uint table, uint offset, [Out] byte[]? data, uint length);
    [DllImport("gdi32", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, in GeometryBitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
}
