// Original ProGPU-owned reference algorithm/24 fixtures from LibreWinForms
// def9a31a9192bd560da34c49787089fec848b4ab, eng/NativeTextBoxReference/WordSelectionReference.cs.
// This attributed copy adds independent raw script/character metadata and context inputs only.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

// This is an observation of the original EDIT implementation, not an alternate
// word breaker. Coordinates come from EM_POSFROMCHAR; no text width is guessed.
internal static partial class WordSelectionReference
{
    private const uint MouseMove = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202, LeftDouble = 0x0203;
    private const uint GetSelection = 0x00B0, PositionFromCharacter = 0x00D6, CharacterFromPosition = 0x00D7;
    private const uint GetWordBreakProcedure = 0x00D1, ScriptBreak = 0x40, ScriptRightToLeft = 0x100;
    private const int MaximumCallbacks = 128;

    private sealed record Case(string Name, string Text, int[] Indices,
        bool Multiline = false, bool Password = false, bool ReadOnly = false, bool RightToLeft = false, bool Wrap = false,
        bool RequireWordBreaking = false);

    private static readonly Case[] Cases =
    [
        new("spaces", "  alpha  beta  ", [0, 2, 4, 6, 7, 9, 12, 13]),
        new("punctuation", "alpha,beta.gamma! (tail) - end ", [0, 4, 5, 6, 10, 15, 17, 21, 23, 25, 27]),
        new("tabs-single", "one\t\ttwo \tthree ", [2, 3, 4, 5, 7, 9, 10, 14]),
        new("tabs-multiline", "one\t\ttwo \tthree ", [2, 3, 4, 5, 7, 9, 10, 14], Multiline: true),
        new("crlf-single", "alpha\r\nbeta\r\n\r\ngamma ", [0, 4, 5, 7, 10, 13, 15, 19]),
        new("crlf-multiline", "alpha\r\nbeta\r\n\r\ngamma ", [0, 4, 5, 7, 10, 13, 15, 19], Multiline: true),
        new("surrogate-combining", "go A\U0001F600 e\u0301 fin ", [0, 3, 4, 5, 7, 8, 10, 12]),
        new("bidi-ltr", "abc \u05D0\u05D1\u05D2, \u0639\u0631\u0628\u0649 end ", [0, 2, 4, 6, 7, 9, 12, 14]),
        new("bidi-rtl", "abc \u05D0\u05D1\u05D2, \u0639\u0631\u0628\u0649 end ", [0, 2, 4, 6, 7, 9, 12, 14], RightToLeft: true),
        new("password", "alpha, beta\tend ", [0, 4, 5, 6, 7, 10, 11, 13], Password: true),
        new("read-only", "  alpha,beta \ttail ", [0, 2, 6, 7, 8, 11, 12, 14, 16], ReadOnly: true),
        new("wrapped-multiline", "alpha beta gamma delta end ", [0, 4, 6, 9, 11, 15, 17, 21], Multiline: true, Wrap: true),
        new("unicode-spaces", "a\u00A0b\u2003c\u202Fd\u3000e ", [0, 1, 2, 3, 4, 5, 6, 7, 8]),
        new("symbols", "a\u00A9b\u2603c\U0001F600d ", [0, 1, 2, 3, 4, 5, 6, 7]),
        new("isolated-breaks-single", "ab\rcd\nef ", [1, 2, 3, 4, 5, 6, 7]),
        new("isolated-breaks-multiline", "ab\rcd\nef ", [1, 2, 3, 4, 5, 6, 7], Multiline: true),
        new("crcrlf-multiline", "ab\r\r\ncd ", [1, 2, 3, 4, 5, 6], Multiline: true),
        new("wrapped-longword", "abcdefghijklmnopqrstuvwxyz0123456789 ", [0, 4, 8, 12, 16, 20, 24, 28, 32, 35], Multiline: true, Wrap: true),
        new("supplementary-scripts", "a\U00010400b\U0001D11Ec\U00020000d ", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]),
        new("emoji-context", "a\u2603\uFE0Fb\U0001F469\u200D\U0001F4BBc ", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]),
        // Authored complete UTF-16 inputs, not guessed native word endpoints.
        // Observe the same Thai phrase with and without an explicit space;
        // Lao and Khmer include their own combining/cluster contexts.
        new("thai-adjacent", "\u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22\u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22 ",
            [0, 2, 4, 6, 7, 9, 11, 13], RequireWordBreaking: true),
        new("thai-spaced", "\u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22 \u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22 ",
            [0, 2, 4, 6, 7, 8, 12, 14], RequireWordBreaking: true),
        new("lao-adjacent", "\u0EAA\u0EB0\u0E9A\u0EB2\u0E8D\u0E94\u0EB5\u0EAA\u0EB0\u0E9A\u0EB2\u0E8D\u0E94\u0EB5 ",
            [0, 2, 4, 6, 7, 9, 11, 13], RequireWordBreaking: true),
        new("khmer-adjacent", "\u1797\u17B6\u179F\u17B6\u1781\u17D2\u1798\u17C2\u179A\u1797\u17B6\u179F\u17B6\u1781\u17D2\u1798\u17C2\u179A ",
            [0, 2, 4, 6, 8, 9, 13, 17], RequireWordBreaking: true)
    ];

    internal static int Run(string path, string dpiMode, string themeFlag)
        => RunCases(path, dpiMode, themeFlag, Cases);

    private static int RunCases(string path, string dpiMode, string themeFlag, Case[] cases)
    {
        string output = Path.GetFullPath(path);
        // CreateNew preserves prior evidence, including failed attempts.
        using FileStream file = new(output, FileMode.CreateNew, FileAccess.Write);
        Stopwatch budget = Stopwatch.StartNew();
        var results = new List<object>();
        var nativeTextModules = new List<object>();
        object? identity = null;
        string? failure = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Original Windows EDIT is required.");
            string formsPath = typeof(Control).Assembly.Location;
            if (!formsPath.Contains("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase)
                || typeof(Control).Assembly.GetName().Name != "System.Windows.Forms")
                throw new InvalidOperationException("Not the original Microsoft WindowsDesktop WinForms reference.");
            identity = new
            {
                framework = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processId = Environment.ProcessId,
                locale = CultureInfo.CurrentCulture.Name,
                uiLocale = CultureInfo.CurrentUICulture.Name,
                osBuild = Environment.OSVersion.Version.ToString(),
                forms = typeof(Control).Assembly.FullName,
                formsPath,
                formsSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(formsPath))).ToLowerInvariant(),
                probeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(WordSelectionReference).Assembly.Location))).ToLowerInvariant(),
                nativeTextModules
            };
            HighDpiMode mode = Enum.Parse<HighDpiMode>(dpiMode);
            bool themed = bool.Parse(themeFlag);
            if (!Application.SetHighDpiMode(mode)) throw new InvalidOperationException("DPI policy rejected.");
            if (themed) Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using Form form = new()
            {
                AutoScaleMode = AutoScaleMode.None,
                ClientSize = new Size(520, 180),
                ShowInTaskbar = false
            };
            _ = form.Handle;
            foreach (Case item in cases)
            {
                var gestures = new List<object>();
                var scriptBreak = new Dictionary<string, object?>();
                int completedGestures = 0;
                // Publish the case before work so failures retain completed gestures.
                results.Add(new { item.Name, requestedText = item.Text, requestedUtf16 = Utf16(item.Text),
                    item.Multiline, item.Password, item.ReadOnly, item.RightToLeft, item.Wrap, item.RequireWordBreaking,
                    item.Indices, scriptBreak, gestures });
                foreach (int index in item.Indices)
                foreach (int quarter in new[] { 1, 3 })
                {
                    CheckBudget(budget);
                    using Probe editor = new()
                    {
                        AutoSize = false,
                        BorderStyle = BorderStyle.FixedSingle,
                        Bounds = new Rectangle(10, 10, item.Wrap ? 96 : 480, 140),
                        Multiline = item.Multiline,
                        WordWrap = item.Wrap,
                        ReadOnly = item.ReadOnly,
                        PasswordChar = item.Password ? '*' : '\0',
                        RightToLeft = item.RightToLeft ? RightToLeft.Yes : RightToLeft.No,
                        Font = SystemFonts.DefaultFont,
                        Text = item.Text
                    };
                    form.Controls.Add(editor);
                    nint handle = editor.Handle;
                    RequireOwner(editor, handle);
                    string initialText = editor.Text;
                    if (scriptBreak.Count == 0)
                    {
                        ObserveScriptBreak(initialText, item.RightToLeft, item.RequireWordBreaking, scriptBreak);
                        if (nativeTextModules.Count == 0) ObserveNativeTextModules(nativeTextModules);
                    }
                    else if (!Equals(scriptBreak["actualText"], initialText))
                        throw new InvalidOperationException("EDIT text changed between independent case observations.");
                    // Borrowed diagnostic address only: do not invoke an unknown callback.
                    nint wordBreakProcedure = SendMessageW(handle, GetWordBreakProcedure, 0, 0);
                    RequireOwner(editor, handle);
                    var positions = ReadPositions(handle, initialText.Length);
                    if (item.Name == "wrapped-longword"
                        && positions.Where(p => p.Index < initialText.Length && p.Point.HasValue)
                            .Select(p => p.Point!.Value.Y).Distinct().Count() < 2)
                        throw new InvalidOperationException("The native long word did not span multiple observed rows.");
                    Hit? anchor = ResolveHit(editor, handle, positions, index, quarter);
                    var steps = new List<object>();
                    gestures.Add(new
                    {
                        requestedIndex = index, quarter, actualText = initialText, actualUtf16 = Utf16(initialText),
                        editor.DeviceDpi, editor.ClientSize, editor.Multiline, editor.ReadOnly,
                        passwordCharacter = (int)editor.PasswordChar, rightToLeft = editor.RightToLeft.ToString(),
                        font = new { editor.Font.Name, editor.Font.Size, unit = editor.Font.Unit.ToString(), style = editor.Font.Style.ToString() },
                        wordBreakProcedure = new { message = GetWordBreakProcedure, address = wordBreakProcedure.ToInt64(),
                            available = wordBreakProcedure != 0, invoked = false },
                        handle = handle.ToInt64(), nativeClass = ClassName(handle), positions, anchor,
                        coordinateUnavailable = anchor is null,
                        unavailableReason = anchor is null ? "No distinct same-row in-client native coordinate span for this UTF-16 request." : null,
                        steps
                    });
                    // Delimiters, coincident UTF-16 cluster positions and clipped
                    // characters are retained as unavailable, never invented boxes.
                    if (anchor is null) continue;

                    editor.BeginObservation();
                    Send("first-down", LeftDown, anchor);
                    Send("first-up", LeftUp, anchor);
                    Send("double-down", LeftDouble, anchor);
                    Selection doubled = ReadSelection(editor, handle);
                    if (editor.DoubleDownCount != 1) throw new InvalidOperationException("The actual double-down callback was not delivered exactly once.");
                    if (!doubled.CaptureOwned) throw new InvalidOperationException("EDIT did not own capture for word drag.");
                    if (item.Name == "spaces" && index == 4 && doubled.Start == doubled.End)
                        throw new InvalidOperationException("The interior ASCII word control did not select any text.");
                    Position[] dragPositions = ReadPositions(handle, initialText.Length);
                    Hit[] available = Enumerable.Range(0, initialText.Length)
                        .Select(i => ResolveHit(editor, handle, dragPositions, i, 1))
                        .OfType<Hit>().ToArray();
                    if (available.Length == 0) throw new InvalidOperationException("No native drag positions are available.");
                    // Logical first/last are labels, not assumptions about bidi X.
                    // Coordinates and current native hit indices accompany every step.
                    Send("drag-last", MouseMove, available[^1]);
                    Send("drag-first-reversal", MouseMove, available[0]);
                    Send("drag-anchor-reversal", MouseMove, anchor);
                    Send("drag-last-again", MouseMove, available[^1]);
                    Send("final-up", LeftUp, available[^1]);
                    if (GetCapture() == handle) throw new InvalidOperationException("EDIT retained capture after release.");
                    completedGestures++;

                    void Send(string name, uint message, Hit planned)
                    {
                        CheckBudget(budget);
                        RequireOwner(editor, handle);
                        Hit current = ResolveHit(editor, handle, ReadPositions(handle, initialText.Length), planned.Index, planned.Quarter)
                            ?? throw new InvalidOperationException($"Native position retired before {name}.");
                        editor.Step = name;
                        int callbackStart = editor.Callbacks.Count;
                        Selection before = ReadSelection(editor, handle);
                        nint result = SendMessageW(handle, message, message == LeftUp ? 0 : 1, Pack(current.Point));
                        RequireOwner(editor, handle);
                        Selection after = ReadSelection(editor, handle);
                        steps.Add(new { name, message, wParam = message == LeftUp ? 0 : 1, hit = current,
                            result = result.ToInt64(), before, after, callbacks = editor.Callbacks.Skip(callbackStart).ToArray() });
                        if (editor.CallbackOverflow) throw new InvalidOperationException("Callback receipt budget exceeded.");
                        if (editor.Text != initialText) throw new InvalidOperationException("Pointer-only input changed source text.");
                        if (after.Start < 0 || after.End < after.Start || after.End > initialText.Length
                            || after.ManagedStart != after.Start || after.ManagedLength != after.End - after.Start
                            || after.SelectedText != initialText.Substring(after.Start, after.End - after.Start))
                            throw new InvalidOperationException("Native/public UTF-16 selection views disagree.");
                        if (message != LeftUp && !after.CaptureOwned)
                            throw new InvalidOperationException("Native selection input lost owned capture.");
                    }
                }
                if (gestures.Count != item.Indices.Length * 2)
                    throw new InvalidOperationException("Incomplete requested coordinate inventory.");
                if (completedGestures == 0)
                    throw new InvalidOperationException($"No native gesture could be observed for {item.Name}.");
            }
        }
        catch (Exception error)
        {
            failure = error.ToString();
            Console.Error.WriteLine(failure);
        }
        var receipt = new
        {
            schema = "native-edit-word-selection-v1",
            completed = failure is null,
            desktopQualified = false,
            physicalInputQualified = false,
            hostShown = false,
            transport = "synchronous owned-HWND native messages; no physical input or timing classification",
            dpiMode, themeFlag, identity, expectedCases = cases.Length, cases = results,
            elapsedMilliseconds = budget.ElapsedMilliseconds, error = failure
        };
        JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        file.Flush();
        return failure is null ? 0 : 1;
    }

    private static void CheckBudget(Stopwatch budget)
    {
        if (budget.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Native EDIT observation exceeded 30 seconds.");
    }

    private static int[] Utf16(string value) => value.Select(c => (int)c).ToArray();

    private static void ObserveNativeTextModules(List<object> modules)
    {
        // Capture the actually loaded DLL paths after calling Uniscribe, not
        // an assumed SDK or System32 file. Newer systems can forward its APIs.
        using Process process = Process.GetCurrentProcess();
        bool foundUniscribe = false;
        foreach (ProcessModule module in process.Modules)
        {
            string path = module.FileName;
            string name = Path.GetFileName(path);
            bool uniscribe = name.Equals("usp10.dll", StringComparison.OrdinalIgnoreCase);
            if (!uniscribe && !name.Equals("gdi32.dll", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("gdi32full.dll", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("textshaping.dll", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("kernelbase.dll", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("user32.dll", StringComparison.OrdinalIgnoreCase)) continue;
            modules.Add(new
            {
                name, path, fileVersion = module.FileVersionInfo.FileVersion,
                fileSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            });
            foundUniscribe |= uniscribe;
        }
        if (!foundUniscribe) throw new InvalidOperationException("The loaded Uniscribe module was not observed.");
    }

    private static void ObserveScriptBreak(string text, bool rightToLeft, bool requireWordBreaking,
        Dictionary<string, object?> result)
    {
        // Independent Uniscribe evidence, NOT a claim that EDIT uses these flags.
        // https://learn.microsoft.com/windows/win32/api/usp10/nf-usp10-scriptstringanalyse
        // https://learn.microsoft.com/windows/win32/api/usp10/ns-usp10-script_logattr
        // No clipping/hotkey transformation: each original UTF-16 unit has one
        // SCRIPT_LOGATTR byte. Never marshal native BYTE bitfields as C# bools.
        uint flags = ScriptBreak | (rightToLeft ? ScriptRightToLeft : 0);
        int glyphCapacity = checked((text.Length * 3 + 1) / 2 + 16);
        result["api"] = "ScriptStringAnalyse/ScriptString_pLogAttr/ScriptStringFree";
        result["independentOfEdit"] = true;
        result["actualText"] = text;
        result["actualUtf16"] = Utf16(text);
        result["flags"] = flags;
        result["hdc"] = 0;
        result["charset"] = -1;
        result["glyphCapacity"] = glyphCapacity;
        result["attributeByteSize"] = 1;
        result["completed"] = false;
        nint analysis = 0;
        bool ownsAnalysis = false;
        ExceptionDispatchInfo? cleanupFailure = null;
        try
        {
            if (text.Length == 0) throw new InvalidOperationException("Break analysis requires nonempty UTF-16 input.");
            var classification = new Dictionary<string, object?>();
            result["classification"] = classification;
            bool hasWordBreakingRun = ObserveScriptClassification(text, rightToLeft, classification);
            int analyseResult = ScriptStringAnalyse(0, text, text.Length, glyphCapacity, -1, flags, 0,
                0, 0, 0, 0, 0, out analysis);
            result["analyseHResult"] = analyseResult;
            result["analyseHResultHex"] = $"0x{unchecked((uint)analyseResult):X8}";
            result["analysisAllocated"] = analysis != 0;
            if (analyseResult != 0 || analysis == 0)
                throw new InvalidOperationException($"ScriptStringAnalyse failed: 0x{unchecked((uint)analyseResult):X8}.");
            ownsAnalysis = true;
            nint attributes = ScriptString_pLogAttr(analysis);
            result["attributesAvailable"] = attributes != 0;
            if (attributes == 0) throw new InvalidOperationException("ScriptString_pLogAttr returned null.");
            byte[] bytes = new byte[text.Length];
            Marshal.Copy(attributes, bytes, 0, bytes.Length);
            // The copy, including reserved bits, survives ScriptStringFree.
            result["rawBytes"] = bytes.Select(b => (int)b).ToArray();
            result["attributes"] = bytes.Select((value, index) => new
            {
                index, utf16 = (int)text[index], raw = (int)value,
                softBreak = (value & 1) != 0, whiteSpace = (value & 2) != 0,
                charStop = (value & 4) != 0, wordStop = (value & 8) != 0,
                invalid = (value & 16) != 0, reserved = value >> 5
            }).ToArray();
            // Require the actually loaded engine's copied property, not a
            // script-ID guess. Keep all observed raw attributes on rejection.
            if (requireWordBreaking && !hasWordBreakingRun)
                throw new InvalidOperationException("The discriminant did not observe an actual fNeedsWordBreaking run.");
        }
        catch (Exception error)
        {
            result["error"] = error.ToString();
            throw;
        }
        finally
        {
            // A failed HRESULT does not transfer an analysis object to us.
            if (ownsAnalysis)
            {
                try
                {
                    int freeResult = ScriptStringFree(ref analysis);
                    result["freeHResult"] = freeResult;
                    result["freeHResultHex"] = $"0x{unchecked((uint)freeResult):X8}";
                    if (freeResult != 0)
                    {
                        var error = new InvalidOperationException($"ScriptStringFree failed: 0x{unchecked((uint)freeResult):X8}.");
                        result["freeError"] = error.ToString();
                        cleanupFailure = ExceptionDispatchInfo.Capture(error);
                    }
                }
                catch (Exception error)
                {
                    result["freeError"] = error.ToString();
                    cleanupFailure = ExceptionDispatchInfo.Capture(error);
                }
            }
        }
        // A primary analysis error already propagates after finally. Publish
        // cleanup failure only here so it cannot replace that original error.
        cleanupFailure?.Throw();
        result["completed"] = true;
    }

    private static bool ObserveScriptClassification(string text, bool rightToLeft, Dictionary<string, object?> result)
    {
        // Independent diagnostic only: eScript identifies a version-dependent
        // native engine, not a portable Unicode script number or EDIT policy.
        // https://learn.microsoft.com/windows/win32/api/usp10/nf-usp10-scriptitemize
        // https://learn.microsoft.com/windows/win32/api/usp10/ns-usp10-script_properties
        result["completed"] = false;
        result["api"] = "ScriptItemize/ScriptGetProperties/GetStringTypeW";
        result["independentOfEdit"] = true;
        result["itemByteSize"] = Marshal.SizeOf<ScriptItem>();
        if (Marshal.SizeOf<ScriptItem>() != 8)
            throw new InvalidOperationException("Unexpected SCRIPT_ITEM ABI.");
        // Pass both control and state for full paragraph bidi analysis, keeping
        // original text and direction. Reserve the documented extra sentinel.
        uint control = 0;
        ushort state = rightToLeft ? (ushort)1 : (ushort)0;
        int capacity = checked(text.Length + 1);
        ScriptItem[] items = new ScriptItem[checked(capacity + 1)];
        result["control"] = control;
        result["initialState"] = state;
        result["itemCapacity"] = capacity;
        int itemizeResult = ScriptItemize(text, text.Length, capacity, in control, in state, items, out int count);
        result["itemizeHResult"] = itemizeResult;
        result["itemCount"] = count;
        if (itemizeResult != 0)
            throw new InvalidOperationException($"ScriptItemize failed: 0x{unchecked((uint)itemizeResult):X8}.");
        if (count <= 0 || count > text.Length || items[0].Start != 0 || items[count].Start != text.Length)
            throw new InvalidOperationException("ScriptItemize returned an invalid source partition.");
        result["terminalPosition"] = items[count].Start;
        int propertiesResult = ScriptGetProperties(out nint propertyTable, out int propertyCount);
        result["propertiesHResult"] = propertiesResult;
        result["propertyCount"] = propertyCount;
        if (propertiesResult != 0 || propertyTable == 0 || propertyCount <= 0)
            throw new InvalidOperationException($"ScriptGetProperties failed: 0x{unchecked((uint)propertiesResult):X8}.");
        var runs = new List<object>();
        result["runs"] = runs;
        bool hasWordBreakingRun = false;
        for (int i = 0; i < count; i++)
        {
            ScriptItem item = items[i];
            int end = items[i + 1].Start;
            int script = item.Analysis & 0x3FF;
            if (item.Start < 0 || end <= item.Start || end > text.Length || script >= propertyCount)
                throw new InvalidOperationException("ScriptItemize returned an invalid item or property index.");
            nint properties = Marshal.ReadIntPtr(propertyTable, checked(script * IntPtr.Size));
            if (properties == 0) throw new InvalidOperationException("Missing SCRIPT_PROPERTIES entry.");
            // Borrowed process-owned static properties: copy their two DWORD
            // bitfields immediately; never free or retain the table pointers.
            uint first = unchecked((uint)Marshal.ReadInt32(properties));
            uint second = unchecked((uint)Marshal.ReadInt32(properties, 4));
            hasWordBreakingRun |= (first & (1U << 18)) != 0;
            // Separate direct item API observation, never substituted for EDIT
            // or asserted equal to the complete-source SSA_BREAK front end.
            ScriptAnalysis directAnalysis = new() { Analysis = item.Analysis, State = item.State };
            if (Marshal.SizeOf<ScriptAnalysis>() != 4) throw new InvalidOperationException("Unexpected SCRIPT_ANALYSIS ABI.");
            byte[] direct = new byte[end - item.Start];
            int directResult = ObserveDirectScriptBreak(text.Substring(item.Start, end - item.Start),
                direct.Length, in directAnalysis, direct);
            if (directResult != 0) throw new InvalidOperationException($"ScriptBreak failed: 0x{unchecked((uint)directResult):X8}.");
            runs.Add(new
            {
                start = item.Start, end, script, rawAnalysis = item.Analysis, rawState = item.State,
                directScriptBreak = new { hResult = directResult, inputStart = item.Start,
                    inputUtf16 = Utf16(text.Substring(item.Start, end - item.Start)),
                    rawBytes = direct.Select(value => (int)value).ToArray() },
                rawPropertiesFirst = first, rawPropertiesSecond = second,
                languageId = first & 0xFFFF, numeric = (first & (1U << 16)) != 0,
                complex = (first & (1U << 17)) != 0, needsWordBreaking = (first & (1U << 18)) != 0,
                needsCaretInfo = (first & (1U << 19)) != 0,
                invalidLogAttributes = (second & 1) != 0, clusterSizeVaries = (second & 8) != 0
            });
        }

        // CTYPE1 is per original WCHAR, including surrogate halves. It is not
        // a scalar classifier and is not substituted for SCRIPT_LOGATTR.
        ushort[] characterTypes = new ushort[text.Length];
        bool typed = GetStringTypeW(1, text, text.Length, characterTypes);
        int typeError = typed ? 0 : Marshal.GetLastPInvokeError();
        result["characterTypesSucceeded"] = typed;
        if (!typed)
        {
            result["characterTypesError"] = typeError;
            throw new InvalidOperationException($"GetStringTypeW failed: {typeError}.");
        }
        result["ctype1"] = characterTypes.Select((value, index) => new
        {
            index, utf16 = (int)text[index], raw = (int)value,
            space = (value & 8) != 0, punctuation = (value & 16) != 0,
            control = (value & 32) != 0, blank = (value & 64) != 0,
            alphabetic = (value & 256) != 0
        }).ToArray();
        // Record the original per-WCHAR bidi/text-processing types separately.
        // Neither API is presumed equivalent to the Unicode17 scalar worker,
        // a soft-break property, a portable engine ID or an EDIT boundary.
        ushort[] bidiTypes = new ushort[text.Length];
        bool bidiTyped = GetStringTypeW(2, text, text.Length, bidiTypes);
        int bidiError = bidiTyped ? 0 : Marshal.GetLastPInvokeError();
        result["ctype2Succeeded"] = bidiTyped;
        result["ctype2Error"] = bidiError;
        if (!bidiTyped) throw new InvalidOperationException($"GetStringTypeW CTYPE2 failed: {bidiError}.");
        result["ctype2"] = bidiTypes.Select((value, index) => new
        {
            index, utf16 = (int)text[index], raw = (int)value
        }).ToArray();
        ushort[] processingTypes = new ushort[text.Length];
        bool processingTyped = GetStringTypeW(4, text, text.Length, processingTypes);
        int processingError = processingTyped ? 0 : Marshal.GetLastPInvokeError();
        result["ctype3Succeeded"] = processingTyped;
        result["ctype3Error"] = processingError;
        if (!processingTyped) throw new InvalidOperationException($"GetStringTypeW CTYPE3 failed: {processingError}.");
        result["ctype3"] = processingTypes.Select((value, index) => new
        {
            index, utf16 = (int)text[index], raw = (int)value,
            nonspacing = (value & 0x0001) != 0, diacritic = (value & 0x0002) != 0,
            vowelMark = (value & 0x0004) != 0, symbol = (value & 0x0008) != 0,
            katakana = (value & 0x0010) != 0, hiragana = (value & 0x0020) != 0,
            halfWidth = (value & 0x0040) != 0, fullWidth = (value & 0x0080) != 0,
            ideograph = (value & 0x0100) != 0, kashida = (value & 0x0200) != 0,
            lexical = (value & 0x0400) != 0, highSurrogate = (value & 0x0800) != 0,
            lowSurrogate = (value & 0x1000) != 0, alphabetic = (value & 0x8000) != 0,
            reserved = value & 0x6000
        }).ToArray();
        result["completed"] = true;
        return hasWordBreakingRun;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScriptItem
    {
        public int Start;
        public ushort Analysis;
        public ushort State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScriptAnalysis { public ushort Analysis; public ushort State; }

    private sealed record Position(int Index, long Raw, Point? Point);
    private sealed record Hit(int Index, int Quarter, int FollowingIndex, Point Start, Point Following,
        Point Point, int NativeCharacter, int NativeLine, long RawHit);
    private sealed record Selection(int Start, int End, int ManagedStart, int ManagedLength,
        string SelectedText, bool Focused, bool CaptureOwned, long CaptureHandle, string PhysicalButtons);

    private static Position[] ReadPositions(nint handle, int length)
        => Enumerable.Range(0, length + 1).Select(index =>
        {
            long raw = SendMessageW(handle, PositionFromCharacter, index, 0).ToInt64();
            return new Position(index, raw, unchecked((int)raw) == -1 ? null
                : new Point(unchecked((short)raw), unchecked((short)(raw >> 16))));
        }).ToArray();

    private static Hit? ResolveHit(Probe editor, nint handle, Position[] positions, int index, int quarter)
    {
        if ((uint)index >= (uint)(positions.Length - 1) || positions[index].Point is not Point start) return null;
        int next = index + 1;
        while (next < positions.Length && positions[next].Point == start) next++;
        if (next == positions.Length || positions[next].Point is not Point following || following.Y != start.Y
            || Math.Abs(following.X - start.X) < 2) return null;
        Point point = new(start.X + (following.X - start.X) * quarter / 4, start.Y);
        if (!editor.ClientRectangle.Contains(point)) return null;
        long rawHit = SendMessageW(handle, CharacterFromPosition, 0, Pack(point)).ToInt64();
        if (unchecked((int)rawHit) == -1) return null;
        int character = (int)(rawHit & 0xffff), line = (int)((rawHit >> 16) & 0xffff);
        if (character >= editor.TextLength) return null;
        return new Hit(index, quarter, next, start, following, point, character, line, rawHit);
    }

    private static Selection ReadSelection(Probe editor, nint handle)
    {
        SendMessageSelection(handle, GetSelection, out uint start, out uint end);
        nint capture = GetCapture();
        return new(checked((int)start), checked((int)end), editor.SelectionStart, editor.SelectionLength,
            editor.SelectedText, editor.Focused, capture == handle, capture.ToInt64(), Control.MouseButtons.ToString());
    }

    private static void RequireOwner(Probe editor, nint handle)
    {
        if (!editor.IsHandleCreated || editor.Handle != handle || editor.IsDisposed
            || GetWindowThreadProcessId(handle, out uint process) == 0 || process != Environment.ProcessId)
            throw new InvalidOperationException("Owned EDIT handle retired.");
        string className = ClassName(handle);
        if (!className.Equals("EDIT", StringComparison.OrdinalIgnoreCase)
            && !className.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected native class {className}.");
    }

    private static string ClassName(nint handle)
    {
        char[] value = new char[256];
        int count = GetClassNameW(handle, value, value.Length);
        if (count == 0) throw new System.ComponentModel.Win32Exception();
        if (count >= value.Length - 1) throw new InvalidOperationException("Native class name was truncated.");
        return new string(value, 0, count);
    }

    private sealed class Probe : TextBox
    {
        internal string Step = "setup";
        internal readonly List<object> Callbacks = [];
        internal bool CallbackOverflow;
        internal int DoubleDownCount;
        private bool _observing;

        internal void BeginObservation()
        {
            MouseDown += (_, e) => Record("MouseDown", e);
            MouseMove += (_, e) => Record("MouseMove", e);
            MouseUp += (_, e) => Record("MouseUp", e);
            Click += (_, _) => Record("Click");
            MouseClick += (_, e) => Record("MouseClick", e);
            DoubleClick += (_, _) => Record("DoubleClick");
            MouseDoubleClick += (_, e) => Record("MouseDoubleClick", e);
            MouseCaptureChanged += (_, _) => Record("MouseCaptureChanged");
            _observing = true;
        }

        private void Record(string name, MouseEventArgs? e = null)
        {
            if (!_observing || !IsHandleCreated) return;
            if (Callbacks.Count >= MaximumCallbacks) { CallbackOverflow = true; return; }
            if (name == "MouseDown" && e?.Clicks == 2) DoubleDownCount++;
            Callbacks.Add(new { sequence = Callbacks.Count, step = Step, name,
                mouse = e is null ? null : new { button = e.Button.ToString(), e.Clicks, e.X, e.Y },
                selection = ReadSelection(this, Handle) });
        }

        protected override void WndProc(ref Message m)
        {
            bool pointer = (uint)m.Msg is WordSelectionReference.MouseMove or WordSelectionReference.LeftDown
                or WordSelectionReference.LeftUp or WordSelectionReference.LeftDouble;
            if (pointer) Record("WndProc-enter");
            base.WndProc(ref m);
            if (pointer) Record("WndProc-return");
        }
    }

    private static nint Pack(Point point) => unchecked((nint)((uint)(ushort)point.X | (uint)(ushort)point.Y << 16));
    [DllImport("user32", EntryPoint = "SendMessageW")] private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32", EntryPoint = "SendMessageW")] private static extern nint SendMessageSelection(nint hwnd, uint message, out uint start, out uint end);
    [DllImport("user32")] private static extern nint GetCapture();
    [DllImport("user32", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetClassNameW(nint hwnd, [Out] char[] name, int capacity);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("usp10.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ScriptStringAnalyse(nint hdc, [MarshalAs(UnmanagedType.LPWStr)] string text,
        int characterCount, int glyphCapacity, int charset, uint flags, int requiredWidth,
        nint control, nint state, nint advances, nint tabs, nint inputClasses, out nint analysis);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("usp10.dll", ExactSpelling = true)] private static extern nint ScriptString_pLogAttr(nint analysis);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("usp10.dll", ExactSpelling = true)] private static extern int ScriptStringFree(ref nint analysis);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("usp10.dll", EntryPoint = "ScriptBreak", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ObserveDirectScriptBreak([MarshalAs(UnmanagedType.LPWStr)] string text,
        int characterCount, in ScriptAnalysis analysis, [Out] byte[] attributes);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("usp10.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ScriptItemize([MarshalAs(UnmanagedType.LPWStr)] string text, int characterCount,
        int itemCapacity, in uint control, in ushort state, [Out] ScriptItem[] items, out int itemCount);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("usp10.dll", ExactSpelling = true)]
    private static extern int ScriptGetProperties(out nint properties, out int count);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStringTypeW(uint type, [MarshalAs(UnmanagedType.LPWStr)] string text,
        int characterCount, [Out] ushort[] characterTypes);
}
