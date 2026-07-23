using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Diagnostics;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Xaml.Infoset;

public sealed class XamlInfosetConversionOptions
{
    public XamlParseMode Mode { get; set; } = XamlParseMode.Strict;
    public int MaximumNodes { get; set; } = 2 * 1024 * 1024;
    public int MaximumDiagnostics { get; set; } = 1024;
    public XamlMarkupParseOptions MarkupOptions { get; set; } = new XamlMarkupParseOptions();
}

/// <summary>
/// Converts lossless XML syntax to the schema-neutral XAML information set. Schema aliases,
/// member existence, assignability, and content-property selection belong to the binder.
/// </summary>
public sealed class XamlInfosetConverter
{
    private readonly XamlMarkupExtensionParser _markupParser = new XamlMarkupExtensionParser();
    private XamlDocumentSyntax _syntax = null!;
    private XamlInfosetConversionOptions _options = null!;
    private CancellationToken _cancellationToken;
    private ImmutableArray<Diagnostic>.Builder _diagnostics = null!;
    private int _nodeCount;

    public XamlInfosetDocument Convert(
        XamlDocumentSyntax syntax,
        XamlInfosetConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));
        _options = options ?? new XamlInfosetConversionOptions();
        _cancellationToken = cancellationToken;
        _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        _nodeCount = 0;

        GreenXamlInfosetObject? root = null;
        if (syntax.Root != null && !(_options.Mode == XamlParseMode.Strict && syntax.HasErrors))
        {
            var namespaces = new Dictionary<string, XamlNamespaceMapping>(StringComparer.Ordinal)
            {
                ["xml"] = new XamlNamespaceMapping("xml", XamlNamespaces.Xml)
            };
            root = ConvertObject(syntax.Root, namespaces, isRoot: true);
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>(syntax.Diagnostics.Length + _diagnostics.Count);
        diagnostics.AddRange(syntax.Diagnostics);
        diagnostics.AddRange(_diagnostics);
        if (root != null)
        {
            var provisional = new XamlInfosetDocument(syntax, root, diagnostics.ToImmutable());
            diagnostics.AddRange(new XamlInfosetStructuralValidator().Validate(
                provisional,
                new XamlInfosetValidationOptions { MaximumDiagnostics = _options.MaximumDiagnostics }));
        }
        if (_options.Mode == XamlParseMode.Strict && ContainsErrors(diagnostics)) root = null;
        return new XamlInfosetDocument(syntax, root, diagnostics.ToImmutable());
    }

    private GreenXamlInfosetObject? ConvertObject(
        XamlObjectSyntax syntax,
        IReadOnlyDictionary<string, XamlNamespaceMapping> inheritedNamespaces,
        bool isRoot)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!ReserveNode(syntax.Span)) return null;

        var namespaceMap = MergeNamespaces(inheritedNamespaces, syntax.NamespaceDeclarations);
        var namespaceSnapshot = Snapshot(namespaceMap);
        var typeName = CreateQualifiedName(syntax.NamespaceUri, syntax.LocalName, syntax.Prefix);
        var members = new List<GreenXamlInfosetMember>();

        foreach (var attribute in syntax.Attributes)
        {
            var name = CreateAttributeMemberName(attribute, typeName);
            var value = ConvertAttributeValue(attribute, namespaceMap);
            var member = new GreenXamlInfosetMember(
                name,
                name.IsDirective ? XamlMemberOrigin.Directive : XamlMemberOrigin.Attribute,
                value == null
                    ? ImmutableArray<GreenXamlInfosetValue>.Empty
                    : ImmutableArray.Create(value),
                namespaceSnapshot,
                attribute.Span,
                CombineStableId(syntax.StableId, "attribute", name.DisplayName, members.Count));
            members.Add(member);
        }

        var contentValues = new List<GreenXamlInfosetValue>();
        var contentInsertIndex = -1;
        TextSpan contentSpan = default;
        foreach (var child in syntax.Children)
        {
            if (child is XamlObjectSyntax childObject && IsMemberElement(childObject))
            {
                var member = ConvertMemberElement(childObject, syntax, namespaceMap);
                if (member != null) members.Add(member);
                // Formatting whitespace before a property element must not decide where the
                // aggregated implicit-content member is inserted. The infoset has one member
                // per logical XAML member, so use the first semantically significant content
                // value as its source-order anchor.
                if (contentValues.All(IsFormattingWhitespace))
                {
                    contentInsertIndex = -1;
                    contentSpan = default;
                }
                continue;
            }

            GreenXamlInfosetValue? value = child switch
            {
                XamlObjectSyntax objectValue => ConvertObject(objectValue, namespaceMap, isRoot: false),
                XamlTextSyntax text => ConvertText(text, namespaceMap),
                _ => null
            };
            if (value == null) continue;
            if (contentInsertIndex < 0)
            {
                contentInsertIndex = members.Count;
                contentSpan = value.SourceSpan;
            }
            else
            {
                contentSpan = TextSpan.FromBounds(contentSpan.Start, value.SourceSpan.End);
            }
            contentValues.Add(value);
        }

        if (contentValues.Count != 0)
        {
            var usesInitialization = XamlIntrinsicSchema.UsesInitializationText(typeName.NamespaceUri, typeName.LocalName);
            var implicitName = usesInitialization
                ? new XamlInfosetMemberName(
                    XamlNamespaces.Language2006,
                    "Initialization",
                    "x",
                    isDirective: true)
                : new XamlInfosetMemberName(
                    typeName.NamespaceUri,
                    "$content",
                    isImplicit: true);
            var contentMember = new GreenXamlInfosetMember(
                implicitName,
                XamlMemberOrigin.ImplicitContent,
                contentValues.ToImmutableArray(),
                namespaceSnapshot,
                contentSpan,
                CombineStableId(syntax.StableId, "content", "$content", contentInsertIndex));
            members.Insert(contentInsertIndex, contentMember);
        }

        return new GreenXamlInfosetObject(
            typeName,
            members.ToImmutableArray(),
            namespaceSnapshot,
            isRetrieved: false,
            isMarkupExtension: false,
            syntax.Span,
            syntax.StableId);
    }

    private static bool IsFormattingWhitespace(GreenXamlInfosetValue value) =>
        value is GreenXamlInfosetText text &&
        !text.IsCData &&
        string.IsNullOrWhiteSpace(text.Text);

    private GreenXamlInfosetMember? ConvertMemberElement(
        XamlObjectSyntax memberSyntax,
        XamlObjectSyntax parentSyntax,
        IReadOnlyDictionary<string, XamlNamespaceMapping> namespaceMap)
    {
        if (!ReserveNode(memberSyntax.Span)) return null;
        SplitDottedName(memberSyntax.LocalName, out var ownerName, out var memberName);
        var requestedNamespace = memberSyntax.NamespaceUri.Length == 0
            ? parentSyntax.NamespaceUri
            : memberSyntax.NamespaceUri;
        var namespaceUri = NormalizeNamespace(requestedNamespace, out var condition);
        var name = new XamlInfosetMemberName(
            namespaceUri,
            memberName,
            memberSyntax.Prefix,
            ownerName,
            isDirective: XamlIntrinsicSchema.TryGetDirective(
                namespaceUri,
                memberName,
                out _),
            condition: condition);
        var memberNamespaces = MergeNamespaces(namespaceMap, memberSyntax.NamespaceDeclarations);
        var values = ImmutableArray.CreateBuilder<GreenXamlInfosetValue>();
        foreach (var child in memberSyntax.Children)
        {
            if (child is XamlObjectSyntax nested && IsMemberElement(nested))
            {
                AddDiagnostic("PGXAML1203", "A member element cannot directly contain another member element.",
                    nested.Span, "8.6.5");
                continue;
            }
            if (child is XamlObjectSyntax childObject)
            {
                var value = ConvertObject(childObject, memberNamespaces, isRoot: false);
                if (value != null) values.Add(value);
            }
            else if (child is XamlTextSyntax text)
            {
                var value = ConvertText(text, memberNamespaces);
                if (value != null) values.Add(value);
            }
        }

        if (memberSyntax.Attributes.Length != 0)
        {
            AddDiagnostic("PGXAML1205", "Member elements cannot declare ordinary XAML members.",
                memberSyntax.Attributes[0].Span, "8.6.5");
        }
        return new GreenXamlInfosetMember(
            name,
            XamlMemberOrigin.MemberElement,
            values.ToImmutable(),
            Snapshot(memberNamespaces),
            memberSyntax.Span,
            memberSyntax.StableId);
    }

    private GreenXamlInfosetValue? ConvertAttributeValue(
        XamlAttributeSyntax attribute,
        IReadOnlyDictionary<string, XamlNamespaceMapping> namespaceMap)
    {
        var source = _syntax.SyntaxTree.GetText();
        if (attribute.Value.StartsWith("{}", StringComparison.Ordinal))
        {
            return new GreenXamlInfosetText(
                attribute.Value.Substring(2),
                attribute.ValueSpan.Length >= 2
                    ? new TextSpan(attribute.ValueSpan.Start + 2, attribute.ValueSpan.Length - 2)
                    : attribute.ValueSpan,
                attribute.StableId,
                isCData: false);
        }

        var markupOptions = CreateAttributeMarkupOptions(_options.MarkupOptions);
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < attribute.Value.Length &&
               char.IsWhiteSpace(attribute.Value[firstNonWhitespace]))
            firstNonWhitespace++;
        var mayContainMarkup =
            firstNonWhitespace < attribute.Value.Length &&
            (attribute.Value[firstNonWhitespace] == '{' ||
             markupOptions.SyntaxLanguage.CanStartWith(attribute.Value[firstNonWhitespace]));
        if (mayContainMarkup)
        {
            if (_markupParser.TryParse(
                    attribute.Value,
                    _syntax.Path,
                    source,
                    attribute.Span,
                    out var extension,
                    out var diagnostic,
                    markupOptions))
            {
                return ConvertMarkupExtension(
                    extension!,
                    namespaceMap,
                    attribute.StableId,
                    attribute.ValueSpan,
                    attribute.Value);
            }
            if (diagnostic != null) _diagnostics.Add(diagnostic);
        }

        return new GreenXamlInfosetText(
            attribute.Value,
            attribute.ValueSpan,
            attribute.StableId,
            isCData: false);
    }

    private static XamlMarkupParseOptions CreateAttributeMarkupOptions(
        XamlMarkupParseOptions template)
    {
        if (template == null) throw new ArgumentNullException(nameof(template));
        return new XamlMarkupParseOptions
        {
            MaximumDepth = template.MaximumDepth,
            MaximumTokens = template.MaximumTokens,
            MaximumTokenLength = template.MaximumTokenLength,
            MaximumArguments = template.MaximumArguments,
            MaximumDiagnostics = template.MaximumDiagnostics,
            BracketPairs = template.BracketPairs,
            BracketPairResolver = template.BracketPairResolver,
            TokenRecognizers = template.TokenRecognizers,
            SyntaxLanguage = template.SyntaxLanguage,
            Context = XamlMarkupSyntaxContexts.AttributeValue
        };
    }

    private GreenXamlInfosetObject ConvertMarkupExtension(
        XamlMarkupExtension extension,
        IReadOnlyDictionary<string, XamlNamespaceMapping> namespaceMap,
        ulong stableId,
        TextSpan sourceValueSpan,
        string? markupText = null)
    {
        SplitQualifiedName(extension.Name, out var prefix, out var localName);
        var requestedNamespace = namespaceMap.TryGetValue(prefix, out var mapping)
            ? mapping.NamespaceUri
            : string.Empty;
        var namespaceUri = NormalizeNamespace(requestedNamespace, out var condition);
        var members = ImmutableArray.CreateBuilder<GreenXamlInfosetMember>();
        if (extension.PositionalArguments.Count != 0)
        {
            var values = ImmutableArray.CreateBuilder<GreenXamlInfosetValue>(extension.PositionalArguments.Count);
            for (var index = 0; index < extension.PositionalArguments.Count; index++)
                values.Add(ConvertMarkupValue(extension.PositionalArguments[index], namespaceMap, stableId, index, sourceValueSpan));
            members.Add(new GreenXamlInfosetMember(
                new XamlInfosetMemberName(XamlNamespaces.Language2006, "PositionalParameters", "x", isDirective: true),
                XamlMemberOrigin.MarkupExtensionArgument,
                values.ToImmutable(),
                Snapshot(namespaceMap),
                TranslateSpan(extension.Span, sourceValueSpan),
                CombineStableId(stableId, "markup", "PositionalParameters", 0)));
        }
        for (var index = 0; index < extension.NamedArguments.Count; index++)
        {
            var argument = extension.NamedArguments[index];
            members.Add(new GreenXamlInfosetMember(
                new XamlInfosetMemberName(namespaceUri, argument.Name),
                XamlMemberOrigin.MarkupExtensionArgument,
                ImmutableArray.Create(ConvertMarkupValue(argument.Value, namespaceMap, stableId, index, sourceValueSpan)),
                Snapshot(namespaceMap),
                TranslateSpan(argument.Span, sourceValueSpan),
                CombineStableId(stableId, "markup", argument.Name, index + 1)));
        }
        return new GreenXamlInfosetObject(
            new XamlQualifiedName(namespaceUri, localName, prefix, condition),
            members.ToImmutable(),
            Snapshot(namespaceMap),
            isRetrieved: false,
            isMarkupExtension: true,
            TranslateSpan(extension.Span, sourceValueSpan),
            stableId,
            markupText);
    }

    private GreenXamlInfosetValue ConvertMarkupValue(
        XamlMarkupValue value,
        IReadOnlyDictionary<string, XamlNamespaceMapping> namespaceMap,
        ulong parentStableId,
        int index,
        TextSpan sourceValueSpan) => value switch
        {
            XamlMarkupExtensionValue nested => ConvertMarkupExtension(
                nested.Extension,
                namespaceMap,
                CombineStableId(parentStableId, "markup-object", nested.Extension.Name, index),
                sourceValueSpan),
            XamlMarkupTextValue text => new GreenXamlInfosetText(
                text.Text,
                TranslateSpan(text.Span, sourceValueSpan),
                CombineStableId(parentStableId, "markup-text", string.Empty, index),
                isCData: false),
            _ => throw new InvalidOperationException("Unknown markup value kind.")
        };

    /// <summary>
    /// Projects a schema-aware reparse into the original document while retaining source
    /// locations, namespace scope, and the root stable identity.
    /// </summary>
    public static XamlInfosetObject ProjectMarkupExtension(
        XamlInfosetObject original,
        XamlMarkupExtension extension)
    {
        if (original == null) throw new ArgumentNullException(nameof(original));
        if (extension == null) throw new ArgumentNullException(nameof(extension));
        if (!original.IsMarkupExtension)
            throw new ArgumentException("Only markup-extension infoset objects can be re-projected.", nameof(original));

        var namespaceMap = new Dictionary<string, XamlNamespaceMapping>(StringComparer.Ordinal);
        foreach (var mapping in original.NamespaceMappings)
            namespaceMap[mapping.Prefix] = mapping;
        var green = new XamlInfosetConverter().ConvertMarkupExtension(
            extension,
            namespaceMap,
            original.StableId,
            original.SourceSpan);
        return new XamlInfosetObject(
            green,
            original.Document,
            original.ParentMember);
    }

    private static TextSpan TranslateSpan(TextSpan relativeSpan, TextSpan sourceValueSpan)
    {
        var relativeStart = Math.Max(0, Math.Min(relativeSpan.Start, sourceValueSpan.Length));
        var relativeEnd = Math.Max(relativeStart, Math.Min(relativeSpan.End, sourceValueSpan.Length));
        return TextSpan.FromBounds(sourceValueSpan.Start + relativeStart, sourceValueSpan.Start + relativeEnd);
    }

    private GreenXamlInfosetText? ConvertText(
        XamlTextSyntax text,
        IReadOnlyDictionary<string, XamlNamespaceMapping> namespaceMap)
    {
        if (!ReserveNode(text.Span)) return null;
        return new GreenXamlInfosetText(text.Text, text.Span, text.StableId, text.IsCData);
    }

    private XamlInfosetMemberName CreateAttributeMemberName(
        XamlAttributeSyntax attribute,
        XamlQualifiedName containingType)
    {
        var namespaceUri = attribute.NamespaceUri.Length == 0
            ? containingType.NamespaceUri
            : attribute.NamespaceUri;
        XamlNamespaceCondition? condition = null;
        if (attribute.NamespaceUri.Length != 0)
            namespaceUri = NormalizeNamespace(namespaceUri, out condition);
        SplitDottedName(attribute.LocalName, out var ownerName, out var memberName);
        // XML default namespaces do not apply to unprefixed attributes. Conversion projects
        // those attributes into the owner type's XAML namespace, but they remain ordinary
        // members (for example x:Array.Type), not language directives.
        var isDirective = attribute.Prefix.Length != 0 &&
            XamlIntrinsicSchema.TryGetDirective(namespaceUri, memberName, out _);
        return new XamlInfosetMemberName(
            namespaceUri,
            memberName,
            attribute.Prefix,
            ownerName,
            isDirective: isDirective,
            condition: condition);
    }

    private static XamlQualifiedName CreateQualifiedName(
        string namespaceUri,
        string localName,
        string prefix)
    {
        var normalized = NormalizeNamespace(namespaceUri, out var condition);
        return new XamlQualifiedName(normalized, localName, prefix, condition);
    }

    private static string NormalizeNamespace(
        string namespaceUri,
        out XamlNamespaceCondition? condition)
    {
        if (XamlNamespaceCondition.TryParse(namespaceUri, out condition))
            return condition!.BaseNamespaceUri;
        condition = null;
        return namespaceUri;
    }

    private static bool IsMemberElement(XamlObjectSyntax syntax)
    {
        var dot = syntax.LocalName.IndexOf('.');
        return (dot > 0 && dot < syntax.LocalName.Length - 1) ||
               XamlIntrinsicSchema.TryGetDirective(syntax.NamespaceUri, syntax.LocalName, out _);
    }

    private static void SplitDottedName(string name, out string? ownerName, out string memberName)
    {
        var dot = name.IndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            ownerName = null;
            memberName = name;
            return;
        }
        ownerName = name.Substring(0, dot);
        memberName = name.Substring(dot + 1);
    }

    private static void SplitQualifiedName(string name, out string prefix, out string localName)
    {
        var colon = name.IndexOf(':');
        if (colon < 0)
        {
            prefix = string.Empty;
            localName = name;
        }
        else
        {
            prefix = name.Substring(0, colon);
            localName = name.Substring(colon + 1);
        }
    }

    private static Dictionary<string, XamlNamespaceMapping> MergeNamespaces(
        IReadOnlyDictionary<string, XamlNamespaceMapping> inherited,
        ImmutableArray<XamlNamespaceDeclaration> declarations)
    {
        var result = new Dictionary<string, XamlNamespaceMapping>(StringComparer.Ordinal);
        foreach (var pair in inherited) result[pair.Key] = pair.Value;
        foreach (var declaration in declarations)
            result[declaration.Prefix] = new XamlNamespaceMapping(
                declaration.Prefix,
                declaration.NamespaceUri,
                declaration.Span);
        return result;
    }

    private static ImmutableArray<XamlNamespaceMapping> Snapshot(
        IReadOnlyDictionary<string, XamlNamespaceMapping> mappings) => mappings.Values
            .OrderBy(mapping => mapping.Prefix, StringComparer.Ordinal)
            .ToImmutableArray();

    private bool ReserveNode(TextSpan span)
    {
        if (_nodeCount++ < _options.MaximumNodes) return true;
        AddDiagnostic("PGXAML1201", $"XAML infoset node count exceeds the configured limit of {_options.MaximumNodes}.",
            span, "8.6.1");
        return false;
    }

    private void AddDiagnostic(string id, string message, TextSpan span, string section)
    {
        if (_diagnostics.Count >= _options.MaximumDiagnostics) return;
        _diagnostics.Add(XamlDiagnostics.Create(
            id,
            DiagnosticSeverity.Error,
            message,
            _syntax.Path,
            _syntax.SyntaxTree.GetText(),
            span,
            section));
    }

    private static bool ContainsErrors(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            if (diagnostic.Severity == DiagnosticSeverity.Error) return true;
        return false;
    }

    private static ulong CombineStableId(ulong parent, string kind, string name, int index)
    {
        unchecked
        {
            var hash = parent == 0 ? 14695981039346656037UL : parent;
            hash = Add(hash, kind);
            hash = Add(hash, name);
            return Add(hash, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static ulong Add(ulong hash, string value)
    {
        unchecked
        {
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }
}
