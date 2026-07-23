using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Diagnostics;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Lowering;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Resources;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Xaml.Roslyn;

/// <summary>
/// Compiles XAML through the schema-neutral infoset, Roslyn symbol binder, and construction IR,
/// then creates C# exclusively with typed Roslyn syntax nodes.
/// </summary>
public sealed class CSharpXamlEmitter : IXamlCodeEmitter
{
    public XamlCompilationResult Emit(
        XamlDocumentSyntax document,
        IXamlTypeSystem typeSystem,
        IXamlFrameworkProfile framework,
        XamlCompilerOptions options)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        if (typeSystem == null) throw new ArgumentNullException(nameof(typeSystem));
        if (framework == null) throw new ArgumentNullException(nameof(framework));
        if (options == null) throw new ArgumentNullException(nameof(options));

        if (framework is not IRoslynXamlFrameworkProfile roslynFramework)
        {
            var diagnostic = CreateError(
                document,
                "PGXAML9001",
                $"Framework profile '{framework.Id}' does not provide Roslyn expression lowering.",
                document.Root?.Span ?? default,
                "EXT-003");
            return new XamlCompilationResult(document, Array.Empty<XamlGeneratedSource>(), new[] { diagnostic });
        }

        var mode = options.Strict ? XamlParseMode.Strict : XamlParseMode.Recovering;
        var infoset = new XamlInfosetConverter().Convert(
            document,
            new XamlInfosetConversionOptions { Mode = mode });
        return Emit(infoset, typeSystem, framework, options);
    }

    public XamlCompilationResult Emit(
        XamlInfosetDocument infoset,
        IXamlTypeSystem typeSystem,
        IXamlFrameworkProfile framework,
        XamlCompilerOptions options)
    {
        if (infoset == null) throw new ArgumentNullException(nameof(infoset));
        if (typeSystem == null) throw new ArgumentNullException(nameof(typeSystem));
        if (framework == null) throw new ArgumentNullException(nameof(framework));
        if (options == null) throw new ArgumentNullException(nameof(options));
        var document = infoset.Syntax;
        if (framework is not IRoslynXamlFrameworkProfile roslynFramework)
        {
            var diagnostic = CreateError(
                document,
                "PGXAML9001",
                $"Framework profile '{framework.Id}' does not provide Roslyn expression lowering.",
                document.Root?.Span ?? default,
                "EXT-003");
            return new XamlCompilationResult(document, Array.Empty<XamlGeneratedSource>(), new[] { diagnostic });
        }
        XamlDocumentBuildMetadata? buildMetadata = null;
        if (typeSystem is IXamlBuildMetadataResolver buildMetadataResolver)
        {
            buildMetadata = buildMetadataResolver.ResolveDocumentBuildMetadata(
                infoset.Path,
                infoset.Root == null
                    ? null
                    : GetDirectiveText(
                        infoset.Root,
                        XamlNamespaces.Language2006,
                        "Class"));
            if (buildMetadata.Issues.Count != 0)
            {
                var diagnostics = new List<Diagnostic>(infoset.Diagnostics);
                var span = infoset.Root?.SourceSpan ?? default;
                foreach (var issue in buildMetadata.Issues)
                {
                    diagnostics.Add(CreateError(
                        document,
                        GetBuildMetadataDiagnosticId(issue.Kind),
                        issue.Message,
                        span,
                        "5.2.1.1"));
                }
                return new XamlCompilationResult(
                    document,
                    Array.Empty<XamlGeneratedSource>(),
                    diagnostics,
                    buildMetadata);
            }
            if (!buildMetadata.ShouldCompile)
            {
                return new XamlCompilationResult(
                    document,
                    Array.Empty<XamlGeneratedSource>(),
                    infoset.Diagnostics,
                    buildMetadata,
                    wasSkipped: true);
            }
        }
        var binder = new XamlSemanticBinder();
        var bound = binder.Bind(
            infoset,
            typeSystem,
            new XamlSemanticBindingOptions { Strict = options.Strict });
        var resourceGraph = new XamlResourceGraphBuilder().Build(
            bound,
            options.ResourceDependencies,
            options.StaticResourceForwardReferenceMode ==
            XamlStaticResourceForwardReferenceMode.Reorder);
        bound = binder.EnrichResourceBindingSources(
            bound,
            resourceGraph,
            typeSystem,
            new XamlSemanticBindingOptions { Strict = options.Strict });
        resourceGraph = resourceGraph.WithDocument(bound);
        var program = new XamlConstructionLowerer().Lower(bound, resourceGraph);
        return EmitProgramCore(program, roslynFramework, options, buildMetadata);
    }

    public XamlCompilationResult EmitProgram(
        XamlConstructionProgram program,
        IRoslynXamlFrameworkProfile framework,
        XamlCompilerOptions options)
        => EmitProgramCore(program, framework, options, buildMetadata: null);

    private XamlCompilationResult EmitProgramCore(
        XamlConstructionProgram program,
        IRoslynXamlFrameworkProfile framework,
        XamlCompilerOptions options,
        XamlDocumentBuildMetadata? buildMetadata)
    {
        if (program == null) throw new ArgumentNullException(nameof(program));
        if (framework == null) throw new ArgumentNullException(nameof(framework));
        if (options == null) throw new ArgumentNullException(nameof(options));

        var document = program.BoundDocument.Infoset.Syntax;
        var diagnostics = new List<Diagnostic>(program.Diagnostics);
        var sources = new List<XamlGeneratedSource>();
        if (program.Root == null || program.Root.Type.Symbol == null ||
            (options.Strict && ContainsErrors(diagnostics)))
        {
            return new XamlCompilationResult(document, sources, diagnostics, buildMetadata);
        }

        var className = GetDirectiveText(program.Root, XamlNamespaces.Language2006, "Class");
        if (string.IsNullOrWhiteSpace(className))
        {
            return EmitCompiledResource(
                program,
                framework,
                options,
                diagnostics,
                buildMetadata);
        }
        className = !string.IsNullOrWhiteSpace(buildMetadata?.EffectiveClassName)
            ? buildMetadata!.EffectiveClassName!
            : className!.Trim();
        var lastDot = className.LastIndexOf('.');
        var namespaceName = lastDot < 0 ? string.Empty : className.Substring(0, lastDot);
        var typeName = lastDot < 0 ? className : className.Substring(lastDot + 1);
        if (!IsValidQualifiedTypeName(namespaceName, typeName))
        {
            diagnostics.Add(CreateError(document, "PGXAML3001",
                $"x:Class '{className}' is not a valid C# type name.", program.Root.SourceSpan, "6.3.1.6"));
            return new XamlCompilationResult(document, sources, diagnostics, buildMetadata);
        }

        var context = new EmitContext(program, framework, options, diagnostics);
        context.EmitExistingRoot(program.Root, SyntaxFactory.ThisExpression());
        var unformattedUnit = BuildCompilationUnit(namespaceName, typeName, context)
            .WithLeadingTrivia(
                SyntaxFactory.Comment("// <auto-generated/>"),
                SyntaxFactory.EndOfLine(Environment.NewLine),
                SyntaxFactory.Trivia(SyntaxFactory.NullableDirectiveTrivia(
                    SyntaxFactory.Token(SyntaxKind.EnableKeyword),
                    isActive: true)),
                SyntaxFactory.EndOfLine(Environment.NewLine));
        var unit = unformattedUnit.NormalizeWhitespace();
        var unformattedTree = CSharpSyntaxTree.Create(unformattedUnit, path: document.Path + ".g.cs");
        var generatedTree = CSharpSyntaxTree.Create(unit, path: document.Path + ".g.cs");
        sources.Add(new XamlGeneratedSource(
            GetHintName(className, options.ResourceUri ?? document.Path),
            unit.ToFullString(),
            generatedTree,
            unformattedTree));
        return new XamlCompilationResult(document, sources, diagnostics, buildMetadata);
    }

    private static XamlCompilationResult EmitCompiledResource(
        XamlConstructionProgram program,
        IRoslynXamlFrameworkProfile framework,
        XamlCompilerOptions options,
        List<Diagnostic> diagnostics,
        XamlDocumentBuildMetadata? buildMetadata)
    {
        var document = program.BoundDocument.Infoset.Syntax;
        var sources = new List<XamlGeneratedSource>();
        var root = program.Root!;
        if (root.Type.Symbol?.IsDictionary != true)
        {
            diagnostics.Add(CreateError(document, "PGXAML3005",
                "A classless compiled XAML root must currently be a dictionary. Add x:Class or use a framework resource dictionary root.",
                root.SourceSpan, "6.1.1.1"));
            return new XamlCompilationResult(document, sources, diagnostics, buildMetadata);
        }
        if (!root.Type.Symbol.IsDefaultConstructible)
        {
            diagnostics.Add(CreateError(document, "PGXAML3006",
                $"Classless dictionary '{root.Type.Symbol.MetadataName}' must be default constructible.",
                root.SourceSpan, "6.2.2.3"));
            return new XamlCompilationResult(document, sources, diagnostics, buildMetadata);
        }

        var artifact = CreateCompiledResourceArtifact(options.ResourceUri ?? document.Path);
        var context = new EmitContext(program, framework, options, diagnostics, isClassBacked: false);
        context.EmitExistingRoot(root, SyntaxFactory.IdentifierName("target"));
        if ((framework.Capabilities & XamlFrameworkCapabilities.Resources) == 0 ||
            framework is not IRoslynXamlCompiledResourceProfile resourceFramework ||
            !resourceFramework.TryCreateCompiledResourceRegistration(artifact, out var registration))
        {
            diagnostics.Add(CreateError(document, "PGXAML5003",
                $"Profile '{framework.Id}' does not provide compiled-resource registration.",
                root.SourceSpan, "EXT-003"));
            return new XamlCompilationResult(document, sources, diagnostics, buildMetadata);
        }

        var unformattedUnit = BuildResourceCompilationUnit(root.Type.Symbol, artifact, context, registration)
            .WithLeadingTrivia(
                SyntaxFactory.Comment("// <auto-generated/>"),
                SyntaxFactory.EndOfLine(Environment.NewLine),
                SyntaxFactory.Trivia(SyntaxFactory.NullableDirectiveTrivia(
                    SyntaxFactory.Token(SyntaxKind.EnableKeyword),
                    isActive: true)),
                SyntaxFactory.EndOfLine(Environment.NewLine));
        var unit = unformattedUnit.NormalizeWhitespace();
        var unformattedTree = CSharpSyntaxTree.Create(unformattedUnit, path: document.Path + ".g.cs");
        var generatedTree = CSharpSyntaxTree.Create(unit, path: document.Path + ".g.cs");
        sources.Add(new XamlGeneratedSource(
            artifact.FactoryTypeName + ".ProGPU.Xaml.g.cs",
            unit.ToFullString(),
            generatedTree,
            unformattedTree,
            artifact));
        return new XamlCompilationResult(document, sources, diagnostics, buildMetadata);
    }

    public static XamlCompiledResourceArtifact CreateCompiledResourceArtifact(string documentPath)
    {
        if (documentPath == null) throw new ArgumentNullException(nameof(documentPath));
        var canonicalPath = CanonicalizeResourceUri(documentPath);
        return new XamlCompiledResourceArtifact(
            canonicalPath,
            "ProGPU.Xaml.Generated",
            "__CompiledResource_" + ComputeStableHash(canonicalPath).ToString("x16", CultureInfo.InvariantCulture),
            "Build",
            "Populate");
    }

    private static string CanonicalizeResourceUri(string resourceUri)
        => XamlResourceUriIdentity.Parse(resourceUri).Canonical;

    private static CompilationUnitSyntax BuildResourceCompilationUnit(
        XamlTypeInfo rootType,
        XamlCompiledResourceArtifact artifact,
        EmitContext context,
        StatementSyntax registration)
    {
        var typeSyntax = RoslynTypeSyntaxFactory.Create(rootType.Symbol);
        var target = SyntaxFactory.IdentifierName("target");
        var build = SyntaxFactory.MethodDeclaration(typeSyntax, artifact.BuildMethodName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.InternalKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword))
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                        .AddVariables(SyntaxFactory.VariableDeclarator(target.Identifier)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.ObjectCreationExpression(typeSyntax)
                                    .WithArgumentList(SyntaxFactory.ArgumentList()))))),
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(artifact.PopulateMethodName))
                        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(target))))),
                SyntaxFactory.ReturnStatement(target)));
        var populate = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                artifact.PopulateMethodName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.InternalKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(target.Identifier).WithType(typeSyntax))))
            .WithBody(SyntaxFactory.Block(context.Statements));
        var register = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                "Register")
            .AddAttributeLists(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(RoslynTypeSyntaxFactory.CreateGlobalName(
                    "System", "Runtime", "CompilerServices", "ModuleInitializer")))))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.InternalKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword))
            .WithBody(SyntaxFactory.Block(
                context.BindingAccessorRegistrations.Concat(
                    new[] { registration })));
        var declaration = SyntaxFactory.ClassDeclaration(artifact.FactoryTypeName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.InternalKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword))
            .AddMembers(build, populate, register);
        return SyntaxFactory.CompilationUnit().AddMembers(
            SyntaxFactory.NamespaceDeclaration(CreateNamespaceName(artifact.FactoryNamespace)).AddMembers(declaration));
    }

    private static CompilationUnitSyntax BuildCompilationUnit(
        string namespaceName,
        string typeName,
        EmitContext context)
    {
        var members = new List<MemberDeclarationSyntax>(
            context.Fields.Count + context.GeneratedMembers.Count + 2);
        members.AddRange(context.Fields);
        members.AddRange(context.GeneratedMembers);
        if (context.BindingAccessorRegistrations.Count != 0)
        {
            members.Add(
                SyntaxFactory.MethodDeclaration(
                        SyntaxFactory.PredefinedType(
                            SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                        context.BindingAccessorRegistrationMethodName)
                    .AddAttributeLists(
                        SyntaxFactory.AttributeList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Attribute(
                                    RoslynTypeSyntaxFactory.CreateGlobalName(
                                        "System", "Runtime", "CompilerServices",
                                        "ModuleInitializer")))))
                    .AddModifiers(
                        SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                        SyntaxFactory.Token(SyntaxKind.StaticKeyword))
                    .WithBody(
                        SyntaxFactory.Block(
                            context.BindingAccessorRegistrations)));
        }
        members.Add(SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                SyntaxFactory.Identifier("InitializeComponent"))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithBody(SyntaxFactory.Block(context.Statements)));

        if (context.Options.EmitHotReloadHooks && context.CanReloadSafely)
        {
            members.Add(SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                    SyntaxFactory.Identifier("Reload"))
                .WithExplicitInterfaceSpecifier(SyntaxFactory.ExplicitInterfaceSpecifier(
                    RoslynTypeSyntaxFactory.CreateGlobalName(
                        "Microsoft", "UI", "Xaml", "HotReload", "IHotReloadable")))
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("context"))
                        .WithType(RoslynTypeSyntaxFactory.CreateGlobalName(
                            "Microsoft", "UI", "Xaml", "HotReload", "HotReloadContext")))))
                .WithBody(SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("InitializeComponent"))))));
        }

        var declaration = SyntaxFactory.ClassDeclaration(EscapeIdentifier(typeName))
            .WithModifiers(context.ClassModifiers.Add(SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
            .AddMembers(members.ToArray());
        if (context.Options.EmitHotReloadHooks && context.CanReloadSafely)
        {
            declaration = declaration.WithBaseList(SyntaxFactory.BaseList(
                SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(SyntaxFactory.SimpleBaseType(
                    RoslynTypeSyntaxFactory.CreateGlobalName(
                        "Microsoft", "UI", "Xaml", "HotReload", "IHotReloadable")))));
        }

        MemberDeclarationSyntax topLevel = declaration;
        if (namespaceName.Length != 0)
            topLevel = SyntaxFactory.NamespaceDeclaration(CreateNamespaceName(namespaceName)).AddMembers(declaration);
        return SyntaxFactory.CompilationUnit().AddMembers(topLevel);
    }

    private sealed class EmitContext
    {
        private readonly XamlConstructionProgram _program;
        private readonly IRoslynXamlFrameworkProfile _framework;
        private readonly List<Diagnostic> _diagnostics;
        private readonly string _checksum;
        private readonly bool _isClassBacked;
        private readonly List<StatementSyntax> _pendingFieldAssignments = new List<StatementSyntax>();
        private readonly List<Action> _pendingNameReferenceActions = new List<Action>();
        private readonly Dictionary<string, ExpressionSyntax> _nameExpressions = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, StatementSyntax> _bindingAccessorRegistrations =
            new SortedDictionary<string, StatementSyntax>(StringComparer.Ordinal);
        private readonly ExpressionSyntax? _contextExpression;
        private readonly XamlTypeInfo? _contextType;
        private ExpressionSyntax? _deferredLifetimeOwnerExpression;
        private readonly ExpressionSyntax? _compiledBindingOwnerExpression;
        private ExpressionSyntax _lookupRootExpression = null!;
        private bool _hasDeferredLifetimeRegistrations;
        private bool _hasCompiledBindings;
        private int _nextObjectId;

        public EmitContext(
            XamlConstructionProgram program,
            IRoslynXamlFrameworkProfile framework,
            XamlCompilerOptions options,
            List<Diagnostic> diagnostics,
            bool isClassBacked = true,
            ExpressionSyntax? contextExpression = null,
            XamlTypeInfo? contextType = null,
            ExpressionSyntax? deferredLifetimeOwnerExpression = null,
            ExpressionSyntax? compiledBindingOwnerExpression = null)
        {
            _program = program;
            _framework = framework;
            Options = options;
            _diagnostics = diagnostics;
            _checksum = ToHex(program.BoundDocument.Infoset.SourceText.GetChecksum());
            _isClassBacked = isClassBacked;
            _contextExpression = contextExpression;
            _contextType = contextType;
            _deferredLifetimeOwnerExpression = deferredLifetimeOwnerExpression;
            _compiledBindingOwnerExpression = compiledBindingOwnerExpression;
        }

        public XamlCompilerOptions Options { get; }
        public List<StatementSyntax> Statements { get; } = new List<StatementSyntax>();
        public List<FieldDeclarationSyntax> Fields { get; } = new List<FieldDeclarationSyntax>();
        public List<MemberDeclarationSyntax> GeneratedMembers { get; } =
            new List<MemberDeclarationSyntax>();
        public IReadOnlyCollection<StatementSyntax> BindingAccessorRegistrations =>
            _bindingAccessorRegistrations.Values;
        public string BindingAccessorRegistrationMethodName =>
            "__RegisterXamlBindingAccessors_" + _checksum;
        public bool CanReloadSafely { get; private set; }
        public SyntaxTokenList ClassModifiers { get; private set; }

        public void EmitExistingRoot(XamlIrObject root, ExpressionSyntax expression)
        {
            _lookupRootExpression = expression;
            RoslynXamlDeferredMarkupExtensionLifecycleSyntax? rootMarkupLifecycle = null;
            var rootMarkupLifecycleProfile =
                _framework as IRoslynXamlDeferredMarkupExtensionLifecycleProfile;
            if (_isClassBacked &&
                _deferredLifetimeOwnerExpression == null &&
                rootMarkupLifecycleProfile != null &&
                rootMarkupLifecycleProfile.TryCreateDeferredMarkupExtensionLifecycle(
                    expression,
                    "__xamlBindingLifetime",
                    out var createdRootMarkupLifecycle))
            {
                rootMarkupLifecycle = createdRootMarkupLifecycle;
                _deferredLifetimeOwnerExpression =
                    rootMarkupLifecycle.RegistrationOwner;
            }
            ClassModifiers = ParseAccessibility(
                GetDirectiveText(root, XamlNamespaces.Language2006, "ClassModifier"),
                root.SourceSpan,
                "x:ClassModifier");
            RegisterObjectName(root, expression);
            EmitOperations(root, expression, isRoot: true);
            foreach (var action in _pendingNameReferenceActions) action();
            Statements.AddRange(_pendingFieldAssignments);
            if (_hasDeferredLifetimeRegistrations)
            {
                if (rootMarkupLifecycle == null ||
                    rootMarkupLifecycleProfile == null ||
                    !rootMarkupLifecycleProfile
                        .TryCreateDeferredMarkupExtensionFinalization(
                            rootMarkupLifecycle.RegistrationOwner,
                            expression,
                            out var finalization))
                {
                    AddError(
                        "PGXAML3047",
                        $"Profile '{_framework.Id}' cannot finalize class-backed " +
                        "markup-extension lifetime ownership.",
                        root.SourceSpan,
                        "EXT-004");
                }
                else
                {
                    Statements.InsertRange(
                        0,
                        rootMarkupLifecycle.PrepareStatements);
                    Statements.AddRange(finalization);
                }
            }
            if (_hasCompiledBindings &&
                _framework is IRoslynXamlCompiledBindingLifecycleProfile lifecycle)
            {
                if (_isClassBacked &&
                    CanEmitCompiledBindingsMember(root) &&
                    lifecycle.TryCreateCompiledBindingLifecycle(
                        expression,
                        out var lifecycleSyntax))
                {
                    GeneratedMembers.AddRange(lifecycleSyntax.Members);
                    Statements.InsertRange(
                        0,
                        lifecycleSyntax.PrepareStatements);
                    Statements.AddRange(
                        lifecycleSyntax.InitializeStatements);
                }
                else if (lifecycle.TryCreateCompiledBindingReset(
                             expression,
                             out var reset))
                {
                    Statements.Insert(0, reset);
                }
            }
            foreach (var operation in root.Operations)
            {
                if (operation.Kind == XamlIrOperationKind.SetMember && operation.Member.Symbol != null &&
                    string.Equals(operation.Member.Symbol.Name, root.Type.Symbol!.ContentMemberName, StringComparison.Ordinal) &&
                    operation.Member.Symbol.CanWrite)
                {
                    CanReloadSafely = true;
                    break;
                }
            }
        }

        private bool CanEmitCompiledBindingsMember(XamlIrObject root)
        {
            if (_program.BoundDocument.RootClassType?.Symbol is not
                INamedTypeSymbol classType ||
                classType.GetMembers("Bindings").Length == 0)
                return true;
            AddError(
                "PGXAML3045",
                $"Type '{classType.ToDisplayString()}' already declares a member named " +
                "'Bindings', which is reserved for the generated compiled-binding lifecycle.",
                root.SourceSpan,
                "EXT-004");
            return false;
        }

        private ExpressionSyntax? EmitValue(
            XamlIrValue value,
            XamlTypeInfo targetType,
            XamlIrObject? valueObject = null,
            ISymbol? member = null,
            ExpressionSyntax? targetObject = null,
            XamlMemberInfo? targetMember = null)
        {
            if (value is XamlIrText text)
            {
                if ((text.TextSyntax.Kind ==
                         XamlTextSyntaxKind.TypeConverter ||
                     text.TextSyntax.Kind ==
                         XamlTextSyntaxKind.CreateFromString) &&
                    text.TextSyntax.CreateFromStringShape is
                        { IsValid: true } createFromString)
                {
                    var converted = CreateFromStringExpression(
                        createFromString,
                        targetType,
                        text.Text);
                    return AnnotateExpression(converted, text.SourceSpan, text.StableId, member, XamlProjectionKind.Literal);
                }
                if (text.TextSyntax.Kind == XamlTextSyntaxKind.Error)
                {
                    AddError("PGXAML2041", text.TextSyntax.Error ?? "The selected XAML text syntax is invalid.",
                        text.SourceSpan, "6.3.2.4");
                    return null;
                }
                if (_framework.TryCreateLiteralExpression(targetType, text.Text, out var expression))
                    return AnnotateExpression(expression, text.SourceSpan, text.StableId, member, XamlProjectionKind.Literal);
                AddError("PGXAML2102",
                    $"Text '{text.Text}' cannot be converted to '{targetType.MetadataName}' by profile '{_framework.Id}'.",
                    text.SourceSpan, "6.3.2.4");
                return null;
            }

            if (value is XamlIrNameReference directReference)
                return ResolveNameReference(directReference);
            if (value is XamlIrType typeValue && typeValue.Type.Symbol != null)
                return AnnotateExpression(
                    SyntaxFactory.TypeOfExpression(RoslynTypeSyntaxFactory.Create(typeValue.Type.Symbol.Symbol)),
                    typeValue.SourceSpan,
                    typeValue.StableId,
                    member,
                    XamlProjectionKind.Literal);
            if (value is XamlIrStaticMember staticValue && staticValue.Value.Member?.ContainingType != null)
                return AnnotateExpression(
                    CreateStaticMemberExpression(staticValue.Value.Member),
                    staticValue.SourceSpan,
                    staticValue.StableId,
                    member,
                    XamlProjectionKind.Literal);
            if (value is XamlIrResourceReference resourceReference)
            {
                var resourceKey = CreateResourceKeyExpression(resourceReference.Reference.ResourceKey, targetType: null);
                if (resourceKey == null) return null;
                if (_framework.TryCreateResourceReferenceExpression(
                        resourceReference,
                        targetType,
                        _lookupRootExpression,
                        resourceKey,
                        out var resourceExpression)) return resourceExpression;
                AddError("PGXAML4006",
                    $"Profile '{_framework.Id}' cannot lower {resourceReference.Reference.Kind} resource '{resourceReference.Reference.Key}'.",
                    resourceReference.SourceSpan,
                    "6.3.2.4");
                return null;
            }

            var objectValue = valueObject ?? (XamlIrObject)value;
            if (_framework is IRoslynXamlObjectExpressionProfile objectExpressions &&
                objectExpressions.TryCreateObjectExpression(
                    objectValue,
                    _lookupRootExpression,
                    out var objectExpression))
                return objectExpression;
            if (TryGetNameReference(objectValue, out var nameReference))
                return ResolveNameReference(nameReference);
            if (objectValue.Kind == XamlIrObjectKind.InvokeMarkupExtension)
            {
                if (objectValue.Type.Symbol?.MarkupExtensionOptionSelector is { IsValid: true } selector &&
                    _framework is IRoslynXamlMarkupExtensionOptionProfile optionProfile &&
                    optionProfile.TryCreateMarkupExtensionOptionExpression(
                        objectValue,
                        selector,
                        targetType,
                        (branchValue, branchType) => EmitValue(
                            branchValue,
                            branchType,
                            member: targetMember?.Symbol,
                            targetObject: targetObject,
                            targetMember: targetMember),
                        targetObject,
                        targetMember,
                        _lookupRootExpression,
                        Options.ResourceUri,
                        out var optionExpression))
                    return optionExpression;
                if (_framework is IRoslynXamlContextualMarkupExpressionProfile contextualMarkup &&
                    contextualMarkup.TryCreateMarkupExtensionExpression(
                        objectValue,
                        targetType,
                        _lookupRootExpression,
                        out var contextualExpression))
                    return contextualExpression;
                if (_framework.TryCreateMarkupExtensionExpression(objectValue, targetType, out var expression)) return expression;
                if (_framework is IRoslynXamlMarkupExtensionInvocationProfile invocationProfile &&
                    objectValue.Type.Symbol?.IsMarkupExtension == true)
                {
                    var instance = EmitNewObject(objectValue);
                    if (instance != null && invocationProfile.TryCreateMarkupExtensionInvocation(
                            objectValue,
                            instance,
                            targetType,
                            targetObject,
                            targetMember,
                            _lookupRootExpression,
                            Options.ResourceUri,
                            out var invocation))
                        return invocation;
                }
                AddError("PGXAML2101",
                    $"Markup extension '{{{objectValue.Type.RequestedName.DisplayName}}}' is not implemented by " +
                    $"profile '{_framework.Id}' for '{targetType.MetadataName}'.",
                    objectValue.SourceSpan, "8.6.7.2");
                return null;
            }
            if (objectValue.Kind == XamlIrObjectKind.CreateArray)
                return EmitArray(objectValue);
            if (objectValue.Kind == XamlIrObjectKind.InitializeFromText)
            {
                var initialization = objectValue.Operations.FirstOrDefault(operation =>
                    operation.Kind == XamlIrOperationKind.ApplyDirective &&
                    string.Equals(operation.Member.RequestedName.LocalName, "Initialization", StringComparison.Ordinal));
                var initializationText = initialization?.Values.OfType<XamlIrText>().SingleOrDefault();
                if (initializationText != null && objectValue.Type.Symbol != null &&
                    _framework.TryCreateLiteralExpression(objectValue.Type.Symbol, initializationText.Text, out var expression))
                    return AnnotateExpression(expression, initializationText.SourceSpan, initializationText.StableId, null, XamlProjectionKind.Literal);
                AddError("PGXAML2104", "Intrinsic initialization text is missing or invalid.", objectValue.SourceSpan, "6.2.2.5");
                return null;
            }
            return EmitNewObject(objectValue);
        }

        private ExpressionSyntax? EmitArray(XamlIrObject value)
        {
            XamlTypeInfo? elementType = null;
            XamlIrOperation? items = null;
            foreach (var operation in value.Operations)
            {
                if (operation.Kind == XamlIrOperationKind.SetIntrinsicMember &&
                    string.Equals(operation.Member.RequestedName.LocalName, "Type", StringComparison.Ordinal))
                    elementType = FindTypeValue(operation.Values)?.Symbol;
                else if (operation.Kind == XamlIrOperationKind.AddCollectionItem)
                    items = operation;
            }
            if (elementType == null)
            {
                AddError("PGXAML2103", "x:Array requires one resolvable Type member.", value.SourceSpan, "6.2.2.10");
                return null;
            }

            var expressions = new List<ExpressionSyntax>();
            if (items != null)
            {
                foreach (var item in items.Values)
                {
                    var expression = EmitValue(item, elementType);
                    if (expression != null) expressions.Add(expression);
                }
            }
            var arrayType = SyntaxFactory.ArrayType(RoslynTypeSyntaxFactory.Create(elementType.Symbol))
                .WithRankSpecifiers(SyntaxFactory.SingletonList(
                    SyntaxFactory.ArrayRankSpecifier(SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                        SyntaxFactory.OmittedArraySizeExpression()))));
            return SyntaxFactory.ArrayCreationExpression(arrayType)
                .WithInitializer(SyntaxFactory.InitializerExpression(
                    SyntaxKind.ArrayInitializerExpression,
                    SyntaxFactory.SeparatedList(expressions)));
        }

        private static XamlBoundTypeReference? FindTypeValue(IEnumerable<XamlIrValue> values)
        {
            foreach (var value in values)
            {
                if (value is XamlIrType type) return type.Type;
                if (value is XamlIrObject nested)
                {
                    foreach (var operation in nested.Operations)
                    {
                        var found = FindTypeValue(operation.Values);
                        if (found != null) return found;
                    }
                }
            }
            return null;
        }

        private static ExpressionSyntax CreateFromStringExpression(
            XamlCreateFromStringShapeInfo shape,
            XamlTypeInfo targetType,
            string text)
        {
            ExpressionSyntax receiver;
            if (shape.InvocationKind ==
                XamlCreateFromStringInvocationKind.StaticMethod)
            {
                receiver = RoslynTypeSyntaxFactory.Create(
                    shape.FactoryType!);
            }
            else
            {
                receiver = SyntaxFactory.ObjectCreationExpression(
                        RoslynTypeSyntaxFactory.Create(
                            shape.Constructor!.ContainingType))
                    .WithArgumentList(SyntaxFactory.ArgumentList());
            }
            var invocation = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        receiver,
                        SyntaxFactory.IdentifierName(
                            shape.Method!.Name)))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(WinUiXamlProfile.StringLiteral(text)))));
            var nonNull = SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.SuppressNullableWarningExpression,
                invocation);
            return SyntaxFactory.CastExpression(RoslynTypeSyntaxFactory.Create(targetType.Symbol), nonNull);
        }

        private ExpressionSyntax? EmitNewObject(XamlIrObject value)
        {
            var variable = EmitObjectDeclaration(value);
            if (variable == null) return null;
            EmitObjectOperations(value, variable);
            return variable;
        }

        private void EmitObjectOperations(
            XamlIrObject value,
            ExpressionSyntax variable)
        {
            var previousLookupRoot = _lookupRootExpression;
            if (value.Type.Symbol?.IsDictionary == true)
            {
                _lookupRootExpression = variable;
            }

            try
            {
                EmitOperations(value, variable, isRoot: false);
            }
            finally
            {
                _lookupRootExpression = previousLookupRoot;
            }
        }

        private ExpressionSyntax? EmitObjectDeclaration(XamlIrObject value)
        {
            if (value.Type.Symbol == null) return null;
            var variableName = "__xamlObject" + (++_nextObjectId).ToString(CultureInfo.InvariantCulture);
            var variable = SyntaxFactory.IdentifierName(variableName);
            var construction = CreateConstructionExpression(value);
            if (construction == null) return null;
            AddStatement(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                    .AddVariables(SyntaxFactory.VariableDeclarator(variableName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(construction)))),
                value.SourceSpan,
                value.StableId);
            RegisterObjectName(value, variable);
            return variable;
        }

        private bool TryEmitTopDownObject(
            XamlIrValue value,
            Action<ExpressionSyntax> publish)
        {
            if (value is XamlIrObject frameworkObject &&
                _framework is IRoslynXamlObjectExpressionProfile objectExpressions &&
                objectExpressions.TryCreateObjectExpression(
                    frameworkObject,
                    _lookupRootExpression,
                    out var frameworkExpression))
            {
                publish(frameworkExpression);
                return true;
            }
            if (value is not XamlIrObject
                {
                    Kind: XamlIrObjectKind.Create,
                    InitializationMode: XamlIrInitializationMode.TopDown
                } objectValue)
                return false;

            var variable = EmitObjectDeclaration(objectValue);
            if (variable != null)
            {
                publish(variable);
                EmitObjectOperations(objectValue, variable);
            }
            return true;
        }

        private ExpressionSyntax? CreateConstructionExpression(XamlIrObject value)
        {
            var factory = value.Operations
                .Where(operation => operation.Kind == XamlIrOperationKind.ApplyDirective &&
                    string.Equals(operation.Member.RequestedName.LocalName, "FactoryMethod", StringComparison.Ordinal))
                .SelectMany(operation => operation.Values)
                .OfType<XamlIrFactoryMethod>()
                .SingleOrDefault();
            if (factory?.Value.Method != null)
            {
                var arguments = CreateCallArguments(value, factory.Value.Method.Parameters);
                if (arguments == null) return null;
                var target = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(factory.Value.Method.ContainingType),
                    SyntaxFactory.IdentifierName(EscapeIdentifier(factory.Value.Method.Name)));
                return SyntaxFactory.InvocationExpression(target)
                    .WithArgumentList(SyntaxFactory.ArgumentList(arguments.Value));
            }
            var constructorArguments = CreateConstructorArguments(value);
            if (constructorArguments == null) return null;
            return SyntaxFactory.ObjectCreationExpression(RoslynTypeSyntaxFactory.Create(value.Type.Symbol!.Symbol))
                .WithArgumentList(SyntaxFactory.ArgumentList(constructorArguments.Value));
        }

        private SeparatedSyntaxList<ArgumentSyntax>? CreateConstructorArguments(XamlIrObject value)
        {
            var argumentsOperation = value.Operations.FirstOrDefault(operation =>
                operation.Kind == XamlIrOperationKind.ApplyDirective &&
                (string.Equals(operation.Member.RequestedName.LocalName, "Arguments", StringComparison.Ordinal) ||
                 string.Equals(operation.Member.RequestedName.LocalName, "PositionalParameters", StringComparison.Ordinal)));
            if (argumentsOperation == null) return default(SeparatedSyntaxList<ArgumentSyntax>);
            var constructors = value.Type.Symbol!.Constructors
                .Where(constructor => constructor.Arity == argumentsOperation.Values.Length && constructor.Symbol != null)
                .ToArray();
            if (constructors.Length != 1)
            {
                AddError("PGXAML2025",
                    $"Type '{value.Type.Symbol.MetadataName}' does not have one unambiguous constructor with {argumentsOperation.Values.Length} arguments.",
                    argumentsOperation.SourceSpan,
                    "6.2.2.4");
                return null;
            }
            return CreateCallArguments(value, constructors[0].Symbol!.Parameters);
        }

        private SeparatedSyntaxList<ArgumentSyntax>? CreateCallArguments(
            XamlIrObject value,
            IReadOnlyList<IParameterSymbol> parameters)
        {
            var argumentsOperation = value.Operations.FirstOrDefault(operation =>
                operation.Kind == XamlIrOperationKind.ApplyDirective &&
                (string.Equals(operation.Member.RequestedName.LocalName, "Arguments", StringComparison.Ordinal) ||
                 string.Equals(operation.Member.RequestedName.LocalName, "PositionalParameters", StringComparison.Ordinal)));
            if (argumentsOperation == null)
                return parameters.Count == 0 ? default(SeparatedSyntaxList<ArgumentSyntax>) : null;
            var result = new List<ArgumentSyntax>(argumentsOperation.Values.Length);
            for (var index = 0; index < argumentsOperation.Values.Length; index++)
            {
                var target = DescribeTemporary(parameters[index].Type, value.Type.Symbol!.NamespaceUri);
                var expression = EmitValue(argumentsOperation.Values[index], target);
                if (expression == null) return null;
                result.Add(SyntaxFactory.Argument(expression));
            }
            return SyntaxFactory.SeparatedList(result);
        }

        private void EmitOperations(XamlIrObject owner, ExpressionSyntax ownerExpression, bool isRoot)
        {
            foreach (var operation in owner.Operations)
            {
                if (operation.Kind == XamlIrOperationKind.ApplyDirective)
                {
                    EmitDirective(owner, ownerExpression, operation, isRoot);
                    continue;
                }
                if (operation.Kind == XamlIrOperationKind.Error) continue;
                if (operation.Kind == XamlIrOperationKind.SetDeferredContent)
                {
                    EmitDeferredContent(owner, ownerExpression, operation);
                    continue;
                }
                if (operation.Member.Symbol == null &&
                    operation.Kind != XamlIrOperationKind.AddCollectionItem &&
                    operation.Kind != XamlIrOperationKind.AddDictionaryItem) continue;

                switch (operation.Kind)
                {
                    case XamlIrOperationKind.SubscribeEvent:
                        EmitEvent(ownerExpression, operation);
                        break;
                    case XamlIrOperationKind.SetMember:
                    case XamlIrOperationKind.SetAttachableMember:
                        var setMember = operation.Member.Symbol;
                        if (setMember == null) break;
                        foreach (var value in operation.Values)
                        {
                            if (value is XamlIrObject { Kind: XamlIrObjectKind.InvokeMarkupExtension } assignedBinding &&
                                setMember.BindingAssignment is { IsValid: true } bindingAssignment)
                            {
                                var bindingInstance = EmitNewObject(assignedBinding);
                                if (bindingInstance != null &&
                                    _framework is IRoslynXamlBindingAssignmentProfile bindingAssignments &&
                                    bindingAssignments.TryCreateBindingObjectAssignment(
                                        assignedBinding,
                                        bindingAssignment,
                                        setMember,
                                        bindingInstance,
                                        ownerExpression,
                                        out var bindingStatement))
                                {
                                    AddStatement(
                                        bindingStatement,
                                        operation.SourceSpan,
                                        operation.StableId,
                                        setMember.Symbol,
                                        XamlProjectionKind.MemberAssignment);
                                    continue;
                                }
                                AddError(
                                    "PGXAML3042",
                                    $"Profile '{_framework.Id}' cannot assign binding object to attributed member " +
                                    $"'{setMember.Name}'.",
                                    operation.SourceSpan,
                                    "5.2.1.1");
                                continue;
                            }
                            if (value is XamlIrObject { Kind: XamlIrObjectKind.InvokeMarkupExtension } interceptedExtension &&
                                owner.Type.Symbol?.MarkupExtensionSetHandler is { IsValid: true } markupHandler)
                            {
                                var instance = EmitNewObject(interceptedExtension);
                                if (instance != null &&
                                    _framework is IRoslynXamlSetValueHandlerProfile setValueHandlers &&
                                    setValueHandlers.TryCreateMarkupExtensionSetHandlerAssignment(
                                        markupHandler,
                                        interceptedExtension,
                                        instance,
                                        setMember,
                                        ownerExpression,
                                        _lookupRootExpression,
                                        Options.ResourceUri,
                                        out var interceptedAssignment))
                                {
                                    AddStatement(
                                        interceptedAssignment,
                                        operation.SourceSpan,
                                        operation.StableId,
                                        markupHandler.Handler,
                                        XamlProjectionKind.MemberAssignment);
                                    continue;
                                }
                                AddError(
                                    "PGXAML3040",
                                    $"Profile '{_framework.Id}' cannot emit object-writer markup-extension handler " +
                                    $"'{markupHandler.Handler?.ToDisplayString()}'.",
                                    operation.SourceSpan,
                                    "5.2.1.1");
                                continue;
                            }
                            if (value is XamlIrObject { Kind: XamlIrObjectKind.InvokeMarkupExtension } receivedExtension &&
                                owner.Type.Symbol?.MarkupExtensionReceiver is { IsValid: true } receiverShape)
                            {
                                var instance = EmitNewObject(receivedExtension);
                                if (instance != null &&
                                    _framework is IRoslynXamlMarkupExtensionReceiverProfile receivers &&
                                    receivers.TryCreateMarkupExtensionReceiverAssignment(
                                        receiverShape,
                                        receivedExtension,
                                        instance,
                                        setMember,
                                        ownerExpression,
                                        _lookupRootExpression,
                                        Options.ResourceUri,
                                        out var receiverAssignment))
                                {
                                    AddStatement(
                                        receiverAssignment,
                                        operation.SourceSpan,
                                        operation.StableId,
                                        receiverShape.ReceiveMethod,
                                        XamlProjectionKind.MemberAssignment);
                                    continue;
                                }
                                AddError(
                                    "PGXAML3043",
                                    $"Profile '{_framework.Id}' cannot emit markup-extension receiver " +
                                    $"'{receiverShape.ReceiveMethod?.ToDisplayString()}'.",
                                    operation.SourceSpan,
                                    "5.2.1.1");
                                continue;
                            }
                            if (value is XamlIrText
                                {
                                    TextSyntax.Kind: XamlTextSyntaxKind.TypeConverter,
                                    TextSyntax.ConverterType: { } converterType
                                } interceptedText &&
                                owner.Type.Symbol?.TypeConverterSetHandler is { IsValid: true } converterHandler)
                            {
                                if (_framework is IRoslynXamlSetValueHandlerProfile setValueHandlers &&
                                    setValueHandlers.TryCreateTypeConverterSetHandlerAssignment(
                                        converterHandler,
                                        interceptedText,
                                        converterType,
                                        setMember,
                                        ownerExpression,
                                        _lookupRootExpression,
                                        Options.ResourceUri,
                                        out var interceptedAssignment))
                                {
                                    AddStatement(
                                        interceptedAssignment,
                                        operation.SourceSpan,
                                        operation.StableId,
                                        converterHandler.Handler,
                                        XamlProjectionKind.MemberAssignment);
                                    continue;
                                }
                                AddError(
                                    "PGXAML3041",
                                    $"Profile '{_framework.Id}' cannot emit object-writer type-converter handler " +
                                    $"'{converterHandler.Handler?.ToDisplayString()}'.",
                                    operation.SourceSpan,
                                    "5.2.1.1");
                                continue;
                            }
                            if (value is XamlIrBinding binding)
                            {
                                StatementSyntax ordinaryBindingStatement =
                                    SyntaxFactory.EmptyStatement();
                                var bindingUsesDeferredLifetime = false;
                                var handled =
                                    _framework is IRoslynXamlOrdinaryBindingAssignmentProfile
                                        canonicalBindingAssignments &&
                                    canonicalBindingAssignments.TryCreateBindingAssignment(
                                        binding,
                                        setMember,
                                        ownerExpression,
                                        _contextExpression,
                                        _contextType,
                                        _lookupRootExpression,
                                        _deferredLifetimeOwnerExpression,
                                        out ordinaryBindingStatement,
                                        out bindingUsesDeferredLifetime);
                                if (!handled &&
                                    _framework is IRoslynXamlMarkupExtensionAssignmentProfile
                                        ordinaryBindingAssignments)
                                {
                                    handled =
                                        ordinaryBindingAssignments
                                            .TryCreateMarkupExtensionAssignment(
                                                binding.Extension,
                                                setMember,
                                                ownerExpression,
                                                _contextExpression,
                                                _contextType,
                                                _lookupRootExpression,
                                                _deferredLifetimeOwnerExpression,
                                                out ordinaryBindingStatement,
                                                out bindingUsesDeferredLifetime);
                                }
                                if (handled)
                                {
                                    if (bindingUsesDeferredLifetime)
                                        _hasDeferredLifetimeRegistrations = true;
                                    RegisterBindingAccessors(binding);
                                    AddStatement(
                                        ordinaryBindingStatement,
                                        operation.SourceSpan,
                                        operation.StableId,
                                        setMember.Symbol,
                                        XamlProjectionKind.MemberAssignment);
                                }
                                else if (_framework is IRoslynXamlContextualMarkupExpressionProfile bindingValues &&
                                         bindingValues.TryCreateMarkupExtensionExpression(
                                             binding.Extension,
                                             setMember.ValueType,
                                             _lookupRootExpression,
                                             out var bindingValue))
                                {
                                    RegisterBindingAccessors(binding);
                                    EmitSet(ownerExpression, operation, bindingValue);
                                }
                                else
                                {
                                    AddError(
                                        "PGXAML3049",
                                        $"Profile '{_framework.Id}' cannot assign ordinary Binding to " +
                                        $"'{setMember.DeclaringType.MetadataName}.{setMember.Name}'.",
                                        operation.SourceSpan,
                                        "EXT-004");
                                }
                                continue;
                            }
                            if (value is XamlIrObject { Kind: XamlIrObjectKind.InvokeMarkupExtension } markupExtension &&
                                _framework is IRoslynXamlMarkupExtensionAssignmentProfile markupAssignments &&
                                markupAssignments.TryCreateMarkupExtensionAssignment(
                                    markupExtension,
                                    setMember,
                                    ownerExpression,
                                    _contextExpression,
                                    _contextType,
                                    _lookupRootExpression,
                                    _deferredLifetimeOwnerExpression,
                                    out var markupAssignment,
                                    out var usesDeferredLifetime))
                            {
                                if (usesDeferredLifetime)
                                    _hasDeferredLifetimeRegistrations = true;
                                AddStatement(
                                    markupAssignment,
                                    operation.SourceSpan,
                                    operation.StableId,
                                    setMember.Symbol,
                                    XamlProjectionKind.MemberAssignment);
                                continue;
                            }
                            if (value is XamlIrCompiledBinding compiledBinding &&
                                _framework is IRoslynXamlCompiledBindingAssignmentProfile compiledBindingAssignments &&
                                GetCompiledBindingSource(compiledBinding) is { } compiledBindingSource &&
                                compiledBindingAssignments.TryCreateCompiledBindingAssignment(
                                    compiledBinding,
                                    setMember,
                                    ownerExpression,
                                    compiledBindingSource,
                                    _lookupRootExpression,
                                    _compiledBindingOwnerExpression,
                                    out var compiledBindingAssignment))
                            {
                                _hasCompiledBindings = true;
                                AddStatement(
                                    compiledBindingAssignment,
                                    operation.SourceSpan,
                                    operation.StableId,
                                    setMember.Symbol,
                                    XamlProjectionKind.MemberAssignment);
                                continue;
                            }
                            if (value is XamlIrResourceReference resourceReference &&
                                _framework is IRoslynXamlResourceAssignmentProfile resourceAssignments &&
                                CreateResourceKeyExpression(resourceReference.Reference.ResourceKey, targetType: null) is { } resourceKey &&
                                resourceAssignments.TryCreateResourceReferenceAssignment(
                                    resourceReference,
                                    setMember,
                                    ownerExpression,
                                    _lookupRootExpression,
                                    resourceKey,
                                    out var resourceAssignment))
                            {
                                AddStatement(
                                    resourceAssignment,
                                    operation.SourceSpan,
                                    operation.StableId,
                                    setMember.Symbol,
                                    XamlProjectionKind.MemberAssignment);
                                continue;
                            }
                            if (TryGetNameReference(value, out var reference) && !_nameExpressions.ContainsKey(reference.Name))
                            {
                                _pendingNameReferenceActions.Add(() =>
                                {
                                    var deferred = ResolveNameReference(reference);
                                    if (deferred != null) EmitSet(ownerExpression, operation, deferred);
                                });
                                continue;
                            }
                            if (TryEmitTopDownObject(
                                    value,
                                    expression => EmitSet(
                                        ownerExpression,
                                        operation,
                                        expression)))
                                continue;
                            var expression = EmitValue(
                                value,
                                setMember.ValueType,
                                member: setMember.Symbol,
                                targetObject: ownerExpression,
                                targetMember: setMember);
                            if (expression != null) EmitSet(ownerExpression, operation, expression);
                        }
                        break;
                    case XamlIrOperationKind.AddCollectionItem:
                        EmitCollectionItems(owner, ownerExpression, operation);
                        break;
                    case XamlIrOperationKind.AddDictionaryItem:
                        EmitDictionaryItems(owner, ownerExpression, operation);
                        break;
                    case XamlIrOperationKind.RetrieveMember:
                        var retrievedMember = operation.Member.Symbol;
                        if (retrievedMember == null) break;
                        var retrieved = operation.Values.OfType<XamlIrObject>().SingleOrDefault();
                        if (retrieved == null)
                        {
                            AddError("PGXAML2007", $"Member '{retrievedMember.Name}' has no retrieved object value.",
                                operation.SourceSpan, "6.3.2.5");
                            break;
                        }
                        EmitOperations(
                            retrieved,
                            MemberAccess(ownerExpression, retrievedMember.CSharpName),
                            isRoot: false);
                        break;
                }
            }
        }

        private void EmitDeferredContent(XamlIrObject owner, ExpressionSyntax ownerExpression, XamlIrOperation operation)
        {
            var member = operation.Member.Symbol;
            var deferredRoot = operation.Values.OfType<XamlIrObject>().SingleOrDefault();
            if (member == null || deferredRoot == null)
            {
                AddError("PGXAML5001",
                    $"Deferred content member '{operation.Member.RequestedName.DisplayName}' does not contain one object root.",
                    operation.SourceSpan,
                    "5.2.1.2");
                return;
            }
            if (_framework is not IRoslynXamlDeferredContentProfile deferredProfile)
            {
                AddError("PGXAML5001",
                    $"Profile '{_framework.Id}' does not provide deferred-factory lowering for '{operation.Member.RequestedName.DisplayName}'.",
                    operation.SourceSpan,
                    "5.2.1.2");
                return;
            }

            var templateContext = SyntaxFactory.IdentifierName("__templateContext");
            const string templateLifetimeIdentifier = "__templateLifetime";
            RoslynXamlDeferredMarkupExtensionLifecycleSyntax? markupLifecycle = null;
            var markupLifecycleProfile =
                _framework as IRoslynXamlDeferredMarkupExtensionLifecycleProfile;
            if (markupLifecycleProfile != null &&
                markupLifecycleProfile.TryCreateDeferredMarkupExtensionLifecycle(
                    templateContext,
                    templateLifetimeIdentifier,
                    out var createdMarkupLifecycle))
            {
                markupLifecycle = createdMarkupLifecycle;
            }
            const string templateBindingsIdentifier = "__templateBindings";
            RoslynXamlDeferredCompiledBindingLifecycleSyntax? bindingLifecycle = null;
            var bindingLifecycleProfile =
                _framework as IRoslynXamlDeferredCompiledBindingLifecycleProfile;
            if (bindingLifecycleProfile != null &&
                bindingLifecycleProfile.TryCreateDeferredCompiledBindingLifecycle(
                    templateContext,
                    templateBindingsIdentifier,
                    out var createdBindingLifecycle))
            {
                bindingLifecycle = createdBindingLifecycle;
            }
            XamlTypeInfo? contextType = null;
            if (_framework is
                    IXamlIrDeferredContentContextTypePolicy contextPolicy &&
                contextPolicy.TryGetDeferredContentContextType(
                    owner,
                    operation,
                    out var resolvedContextType))
            {
                contextType = resolvedContextType;
            }
            var nested = new EmitContext(
                _program,
                _framework,
                Options,
                _diagnostics,
                isClassBacked: false,
                contextExpression: templateContext,
                contextType: contextType,
                deferredLifetimeOwnerExpression: markupLifecycle?.RegistrationOwner,
                compiledBindingOwnerExpression: bindingLifecycle?.RegistrationOwner);
            var rootExpression = nested.EmitDeferredRoot(deferredRoot);
            if (rootExpression == null) return;
            MergeBindingAccessorRegistrations(nested);
            var bodyStatements = new List<StatementSyntax>();
            if (nested._hasDeferredLifetimeRegistrations)
            {
                if (markupLifecycle == null ||
                    markupLifecycleProfile == null)
                {
                    AddError(
                        "PGXAML3047",
                        $"Profile '{_framework.Id}' does not provide per-instance lifecycle " +
                        "lowering for markup-extension registrations inside deferred content.",
                        operation.SourceSpan,
                        "EXT-004");
                    return;
                }
                bodyStatements.AddRange(markupLifecycle.PrepareStatements);
            }
            if (nested._hasCompiledBindings)
            {
                if (bindingLifecycle == null ||
                    bindingLifecycleProfile == null)
                {
                    AddError(
                        "PGXAML3046",
                        $"Profile '{_framework.Id}' does not provide per-instance lifecycle " +
                        "lowering for compiled bindings inside deferred content.",
                        operation.SourceSpan,
                        "EXT-004");
                    return;
                }
                bodyStatements.AddRange(bindingLifecycle.PrepareStatements);
            }
            bodyStatements.AddRange(nested.Statements);
            if (nested._hasDeferredLifetimeRegistrations)
            {
                if (!markupLifecycleProfile!.TryCreateDeferredMarkupExtensionFinalization(
                        markupLifecycle!.RegistrationOwner,
                        rootExpression,
                        out var markupFinalization))
                {
                    AddError(
                        "PGXAML3047",
                        $"Profile '{_framework.Id}' rejected per-instance markup-extension " +
                        "lifecycle finalization for deferred content.",
                        operation.SourceSpan,
                        "EXT-004");
                    return;
                }
                bodyStatements.AddRange(markupFinalization);
            }
            if (nested._hasCompiledBindings)
            {
                if (!bindingLifecycleProfile!.TryCreateDeferredCompiledBindingFinalization(
                        bindingLifecycle!.RegistrationOwner,
                        rootExpression,
                        out var finalization))
                {
                    AddError(
                        "PGXAML3046",
                        $"Profile '{_framework.Id}' rejected per-instance compiled-binding " +
                        "finalization for deferred content.",
                        operation.SourceSpan,
                        "EXT-004");
                    return;
                }
                bodyStatements.AddRange(finalization);
            }
            bodyStatements.Add(SyntaxFactory.ReturnStatement(rootExpression));
            var factory = SyntaxFactory.ParenthesizedLambdaExpression()
                .WithParameterList(SyntaxFactory.ParameterList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Parameter(SyntaxFactory.Identifier("__templateContext"))
                            .WithType(SyntaxFactory.NullableType(
                                SyntaxFactory.PredefinedType(
                                    SyntaxFactory.Token(SyntaxKind.ObjectKeyword)))))))
                .WithBlock(SyntaxFactory.Block(bodyStatements));
            if (!deferredProfile.TryCreateDeferredContentAssignment(member, ownerExpression, factory, out var assignment))
            {
                AddError("PGXAML5001",
                    $"Profile '{_framework.Id}' rejected deferred member '{operation.Member.RequestedName.DisplayName}'.",
                    operation.SourceSpan,
                    "5.2.1.2");
                return;
            }
            AddStatement(assignment, operation.SourceSpan, operation.StableId, null, XamlProjectionKind.MemberAssignment);
        }

        private void RegisterBindingAccessors(XamlIrBinding binding)
        {
            if (binding.Binding.Accessors.IsEmpty)
                return;
            if (_framework is not IRoslynXamlBindingAccessorRegistrationProfile profile)
            {
                AddError(
                    "PGXAML3048",
                    $"Profile '{_framework.Id}' cannot publish typed ordinary-binding accessors.",
                    binding.SourceSpan,
                    "EXT-004");
                return;
            }
            foreach (var accessor in binding.Binding.Accessors)
            {
                var key =
                    accessor.SourceType.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat) +
                    "\0" +
                    accessor.Kind.ToString() +
                    "\0" +
                    (accessor.Kind switch
                    {
                        XamlBindingPathAccessorKind.IntegerIndexer =>
                            accessor.IntegerIndex.ToString(
                                CultureInfo.InvariantCulture),
                        XamlBindingPathAccessorKind.StringIndexer =>
                            accessor.StringIndex,
                        _ => accessor.Member.MetadataName
                    }) +
                    "\0" +
                    accessor.ValueType.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat);
                if (_bindingAccessorRegistrations.ContainsKey(key))
                    continue;
                if (!profile.TryCreateBindingAccessorRegistration(
                        accessor,
                        out var statement))
                {
                    AddError(
                        "PGXAML3048",
                        $"Profile '{_framework.Id}' rejected typed ordinary-binding accessor " +
                        $"'{accessor.SourceType.ToDisplayString()}.{accessor.Member.Name}'.",
                        binding.SourceSpan,
                        "EXT-004");
                    continue;
                }
                _bindingAccessorRegistrations.Add(key, statement);
            }
        }

        private void MergeBindingAccessorRegistrations(EmitContext nested)
        {
            foreach (var registration in nested._bindingAccessorRegistrations)
            {
                if (!_bindingAccessorRegistrations.ContainsKey(registration.Key))
                    _bindingAccessorRegistrations.Add(
                        registration.Key,
                        registration.Value);
            }
        }

        private ExpressionSyntax? EmitDeferredRoot(XamlIrObject root)
        {
            if (root.Type.Symbol == null) return null;
            var variableName = "__templateRoot" + _nextObjectId++.ToString(CultureInfo.InvariantCulture);
            var variable = SyntaxFactory.IdentifierName(variableName);
            var construction = CreateConstructionExpression(root);
            if (construction == null) return null;
            AddStatement(SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                        .AddVariables(SyntaxFactory.VariableDeclarator(variableName)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(construction)))),
                root.SourceSpan,
                root.StableId);
            _lookupRootExpression = variable;
            RegisterObjectName(root, variable);
            EmitOperations(root, variable, isRoot: true);
            foreach (var action in _pendingNameReferenceActions) action();
            Statements.AddRange(_pendingFieldAssignments);
            return variable;
        }

        private void EmitDirective(
            XamlIrObject owner,
            ExpressionSyntax ownerExpression,
            XamlIrOperation operation,
            bool isRoot)
        {
            if (!string.Equals(operation.Member.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) ||
                !string.Equals(operation.Member.RequestedName.LocalName, "Name", StringComparison.Ordinal)) return;
            var text = operation.Values.OfType<XamlIrText>().FirstOrDefault();
            if (text == null || owner.Type.Symbol == null) return;
            if (!SyntaxFacts.IsValidIdentifier(text.Text))
            {
                AddError("PGXAML3002", $"x:Name '{text.Text}' is not a valid C# identifier.",
                    text.SourceSpan, "6.3.2.6");
                return;
            }

            if (!isRoot && _isClassBacked)
            {
                var nullForgiving = SyntaxFactory.PostfixUnaryExpression(
                    SyntaxKind.SuppressNullableWarningExpression,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                Fields.Add(SyntaxFactory.FieldDeclaration(
                        SyntaxFactory.VariableDeclaration(RoslynTypeSyntaxFactory.Create(owner.Type.Symbol.Symbol))
                            .AddVariables(SyntaxFactory.VariableDeclarator(EscapeIdentifier(text.Text))
                                .WithInitializer(SyntaxFactory.EqualsValueClause(nullForgiving))))
                    .WithModifiers(ParseAccessibility(
                        GetDirectiveText(owner, XamlNamespaces.Language2006, "FieldModifier") ?? "internal",
                        operation.SourceSpan,
                        "x:FieldModifier")));
                _pendingFieldAssignments.Add(Annotate(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        MemberAccess(SyntaxFactory.ThisExpression(), text.Text),
                        ownerExpression)), operation.SourceSpan, operation.StableId, null, XamlProjectionKind.Name));
            }

            var runtimeName = owner.Type.Symbol.RuntimeNameMemberName;
            if (!string.IsNullOrEmpty(runtimeName))
            {
                var runtimeMember = FindInstanceProperty(owner.Type.Symbol.Symbol, runtimeName!);
                if (runtimeMember == null) return;
                AddStatement(Assignment(
                    MemberAccess(ownerExpression, runtimeName!),
                    WinUiXamlProfile.StringLiteral(text.Text)),
                    operation.SourceSpan,
                    operation.StableId,
                    runtimeMember,
                    XamlProjectionKind.Name);
            }
        }

        private static IPropertySymbol? FindInstanceProperty(ITypeSymbol type, string name)
        {
            for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            {
                var property = current.GetMembers(name).OfType<IPropertySymbol>()
                    .FirstOrDefault(static candidate => !candidate.IsStatic);
                if (property != null) return property;
            }
            return null;
        }

        private void EmitEvent(ExpressionSyntax ownerExpression, XamlIrOperation operation)
        {
            var member = operation.Member.Symbol!;
            var compiledBinding = operation.Values.OfType<XamlIrCompiledBinding>().SingleOrDefault();
            if (compiledBinding != null &&
                _framework is IRoslynXamlCompiledEventBindingProfile eventBindings &&
                GetCompiledBindingSource(compiledBinding) is { } compiledBindingSource &&
                eventBindings.TryCreateCompiledEventBinding(
                    compiledBinding,
                    member,
                    ownerExpression,
                    compiledBindingSource,
                    out var compiledStatement))
            {
                AddStatement(
                    compiledStatement,
                    operation.SourceSpan,
                    operation.StableId,
                    member.Symbol,
                    XamlProjectionKind.Event);
                _hasCompiledBindings = true;
                return;
            }
            var handlerName = operation.Values.OfType<XamlIrText>().FirstOrDefault()?.Text;
            if (handlerName == null || !SyntaxFacts.IsValidIdentifier(handlerName))
            {
                AddError("PGXAML3003", $"Event handler '{handlerName}' is not a valid C# method name.",
                    operation.SourceSpan, "6.2.1.2");
                return;
            }
            var handler = MemberAccess(SyntaxFactory.ThisExpression(), handlerName);
            AddStatement(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                SyntaxKind.AddAssignmentExpression,
                MemberAccess(ownerExpression, member.CSharpName),
                handler)), operation.SourceSpan, operation.StableId, member.Symbol, XamlProjectionKind.Event);
        }

        private ExpressionSyntax? GetCompiledBindingSource(XamlIrCompiledBinding binding)
        {
            if (binding.Binding.SourceKind ==
                ProGPU.Xaml.Binding.XamlCompiledBindingSourceKind.Context)
            {
                if (_contextExpression != null) return _contextExpression;
                AddError(
                    "PGXAML3044",
                    "A context-scoped compiled binding is not inside a deferred template factory.",
                    binding.SourceSpan,
                    "EXT-004");
                return null;
            }
            return _lookupRootExpression;
        }

        private void EmitSet(ExpressionSyntax ownerExpression, XamlIrOperation operation, ExpressionSyntax value)
        {
            var member = operation.Member.Symbol!;
            if (operation.Kind == XamlIrOperationKind.SetAttachableMember)
            {
                var method = (IMethodSymbol)member.Symbol!;
                var target = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(method.ContainingType),
                    SyntaxFactory.IdentifierName(EscapeIdentifier(method.Name)));
                AddStatement(Invocation(target, ownerExpression, value), operation.SourceSpan,
                    operation.StableId, member.Symbol, XamlProjectionKind.MemberAssignment);
            }
            else
            {
                AddStatement(Assignment(MemberAccess(ownerExpression, member.CSharpName), value),
                    operation.SourceSpan, operation.StableId, member.Symbol, XamlProjectionKind.MemberAssignment);
            }
        }

        private void EmitCollectionItems(
            XamlIrObject owner,
            ExpressionSyntax ownerExpression,
            XamlIrOperation operation)
        {
            var collectionType = operation.Member.Symbol?.ValueType ?? owner.Type.Symbol!;
            var collectionExpression = operation.Member.Symbol == null
                ? ownerExpression
                : MemberAccess(ownerExpression, operation.Member.Symbol.CSharpName);
            if (operation.Member.Symbol?.AttachableShape is { Setter: null } attachedCollection)
            {
                var getter = attachedCollection.Getter;
                var target = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(getter.ContainingType),
                    SyntaxFactory.IdentifierName(EscapeIdentifier(getter.Name)));
                collectionExpression = SyntaxFactory.InvocationExpression(target)
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(ownerExpression))));
            }
            collectionExpression = CreateInsertionReceiver(collectionExpression, collectionType);
            var addMethod = collectionType.CollectionShape?.AddMethod.Name ?? "Add";
            foreach (var value in operation.Values)
            {
                if (TryGetNameReference(value, out var reference) && !_nameExpressions.ContainsKey(reference.Name))
                {
                    _pendingNameReferenceActions.Add(() =>
                    {
                        var deferred = ResolveNameReference(reference);
                        if (deferred != null)
                            AddStatement(Invocation(MemberAccess(collectionExpression, addMethod), deferred),
                                value.SourceSpan, value.StableId, operation.Member.Symbol?.Symbol, XamlProjectionKind.MemberAssignment);
                    });
                    continue;
                }
                var targetType = collectionType.CollectionShape == null
                    ? operation.Member.Symbol?.ValueType ?? owner.Type.Symbol!
                    : DescribeTemporary(collectionType.CollectionShape.ItemType, collectionType.NamespaceUri);
                if (TryEmitTopDownObject(
                        value,
                        expression => AddStatement(
                            Invocation(
                                MemberAccess(collectionExpression, addMethod),
                                expression),
                            value.SourceSpan,
                            value.StableId,
                            operation.Member.Symbol?.Symbol,
                            XamlProjectionKind.MemberAssignment)))
                    continue;
                var expression = EmitValue(value, targetType, member: operation.Member.Symbol?.Symbol);
                if (expression != null)
                    AddStatement(Invocation(MemberAccess(collectionExpression, addMethod), expression),
                        value.SourceSpan, value.StableId, operation.Member.Symbol?.Symbol, XamlProjectionKind.MemberAssignment);
            }
        }

        private void RegisterObjectName(XamlIrObject value, ExpressionSyntax expression)
        {
            var name = GetDirectiveText(value, XamlNamespaces.Language2006, "Name");
            if (!string.IsNullOrWhiteSpace(name) && !_nameExpressions.ContainsKey(name!))
                _nameExpressions.Add(name!, expression);
        }

        private ExpressionSyntax? ResolveNameReference(XamlIrNameReference reference)
        {
            if (_nameExpressions.TryGetValue(reference.Name, out var expression)) return expression;
            AddError("PGXAML3039", $"x:Reference name '{reference.Name}' is unavailable during structured emission.",
                reference.SourceSpan, "6.2.2.11");
            return null;
        }

        private static bool TryGetNameReference(XamlIrValue value, out XamlIrNameReference reference)
        {
            if (value is XamlIrNameReference direct)
            {
                reference = direct;
                return true;
            }
            if (value is XamlIrObject objectValue &&
                string.Equals(objectValue.Type.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
                (string.Equals(objectValue.Type.RequestedName.LocalName, "Reference", StringComparison.Ordinal) ||
                 string.Equals(objectValue.Type.RequestedName.LocalName, "ReferenceExtension", StringComparison.Ordinal)))
            {
                foreach (var operation in objectValue.Operations)
                    foreach (var child in operation.Values)
                        if (child is XamlIrNameReference found)
                        {
                            reference = found;
                            return true;
                        }
            }
            reference = null!;
            return false;
        }

        private void EmitDictionaryItems(
            XamlIrObject owner,
            ExpressionSyntax ownerExpression,
            XamlIrOperation operation)
        {
            var dictionaryType = operation.Member.Symbol?.ValueType ?? owner.Type.Symbol!;
            var dictionaryExpression = operation.Member.Symbol == null
                ? ownerExpression
                : MemberAccess(ownerExpression, operation.Member.Symbol.CSharpName);
            dictionaryExpression = CreateInsertionReceiver(dictionaryExpression, dictionaryType);
            var addMethod = dictionaryType.CollectionShape?.AddMethod.Name ?? "Add";
            var dictionaryItems = operation.Values.ToArray();
            if (Options.StaticResourceForwardReferenceMode ==
                XamlStaticResourceForwardReferenceMode.Reorder)
                dictionaryItems = OrderDictionaryItemsByStaticDependencies(dictionaryItems);
            foreach (var value in dictionaryItems)
            {
                var key = FindDictionaryKey(value);
                if (key == null)
                {
                    AddError("PGXAML2008", "Objects in a dictionary require x:Key or an implicit key member.",
                        value.SourceSpan, "6.3.1.4");
                    continue;
                }
                var valueType = dictionaryType.CollectionShape == null
                    ? value is XamlIrObject objectValue
                        ? objectValue.Type.Symbol
                        : (value as XamlIrResourceReference)?.ResolvedValueType
                    : DescribeTemporary(dictionaryType.CollectionShape.ItemType, dictionaryType.NamespaceUri);
                if (valueType == null) continue;
                var keySymbol = dictionaryType.CollectionShape?.KeyType;
                var keyType = keySymbol == null
                    ? null
                    : DescribeTemporary(keySymbol, dictionaryType.NamespaceUri);
                var keyExpression = CreateResourceKeyExpression(key, keyType);
                if (keyExpression == null) continue;
                if (TryEmitTopDownObject(
                        value,
                        expression => AddStatement(
                            Invocation(
                                MemberAccess(dictionaryExpression, addMethod),
                                keyExpression,
                                expression),
                            value.SourceSpan,
                            value.StableId,
                            operation.Member.Symbol?.Symbol,
                            XamlProjectionKind.Resource)))
                    continue;
                var expression = EmitValue(
                    value,
                    valueType,
                    value as XamlIrObject,
                    operation.Member.Symbol?.Symbol);
                if (expression == null) continue;
                AddStatement(Invocation(MemberAccess(dictionaryExpression, addMethod),
                        keyExpression, expression),
                    value.SourceSpan, value.StableId, operation.Member.Symbol?.Symbol, XamlProjectionKind.Resource);
            }
        }

        private XamlIrValue[] OrderDictionaryItemsByStaticDependencies(XamlIrValue[] items)
        {
            if (items.Length < 2) return items;

            var indexByStableId = new Dictionary<ulong, int>();
            for (var index = 0; index < items.Length; index++)
                indexByStableId[items[index].StableId] = index;

            var dependents = new List<int>[items.Length];
            var indegree = new int[items.Length];
            for (var index = 0; index < dependents.Length; index++)
                dependents[index] = new List<int>();

            for (var index = 0; index < items.Length; index++)
            {
                var dependencyIds = new HashSet<ulong>();
                CollectImmediateStaticResourceDependencies(items[index], dependencyIds);
                foreach (var dependencyId in dependencyIds)
                {
                    if (!indexByStableId.TryGetValue(dependencyId, out var dependencyIndex))
                        continue;
                    dependents[dependencyIndex].Add(index);
                    indegree[index]++;
                }
            }

            var ready = new SortedSet<int>();
            for (var index = 0; index < indegree.Length; index++)
                if (indegree[index] == 0) ready.Add(index);
            var result = new XamlIrValue[items.Length];
            var resultIndex = 0;
            while (ready.Count != 0)
            {
                var index = ready.Min;
                ready.Remove(index);
                result[resultIndex++] = items[index];
                foreach (var dependent in dependents[index])
                {
                    indegree[dependent]--;
                    if (indegree[dependent] == 0) ready.Add(dependent);
                }
            }

            if (resultIndex == items.Length) return result;
            AddError(
                "PGXAML4010",
                "Compiled resource entries contain a StaticResource dependency cycle and cannot be safely reordered.",
                items.First(item => indegree[indexByStableId[item.StableId]] != 0).SourceSpan,
                "6.3.2.4");
            return items;
        }

        private static void CollectImmediateStaticResourceDependencies(
            XamlIrValue value,
            ISet<ulong> dependencies)
        {
            if (value is XamlIrResourceReference resourceReference)
            {
                if (resourceReference.Reference.Kind == XamlResourceReferenceKind.Static &&
                    resourceReference.Reference.DefinitionStableId is { } definitionStableId)
                    dependencies.Add(definitionStableId);
                return;
            }
            if (value is not XamlIrObject objectValue) return;
            foreach (var operation in objectValue.Operations)
            {
                // Deferred template content executes after the containing dictionary is
                // available and therefore does not constrain provider population order.
                if (operation.Kind == XamlIrOperationKind.SetDeferredContent) continue;
                foreach (var child in operation.Values)
                    CollectImmediateStaticResourceDependencies(child, dependencies);
            }
        }

        private XamlResourceKeyInfo? FindDictionaryKey(XamlIrValue value)
        {
            var graphKey = _program.ResourceGraph?.Definitions
                .FirstOrDefault(definition => definition.ValueStableId == value.StableId)?.ResourceKey;
            if (graphKey != null) return graphKey;
            if (value is not XamlIrObject objectValue) return null;
            foreach (var operation in objectValue.Operations)
            {
                if (operation.Member.Kind == XamlBoundReferenceKind.Directive &&
                    string.Equals(operation.Member.RequestedName.NamespaceUri, XamlNamespaces.Language2006, StringComparison.Ordinal) &&
                    string.Equals(operation.Member.RequestedName.LocalName, "Key", StringComparison.Ordinal))
                    return FindResourceKey(operation.Values);
                if (objectValue.Type.Symbol?.DictionaryKeyMemberName != null && operation.Member.Symbol != null &&
                    string.Equals(operation.Member.Symbol.Name, objectValue.Type.Symbol.DictionaryKeyMemberName, StringComparison.Ordinal))
                    return FindResourceKey(operation.Values);
            }
            return null;
        }

        private static XamlResourceKeyInfo? FindResourceKey(IEnumerable<XamlIrValue> values)
        {
            foreach (var value in values)
            {
                switch (value)
                {
                    case XamlIrText text:
                        return XamlResourceKeyInfo.FromText(text.Text, text.SourceSpan, text.StableId);
                    case XamlIrType type when type.Type.Symbol != null:
                        return XamlResourceKeyInfo.FromType(type.Type.Symbol.Symbol, type.SourceSpan, type.StableId);
                    case XamlIrStaticMember member when member.Value.Member != null:
                        return XamlResourceKeyInfo.FromStaticMember(member.Value.Member, member.SourceSpan, member.StableId);
                    case XamlIrObject nested:
                    {
                        var found = FindResourceKey(nested.Operations.SelectMany(static operation => operation.Values));
                        if (found != null) return found;
                        break;
                    }
                }
            }
            return null;
        }

        private ExpressionSyntax? CreateResourceKeyExpression(
            XamlResourceKeyInfo key,
            XamlTypeInfo? targetType)
        {
            switch (key.Kind)
            {
                case XamlResourceKeyKind.Text:
                    if (targetType == null)
                        return WinUiXamlProfile.StringLiteral(key.Text);
                    if (_framework.TryCreateLiteralExpression(targetType, key.Text, out var literal))
                        return literal;
                    AddError("PGXAML2108",
                        $"Resource key '{key.Text}' cannot be converted to dictionary key type '{targetType.MetadataName}'.",
                        key.SourceSpan,
                        "6.3.1.4");
                    return null;
                case XamlResourceKeyKind.Type when key.TypeSymbol != null:
                    return SyntaxFactory.TypeOfExpression(RoslynTypeSyntaxFactory.Create(key.TypeSymbol));
                case XamlResourceKeyKind.StaticMember when key.StaticMember?.ContainingType != null:
                    return CreateStaticMemberExpression(key.StaticMember);
                default:
                    AddError("PGXAML2109", "The resource key cannot be represented as a structured C# expression.",
                        key.SourceSpan, "6.3.1.4");
                    return null;
            }
        }

        private static ExpressionSyntax CreateStaticMemberExpression(ISymbol member) =>
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)RoslynTypeSyntaxFactory.Create(member.ContainingType!),
                SyntaxFactory.IdentifierName(EscapeIdentifier(member.Name)));

        private static ExpressionSyntax CreateInsertionReceiver(
            ExpressionSyntax receiver,
            XamlTypeInfo collectionType)
        {
            var method = collectionType.CollectionShape?.AddMethod;
            if (method?.ContainingType.TypeKind != TypeKind.Interface) return receiver;
            return SyntaxFactory.ParenthesizedExpression(SyntaxFactory.CastExpression(
                RoslynTypeSyntaxFactory.Create(method.ContainingType),
                receiver));
        }

        private static XamlTypeInfo DescribeTemporary(ITypeSymbol symbol, string namespaceUri) => new XamlTypeInfo(
            namespaceUri,
            symbol.Name,
            symbol,
            symbol.ToDisplayString(),
            symbol.IsValueType,
            symbol.TypeKind == TypeKind.Enum,
            symbol.NullableAnnotation == NullableAnnotation.Annotated || !symbol.IsValueType,
            isCollection: false,
            isDictionary: false,
            contentMemberName: null);

        private SyntaxTokenList ParseAccessibility(string? value, TextSpan span, string directive)
        {
            if (string.IsNullOrWhiteSpace(value)) return default;
            switch (value!.Trim().ToLowerInvariant())
            {
                case "public": return SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                case "private": return SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
                case "protected": return SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                case "internal":
                case "notpublic": return SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword));
                case "protected internal": return SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
                    SyntaxFactory.Token(SyntaxKind.InternalKeyword));
                case "private protected": return SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                    SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
                default:
                    AddError("PGXAML3004", $"{directive} value '{value}' is not a supported accessibility.",
                        span, directive == "x:ClassModifier" ? "6.3.1.8" : "6.3.1.10");
                    return default;
            }
        }

        private void AddError(string id, string message, TextSpan span, string section) =>
            _diagnostics.Add(CreateError(_program.BoundDocument.Infoset.Syntax, id, message, span, section));

        private void AddStatement(
            StatementSyntax statement,
            TextSpan span,
            ulong stableId,
            ISymbol? member = null,
            XamlProjectionKind kind = XamlProjectionKind.Construction) =>
            Statements.Add(Annotate(statement, span, stableId, member, kind));

        private StatementSyntax Annotate(
            StatementSyntax statement,
            TextSpan span,
            ulong stableId,
            ISymbol? member,
            XamlProjectionKind kind) => statement.WithAdditionalAnnotations(XamlProjectionMap.CreateAnnotation(
                _program.BoundDocument.Infoset.Path,
                _checksum,
                stableId,
                kind,
                member,
                span));

        private ExpressionSyntax AnnotateExpression(
            ExpressionSyntax expression,
            TextSpan span,
            ulong stableId,
            ISymbol? member,
            XamlProjectionKind kind) => expression.WithAdditionalAnnotations(XamlProjectionMap.CreateAnnotation(
                _program.BoundDocument.Infoset.Path,
                _checksum,
                stableId,
                kind,
                member,
                span));
    }

    private static string? GetDirectiveText(XamlIrObject value, string namespaceUri, string localName)
    {
        foreach (var operation in value.Operations)
        {
            if (operation.Member.Kind == XamlBoundReferenceKind.Directive &&
                string.Equals(operation.Member.RequestedName.NamespaceUri, namespaceUri, StringComparison.Ordinal) &&
                string.Equals(operation.Member.RequestedName.LocalName, localName, StringComparison.Ordinal))
                return operation.Values.OfType<XamlIrText>().FirstOrDefault()?.Text;
        }
        return null;
    }

    private static string? GetDirectiveText(
        XamlInfosetObject value,
        string namespaceUri,
        string localName)
    {
        foreach (var member in value.Members)
        {
            if (!member.Name.IsDirective ||
                !string.Equals(member.Name.NamespaceUri, namespaceUri, StringComparison.Ordinal) ||
                !string.Equals(member.Name.LocalName, localName, StringComparison.Ordinal))
            {
                continue;
            }
            return member.Values.OfType<XamlInfosetText>().FirstOrDefault()?.Text;
        }
        return null;
    }

    private static string GetBuildMetadataDiagnosticId(
        XamlBuildMetadataIssueKind kind) => kind switch
        {
            XamlBuildMetadataIssueKind.CompilationMode => "PGXAML2072",
            XamlBuildMetadataIssueKind.FilePath => "PGXAML2073",
            XamlBuildMetadataIssueKind.ResourceIdentity => "PGXAML2074",
            XamlBuildMetadataIssueKind.RootNamespace => "PGXAML2075",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static Diagnostic CreateError(
        XamlDocumentSyntax document,
        string id,
        string message,
        TextSpan span,
        string section) => XamlDiagnostics.Create(
        id,
        DiagnosticSeverity.Error,
        message,
        document.Path,
        document.SyntaxTree.GetText(),
        span,
        section);

    private static bool ContainsErrors(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            if (diagnostic.Severity == DiagnosticSeverity.Error) return true;
        return false;
    }

    private static bool IsValidQualifiedTypeName(string namespaceName, string typeName) =>
        SyntaxFacts.IsValidIdentifier(typeName) &&
        (namespaceName.Length == 0 || namespaceName.Split('.').All(SyntaxFacts.IsValidIdentifier));

    private static NameSyntax CreateNamespaceName(string namespaceName)
    {
        var segments = namespaceName.Split('.');
        NameSyntax result = SyntaxFactory.IdentifierName(EscapeIdentifier(segments[0]));
        for (var index = 1; index < segments.Length; index++)
            result = SyntaxFactory.QualifiedName(result, SyntaxFactory.IdentifierName(EscapeIdentifier(segments[index])));
        return result;
    }

    private static string GetHintName(string className, string path)
    {
        var identifier = new char[className.Length];
        for (var index = 0; index < className.Length; index++)
        {
            var current = className[index];
            identifier[index] = char.IsLetterOrDigit(current) || current == '_' ? current : '_';
        }
        return new string(identifier) + "." + ComputeStableHash(path).ToString("x16", CultureInfo.InvariantCulture) +
               ".ProGPU.Xaml.g.cs";
    }

    private static ulong ComputeStableHash(string value)
    {
        unchecked
        {
            var hash = 14695981039346656037UL;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }

    private static string ToHex(ImmutableArray<byte> checksum)
    {
        if (checksum.IsDefaultOrEmpty) return string.Empty;
        var builder = new StringBuilder(checksum.Length * 2);
        foreach (var value in checksum) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static ExpressionStatementSyntax Assignment(ExpressionSyntax left, ExpressionSyntax right) =>
        SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression, left, right));

    private static ExpressionStatementSyntax Invocation(ExpressionSyntax target, params ExpressionSyntax[] arguments) =>
        SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(target)
            .WithArgumentList(ArgumentList(arguments)));

    private static ArgumentListSyntax ArgumentList(ExpressionSyntax[] expressions)
    {
        var arguments = new ArgumentSyntax[expressions.Length];
        for (var index = 0; index < expressions.Length; index++) arguments[index] = SyntaxFactory.Argument(expressions[index]);
        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments));
    }

    private static MemberAccessExpressionSyntax MemberAccess(ExpressionSyntax target, string memberName) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            target,
            SyntaxFactory.IdentifierName(EscapeIdentifier(memberName)));

    private static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;
}
