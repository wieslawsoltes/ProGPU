namespace ProGPU.Media.Audio;

using ProGPU.Media.Effects;

/// <summary>
/// Provider-neutral native audio graph node kinds. Values are stable because
/// browser and native providers consume them through typed interop.
/// </summary>
public enum MediaAudioGraphEffectKind
{
    Gain = 1,
    StereoBalance = 2
}

/// <summary>
/// Allocation-free snapshot of a portable native audio graph node. Parameter
/// meanings are defined by <see cref="Kind"/>. Gain uses Parameter0 as a
/// nonnegative linear amplitude multiplier. StereoBalance uses Parameter0
/// in the WinUI-aligned inclusive range [-1, 1].
/// </summary>
public readonly record struct MediaAudioGraphEffectState
{
    public MediaAudioGraphEffectState(
        MediaAudioGraphEffectKind kind,
        float parameter0 = 0f,
        float parameter1 = 0f,
        float parameter2 = 0f,
        float parameter3 = 0f)
    {
        if (!float.IsFinite(parameter0) ||
            !float.IsFinite(parameter1) ||
            !float.IsFinite(parameter2) ||
            !float.IsFinite(parameter3))
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameter0),
                "Audio graph parameters must be finite.");
        }
        if (kind == MediaAudioGraphEffectKind.Gain &&
            parameter0 < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameter0));
        }
        if (kind ==
                MediaAudioGraphEffectKind.StereoBalance &&
            parameter0 is < -1f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameter0));
        }

        Kind = kind;
        Parameter0 = parameter0;
        Parameter1 = parameter1;
        Parameter2 = parameter2;
        Parameter3 = parameter3;
    }

    public MediaAudioGraphEffectKind Kind { get; }

    public float Parameter0 { get; }

    public float Parameter1 { get; }

    public float Parameter2 { get; }

    public float Parameter3 { get; }
}

/// <summary>
/// Allocation-free left/right linear levels used to fold portable graph
/// nodes into native player controls. Sequential gain and balance nodes are
/// multiplied in declaration order. Work and storage are O(1) per node.
/// </summary>
public readonly record struct MediaAudioStereoLevels
{
    public MediaAudioStereoLevels(
        float left,
        float right)
    {
        if (!float.IsFinite(left) || left < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(left));
        }
        if (!float.IsFinite(right) || right < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(right));
        }
        Left = left;
        Right = right;
    }

    public static MediaAudioStereoLevels Identity =>
        new(1f, 1f);

    public static MediaAudioStereoLevels FromBalance(
        float balance)
    {
        if (!float.IsFinite(balance) ||
            balance is < -1f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(balance));
        }
        return balance < 0f
            ? new MediaAudioStereoLevels(
                1f,
                1f + balance)
            : new MediaAudioStereoLevels(
                1f - balance,
                1f);
    }

    public float Left { get; }

    public float Right { get; }

    public float Peak => Math.Max(Left, Right);

    /// <summary>
    /// Returns the equivalent WinUI MediaPlayer.AudioBalance value after
    /// removing the common peak gain.
    /// </summary>
    public float Balance =>
        Left == Right ||
        Peak == 0f
            ? 0f
            : Left > Right
                ? Right / Left - 1f
                : 1f - Left / Right;

    public MediaAudioStereoLevels Apply(
        in MediaAudioGraphEffectState state)
    {
        return state.Kind switch
        {
            MediaAudioGraphEffectKind.Gain =>
                Scale(state.Parameter0),
            MediaAudioGraphEffectKind.StereoBalance =>
                Multiply(
                    FromBalance(
                        state.Parameter0)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(state))
        };
    }

    public MediaAudioStereoLevels Scale(float gain)
    {
        if (!float.IsFinite(gain) || gain < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gain));
        }
        return new MediaAudioStereoLevels(
            SaturatingMultiply(Left, gain),
            SaturatingMultiply(Right, gain));
    }

    public MediaAudioStereoLevels Multiply(
        in MediaAudioStereoLevels other) =>
        new(
            SaturatingMultiply(Left, other.Left),
            SaturatingMultiply(Right, other.Right));

    private static float SaturatingMultiply(
        float left,
        float right)
    {
        float value = left * right;
        return float.IsFinite(value)
            ? value
            : float.MaxValue;
    }
}

