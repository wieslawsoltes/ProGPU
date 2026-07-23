using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Lowering;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Xaml.Roslyn;

/// <summary>
/// Roslyn-specific expression factory implemented by framework plugins. The compiler owns
/// statement and declaration generation; plugins only lower framework value syntax.
/// </summary>
public interface IRoslynXamlFrameworkProfile : IXamlFrameworkProfile
{
    bool TryCreateLiteralExpression(XamlTypeInfo targetType, string text, out ExpressionSyntax expression);

    bool TryCreateMarkupExtensionExpression(
        XamlMarkupExtension extension,
        XamlTypeInfo targetType,
        out ExpressionSyntax expression);

    bool TryCreateMarkupExtensionExpression(
        XamlIrObject extension,
        XamlTypeInfo targetType,
        out ExpressionSyntax expression);

    bool TryCreateResourceReferenceExpression(
        XamlIrResourceReference reference,
        XamlTypeInfo targetType,
        ExpressionSyntax lookupRoot,
        ExpressionSyntax resourceKey,
        out ExpressionSyntax expression);

}

/// <summary>
/// Optional structured markup-expression hook for framework extensions whose value depends on
/// the active compiled-XAML resource lookup root.
/// </summary>
public interface IRoslynXamlContextualMarkupExpressionProfile
{
    bool TryCreateMarkupExtensionExpression(
        XamlIrObject extension,
        XamlTypeInfo targetType,
        ExpressionSyntax lookupRoot,
        out ExpressionSyntax expression);
}

/// <summary>
/// Optional structured runtime lowering for a framework-owned conditional-XAML predicate.
/// The compiler owns the surrounding <c>if</c> statement and never emits C# as text.
/// </summary>
public interface IRoslynXamlConditionalNamespaceProfile
{
    bool TryCreateConditionalNamespaceExpression(
        XamlNamespaceCondition condition,
        out ExpressionSyntax expression);
}

/// <summary>
/// Optional structured replacement for a framework object whose declarative semantics map to
/// a different runtime representation.
/// </summary>
public interface IRoslynXamlObjectExpressionProfile
{
    bool TryCreateObjectExpression(
        XamlIrObject value,
        ExpressionSyntax lookupRoot,
        out ExpressionSyntax expression);
}

/// <summary>
/// Optional Roslyn emission contract for frameworks that publish classless compiled XAML
/// through a typed runtime resource registry.
/// </summary>
public interface IRoslynXamlCompiledResourceProfile
{
    bool TryCreateCompiledResourceRegistration(
        XamlCompiledResourceArtifact artifact,
        out StatementSyntax statement);
}

/// <summary>
/// Optional framework-owned lowering for resource values that must enter a property system
/// through an operation rather than through the statically typed CLR property setter.
/// </summary>
public interface IRoslynXamlResourceAssignmentProfile
{
    bool TryCreateResourceReferenceAssignment(
        XamlIrResourceReference reference,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax lookupRoot,
        ExpressionSyntax resourceKey,
        out StatementSyntax statement);
}

/// <summary>
/// Optional structured lowering contract for parser-owned deferred content. The core emitter
/// owns the factory body and the framework profile owns only the typed runtime publication call.
/// </summary>
public interface IRoslynXamlDeferredContentProfile
{
    bool TryCreateDeferredContentAssignment(
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ParenthesizedLambdaExpressionSyntax factory,
        out StatementSyntax statement);
}

/// <summary>
/// Optional structured publication of one compiler-owned XAML namescope. The compiler core
/// supplies the exact name/object expressions for one construction scope; the framework owns
/// only the runtime registration statements.
/// </summary>
public interface IRoslynXamlNameScopeProfile
{
    bool TryCreateNameScopeFinalization(
        ExpressionSyntax root,
        ImmutableArray<RoslynXamlNameRegistrationSyntax> registrations,
        out ImmutableArray<StatementSyntax> statements);
}

public sealed class RoslynXamlNameRegistrationSyntax
{
    public RoslynXamlNameRegistrationSyntax(
        string name,
        ExpressionSyntax expression)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A XAML name is required.", nameof(name))
            : name;
        Expression = expression ??
            throw new ArgumentNullException(nameof(expression));
    }

    public string Name { get; }
    public ExpressionSyntax Expression { get; }
}

/// <summary>
/// Optional structured lowering for markup extensions whose meaning is an operation on the
/// receiving member rather than a value assignable to that member's CLR type.
/// </summary>
public interface IRoslynXamlMarkupExtensionAssignmentProfile
{
    bool TryCreateMarkupExtensionAssignment(
        XamlIrObject extension,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax? context,
        XamlTypeInfo? contextType,
        ExpressionSyntax lookupRoot,
        ExpressionSyntax? deferredLifetimeOwner,
        out StatementSyntax statement,
        out bool usesDeferredLifetime);
}

/// <summary>
/// Structured lowering for canonical ordinary bindings. The profile receives the bound path
/// and its original runtime descriptor so it can publish a framework-native pre-tokenized plan.
/// </summary>
public interface IRoslynXamlOrdinaryBindingAssignmentProfile
{
    bool TryCreateBindingAssignment(
        XamlIrBinding binding,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax? context,
        XamlTypeInfo? contextType,
        ExpressionSyntax lookupRoot,
        ExpressionSyntax? deferredLifetimeOwner,
        out StatementSyntax statement,
        out bool usesDeferredLifetime);
}

/// <summary>
/// Optional structured lifecycle shared by markup-extension assignments emitted inside one
/// deferred-factory invocation. The compiler core owns aggregation and ordering; the framework
/// profile owns creation and publication of its per-materialization registration owner.
/// </summary>
public interface IRoslynXamlDeferredMarkupExtensionLifecycleProfile
{
    bool TryCreateDeferredMarkupExtensionLifecycle(
        ExpressionSyntax context,
        string ownerIdentifier,
        out RoslynXamlDeferredMarkupExtensionLifecycleSyntax lifecycle);

    bool TryCreateDeferredMarkupExtensionFinalization(
        ExpressionSyntax owner,
        ExpressionSyntax root,
        out ImmutableArray<StatementSyntax> statements);
}

/// <summary>
/// Optional structured publication of canonical ordinary-binding CLR accessors. The compiler
/// supplies exact Roslyn symbols; the framework profile owns only its runtime registry call.
/// </summary>
public interface IRoslynXamlBindingAccessorRegistrationProfile
{
    bool TryCreateBindingAccessorRegistration(
        XamlBindingPathAccessor accessor,
        out StatementSyntax statement);
}

public sealed class RoslynXamlDeferredMarkupExtensionLifecycleSyntax
{
    public RoslynXamlDeferredMarkupExtensionLifecycleSyntax(
        ExpressionSyntax registrationOwner,
        ImmutableArray<StatementSyntax> prepareStatements)
    {
        RegistrationOwner = registrationOwner ??
            throw new ArgumentNullException(nameof(registrationOwner));
        PrepareStatements = prepareStatements.IsDefault
            ? ImmutableArray<StatementSyntax>.Empty
            : prepareStatements;
    }

    public ExpressionSyntax RegistrationOwner { get; }
    public ImmutableArray<StatementSyntax> PrepareStatements { get; }
}

/// <summary>
/// Framework publication contract for a compiler-owned, Roslyn-symbol-bound binding path.
/// Implementations receive canonical IR and must emit structured Roslyn syntax only.
/// </summary>
public interface IRoslynXamlCompiledBindingAssignmentProfile
{
    bool TryCreateCompiledBindingAssignment(
        XamlIrCompiledBinding binding,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax source,
        ExpressionSyntax lookupRoot,
        ExpressionSyntax? bindingOwner,
        out StatementSyntax statement);
}

public interface IRoslynXamlCompiledBindingLifecycleProfile
{
    bool TryCreateCompiledBindingReset(
        ExpressionSyntax source,
        out StatementSyntax statement);

    bool TryCreateCompiledBindingLifecycle(
        ExpressionSyntax source,
        out RoslynXamlCompiledBindingLifecycleSyntax lifecycle);
}

/// <summary>
/// Optional structured lifecycle for compiled bindings emitted inside one deferred-factory
/// invocation. The core owns binding aggregation and ordering; the profile owns only the
/// framework runtime calls and returned registration-owner expression.
/// </summary>
public interface IRoslynXamlDeferredCompiledBindingLifecycleProfile
{
    bool TryCreateDeferredCompiledBindingLifecycle(
        ExpressionSyntax context,
        string controllerIdentifier,
        out RoslynXamlDeferredCompiledBindingLifecycleSyntax lifecycle);

    bool TryCreateDeferredCompiledBindingFinalization(
        ExpressionSyntax controller,
        ExpressionSyntax root,
        out ImmutableArray<StatementSyntax> statements);
}

public sealed class RoslynXamlCompiledBindingLifecycleSyntax
{
    public RoslynXamlCompiledBindingLifecycleSyntax(
        ImmutableArray<MemberDeclarationSyntax> members,
        ImmutableArray<StatementSyntax> prepareStatements,
        ImmutableArray<StatementSyntax> initializeStatements)
    {
        Members = members;
        PrepareStatements = prepareStatements;
        InitializeStatements = initializeStatements;
    }

    public ImmutableArray<MemberDeclarationSyntax> Members { get; }
    public ImmutableArray<StatementSyntax> PrepareStatements { get; }
    public ImmutableArray<StatementSyntax> InitializeStatements { get; }
}

public sealed class RoslynXamlDeferredCompiledBindingLifecycleSyntax
{
    public RoslynXamlDeferredCompiledBindingLifecycleSyntax(
        ExpressionSyntax registrationOwner,
        ImmutableArray<StatementSyntax> prepareStatements)
    {
        RegistrationOwner = registrationOwner ??
            throw new ArgumentNullException(nameof(registrationOwner));
        PrepareStatements = prepareStatements.IsDefault
            ? ImmutableArray<StatementSyntax>.Empty
            : prepareStatements;
    }

    public ExpressionSyntax RegistrationOwner { get; }
    public ImmutableArray<StatementSyntax> PrepareStatements { get; }
}

public interface IRoslynXamlCompiledEventBindingProfile
{
    bool TryCreateCompiledEventBinding(
        XamlIrCompiledBinding binding,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax source,
        out StatementSyntax statement);
}

/// <summary>
/// Optional structured lowering for members whose public metadata requires a binding markup
/// object to be assigned as a value rather than applied through the framework binding engine.
/// The core constructs and populates the exact extension type; the profile owns publication.
/// </summary>
public interface IRoslynXamlBindingAssignmentProfile
{
    bool TryCreateBindingObjectAssignment(
        XamlIrObject binding,
        XamlBindingAssignmentInfo assignment,
        XamlMemberInfo member,
        ExpressionSyntax bindingInstance,
        ExpressionSyntax receiver,
        out StatementSyntax statement);
}

/// <summary>
/// Framework-owned invocation of a CLR markup extension after the neutral emitter has
/// constructed and populated its canonical Roslyn type.
/// </summary>
public interface IRoslynXamlMarkupExtensionInvocationProfile
{
    bool TryCreateMarkupExtensionInvocation(
        XamlIrObject extension,
        ExpressionSyntax extensionInstance,
        XamlTypeInfo targetType,
        ExpressionSyntax? targetObject,
        XamlMemberInfo? targetMember,
        ExpressionSyntax rootObject,
        string? resourceUri,
        out ExpressionSyntax expression);
}

/// <summary>
/// Optional framework-owned structured lowering for switch-like markup extensions.
/// The core supplies canonical option/selector Roslyn symbols and a callback that lowers
/// branch values through the active emitter; the profile owns evaluation order, context
/// creation, fallback semantics, and whether compile-time trimming is enabled.
/// </summary>
public interface IRoslynXamlMarkupExtensionOptionProfile
{
    bool TryCreateMarkupExtensionOptionExpression(
        XamlIrObject extension,
        XamlMarkupExtensionOptionSelectorShapeInfo selector,
        XamlTypeInfo targetType,
        Func<XamlIrValue, XamlTypeInfo, ExpressionSyntax?> emitValue,
        ExpressionSyntax? targetObject,
        XamlMemberInfo? targetMember,
        ExpressionSyntax rootObject,
        string? resourceUri,
        out ExpressionSyntax expression);
}

/// <summary>
/// Optional structured object-writer interception contract. The neutral compiler resolves
/// the exact attributed callback and constructs ordinary markup-extension instances; the
/// framework profile owns its event-args/runtime protocol and handled/fallback behavior.
/// </summary>
public interface IRoslynXamlSetValueHandlerProfile
{
    bool TryCreateMarkupExtensionSetHandlerAssignment(
        XamlSetValueHandlerShapeInfo handler,
        XamlIrObject extension,
        ExpressionSyntax extensionInstance,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax rootObject,
        string? resourceUri,
        out StatementSyntax statement);

    bool TryCreateTypeConverterSetHandlerAssignment(
        XamlSetValueHandlerShapeInfo handler,
        XamlIrText value,
        INamedTypeSymbol converterType,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax rootObject,
        string? resourceUri,
        out StatementSyntax statement);
}

/// <summary>
/// Optional structured lowering for a profile-authorized markup-extension receiver
/// interface or duck callable. The profile owns service-provider construction and
/// compatibility fallback; the core supplies exact Roslyn shape evidence.
/// </summary>
public interface IRoslynXamlMarkupExtensionReceiverProfile
{
    bool TryCreateMarkupExtensionReceiverAssignment(
        XamlMarkupExtensionReceiverShapeInfo receiverShape,
        XamlIrObject extension,
        ExpressionSyntax extensionInstance,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax rootObject,
        string? resourceUri,
        out StatementSyntax statement);
}

