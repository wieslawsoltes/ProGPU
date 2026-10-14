using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

internal static partial class Program
{
    private const string SoftWrapCaseFamily = "soft-wrap-spaces-v1";
    private static void CaptureSoftWrapCases(string font, FontFamily family,
        string rtlFont, FontFamily rtlFamily, Stopwatch timer, List<object> cases)
    {
        // New independent observations; no existing case or expected value is replaced.
        // Both physical fonts retain the original byte and no-fallback checks.
        string[] latin = ["AB CD EF ", "AB  CD   EF  ", "AB\u00a0CD EF ",
            "AB\u2002CD EF ", "a\u0301 b c ", "AB CD\nEF GH ", "AB 12 CD "];
        string[] hebrew = ["\u05d0\u05d1 \u05d2\u05d3 \u05d4\u05d5 ",
            "\u05d0\u05d1  \u05d2\u05d3   \u05d4\u05d5  ",
            "\u05d0\u05d1\u00a0\u05d2\u05d3 \u05d4\u05d5 ",
            "\u05d0\u05d1 \u05d2\u05d3\n\u05d4\u05d5 \u05d0\u05d1 "];
        foreach (bool rtlFace in new[] { false, true })
        foreach (string text in rtlFace ? hebrew : latin)
        foreach (FlowDirection direction in new[] { FlowDirection.LeftToRight, FlowDirection.RightToLeft })
        foreach (double dpi in new[] { 1.0, 1.25, 1.5 })
        foreach (double width in new[] { 25.0, 41.0, 1000.0 })
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException("Original soft-wrap reference exceeded 60 seconds.");
            cases.Add(Capture(rtlFace ? rtlFont : font, rtlFace ? rtlFamily : family,
                text, TextFormattingMode.Display, direction, dpi, 13.0, width, sourceInputs: true));
        }
        if (cases.Count != 198) throw new InvalidOperationException("Incomplete soft-wrap reference inventory.");
    }
}
