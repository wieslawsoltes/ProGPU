using ProGPU.Text;

namespace ProGPU.Fonts.Inter;

/// <summary>
/// Provides all unmodified static and variable faces from the official Inter 4.1 release.
/// Individual faces are parsed once on first use; catalog registration is allocation-light
/// and does not parse faces that the application never requests.
/// </summary>
public static class InterFontFamily
{
    private sealed record FaceDescriptor(
        string FamilyName,
        FontStyleRequest Style,
        Lazy<TtfFont> Font);

    public const string Version = "4.1";
    public const string TextFamilyName = "Inter";
    public const string DisplayFamilyName = "Inter Display";
    public const string VariableFamilyName = "Inter Variable";

    private const string ResourcePrefix = "ProGPU.Fonts.Inter.Fonts.";

    private static readonly Lazy<TtfFont> s_variable = VariableFace("InterVariable.ttf");
    private static readonly Lazy<TtfFont> s_variableItalic = VariableFace("InterVariable-Italic.ttf");

    private static readonly FaceDescriptor[] s_faces =
    [
        Face(TextFamilyName, "Inter-Thin.ttf", 100, FontSlant.Upright),
        Face(TextFamilyName, "Inter-ThinItalic.ttf", 100, FontSlant.Italic),
        Face(TextFamilyName, "Inter-ExtraLight.ttf", 200, FontSlant.Upright),
        Face(TextFamilyName, "Inter-ExtraLightItalic.ttf", 200, FontSlant.Italic),
        Face(TextFamilyName, "Inter-Light.ttf", 300, FontSlant.Upright),
        Face(TextFamilyName, "Inter-LightItalic.ttf", 300, FontSlant.Italic),
        Face(TextFamilyName, "Inter-Regular.ttf", 400, FontSlant.Upright),
        Face(TextFamilyName, "Inter-Italic.ttf", 400, FontSlant.Italic),
        Face(TextFamilyName, "Inter-Medium.ttf", 500, FontSlant.Upright),
        Face(TextFamilyName, "Inter-MediumItalic.ttf", 500, FontSlant.Italic),
        Face(TextFamilyName, "Inter-SemiBold.ttf", 600, FontSlant.Upright),
        Face(TextFamilyName, "Inter-SemiBoldItalic.ttf", 600, FontSlant.Italic),
        Face(TextFamilyName, "Inter-Bold.ttf", 700, FontSlant.Upright),
        Face(TextFamilyName, "Inter-BoldItalic.ttf", 700, FontSlant.Italic),
        Face(TextFamilyName, "Inter-ExtraBold.ttf", 800, FontSlant.Upright),
        Face(TextFamilyName, "Inter-ExtraBoldItalic.ttf", 800, FontSlant.Italic),
        Face(TextFamilyName, "Inter-Black.ttf", 900, FontSlant.Upright),
        Face(TextFamilyName, "Inter-BlackItalic.ttf", 900, FontSlant.Italic),

        Face(DisplayFamilyName, "InterDisplay-Thin.ttf", 100, FontSlant.Upright),
        Face(DisplayFamilyName, "InterDisplay-ThinItalic.ttf", 100, FontSlant.Italic),
        Face(DisplayFamilyName, "InterDisplay-ExtraLight.ttf", 200, FontSlant.Upright),
        Face(DisplayFamilyName, "InterDisplay-ExtraLightItalic.ttf", 200, FontSlant.Italic),
        Face(DisplayFamilyName, "InterDisplay-Light.ttf", 300, FontSlant.Upright),
        Face(DisplayFamilyName, "InterDisplay-LightItalic.ttf", 300, FontSlant.Italic),
        Face(DisplayFamilyName, "InterDisplay-Regular.ttf", 400, FontSlant.Upright),
        Face(DisplayFamilyName, "InterDisplay-Italic.ttf", 400, FontSlant.Italic),
        Face(DisplayFamilyName, "InterDisplay-Medium.ttf", 500, FontSlant.Upright),
        Face(DisplayFamilyName, "InterDisplay-MediumItalic.ttf", 500, FontSlant.Italic),
        Face(DisplayFamilyName, "InterDisplay-SemiBold.ttf", 600, FontSlant.Upright),
        Face(DisplayFamilyName, "InterDisplay-SemiBoldItalic.ttf", 600, FontSlant.Italic),
        Face(DisplayFamilyName, "InterDisplay-Bold.ttf", 700, FontSlant.Upright),
        Face(DisplayFamilyName, "InterDisplay-BoldItalic.ttf", 700, FontSlant.Italic),
        Face(DisplayFamilyName, "InterDisplay-ExtraBold.ttf", 800, FontSlant.Upright),
        Face(DisplayFamilyName, "InterDisplay-ExtraBoldItalic.ttf", 800, FontSlant.Italic),
        Face(DisplayFamilyName, "InterDisplay-Black.ttf", 900, FontSlant.Upright),
        Face(DisplayFamilyName, "InterDisplay-BlackItalic.ttf", 900, FontSlant.Italic)
    ];

