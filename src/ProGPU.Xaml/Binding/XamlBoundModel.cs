using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Serialization;

namespace ProGPU.Xaml.Binding;

public enum XamlBoundReferenceKind
{
    Resolved,
    Directive,
    Intrinsic,
    CollectionItems,
    DictionaryItems,
    Error
}

public sealed class XamlBoundTypeReference
{
    private XamlBoundTypeReference(
        XamlQualifiedName requestedName,
        XamlTypeInfo? symbol,
        Diagnostic? diagnostic)
    {
        RequestedName = requestedName;
        Symbol = symbol;
        Diagnostic = diagnostic;
    }

    public XamlQualifiedName RequestedName { get; }
    public XamlTypeInfo? Symbol { get; }
    public Diagnostic? Diagnostic { get; }
    public bool IsError => Symbol == null;
    public static XamlBoundTypeReference Resolved(XamlQualifiedName name, XamlTypeInfo symbol) =>
        new XamlBoundTypeReference(name, symbol ?? throw new ArgumentNullException(nameof(symbol)), null);
    public static XamlBoundTypeReference Error(XamlQualifiedName name, Diagnostic diagnostic) =>
        new XamlBoundTypeReference(name, null, diagnostic ?? throw new ArgumentNullException(nameof(diagnostic)));
}

public sealed class XamlBoundMemberReference
{
    private XamlBoundMemberReference(
        XamlInfosetMemberName requestedName,
        XamlBoundReferenceKind kind,
        XamlMemberInfo? symbol,
        Diagnostic? diagnostic)
    {
        RequestedName = requestedName;
        Kind = kind;
        Symbol = symbol;
        Diagnostic = diagnostic;
    }

    public XamlInfosetMemberName RequestedName { get; }
    public XamlBoundReferenceKind Kind { get; }
    public XamlMemberInfo? Symbol { get; }
    public Diagnostic? Diagnostic { get; }
    public bool IsError => Kind == XamlBoundReferenceKind.Error;
    public string Identity => Symbol?.Identity ??
        Kind.ToString() + ":" + RequestedName.NamespaceUri + ":" + RequestedName.DisplayName;

    public static XamlBoundMemberReference Resolved(XamlInfosetMemberName name, XamlMemberInfo symbol) =>
        new XamlBoundMemberReference(name, XamlBoundReferenceKind.Resolved,
            symbol ?? throw new ArgumentNullException(nameof(symbol)), null);
    public static XamlBoundMemberReference Synthetic(XamlInfosetMemberName name, XamlBoundReferenceKind kind) =>
        kind == XamlBoundReferenceKind.Resolved || kind == XamlBoundReferenceKind.Error
            ? throw new ArgumentOutOfRangeException(nameof(kind))
            : new XamlBoundMemberReference(name, kind, null, null);
    public static XamlBoundMemberReference Error(XamlInfosetMemberName name, Diagnostic diagnostic) =>
        new XamlBoundMemberReference(name, XamlBoundReferenceKind.Error, null,
            diagnostic ?? throw new ArgumentNullException(nameof(diagnostic)));
}

public abstract class XamlBoundValue
{
    protected XamlBoundValue(TextSpan sourceSpan, ulong stableId)
    {
        SourceSpan = sourceSpan;
        StableId = stableId;
    }

    public TextSpan SourceSpan { get; }
    public ulong StableId { get; }
}

public sealed class XamlBoundText : XamlBoundValue
{
    public XamlBoundText(
        string text,
        bool isCData,
        TextSpan sourceSpan,
        ulong stableId,
        XamlTextSyntaxInfo? textSyntax = null,
        string? originalText = null)
        : base(sourceSpan, stableId)
    {
        Text = text ?? string.Empty;
        OriginalText = originalText ?? Text;
        IsCData = isCData;
        TextSyntax = textSyntax ?? XamlTextSyntaxInfo.None;
    }

