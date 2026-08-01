#nullable disable

using System.Collections;
using System.Runtime.InteropServices;

namespace SkiaSharp;

internal enum SKRuntimeEffectKind
{
    Shader,
    ColorFilter,
    Blender,
}

internal enum SKRuntimeEffectChildKind
{
    Shader,
    ColorFilter,
    Blender,
}

internal readonly record struct SKRuntimeEffectUniformDescriptor(
    string Name,
    int Offset,
    int Size);

internal readonly record struct SKRuntimeEffectChildDescriptor(
    string Name,
    SKRuntimeEffectChildKind Kind);

internal sealed class SKRuntimeEffectProgram
{
    public SKRuntimeEffectProgram(
        string source,
        SKRuntimeEffectKind kind,
        SKRuntimeEffectUniformDescriptor[] uniforms,
        SKRuntimeEffectChildDescriptor[] children,
        int uniformSize)
    {
        Source = source;
        Kind = kind;
        UniformDescriptors = uniforms;
        ChildDescriptors = children;
        UniformSize = uniformSize;
        UniformNames = Array.AsReadOnly(uniforms.Select(static uniform => uniform.Name).ToArray());
        ChildNames = Array.AsReadOnly(children.Select(static child => child.Name).ToArray());
    }

    public string Source { get; }
    public SKRuntimeEffectKind Kind { get; }
    public SKRuntimeEffectUniformDescriptor[] UniformDescriptors { get; }
    public SKRuntimeEffectChildDescriptor[] ChildDescriptors { get; }
    public IReadOnlyList<string> UniformNames { get; }
    public IReadOnlyList<string> ChildNames { get; }
    public int UniformSize { get; }
}

internal sealed class SKRuntimeEffectInstance
{
    public SKRuntimeEffectInstance(
        SKRuntimeEffectProgram program,
        byte[] uniformData,
        SKObject[] children,
        SKMatrix localMatrix)
    {
        Program = program;
        UniformData = uniformData;
        Children = children;
        LocalMatrix = localMatrix;
    }

    public SKRuntimeEffectProgram Program { get; }
    public byte[] UniformData { get; }
    public SKObject[] Children { get; }
    public SKMatrix LocalMatrix { get; }
}

public class SKRuntimeEffect : SKObject
{
    private readonly SKRuntimeEffectProgram _program;

