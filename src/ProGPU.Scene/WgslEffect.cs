using System;
using ProGPU.Backend;

namespace ProGPU.Scene;

/// <summary>
/// Immutable, cacheable WGSL image-effect module. The module must implement
/// <c>progpu_effect_main(input: ProGpuEffectInput) -&gt; vec4&lt;f32&gt;</c>.
/// </summary>
public sealed class WgslEffectDefinition
{
    public WgslEffectDefinition(string key, string source)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A stable effect key is required.", nameof(key));
        }
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("WGSL effect source is required.", nameof(source));
        }

        Key = key;
        Source = source;
    }

    public string Key { get; }

    public string Source { get; }
}

/// <summary>
/// Mutable inputs for a reusable WGSL image effect. Constants map to
/// <c>progpu_constant(0u..31u)</c>; samplers map to
/// <c>progpu_sample(register, uv)</c>.
/// </summary>
public sealed class WgslEffectParameters
{
    private static readonly string Prelude =
        ShaderResource.Load(typeof(WgslEffectParameters), "WgslEffectPrelude.wgsl");
    private static readonly string Adapter =
        ShaderResource.Load(typeof(WgslEffectParameters), "WgslEffectAdapter.wgsl");

    private readonly WpfShaderEffectParams _adapterParameters = new();
    private string? _adaptedDefinitionKey;
    private string? _adaptedDefinitionSource;
    private string? _adaptedSource;

    public WgslEffectParameters(WgslEffectDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public WgslEffectDefinition Definition { get; set; }

    public GpuTexture? SourceTexture { get; set; }

    public Rect Bounds { get; set; }

    public float[] Constants { get; set; } = Array.Empty<float>();

    public WgslEffectSampler[] Samplers { get; set; } = Array.Empty<WgslEffectSampler>();

    public TextureSamplingMode SamplingMode { get; set; } = TextureSamplingMode.Linear;

    public bool IsFailed => _adapterParameters.IsFailed;

    public string? LastError => _adapterParameters.LastError;

    internal WpfShaderEffectParams GetAdapterParameters()
    {
        var definition = Definition ?? throw new InvalidOperationException("WGSL effect definition is required.");
        if (!string.Equals(_adaptedDefinitionKey, definition.Key, StringComparison.Ordinal) ||
            !string.Equals(_adaptedDefinitionSource, definition.Source, StringComparison.Ordinal))
        {
            _adaptedSource = string.Concat(Prelude, "\n", definition.Source, "\n", Adapter);
            _adaptedDefinitionKey = definition.Key;
            _adaptedDefinitionSource = definition.Source;
            _adapterParameters.IsFailed = false;
            _adapterParameters.LastError = null;
        }

        _adapterParameters.Texture = SourceTexture;
        _adapterParameters.Rect = Bounds;
        _adapterParameters.ShaderSource = _adaptedSource!;
        _adapterParameters.ShaderKey = "progpu_wgsl_effect_" + definition.Key;
        _adapterParameters.Constants = Constants;
        _adapterParameters.SamplingMode = SamplingMode;
        _adapterParameters.SourceTextureRegisterIndex = 0;
        _adapterParameters.SourceTextureOverridesSampler = true;

        var sourceSamplers = Samplers;
        if (_adapterParameters.Samplers.Length != sourceSamplers.Length)
        {
            _adapterParameters.Samplers = new WpfShaderEffectSampler[sourceSamplers.Length];
            for (var index = 0; index < sourceSamplers.Length; index++)
            {
                _adapterParameters.Samplers[index] = new WpfShaderEffectSampler();
            }
        }

        for (var index = 0; index < sourceSamplers.Length; index++)
        {
            var source = sourceSamplers[index];
            var target = _adapterParameters.Samplers[index];
            target.RegisterIndex = source.Binding;
            target.Texture = source.Texture;
            target.SamplingMode = source.SamplingMode;
        }

        return _adapterParameters;
    }
}

public sealed class WgslEffectSampler
{
    private int _binding;

    public WgslEffectSampler()
    {
    }

    public WgslEffectSampler(
        int binding,
        GpuTexture? texture,
        TextureSamplingMode samplingMode = TextureSamplingMode.Linear)
    {
        Binding = binding;
        Texture = texture;
        SamplingMode = samplingMode;
    }

    public int Binding
    {
        get => _binding;
        set
        {
            WpfShaderEffectParams.ValidateSamplerRegister(value);
            _binding = value;
        }
    }

    public GpuTexture? Texture { get; set; }

    public TextureSamplingMode SamplingMode { get; set; } = TextureSamplingMode.Linear;
}

/// <summary>
/// A retained visual effect backed entirely by a user WGSL function.
/// </summary>
public sealed class WgslEffect : EffectBase
{
    private float _padding;

    public WgslEffect(WgslEffectParameters parameters)
    {
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public WgslEffectParameters Parameters { get; }

    public float Padding
    {
        get => _padding;
        set
        {
            if (_padding != value)
            {
                _padding = value;
                Invalidate();
            }
        }
    }

    public bool IsFailed => Parameters.IsFailed;

    public string? LastError => Parameters.LastError;

    /// <summary>
    /// Advances retained-scene invalidation after mutating parameter arrays or sampler inputs.
    /// </summary>
    public void InvalidateEffect() => Invalidate();

    internal WpfShaderEffectParams UpdateDrawParameters(GpuTexture sourceTexture, Rect bounds)
    {
        Parameters.SourceTexture = sourceTexture;
        Parameters.Bounds = bounds;
        return Parameters.GetAdapterParameters();
    }

    internal override int GetRenderCacheKey()
    {
        var hash = new HashCode();
        hash.Add(base.GetRenderCacheKey());
        hash.Add(Padding);
        Parameters.GetAdapterParameters().AddRenderCacheKey(ref hash);
        return hash.ToHashCode();
    }
}

public static class WgslEffectShaders
{
    public static readonly WgslEffectDefinition PassThrough = new(
        "pass_through_v1",
        ShaderResource.Load(typeof(WgslEffectShaders), "WgslEffectPassThrough.wgsl"));

    public static readonly WgslEffectDefinition VoxelWeather = new(
        "voxel_weather_v1",
        ShaderResource.Load(typeof(WgslEffectShaders), "WgslVoxelWeatherEffect.wgsl"));
}
