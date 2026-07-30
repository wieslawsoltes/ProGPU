using Microsoft.UI.Xaml;

namespace ProGPU.WinUI.Platform;

public interface IWindowActivationHost
{
    void Activate(Window window, bool activateWindow);
}