    /// <summary>Gets Inter Regular.</summary>
    public static TtfFont Regular => GetFont(400);

    /// <summary>Gets Inter Bold.</summary>
    public static TtfFont Bold => GetFont(700);

    /// <summary>Gets the true Inter Italic face.</summary>
    public static TtfFont Italic => GetFont(400, FontSlant.Italic);

    /// <summary>Gets Inter Display Regular, the large optical-size design.</summary>
    public static TtfFont Display => GetFont(400, FontSlant.Upright, display: true);

    /// <summary>Gets the official upright variable font at its default coordinates.</summary>
    public static TtfFont Variable => s_variable.Value;

    /// <summary>Gets the official true-italic variable font at its default coordinates.</summary>
    public static TtfFont VariableItalic => s_variableItalic.Value;

    /// <summary>
    /// Gets a continuous Inter instance using the standard <c>wght</c> and <c>opsz</c>
    /// OpenType axes. Values are clamped by the font to wght 100–900 and opsz 14–32.
    /// </summary>
    public static TtfFont GetVariableFont(
        float weight = 400f,
        float opticalSize = 14f,
        FontSlant slant = FontSlant.Upright)
    {
        TtfFont source = slant == FontSlant.Upright ? Variable : VariableItalic;
        return source.WithVariations(
            new FontVariationSetting("wght", weight),
            new FontVariationSetting("opsz", opticalSize));
    }

    /// <summary>Gets the closest static Inter face for a weight, slant, and optical family.</summary>
    public static TtfFont GetFont(
        int weight = 400,
        FontSlant slant = FontSlant.Upright,
        bool display = false)
    {
        string familyName = display ? DisplayFamilyName : TextFamilyName;
        int normalizedWeight = Math.Clamp(weight, 1, 1000);
        FaceDescriptor? best = null;
        var bestDistance = int.MaxValue;
        for (var index = 0; index < s_faces.Length; index++)
        {
            FaceDescriptor candidate = s_faces[index];
            if (!candidate.FamilyName.Equals(familyName, StringComparison.Ordinal))
            {
                continue;
            }

            int distance = Math.Abs(candidate.Style.Weight - normalizedWeight) +
                           (candidate.Style.Slant == slant ? 0 : 10_000);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return (best ?? throw new InvalidOperationException($"No {familyName} face is registered.")).Font.Value;
    }

    /// <summary>
    /// Registers all 36 static faces with the shared font manager without parsing them.
    /// Repeated registration with the same manager is idempotent.
    /// </summary>
    public static void RegisterFonts(FontManager? manager = null)
    {
        manager ??= FontManager.Default;
        for (var index = 0; index < s_faces.Length; index++)
        {
            FaceDescriptor face = s_faces[index];
            manager.RegisterFont(face.FamilyName, face.Font, face.Style);
        }


        manager.RegisterFont(
            VariableFamilyName,
            s_variable,
            new FontStyleRequest(400, 5, FontSlant.Upright));
        manager.RegisterFont(
            VariableFamilyName,
            s_variableItalic,
            new FontStyleRequest(400, 5, FontSlant.Italic));
    }

    private static FaceDescriptor Face(string familyName, string fileName, int weight, FontSlant slant) =>
        new(
            familyName,
            new FontStyleRequest(weight, 5, slant),
            new Lazy<TtfFont>(
                () => Load(ResourcePrefix + fileName),
                LazyThreadSafetyMode.ExecutionAndPublication));

    private static Lazy<TtfFont> VariableFace(string fileName) =>
        new(
            () => Load(ResourcePrefix + fileName),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static TtfFont Load(string resourceName)
    {
        using var stream = typeof(InterFontFamily).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded Inter font '{resourceName}' is missing.");
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return new TtfFont(data);
    }
}
