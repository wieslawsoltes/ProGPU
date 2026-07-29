namespace ProGPU.Media.Audio;

using ProGPU.Media.Effects;

/// <summary>
/// Provider-neutral native audio graph node kinds. Values are stable because
/// browser and native providers consume them through typed interop.
/// </summary>
public enum MediaAudioGraphEffectKind
{
    Gain = 1
}

/// <summary>
/// Allocation-free snapshot of a portable native audio graph node. Parameter
/// meanings are defined by <see cref="Kind"/>. Gain uses Parameter0 as a
/// nonnegative linear amplitude multiplier.
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
