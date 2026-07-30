namespace ProGPU.WinUI.Platform;

/// <summary>
/// Optional extension for a XAML platform-resource provider that can identify
/// the active operating-system contrast scheme.
/// </summary>
public interface IHighContrastSchemeProvider
{
    string HighContrastScheme { get; }
}
