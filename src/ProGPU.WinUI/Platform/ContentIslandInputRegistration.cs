using Microsoft.UI.Content;
using Microsoft.UI.Xaml.Input;

namespace ProGPU.WinUI.Platform;

public interface IContentIslandFocusProvider
{
    bool TrySetFocus(
        ContentIsland island,
        WindowInputState state);
}

public interface IContentIslandSiteProvider
{
    ContentIsland? ContentIsland { get; }
}

public static class ContentIslandInputRegistration
{
    public static void Attach(
        ContentIsland island,
        WindowInputState state,
        IContentIslandFocusProvider? focusProvider = null)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(state);
        island.AttachInputState(state);
        state.ContentIslandFocusProvider = focusProvider;
    }
}
