using ProGPU.Text;
using System.Runtime.InteropServices;

namespace System.Drawing.Text;

/// <summary>
/// Owns portable fonts loaded from files or caller memory.
/// </summary>
public sealed class PrivateFontCollection : FontCollection
{
    private sealed class PrivateFamily
    {
        internal required string Name { get; init; }
        internal List<TtfFont> Faces { get; } = [];
    }

    private const int MaximumCollectionFaces = 1024;
    private readonly object _sync = new();
    private readonly Dictionary<string, PrivateFamily> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedFiles = new(StringComparer.OrdinalIgnoreCase);

    public PrivateFontCollection()
        : base(remainsUsableAfterDispose: false)
    {
    }

    public void AddFontFile(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ThrowIfDisposed();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filename);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new FileNotFoundException("The font file was not found.", filename, exception);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The font file was not found.", filename);
        }

        lock (_sync)
        {
            if (!_loadedFiles.Add(fullPath))
            {
                return;
            }
        }

        try
        {
            AddOwnedData(File.ReadAllBytes(fullPath));
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            // GDI+ accepts non-font files without adding a family. Preserve that
            // observable collection behavior while keeping the parser typed.
        }
        catch
        {
            lock (_sync)
            {
                _loadedFiles.Remove(fullPath);
            }

            throw;
        }
    }

    public void AddMemoryFont(IntPtr memory, int length)
    {
        ThrowIfDisposed();
        if (memory == IntPtr.Zero || length <= 0)
        {
            throw new ArgumentException("The memory address and length must describe a font buffer.");
        }

        var owned = new byte[length];
        Marshal.Copy(memory, owned, 0, length);
        AddOwnedData(owned);
    }

    internal override bool TryResolveFamilyCore(string name, out FontFamilySource? source)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (_families.TryGetValue(name, out PrivateFamily? family))
            {
                source = new FontFamilySource(family.Name, family.Faces.ToArray());
                return true;
            }
        }

        source = null;
        return false;
    }

    private protected override FontFamily[] GetFamiliesCore()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            return _families.Values
                .OrderBy(static family => family.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static family => new FontFamily(new FontFamilySource(family.Name, family.Faces.ToArray())))
                .ToArray();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        lock (_sync)
        {
            _families.Clear();
            _loadedFiles.Clear();
        }
    }

    private void AddOwnedData(byte[] data)
    {
        for (int faceIndex = 0; faceIndex < MaximumCollectionFaces; faceIndex++)
        {
            TtfFont face;
            try
            {
                face = new TtfFont(data, faceIndex);
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException or ArgumentOutOfRangeException)
            {
                if (faceIndex == 0)
                {
                    throw new InvalidDataException("The buffer does not contain a supported OpenType font.", exception);
                }

                break;
            }

            string familyName = !string.IsNullOrWhiteSpace(face.FamilyName)
                ? face.FamilyName
                : face.FullName;
            if (string.IsNullOrWhiteSpace(familyName))
            {
                continue;
            }

            lock (_sync)
            {
                if (!_families.TryGetValue(familyName, out PrivateFamily? family))
                {
                    family = new PrivateFamily { Name = familyName };
                    _families.Add(familyName, family);
                }

                FontStyleRequest style = FontStyleRequest.FromFont(face);
                if (!family.Faces.Any(existing => FontStyleRequest.FromFont(existing).Equals(style)))
                {
                    family.Faces.Add(face);
                }
            }

            if (faceIndex == 0 && !LooksLikeCollection(data))
            {
                break;
            }
        }
    }

    private static bool LooksLikeCollection(byte[] data) =>
        data.Length >= 4 && data[0] == (byte)'t' && data[1] == (byte)'t' && data[2] == (byte)'c' && data[3] == (byte)'f';

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
        {
            throw new ArgumentException("Parameter is not valid.");
        }
    }
}
