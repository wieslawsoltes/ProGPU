using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Xaml.Infoset;

public enum XamlInfosetKind
{
    Object,
    Member,
    Text
}

public enum XamlMemberOrigin
{
    Attribute,
    MemberElement,
    ImplicitContent,
    MarkupExtensionArgument,
    Directive
}

/// <summary>
/// One framework-neutral conditional-XAML namespace predicate. The compiler preserves the
/// method and arguments as declarative data; a framework profile owns runtime evaluation.
/// </summary>
public sealed class XamlNamespaceCondition : IEquatable<XamlNamespaceCondition>
{
    private XamlNamespaceCondition(
        string originalNamespaceUri,
        string baseNamespaceUri,
        string method,
        ImmutableArray<string> arguments)
    {
        OriginalNamespaceUri = originalNamespaceUri ?? throw new ArgumentNullException(nameof(originalNamespaceUri));
        BaseNamespaceUri = baseNamespaceUri ?? throw new ArgumentNullException(nameof(baseNamespaceUri));
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Arguments = arguments.IsDefault ? ImmutableArray<string>.Empty : arguments;
    }

    public string OriginalNamespaceUri { get; }
    public string BaseNamespaceUri { get; }
    public string Method { get; }
    public ImmutableArray<string> Arguments { get; }

    public static bool TryParse(string namespaceUri, out XamlNamespaceCondition? condition)
    {
        condition = null;
        if (string.IsNullOrEmpty(namespaceUri)) return false;
        var question = namespaceUri.LastIndexOf('?');
        if (question <= 0 || question == namespaceUri.Length - 1) return false;
        var open = namespaceUri.IndexOf('(', question + 1);
        if (open <= question + 1 ||
            namespaceUri[namespaceUri.Length - 1] != ')' ||
            namespaceUri.IndexOf(')', open + 1) != namespaceUri.Length - 1)
            return false;

        var method = namespaceUri.Substring(question + 1, open - question - 1);
        if (!IsIdentifier(method)) return false;
        var argumentText = namespaceUri.Substring(open + 1, namespaceUri.Length - open - 2);
        var arguments = ImmutableArray.CreateBuilder<string>();
        if (argumentText.Length != 0)
        {
            var start = 0;
            while (start <= argumentText.Length)
            {
                var comma = argumentText.IndexOf(',', start);
                var end = comma < 0 ? argumentText.Length : comma;
                var argument = argumentText.Substring(start, end - start).Trim();
                if (argument.Length == 0) return false;
                arguments.Add(argument);
                if (comma < 0) break;
                start = comma + 1;
            }
        }

        condition = new XamlNamespaceCondition(
            namespaceUri,
            namespaceUri.Substring(0, question),
            method,
            arguments.ToImmutable());
        return true;
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_')) return false;
        for (var index = 1; index < value.Length; index++)
            if (!(char.IsLetterOrDigit(value[index]) || value[index] == '_')) return false;
        return true;
    }

    public bool Equals(XamlNamespaceCondition? other) =>
        other != null &&
        string.Equals(OriginalNamespaceUri, other.OriginalNamespaceUri, StringComparison.Ordinal);
    public override bool Equals(object? obj) => Equals(obj as XamlNamespaceCondition);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(OriginalNamespaceUri);
    public override string ToString() => OriginalNamespaceUri;
}

public readonly struct XamlQualifiedName : IEquatable<XamlQualifiedName>
{
    public XamlQualifiedName(
        string namespaceUri,
        string localName,
        string prefix = "",
        XamlNamespaceCondition? condition = null)
    {
        NamespaceUri = namespaceUri ?? string.Empty;
        LocalName = localName ?? throw new ArgumentNullException(nameof(localName));
        Prefix = prefix ?? string.Empty;
        Condition = condition;
    }

    public string NamespaceUri { get; }
    public string LocalName { get; }
    public string Prefix { get; }
    public XamlNamespaceCondition? Condition { get; }
    public string DisplayName => Prefix.Length == 0 ? LocalName : Prefix + ":" + LocalName;
    public bool Equals(XamlQualifiedName other) =>
        string.Equals(NamespaceUri, other.NamespaceUri, StringComparison.Ordinal) &&
        string.Equals(LocalName, other.LocalName, StringComparison.Ordinal) &&
        Equals(Condition, other.Condition);
    public override bool Equals(object? obj) => obj is XamlQualifiedName other && Equals(other);
    public override int GetHashCode() => unchecked(
        ((StringComparer.Ordinal.GetHashCode(NamespaceUri) * 397) ^
         StringComparer.Ordinal.GetHashCode(LocalName)) * 397 ^
        (Condition?.GetHashCode() ?? 0));
    public override string ToString() => DisplayName;
}

