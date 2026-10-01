using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

internal static partial class WordSelectionReference
{
    internal static int RunPolicyContexts(string path)
    {
        var cases = new List<Case>();
        // Contextual discriminants, not expected boundary tables. Include BMP
        // ID/AL/emoji-style symbols and variation presentation independently.
        foreach (int scalar in new[] { 0x00A9, 0x00AE, 0x2122, 0x2194, 0x2300,
            0x231A, 0x23F0, 0x25FD, 0x2600, 0x2603, 0x260E, 0x2615, 0x263A,
            0x2640, 0x2665, 0x2695, 0x26A1, 0x26BD, 0x2708, 0x2764, 0x3030, 0x3299 })
        {
            string symbol = char.ConvertFromUtf32(scalar);
            Add($"bmp-{scalar:X4}-latin", "a" + symbol + "b ");
            Add($"bmp-{scalar:X4}-variation", "a" + symbol + "\uFE0Fb ");
        }
        foreach ((string name, string left, string right) in new[] {
            ("latin", "a", "b"), ("arabic", "\u0628", "\u062A"),
            ("devanagari", "\u0915\u094D", "\u0937"),
            ("bmp-symbol", "\u2603", "\u2764"),
            ("mixed-emoji", "\u2764\uFE0F", "\U0001F4BB"),
            ("supplementary-emoji", "\U0001F469", "\U0001F4BB"),
            ("supplementary-letter", "\U00010400", "\U00010401"),
            ("supplementary-cjk", "\U00020000", "\U00020001") })
        {
            Add(name + "-no-joiner", "x" + left + right + "y ");
            Add(name + "-zwj", "x" + left + "\u200D" + right + "y ");
            Add(name + "-zwnj", "x" + left + "\u200C" + right + "y ");
        }
        Add("zwj-leading", "\u200Da\U0001F600b ");
        Add("zwj-trailing", "a\U0001F600\u200D ");
        Add("zwj-space", "a \u200Db ");
        Add("zwj-repeat", "a\U0001F469\u200D\u200D\U0001F4BBb ");
        if (cases.Count != 72) throw new InvalidOperationException("Context inventory changed.");
        return RunCases(path, "PerMonitorV2", "true", cases.ToArray());

        void Add(string name, string text)
        {
            if (text.Length is < 2 or > 16) throw new InvalidOperationException("Unbounded context input.");
            cases.Add(new Case(name, text, Enumerable.Range(0, text.Length).ToArray()));
        }
    }

    internal static int RunItemPolicyContexts(string path)
    {
        var cases = new List<Case>();
        // An actual repeated-symbol item exposes interior ScriptBreak bytes;
        // the first byte of a single-symbol item cannot identify that policy.
        // Do not manufacture SCRIPT_ANALYSIS or call across original items.
        foreach (int scalar in new[] { 0x00A9, 0x00AE, 0x2122, 0x2194, 0x2300,
            0x231A, 0x23F0, 0x25FD, 0x2600, 0x2603, 0x260E, 0x2615, 0x263A,
            0x2640, 0x2665, 0x2695, 0x26A1, 0x26BD, 0x2708, 0x2764, 0x3030, 0x3299 })
        {
            string symbol = char.ConvertFromUtf32(scalar);
            Add($"item-{scalar:X4}-repeat", symbol + symbol + " ");
            Add($"item-{scalar:X4}-variation-repeat", symbol + "\uFE0F" + symbol + "\uFE0F ");
        }
        foreach ((string name, string prefix) in new[] {
            ("latin", "x"), ("greek", "\u03B1"), ("hebrew", "\u05D0"), ("cjk", "\u4E00"),
            ("digit", "1"), ("comma", "x,"), ("period", "x."), ("hyphen", "x-"),
            ("space", "x "), ("nbsp", "x\u00A0"), ("tab", "x\t"),
            ("zwj", "x\u200D"), ("zwnj", "x\u200C"), ("mark", "x\u0301"),
            ("arabic-mark", "x\u064E"), ("arabic-presentation", "x\uFE8F"),
            ("arabic-number", "x\u0661"), ("embedding", "x\u202B") })
        {
            Add("arabic-entry-" + name + "-ltr", prefix + "\u0628\u062Ay ");
            Add("arabic-entry-" + name + "-rtl", prefix + "\u0628\u062Ay ", true);
        }
        foreach ((string script, string text) in new[] {
            ("thai", "\u0E01\u0E02"), ("lao", "\u0E81\u0E82"), ("khmer", "\u1780\u1781"),
            ("syriac", "\u0710\u0712"), ("myanmar", "\u1000\u1001"),
            ("devanagari", "\u0915\u0937"), ("hebrew", "\u05D0\u05D1"), ("arabic", "\u0628\u062A") })
        {
            foreach ((string name, string prefix) in new[] { ("latin", "x"), ("cjk", "\u4E00"), ("space", "x ") })
            {
                Add("script-entry-" + script + "-" + name + "-ltr", prefix + text + "y ");
                Add("script-entry-" + script + "-" + name + "-rtl", prefix + text + "y ", true);
            }
        }
        if (cases.Count != 128) throw new InvalidOperationException("Item-context inventory changed.");
        return RunCases(path, "PerMonitorV2", "true", cases.ToArray());

        void Add(string name, string text, bool rightToLeft = false)
        {
            if (text.Length is < 2 or > 16) throw new InvalidOperationException("Unbounded item-context input.");
            cases.Add(new Case(name, text, Enumerable.Range(0, text.Length).ToArray(), RightToLeft: rightToLeft));
        }
    }

