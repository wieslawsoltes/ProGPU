using System.Text.Json;
using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Browser;

/// <summary>
/// Immutable configuration for an application-supplied browser
/// <c>AudioWorkletProcessor</c>.
/// </summary>
/// <remarks>
/// The module is loaded once per browser audio context and the node options
/// are structured-cloned by the browser into the audio rendering realm.
/// Validation is O(J) time for J JSON bytes, uses O(J) parser storage during
/// configuration, and never runs in an audio callback.
/// </remarks>
public readonly record struct BrowserAudioWorkletEffectState
{
    public const int MaximumNodeOptionsJsonLength = 64 * 1024;

    public BrowserAudioWorkletEffectState(
        string moduleUri,
        string processorName,
        string nodeOptionsJson = "{}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(processorName);
        ArgumentNullException.ThrowIfNull(nodeOptionsJson);
        if (!Uri.TryCreate(
                moduleUri,
                UriKind.RelativeOrAbsolute,
                out _) ||
            moduleUri.AsSpan().Trim().Length !=
                moduleUri.Length)
        {
            throw new ArgumentException(
                "The AudioWorklet module URI must be a valid relative or absolute URI without surrounding whitespace.",
                nameof(moduleUri));
        }
        if (processorName.AsSpan().Trim().Length !=
                processorName.Length)
        {
            throw new ArgumentException(
                "The AudioWorklet processor name cannot contain surrounding whitespace.",
                nameof(processorName));
        }
        if (nodeOptionsJson.Length >
            MaximumNodeOptionsJsonLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nodeOptionsJson),
                $"AudioWorklet node options cannot exceed {MaximumNodeOptionsJsonLength} UTF-16 code units.");
        }

        using JsonDocument document =
            JsonDocument.Parse(nodeOptionsJson);
        if (document.RootElement.ValueKind !=
            JsonValueKind.Object)
        {
            throw new ArgumentException(
                "AudioWorklet node options must be a JSON object.",
                nameof(nodeOptionsJson));
        }

        ModuleUri = moduleUri;
        ProcessorName = processorName;
        NodeOptionsJson = nodeOptionsJson;
    }

    public string ModuleUri { get; }

    public string ProcessorName { get; }

    public string NodeOptionsJson { get; }
}

/// <summary>
/// Browser-specific typed media effect whose processing is implemented by an
/// application-owned <c>AudioWorkletProcessor</c> module.
/// </summary>
/// <remarks>
/// The interface deliberately does not expose a managed PCM callback. Browser
/// providers snapshot the state on a configuration thread, load the declared
/// module through <c>audioWorklet.addModule()</c>, and keep sample processing
/// in the browser audio rendering realm.
/// </remarks>
public interface IBrowserAudioWorkletEffect :
    IMediaEffect
{
    event Action? StateChanged;

    BrowserAudioWorkletEffectState CaptureState();
}

/// <summary>
/// Typed registry factory for immutable browser AudioWorklet definitions.
/// Properties supplied by a WinUI-aligned audio-effect definition can
/// override the configured module URI, processor name, and node-options JSON.
/// </summary>
public sealed class BrowserAudioWorkletEffectFactory :
    IMediaEffectFactory
{
    public const string ModuleUriPropertyName =
        "ModuleUri";
    public const string ProcessorNamePropertyName =
        "ProcessorName";
    public const string NodeOptionsJsonPropertyName =
        "NodeOptionsJson";

    private readonly BrowserAudioWorkletEffectState
        _defaultState;

    public BrowserAudioWorkletEffectFactory(
        string activatableClassId,
        string moduleUri,
        string processorName,
        string nodeOptionsJson = "{}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            activatableClassId);
        ActivatableClassId = activatableClassId;
        _defaultState =
            new BrowserAudioWorkletEffectState(
                moduleUri,
                processorName,
                nodeOptionsJson);
    }

    public string ActivatableClassId { get; }

    public IMediaEffect Create(
        in MediaEffectDescriptor descriptor)
    {
        if (descriptor.Kind != MediaEffectKind.Audio)
        {
            throw new ArgumentException(
                "The AudioWorklet effect can be activated only as an audio effect.",
                nameof(descriptor));
        }

        string moduleUri = GetString(
            descriptor.Properties,
            ModuleUriPropertyName,
            _defaultState.ModuleUri);
        string processorName = GetString(
            descriptor.Properties,
            ProcessorNamePropertyName,
            _defaultState.ProcessorName);
        string nodeOptionsJson = GetString(
            descriptor.Properties,
            NodeOptionsJsonPropertyName,
            _defaultState.NodeOptionsJson);
        return new Effect(
            ActivatableClassId,
            new BrowserAudioWorkletEffectState(
                moduleUri,
                processorName,
                nodeOptionsJson));
    }

    private static string GetString(
        IReadOnlyDictionary<string, object?>
            properties,
        string name,
        string fallback)
    {
        if (!properties.TryGetValue(
                name,
                out object? value))
        {
            return fallback;
        }
        return value is string text
            ? text
            : throw new ArgumentException(
                $"'{name}' must be a string value.");
    }

    private sealed class Effect :
        IBrowserAudioWorkletEffect
    {
        private readonly BrowserAudioWorkletEffectState
            _state;

        public Effect(
            string id,
            in BrowserAudioWorkletEffectState state)
        {
            Id = id;
            _state = state;
        }

        public string Id { get; }

        public MediaEffectKind Kind =>
            MediaEffectKind.Audio;

        public event Action? StateChanged
        {
            add
            {
            }
            remove
            {
            }
        }

        public BrowserAudioWorkletEffectState
            CaptureState() => _state;

        public void Dispose()
        {
        }
    }
}