public readonly struct XamlInfosetMemberName : IEquatable<XamlInfosetMemberName>
{
    public XamlInfosetMemberName(
        string namespaceUri,
        string localName,
        string prefix = "",
        string? ownerTypeName = null,
        bool isDirective = false,
        bool isImplicit = false,
        XamlNamespaceCondition? condition = null)
    {
        NamespaceUri = namespaceUri ?? string.Empty;
        LocalName = localName ?? throw new ArgumentNullException(nameof(localName));
        Prefix = prefix ?? string.Empty;
        OwnerTypeName = ownerTypeName;
        IsDirective = isDirective;
        IsImplicit = isImplicit;
        Condition = condition;
    }

    public string NamespaceUri { get; }
    public string LocalName { get; }
    public string Prefix { get; }
    public string? OwnerTypeName { get; }
    public bool IsDirective { get; }
    public bool IsImplicit { get; }
    public XamlNamespaceCondition? Condition { get; }
    public string DisplayName => OwnerTypeName == null
        ? (Prefix.Length == 0 ? LocalName : Prefix + ":" + LocalName)
        : (Prefix.Length == 0 ? OwnerTypeName + "." + LocalName : Prefix + ":" + OwnerTypeName + "." + LocalName);

    public bool Equals(XamlInfosetMemberName other) =>
        string.Equals(NamespaceUri, other.NamespaceUri, StringComparison.Ordinal) &&
        string.Equals(LocalName, other.LocalName, StringComparison.Ordinal) &&
        string.Equals(OwnerTypeName, other.OwnerTypeName, StringComparison.Ordinal) &&
        IsDirective == other.IsDirective && IsImplicit == other.IsImplicit &&
        Equals(Condition, other.Condition);
    public override bool Equals(object? obj) => obj is XamlInfosetMemberName other && Equals(other);
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(NamespaceUri);
            hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(LocalName);
            hash = (hash * 397) ^ (OwnerTypeName == null ? 0 : StringComparer.Ordinal.GetHashCode(OwnerTypeName));
            hash = (hash * 397) ^ IsDirective.GetHashCode();
            hash = (hash * 397) ^ IsImplicit.GetHashCode();
            return (hash * 397) ^ (Condition?.GetHashCode() ?? 0);
        }
    }
    public override string ToString() => DisplayName;
}