    private SKRuntimeEffect(SKRuntimeEffectProgram program)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _program = program;
    }

    public IReadOnlyList<string> Uniforms => _program.UniformNames;

    public IReadOnlyList<string> Children => _program.ChildNames;

    public int UniformSize => _program.UniformSize;

    internal SKRuntimeEffectProgram Program => _program;

    public static SKRuntimeEffect CreateShader(string sksl, out string errors) =>
        Create(sksl, SKRuntimeEffectKind.Shader, out errors);

    public static SKRuntimeEffect CreateColorFilter(string sksl, out string errors) =>
        Create(sksl, SKRuntimeEffectKind.ColorFilter, out errors);

    public static SKRuntimeEffect CreateBlender(string sksl, out string errors) =>
        Create(sksl, SKRuntimeEffectKind.Blender, out errors);

    public static SKRuntimeShaderBuilder BuildShader(string sksl)
    {
        var effect = CreateShader(sksl, out var errors);
        return effect == null
            ? throw new SKRuntimeEffectBuilderException(errors)
            : new SKRuntimeShaderBuilder(effect);
    }

    public static SKRuntimeColorFilterBuilder BuildColorFilter(string sksl)
    {
        var effect = CreateColorFilter(sksl, out var errors);
        return effect == null
            ? throw new SKRuntimeEffectBuilderException(errors)
            : new SKRuntimeColorFilterBuilder(effect);
    }

    public static SKRuntimeBlenderBuilder BuildBlender(string sksl)
    {
        var effect = CreateBlender(sksl, out var errors);
        return effect == null
            ? throw new SKRuntimeEffectBuilderException(errors)
            : new SKRuntimeBlenderBuilder(effect);
    }

    public SKShader ToShader()
    {
        using var uniforms = new SKRuntimeEffectUniforms(this);
        using var children = new SKRuntimeEffectChildren(this);
        return ToShader(uniforms, children);
    }

    public SKShader ToShader(SKRuntimeEffectUniforms uniforms)
    {
        using var children = new SKRuntimeEffectChildren(this);
        return ToShader(uniforms, children);
    }

    public SKShader ToShader(SKRuntimeEffectUniforms uniforms, SKRuntimeEffectChildren children) =>
        ToShader(uniforms, children, SKMatrix.Identity);

    public SKShader ToShader(
        SKRuntimeEffectUniforms uniforms,
        SKRuntimeEffectChildren children,
        SKMatrix localMatrix)
    {
        EnsureKind(SKRuntimeEffectKind.Shader);
        return SKShader.CreateRuntime(CreateInstance(uniforms, children, localMatrix));
    }

    public SKColorFilter ToColorFilter()
    {
        using var uniforms = new SKRuntimeEffectUniforms(this);
        using var children = new SKRuntimeEffectChildren(this);
        return ToColorFilter(uniforms, children);
    }

    public SKColorFilter ToColorFilter(SKRuntimeEffectUniforms uniforms)
    {
        using var children = new SKRuntimeEffectChildren(this);
        return ToColorFilter(uniforms, children);
    }

    public SKColorFilter ToColorFilter(
        SKRuntimeEffectUniforms uniforms,
        SKRuntimeEffectChildren children)
    {
        EnsureKind(SKRuntimeEffectKind.ColorFilter);
        return SKColorFilter.CreateRuntime(CreateInstance(uniforms, children, SKMatrix.Identity));
    }

    public SKBlender ToBlender()
    {
        using var uniforms = new SKRuntimeEffectUniforms(this);
        using var children = new SKRuntimeEffectChildren(this);
        return ToBlender(uniforms, children);
    }

    public SKBlender ToBlender(SKRuntimeEffectUniforms uniforms)
    {
        using var children = new SKRuntimeEffectChildren(this);
        return ToBlender(uniforms, children);
    }

    public SKBlender ToBlender(
        SKRuntimeEffectUniforms uniforms,
        SKRuntimeEffectChildren children)
    {
        EnsureKind(SKRuntimeEffectKind.Blender);
        return SKBlender.CreateRuntime(CreateInstance(uniforms, children, SKMatrix.Identity));
    }

    private SKRuntimeEffectInstance CreateInstance(
        SKRuntimeEffectUniforms uniforms,
        SKRuntimeEffectChildren children,
        SKMatrix localMatrix)
    {
        ArgumentNullException.ThrowIfNull(uniforms);
        ArgumentNullException.ThrowIfNull(children);
        uniforms.EnsureEffect(this);
        children.EnsureEffect(this);
        return new SKRuntimeEffectInstance(
            _program,
            uniforms.CopyData(),
            children.CopyValues(),
            localMatrix);
    }

    private void EnsureKind(SKRuntimeEffectKind expected)
    {
        if (_program.Kind != expected)
        {
            throw new InvalidOperationException($"The runtime effect was compiled for {_program.Kind}, not {expected}.");
        }
    }

    private static SKRuntimeEffect Create(
        string sksl,
        SKRuntimeEffectKind kind,
        out string errors)
    {
        if (!SKRuntimeEffectParser.TryParse(sksl, kind, out var program, out errors))
        {
            return null;
        }

        return new SKRuntimeEffect(program);
    }
}

public class SKRuntimeEffectBuilderException : ApplicationException
{
    public SKRuntimeEffectBuilderException(string message)
        : base(message)
    {
    }
}

public class SKRuntimeEffectBuilder : IDisposable
{
    private int _disposed;

    public SKRuntimeEffectBuilder(SKRuntimeEffect effect)
    {
        Effect = effect ?? throw new ArgumentNullException(nameof(effect));
        Uniforms = new SKRuntimeEffectUniforms(effect);
        Children = new SKRuntimeEffectChildren(effect);
    }

    public SKRuntimeEffect Effect { get; }

