using System.Runtime.InteropServices;

namespace System.Drawing.Imaging;

[StructLayout(LayoutKind.Sequential)]
public sealed unsafe class EncoderParameter : IDisposable
{
    private Guid _parameterGuid;
    private readonly int _numberOfValues;
    private readonly EncoderParameterValueType _parameterValueType;
    private nint _parameterValue;

    public Encoder Encoder
    {
        get => new(_parameterGuid);
        set => _parameterGuid = value.Guid;
    }

    public EncoderParameterValueType Type => _parameterValueType;

    public EncoderParameterValueType ValueType => _parameterValueType;

    public int NumberOfValues => _numberOfValues;

    public EncoderParameter(Encoder encoder, byte value)
        : this(encoder, [value], EncoderParameterValueType.ValueTypeByte)
    {
    }

    public EncoderParameter(Encoder encoder, byte value, bool undefined)
        : this(encoder, [value], undefined ? EncoderParameterValueType.ValueTypeUndefined : EncoderParameterValueType.ValueTypeByte)
    {
    }

    public EncoderParameter(Encoder encoder, short value)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        _parameterGuid = encoder.Guid;
        _parameterValueType = EncoderParameterValueType.ValueTypeShort;
        _numberOfValues = 1;
        _parameterValue = AllocateAndCopy(&value, sizeof(short));
    }

    public EncoderParameter(Encoder encoder, long value)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        int narrowed = (int)value;
        _parameterGuid = encoder.Guid;
        _parameterValueType = EncoderParameterValueType.ValueTypeLong;
        _numberOfValues = 1;
        _parameterValue = AllocateAndCopy(&narrowed, sizeof(int));
    }

    public EncoderParameter(Encoder encoder, int numerator, int denominator)
        : this(encoder, [numerator], [denominator])
    {
    }

    public EncoderParameter(Encoder encoder, long rangebegin, long rangeend)
        : this(encoder, [rangebegin], [rangeend])
    {
    }

    public EncoderParameter(
        Encoder encoder,
        int numerator1,
        int demoninator1,
        int numerator2,
        int demoninator2)
        : this(encoder, [numerator1], [demoninator1], [numerator2], [demoninator2])
    {
    }

    public EncoderParameter(Encoder encoder, string value)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(value);
        _parameterGuid = encoder.Guid;
        _parameterValueType = EncoderParameterValueType.ValueTypeAscii;
        _numberOfValues = value.Length;
        _parameterValue = Marshal.StringToHGlobalAnsi(value);
    }

    public EncoderParameter(Encoder encoder, byte[] value)
        : this(encoder, value, EncoderParameterValueType.ValueTypeByte)
    {
    }

    public EncoderParameter(Encoder encoder, byte[] value, bool undefined)
        : this(encoder, value, undefined ? EncoderParameterValueType.ValueTypeUndefined : EncoderParameterValueType.ValueTypeByte)
    {
    }

    public EncoderParameter(Encoder encoder, short[] value)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(value);
        _parameterGuid = encoder.Guid;
        _parameterValueType = EncoderParameterValueType.ValueTypeShort;
        _numberOfValues = value.Length;
        _parameterValue = Allocate(checked(value.Length * sizeof(short)));
        Marshal.Copy(value, 0, _parameterValue, value.Length);
    }

    public EncoderParameter(Encoder encoder, long[] value)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(value);
        _parameterGuid = encoder.Guid;
        _parameterValueType = EncoderParameterValueType.ValueTypeLong;
        _numberOfValues = value.Length;
        _parameterValue = Allocate(checked(value.Length * sizeof(int)));
        int* destination = (int*)_parameterValue;
        for (int index = 0; index < value.Length; index++)
        {
            destination[index] = (int)value[index];
        }
    }

    public EncoderParameter(Encoder encoder, int[] numerator, int[] denominator)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ValidateEqualLength(numerator, denominator);
        _parameterGuid = encoder.Guid;
        _parameterValueType = EncoderParameterValueType.ValueTypeRational;
        _numberOfValues = numerator.Length;
        _parameterValue = Allocate(checked(numerator.Length * 2 * sizeof(int)));
        int* destination = (int*)_parameterValue;
        for (int index = 0; index < numerator.Length; index++)
        {
            destination[index * 2] = numerator[index];
            destination[index * 2 + 1] = denominator[index];
        }
    }

    public EncoderParameter(Encoder encoder, long[] rangebegin, long[] rangeend)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ValidateEqualLength(rangebegin, rangeend);
        _parameterGuid = encoder.Guid;
        _parameterValueType = EncoderParameterValueType.ValueTypeLongRange;
        _numberOfValues = rangebegin.Length;
        _parameterValue = Allocate(checked(rangebegin.Length * 2 * sizeof(int)));
        int* destination = (int*)_parameterValue;
        for (int index = 0; index < rangebegin.Length; index++)
        {
            destination[index * 2] = (int)rangebegin[index];
            destination[index * 2 + 1] = (int)rangeend[index];
        }
    }

    public EncoderParameter(
        Encoder encoder,
        int[] numerator1,
        int[] denominator1,
        int[] numerator2,
        int[] denominator2)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(numerator1);
        ArgumentNullException.ThrowIfNull(denominator1);
        ArgumentNullException.ThrowIfNull(numerator2);
        ArgumentNullException.ThrowIfNull(denominator2);
        if (numerator1.Length != denominator1.Length ||
            numerator1.Length != numerator2.Length ||
            numerator1.Length != denominator2.Length)
        {
            throw new ArgumentException("Rational range arrays must have equal lengths.");
        }

        _parameterGuid = encoder.Guid;
        _parameterValueType = EncoderParameterValueType.ValueTypeRationalRange;
        _numberOfValues = numerator1.Length;
        _parameterValue = Allocate(checked(numerator1.Length * 4 * sizeof(int)));
        int* destination = (int*)_parameterValue;
        for (int index = 0; index < numerator1.Length; index++)
        {
            destination[index * 4] = numerator1[index];
            destination[index * 4 + 1] = denominator1[index];
            destination[index * 4 + 2] = numerator2[index];
            destination[index * 4 + 3] = denominator2[index];
        }
    }

    [Obsolete("This constructor has been deprecated. Use EncoderParameter(Encoder encoder, int numberValues, EncoderParameterValueType type, IntPtr value) instead.")]
    public EncoderParameter(Encoder encoder, int NumberOfValues, int Type, int Value)
        : this(encoder, NumberOfValues, (EncoderParameterValueType)Type, new IntPtr(Value))
    {
    }

    public EncoderParameter(Encoder encoder, int numberValues, EncoderParameterValueType type, IntPtr value)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        if (numberValues < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numberValues));
        }

        int elementSize = GetElementSize(type);
        int byteCount = checked(elementSize * numberValues);
        if (byteCount != 0 && value == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(value));
        }

        _parameterGuid = encoder.Guid;
        _parameterValueType = type;
        _numberOfValues = numberValues;
        _parameterValue = AllocateAndCopy((void*)value, byteCount);
    }

    public void Dispose()
    {
        if (_parameterValue != 0)
        {
            Marshal.FreeHGlobal(_parameterValue);
            _parameterValue = 0;
        }

        GC.SuppressFinalize(this);
    }

    ~EncoderParameter() => Dispose();

    internal bool TryGetInt64(out long value)
    {
        value = 0;
        if (_numberOfValues == 0 || _parameterValue == 0)
        {
            return false;
        }

        switch (_parameterValueType)
        {
            case EncoderParameterValueType.ValueTypeByte:
            case EncoderParameterValueType.ValueTypeUndefined:
                value = *(byte*)_parameterValue;
                return true;
            case EncoderParameterValueType.ValueTypeShort:
                value = *(short*)_parameterValue;
                return true;
            case EncoderParameterValueType.ValueTypeLong:
                value = *(int*)_parameterValue;
                return true;
            default:
                return false;
        }
    }

    private EncoderParameter(Encoder encoder, byte[] value, EncoderParameterValueType type)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(value);
        _parameterGuid = encoder.Guid;
        _parameterValueType = type;
        _numberOfValues = value.Length;
        _parameterValue = Allocate(value.Length);
        if (value.Length != 0)
        {
            Marshal.Copy(value, 0, _parameterValue, value.Length);
        }
    }

    private static void ValidateEqualLength<T>(T[] first, T[] second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Length != second.Length)
        {
            throw new ArgumentException("Parameter arrays must have equal lengths.");
        }
    }

    private static int GetElementSize(EncoderParameterValueType type) => type switch
    {
        EncoderParameterValueType.ValueTypeByte or
        EncoderParameterValueType.ValueTypeAscii or
        EncoderParameterValueType.ValueTypeUndefined => 1,
        EncoderParameterValueType.ValueTypeShort => 2,
        EncoderParameterValueType.ValueTypeLong => 4,
        EncoderParameterValueType.ValueTypeRational or
        EncoderParameterValueType.ValueTypeLongRange => 8,
        EncoderParameterValueType.ValueTypeRationalRange => 16,
        EncoderParameterValueType.ValueTypePointer => IntPtr.Size,
        _ => throw new ArgumentException("Invalid encoder parameter value type.", nameof(type))
    };

    private static nint Allocate(int byteCount) =>
        byteCount == 0 ? 0 : Marshal.AllocHGlobal(byteCount);

    private static nint AllocateAndCopy(void* source, int byteCount)
    {
        nint destination = Allocate(byteCount);
        if (byteCount != 0)
        {
            new ReadOnlySpan<byte>(source, byteCount).CopyTo(new Span<byte>((void*)destination, byteCount));
        }

        return destination;
    }
}