    public string Text { get; }
    public string OriginalText { get; }
    public bool IsNormalized => !string.Equals(Text, OriginalText, StringComparison.Ordinal);
    public bool IsCData { get; }
    public XamlTextSyntaxInfo TextSyntax { get; }
}

/// <summary>A type-valued intrinsic argument resolved through the canonical XAML type system.</summary>
public sealed class XamlBoundTypeValue : XamlBoundValue
{
    public XamlBoundTypeValue(XamlBoundTypeReference type, TextSpan sourceSpan, ulong stableId)
        : base(sourceSpan, stableId) => Type = type ?? throw new ArgumentNullException(nameof(type));
    public XamlBoundTypeReference Type { get; }
}

public sealed class XamlBoundStaticMemberValue : XamlBoundValue
{
    public XamlBoundStaticMemberValue(ISymbol? member, string requestedName, Diagnostic? diagnostic, TextSpan sourceSpan, ulong stableId)
        : base(sourceSpan, stableId)
    {
        Member = member;
        RequestedName = requestedName ?? throw new ArgumentNullException(nameof(requestedName));
        Diagnostic = diagnostic;
    }
    public ISymbol? Member { get; }
    public string RequestedName { get; }
    public Diagnostic? Diagnostic { get; }
    public bool IsError => Member == null;
}

public sealed class XamlBoundFactoryMethodValue : XamlBoundValue
{
    public XamlBoundFactoryMethodValue(IMethodSymbol? method, string requestedName, Diagnostic? diagnostic, TextSpan sourceSpan, ulong stableId)
        : base(sourceSpan, stableId)
    {
        Method = method;
        RequestedName = requestedName ?? throw new ArgumentNullException(nameof(requestedName));
        Diagnostic = diagnostic;
    }
    public IMethodSymbol? Method { get; }
    public string RequestedName { get; }
    public Diagnostic? Diagnostic { get; }
    public bool IsError => Method == null;
}

public sealed class XamlBoundNameReferenceValue : XamlBoundValue
{
    public XamlBoundNameReferenceValue(string name, TextSpan sourceSpan, ulong stableId)
        : base(sourceSpan, stableId) => Name = name ?? throw new ArgumentNullException(nameof(name));
    public string Name { get; }
}

public enum XamlCompiledBindingMode
{
    Default,
    OneTime,
    OneWay,
    TwoWay
}

public enum XamlCompiledBindingKind
{
    Property,
    Event
}

public enum XamlCompiledBindingSourceKind
{
    Root,
    Context
}

public enum XamlCompiledBindingPathSegmentKind
{
    Member,
    IntegerIndexer,
    StringIndexer,
    Cast,
    AttachedMember
}

/// <summary>Framework policy surfaced through the active type system to the neutral binder.</summary>
public interface IXamlCompiledBindingPolicy
{
    XamlCompiledBindingMode DefaultCompiledBindingMode { get; }
    string? DefaultCompiledBindingModeDirective { get; }
    string? CompiledBindingDataTypeDirective { get; }
    IReadOnlyDictionary<char, char> CompiledBindingPathBracketPairs { get; }
}

public sealed class XamlCompiledBindingPathSegment
{
    public XamlCompiledBindingPathSegment(
        ISymbol member,
        ITypeSymbol sourceType,
        ITypeSymbol valueType,
        bool canWrite)
        : this(
            XamlCompiledBindingPathSegmentKind.Member,
            member,
            sourceType,
            valueType,
            canWrite,
            integerIndex: 0,
            stringIndex: null)
    {
    }

    public XamlCompiledBindingPathSegment(
        XamlCompiledBindingPathSegmentKind kind,
        ISymbol member,
        ITypeSymbol sourceType,
        ITypeSymbol valueType,
        bool canWrite,
        int integerIndex = 0,
        string? stringIndex = null,
        IMethodSymbol? setterMethod = null,
        string? attachedMemberName = null)
    {
        if (kind == XamlCompiledBindingPathSegmentKind.StringIndexer &&
            stringIndex == null)
            throw new ArgumentNullException(nameof(stringIndex));
        Member = member ?? throw new ArgumentNullException(nameof(member));
        SourceType = sourceType ?? throw new ArgumentNullException(nameof(sourceType));
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        Kind = kind;
        CanWrite = canWrite;
        IntegerIndex = integerIndex;
        StringIndex = stringIndex;
        SetterMethod = setterMethod;
        AttachedMemberName = attachedMemberName;
    }