    internal static int RunSymbolAttributes(string path)
    {
        using FileStream file = new(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write);
        var budget = Stopwatch.StartNew();
        var cases = new List<object>();
        var modules = new List<object>();
        string? failure = null;
        int symbols = 0;
        try
        {
            if (!typeof(Control).Assembly.Location.Contains("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Not the original Microsoft reference.");
            // Complete BMP symbol-property sweep from the ACTUAL pinned runtime,
            // with its category recorded. This is reference observation, not
            // the portable Unicode worker or an expected classifier output.
            for (int scalar = 0; scalar <= 0xFFFF; scalar++)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory((char)scalar);
                if (category is not (UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol
                    or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol)) continue;
                symbols++;
                foreach ((string name, string text) in new[] {
                    ("latin", "a" + (char)scalar + "b "),
                    ("variation", "a" + (char)scalar + "\uFE0Fb "),
                    ("cjk", "\u4E00" + (char)scalar + "\u4E8C ") })
                {
                    CheckBudget(budget);
                    var observation = new Dictionary<string, object?>();
                    cases.Add(new { name = $"bmp-{scalar:X4}-{name}", scalar,
                        runtimeCategory = category.ToString(), requestedText = text,
                        requestedUtf16 = Utf16(text), scriptBreak = observation });
                    ObserveScriptBreak(text, false, false, observation);
                    if (modules.Count == 0) ObserveNativeTextModules(modules);
                    if (cases.Count > 12288) throw new InvalidOperationException("Symbol sweep exceeded its hard bound.");
                }
            }
            if (symbols == 0 || cases.Count != symbols * 3)
                throw new InvalidOperationException("Incomplete BMP symbol inventory.");
        }
        catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(failure); }
        var receipt = new {
            schema = "native-edit-symbol-attributes-v1", completed = failure is null,
            desktopQualified = false, physicalInputQualified = false, hostShown = false,
            api = "ScriptStringAnalyse/ScriptString_pLogAttr; independent ScriptItemize/ScriptBreak/GetStringTypeW",
            editGesturesObserved = false, symbolCount = symbols, expectedCases = symbols * 3,
            identity = new {
                framework = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription, osBuild = Environment.OSVersion.Version.ToString(),
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), processId = Environment.ProcessId,
                locale = CultureInfo.CurrentCulture.Name, uiLocale = CultureInfo.CurrentUICulture.Name,
                forms = typeof(Control).Assembly.FullName, formsPath = typeof(Control).Assembly.Location,
                formsSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Control).Assembly.Location))).ToLowerInvariant(),
                probeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(WordSelectionReference).Assembly.Location))).ToLowerInvariant(),
                nativeTextModules = modules },
            cases, elapsedMilliseconds = budget.ElapsedMilliseconds, error = failure };
        JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        file.Flush();
        return failure is null ? 0 : 1;
    }
}
