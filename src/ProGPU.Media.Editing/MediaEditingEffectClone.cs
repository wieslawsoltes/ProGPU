using Windows.Foundation.Collections;
using Windows.Media.Effects;

namespace Windows.Media.Editing;

internal static class MediaEditingEffectClone
{
    public static IAudioEffectDefinition Clone(
        IAudioEffectDefinition source) =>
        new AudioEffectDefinition(
            source.ActivatableClassId,
            Copy(source.Properties));

    public static IVideoEffectDefinition Clone(
        IVideoEffectDefinition source) =>
        new VideoEffectDefinition(
            source.ActivatableClassId,
            Copy(source.Properties));

    public static IVideoCompositorDefinition Clone(
        IVideoCompositorDefinition source) =>
        new VideoCompositorDefinition(
            source.ActivatableClassId,
            Copy(source.Properties));

    private static PropertySet Copy(
        IPropertySet source)
    {
        var result = new PropertySet();
        foreach ((string key, object? value) in source)
        {
            result.Add(key, value);
        }
        return result;
    }
}