    public XamlCompiledBindingPathSegmentKind Kind { get; }
    public ISymbol Member { get; }
    public ITypeSymbol SourceType { get; }
    public ITypeSymbol ValueType { get; }
    public bool CanWrite { get; }
    public int IntegerIndex { get; }
    public string? StringIndex { get; }
    public IMethodSymbol? SetterMethod { get; }
    public string? AttachedMemberName { get; }
    public string DisplayName => Kind switch
    {
        XamlCompiledBindingPathSegmentKind.IntegerIndexer => "[" + IntegerIndex + "]",
        XamlCompiledBindingPathSegmentKind.StringIndexer => "['" + StringIndex + "']",
        XamlCompiledBindingPathSegmentKind.Cast => "(" + ValueType.ToDisplayString() + ")",
        XamlCompiledBindingPathSegmentKind.AttachedMember =>
            Member.ContainingType?.Name + "." + AttachedMemberName,
        _ => Member.Name
    };
}

public enum XamlCompiledBindingFunctionArgumentKind
{
    Path,
    String,
    Number,
    Boolean
}

/// <summary>
/// One exact argument to a compiled-binding function. Path arguments retain their complete
/// symbol-bound dependency path; literal arguments retain a typed value and the selected
/// parameter symbol.
/// </summary>
public sealed class XamlCompiledBindingFunctionArgument
{
    public XamlCompiledBindingFunctionArgument(
        XamlCompiledBindingFunctionArgumentKind kind,
        IParameterSymbol parameter,
        ITypeSymbol valueType,
        ImmutableArray<XamlCompiledBindingPathSegment> pathSegments = default,
        object? literalValue = null)
    {
        Kind = kind;
        Parameter = parameter ?? throw new ArgumentNullException(nameof(parameter));
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        PathSegments = pathSegments.IsDefault
            ? ImmutableArray<XamlCompiledBindingPathSegment>.Empty
            : pathSegments;
        LiteralValue = literalValue;
    }

    public XamlCompiledBindingFunctionArgumentKind Kind { get; }
    public IParameterSymbol Parameter { get; }
    public ITypeSymbol ValueType { get; }
    public ImmutableArray<XamlCompiledBindingPathSegment> PathSegments { get; }
    public object? LiteralValue { get; }
}

/// <summary>
/// Exact Roslyn-symbol representation of a terminal compiled-binding invocation. Instance
/// owner and argument paths remain separate because every path is independently tracked.
/// </summary>
public sealed class XamlCompiledBindingFunction
{
    public XamlCompiledBindingFunction(
        IMethodSymbol method,
        ImmutableArray<XamlCompiledBindingPathSegment> ownerPathSegments,
        ImmutableArray<XamlCompiledBindingFunctionArgument> arguments)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        OwnerPathSegments = ownerPathSegments;
        Arguments = arguments;
    }

    public IMethodSymbol Method { get; }
    public ImmutableArray<XamlCompiledBindingPathSegment> OwnerPathSegments { get; }
    public ImmutableArray<XamlCompiledBindingFunctionArgument> Arguments { get; }
    public bool IsStatic => Method.IsStatic;
}

public enum XamlBindingPathAccessorKind
{
    Member,
    IntegerIndexer,
    StringIndexer
}

