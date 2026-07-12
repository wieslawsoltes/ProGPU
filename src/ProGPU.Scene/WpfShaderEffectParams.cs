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
    internal const int SourceTextureRegisterMetadataIndex = ConstantFloatCount + 8;
    internal const int CanvasWidthMetadataIndex = ConstantFloatCount + 9;
    internal const int CanvasHeightMetadataIndex = ConstantFloatCount + 10;
    internal const int HasMaskMetadataIndex = ConstantFloatCount + 11;

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

    internal string GetStableShaderSourceKey()
    {
        return ComputeStableHash(GetShaderSourceOrDefault()).ToString("x16");
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
        destination[SourceTextureRegisterMetadataIndex] = SourceTextureRegisterIndex;
    }

    public bool HasAnyTexture()
    {
        if (Texture != null)
        {
            return true;
        }

        var samplers = Samplers;
        for (var i = 0; i < samplers.Length; i++)
        {
            var sampler = samplers[i];
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

        var samplers = Samplers;
        for (var i = 0; i < samplers.Length; i++)
        {
            var sampler = samplers[i];
            if (sampler.RegisterIndex == registerIndex && sampler.Texture != null)
            {
                texture = sampler.Texture;
                samplingMode = sampler.SamplingMode;
                return true;
            }
        }

        if (registerIndex == SourceTextureRegisterIndex && Texture != null)
        {
            texture = Texture;
            samplingMode = SamplingMode;
            return true;
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
        if (TryGetSampler(SourceTextureRegisterIndex, out texture, out _))
        {
            return true;
        }

        if (SourceTextureRegisterIndex != 0 && TryGetSampler(0, out texture, out _))
        {
            return true;
        }

        var samplers = Samplers;
        for (var i = 0; i < samplers.Length; i++)
        {
            var sampler = samplers[i];
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

    internal void AddRenderCacheKey(ref HashCode hash)
    {
        hash.Add(GetStableShaderKey());
        hash.Add(GetShaderSourceOrDefault());
        hash.Add(SamplingMode);
        hash.Add(IsFailed);
        hash.Add(LastError);
        hash.Add(SourceTextureRegisterIndex);
        hash.Add(Texture?.Id ?? 0UL);
        hash.Add(Texture?.Generation ?? 0u);
        hash.Add(Rect.X);
        hash.Add(Rect.Y);
        hash.Add(Rect.Width);
        hash.Add(Rect.Height);

        hash.Add(Constants.Length);
        for (int i = 0; i < Constants.Length; i++)
        {
            hash.Add(Constants[i]);
        }

        hash.Add(Samplers.Length);
        for (int i = 0; i < Samplers.Length; i++)
        {
            var sampler = Samplers[i];
            hash.Add(sampler.RegisterIndex);
            hash.Add(sampler.SamplingMode);
            hash.Add(sampler.Texture?.Id ?? 0UL);
            hash.Add(sampler.Texture?.Generation ?? 0u);
        }
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
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
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
    public static readonly string PassThrough = ShaderResource.Load(typeof(WpfShaderEffectShaders), "WpfEffectPassThrough.wgsl");
}
