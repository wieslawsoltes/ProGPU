using System.Runtime.InteropServices.JavaScript;

namespace ProGPU.Browser;

public static partial class BrowserGpuRuntime
{
    private static readonly object TextureLimitsGate = new();
    private static object? _textureLimitsInitialization;
    private static double _textureLimitsGeneration;

    private static object BeginTextureLimitsInitialization()
    {
        lock (TextureLimitsGate)
        {
            _textureLimitsGeneration = 0;
            return _textureLimitsInitialization = new object();
        }
    }

    private static void CompleteTextureLimitsInitialization(object initialization, bool supported)
    {
        lock (TextureLimitsGate)
        {
            // A late/canceled predecessor cannot grant access to the new
            // runtime device, even if its public capability reply was valid.
            if (!ReferenceEquals(initialization, _textureLimitsInitialization) || !supported) return;
            double generation;
            try { generation = GetTextureLimitsGenerationCore(); }
            catch (JSException) { return; }
            if (double.IsFinite(generation) && generation > 0 &&
                generation <= 9007199254740991d && Math.Truncate(generation) == generation)
                _textureLimitsGeneration = generation;
        }
    }

    internal static double CaptureTextureLimitsGeneration()
    {
        if (!OperatingSystem.IsBrowser()) return 0;
        lock (TextureLimitsGate) return _textureLimitsGeneration;
    }

    internal static bool TryGetTextureDimension2D(double generation, out uint maximum)
    {
        maximum = 0;
        if (!OperatingSystem.IsBrowser() || generation == 0) return false;
        lock (TextureLimitsGate)
        {
            if (generation != _textureLimitsGeneration) return false;
            double value;
            try { value = GetTextureDimension2DCore(generation); }
            catch (JSException) { return false; }
            if (generation != _textureLimitsGeneration || !double.IsFinite(value) ||
                value <= 0 || value > uint.MaxValue || Math.Truncate(value) != value)
                return false;
            maximum = (uint)value;
            return true;
        }
    }

    [JSImport("getTextureLimitsGeneration", "progpu-browser")]
    private static partial double GetTextureLimitsGenerationCore();

    [JSImport("getTextureDimension2D", "progpu-browser")]
    private static partial double GetTextureDimension2DCore(double generation);
}