/// <summary>
/// One canonical CLR member or constant indexer required by a typed ordinary-binding path.
/// Unlike compiled-binding executable segments, this descriptor authorizes publication into a
/// framework accessor registry while ordinary binding retains its runtime source semantics.
/// </summary>
public sealed class XamlBindingPathAccessor
{
    public XamlBindingPathAccessor(
        XamlBindingPathAccessorKind kind,
        ISymbol member,
        ITypeSymbol sourceType,
        ITypeSymbol valueType,
        bool canWrite,
        int integerIndex = 0,
        string? stringIndex = null)
    {
        if (kind == XamlBindingPathAccessorKind.StringIndexer &&
            stringIndex == null)
            throw new ArgumentNullException(nameof(stringIndex));
        Member = member ?? throw new ArgumentNullException(nameof(member));
        Kind = kind;
        SourceType = sourceType ?? throw new ArgumentNullException(nameof(sourceType));
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        CanWrite = canWrite;
        IntegerIndex = integerIndex;
        StringIndex = stringIndex;
    }

    public XamlBindingPathAccessorKind Kind { get; }
    public ISymbol Member { get; }
    public ITypeSymbol SourceType { get; }
    public ITypeSymbol ValueType { get; }
    public bool CanWrite { get; }
    public int IntegerIndex { get; }
    public string? StringIndex { get; }
}

public enum XamlBindingSourceKind
{
    Unknown,
    LexicalDataType,
    ExplicitValue,
    ExplicitStaticMember,
    RelativeSelf
}

/// <summary>
/// Canonical ordinary-binding operation. The original extension remains the runtime descriptor;
/// optional accessor evidence is emitted only when a lexical or explicit source type is known.
/// </summary>
public sealed class XamlBoundBinding : XamlBoundValue
{
    public XamlBoundBinding(
        XamlBoundObject extension,
        XamlTypeInfo? sourceType,
        XamlBindingSourceKind sourceKind,
        string path,
        XamlBindingPathSyntax? pathSyntax,
        ImmutableArray<XamlBindingPathAccessor> accessors,
        TextSpan sourceSpan,
        ulong stableId)
        : base(sourceSpan, stableId)
    {
        Extension = extension ?? throw new ArgumentNullException(nameof(extension));
        SourceType = sourceType;
        SourceKind = sourceKind;
        Path = path ?? string.Empty;
        PathSyntax = pathSyntax;
        Accessors = accessors.IsDefault
            ? ImmutableArray<XamlBindingPathAccessor>.Empty
            : accessors;
    }

    public XamlBoundObject Extension { get; }
    public XamlTypeInfo? SourceType { get; }
    public XamlBindingSourceKind SourceKind { get; }
    public string Path { get; }
    public XamlBindingPathSyntax? PathSyntax { get; }
    public ImmutableArray<XamlBindingPathAccessor> Accessors { get; }
}

/// <summary>
/// Canonical, symbol-bound compiled-binding expression. The original extension remains
/// available for framework-owned options such as converters and fallback values, while the
/// executable path never needs runtime member-name discovery or reflection.
/// </summary>
public sealed class XamlBoundCompiledBinding : XamlBoundValue
{
    public XamlBoundCompiledBinding(
        XamlBoundObject extension,
        XamlTypeInfo sourceType,
        string path,
        ImmutableArray<XamlCompiledBindingPathSegment> pathSegments,
        XamlCompiledBindingMode mode,
        IMethodSymbol? bindBackMethod,
        TextSpan sourceSpan,
        ulong stableId,
        XamlCompiledBindingKind kind = XamlCompiledBindingKind.Property,
        IMethodSymbol? eventHandlerMethod = null,
        XamlCompiledBindingSourceKind sourceKind = XamlCompiledBindingSourceKind.Root,
        XamlCompiledBindingFunction? function = null)
        : base(sourceSpan, stableId)
    {
        Extension = extension ?? throw new ArgumentNullException(nameof(extension));
        SourceType = sourceType ?? throw new ArgumentNullException(nameof(sourceType));
        Path = path ?? string.Empty;
        PathSegments = pathSegments;
        Mode = mode;
        BindBackMethod = bindBackMethod;
        Kind = kind;
        EventHandlerMethod = eventHandlerMethod;
        SourceKind = sourceKind;
        Function = function;
    }