    public SKRuntimeEffectUniforms Uniforms { get; }

    public SKRuntimeEffectChildren Children { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Uniforms.Dispose();
        Children.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class SKRuntimeShaderBuilder : SKRuntimeEffectBuilder
{
    public SKRuntimeShaderBuilder(SKRuntimeEffect effect)
        : base(effect)
    {
    }

    public SKShader Build() => Effect.ToShader(Uniforms, Children);

    public SKShader Build(SKMatrix localMatrix) => Effect.ToShader(Uniforms, Children, localMatrix);
}

public class SKRuntimeColorFilterBuilder : SKRuntimeEffectBuilder
{
    public SKRuntimeColorFilterBuilder(SKRuntimeEffect effect)
        : base(effect)
    {
    }

    public SKColorFilter Build() => Effect.ToColorFilter(Uniforms, Children);
}

public class SKRuntimeBlenderBuilder : SKRuntimeEffectBuilder
{
    public SKRuntimeBlenderBuilder(SKRuntimeEffect effect)
        : base(effect)
    {
    }

    public SKBlender Build() => Effect.ToBlender(Uniforms, Children);
}

public class SKRuntimeEffectUniforms : IDisposable, IEnumerable<string>
{
    private readonly SKRuntimeEffect _effect;
    private byte[] _data;
    private int _disposed;

    public SKRuntimeEffectUniforms(SKRuntimeEffect effect)
    {
        _effect = effect ?? throw new ArgumentNullException(nameof(effect));
        _data = new byte[effect.UniformSize];
    }

    public int Count => Names.Count;

    public int Size => _data.Length;

    public IReadOnlyList<string> Names => _effect.Uniforms;

    public SKRuntimeEffectUniform this[string name]
    {
        set => Add(name, value);
    }

    public bool Contains(string name) => Find(name).HasValue;

    public void Add(string name, SKRuntimeEffectUniform value)
    {
        ThrowIfDisposed();
        var descriptor = Find(name) ?? throw new ArgumentException($"Unknown runtime-effect uniform '{name}'.", nameof(name));
        if (value.Size != descriptor.Size)
        {
            throw new ArgumentException(
                $"Uniform '{name}' requires {descriptor.Size} bytes, but the supplied value contains {value.Size} bytes.",
                nameof(value));
        }

        value.WriteTo(_data.AsSpan(descriptor.Offset, descriptor.Size));
    }

    public void Reset()
    {
        ThrowIfDisposed();
        _data.AsSpan().Clear();
    }

    public SKData ToData()
    {
        ThrowIfDisposed();
        return new SKData((byte[])_data.Clone());
    }

    public IEnumerator<string> GetEnumerator()
    {
        ThrowIfDisposed();
        return Names.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _data = Array.Empty<byte>();
        }
        GC.SuppressFinalize(this);
    }

    internal byte[] CopyData()
    {
        ThrowIfDisposed();
        return (byte[])_data.Clone();
    }

    internal void EnsureEffect(SKRuntimeEffect effect)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(_effect, effect))
        {
            throw new ArgumentException("The uniforms belong to a different runtime effect.", nameof(effect));
        }
    }

    private SKRuntimeEffectUniformDescriptor? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var descriptor in _effect.Program.UniformDescriptors)
        {
            if (string.Equals(descriptor.Name, name, StringComparison.Ordinal))
            {
                return descriptor;
            }
        }
        return null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

public class SKRuntimeEffectChildren : IDisposable, IEnumerable<string>
{
    private readonly SKRuntimeEffect _effect;
    private SKRuntimeEffectChild?[] _values;
    private int _disposed;

    public SKRuntimeEffectChildren(SKRuntimeEffect effect)
    {
        _effect = effect ?? throw new ArgumentNullException(nameof(effect));
        _values = new SKRuntimeEffectChild?[effect.Children.Count];
    }

    public int Count => Names.Count;

    public IReadOnlyList<string> Names => _effect.Children;

    public SKRuntimeEffectChild? this[string name]
    {
        set => Add(name, value);
    }

