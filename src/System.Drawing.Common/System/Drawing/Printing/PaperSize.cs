using System.Globalization;

namespace System.Drawing.Printing;

/// <summary>
/// Specifies a paper size in hundredths of an inch.
/// </summary>
public class PaperSize
{
    private PaperKind _kind;
    private string _name;
    private int _width;
    private int _height;
    private readonly bool _createdByDefaultConstructor;

    public PaperSize()
    {
        _kind = PaperKind.Custom;
        _name = string.Empty;
        _createdByDefaultConstructor = true;
    }

    public PaperSize(string name, int width, int height)
    {
        _kind = PaperKind.Custom;
        _name = name;
        _width = width;
        _height = height;
    }

    internal PaperSize(PaperKind kind, string name, int width, int height)
    {
        _kind = kind;
        _name = name;
        _width = width;
        _height = height;
    }

    public int Height
    {
        get => _height;
        set
        {
            EnsureCustom();
            _height = value;
        }
    }

    public PaperKind Kind =>
        _kind is <= PaperKind.PrcEnvelopeNumber10Rotated
            and not ((PaperKind)48 or (PaperKind)49)
            ? _kind
            : PaperKind.Custom;

    public string PaperName
    {
        get => _name;
        set
        {
            EnsureCustom();
            _name = value;
        }
    }

    public int RawKind
    {
        get => (int)_kind;
        set => _kind = (PaperKind)value;
    }

    public int Width
    {
        get => _width;
        set
        {
            EnsureCustom();
            _width = value;
        }
    }

    public override string ToString() =>
        $"[PaperSize {PaperName} Kind={Kind} Height={Height.ToString(CultureInfo.InvariantCulture)} Width={Width.ToString(CultureInfo.InvariantCulture)}]";

    private void EnsureCustom()
    {
        if (_kind != PaperKind.Custom && !_createdByDefaultConstructor)
        {
            throw new ArgumentException("The paper size can be changed only when Kind is Custom.", "value");
        }
    }
}