    public XamlBoundObject Extension { get; }
    public XamlTypeInfo SourceType { get; }
    public string Path { get; }
    public ImmutableArray<XamlCompiledBindingPathSegment> PathSegments { get; }
    public ITypeSymbol ValueType =>
        Function?.Method.ReturnType ??
        (PathSegments.IsEmpty ? SourceType.Symbol : PathSegments[PathSegments.Length - 1].ValueType);
    public XamlCompiledBindingMode Mode { get; }
    public IMethodSymbol? BindBackMethod { get; }
    public XamlCompiledBindingKind Kind { get; }
    public IMethodSymbol? EventHandlerMethod { get; }
    public XamlCompiledBindingSourceKind SourceKind { get; }
    public XamlCompiledBindingFunction? Function { get; }
    public bool CanWrite =>
        BindBackMethod != null ||
        (Function == null &&
         !PathSegments.IsEmpty &&
         PathSegments[PathSegments.Length - 1].CanWrite);
}

public sealed class XamlBoundMember
{
    public XamlBoundMember(
        XamlBoundMemberReference member,
        XamlMemberOrigin origin,
        ImmutableArray<XamlBoundValue> values,
        TextSpan sourceSpan,
        ulong stableId)
    {
        Member = member ?? throw new ArgumentNullException(nameof(member));
        Origin = origin;
        Values = values;
        SourceSpan = sourceSpan;
        StableId = stableId;
    }

    public XamlBoundMemberReference Member { get; }
    public XamlMemberOrigin Origin { get; }
    public ImmutableArray<XamlBoundValue> Values { get; }
    public TextSpan SourceSpan { get; }
    public ulong StableId { get; }
}

public sealed class XamlBoundObject : XamlBoundValue
{
    public XamlBoundObject(
        XamlBoundTypeReference type,
        ImmutableArray<XamlBoundMember> members,
        bool isRetrieved,
        bool isMarkupExtension,
        TextSpan sourceSpan,
        ulong stableId)
        : base(sourceSpan, stableId)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Members = members;
        IsRetrieved = isRetrieved;
        IsMarkupExtension = isMarkupExtension;
    }

    public XamlBoundTypeReference Type { get; }
    public ImmutableArray<XamlBoundMember> Members { get; }
    public bool IsRetrieved { get; }
    public bool IsMarkupExtension { get; }
}

public sealed class XamlBoundDocument
{
    public XamlBoundDocument(
        XamlInfosetDocument infoset,
        XamlBoundObject? root,
        ImmutableArray<Diagnostic> diagnostics,
        ImmutableArray<string> dictionaryKeyDirectiveAliases = default,
        XamlTypeInfo? rootClassType = null)
    {
        Infoset = infoset ?? throw new ArgumentNullException(nameof(infoset));
        Root = root;
        Diagnostics = diagnostics;
        DictionaryKeyDirectiveAliases = dictionaryKeyDirectiveAliases.IsDefault
            ? ImmutableArray<string>.Empty
            : dictionaryKeyDirectiveAliases;
        RootClassType = rootClassType;
        DataTypeContexts = new XamlDataTypeContextGraphBuilder().Build(this);
        SerializationPlans = new XamlSerializationPlanner().Build(this);
    }

    public XamlInfosetDocument Infoset { get; }
    public XamlBoundObject? Root { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    /// <summary>
    /// Framework-authorized language directives that are dictionary-key aliases. This is
    /// immutable semantic evidence captured during binding so later framework-neutral passes
    /// do not need the live framework profile.
    /// </summary>
    public ImmutableArray<string> DictionaryKeyDirectiveAliases { get; }
    /// <summary>The canonical Roslyn-backed type named by root x:Class, when present.</summary>
    public XamlTypeInfo? RootClassType { get; }
    public XamlDataTypeContextGraph DataTypeContexts { get; }
    public XamlSerializationPlanGraph SerializationPlans { get; }
    public bool HasErrors
    {
        get
        {
            foreach (var diagnostic in Diagnostics)
                if (diagnostic.Severity == DiagnosticSeverity.Error) return true;
            return false;
        }
    }
}
