using ProGPU.Text.Shaping;

namespace ProGPU.Text;

/// <summary>
/// Deterministic, managed OpenType shaping executor. The output uses signed
/// font-design units and can be consumed directly by CPU or WebGPU callers.
/// </summary>
public sealed class CpuOpenTypeShaper : IOpenTypeShaper
{
    public static CpuOpenTypeShaper Instance { get; } = new();

    public void Shape(
        ReadOnlySpan<char> text,
        IShapingFontFace font,
        in ShapingRequest request,
        ShapingBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(buffer);
        if (font is not TtfShapingFontFace ttfFace)
        {
            throw new ArgumentException(
                $"{nameof(CpuOpenTypeShaper)} requires a {nameof(TtfShapingFontFace)} so the parsed, immutable font tables can be reused.",
                nameof(font));
        }

        ReadOnlySpan<ShapingFeature> requestedFeatures = request.Features.Span;
        var globalOverrides = new List<OpenTypeFeatureSetting>(requestedFeatures.Length);
        var explicitTags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < requestedFeatures.Length; index++)
        {
            ShapingFeature feature = requestedFeatures[index];
            string tag = feature.Tag.ToString();
            if (feature.Start == 0 && feature.End == uint.MaxValue)
                globalOverrides.Add(new OpenTypeFeatureSetting(tag, checked((int)Math.Min(feature.Value, int.MaxValue))));
            explicitTags.Add(tag);
        }

        TextShapingOptions baseline = TextShapingOptions.WithFeatures(globalOverrides.ToArray());
        var selectedFeatures = baseline.Features.ToList();
        foreach (ShapingFeature feature in requestedFeatures)
        {
            string tag = feature.Tag.ToString();
            if (feature.Value == 0 || selectedFeatures.Any(setting => setting.Tag == tag && setting.Value != 0)) continue;
            selectedFeatures.Add(new OpenTypeFeatureSetting(tag, 1));
        }
        var options = new TextShapingOptions
        {
            Script = request.Script == OpenTypeTag.DefaultScript ? null : request.Script.ToString(),
            Language = request.Language,
            Direction = request.Direction,
            Features = selectedFeatures,
            ExplicitFeatureTags = explicitTags,
            ClusterLevel = request.ClusterLevel,
            BufferFlags = request.Flags,
            RangedFeatures = request.Features,
            BaseFeatures = baseline.Features
        };

        OpenTypeTextShaper.ShapeDesignUnits(
            text.ToString(),
            ttfFace.Font,
            options,
            buffer,
            request.PreContext,
            request.PostContext);
    }
}
