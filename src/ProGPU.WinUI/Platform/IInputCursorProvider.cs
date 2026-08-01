using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;

namespace ProGPU.WinUI.Platform;

public interface IInputCursorProvider
{
    void SetCursor(InputCursor? cursor);
}

public static class InputCursorProviderRegistration
{
    public static void SetProvider(
        WindowInputState state,
        IInputCursorProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.InputCursorProvider = provider;
        provider?.SetCursor(state.ActiveInputCursor);
    }
}