internal enum BrowserAudioEffectNodeKind
{
    NativeGraph = 1,
    AudioWorklet = 2
}

internal readonly record struct BrowserAudioEffectNodeState
{
    private BrowserAudioEffectNodeState(
        BrowserAudioEffectNodeKind kind,
        ProGPU.Media.Audio.MediaAudioGraphEffectState
            nativeState,
        BrowserAudioWorkletEffectState workletState)
    {
        Kind = kind;
        NativeState = nativeState;
        WorkletState = workletState;
    }

    public BrowserAudioEffectNodeKind Kind { get; }

    public ProGPU.Media.Audio.MediaAudioGraphEffectState
        NativeState { get; }

    public BrowserAudioWorkletEffectState
        WorkletState { get; }

    public static BrowserAudioEffectNodeState Native(
        in ProGPU.Media.Audio.MediaAudioGraphEffectState
            state) =>
        new(
            BrowserAudioEffectNodeKind.NativeGraph,
            state,
            default);

    public static BrowserAudioEffectNodeState Worklet(
        in BrowserAudioWorkletEffectState state) =>
        new(
            BrowserAudioEffectNodeKind.AudioWorklet,
            default,
            state);
}

/// <summary>
/// Activates a serialized WinUI-aligned audio-effect list and captures the
/// browser-native node descriptors needed to build its Web Audio graph.
/// </summary>
/// <remarks>
/// Configuration is O(E + J) time and O(E) retained state for E effects and
/// J validated AudioWorklet JSON bytes. Effect activation and disposal happen
/// before browser graph construction; no managed effect instance reaches the
/// audio rendering realm.
/// </remarks>
internal static class BrowserAudioEffectResolver
{
    public static bool TryCaptureGraph(
        MediaEffectRegistry registry,
        IReadOnlyList<
            MediaCompositionEffectDefinition>
            definitions,
        out BrowserAudioEffectNodeState[] states)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definitions);

        states = definitions.Count == 0
            ? []
            : new BrowserAudioEffectNodeState[
                definitions.Count];
        for (int index = 0;
             index < definitions.Count;
             index++)
        {
            MediaCompositionEffectDefinition
                definition = definitions[index];
            var descriptor =
                new MediaEffectDescriptor(
                    definition.ActivatableClassId,
                    MediaEffectKind.Audio,
                    definition.Properties);
            IMediaEffect? effect = null;
            try
            {
                if (!registry.TryCreate(
                        descriptor,
                        out effect) ||
                    effect is null ||
                    effect.Kind !=
                        MediaEffectKind.Audio)
                {
                    states = [];
                    return false;
                }

                if (effect is
                    IMediaAudioGraphEffect graphEffect)
                {
                    MediaAudioGraphEffectState state =
                        graphEffect.CaptureState();
                    if (state.Kind is not (
                            MediaAudioGraphEffectKind.Gain or
                            MediaAudioGraphEffectKind
                                .StereoBalance))
                    {
                        states = [];
                        return false;
                    }
                    states[index] =
                        BrowserAudioEffectNodeState
                            .Native(state);
                }
                else if (effect is
                         IBrowserAudioWorkletEffect
                             workletEffect)
                {
                    BrowserAudioWorkletEffectState
                        captured =
                            workletEffect.CaptureState();
                    var state =
                        new BrowserAudioWorkletEffectState(
                            captured.ModuleUri,
                            captured.ProcessorName,
                            captured.NodeOptionsJson);
                    states[index] =
                        BrowserAudioEffectNodeState
                            .Worklet(state);
                }
                else
                {
                    states = [];
                    return false;
                }
            }
            catch
            {
                states = [];
                return false;
            }
            finally
            {
                effect?.Dispose();
            }
        }
        return true;
    }
}