public sealed class WinUiXamlProfile : IRoslynXamlFrameworkProfile, IRoslynXamlContextualMarkupExpressionProfile, IRoslynXamlConditionalNamespaceProfile, IRoslynXamlObjectExpressionProfile, IRoslynXamlCompiledResourceProfile, IRoslynXamlResourceAssignmentProfile, IRoslynXamlDeferredContentProfile, IRoslynXamlNameScopeProfile, IRoslynXamlMarkupExtensionAssignmentProfile, IRoslynXamlOrdinaryBindingAssignmentProfile, IRoslynXamlDeferredMarkupExtensionLifecycleProfile, IRoslynXamlBindingAccessorRegistrationProfile, IRoslynXamlCompiledBindingAssignmentProfile, IRoslynXamlCompiledBindingLifecycleProfile, IRoslynXamlDeferredCompiledBindingLifecycleProfile, IRoslynXamlCompiledEventBindingProfile, IRoslynXamlMarkupExtensionInvocationProfile, IXamlSchemaMetadataProvider, IXamlSyntheticSchemaProvider, IXamlDialectDirectiveProvider, IXamlTextValuePolicy, IXamlDictionaryKeyDirectivePolicy, ProGPU.Xaml.Binding.IXamlCompiledBindingPolicy, IXamlDeferredContentContextTypePolicy, IXamlIrDeferredContentContextTypePolicy
{
    private static readonly string[] Extensions = { ".xaml" };
    private static readonly string[] PresentationNamespaces =
    {
        "Microsoft.UI.Xaml",
        "Microsoft.UI.Xaml.Controls",
        "Microsoft.UI.Xaml.Controls.Primitives",
        "Microsoft.UI.Xaml.Automation",
        "Microsoft.UI.Xaml.Automation.Peers",
        "Microsoft.UI.Xaml.Data",
        "Microsoft.UI.Xaml.Documents",
        "Microsoft.UI.Xaml.Input",
        "Microsoft.UI.Xaml.Markup",
        "Microsoft.UI.Xaml.Media",
        "Microsoft.UI.Xaml.Media.Animation",
        "Microsoft.UI.Xaml.Media.Imaging",
        "Microsoft.UI.Xaml.Shapes",
        "Microsoft.UI.Text",
        "Windows.UI.Text",
        "ProGPU.Vector"
    };
    private static readonly string[] DictionaryKeyAliases = { "Name" };