/// <summary>
/// An audio effect that can execute either in ProGPU's typed PCM callback
/// chain or as an equivalent platform-native audio graph node. Changes are
/// raised only from configuration threads; providers snapshot state before
/// crossing into their native graph.
/// </summary>
public interface IMediaAudioGraphEffect :
    IMediaAudioEffect
{
    event Action? StateChanged;

    MediaAudioGraphEffectState CaptureState();
}

/// <summary>
/// Typed factory for a live, allocation-free gain processor. Created effects
/// share the factory's atomic gain state, allowing UI controls to update an
/// installed native or PCM effect without removing the effect graph.
/// </summary>
public sealed class MediaAudioGainEffectFactory :
    IMediaEffectFactory
{
    public const string GainPropertyName = "Gain";

    private readonly SharedGainState _state = new();

    public MediaAudioGainEffectFactory(
        string activatableClassId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            activatableClassId);
        ActivatableClassId = activatableClassId;
    }

    public string ActivatableClassId { get; }

    public float Gain
    {
        get => _state.Gain;
        set => _state.Gain = value;
    }

    public IMediaEffect Create(
        in MediaEffectDescriptor descriptor)
    {
        if (descriptor.Kind != MediaEffectKind.Audio)
        {
            throw new ArgumentException(
                "The gain effect can be activated only as an audio effect.",
                nameof(descriptor));
        }
        SharedGainState state = _state;
        if (descriptor.Properties.TryGetValue(
                GainPropertyName,
                out object? value))
        {
            state = new SharedGainState
            {
                Gain = ReadGain(value)
            };
        }
        return new MediaAudioGainEffect(
            ActivatableClassId,
            state);
    }

    private static float ReadGain(object? value)
    {
        float gain = value switch
        {
            byte number => number,
            sbyte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            float number => number,
            double number => checked((float)number),
            decimal number => checked((float)number),
            _ => throw new ArgumentException(
                $"'{GainPropertyName}' must be a numeric value.")
        };
        if (!float.IsFinite(gain) || gain < 0f)
        {
            throw new ArgumentOutOfRangeException(
                GainPropertyName);
        }
        return gain;
    }

    private sealed class SharedGainState
    {
        private float _gain = 1f;

        public event Action? Changed;

        public float Gain
        {
            get => Volatile.Read(ref _gain);
            set
            {
                if (!float.IsFinite(value) ||
                    value < 0f)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value));
                }
                float previous =
                    Interlocked.Exchange(
                        ref _gain,
                        value);
                if (previous != value)
                {
                    Changed?.Invoke();
                }
            }
        }
    }

    private sealed class MediaAudioGainEffect :
        IMediaAudioGraphEffect
    {
        private readonly SharedGainState _state;

        public MediaAudioGainEffect(
            string id,
            SharedGainState state)
        {
            Id = id;
            _state = state;
        }

        public string Id { get; }

        public MediaEffectKind Kind =>
            MediaEffectKind.Audio;

        public event Action? StateChanged
        {
            add => _state.Changed += value;
            remove => _state.Changed -= value;
        }

        public MediaAudioGraphEffectState
            CaptureState() =>
            new(
                MediaAudioGraphEffectKind.Gain,
                _state.Gain);

        public void Process(
            Span<float> interleavedSamples,
            in MediaAudioProcessContext context)
        {
            float gain = _state.Gain;
            if (gain == 1f)
            {
                return;
            }

            int sampleCount = checked(
                context.FrameCount *
                context.Format.ChannelCount);
            Span<float> samples =
                interleavedSamples[..sampleCount];
            for (int index = 0;
                 index < samples.Length;
                 index++)
            {
                samples[index] *= gain;
            }
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Typed factory for a live stereo-balance node. It follows WinUI
/// MediaPlayer.AudioBalance semantics and runs either as native left/right
/// levels or as an allocation-free in-place float PCM processor.
/// </summary>
public sealed class MediaAudioStereoBalanceEffectFactory :
    IMediaEffectFactory
{
    public const string BalancePropertyName =
        "AudioBalance";

    private readonly SharedBalanceState _state = new();

    public MediaAudioStereoBalanceEffectFactory(
        string activatableClassId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            activatableClassId);
        ActivatableClassId = activatableClassId;
    }

    public string ActivatableClassId { get; }

    public float Balance
    {
        get => _state.Balance;
        set => _state.Balance = value;
    }

    public IMediaEffect Create(
        in MediaEffectDescriptor descriptor)
    {
        if (descriptor.Kind != MediaEffectKind.Audio)
        {
            throw new ArgumentException(
                "The stereo-balance effect can be activated only as an audio effect.",
                nameof(descriptor));
        }
        SharedBalanceState state = _state;
        if (descriptor.Properties.TryGetValue(
                BalancePropertyName,
                out object? value))
        {
            state = new SharedBalanceState
            {
                Balance = ReadBalance(value)
            };
        }
        return new MediaAudioStereoBalanceEffect(
            ActivatableClassId,
            state);
    }

    private static float ReadBalance(object? value)
    {
        float balance = value switch
        {
            byte number => number,
            sbyte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            float number => number,
            double number => checked((float)number),
            decimal number => checked((float)number),
            _ => throw new ArgumentException(
                $"'{BalancePropertyName}' must be a numeric value.")
        };
        if (!float.IsFinite(balance) ||
            balance is < -1f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                BalancePropertyName);
        }
        return balance;
    }

    private sealed class SharedBalanceState
    {
        private float _balance;

        public event Action? Changed;

        public float Balance
        {
            get => Volatile.Read(ref _balance);
            set
            {
                if (!float.IsFinite(value) ||
                    value is < -1f or > 1f)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value));
                }
                float previous =
                    Interlocked.Exchange(
                        ref _balance,
                        value);
                if (previous != value)
                {
                    Changed?.Invoke();
                }
            }
        }
    }

    private sealed class MediaAudioStereoBalanceEffect :
        IMediaAudioGraphEffect
    {
        private readonly SharedBalanceState _state;

        public MediaAudioStereoBalanceEffect(
            string id,
            SharedBalanceState state)
        {
            Id = id;
            _state = state;
        }

        public string Id { get; }

        public MediaEffectKind Kind =>
            MediaEffectKind.Audio;

        public event Action? StateChanged
        {
            add => _state.Changed += value;
            remove => _state.Changed -= value;
        }

        public MediaAudioGraphEffectState
            CaptureState() =>
            new(
                MediaAudioGraphEffectKind
                    .StereoBalance,
                _state.Balance);

        public void Process(
            Span<float> interleavedSamples,
            in MediaAudioProcessContext context)
        {
            if (context.Format.ChannelCount < 2)
            {
                return;
            }

            MediaAudioStereoLevels levels =
                MediaAudioStereoLevels.FromBalance(
                    _state.Balance);
            if (levels == MediaAudioStereoLevels.Identity)
            {
                return;
            }

            int sampleCount = checked(
                context.FrameCount *
                context.Format.ChannelCount);
            Span<float> samples =
                interleavedSamples[..sampleCount];
            int channels = context.Format.ChannelCount;
            for (int frame = 0;
                 frame < context.FrameCount;
                 frame++)
            {
                int offset = frame * channels;
                samples[offset] *= levels.Left;
                samples[offset + 1] *= levels.Right;
            }
        }

        public void Dispose()
        {
        }
    }
}
