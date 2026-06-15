using System;
using ProGPU.Backend;

namespace ProGPU.Scene;

public sealed class WpfShaderEffectParams
{
    public const int MaxConstantRegisterCount = 32;
    public const int FloatsPerConstantRegister = 4;
    public const int ConstantFloatCount = MaxConstantRegisterCount * FloatsPerConstantRegister;
    public const int UniformFloatCount = ConstantFloatCount + 12;
    public const int UniformByteCount = UniformFloatCount * sizeof(float);
    public const int MaxSamplerRegisterCount = 16;

    public GpuTexture? Texture { get; set; }
    public Rect Rect { get; set; }
    public string ShaderSource { get; set; } = WpfShaderEffectShaders.PassThrough;
    public string ShaderKey { get; set; } = string.Empty;
    public float[] Constants { get; set; } = Array.Empty<float>();
    public WpfShaderEffectSampler[] Samplers { get; set; } = Array.Empty<WpfShaderEffectSampler>();
    public TextureSamplingMode SamplingMode { get; set; } = TextureSamplingMode.Linear;
    public bool IsFailed { get; set; }
    public string? LastError { get; set; }
    private int _sourceTextureRegisterIndex;
    internal bool SourceTextureOverridesSampler { get; set; }

    public int SourceTextureRegisterIndex
    {
        get => _sourceTextureRegisterIndex;
        set
        {
            ValidateSamplerRegister(value);
            _sourceTextureRegisterIndex = value;
        }
    }

    public string GetStableShaderKey()
    {
        if (!string.IsNullOrWhiteSpace(ShaderKey))
        {
            return ShaderKey;
        }

        return "wpf_shader_" + ComputeStableHash(GetShaderSourceOrDefault()).ToString("x16");
    }

    public string GetShaderSourceOrDefault()
    {
        return string.IsNullOrWhiteSpace(ShaderSource)
            ? WpfShaderEffectShaders.PassThrough
            : ShaderSource;
    }

    public void CopyUniformFloats(Span<float> destination, uint textureWidth, uint textureHeight)
    {
        if (destination.Length < UniformFloatCount)
        {
            throw new ArgumentException("Destination span is too small for WPF shader effect uniforms.", nameof(destination));
        }

        destination[..UniformFloatCount].Clear();

        if (Constants.Length > 0)
        {
            Constants
                .AsSpan(0, Math.Min(Constants.Length, ConstantFloatCount))
                .CopyTo(destination);
        }

        destination[ConstantFloatCount] = Rect.X;
        destination[ConstantFloatCount + 1] = Rect.Y;
        destination[ConstantFloatCount + 2] = Rect.Width;
        destination[ConstantFloatCount + 3] = Rect.Height;

        destination[ConstantFloatCount + 4] = textureWidth;
        destination[ConstantFloatCount + 5] = textureHeight;
        destination[ConstantFloatCount + 6] = textureWidth > 0 ? 1f / textureWidth : 0f;
        destination[ConstantFloatCount + 7] = textureHeight > 0 ? 1f / textureHeight : 0f;
        destination[ConstantFloatCount + 8] = SourceTextureRegisterIndex;
    }

    public bool HasAnyTexture()
    {
        if (Texture != null)
        {
            return true;
        }

        foreach (var sampler in Samplers)
        {
            if (sampler.Texture != null)
            {
                return true;
            }
        }

        return false;
    }

    public bool TryGetSampler(int registerIndex, out GpuTexture texture, out TextureSamplingMode samplingMode)
    {
        ValidateSamplerRegister(registerIndex);

        if (registerIndex == SourceTextureRegisterIndex && SourceTextureOverridesSampler && Texture != null)
        {
            texture = Texture;
            samplingMode = SamplingMode;
            return true;
        }

        foreach (var sampler in Samplers)
        {
            if (sampler.RegisterIndex == registerIndex && sampler.Texture != null)
            {
                texture = sampler.Texture;
                samplingMode = sampler.SamplingMode;
                return true;
            }
        }

        if (registerIndex == 0 && Texture != null)
        {
            texture = Texture;
            samplingMode = SamplingMode;
            return true;
        }

        texture = null!;
        samplingMode = TextureSamplingMode.Linear;
        return false;
    }

    public bool TryGetPrimaryTexture(out GpuTexture texture)
    {
        if (TryGetSampler(0, out texture, out _))
        {
            return true;
        }

        foreach (var sampler in Samplers)
        {
            if (sampler.Texture != null)
            {
                texture = sampler.Texture;
                return true;
            }
        }

        if (Texture != null)
        {
            texture = Texture;
            return true;
        }

        texture = null!;
        return false;
    }

    internal static void ValidateSamplerRegister(int registerIndex)
    {
        if ((uint)registerIndex >= MaxSamplerRegisterCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(registerIndex),
                registerIndex,
                $"WPF shader sampler register must be between 0 and {MaxSamplerRegisterCount - 1}.");
        }
    }

    private static ulong ComputeStableHash(string value)
    {
        const ulong fnvOffsetBasis = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;

        var hash = fnvOffsetBasis;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= fnvPrime;
        }

        return hash;
    }
}

public sealed class WpfShaderEffectSampler
{
    private int _registerIndex;

    public WpfShaderEffectSampler()
    {
    }

    public WpfShaderEffectSampler(
        int registerIndex,
        GpuTexture? texture,
        TextureSamplingMode samplingMode = TextureSamplingMode.Linear)
    {
        RegisterIndex = registerIndex;
        Texture = texture;
        SamplingMode = samplingMode;
    }

    public int RegisterIndex
    {
        get => _registerIndex;
        set
        {
            WpfShaderEffectParams.ValidateSamplerRegister(value);
            _registerIndex = value;
        }
    }

    public GpuTexture? Texture { get; set; }
    public TextureSamplingMode SamplingMode { get; set; } = TextureSamplingMode.Linear;
}

public static class WpfShaderEffectShaders
{
    public const string PassThrough = @"
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    return inputColor;
}
";
}
