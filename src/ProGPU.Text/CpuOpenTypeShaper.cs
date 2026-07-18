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
        var overrides = new OpenTypeFeatureSetting[requestedFeatures.Length];
        var explicitTags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < requestedFeatures.Length; index++)
        {
            ShapingFeature feature = requestedFeatures[index];
            string tag = feature.Tag.ToString();
            overrides[index] = new OpenTypeFeatureSetting(tag, checked((int)Math.Min(feature.Value, int.MaxValue)));
            explicitTags.Add(tag);
        }

        TextShapingOptions resolved = TextShapingOptions.WithFeatures(overrides);
        var options = new TextShapingOptions
        {
            Script = request.Script == OpenTypeTag.DefaultScript ? null : request.Script.ToString(),
            Language = request.Language,
            Direction = request.Direction,
            Features = resolved.Features,
            ExplicitFeatureTags = explicitTags,
            ClusterLevel = request.ClusterLevel,
            BufferFlags = request.Flags,
            RangedFeatures = request.Features
        };

        OpenTypeTextShaper.ShapeDesignUnits(text.ToString(), ttfFace.Font, options, buffer);
    }
}