public readonly struct XamlNamespaceMapping : IEquatable<XamlNamespaceMapping>
{
    public XamlNamespaceMapping(string prefix, string namespaceUri, TextSpan declarationSpan = default)
    {
        Prefix = prefix ?? string.Empty;
        NamespaceUri = namespaceUri ?? string.Empty;
        DeclarationSpan = declarationSpan;
    }

    public string Prefix { get; }
    public string NamespaceUri { get; }
    public TextSpan DeclarationSpan { get; }
    public bool Equals(XamlNamespaceMapping other) =>
        string.Equals(Prefix, other.Prefix, StringComparison.Ordinal) &&
        string.Equals(NamespaceUri, other.NamespaceUri, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is XamlNamespaceMapping other && Equals(other);
    public override int GetHashCode() => unchecked(
        (StringComparer.Ordinal.GetHashCode(Prefix) * 397) ^ StringComparer.Ordinal.GetHashCode(NamespaceUri));
}

internal abstract class GreenXamlInfosetValue
{
    protected GreenXamlInfosetValue(XamlInfosetKind kind, TextSpan sourceSpan, ulong stableId)
    {
        Kind = kind;
        SourceSpan = sourceSpan;
        StableId = stableId;
    }

    public XamlInfosetKind Kind { get; }
    public TextSpan SourceSpan { get; }
    public ulong StableId { get; }
}

internal sealed class GreenXamlInfosetText : GreenXamlInfosetValue
{
    public GreenXamlInfosetText(string text, TextSpan sourceSpan, ulong stableId, bool isCData)
        : base(XamlInfosetKind.Text, sourceSpan, stableId)
    {
        Text = text;
        IsCData = isCData;
    }

    public string Text { get; }
    public bool IsCData { get; }
}

internal sealed class GreenXamlInfosetMember : GreenXamlInfosetValue
{
    public GreenXamlInfosetMember(
        XamlInfosetMemberName name,
        XamlMemberOrigin origin,
        ImmutableArray<GreenXamlInfosetValue> values,
        ImmutableArray<XamlNamespaceMapping> namespaceMappings,
        TextSpan sourceSpan,
        ulong stableId)
        : base(XamlInfosetKind.Member, sourceSpan, stableId)
    {
        Name = name;
        Origin = origin;
        Values = values;
        NamespaceMappings = namespaceMappings;
    }

    public XamlInfosetMemberName Name { get; }
    public XamlMemberOrigin Origin { get; }
    public ImmutableArray<GreenXamlInfosetValue> Values { get; }
    public ImmutableArray<XamlNamespaceMapping> NamespaceMappings { get; }
}

internal sealed class GreenXamlInfosetObject : GreenXamlInfosetValue
{
    public GreenXamlInfosetObject(
        XamlQualifiedName typeName,
        ImmutableArray<GreenXamlInfosetMember> members,
        ImmutableArray<XamlNamespaceMapping> namespaceMappings,
        bool isRetrieved,
        bool isMarkupExtension,
        TextSpan sourceSpan,
        ulong stableId,
        string? markupText = null)
        : base(XamlInfosetKind.Object, sourceSpan, stableId)
    {
        TypeName = typeName;
        Members = members;
        NamespaceMappings = namespaceMappings;
        IsRetrieved = isRetrieved;
        IsMarkupExtension = isMarkupExtension;
        MarkupText = markupText;
    }

    public XamlQualifiedName TypeName { get; }
    public ImmutableArray<GreenXamlInfosetMember> Members { get; }
    public ImmutableArray<XamlNamespaceMapping> NamespaceMappings { get; }
    public bool IsRetrieved { get; }
    public bool IsMarkupExtension { get; }
    public string? MarkupText { get; }
}

public abstract class XamlInfosetValue
{
    internal XamlInfosetValue(GreenXamlInfosetValue green, XamlInfosetDocument document)
    {
        Green = green;
        Document = document;
    }

    internal GreenXamlInfosetValue Green { get; }
    public XamlInfosetDocument Document { get; }
    public XamlInfosetKind Kind => Green.Kind;
    public TextSpan SourceSpan => Green.SourceSpan;
    public ulong StableId => Green.StableId;
    public Location GetLocation() => Document.GetLocation(SourceSpan);
}

public sealed class XamlInfosetText : XamlInfosetValue
{
    internal XamlInfosetText(GreenXamlInfosetText green, XamlInfosetDocument document, XamlInfosetMember parentMember)
        : base(green, document)
    {
        TextGreen = green;
        ParentMember = parentMember;
    }

    private GreenXamlInfosetText TextGreen { get; }
    public XamlInfosetMember ParentMember { get; }
    public string Text => TextGreen.Text;
    public bool IsCData => TextGreen.IsCData;
}

public sealed class XamlInfosetMember : XamlInfosetValue
{
    private ImmutableArray<XamlInfosetValue> _values;

    internal XamlInfosetMember(GreenXamlInfosetMember green, XamlInfosetDocument document, XamlInfosetObject parentObject)
        : base(green, document)
    {
        MemberGreen = green;
        ParentObject = parentObject;
    }

    private GreenXamlInfosetMember MemberGreen { get; }
    public XamlInfosetObject ParentObject { get; }
    public XamlInfosetMemberName Name => MemberGreen.Name;
    public XamlMemberOrigin Origin => MemberGreen.Origin;
    public ImmutableArray<XamlNamespaceMapping> NamespaceMappings => MemberGreen.NamespaceMappings;
    public ImmutableArray<XamlInfosetValue> Values
    {
        get
        {
            if (_values.IsDefault)
            {
                var builder = ImmutableArray.CreateBuilder<XamlInfosetValue>(MemberGreen.Values.Length);
                foreach (var value in MemberGreen.Values)
                {
                    builder.Add(value is GreenXamlInfosetObject childObject
                        ? new XamlInfosetObject(childObject, Document, this)
                        : new XamlInfosetText((GreenXamlInfosetText)value, Document, this));
                }
                ImmutableInterlocked.InterlockedInitialize(ref _values, builder.MoveToImmutable());
            }
            return _values;
        }
    }
}

public sealed class XamlInfosetObject : XamlInfosetValue
{
    private ImmutableArray<XamlInfosetMember> _members;

    internal XamlInfosetObject(
        GreenXamlInfosetObject green,
        XamlInfosetDocument document,
        XamlInfosetMember? parentMember)
        : base(green, document)
    {
        ObjectGreen = green;
        ParentMember = parentMember;
    }

    private GreenXamlInfosetObject ObjectGreen { get; }
    public XamlQualifiedName TypeName => ObjectGreen.TypeName;
    public XamlInfosetMember? ParentMember { get; }
    public bool IsRetrieved => ObjectGreen.IsRetrieved;
    public bool IsMarkupExtension => ObjectGreen.IsMarkupExtension;
    /// <summary>
    /// The decoded source attribute value for a root markup-extension object. Nested
    /// markup objects are projected from this value and do not retain duplicate text.
    /// </summary>
    public string? MarkupText => ObjectGreen.MarkupText;
    public ImmutableArray<XamlNamespaceMapping> NamespaceMappings => ObjectGreen.NamespaceMappings;
    public ImmutableArray<XamlInfosetMember> Members
    {
        get
        {
            if (_members.IsDefault)
            {
                var builder = ImmutableArray.CreateBuilder<XamlInfosetMember>(ObjectGreen.Members.Length);
                foreach (var member in ObjectGreen.Members)
                    builder.Add(new XamlInfosetMember(member, Document, this));
                ImmutableInterlocked.InterlockedInitialize(ref _members, builder.MoveToImmutable());
            }
            return _members;
        }
    }

    public XamlInfosetMember? FindMember(string namespaceUri, string localName)
    {
        foreach (var member in Members)
        {
            if (string.Equals(member.Name.NamespaceUri, namespaceUri, StringComparison.Ordinal) &&
                string.Equals(member.Name.LocalName, localName, StringComparison.Ordinal)) return member;
        }
        return null;
    }
}

public sealed class XamlInfosetDocument
{
    private XamlInfosetObject? _root;

    internal XamlInfosetDocument(
        XamlDocumentSyntax syntax,
        GreenXamlInfosetObject? greenRoot,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Syntax = syntax;
        GreenRoot = greenRoot;
        Diagnostics = diagnostics;
    }

    private GreenXamlInfosetObject? GreenRoot { get; }
    public XamlDocumentSyntax Syntax { get; }
    public SourceText SourceText => Syntax.SyntaxTree.GetText();
    public string Path => Syntax.Path;
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public bool HasErrors
    {
        get
        {
            foreach (var diagnostic in Diagnostics)
                if (diagnostic.Severity == DiagnosticSeverity.Error) return true;
            return false;
        }
    }
    public XamlInfosetObject? Root => GreenRoot == null
        ? null
        : LazyInitializer.EnsureInitialized(ref _root, () => new XamlInfosetObject(GreenRoot, this, null));
    public Location GetLocation(TextSpan span) => Location.Create(
        Path,
        span,
        SourceText.Lines.GetLinePositionSpan(span));
}