    public bool Contains(string name) => FindIndex(name) >= 0;

    public void Add(string name, SKRuntimeEffectChild? value)
    {
        ThrowIfDisposed();
        var index = FindIndex(name);
        if (index < 0)
        {
            throw new ArgumentException($"Unknown runtime-effect child '{name}'.", nameof(name));
        }
        _values[index] = value;
    }

    public void Reset()
    {
        ThrowIfDisposed();
        Array.Clear(_values);
    }

    public SKObject[] ToArray()
    {
        ThrowIfDisposed();
        var values = new SKObject[_values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = _values[index]?.Value;
        }
        return values;
    }

    public IEnumerator<string> GetEnumerator()
    {
        ThrowIfDisposed();
        return Names.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _values = Array.Empty<SKRuntimeEffectChild?>();
        }
        GC.SuppressFinalize(this);
    }

    internal SKObject[] CopyValues() => ToArray();

    internal void EnsureEffect(SKRuntimeEffect effect)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(_effect, effect))
        {
            throw new ArgumentException("The children belong to a different runtime effect.", nameof(effect));
        }
    }

    private int FindIndex(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var descriptors = _effect.Program.ChildDescriptors;
        for (var index = 0; index < descriptors.Length; index++)
        {
            if (string.Equals(descriptors[index].Name, name, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct SKRuntimeEffectChild
{
    private readonly SKObject _value;

    public SKRuntimeEffectChild(SKShader shader) =>
        _value = shader ?? throw new ArgumentNullException(nameof(shader));

    public SKRuntimeEffectChild(SKColorFilter colorFilter) =>
        _value = colorFilter ?? throw new ArgumentNullException(nameof(colorFilter));

    public SKRuntimeEffectChild(SKBlender blender) =>
        _value = blender ?? throw new ArgumentNullException(nameof(blender));

    public SKObject Value => _value;

    public SKShader Shader => _value as SKShader;

    public SKColorFilter ColorFilter => _value as SKColorFilter;

    public SKBlender Blender => _value as SKBlender;

    public static implicit operator SKRuntimeEffectChild(SKShader shader) => new(shader);

    public static implicit operator SKRuntimeEffectChild(SKColorFilter colorFilter) => new(colorFilter);

    public static implicit operator SKRuntimeEffectChild(SKBlender blender) => new(blender);
}

[StructLayout(LayoutKind.Sequential)]
public readonly ref struct SKRuntimeEffectUniform
{
    private readonly float _floatValue;
    private readonly ReadOnlySpan<float> _floatValues;
    private readonly int _intValue;
    private readonly ReadOnlySpan<int> _intValues;
    private readonly SKColorF _colorValue;
    private readonly int _size;
    private readonly DataType _dataType;

    private enum DataType
    {
        Empty,
        Float,
        FloatValues,
        Int,
        IntValues,
        Color,
    }

    private SKRuntimeEffectUniform(float value)
    {
        this = default;
        _floatValue = value;
        _size = sizeof(float);
        _dataType = DataType.Float;
    }

    private SKRuntimeEffectUniform(ReadOnlySpan<float> values)
    {
        this = default;
        _floatValues = values;
        _size = checked(values.Length * sizeof(float));
        _dataType = DataType.FloatValues;
    }

    private SKRuntimeEffectUniform(int value)
    {
        this = default;
        _intValue = value;
        _size = sizeof(int);
        _dataType = DataType.Int;
    }

    private SKRuntimeEffectUniform(ReadOnlySpan<int> values)
    {
        this = default;
        _intValues = values;
        _size = checked(values.Length * sizeof(int));
        _dataType = DataType.IntValues;
    }

    private SKRuntimeEffectUniform(SKColorF value, int componentCount)
    {
        this = default;
        _colorValue = value;
        _size = checked(componentCount * sizeof(float));
        _dataType = DataType.Color;
    }

    public static SKRuntimeEffectUniform Empty => default;

    public bool IsEmpty => _dataType == DataType.Empty;

    public int Size => _size;

    public void WriteTo(Span<byte> data)
    {
        if (data.Length < _size)
        {
            throw new ArgumentException("The destination is smaller than the runtime-effect uniform.", nameof(data));
        }

        var destination = data[.._size];
        switch (_dataType)
        {
            case DataType.Empty:
                return;
            case DataType.Float:
                MemoryMarshal.Write(destination, in _floatValue);
                return;
            case DataType.FloatValues:
                MemoryMarshal.AsBytes(_floatValues).CopyTo(destination);
                return;
            case DataType.Int:
                MemoryMarshal.Write(destination, in _intValue);
                return;
            case DataType.IntValues:
                MemoryMarshal.AsBytes(_intValues).CopyTo(destination);
                return;
            case DataType.Color:
                Span<float> components = stackalloc float[4]
                {
                    _colorValue.Red,
                    _colorValue.Green,
                    _colorValue.Blue,
                    _colorValue.Alpha,
                };
                MemoryMarshal.AsBytes(components)[.._size].CopyTo(destination);
                return;
        }
    }

    public static implicit operator SKRuntimeEffectUniform(float value) => new(value);

    public static implicit operator SKRuntimeEffectUniform(float[] value) =>
        new((ReadOnlySpan<float>)(value ?? throw new ArgumentNullException(nameof(value))));

    public static implicit operator SKRuntimeEffectUniform(Span<float> value) => new(value);

    public static implicit operator SKRuntimeEffectUniform(ReadOnlySpan<float> value) => new(value);

    public static implicit operator SKRuntimeEffectUniform(int value) => new(value);

    public static implicit operator SKRuntimeEffectUniform(int[] value) =>
        new((ReadOnlySpan<int>)(value ?? throw new ArgumentNullException(nameof(value))));

    public static implicit operator SKRuntimeEffectUniform(Span<int> value) => new(value);

    public static implicit operator SKRuntimeEffectUniform(ReadOnlySpan<int> value) => new(value);

    public static implicit operator SKRuntimeEffectUniform(SKColor value) =>
        new(new SKColorF(value.Red / 255f, value.Green / 255f, value.Blue / 255f, value.Alpha / 255f), 4);

    public static implicit operator SKRuntimeEffectUniform(SKColorF value) => new(value, 4);

    public static implicit operator SKRuntimeEffectUniform(SKPoint value) =>
        new(new SKColorF(value.X, value.Y, 0f, 0f), 2);

    public static implicit operator SKRuntimeEffectUniform(SKPoint3 value) =>
        new(new SKColorF(value.X, value.Y, value.Z, 0f), 3);

    public static implicit operator SKRuntimeEffectUniform(SKSize value) =>
        new(new SKColorF(value.Width, value.Height, 0f, 0f), 2);

    public static implicit operator SKRuntimeEffectUniform(SKPointI value) => new(new[] { value.X, value.Y });

    public static implicit operator SKRuntimeEffectUniform(SKSizeI value) => new(new[] { value.Width, value.Height });

    public static implicit operator SKRuntimeEffectUniform(SKMatrix value) => new(value.Values);

    public static implicit operator SKRuntimeEffectUniform(float[][] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var length = 0;
        foreach (var row in value)
        {
            ArgumentNullException.ThrowIfNull(row);
            length = checked(length + row.Length);
        }

        var flattened = new float[length];
        var offset = 0;
        foreach (var row in value)
        {
            row.CopyTo(flattened, offset);
            offset += row.Length;
        }
        return new SKRuntimeEffectUniform(flattened);
    }
}

internal static class SKRuntimeEffectParser
{
    public static bool TryParse(
        string source,
        SKRuntimeEffectKind kind,
        out SKRuntimeEffectProgram program,
        out string errors)
    {
        program = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            errors = "The SkSL source is empty.";
            return false;
        }

        if (!ContainsMainFunction(source))
        {
            errors = "The SkSL source does not declare a main function.";
            return false;
        }

        var uniforms = new List<SKRuntimeEffectUniformDescriptor>();
        var children = new List<SKRuntimeEffectChildDescriptor>();
        var offset = 0;
        foreach (var statement in EnumerateTopLevelStatements(source))
        {
            if (!TryParseUniform(statement, out var type, out var name, out var arrayCount))
            {
                continue;
            }

            if (TryGetChildKind(type, out var childKind))
            {
                if (arrayCount != 1)
                {
                    errors = $"Child arrays are not supported for '{name}'.";
                    return false;
                }
                children.Add(new SKRuntimeEffectChildDescriptor(name, childKind));
                continue;
            }

            if (!TryGetUniformElementSize(type, out var elementSize))
            {
                errors = $"Unsupported runtime-effect uniform type '{type}'.";
                return false;
            }

            var size = checked(elementSize * arrayCount);
            uniforms.Add(new SKRuntimeEffectUniformDescriptor(name, offset, size));
            offset = checked(offset + size);
        }

        program = new SKRuntimeEffectProgram(
            source,
            kind,
            uniforms.ToArray(),
            children.ToArray(),
            offset);
        errors = string.Empty;
        return true;
    }

    private static bool ContainsMainFunction(string source)
    {
        for (var index = 0; index <= source.Length - 4; index++)
        {
            if (!source.AsSpan(index, 4).SequenceEqual("main"))
            {
                continue;
            }

            var before = index == 0 ? '\0' : source[index - 1];
            var after = index + 4 >= source.Length ? '\0' : source[index + 4];
            if (!IsIdentifierPart(before) && (char.IsWhiteSpace(after) || after == '('))
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> EnumerateTopLevelStatements(string source)
    {
        var depth = 0;
        var start = 0;
        var inLineComment = false;
        var inBlockComment = false;
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (inLineComment)
            {
                if (current == '\n') inLineComment = false;
                continue;
            }
            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }
            if (current == '/' && next == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }
            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }
            if (current == '{') depth++;
            else if (current == '}') depth = Math.Max(0, depth - 1);
            else if (current == ';' && depth == 0)
            {
                yield return source[start..index];
                start = index + 1;
            }
        }
    }

    private static bool TryParseUniform(
        string statement,
        out string type,
        out string name,
        out int arrayCount)
    {
        type = string.Empty;
        name = string.Empty;
        arrayCount = 1;
        var text = statement.AsSpan().Trim();
        var uniformIndex = text.IndexOf("uniform".AsSpan(), StringComparison.Ordinal);
        if (uniformIndex < 0)
        {
            return false;
        }

        text = text[(uniformIndex + "uniform".Length)..].Trim();
        var typeLength = ReadIdentifier(text);
        if (typeLength == 0) return false;
        type = text[..typeLength].ToString();
        text = text[typeLength..].Trim();
        var nameLength = ReadIdentifier(text);
        if (nameLength == 0) return false;
        name = text[..nameLength].ToString();
        text = text[nameLength..].Trim();
        if (text.IsEmpty) return true;
        if (text[0] != '[' || text[^1] != ']') return false;
        return int.TryParse(text[1..^1], out arrayCount) && arrayCount > 0;
    }

    private static int ReadIdentifier(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || !(char.IsLetter(text[0]) || text[0] == '_')) return 0;
        var length = 1;
        while (length < text.Length && IsIdentifierPart(text[length])) length++;
        return length;
    }

    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static bool TryGetChildKind(string type, out SKRuntimeEffectChildKind kind)
    {
        switch (type)
        {
            case "shader":
                kind = SKRuntimeEffectChildKind.Shader;
                return true;
            case "colorFilter":
                kind = SKRuntimeEffectChildKind.ColorFilter;
                return true;
            case "blender":
                kind = SKRuntimeEffectChildKind.Blender;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool TryGetUniformElementSize(string type, out int size)
    {
        size = type switch
        {
            "float" or "half" or "int" => 4,
            "float2" or "half2" or "int2" => 8,
            "float3" or "half3" or "int3" => 12,
            "float4" or "half4" or "int4" => 16,
            "float2x2" or "half2x2" => 16,
            "float3x3" or "half3x3" => 36,
            "float4x4" or "half4x4" => 64,
            _ => 0,
        };
        return size != 0;
    }
}
