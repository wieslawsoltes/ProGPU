using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

internal static partial class Program
{
    // Diagnostic-only access to the actual source request seam. No implementation
    // is copied, no property ratio is interpreted as a Display metric, and no
    // output TextLine supplies an expected input. Missing internals fail closed.
    private static object CaptureSourceMetrics(Typeface typeface, string font,
        TextFormattingMode mode, double em, double dpi)
    {
        if (typeof(Typeface).Assembly != typeof(TextFormatter).Assembly ||
            Convert.ToHexString(typeof(Typeface).Assembly.GetName().GetPublicKeyToken() ?? []) != "31BF3856AD364E35")
            throw new InvalidOperationException("Source metrics require the original Microsoft Typeface assembly.");
        if (!typeface.TryGetGlyphTypeface(out var face) || !face.FontUri.IsFile ||
            !string.Equals(Path.GetFullPath(face.FontUri.LocalPath), font, StringComparison.OrdinalIgnoreCase) ||
            face.StyleSimulations != StyleSimulations.None)
            throw new InvalidOperationException("Independent metrics did not resolve the exact original physical face.");

        double baseline = Invoke("Baseline"), spacing = Invoke("LineSpacing");
        Finite(baseline); Finite(spacing);
        if (baseline <= 0 || spacing < baseline)
            throw new InvalidOperationException("Original independent source metrics are outside the retained horizontal family.");
        return new
        {
            Version = 1, Mode = mode.ToString(), Em = em, Dpi = dpi, ToReal = 1.0,
            Baseline = baseline, LineSpacing = spacing,
            Font = FileIdentity(font), FontUri = face.FontUri.AbsoluteUri,
            TypefaceIdentity = typeof(Typeface).Assembly.FullName,
            TypefaceAssembly = FileIdentity(typeof(Typeface).Assembly.Location),
            BaselineMethod = "System.Windows.Media.Typeface.Baseline(Double,Double,Double,TextFormattingMode):Double",
            LineSpacingMethod = "System.Windows.Media.Typeface.LineSpacing(Double,Double,Double,TextFormattingMode):Double",
            CaptureOrder = "Before TextFormatter creation and FormatLine; original source request metrics, not line outputs."
        };

        double Invoke(string name)
        {
            MethodInfo? method = typeof(Typeface).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                binder: null, types: [typeof(double), typeof(double), typeof(double), typeof(TextFormattingMode)], modifiers: null);
            if (method is null || method.DeclaringType != typeof(Typeface) || method.ReturnType != typeof(double) ||
                !method.IsAssembly || method.IsStatic || method.IsGenericMethod)
                throw new MissingMethodException("The exact original Typeface source-metric method is unavailable: " + name);
            try { return (double)method.Invoke(typeface, [em, 1.0, dpi, mode])!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
