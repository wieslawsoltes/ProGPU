#nullable disable

namespace SkiaSharp;

/// <summary>
/// Provides the native-compatibility version represented by this clean-room
/// SkiaSharp surface.
/// </summary>
public static class SkiaSharpVersion
{
    private static readonly Version s_nativeVersion = new(151, 0);

    /// <summary>
    /// Gets the implemented native compatibility level.
    /// </summary>
    public static Version Native => s_nativeVersion;

    /// <summary>
    /// Gets the minimum native compatibility level required by this assembly.
    /// </summary>
    public static Version NativeMinimum => s_nativeVersion;

    /// <summary>
    /// Checks whether the active implementation satisfies the managed
    /// compatibility contract.
    /// </summary>
    public static bool CheckNativeLibraryCompatible(bool throwIfIncompatible = false) => true;
}