    public string Id => "WinUI";
    public int ContractVersion => XamlFrameworkContract.CurrentVersion;
    public XamlFrameworkCapabilities Capabilities =>
        XamlFrameworkCapabilities.SchemaMetadata |
        XamlFrameworkCapabilities.NamespaceMetadata |
        XamlFrameworkCapabilities.StructuredCSharpEmission |
        XamlFrameworkCapabilities.MarkupExtensions |
        XamlFrameworkCapabilities.Resources |
        XamlFrameworkCapabilities.Bindings |
        XamlFrameworkCapabilities.Templates |
        XamlFrameworkCapabilities.HotReload |
        XamlFrameworkCapabilities.ConditionalNamespaces;
    public IReadOnlyList<string> FileExtensions => Extensions;
    public IReadOnlyList<string> DictionaryKeyDirectiveAliases => DictionaryKeyAliases;
    public ProGPU.Xaml.Binding.XamlCompiledBindingMode DefaultCompiledBindingMode =>
        ProGPU.Xaml.Binding.XamlCompiledBindingMode.OneTime;
    public string DefaultCompiledBindingModeDirective => "DefaultBindMode";
    public string CompiledBindingDataTypeDirective => "DataType";
    public IReadOnlyDictionary<char, char> CompiledBindingPathBracketPairs { get; } =
        new Dictionary<char, char>
        {
            ['('] = ')',
            ['['] = ']'
        };
    public string MetadataProviderId => "ProGPU.WinUI";
    public int MetadataPriority => 100;
    public IReadOnlyList<XamlSchemaAttributeRule> AttributeRules { get; } =
        XamlSchemaAttributeCatalog.Combine(
            XamlSchemaAttributeCatalog.Combine(
                XamlSchemaAttributeCatalog.Common,
                XamlSchemaAttributeCatalog.BuildControl),
            XamlSchemaAttributeCatalog.WinUi);
    public XamlSymbolShapePolicy SymbolShapePolicy { get; } = new XamlSymbolShapePolicy(
        runtimeNameFallback: "Name",
        addChildInterfaceMetadataNames: new[] { "Microsoft.UI.Xaml.Markup.IAddChild" },
        propertyIdentifierSuffix: "Property",
        propertyIdentifierTypeMetadataName: "Microsoft.UI.Xaml.DependencyProperty",
        propertySetterMethodName: "SetValue",
        implicitDictionaryKeyMembers: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Microsoft.UI.Xaml.Style"] = "TargetType"
        },
        resourceMemberRoles: new Dictionary<string, XamlResourceMemberRole>(StringComparer.Ordinal)
        {
            ["Resources"] = XamlResourceMemberRole.LexicalResources,
            ["MergedDictionaries"] = XamlResourceMemberRole.MergedDictionaries,
            ["ThemeDictionaries"] = XamlResourceMemberRole.ThemeDictionaries,
            ["Source"] = XamlResourceMemberRole.Source
        },
        profileTextSyntaxTypeMetadataNames: new[]
        {
            "Microsoft.UI.Xaml.Thickness",
            "Microsoft.UI.Xaml.CornerRadius",
            "Microsoft.UI.Xaml.GridLength",
            "Microsoft.UI.Xaml.Controls.GridLength",
            "Microsoft.UI.Xaml.Duration",
            "Microsoft.UI.Xaml.TargetPropertyPath",
            "Microsoft.UI.Xaml.Media.Animation.KeyTime",
            "Microsoft.UI.Xaml.Media.Animation.KeySpline",
            "Microsoft.UI.Xaml.Media.FontFamily",
            "Windows.UI.Text.FontWeight",
            "ProGPU.Vector.Color",
            "Windows.UI.Color"
        },
        pseudoContentMembers: new Dictionary<string, XamlPseudoMemberDefinition>(StringComparer.Ordinal)
        {
            ["Microsoft.UI.Xaml.FrameworkTemplate"] = new XamlPseudoMemberDefinition(
                "Template",
                "System.Object",
                XamlMemberKind.DeferredContent,
                XamlSchemaSemantics.TemplateContent)
        },
        inferGetterOnlyAttachedCollections: true,
        markupExtensionBaseTypeMetadataNames: new[] { "Microsoft.UI.Xaml.Markup.MarkupExtension" },
        markupExtensionProvideValueMethodNames: new[] { "ProvideValue" },
        markupExtensionServiceProviderTypeMetadataNames: new[] { "Microsoft.UI.Xaml.IXamlServiceProvider" },
        markupExtensionAvailableServiceTypeMetadataNames: new[]
        {
            "Microsoft.UI.Xaml.IXamlServiceProvider",
            "Microsoft.UI.Xaml.Markup.IProvideValueTarget",
            "Microsoft.UI.Xaml.Markup.IRootObjectProvider",
            "Microsoft.UI.Xaml.Markup.IUriContext"
        },
        inferDesignerSerializationMethods: true);
    public IReadOnlyList<XamlSyntheticTypeDefinition> SyntheticTypes { get; } = new[]
    {
        Extension("StaticResource", allowDefault: true, members: new[] { Member("ResourceKey", "System.Object") },
            resourceReferenceRole: XamlResourceReferenceRole.Static),
        Extension("ThemeResource", allowDefault: true, members: new[] { Member("ResourceKey", "System.Object") },
            resourceReferenceRole: XamlResourceReferenceRole.Dynamic),
        Extension(
            "Binding",
            allowDefault: true,
            members: BindingMembers(),
            expressionRole: XamlExpressionRole.Binding),
        new XamlSyntheticTypeDefinition(
            XamlNamespaces.Language2006,
            "Bind",
            isMarkupExtension: true,
            returnTypeMetadataName: "System.Object",
            constructors: new IReadOnlyList<string>[] { Array.Empty<string>(), new[] { "System.String" } },
            members: BindMembers(),
            expressionRole: XamlExpressionRole.CompiledBinding),
        Extension("TemplateBinding", allowDefault: true, members: new[] { Member("Property", "System.String") }),
        Extension("RelativeSource", members: new[] { Member("Mode") })
    };

    public IReadOnlyList<XamlDialectDirectiveDefinition> DialectDirectives { get; } = new[]
    {
        new XamlDialectDirectiveDefinition(XamlNamespaces.Language2006, "Load", XamlAllowedLocation.AttributeOnly),
        new XamlDialectDirectiveDefinition(XamlNamespaces.Language2006, "DeferLoadStrategy", XamlAllowedLocation.AttributeOnly),
        new XamlDialectDirectiveDefinition(XamlNamespaces.Language2006, "DefaultBindMode", XamlAllowedLocation.AttributeOnly),
        new XamlDialectDirectiveDefinition(XamlNamespaces.Language2006, "DataType", XamlAllowedLocation.AttributeOnly)
    };

    public IReadOnlyList<string> GetClrNamespaceCandidates(string xamlNamespaceUri) =>
        string.Equals(xamlNamespaceUri, XamlNamespaces.Presentation2006, StringComparison.Ordinal)
            ? PresentationNamespaces
            : Array.Empty<string>();

    public bool TryCreateConditionalNamespaceExpression(
        XamlNamespaceCondition condition,
        out ExpressionSyntax expression)
    {
        if (condition == null) throw new ArgumentNullException(nameof(condition));
        var valid = condition.Method switch
        {
            "IsApiContractPresent" or "IsApiContractNotPresent" =>
                (condition.Arguments.Length == 2 ||
                 condition.Arguments.Length == 3) &&
                ushort.TryParse(
                    condition.Arguments[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _) &&
                (condition.Arguments.Length == 2 ||
                 ushort.TryParse(
                     condition.Arguments[2],
                     NumberStyles.None,
                     CultureInfo.InvariantCulture,
                     out _)),
            "IsTypePresent" or "IsTypeNotPresent" =>
                condition.Arguments.Length == 1,
            "IsPropertyPresent" or "IsPropertyNotPresent" =>
                condition.Arguments.Length == 2,
            _ => false
        };
        if (!valid)
        {
            expression = null!;
            return false;
        }
        var arguments = new List<ArgumentSyntax>(condition.Arguments.Length + 1)
        {
            SyntaxFactory.Argument(StringLiteral(condition.Method))
        };
        arguments.AddRange(condition.Arguments.Select(argument =>
            SyntaxFactory.Argument(StringLiteral(argument))));
        expression = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    RoslynTypeSyntaxFactory.CreateGlobalName(
                        "Microsoft", "UI", "Xaml", "Markup", "ConditionalXaml"),
                    SyntaxFactory.IdentifierName("IsEnabled")))
            .WithArgumentList(SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(arguments)));
        return true;
    }

    private static XamlSyntheticTypeDefinition Extension(
        string name,
        bool allowDefault = false,
        IReadOnlyList<XamlSyntheticMemberDefinition>? members = null,
        XamlResourceReferenceRole resourceReferenceRole = XamlResourceReferenceRole.None,
        XamlExpressionRole expressionRole = XamlExpressionRole.None) => new(
        XamlNamespaces.Presentation2006,
        name,
        isMarkupExtension: true,
        returnTypeMetadataName: "System.Object",
        constructors: allowDefault
            ? new IReadOnlyList<string>[] { Array.Empty<string>(), new[] { "System.String" } }
            : new IReadOnlyList<string>[] { new[] { "System.String" } },
        members: members,
        resourceReferenceRole: resourceReferenceRole,
        expressionRole: expressionRole);

    private static XamlSyntheticMemberDefinition Member(string name, string type = "System.Object") => new(name, type);

    private static IReadOnlyList<XamlSyntheticMemberDefinition> BindingMembers() => new[]
    {
        Member("Path", "System.String"), Member("Mode"), Member("ElementName", "System.String"),
        Member("RelativeSource"), Member("Source"), Member("Converter"), Member("ConverterParameter"),
        Member("ConverterLanguage", "System.String"), Member("UpdateSourceTrigger"),
        Member("FallbackValue"), Member("TargetNullValue")
    };

    private static IReadOnlyList<XamlSyntheticMemberDefinition> BindMembers() => new[]
    {
        Member("Path", "System.String"), Member("Mode"), Member("BindBack", "System.String"),
        Member("Converter"), Member("ConverterParameter"), Member("ConverterLanguage", "System.String"),
        Member("UpdateSourceTrigger"), Member("FallbackValue"), Member("TargetNullValue")
    };

    public bool TryValidateTextValue(XamlTypeInfo targetType, string text, out bool isValid)
    {
        var symbol = targetType.Symbol;
        if (symbol is INamedTypeSymbol { IsGenericType: true } nullable &&
            nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
            symbol = nullable.TypeArguments[0];
        if (symbol.SpecialType == SpecialType.System_Double)
        {
            isValid = TryParseWinUiDouble(text, out _);
            return true;
        }
        isValid = false;
        return false;
    }

    public bool TryCreateLiteralExpression(XamlTypeInfo targetType, string text, out ExpressionSyntax expression)
    {
        text = text ?? string.Empty;
        if (targetType.Symbol is INamedTypeSymbol { IsGenericType: true } nullable &&
            nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            var underlying = nullable.TypeArguments[0];
            if (underlying.SpecialType == SpecialType.System_Double &&
                TryParseWinUiDouble(text, out var nullableDouble))
            {
                expression = SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(nullableDouble));
                return true;
            }
            if (underlying.SpecialType == SpecialType.System_Single &&
                float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var nullableSingle))
            {
                expression = SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(nullableSingle));
                return true;
            }
            if (underlying.SpecialType == SpecialType.System_Int32 &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nullableInteger))
            {
                expression = SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(nullableInteger));
                return true;
            }
            if (string.Equals(underlying.ToDisplayString(), "System.TimeSpan", StringComparison.Ordinal) &&
                TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var nullableTimeSpan))
            {
                expression = CreateTimeSpanExpression(nullableTimeSpan);
                return true;
            }
            if (string.Equals(underlying.ToDisplayString(), "Windows.UI.Color", StringComparison.Ordinal) &&
                TryParseColor(text, out var alpha, out var red, out var green, out var blue))
            {
                expression = CreateWindowsColorExpression(underlying, alpha, red, green, blue);
                return true;
            }
        }
        switch (targetType.Symbol.SpecialType)
        {
            case SpecialType.System_String:
            case SpecialType.System_Object:
                expression = StringLiteral(text.StartsWith("{}", StringComparison.Ordinal) ? text.Substring(2) : text);
                return true;
            case SpecialType.System_Boolean:
                if (bool.TryParse(text, out var boolean))
                {
                    expression = SyntaxFactory.LiteralExpression(boolean
                        ? SyntaxKind.TrueLiteralExpression
                        : SyntaxKind.FalseLiteralExpression);
                    return true;
                }
                break;
            case SpecialType.System_Byte:
                if (byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var byteValue))
                {
                    expression = SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal((int)byteValue));
                    return true;
                }
                break;
            case SpecialType.System_Int16:
                if (short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int16))
                {
                    expression = SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal((int)int16));
                    return true;
                }
                break;
            case SpecialType.System_Int32:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer32))
                {
                    expression = SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(integer32));
                    return true;
                }
                break;
            case SpecialType.System_Int64:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer64))
                {
                    expression = SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(integer64));
                    return true;
                }
                break;
            case SpecialType.System_Single:
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var single))
                {
                    expression = SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(single));
                    return true;
                }
                break;
            case SpecialType.System_Double:
                if (TryParseWinUiDouble(text, out var @double))
                {
                    expression = SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(@double));
                    return true;
                }
                break;
            case SpecialType.System_Decimal:
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var @decimal))
                {
                    expression = SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(@decimal));
                    return true;
                }
                break;
            case SpecialType.System_Char:
                if (text.Length == 1)
                {
                    expression = SyntaxFactory.LiteralExpression(
                        SyntaxKind.CharacterLiteralExpression,
                        SyntaxFactory.Literal(text[0]));
                    return true;
                }
                break;
        }

        switch (targetType.MetadataName)
        {
            case "System.Uri":
                expression = SyntaxFactory.ObjectCreationExpression(
                        RoslynTypeSyntaxFactory.CreateGlobalName("System", "Uri"))
                    .WithArgumentList(Arguments(
                        StringLiteral(text),
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName("System", "UriKind"),
                            SyntaxFactory.IdentifierName("RelativeOrAbsolute"))));
                return true;
            case "System.TimeSpan":
                if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var timeSpan))
                {
                    expression = CreateTimeSpanExpression(timeSpan);
                    return true;
                }
                break;
            case "Microsoft.UI.Xaml.Thickness":
            case "Microsoft.UI.Xaml.CornerRadius":
                return TryCreateFloatTuple(targetType, text, out expression);
            case "Microsoft.UI.Xaml.GridLength":
            case "Microsoft.UI.Xaml.Controls.GridLength":
                return TryCreateGridLength(targetType, text, out expression);
            case "System.Numerics.Vector2":
                return TryCreateNumericsVector(targetType, text, 2, out expression);
            case "System.Numerics.Vector4":
                return TryCreateVector4(targetType, text, out expression);
            case "ProGPU.Scene.Rect":
                return TryCreateFloatComponents(targetType, text, 4, out expression);
            case "ProGPU.Vector.Color":
                return TryCreateColor(targetType, text, out expression);
            case "ProGPU.Vector.Brush":
                return TryCreateBrush(text, out expression);
            case "Microsoft.UI.Xaml.Media.Animation.KeyTime":
                return TryCreateKeyTime(targetType, text, out expression);
            case "Microsoft.UI.Xaml.Media.Animation.KeySpline":
                return TryCreateKeySpline(targetType, text, out expression);
            case "Microsoft.UI.Xaml.Duration":
                return TryCreateDuration(targetType, text, out expression);
            case "Microsoft.UI.Xaml.TargetPropertyPath":
                return TryCreateTargetPropertyPath(targetType, text, out expression);
            case "Microsoft.UI.Xaml.Media.Geometry":
                expression = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                                "Microsoft", "UI", "Xaml", "Media", "PathGeometry"),
                            SyntaxFactory.IdentifierName("Parse")))
                    .WithArgumentList(Arguments(StringLiteral(text)));
                return !string.IsNullOrWhiteSpace(text);
            case "Microsoft.UI.Xaml.Controls.IconElement":
                expression = SyntaxFactory.ObjectCreationExpression(
                        RoslynTypeSyntaxFactory.CreateGlobalName(
                            "Microsoft", "UI", "Xaml", "Controls", "SymbolIcon"))
                    .WithArgumentList(SyntaxFactory.ArgumentList())
                    .WithInitializer(SyntaxFactory.InitializerExpression(
                        SyntaxKind.ObjectInitializerExpression,
                        SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                            InitializerAssignment(
                                "Symbol",
                                EnumMember(
                                    "Microsoft.UI.Xaml.Controls.Symbol",
                                    text.Trim())))));
                return !string.IsNullOrWhiteSpace(text);
            case "Microsoft.UI.Xaml.Media.FontFamily":
                expression = SyntaxFactory.ObjectCreationExpression(RoslynTypeSyntaxFactory.Create(targetType.Symbol))
                    .WithArgumentList(Arguments(StringLiteral(text)));
                return !string.IsNullOrWhiteSpace(text);
            case "Windows.UI.Text.FontWeight":
                return TryCreateFontWeight(text, out expression);
            case "Windows.UI.Color":
                return TryCreateWindowsColor(targetType, text, out expression);
        }

        if (targetType.IsEnum)
        {
            foreach (var value in targetType.EnumValues)
            {
                if (string.Equals(value.Name, text, StringComparison.OrdinalIgnoreCase))
                {
                    expression = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        TypeExpression(targetType),
                        SyntaxFactory.IdentifierName(EscapeIdentifier(value.Symbol.Name)));
                    return true;
                }
            }
        }

        expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        return false;
    }

    private static bool TryParseWinUiDouble(string? text, out double value)
    {
        text ??= string.Empty;
        if (text.Length == 0)
        {
            value = 0d;
            return true;
        }
        if (text[text.Length - 1] == '}')
            text = text.Substring(0, text.Length - 1);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryCreateFontWeight(string text, out ExpressionSyntax expression)
    {
        var memberName = text.Trim() switch
        {
            var value when value.Equals("Thin", StringComparison.OrdinalIgnoreCase) => "Thin",
            var value when value.Equals("ExtraLight", StringComparison.OrdinalIgnoreCase) => "ExtraLight",
            var value when value.Equals("Light", StringComparison.OrdinalIgnoreCase) => "Light",
            var value when value.Equals("SemiLight", StringComparison.OrdinalIgnoreCase) => "SemiLight",
            var value when value.Equals("Normal", StringComparison.OrdinalIgnoreCase) => "Normal",
            var value when value.Equals("Regular", StringComparison.OrdinalIgnoreCase) => "Normal",
            var value when value.Equals("Medium", StringComparison.OrdinalIgnoreCase) => "Medium",
            var value when value.Equals("SemiBold", StringComparison.OrdinalIgnoreCase) => "SemiBold",
            var value when value.Equals("Bold", StringComparison.OrdinalIgnoreCase) => "Bold",
            var value when value.Equals("ExtraBold", StringComparison.OrdinalIgnoreCase) => "ExtraBold",
            var value when value.Equals("Black", StringComparison.OrdinalIgnoreCase) => "Black",
            var value when value.Equals("ExtraBlack", StringComparison.OrdinalIgnoreCase) => "ExtraBlack",
            _ => null
        };

        if (memberName is not null)
        {
            expression = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName("Microsoft", "UI", "Text", "FontWeights"),
                SyntaxFactory.IdentifierName(memberName));
            return true;
        }

        if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) &&
            numeric is >= 1 and <= 999)
        {
            expression = SyntaxFactory.ObjectCreationExpression(
                    RoslynTypeSyntaxFactory.CreateGlobalName("Windows", "UI", "Text", "FontWeight"))
                .WithArgumentList(SyntaxFactory.ArgumentList())
                .WithInitializer(SyntaxFactory.InitializerExpression(
                    SyntaxKind.ObjectInitializerExpression,
                    SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName("Weight"),
                            SyntaxFactory.CastExpression(
                                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.UShortKeyword)),
                                SyntaxFactory.LiteralExpression(
                                    SyntaxKind.NumericLiteralExpression,
                                    SyntaxFactory.Literal((int)numeric)))))));
            return true;
        }

        expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        return false;
    }

    public bool TryCreateCompiledResourceRegistration(
        XamlCompiledResourceArtifact artifact,
        out StatementSyntax statement)
    {
        var factorySegments = artifact.FactoryNamespace.Split('.').Concat(new[] { artifact.FactoryTypeName }).ToArray();
        var buildMethod = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(factorySegments),
            SyntaxFactory.IdentifierName(artifact.BuildMethodName));
        var register = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "XamlResourceProviderRegistry"),
            SyntaxFactory.IdentifierName("Register"));
        statement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(register).WithArgumentList(Arguments(
                StringLiteral(artifact.ResourceUri),
                buildMethod)));
        return true;
    }

    public bool TryCreateObjectExpression(
        XamlIrObject value,
        ExpressionSyntax lookupRoot,
        out ExpressionSyntax expression)
    {
        expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        if (!string.Equals(
                value.Type.Symbol?.MetadataName,
                "ProGPU.Vector.SolidColorBrush",
                StringComparison.Ordinal))
            return false;

        XamlIrResourceReference? colorResource = null;
        float? opacity = null;
        foreach (var operation in value.Operations)
        {
            var memberName = operation.Member.Symbol?.Name ??
                             operation.Member.RequestedName.LocalName;
            if (operation.Kind == XamlIrOperationKind.ApplyDirective) continue;
            if (string.Equals(memberName, "Color", StringComparison.Ordinal))
            {
                colorResource = operation.Values.OfType<XamlIrResourceReference>()
                    .SingleOrDefault();
                if (colorResource?.Reference.Kind !=
                    ProGPU.Xaml.Resources.XamlResourceReferenceKind.Theme)
                    return false;
                continue;
            }
            if (string.Equals(memberName, "Opacity", StringComparison.Ordinal))
            {
                var text = operation.Values.OfType<XamlIrText>().SingleOrDefault()?.Text;
                if (!float.TryParse(
                        text,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsedOpacity))
                    return false;
                opacity = parsedOpacity;
                continue;
            }
            return false;
        }
        if (colorResource == null) return false;

        var creation = SyntaxFactory.ObjectCreationExpression(
                RoslynTypeSyntaxFactory.CreateGlobalName(
                    "ProGPU", "Vector", "ThemeResourceBrush"))
            .WithArgumentList(Arguments(
                lookupRoot,
                StringLiteral(colorResource.Reference.ResourceKey.Text)));
        expression = opacity.HasValue
            ? creation.WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                    InitializerAssignment("Opacity", FloatLiteral(opacity.Value)))))
            : creation;
        return true;
    }

    public bool TryCreateMarkupExtensionExpression(
        XamlMarkupExtension extension,
        XamlTypeInfo targetType,
        out ExpressionSyntax expression)
    {
        if (string.Equals(extension.Name, "x:Null", StringComparison.Ordinal) ||
            string.Equals(extension.Name, "Null", StringComparison.Ordinal))
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return true;
        }

        var key = GetFirstTextArgument(extension);
        if (key != null && string.Equals(extension.Name, "ThemeResource", StringComparison.Ordinal))
        {
            var isBrush = string.Equals(targetType.Symbol.Name, "Brush", StringComparison.Ordinal) ||
                           targetType.Symbol.Name.EndsWith("Brush", StringComparison.Ordinal)
                ;
            var typeName = isBrush
                ? RoslynTypeSyntaxFactory.CreateGlobalName("ProGPU", "Vector", "ThemeResourceBrush")
                : RoslynTypeSyntaxFactory.CreateGlobalName("Microsoft", "UI", "Xaml", "ThemeResource");
            expression = SyntaxFactory.ObjectCreationExpression(typeName)
                .WithArgumentList(Arguments(StringLiteral(key)));
            return true;
        }

        expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        return false;
    }

    public bool TryCreateResourceReferenceExpression(
        XamlIrResourceReference reference,
        XamlTypeInfo targetType,
        ExpressionSyntax lookupRoot,
        ExpressionSyntax resourceKey,
        out ExpressionSyntax expression)
    {
        if (reference.Reference.Kind == ProGPU.Xaml.Resources.XamlResourceReferenceKind.Theme)
        {
            var isBrush = string.Equals(targetType.Symbol.Name, "Brush", StringComparison.Ordinal) ||
                          targetType.Symbol.Name.EndsWith("Brush", StringComparison.Ordinal);
            var typeName = isBrush
                ? RoslynTypeSyntaxFactory.CreateGlobalName("ProGPU", "Vector", "ThemeResourceBrush")
                : RoslynTypeSyntaxFactory.CreateGlobalName("Microsoft", "UI", "Xaml", "ThemeResource");
            expression = SyntaxFactory.ObjectCreationExpression(typeName)
                .WithArgumentList(Arguments(lookupRoot, resourceKey));
            return true;
        }

        TypeSyntax resolverType;
        if (IsPlatformColorResource(reference.TerminalResourceKey.Text) &&
            string.Equals(
                targetType.Symbol.ToDisplayString(),
                "System.Numerics.Vector4",
                StringComparison.Ordinal))
        {
            resolverType = RoslynTypeSyntaxFactory.CreateGlobalName(
                "ProGPU",
                "Vector",
                "Color");
        }
        else
        {
            var resolvedType = reference.ResolvedValueType is { } definitionType &&
                               HasImplicitResourceConversion(definitionType.Symbol, targetType.Symbol)
                ? definitionType
                : targetType;
            resolverType = RoslynTypeSyntaxFactory.Create(resolvedType.Symbol);
        }
        var resolver = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName("Microsoft", "UI", "Xaml", "XamlResourceResolver"),
            SyntaxFactory.GenericName("Resolve")
                .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(
                    resolverType))));
        expression = SyntaxFactory.InvocationExpression(resolver)
            .WithArgumentList(Arguments(lookupRoot, resourceKey));
        return true;
    }

    private static bool IsPlatformColorResource(string key) =>
        key == "SystemColorButtonTextColor" ||
        key == "SystemColorButtonFaceColor" ||
        key == "SystemColorGrayTextColor" ||
        key == "SystemColorHighlightColor" ||
        key == "SystemColorHighlightTextColor" ||
        key == "SystemColorHotlightColor" ||
        key == "SystemColorWindowColor" ||
        key == "SystemColorWindowTextColor";

    private static bool HasImplicitResourceConversion(
        ITypeSymbol source,
        ITypeSymbol target)
    {
        if (HasImplicitConversionOperator(source, target, source) ||
            HasImplicitConversionOperator(source, target, target))
            return true;
        return IsImplicitNumericConversion(source.SpecialType, target.SpecialType);
    }

    private static bool HasImplicitConversionOperator(
        ITypeSymbol source,
        ITypeSymbol target,
        ITypeSymbol declaringType)
    {
        if (declaringType is not INamedTypeSymbol named) return false;
        return named.GetMembers("op_Implicit").OfType<IMethodSymbol>().Any(method =>
            method.Parameters.Length == 1 &&
            SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, source) &&
            SymbolEqualityComparer.Default.Equals(method.ReturnType, target));
    }

    private static bool IsImplicitNumericConversion(
        SpecialType source,
        SpecialType target)
    {
        switch (source)
        {
            case SpecialType.System_SByte:
                return target == SpecialType.System_Int16 ||
                       target == SpecialType.System_Int32 ||
                       target == SpecialType.System_Int64 ||
                       target == SpecialType.System_Single ||
                       target == SpecialType.System_Double ||
                       target == SpecialType.System_Decimal;
            case SpecialType.System_Byte:
                return target == SpecialType.System_Int16 ||
                       target == SpecialType.System_UInt16 ||
                       target == SpecialType.System_Int32 ||
                       target == SpecialType.System_UInt32 ||
                       target == SpecialType.System_Int64 ||
                       target == SpecialType.System_UInt64 ||
                       target == SpecialType.System_Single ||
                       target == SpecialType.System_Double ||
                       target == SpecialType.System_Decimal;
            case SpecialType.System_Int16:
                return target == SpecialType.System_Int32 ||
                       target == SpecialType.System_Int64 ||
                       target == SpecialType.System_Single ||
                       target == SpecialType.System_Double ||
                       target == SpecialType.System_Decimal;
            case SpecialType.System_UInt16:
            case SpecialType.System_Char:
                return target == SpecialType.System_Int32 ||
                       target == SpecialType.System_UInt32 ||
                       target == SpecialType.System_Int64 ||
                       target == SpecialType.System_UInt64 ||
                       target == SpecialType.System_Single ||
                       target == SpecialType.System_Double ||
                       target == SpecialType.System_Decimal;
            case SpecialType.System_Int32:
                return target == SpecialType.System_Int64 ||
                       target == SpecialType.System_Single ||
                       target == SpecialType.System_Double ||
                       target == SpecialType.System_Decimal;
            case SpecialType.System_UInt32:
                return target == SpecialType.System_Int64 ||
                       target == SpecialType.System_UInt64 ||
                       target == SpecialType.System_Single ||
                       target == SpecialType.System_Double ||
                       target == SpecialType.System_Decimal;
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                return target == SpecialType.System_Single ||
                       target == SpecialType.System_Double ||
                       target == SpecialType.System_Decimal;
            case SpecialType.System_Single:
                return target == SpecialType.System_Double;
            default:
                return false;
        }
    }

    public bool TryCreateResourceReferenceAssignment(
        XamlIrResourceReference reference,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax lookupRoot,
        ExpressionSyntax resourceKey,
        out StatementSyntax statement)
    {
        var shape = member.PropertySystemShape;
        if (reference.Reference.Kind != ProGPU.Xaml.Resources.XamlResourceReferenceKind.Theme)
        {
            statement = SyntaxFactory.EmptyStatement();
            return false;
        }
        if (!IsDependencyObject(member.DeclaringType.Symbol))
        {
            statement = SyntaxFactory.EmptyStatement();
            return false;
        }

        var themeResource = SyntaxFactory.ObjectCreationExpression(
                RoslynTypeSyntaxFactory.CreateGlobalName("Microsoft", "UI", "Xaml", "ThemeResource"))
            .WithArgumentList(Arguments(lookupRoot, resourceKey));
        if (shape?.Identifier.ContainingType == null)
        {
            var setResource = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                    "Microsoft", "UI", "Xaml", "ThemeResourceOperations"),
                SyntaxFactory.IdentifierName("SetResource"));
            statement = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(setResource)
                    .WithArgumentList(Arguments(
                        receiver,
                        StringLiteral(member.Name),
                        themeResource)));
            return true;
        }

        var propertyIdentifier = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(shape.Identifier.ContainingType),
            SyntaxFactory.IdentifierName(EscapeIdentifier(shape.Identifier.Name)));
        var setter = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            receiver,
            SyntaxFactory.IdentifierName(EscapeIdentifier(shape.Setter.Name)));
        statement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(setter)
                .WithArgumentList(Arguments(propertyIdentifier, themeResource)));
        return true;
    }

    public bool TryCreateDeferredContentAssignment(
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ParenthesizedLambdaExpressionSyntax factory,
        out StatementSyntax statement)
    {
        if (member.Kind != XamlMemberKind.DeferredContent ||
            !string.Equals(member.SyntheticSemantic, XamlSchemaSemantics.TemplateContent, StringComparison.Ordinal))
        {
            statement = SyntaxFactory.EmptyStatement();
            return false;
        }

        var setter = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "Markup", "XamlTemplateFactory"),
            SyntaxFactory.IdentifierName("SetFactory"));
        statement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(setter)
                .WithArgumentList(Arguments(receiver, factory)));
        return true;
    }

    public bool TryCreateNameScopeFinalization(
        ExpressionSyntax root,
        ImmutableArray<RoslynXamlNameRegistrationSyntax> registrations,
        out ImmutableArray<StatementSyntax> statements)
    {
        if (registrations.IsDefaultOrEmpty)
        {
            statements = ImmutableArray<StatementSyntax>.Empty;
            return true;
        }

        var factory = (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
            "Microsoft", "UI", "Xaml", "Markup", "XamlTemplateFactory");
        var builder = ImmutableArray.CreateBuilder<StatementSyntax>(
            registrations.Length + 1);
        builder.Add(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            factory,
                            SyntaxFactory.IdentifierName("BeginNameScope")))
                    .WithArgumentList(Arguments(root))));
        foreach (var registration in registrations)
        {
            builder.Add(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                factory,
                                SyntaxFactory.IdentifierName("RegisterName")))
                        .WithArgumentList(Arguments(
                            root,
                            StringLiteral(registration.Name),
                            registration.Expression))));
        }

        statements = builder.MoveToImmutable();
        return true;
    }

    public bool TryGetDeferredContentContextType(
        XamlBoundObject owner,
        XamlBoundMember deferredMember,
        out XamlTypeInfo contextType)
    {
        contextType = null!;
        if (deferredMember.Member.Symbol?.Kind !=
                XamlMemberKind.DeferredContent ||
            !string.Equals(
                deferredMember.Member.Symbol.SyntheticSemantic,
                XamlSchemaSemantics.TemplateContent,
                StringComparison.Ordinal))
            return false;

        foreach (var member in owner.Members)
        {
            if (!string.Equals(
                    member.Member.Symbol?.Name,
                    "TargetType",
                    StringComparison.Ordinal))
                continue;
            var type = member.Values
                .OfType<XamlBoundTypeValue>()
                .FirstOrDefault()?
                .Type
                .Symbol;
            if (type == null)
                continue;
            contextType = type;
            return true;
        }

        return false;
    }

    public bool IsDeferredContentContextSource(
        XamlBoundBinding binding)
    {
        if (FindBoundMember(binding.Extension, "Source") != null)
            return false;
        var elementName = FindBoundMember(
            binding.Extension,
            "ElementName");
        if (elementName?.Values.OfType<XamlBoundText>().Any(
                static value =>
                    !string.IsNullOrWhiteSpace(value.Text)) == true)
            return false;
        var relativeSource = FindBoundMember(
                binding.Extension,
                "RelativeSource")?
            .Values
            .OfType<XamlBoundObject>()
            .SingleOrDefault();
        if (relativeSource == null)
            return false;
        var mode = FirstBoundText(
            FindBoundMember(relativeSource, "Mode")) ??
            FirstBoundText(
                relativeSource.Members.FirstOrDefault(member =>
                    member.Member.Kind ==
                        XamlBoundReferenceKind.Directive &&
                    string.Equals(
                        member.Member.RequestedName.LocalName,
                        "PositionalParameters",
                        StringComparison.Ordinal)));
        return string.Equals(
            mode?.Trim(),
            "TemplatedParent",
            StringComparison.Ordinal);
    }

    public bool TryGetDeferredContentContextType(
        XamlIrObject owner,
        XamlIrOperation deferredOperation,
        out XamlTypeInfo contextType)
    {
        contextType = null!;
        if (deferredOperation.Member.Symbol?.Kind !=
                XamlMemberKind.DeferredContent ||
            !string.Equals(
                deferredOperation.Member.Symbol.SyntheticSemantic,
                XamlSchemaSemantics.TemplateContent,
                StringComparison.Ordinal))
            return false;

        foreach (var operation in owner.Operations)
        {
            if (!string.Equals(
                    operation.Member.Symbol?.Name,
                    "TargetType",
                    StringComparison.Ordinal))
                continue;
            var type = operation.Values
                .OfType<XamlIrType>()
                .FirstOrDefault()?
                .Type
                .Symbol;
            if (type == null)
                continue;
            contextType = type;
            return true;
        }

        return false;
    }

    public bool TryCreateMarkupExtensionAssignment(
        XamlIrObject extension,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax? context,
        XamlTypeInfo? contextType,
        ExpressionSyntax lookupRoot,
        ExpressionSyntax? deferredLifetimeOwner,
        out StatementSyntax statement,
        out bool usesDeferredLifetime)
    {
        statement = SyntaxFactory.EmptyStatement();
        usesDeferredLifetime = false;
        var extensionName = extension.Type.RequestedName.LocalName;
        if (string.Equals(extensionName, "Binding", StringComparison.Ordinal) ||
            string.Equals(extensionName, "BindingExtension", StringComparison.Ordinal))
        {
            if (!IsDependencyObject(member.DeclaringType.Symbol))
                return false;
            if (!TryCreateBindingExpression(extension, lookupRoot, out var binding))
                return false;
            var setBinding = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                    "Microsoft", "UI", "Xaml", "Data", "BindingOperations"),
                SyntaxFactory.IdentifierName("SetBinding"));
            var bindingArguments = deferredLifetimeOwner == null
                ? Arguments(
                    receiver,
                    StringLiteral(member.Name),
                    binding,
                    context ?? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression),
                    lookupRoot)
                : Arguments(
                    receiver,
                    StringLiteral(member.Name),
                    binding,
                    context ?? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression),
                    lookupRoot,
                    deferredLifetimeOwner);
            statement = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(setBinding)
                    .WithArgumentList(bindingArguments));
            usesDeferredLifetime =
                context != null ||
                deferredLifetimeOwner != null;
            return true;
        }

        if (!string.Equals(extensionName, "TemplateBinding", StringComparison.Ordinal) ||
            context == null || contextType == null)
            return false;

        var propertyPath = (GetTextArgument(extension, "Property") ?? GetFirstTextArgument(extension))?.Trim();
        if (string.IsNullOrWhiteSpace(propertyPath)) return false;

        var sourceOwner = contextType.Symbol as INamedTypeSymbol;
        var sourcePropertyName = propertyPath!;
        var separator = propertyPath!.LastIndexOf('.');
        if (separator > 0)
        {
            sourcePropertyName = propertyPath.Substring(separator + 1);
            sourceOwner = FindPresentationType(contextType.Symbol.ContainingAssembly, propertyPath.Substring(0, separator));
        }

        var sourceIdentifier = FindPropertyIdentifier(sourceOwner, sourcePropertyName);
        var targetIdentifier =
            member.PropertySystemShape?.Identifier ??
            FindPropertyIdentifier(member.DeclaringType.Symbol as INamedTypeSymbol, member.Name);
        if (sourceIdentifier?.ContainingType == null ||
            targetIdentifier?.ContainingType == null)
        {
            var bindByName = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                    "Microsoft", "UI", "Xaml", "Controls", "XamlTemplateBindingRuntime"),
                SyntaxFactory.IdentifierName("Bind"));
            statement = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(bindByName)
                    .WithArgumentList(Arguments(
                        receiver,
                        StringLiteral(member.Name),
                        SyntaxFactory.PostfixUnaryExpression(
                            SyntaxKind.SuppressNullableWarningExpression,
                            context),
                        StringLiteral(sourcePropertyName))));
            return true;
        }

        ExpressionSyntax IdentifierExpression(ISymbol identifier) =>
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(identifier.ContainingType),
                SyntaxFactory.IdentifierName(EscapeIdentifier(identifier.Name)));

        var bind = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "Controls", "TemplateBinding"),
            SyntaxFactory.IdentifierName("Bind"));
        var typedContext = SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.CastExpression(
                RoslynTypeSyntaxFactory.Create(contextType.Symbol),
                SyntaxFactory.PostfixUnaryExpression(
                    SyntaxKind.SuppressNullableWarningExpression,
                    context)));
        statement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(bind).WithArgumentList(Arguments(
                receiver,
                IdentifierExpression(targetIdentifier),
                typedContext,
                IdentifierExpression(sourceIdentifier))));
        return true;
    }

    public bool TryCreateBindingAssignment(
        XamlIrBinding binding,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax? context,
        XamlTypeInfo? contextType,
        ExpressionSyntax lookupRoot,
        ExpressionSyntax? deferredLifetimeOwner,
        out StatementSyntax statement,
        out bool usesDeferredLifetime)
    {
        statement = SyntaxFactory.EmptyStatement();
        usesDeferredLifetime = false;
        if (!IsDependencyObject(member.DeclaringType.Symbol))
            return false;
        if (binding.Binding.PathSyntax != null &&
            binding.Binding.Accessors.Length !=
            binding.Binding.PathSyntax.Steps.Length)
            return false;
        if (!TryCreateBindingExpression(
                binding.Extension,
                lookupRoot,
                out var descriptor))
            return false;

        var setBinding = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "Data", "BindingOperations"),
            SyntaxFactory.IdentifierName("SetBindingWithPath"));
        var path = CreateOrdinaryBindingPath(binding.Binding.Accessors);
        var arguments = deferredLifetimeOwner == null
            ? Arguments(
                receiver,
                StringLiteral(member.Name),
                descriptor,
                path,
                context ?? SyntaxFactory.LiteralExpression(
                    SyntaxKind.NullLiteralExpression),
                lookupRoot)
            : Arguments(
                receiver,
                StringLiteral(member.Name),
                descriptor,
                path,
                context ?? SyntaxFactory.LiteralExpression(
                    SyntaxKind.NullLiteralExpression),
                lookupRoot,
                deferredLifetimeOwner);
        statement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(setBinding)
                .WithArgumentList(arguments));
        usesDeferredLifetime =
            context != null ||
            deferredLifetimeOwner != null;
        return true;
    }

    private static ExpressionSyntax CreateOrdinaryBindingPath(
        ImmutableArray<XamlBindingPathAccessor> accessors)
    {
        var segmentType = SyntaxFactory.QualifiedName(
            RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "Data"),
            SyntaxFactory.IdentifierName("BindingPathSegment"));
        var elements = new List<ExpressionSyntax>(accessors.Length);
        foreach (var accessor in accessors)
        {
            var factoryName = accessor.Kind ==
                XamlBindingPathAccessorKind.Member
                    ? "Member"
                    : "Indexer";
            ExpressionSyntax value = accessor.Kind switch
            {
                XamlBindingPathAccessorKind.IntegerIndexer =>
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(accessor.IntegerIndex)),
                XamlBindingPathAccessorKind.StringIndexer =>
                    StringLiteral(accessor.StringIndex!),
                _ => StringLiteral(accessor.Member.Name)
            };
            elements.Add(
                SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            segmentType,
                            SyntaxFactory.IdentifierName(factoryName)))
                    .WithArgumentList(Arguments(value)));
        }

        return SyntaxFactory.ArrayCreationExpression(
                SyntaxFactory.ArrayType(segmentType)
                    .WithRankSpecifiers(
                        SyntaxFactory.SingletonList(
                            SyntaxFactory.ArrayRankSpecifier(
                                SyntaxFactory.SingletonSeparatedList<
                                    ExpressionSyntax>(
                                    SyntaxFactory.OmittedArraySizeExpression())))))
            .WithInitializer(
                SyntaxFactory.InitializerExpression(
                    SyntaxKind.ArrayInitializerExpression,
                    SyntaxFactory.SeparatedList(elements)));
    }

    public bool TryCreateBindingAccessorRegistration(
        XamlBindingPathAccessor accessor,
        out StatementSyntax statement)
    {
        var source = SyntaxFactory.IdentifierName("source");
        var value = SyntaxFactory.IdentifierName("value");
        var member = CreateOrdinaryBindingAccessorAccess(
            accessor,
            source);
        var getter = SyntaxFactory.ParenthesizedLambdaExpression()
            .WithModifiers(
                SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(
                SyntaxFactory.ParameterList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Parameter(
                            SyntaxFactory.Identifier("source")))))
            .WithExpressionBody(member);
        ExpressionSyntax setter = SyntaxFactory.LiteralExpression(
            SyntaxKind.NullLiteralExpression);
        if (accessor.CanWrite)
        {
            setter = SyntaxFactory.ParenthesizedLambdaExpression()
                .WithModifiers(
                    SyntaxFactory.TokenList(
                        SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
                .WithParameterList(
                    SyntaxFactory.ParameterList(
                        SyntaxFactory.SeparatedList(
                            new[]
                            {
                                SyntaxFactory.Parameter(
                                    SyntaxFactory.Identifier("source")),
                                SyntaxFactory.Parameter(
                                    SyntaxFactory.Identifier("value"))
                            })))
                .WithExpressionBody(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        member,
                        value));
        }
        var register = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "Data",
                "BindingMemberAccessorRegistry"),
            SyntaxFactory.GenericName(
                    accessor.Kind == XamlBindingPathAccessorKind.Member
                        ? "Register"
                        : "RegisterIndexer")
                .WithTypeArgumentList(
                    SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SeparatedList(
                            new[]
                            {
                                RoslynTypeSyntaxFactory.Create(
                                    accessor.SourceType),
                                RoslynTypeSyntaxFactory.Create(
                                    accessor.ValueType)
                            }))));
        ExpressionSyntax key = accessor.Kind switch
        {
            XamlBindingPathAccessorKind.IntegerIndexer =>
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(accessor.IntegerIndex)),
            XamlBindingPathAccessorKind.StringIndexer =>
                StringLiteral(accessor.StringIndex!),
            _ => StringLiteral(accessor.Member.Name)
        };
        statement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(register)
                .WithArgumentList(
                    Arguments(
                        key,
                        getter,
                        setter)));
        return true;
    }

    private static ExpressionSyntax CreateOrdinaryBindingAccessorAccess(
        XamlBindingPathAccessor accessor,
        ExpressionSyntax source)
    {
        if (accessor.Kind == XamlBindingPathAccessorKind.Member)
        {
            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                source,
                SyntaxFactory.IdentifierName(
                    EscapeIdentifier(accessor.Member.Name)));
        }

        ExpressionSyntax receiver = source;
        if (accessor.Member.ContainingType != null)
        {
            receiver = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.CastExpression(
                    RoslynTypeSyntaxFactory.Create(
                        accessor.Member.ContainingType),
                    source));
        }
        ExpressionSyntax key = accessor.Kind ==
            XamlBindingPathAccessorKind.IntegerIndexer
                ? SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(accessor.IntegerIndex))
                : StringLiteral(accessor.StringIndex!);
        return SyntaxFactory.ElementAccessExpression(receiver)
            .WithArgumentList(
                SyntaxFactory.BracketedArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(key))));
    }

    public bool TryCreateCompiledBindingAssignment(
        XamlIrCompiledBinding binding,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax source,
        ExpressionSyntax lookupRoot,
        ExpressionSyntax? bindingOwner,
        out StatementSyntax statement)
    {
        statement = SyntaxFactory.EmptyStatement();
        var targetIdentifier =
            member.PropertySystemShape?.Identifier ??
            FindPropertyIdentifier(member.DeclaringType.Symbol as INamedTypeSymbol, member.Name);
        if (targetIdentifier?.ContainingType == null)
            return false;

        var pathElements = new List<ExpressionSyntax>(
            binding.Binding.Function == null
                ? binding.Binding.PathSegments.Length
                : 1);
        if (binding.Binding.Function != null)
            pathElements.Add(CreateCompiledBindingFunctionSegment(binding.Binding));
        else
            foreach (var segment in binding.Binding.PathSegments)
                pathElements.Add(CreateCompiledBindingPathSegment(segment));
        var path = SyntaxFactory.ArrayCreationExpression(
                SyntaxFactory.ArrayType(
                        RoslynTypeSyntaxFactory.CreateGlobalName(
                            "Microsoft", "UI", "Xaml", "Data", "ICompiledBindingPathSegment"))
                    .WithRankSpecifiers(SyntaxFactory.SingletonList(
                        SyntaxFactory.ArrayRankSpecifier(
                            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                SyntaxFactory.OmittedArraySizeExpression())))))
            .WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.ArrayInitializerExpression,
                SyntaxFactory.SeparatedList(pathElements)));

        var optionsAssignments = new List<ExpressionSyntax>
        {
            InitializerAssignment(
                "Mode",
                EnumMember(
                    "Microsoft.UI.Xaml.Data.BindingMode",
                    binding.Binding.Mode switch
                    {
                        ProGPU.Xaml.Binding.XamlCompiledBindingMode.OneWay => "OneWay",
                        ProGPU.Xaml.Binding.XamlCompiledBindingMode.TwoWay => "TwoWay",
                        _ => "OneTime"
                    }))
        };
        var trigger = GetTextArgument(binding.Extension, "UpdateSourceTrigger")?.Trim();
        if (!string.IsNullOrEmpty(trigger))
            optionsAssignments.Add(InitializerAssignment(
                "UpdateSourceTrigger",
                EnumMember("Microsoft.UI.Xaml.Data.UpdateSourceTrigger", trigger!)));
        var language = GetTextArgument(binding.Extension, "ConverterLanguage");
        if (language != null)
            optionsAssignments.Add(InitializerAssignment(
                "ConverterLanguage",
                StringLiteral(language)));
        foreach (var property in new[]
                 {
                     "Converter", "ConverterParameter", "FallbackValue", "TargetNullValue"
                 })
        {
            var value = GetOperationValue(binding.Extension, property);
            if (value == null) continue;
            if (!TryCreateBindingArgumentExpression(value, lookupRoot, out var valueExpression))
                return false;
            optionsAssignments.Add(InitializerAssignment(property, valueExpression));
        }
        if (binding.Binding.BindBackMethod != null)
            optionsAssignments.Add(InitializerAssignment(
                "BindBack",
                CreateBindBackLambda(binding.Binding)));

        var options = SyntaxFactory.ObjectCreationExpression(
                RoslynTypeSyntaxFactory.CreateGlobalName(
                    "Microsoft", "UI", "Xaml", "Data", "CompiledBindingOptions"))
            .WithArgumentList(SyntaxFactory.ArgumentList())
            .WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SeparatedList(optionsAssignments)));
        var setBinding = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "Data", "CompiledBindingOperations"),
            SyntaxFactory.IdentifierName("SetBinding"));
        var propertyIdentifier = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(targetIdentifier.ContainingType),
            SyntaxFactory.IdentifierName(EscapeIdentifier(targetIdentifier.Name)));
        var runtimeSource = binding.Binding.SourceKind ==
            ProGPU.Xaml.Binding.XamlCompiledBindingSourceKind.Context
                ? (ExpressionSyntax)SyntaxFactory.PostfixUnaryExpression(
                    SyntaxKind.SuppressNullableWarningExpression,
                    source)
                : source;
        var arguments = bindingOwner == null
            ? Arguments(
                receiver,
                propertyIdentifier,
                runtimeSource,
                path,
                options)
            : Arguments(
                receiver,
                propertyIdentifier,
                runtimeSource,
                path,
                options,
                bindingOwner);
        statement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(setBinding)
                .WithArgumentList(arguments));
        return true;
    }

    public bool TryCreateCompiledBindingReset(
        ExpressionSyntax source,
        out StatementSyntax statement)
    {
        var clear = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "Data", "CompiledBindingOperations"),
            SyntaxFactory.IdentifierName("ClearBindingsForSource"));
        statement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(clear)
                .WithArgumentList(Arguments(source)));
        return true;
    }

    public bool TryCreateCompiledBindingLifecycle(
        ExpressionSyntax source,
        out RoslynXamlCompiledBindingLifecycleSyntax lifecycle)
    {
        var bindingsType = RoslynTypeSyntaxFactory.CreateGlobalName(
            "Microsoft", "UI", "Xaml", "Data", "ICompiledBindings");
        var property = SyntaxFactory.PropertyDeclaration(
                bindingsType,
                SyntaxFactory.Identifier("Bindings"))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithAccessorList(SyntaxFactory.AccessorList(
                SyntaxFactory.List(new[]
                {
                    SyntaxFactory.AccessorDeclaration(
                            SyntaxKind.GetAccessorDeclaration)
                        .WithSemicolonToken(
                            SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                    SyntaxFactory.AccessorDeclaration(
                            SyntaxKind.SetAccessorDeclaration)
                        .AddModifiers(
                            SyntaxFactory.Token(SyntaxKind.PrivateKeyword))
                        .WithSemicolonToken(
                            SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                })))
            .WithInitializer(SyntaxFactory.EqualsValueClause(
                SyntaxFactory.PostfixUnaryExpression(
                    SyntaxKind.SuppressNullableWarningExpression,
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NullLiteralExpression))))
            .WithSemicolonToken(
                SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        TryCreateCompiledBindingReset(source, out var reset);
        var begin = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName("Bindings"),
                SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                                "Microsoft", "UI", "Xaml", "Data",
                                "CompiledBindingOperations"),
                            SyntaxFactory.IdentifierName("BeginBindings")))
                    .WithArgumentList(Arguments(source))));
        var initialize = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("Bindings"),
                    SyntaxFactory.IdentifierName("Initialize"))));
        lifecycle = new RoslynXamlCompiledBindingLifecycleSyntax(
            ImmutableArray.Create<MemberDeclarationSyntax>(property),
            ImmutableArray.Create(reset, begin),
            ImmutableArray.Create<StatementSyntax>(initialize));
        return true;
    }

    public bool TryCreateDeferredCompiledBindingLifecycle(
        ExpressionSyntax context,
        string controllerIdentifier,
        out RoslynXamlDeferredCompiledBindingLifecycleSyntax lifecycle)
    {
        _ = context;
        var controller = SyntaxFactory.IdentifierName(controllerIdentifier);
        var begin = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                    "Microsoft", "UI", "Xaml", "Data",
                    "CompiledBindingOperations"),
                SyntaxFactory.IdentifierName("BeginBindings")));
        var declaration = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"))
                .AddVariables(
                    SyntaxFactory.VariableDeclarator(controllerIdentifier)
                        .WithInitializer(
                            SyntaxFactory.EqualsValueClause(begin))));
        lifecycle = new RoslynXamlDeferredCompiledBindingLifecycleSyntax(
            controller,
            ImmutableArray.Create<StatementSyntax>(declaration));
        return true;
    }

    public bool TryCreateDeferredMarkupExtensionLifecycle(
        ExpressionSyntax context,
        string ownerIdentifier,
        out RoslynXamlDeferredMarkupExtensionLifecycleSyntax lifecycle)
    {
        _ = context;
        var owner = SyntaxFactory.IdentifierName(ownerIdentifier);
        var begin = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                    "Microsoft", "UI", "Xaml", "Data", "BindingOperations"),
                SyntaxFactory.IdentifierName("BeginBindings")));
        var declaration = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"))
                .AddVariables(
                    SyntaxFactory.VariableDeclarator(ownerIdentifier)
                        .WithInitializer(
                            SyntaxFactory.EqualsValueClause(begin))));
        lifecycle = new RoslynXamlDeferredMarkupExtensionLifecycleSyntax(
            owner,
            ImmutableArray.Create<StatementSyntax>(declaration));
        return true;
    }

    public bool TryCreateDeferredMarkupExtensionFinalization(
        ExpressionSyntax owner,
        ExpressionSyntax root,
        out ImmutableArray<StatementSyntax> statements)
    {
        var attach = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "Markup", "XamlTemplateFactory"),
            SyntaxFactory.IdentifierName("AttachLifetime"));
        statements = ImmutableArray.Create<StatementSyntax>(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(attach)
                    .WithArgumentList(Arguments(root, owner))));
        return true;
    }

    public bool TryCreateDeferredCompiledBindingFinalization(
        ExpressionSyntax controller,
        ExpressionSyntax root,
        out ImmutableArray<StatementSyntax> statements)
    {
        var attach = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "Markup", "XamlTemplateFactory"),
            SyntaxFactory.IdentifierName("AttachBindings"));
        statements = ImmutableArray.Create<StatementSyntax>(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(attach)
                    .WithArgumentList(Arguments(root, controller))));
        return true;
    }

    public bool TryCreateCompiledEventBinding(
        XamlIrCompiledBinding binding,
        XamlMemberInfo member,
        ExpressionSyntax receiver,
        ExpressionSyntax source,
        out StatementSyntax statement)
    {
        statement = SyntaxFactory.EmptyStatement();
        if (binding.Binding.Kind !=
                ProGPU.Xaml.Binding.XamlCompiledBindingKind.Event ||
            binding.Binding.EventHandlerMethod == null ||
            member.Symbol is not IEventSymbol eventSymbol ||
            eventSymbol.Type is not INamedTypeSymbol delegateType ||
            delegateType.DelegateInvokeMethod is not { } invoke)
            return false;

        ExpressionSyntax handlerOwner = source;
        if (binding.Binding.SourceKind ==
            ProGPU.Xaml.Binding.XamlCompiledBindingSourceKind.Context)
        {
            handlerOwner = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.CastExpression(
                    RoslynTypeSyntaxFactory.Create(binding.Binding.SourceType.Symbol),
                    SyntaxFactory.PostfixUnaryExpression(
                        SyntaxKind.SuppressNullableWarningExpression,
                        source)));
        }
        foreach (var segment in binding.Binding.PathSegments)
            handlerOwner = CreateCompiledBindingSegmentAccess(segment, handlerOwner);
        var method = binding.Binding.EventHandlerMethod;
        var parameters = new List<ParameterSyntax>(invoke.Parameters.Length);
        var arguments = new List<ExpressionSyntax>(method.Parameters.Length);
        for (var index = 0; index < invoke.Parameters.Length; index++)
        {
            var identifier = SyntaxFactory.Identifier("__eventArg" +
                index.ToString(CultureInfo.InvariantCulture));
            parameters.Add(SyntaxFactory.Parameter(identifier));
            if (method.Parameters.Length != 0)
                arguments.Add(SyntaxFactory.IdentifierName(identifier));
        }
        var call = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    handlerOwner,
                    SyntaxFactory.IdentifierName(EscapeIdentifier(method.Name))))
            .WithArgumentList(Arguments(arguments.ToArray()));
        var handler = SyntaxFactory.ParenthesizedLambdaExpression(
            SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)),
            call);
        statement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.AddAssignmentExpression,
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiver,
                    SyntaxFactory.IdentifierName(EscapeIdentifier(member.CSharpName))),
                handler));
        return true;
    }

    private static ExpressionSyntax CreateCompiledBindingPathSegment(
        ProGPU.Xaml.Binding.XamlCompiledBindingPathSegment segment)
    {
        var sourceParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier("source"));
        var valueParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"));
        var sourceAccess = CreateCompiledBindingSegmentAccess(
            segment,
            SyntaxFactory.IdentifierName(sourceParameter.Identifier));
        var getter = SyntaxFactory.ParenthesizedLambdaExpression(
                SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(sourceParameter)),
                sourceAccess)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
        ExpressionSyntax setter = SyntaxFactory.LiteralExpression(
            SyntaxKind.NullLiteralExpression);
        if (segment.CanWrite)
        {
            CSharpSyntaxNode setterBody;
            if (segment.Kind ==
                ProGPU.Xaml.Binding.XamlCompiledBindingPathSegmentKind.AttachedMember)
            {
                var setterMethod = segment.SetterMethod!;
                setterBody = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(
                                setterMethod.ContainingType),
                            SyntaxFactory.IdentifierName(
                                EscapeIdentifier(setterMethod.Name))))
                    .WithArgumentList(Arguments(
                        SyntaxFactory.IdentifierName(sourceParameter.Identifier),
                        SyntaxFactory.IdentifierName(valueParameter.Identifier)));
            }
            else
            {
                setterBody = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    sourceAccess,
                    SyntaxFactory.IdentifierName(valueParameter.Identifier));
            }
            setter = SyntaxFactory.ParenthesizedLambdaExpression(
                    SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(new[]
                    {
                        sourceParameter,
                        valueParameter
                    })),
                    setterBody)
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
        }

        if (segment.Kind ==
            ProGPU.Xaml.Binding.XamlCompiledBindingPathSegmentKind.Cast)
        {
            var castType = SyntaxFactory.QualifiedName(
                RoslynTypeSyntaxFactory.CreateGlobalName(
                    "Microsoft", "UI", "Xaml", "Data"),
                SyntaxFactory.GenericName("CompiledBindingCastPathSegment")
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SeparatedList(new[]
                        {
                            RoslynTypeSyntaxFactory.Create(segment.SourceType),
                            RoslynTypeSyntaxFactory.Create(segment.ValueType)
                        }))));
            return SyntaxFactory.ObjectCreationExpression(castType)
                .WithArgumentList(Arguments(
                    StringLiteral(segment.ValueType.ToDisplayString()),
                    getter));
        }

        if (segment.Kind !=
                ProGPU.Xaml.Binding.XamlCompiledBindingPathSegmentKind.Member &&
            segment.Kind !=
                ProGPU.Xaml.Binding.XamlCompiledBindingPathSegmentKind.AttachedMember)
        {
            var indexerType = SyntaxFactory.QualifiedName(
                RoslynTypeSyntaxFactory.CreateGlobalName(
                    "Microsoft", "UI", "Xaml", "Data"),
                SyntaxFactory.GenericName("CompiledBindingIndexerPathSegment")
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SeparatedList(new[]
                        {
                            RoslynTypeSyntaxFactory.Create(segment.SourceType),
                            RoslynTypeSyntaxFactory.Create(segment.ValueType)
                        }))));
            ExpressionSyntax key = segment.Kind ==
                ProGPU.Xaml.Binding.XamlCompiledBindingPathSegmentKind.IntegerIndexer
                    ? SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(segment.IntegerIndex))
                    : StringLiteral(segment.StringIndex!);
            return SyntaxFactory.ObjectCreationExpression(indexerType)
                .WithArgumentList(Arguments(key, getter, setter));
        }

        ExpressionSyntax propertyIdentifier = SyntaxFactory.LiteralExpression(
            SyntaxKind.NullLiteralExpression);
        var identifierOwner = segment.Kind ==
            ProGPU.Xaml.Binding.XamlCompiledBindingPathSegmentKind.AttachedMember
                ? segment.Member.ContainingType
                : segment.SourceType as INamedTypeSymbol;
        var identifierName = segment.Kind ==
            ProGPU.Xaml.Binding.XamlCompiledBindingPathSegmentKind.AttachedMember
                ? segment.AttachedMemberName!
                : segment.Member.Name;
        var identifier = FindPropertyIdentifier(
            identifierOwner,
            identifierName);
        if (identifier?.ContainingType != null)
            propertyIdentifier = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(identifier.ContainingType),
                SyntaxFactory.IdentifierName(EscapeIdentifier(identifier.Name)));

        var genericType = SyntaxFactory.QualifiedName(
            RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "Data"),
            SyntaxFactory.GenericName("CompiledBindingPathSegment")
                .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SeparatedList(new[]
                    {
                        RoslynTypeSyntaxFactory.Create(segment.SourceType),
                        RoslynTypeSyntaxFactory.Create(segment.ValueType)
                    }))));
        return SyntaxFactory.ObjectCreationExpression(genericType)
            .WithArgumentList(Arguments(
                StringLiteral(segment.DisplayName),
                getter,
                setter,
                propertyIdentifier));
    }

    private static ExpressionSyntax CreateCompiledBindingFunctionSegment(
        ProGPU.Xaml.Binding.XamlBoundCompiledBinding binding)
    {
        var function = binding.Function!;
        var sourceParameter = SyntaxFactory.Parameter(
            SyntaxFactory.Identifier("source"));
        var source = (ExpressionSyntax)SyntaxFactory.IdentifierName(
            sourceParameter.Identifier);
        ExpressionSyntax owner = source;
        foreach (var segment in function.OwnerPathSegments)
            owner = CreateCompiledBindingSegmentAccess(segment, owner);

        ExpressionSyntax target = function.IsStatic
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(
                    function.Method.ContainingType),
                SyntaxFactory.IdentifierName(
                    EscapeIdentifier(function.Method.Name)))
            : SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                owner,
                SyntaxFactory.IdentifierName(
                    EscapeIdentifier(function.Method.Name)));
        var arguments = new List<ExpressionSyntax>(function.Arguments.Length);
        foreach (var argument in function.Arguments)
        {
            ExpressionSyntax expression;
            if (argument.Kind ==
                ProGPU.Xaml.Binding.XamlCompiledBindingFunctionArgumentKind.Path)
            {
                expression = source;
                foreach (var segment in argument.PathSegments)
                    expression = CreateCompiledBindingSegmentAccess(
                        segment,
                        expression);
            }
            else
            {
                expression = CreateCompiledBindingFunctionLiteral(argument);
            }
            arguments.Add(SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.CastExpression(
                    RoslynTypeSyntaxFactory.Create(argument.Parameter.Type),
                    expression)));
        }
        var invocation = SyntaxFactory.InvocationExpression(target)
            .WithArgumentList(Arguments(arguments.ToArray()));
        var getter = SyntaxFactory.ParenthesizedLambdaExpression(
                SyntaxFactory.ParameterList(
                    SyntaxFactory.SingletonSeparatedList(sourceParameter)),
                invocation)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)));

        var dependencies = new List<ExpressionSyntax>();
        foreach (var argument in function.Arguments)
        {
            if (argument.Kind ==
                    ProGPU.Xaml.Binding.XamlCompiledBindingFunctionArgumentKind.Path &&
                !argument.PathSegments.IsEmpty)
                dependencies.Add(CreateCompiledBindingDependencyPath(
                    argument.PathSegments));
        }
        var dependencyArray = SyntaxFactory.ArrayCreationExpression(
                SyntaxFactory.ArrayType(
                        RoslynTypeSyntaxFactory.CreateGlobalName(
                            "Microsoft", "UI", "Xaml", "Data",
                            "ICompiledBindingPathSegment"))
                    .WithRankSpecifiers(SyntaxFactory.List(new[]
                    {
                        SyntaxFactory.ArrayRankSpecifier(
                            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                SyntaxFactory.OmittedArraySizeExpression())),
                        SyntaxFactory.ArrayRankSpecifier(
                            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                SyntaxFactory.OmittedArraySizeExpression()))
                    })))
            .WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.ArrayInitializerExpression,
                SyntaxFactory.SeparatedList(dependencies)));
        var functionType = SyntaxFactory.QualifiedName(
            RoslynTypeSyntaxFactory.CreateGlobalName(
                "Microsoft", "UI", "Xaml", "Data"),
            SyntaxFactory.GenericName("CompiledBindingFunctionPathSegment")
                .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SeparatedList(new[]
                    {
                        RoslynTypeSyntaxFactory.Create(binding.SourceType.Symbol),
                        RoslynTypeSyntaxFactory.Create(function.Method.ReturnType)
                    }))));
        return SyntaxFactory.ObjectCreationExpression(functionType)
            .WithArgumentList(Arguments(
                StringLiteral(function.Method.Name),
                getter,
                CreateCompiledBindingDependencyPath(
                    function.OwnerPathSegments),
                dependencyArray));
    }

    private static ExpressionSyntax CreateCompiledBindingDependencyPath(
        ImmutableArray<ProGPU.Xaml.Binding.XamlCompiledBindingPathSegment> segments)
    {
        var elements = new List<ExpressionSyntax>(segments.Length);
        foreach (var segment in segments)
            elements.Add(CreateCompiledBindingPathSegment(segment));
        return SyntaxFactory.ArrayCreationExpression(
                SyntaxFactory.ArrayType(
                        RoslynTypeSyntaxFactory.CreateGlobalName(
                            "Microsoft", "UI", "Xaml", "Data",
                            "ICompiledBindingPathSegment"))
                    .WithRankSpecifiers(SyntaxFactory.SingletonList(
                        SyntaxFactory.ArrayRankSpecifier(
                            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                SyntaxFactory.OmittedArraySizeExpression())))))
            .WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.ArrayInitializerExpression,
                SyntaxFactory.SeparatedList(elements)));
    }

    private static ExpressionSyntax CreateCompiledBindingFunctionLiteral(
        ProGPU.Xaml.Binding.XamlCompiledBindingFunctionArgument argument)
    {
        return argument.LiteralValue switch
        {
            string value => StringLiteral(value),
            bool value => SyntaxFactory.LiteralExpression(
                value
                    ? SyntaxKind.TrueLiteralExpression
                    : SyntaxKind.FalseLiteralExpression),
            int value => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(value)),
            double value => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(value)),
            _ => SyntaxFactory.LiteralExpression(
                SyntaxKind.NullLiteralExpression)
        };
    }

    private static ExpressionSyntax CreateCompiledBindingSegmentAccess(
        ProGPU.Xaml.Binding.XamlCompiledBindingPathSegment segment,
        ExpressionSyntax source)
    {
        if (segment.Kind ==
            ProGPU.Xaml.Binding.XamlCompiledBindingPathSegmentKind.Member)
        {
            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                source,
                SyntaxFactory.IdentifierName(EscapeIdentifier(segment.Member.Name)));
        }
        if (segment.Kind ==
            ProGPU.Xaml.Binding.XamlCompiledBindingPathSegmentKind.Cast)
        {
            return SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.CastExpression(
                    RoslynTypeSyntaxFactory.Create(segment.ValueType),
                    source));
        }
        if (segment.Kind ==
            ProGPU.Xaml.Binding.XamlCompiledBindingPathSegmentKind.AttachedMember)
        {
            var getter = (IMethodSymbol)segment.Member;
            return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(
                            getter.ContainingType),
                        SyntaxFactory.IdentifierName(
                            EscapeIdentifier(getter.Name))))
                .WithArgumentList(Arguments(source));
        }

        ExpressionSyntax receiver = source;
        if (segment.Member.ContainingType != null)
        {
            receiver = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.CastExpression(
                    RoslynTypeSyntaxFactory.Create(segment.Member.ContainingType),
                    source));
        }
        ExpressionSyntax key = segment.Kind ==
            ProGPU.Xaml.Binding.XamlCompiledBindingPathSegmentKind.IntegerIndexer
                ? SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(segment.IntegerIndex))
                : StringLiteral(segment.StringIndex!);
        return SyntaxFactory.ElementAccessExpression(receiver)
            .WithArgumentList(SyntaxFactory.BracketedArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(key))));
    }

    private static ParenthesizedLambdaExpressionSyntax CreateBindBackLambda(
        ProGPU.Xaml.Binding.XamlBoundCompiledBinding binding)
    {
        var method = binding.BindBackMethod!;
        var sourceParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier("source"));
        var valueParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"));
        var typedSource = SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.CastExpression(
                RoslynTypeSyntaxFactory.Create(binding.SourceType.Symbol),
                SyntaxFactory.IdentifierName(sourceParameter.Identifier)));
        var typedValue = SyntaxFactory.CastExpression(
            RoslynTypeSyntaxFactory.Create(method.Parameters[0].Type),
            SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.SuppressNullableWarningExpression,
                SyntaxFactory.IdentifierName(valueParameter.Identifier)));
        var invocation = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    typedSource,
                    SyntaxFactory.IdentifierName(EscapeIdentifier(method.Name))))
            .WithArgumentList(Arguments(typedValue));
        return SyntaxFactory.ParenthesizedLambdaExpression(
                SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(new[]
                {
                    sourceParameter,
                    valueParameter
                })),
                invocation)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
    }

    private static bool IsDependencyObject(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (string.Equals(
                    current.ToDisplayString(),
                    "Microsoft.UI.Xaml.DependencyObject",
                    StringComparison.Ordinal))
                return true;
        return false;
    }

    public bool TryCreateMarkupExtensionInvocation(
        XamlIrObject extension,
        ExpressionSyntax extensionInstance,
        XamlTypeInfo targetType,
        ExpressionSyntax? targetObject,
        XamlMemberInfo? targetMember,
        ExpressionSyntax rootObject,
        string? resourceUri,
        out ExpressionSyntax expression)
    {
        var shape = extension.Type.Symbol?.MarkupExtensionShape;
        if (extension.Type.Symbol?.IsMarkupExtension != true ||
            shape?.IsValid != true ||
            shape.IdentityKind != XamlMarkupExtensionIdentityKind.BaseType ||
            !string.Equals(
                shape.Identity,
                "Microsoft.UI.Xaml.Markup.MarkupExtension",
                StringComparison.Ordinal))
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return false;
        }

        var evaluate = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                "ProGPU", "Xaml", "Runtime", "WinUiMarkupExtensionRuntime"),
            SyntaxFactory.GenericName("Evaluate")
                .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(
                    RoslynTypeSyntaxFactory.Create(targetType.Symbol)))));
        var declaringType = targetMember?.Symbol?.ContainingType;
        var targetTypeExpression = declaringType == null
            ? (ExpressionSyntax)SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
            : SyntaxFactory.TypeOfExpression(RoslynTypeSyntaxFactory.Create(declaringType));
        expression = SyntaxFactory.InvocationExpression(evaluate).WithArgumentList(Arguments(
            extensionInstance,
            targetObject ?? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression),
            targetTypeExpression,
            targetMember == null
                ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                : StringLiteral(targetMember.Name),
            rootObject,
            resourceUri == null
                ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                : StringLiteral(resourceUri)));
        return true;
    }

    private static IFieldSymbol? FindPropertyIdentifier(INamedTypeSymbol? owner, string propertyName)
    {
        for (var current = owner; current != null; current = current.BaseType)
        {
            var identifier = current.GetMembers(propertyName + "Property")
                .OfType<IFieldSymbol>()
                .FirstOrDefault(static field => field.IsStatic);
            if (identifier != null) return identifier;
        }
        return null;
    }

    private static INamedTypeSymbol? FindPresentationType(IAssemblySymbol assembly, string typeName)
    {
        INamedTypeSymbol? FindInAssembly(IAssemblySymbol candidate)
        {
            for (var index = 0; index < PresentationNamespaces.Length; index++)
            {
                var symbol = candidate.GetTypeByMetadataName(PresentationNamespaces[index] + "." + typeName);
                if (symbol != null) return symbol;
            }
            return candidate.GetTypeByMetadataName(typeName);
        }

        var local = FindInAssembly(assembly);
        if (local != null) return local;
        foreach (var module in assembly.Modules)
            foreach (var reference in module.ReferencedAssemblySymbols)
                if (FindInAssembly(reference) is { } referenced) return referenced;
        return null;
    }

    public bool TryCreateMarkupExtensionExpression(
        XamlIrObject extension,
        XamlTypeInfo targetType,
        ExpressionSyntax lookupRoot,
        out ExpressionSyntax expression)
    {
        var name = extension.Type.RequestedName.LocalName;
        if (string.Equals(name, "RelativeSource", StringComparison.Ordinal) ||
            string.Equals(name, "RelativeSourceExtension", StringComparison.Ordinal))
            return TryCreateRelativeSourceExpression(extension, out expression);
        if (string.Equals(name, "Binding", StringComparison.Ordinal) ||
            string.Equals(name, "BindingExtension", StringComparison.Ordinal))
        {
            if (CanStoreBindingDescriptor(targetType))
                return TryCreateBindingExpression(
                    extension,
                    lookupRoot,
                    out expression);
            expression = SyntaxFactory.LiteralExpression(
                SyntaxKind.NullLiteralExpression);
            return false;
        }
        if (string.Equals(name, "TemplateBinding", StringComparison.Ordinal) ||
            string.Equals(name, "TemplateBindingExtension", StringComparison.Ordinal))
        {
            var path = (GetTextArgument(extension, "Property") ??
                        GetFirstTextArgument(extension))?.Trim();
            if (!string.IsNullOrEmpty(path))
            {
                var relativeSource = SyntaxFactory.ObjectCreationExpression(
                        RoslynTypeSyntaxFactory.CreateGlobalName(
                            "Microsoft", "UI", "Xaml", "Data", "RelativeSource"))
                    .WithArgumentList(SyntaxFactory.ArgumentList())
                    .WithInitializer(SyntaxFactory.InitializerExpression(
                        SyntaxKind.ObjectInitializerExpression,
                        SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName("Mode"),
                                EnumMember(
                                    "Microsoft.UI.Xaml.Data.RelativeSourceMode",
                                    "TemplatedParent")))));
                expression = SyntaxFactory.ObjectCreationExpression(
                        RoslynTypeSyntaxFactory.CreateGlobalName(
                            "Microsoft", "UI", "Xaml", "Data", "Binding"))
                    .WithArgumentList(SyntaxFactory.ArgumentList())
                    .WithInitializer(SyntaxFactory.InitializerExpression(
                        SyntaxKind.ObjectInitializerExpression,
                        SyntaxFactory.SeparatedList<ExpressionSyntax>(new ExpressionSyntax[]
                        {
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName("Path"),
                                StringLiteral(path!)),
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName("RelativeSource"),
                                relativeSource)
                        })));
                return true;
            }
        }
        expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        return false;
    }

    public bool TryCreateMarkupExtensionExpression(
        XamlIrObject extension,
        XamlTypeInfo targetType,
        out ExpressionSyntax expression)
    {
        var name = extension.Type.RequestedName.LocalName;
        if (string.Equals(name, "Null", StringComparison.Ordinal) ||
            string.Equals(name, "NullExtension", StringComparison.Ordinal))
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return true;
        }

        if (string.Equals(name, "Type", StringComparison.Ordinal) ||
            string.Equals(name, "TypeExtension", StringComparison.Ordinal))
        {
            var typeValue = extension.Operations.SelectMany(static operation => operation.Values)
                .OfType<XamlIrType>()
                .FirstOrDefault();
            if (typeValue?.Type.Symbol != null)
            {
                expression = SyntaxFactory.TypeOfExpression(
                    RoslynTypeSyntaxFactory.Create(typeValue.Type.Symbol.Symbol));
                return true;
            }
        }

        if (string.Equals(name, "Static", StringComparison.Ordinal) ||
            string.Equals(name, "StaticExtension", StringComparison.Ordinal))
            return TryCreateStaticMemberExpression(
                extension,
                out expression);

        var key = GetFirstTextArgument(extension);
        if (key != null && (string.Equals(name, "ThemeResource", StringComparison.Ordinal) ||
                            string.Equals(name, "ThemeResourceExtension", StringComparison.Ordinal)))
        {
            var isBrush = string.Equals(targetType.Symbol.Name, "Brush", StringComparison.Ordinal) ||
                          targetType.Symbol.Name.EndsWith("Brush", StringComparison.Ordinal);
            var typeName = isBrush
                ? RoslynTypeSyntaxFactory.CreateGlobalName("ProGPU", "Vector", "ThemeResourceBrush")
                : RoslynTypeSyntaxFactory.CreateGlobalName("Microsoft", "UI", "Xaml", "ThemeResource");
            expression = SyntaxFactory.ObjectCreationExpression(typeName)
                .WithArgumentList(Arguments(StringLiteral(key)));
            return true;
        }

        expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        return false;
    }

    private static bool CanStoreBindingDescriptor(XamlTypeInfo targetType)
    {
        if (targetType.Symbol.SpecialType == SpecialType.System_Object)
            return true;
        var metadataName = targetType.MetadataName;
        return string.Equals(
                   metadataName,
                   "Microsoft.UI.Xaml.Data.Binding",
                   StringComparison.Ordinal) ||
               string.Equals(
                   metadataName,
                   "Microsoft.UI.Xaml.Data.BindingBase",
                   StringComparison.Ordinal);
    }

    private static bool TryCreateBindingExpression(
        XamlIrObject extension,
        ExpressionSyntax lookupRoot,
        out ExpressionSyntax expression)
    {
        var assignments = new List<ExpressionSyntax>();
        var path = (GetTextArgument(extension, "Path") ?? GetFirstTextArgument(extension))?.Trim();
        if (!string.IsNullOrEmpty(path))
            assignments.Add(InitializerAssignment("Path", StringLiteral(path!)));

        foreach (var property in new[]
                 {
                     "ElementName", "ConverterLanguage"
                 })
        {
            var value = GetTextArgument(extension, property);
            if (value != null)
                assignments.Add(InitializerAssignment(property, StringLiteral(value)));
        }
        foreach (var enumProperty in new[]
                 {
                     ("Mode", "Microsoft.UI.Xaml.Data.BindingMode"),
                     ("UpdateSourceTrigger", "Microsoft.UI.Xaml.Data.UpdateSourceTrigger")
                 })
        {
            var value = GetTextArgument(extension, enumProperty.Item1);
            if (!string.IsNullOrWhiteSpace(value))
                assignments.Add(InitializerAssignment(
                    enumProperty.Item1,
                    EnumMember(enumProperty.Item2, value!.Trim())));
        }
        foreach (var property in new[]
                 {
                     "RelativeSource", "Source", "Converter", "ConverterParameter",
                     "FallbackValue", "TargetNullValue"
                 })
        {
            var value = GetOperationValue(extension, property);
            if (value == null) continue;
            if (!TryCreateBindingArgumentExpression(value, lookupRoot, out var valueExpression))
            {
                expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
                return false;
            }
            assignments.Add(InitializerAssignment(property, valueExpression));
        }

        expression = SyntaxFactory.ObjectCreationExpression(
                RoslynTypeSyntaxFactory.CreateGlobalName(
                    "Microsoft", "UI", "Xaml", "Data", "Binding"))
            .WithArgumentList(SyntaxFactory.ArgumentList())
            .WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SeparatedList(assignments)));
        return true;
    }

    private static bool TryCreateRelativeSourceExpression(
        XamlIrObject extension,
        out ExpressionSyntax expression)
    {
        var mode = (GetTextArgument(extension, "Mode") ??
                    GetFirstTextArgument(extension))?.Trim();
        if (string.IsNullOrEmpty(mode))
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return false;
        }
        expression = SyntaxFactory.ObjectCreationExpression(
                RoslynTypeSyntaxFactory.CreateGlobalName(
                    "Microsoft", "UI", "Xaml", "Data", "RelativeSource"))
            .WithArgumentList(SyntaxFactory.ArgumentList())
            .WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                    InitializerAssignment(
                        "Mode",
                        EnumMember("Microsoft.UI.Xaml.Data.RelativeSourceMode", mode!)))));
        return true;
    }

    private static bool TryCreateBindingArgumentExpression(
        XamlIrValue value,
        ExpressionSyntax lookupRoot,
        out ExpressionSyntax expression)
    {
        if (value is XamlIrText text)
        {
            expression = StringLiteral(text.Text);
            return true;
        }
        if (value is XamlIrObject staticExtension &&
            (string.Equals(
                 staticExtension.Type.RequestedName.LocalName,
                 "Static",
                 StringComparison.Ordinal) ||
             string.Equals(
                 staticExtension.Type.RequestedName.LocalName,
                 "StaticExtension",
                 StringComparison.Ordinal)))
            return TryCreateStaticMemberExpression(
                staticExtension,
                out expression);
        if (value is XamlIrObject nested &&
            (string.Equals(nested.Type.RequestedName.LocalName, "RelativeSource", StringComparison.Ordinal) ||
             string.Equals(nested.Type.RequestedName.LocalName, "RelativeSourceExtension", StringComparison.Ordinal)))
            return TryCreateRelativeSourceExpression(nested, out expression);
        if (value is XamlIrResourceReference resource)
        {
            var key = StringLiteral(resource.Reference.ResourceKey.Text);
            if (resource.Reference.Kind ==
                ProGPU.Xaml.Resources.XamlResourceReferenceKind.Theme)
            {
                expression = SyntaxFactory.ObjectCreationExpression(
                        RoslynTypeSyntaxFactory.CreateGlobalName(
                            "Microsoft", "UI", "Xaml", "ThemeResource"))
                    .WithArgumentList(Arguments(lookupRoot, key));
                return true;
            }
            var resolve = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(
                    "Microsoft", "UI", "Xaml", "XamlResourceResolver"),
                SyntaxFactory.GenericName("Resolve")
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))))));
            expression = SyntaxFactory.InvocationExpression(resolve)
                .WithArgumentList(Arguments(lookupRoot, key));
            return true;
        }
        expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        return false;
    }

    private static bool TryCreateStaticMemberExpression(
        XamlIrObject extension,
        out ExpressionSyntax expression)
    {
        var staticValue = extension.Operations
            .SelectMany(static operation => operation.Values)
            .OfType<XamlIrStaticMember>()
            .FirstOrDefault();
        var member = staticValue?.Value.Member;
        if (member?.ContainingType != null)
        {
            expression = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(
                    member.ContainingType),
                SyntaxFactory.IdentifierName(
                    EscapeIdentifier(member.Name)));
            return true;
        }

        expression = SyntaxFactory.LiteralExpression(
            SyntaxKind.NullLiteralExpression);
        return false;
    }

    private static XamlIrValue? GetOperationValue(XamlIrObject extension, string memberName)
    {
        foreach (var operation in extension.Operations)
        {
            if ((string.Equals(operation.Member.Symbol?.Name, memberName, StringComparison.Ordinal) ||
                 string.Equals(operation.Member.RequestedName.LocalName, memberName, StringComparison.Ordinal)) &&
                operation.Values.Length != 0)
                return operation.Values[0];
        }
        return null;
    }

    private static AssignmentExpressionSyntax InitializerAssignment(
        string property,
        ExpressionSyntax value) =>
        SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName(property),
            value);

    private static ExpressionSyntax EnumMember(string metadataName, string memberName) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            (ExpressionSyntax)RoslynTypeSyntaxFactory.CreateGlobalName(metadataName.Split('.')),
            SyntaxFactory.IdentifierName(memberName));

    private static bool TryCreateFloatTuple(XamlTypeInfo type, string text, out ExpressionSyntax expression)
    {
        var parts = text.Split(',');
        if (parts.Length != 1 && parts.Length != 2 && parts.Length != 4)
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return false;
        }

        var arguments = new List<ArgumentSyntax>(parts.Length);
        foreach (var part in parts)
        {
            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
                return false;
            }
            arguments.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(value))));
        }

        expression = SyntaxFactory.ObjectCreationExpression(
                RoslynTypeSyntaxFactory.Create(
                    type.Symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated)))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
        return true;
    }

    private static bool TryCreateNumericsVector(
        XamlTypeInfo type,
        string text,
        int componentCount,
        out ExpressionSyntax expression)
    {
        var parts = text.Split(',');
        if (parts.Length != componentCount)
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return false;
        }
        var arguments = new List<ArgumentSyntax>(componentCount);
        foreach (var part in parts)
        {
            if (!float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
                return false;
            }
            arguments.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(value))));
        }
        expression = SyntaxFactory.ObjectCreationExpression(RoslynTypeSyntaxFactory.Create(type.Symbol))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
        return true;
    }

    private static bool TryCreateFloatComponents(
        XamlTypeInfo type,
        string text,
        int componentCount,
        out ExpressionSyntax expression) =>
        TryCreateNumericsVector(type, text, componentCount, out expression);

    private static bool TryCreateVector4(
        XamlTypeInfo type,
        string text,
        out ExpressionSyntax expression)
    {
        if (TryParseColor(text, out var alpha, out var red, out var green, out var blue))
        {
            expression = SyntaxFactory.ObjectCreationExpression(RoslynTypeSyntaxFactory.Create(type.Symbol))
                .WithArgumentList(Arguments(
                    FloatLiteral(red / 255f),
                    FloatLiteral(green / 255f),
                    FloatLiteral(blue / 255f),
                    FloatLiteral(alpha / 255f)));
            return true;
        }
        if (TryParseNamedColor(text, out alpha, out red, out green, out blue))
        {
            expression = SyntaxFactory.ObjectCreationExpression(RoslynTypeSyntaxFactory.Create(type.Symbol))
                .WithArgumentList(Arguments(
                    FloatLiteral(red / 255f),
                    FloatLiteral(green / 255f),
                    FloatLiteral(blue / 255f),
                    FloatLiteral(alpha / 255f)));
            return true;
        }
        return TryCreateNumericsVector(type, text, 4, out expression);
    }

    private static bool TryCreateBrush(string text, out ExpressionSyntax expression)
    {
        if (!TryParseColor(text, out var alpha, out var red, out var green, out var blue) &&
            !TryParseNamedColor(text, out alpha, out red, out green, out blue))
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return false;
        }
        var color = SyntaxFactory.ObjectCreationExpression(
                RoslynTypeSyntaxFactory.CreateGlobalName("System", "Numerics", "Vector4"))
            .WithArgumentList(Arguments(
                FloatLiteral(red / 255f),
                FloatLiteral(green / 255f),
                FloatLiteral(blue / 255f),
                FloatLiteral(alpha / 255f)));
        expression = SyntaxFactory.ObjectCreationExpression(
                RoslynTypeSyntaxFactory.CreateGlobalName("ProGPU", "Vector", "SolidColorBrush"))
            .WithArgumentList(Arguments(color));
        return true;
    }

    private static LiteralExpressionSyntax FloatLiteral(float value) =>
        SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(value));

    private static bool TryParseNamedColor(
        string text,
        out byte alpha,
        out byte red,
        out byte green,
        out byte blue)
    {
        alpha = 255;
        switch (text.Trim().ToUpperInvariant())
        {
            case "TRANSPARENT": alpha = red = green = blue = 0; return true;
            case "BLACK": red = green = blue = 0; return true;
            case "WHITE": red = green = blue = 255; return true;
            case "LIGHTGRAY":
            case "LIGHTGREY": red = green = blue = 211; return true;
            case "GRAY":
            case "GREY": red = green = blue = 128; return true;
            case "RED": red = 255; green = blue = 0; return true;
            case "GREEN": green = 128; red = blue = 0; return true;
            case "BLUE": blue = 255; red = green = 0; return true;
            case "YELLOW": red = green = 255; blue = 0; return true;
            default: alpha = red = green = blue = 0; return false;
        }
    }

    private static bool TryCreateGridLength(XamlTypeInfo type, string text, out ExpressionSyntax expression)
    {
        text = text.Trim();
        var typeExpression = TypeExpression(type);
        if (string.Equals(text, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            expression = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                typeExpression,
                SyntaxFactory.IdentifierName("Auto"));
            return true;
        }

        if (text.EndsWith("*", StringComparison.Ordinal))
        {
            var scalarText = text.Substring(0, text.Length - 1);
            if (scalarText.Length == 0) scalarText = "1";
            if (float.TryParse(scalarText, NumberStyles.Float, CultureInfo.InvariantCulture, out var scalar))
            {
                expression = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        typeExpression,
                        SyntaxFactory.IdentifierName("Star")))
                    .WithArgumentList(Arguments(SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(scalar))));
                return true;
            }
        }

        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var absolute))
        {
            var unitType = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                RoslynTypeSyntaxFactory.CreateGlobalName("Microsoft", "UI", "Xaml", "GridUnitType"),
                SyntaxFactory.IdentifierName("Absolute"));
            expression = SyntaxFactory.ObjectCreationExpression(RoslynTypeSyntaxFactory.Create(type.Symbol))
                .WithArgumentList(Arguments(
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(absolute)),
                    unitType));
            return true;
        }

        expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        return false;
    }

    private static bool TryCreateColor(XamlTypeInfo type, string text, out ExpressionSyntax expression)
    {
        if (!TryParseColor(text, out var alpha, out var red, out var green, out var blue))
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return false;
        }

        expression = SyntaxFactory.ObjectCreationExpression(RoslynTypeSyntaxFactory.Create(type.Symbol))
            .WithArgumentList(Arguments(
                ByteLiteral(red), ByteLiteral(green), ByteLiteral(blue), ByteLiteral(alpha)));
        return true;
    }

    private static LiteralExpressionSyntax ByteLiteral(byte value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal((int)value));

    private static bool TryParseColor(
        string text,
        out byte alpha,
        out byte red,
        out byte green,
        out byte blue)
    {
        text = text.Trim();
        alpha = red = green = blue = 0;
        if ((text.Length != 7 && text.Length != 9) || text[0] != '#') return false;
        var offset = text.Length == 9 ? 3 : 1;
        var alphaText = text.Length == 9 ? text.Substring(1, 2) : "FF";
        return byte.TryParse(alphaText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out alpha) &&
               byte.TryParse(text.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red) &&
               byte.TryParse(text.Substring(offset + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green) &&
               byte.TryParse(text.Substring(offset + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue);
    }

    private static bool TryCreateKeyTime(XamlTypeInfo type, string text, out ExpressionSyntax expression)
    {
        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value))
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return false;
        }

        expression = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                TypeExpression(type),
                SyntaxFactory.IdentifierName("FromTimeSpan")))
            .WithArgumentList(Arguments(CreateTimeSpanExpression(value)));
        return true;
    }

    private static bool TryCreateDuration(XamlTypeInfo type, string text, out ExpressionSyntax expression)
    {
        text = text.Trim();
        if (string.Equals(text, "Automatic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "Forever", StringComparison.OrdinalIgnoreCase))
        {
            expression = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                TypeExpression(type),
                SyntaxFactory.IdentifierName(string.Equals(text, "Forever", StringComparison.OrdinalIgnoreCase)
                    ? "Forever"
                    : "Automatic"));
            return true;
        }
        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value))
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return false;
        }
        expression = SyntaxFactory.ObjectCreationExpression(RoslynTypeSyntaxFactory.Create(type.Symbol))
            .WithArgumentList(Arguments(CreateTimeSpanExpression(value)));
        return true;
    }

    private static bool TryCreateKeySpline(XamlTypeInfo type, string text, out ExpressionSyntax expression)
    {
        var parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x1) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y1) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x2) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y2))
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return false;
        }

        ExpressionSyntax Point(double x, double y) =>
            SyntaxFactory.ObjectCreationExpression(
                    RoslynTypeSyntaxFactory.CreateGlobalName("Windows", "Foundation", "Point"))
                .WithArgumentList(Arguments(
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(x)),
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(y))));
        expression = SyntaxFactory.ObjectCreationExpression(
                RoslynTypeSyntaxFactory.Create(
                    type.Symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated)))
            .WithArgumentList(SyntaxFactory.ArgumentList())
            .WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SeparatedList<ExpressionSyntax>(new ExpressionSyntax[]
                {
                    SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName("ControlPoint1"), Point(x1, y1)),
                    SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName("ControlPoint2"), Point(x2, y2))
                })));
        return true;
    }

    private static bool TryCreateTargetPropertyPath(XamlTypeInfo type, string text, out ExpressionSyntax expression)
    {
        var pathAssignment = SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName("Path"),
            SyntaxFactory.ObjectCreationExpression(
                    RoslynTypeSyntaxFactory.CreateGlobalName("Microsoft", "UI", "Xaml", "PropertyPath"))
                .WithArgumentList(Arguments(StringLiteral(text))));
        expression = SyntaxFactory.ObjectCreationExpression(
                RoslynTypeSyntaxFactory.Create(
                    type.Symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated)))
            .WithArgumentList(SyntaxFactory.ArgumentList())
            .WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(pathAssignment)));
        return true;
    }

    private static bool TryCreateWindowsColor(XamlTypeInfo type, string text, out ExpressionSyntax expression)
    {
        if (!TryParseColor(text, out var alpha, out var red, out var green, out var blue))
        {
            expression = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            return false;
        }
        expression = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                TypeExpression(type),
                SyntaxFactory.IdentifierName("FromArgb")))
            .WithArgumentList(Arguments(ByteLiteral(alpha), ByteLiteral(red), ByteLiteral(green), ByteLiteral(blue)));
        return true;
    }

    private static ExpressionSyntax CreateWindowsColorExpression(
        ITypeSymbol type,
        byte alpha,
        byte red,
        byte green,
        byte blue) =>
        SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(type),
                SyntaxFactory.IdentifierName("FromArgb")))
            .WithArgumentList(Arguments(ByteLiteral(alpha), ByteLiteral(red), ByteLiteral(green), ByteLiteral(blue)));

    private static ObjectCreationExpressionSyntax CreateTimeSpanExpression(TimeSpan value) =>
        SyntaxFactory.ObjectCreationExpression(RoslynTypeSyntaxFactory.CreateGlobalName("System", "TimeSpan"))
            .WithArgumentList(Arguments(SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(value.Ticks))));

    internal static LiteralExpressionSyntax StringLiteral(string value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value));

    private static ExpressionSyntax TypeExpression(XamlTypeInfo type) =>
        (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(type.Symbol);

    private static ArgumentListSyntax Arguments(params ExpressionSyntax[] expressions)
    {
        var arguments = new ArgumentSyntax[expressions.Length];
        for (var index = 0; index < expressions.Length; index++)
        {
            arguments[index] = SyntaxFactory.Argument(expressions[index]);
        }
        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments));
    }

    private static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;

    private static string? GetFirstTextArgument(XamlMarkupExtension extension) =>
        extension.PositionalArguments.Count > 0 && extension.PositionalArguments[0] is XamlMarkupTextValue text
            ? text.Text
            : null;

    private static string? GetFirstTextArgument(XamlIrObject extension)
    {
        foreach (var operation in extension.Operations)
        {
            if (operation.Member.Kind != ProGPU.Xaml.Binding.XamlBoundReferenceKind.Directive ||
                !string.Equals(operation.Member.RequestedName.LocalName, "PositionalParameters", StringComparison.Ordinal))
                continue;
            foreach (var value in operation.Values)
                if (value is XamlIrText text) return text.Text;
        }
        return null;
    }

    private static string? GetTextArgument(XamlIrObject extension, string memberName)
    {
        foreach (var operation in extension.Operations)
        {
            if (!string.Equals(operation.Member.Symbol?.Name ?? operation.Member.RequestedName.LocalName,
                    memberName, StringComparison.Ordinal)) continue;
            return operation.Values.OfType<XamlIrText>().FirstOrDefault()?.Text;
        }
        return null;
    }

    private static XamlBoundMember? FindBoundMember(
        XamlBoundObject value,
        string memberName) =>
        value.Members.FirstOrDefault(member =>
            string.Equals(
                member.Member.Symbol?.Name ??
                member.Member.RequestedName.LocalName,
                memberName,
                StringComparison.Ordinal));

    private static string? FirstBoundText(
        XamlBoundMember? member) =>
        member?.Values.OfType<XamlBoundText>()
            .FirstOrDefault()?
            .Text;
}
