using System.Threading;

namespace System.Drawing;

/// <summary>
/// Provides lazy, process-wide resources for the standard known-color properties.
/// Lookup is O(1), initializes at most one retained resource per color and kind,
/// and performs no allocation after a property has been warmed.
/// </summary>
internal static class KnownColorResources
{
    // KnownColor is a compact framework enum. Leave spare capacity so adding a new
    // contract value does not change the cache algorithm or require a dictionary.
    private const int CacheCapacity = 256;
    private static readonly Brush?[] s_brushes = new Brush[CacheCapacity];
    private static readonly Pen?[] s_pens = new Pen[CacheCapacity];

    public static Brush GetBrush(KnownColor knownColor)
    {
        int index = GetIndex(knownColor);
        Brush? current = Volatile.Read(ref s_brushes[index]);
        if (current is not null)
        {
            return current;
        }

        var created = new SolidBrush(Color.FromKnownColor(knownColor), immutable: true);
        return Interlocked.CompareExchange(ref s_brushes[index], created, null) ?? created;
    }

    public static Pen GetPen(KnownColor knownColor)
    {
        int index = GetIndex(knownColor);
        Pen? current = Volatile.Read(ref s_pens[index]);
        if (current is not null)
        {
            return current;
        }

        var created = new Pen(Color.FromKnownColor(knownColor), immutable: true);
        return Interlocked.CompareExchange(ref s_pens[index], created, null) ?? created;
    }

    private static int GetIndex(KnownColor knownColor)
    {
        int index = (int)knownColor;
        if ((uint)index >= CacheCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(knownColor));
        }

        return index;
    }
}
