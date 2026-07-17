using ProGPU.Text;

namespace ProGPU.Fonts.Noto;

/// <summary>
/// Provides official Noto fallback faces for scripts and symbols outside Inter's repertoire.
/// Faces are parsed once on first glyph demand rather than during application startup.
/// </summary>
public static class NotoFontFamily
{
    public const string CjkVersion = "2.004";
    public const string SymbolsVersion = "2.006";

    private const string JapaneseFamilyName = "Noto Sans CJK JP";
    private const string SymbolsFamilyName = "Noto Sans Symbols 2";
    private const string JapaneseResourceName = "ProGPU.Fonts.Noto.Fonts.NotoSansCJKjp-Regular.otf";
    private const string SymbolsResourceName = "ProGPU.Fonts.Noto.Fonts.NotoSansSymbols2-Regular.ttf";

    private static readonly Lazy<TtfFont> s_japanese = CreateLazy(JapaneseResourceName);
    private static readonly Lazy<TtfFont> s_symbols = CreateLazy(SymbolsResourceName);

    /// <summary>Gets the shared Japanese CJK face.</summary>
    public static TtfFont Japanese => s_japanese.Value;

    /// <summary>Gets the shared broad Unicode symbol face.</summary>
    public static TtfFont Symbols => s_symbols.Value;

    /// <summary>
    /// Registers lazy fallback descriptors with the supplied manager. This operation does not
    /// read or parse either embedded font and is idempotent for a manager instance.
    /// </summary>
    public static void RegisterFallbacks(FontManager? manager = null)
    {
        manager ??= FontManager.Default;

        // Symbols comes first for generic fallback. Script-specific Japanese matching uses the
        // explicit family preference and therefore does not probe or parse Symbols first.
        manager.RegisterFont(SymbolsFamilyName, s_symbols, FontStyleRequest.Normal, isFallback: true);
        manager.RegisterFont(JapaneseFamilyName, s_japanese, FontStyleRequest.Normal, isFallback: true);
    }

    private static Lazy<TtfFont> CreateLazy(string resourceName) =>
        new(() => Load(resourceName), LazyThreadSafetyMode.ExecutionAndPublication);

    private static TtfFont Load(string resourceName)
    {
        using var stream = typeof(NotoFontFamily).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded Noto font '{resourceName}' is missing.");
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return new TtfFont(data);
    }
}
