using System.ComponentModel;
using System.Drawing.Text;

namespace System.Drawing;

public enum StringAlignment
{
    Near = 0,
    Center = 1,
    Far = 2
}

public enum StringTrimming
{
    None = 0,
    Character = 1,
    Word = 2,
    EllipsisCharacter = 3,
    EllipsisWord = 4,
    EllipsisPath = 5
}

[Flags]
public enum StringFormatFlags
{
    DirectionRightToLeft = 0x00000001,
    DirectionVertical = 0x00000002,
    FitBlackBox = 0x00000004,
    DisplayFormatControl = 0x00000020,
    NoFontFallback = 0x00000400,
    MeasureTrailingSpaces = 0x00000800,
    NoWrap = 0x00001000,
    LineLimit = 0x00002000,
    NoClip = 0x00004000
}

public enum StringDigitSubstitute
{
    User = 0,
    None = 1,
    National = 2,
    Traditional = 3
}

public struct CharacterRange : IEquatable<CharacterRange>
{
    public CharacterRange(int first, int length)
    {
        First = first;
        Length = length;
    }

    public int First { get; set; }
    public int Length { get; set; }

    public readonly bool Equals(CharacterRange other) => First == other.First && Length == other.Length;
    public override readonly bool Equals(object? obj) => obj is CharacterRange other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(First, Length);
    public static bool operator ==(CharacterRange left, CharacterRange right) => left.Equals(right);
    public static bool operator !=(CharacterRange left, CharacterRange right) => !left.Equals(right);
}

public sealed class StringFormat : MarshalByRefObject, ICloneable, IDisposable
{
    private StringAlignment _alignment;
    private int _digitSubstitutionLanguage;
    private StringDigitSubstitute _digitSubstitutionMethod;
    private StringFormatFlags _formatFlags;
    private HotkeyPrefix _hotkeyPrefix;
    private StringAlignment _lineAlignment;
    private StringTrimming _trimming = StringTrimming.Character;
    // GDI+ retains the creation language for shaping while digit-substitution
    // queries remain at User/0 until SetDigitSubstitution is called.
    private ushort _language;
    private float _firstTabOffset;
    private float[] _tabStops = [];
    private CharacterRange[] _measurableCharacterRanges = [];
    private bool _disposed;

    public StringFormat()
    {
    }

    public StringFormat(StringFormatFlags options)
        : this(options, language: 0)
    {
    }

    public StringFormat(StringFormatFlags options, int language)
    {
        _formatFlags = options;
        _language = unchecked((ushort)language);
    }

    public StringFormat(StringFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.EnsureNotDisposed();

        _alignment = format._alignment;
        _digitSubstitutionLanguage = format._digitSubstitutionLanguage;
        _digitSubstitutionMethod = format._digitSubstitutionMethod;
        _formatFlags = format._formatFlags;
        _hotkeyPrefix = format._hotkeyPrefix;
        _lineAlignment = format._lineAlignment;
        _trimming = format._trimming;
        _language = format._language;
        _firstTabOffset = format._firstTabOffset;
        _tabStops = (float[])format._tabStops.Clone();
        _measurableCharacterRanges = (CharacterRange[])format._measurableCharacterRanges.Clone();
    }

    public static StringFormat GenericDefault => new();

    public static StringFormat GenericTypographic => new(
        StringFormatFlags.FitBlackBox | StringFormatFlags.LineLimit | StringFormatFlags.NoClip)
    {
        Trimming = StringTrimming.None
    };

    public StringAlignment Alignment
    {
        get
        {
            EnsureNotDisposed();
            return _alignment;
        }
        set
        {
            EnsureNotDisposed();
            ValidateAlignment(value, nameof(value));
            _alignment = value;
        }
    }

    public int DigitSubstitutionLanguage
    {
        get
        {
            EnsureNotDisposed();
            return _digitSubstitutionLanguage;
        }
    }

    public StringDigitSubstitute DigitSubstitutionMethod
    {
        get
        {
            EnsureNotDisposed();
            return _digitSubstitutionMethod;
        }
    }

    public StringFormatFlags FormatFlags
    {
        get
        {
            EnsureNotDisposed();
            return _formatFlags;
        }
        set
        {
            EnsureNotDisposed();
            _formatFlags = value;
        }
    }

    public HotkeyPrefix HotkeyPrefix
    {
        get
        {
            EnsureNotDisposed();
            return _hotkeyPrefix;
        }
        set
        {
            EnsureNotDisposed();
            if (value < HotkeyPrefix.None || value > HotkeyPrefix.Hide)
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(HotkeyPrefix));
            }

            _hotkeyPrefix = value;
        }
    }

    public StringAlignment LineAlignment
    {
        get
        {
            EnsureNotDisposed();
            return _lineAlignment;
        }
        set
        {
            EnsureNotDisposed();
            ValidateAlignment(value, nameof(value));
            _lineAlignment = value;
        }
    }

    public StringTrimming Trimming
    {
        get
        {
            EnsureNotDisposed();
            return _trimming;
        }
        set
        {
            EnsureNotDisposed();
            if (value < StringTrimming.None || value > StringTrimming.EllipsisPath)
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(StringTrimming));
            }

            _trimming = value;
        }
    }

    public object Clone()
    {
        EnsureNotDisposed();
        return new StringFormat(this);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    public float[] GetTabStops(out float firstTabOffset)
    {
        EnsureNotDisposed();
        firstTabOffset = _firstTabOffset;
        return (float[])_tabStops.Clone();
    }

    public void SetDigitSubstitution(int language, StringDigitSubstitute substitute)
    {
        EnsureNotDisposed();
        _digitSubstitutionLanguage = unchecked((ushort)language);
        _digitSubstitutionMethod = substitute;
    }

    public void SetMeasurableCharacterRanges(CharacterRange[] ranges)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Length > 32)
        {
            throw new OverflowException("A StringFormat supports at most 32 measurable character ranges.");
        }

        _measurableCharacterRanges = (CharacterRange[])ranges.Clone();
    }

    public void SetTabStops(float firstTabOffset, float[] tabStops)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(tabStops);
        if (firstTabOffset < 0f)
        {
            throw new ArgumentException("The first tab offset cannot be negative.");
        }

        for (int i = 0; i < tabStops.Length; i++)
        {
            if (float.IsNegativeInfinity(tabStops[i]))
            {
                throw new NotImplementedException("Negative-infinity tab stops are not supported by GDI+.");
            }
        }

        _firstTabOffset = tabStops.Length == 0 ? 0f : firstTabOffset;
        _tabStops = (float[])tabStops.Clone();
    }

    public override string ToString()
    {
        EnsureNotDisposed();
        return $"[StringFormat, FormatFlags={_formatFlags}]";
    }

    internal CharacterRange[] GetMeasurableCharacterRanges()
    {
        EnsureNotDisposed();
        return (CharacterRange[])_measurableCharacterRanges.Clone();
    }

    internal void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ArgumentException("Parameter is not valid.");
        }
    }

    private static void ValidateAlignment(StringAlignment value, string parameterName)
    {
        if (value < StringAlignment.Near || value > StringAlignment.Far)
        {
            throw new InvalidEnumArgumentException(parameterName, (int)value, typeof(StringAlignment));
        }
    }
}
