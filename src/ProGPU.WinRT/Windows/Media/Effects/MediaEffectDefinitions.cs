using Windows.Foundation.Collections;

namespace Windows.Media.Effects;

public interface IAudioEffectDefinition
{
    string ActivatableClassId { get; }
    IPropertySet Properties { get; }
}

public interface IVideoEffectDefinition
{
    string ActivatableClassId { get; }
    IPropertySet Properties { get; }
}

public interface IVideoCompositorDefinition
{
    string ActivatableClassId { get; }
    IPropertySet Properties { get; }
}

public sealed class AudioEffectDefinition :
    IAudioEffectDefinition
{
    public AudioEffectDefinition(
        string activatableClassId)
        : this(
            activatableClassId,
            new PropertySet())
    {
    }

    public AudioEffectDefinition(
        string activatableClassId,
        IPropertySet properties)
    {
        if (string.IsNullOrWhiteSpace(
                activatableClassId))
        {
            throw new ArgumentException(
                "An audio effect class ID is required.",
                nameof(activatableClassId));
        }
        ActivatableClassId = activatableClassId;
        Properties =
            properties ??
            throw new ArgumentNullException(
                nameof(properties));
    }

    public string ActivatableClassId { get; }
    public IPropertySet Properties { get; }
}

public sealed class VideoEffectDefinition :
    IVideoEffectDefinition
{
    public VideoEffectDefinition(
        string activatableClassId)
        : this(
            activatableClassId,
            new PropertySet())
    {
    }

    public VideoEffectDefinition(
        string activatableClassId,
        IPropertySet properties)
    {
        if (string.IsNullOrWhiteSpace(
                activatableClassId))
        {
            throw new ArgumentException(
                "A video effect class ID is required.",
                nameof(activatableClassId));
        }
        ActivatableClassId = activatableClassId;
        Properties =
            properties ??
            throw new ArgumentNullException(
                nameof(properties));
    }

    public string ActivatableClassId { get; }
    public IPropertySet Properties { get; }
}

public sealed class VideoCompositorDefinition :
    IVideoCompositorDefinition
{
    public VideoCompositorDefinition(
        string activatableClassId)
        : this(
            activatableClassId,
            new PropertySet())
    {
    }

    public VideoCompositorDefinition(
        string activatableClassId,
        IPropertySet properties)
    {
        if (string.IsNullOrWhiteSpace(
                activatableClassId))
        {
            throw new ArgumentException(
                "A video compositor class ID is required.",
                nameof(activatableClassId));
        }
        ActivatableClassId = activatableClassId;
        Properties =
            properties ??
            throw new ArgumentNullException(
                nameof(properties));
    }

    public string ActivatableClassId { get; }
    public IPropertySet Properties { get; }
}
