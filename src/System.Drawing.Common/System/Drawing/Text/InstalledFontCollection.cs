using ProGPU.Text;

namespace System.Drawing.Text;

/// <summary>
/// Represents the font families visible through the ProGPU platform catalog.
/// </summary>
public sealed class InstalledFontCollection : FontCollection
{
    public InstalledFontCollection()
        : base(remainsUsableAfterDispose: true)
    {
    }

    internal override bool TryResolveFamilyCore(string name, out FontFamilySource? source)
    {
        IReadOnlyList<string> names = FontApi.Manager.FontFamilies;
        for (int index = 0; index < names.Count; index++)
        {
            if (names[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                source = new FontFamilySource(names[index]);
                return true;
            }
        }

        source = null;
        return false;
    }

    private protected override FontFamily[] GetFamiliesCore() => FontFamily.Families;
}
