using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Formatting;
using ProGPU.Xaml.Generation;
using ProGPU.Xaml.Parsing;
using ProGPU.Xaml.Syntax;
using Xunit;

namespace ProGPU.Xaml.Tests;

public sealed class XamlMarkupTests
{
    [Fact]
    public void TokenizerIsLosslessAndNestedParserIsFrameworkNeutral()
    {
        const string value = "{Binding Path={x:Static local:State.Name}, Mode=TwoWay}";
        var source = SourceText.From(value);
        var result = new XamlMarkupExtensionParser().Parse(source, new TextSpan(0, source.Length));
        Assert.False(result.HasErrors);
        Assert.Equal(value, string.Concat(result.Tokens
            .Where(token => token.Kind != XamlMarkupTokenKind.EndOfFile)
            .Select(token => token.Text)));
        Assert.Equal("Binding", result.Root!.Name);
        Assert.IsType<XamlMarkupExtensionValue>(result.Root.NamedArguments[0].Value);
    }

    [Fact]
    public void FormatterProducesRoslynTextChangesAndIsIdempotent()
    {
        var source = SourceText.From("{Binding  Path = Name,  Mode = TwoWay } ");
        var parser = new XamlMarkupExtensionParser();
        var syntax = parser.Parse(source, new TextSpan(0, source.Length));
        var formatted = XamlMarkupFormatter.Format(syntax, source);
        Assert.Equal("{Binding Path=Name, Mode=TwoWay} ", formatted.ToString());
        var reparsed = parser.Parse(formatted, new TextSpan(0, formatted.Length));
        Assert.Empty(XamlMarkupFormatter.GetTextChanges(reparsed, formatted));
    }

    [Fact]
    public void GeneratorEscapesAndRoundTripsNestedValues()
    {
        var nested = new XamlMarkupExtension("StaticResource",
            new XamlMarkupValue[] { new XamlMarkupTextValue("key,with comma") },
            Array.Empty<XamlMarkupNamedArgument>());
        var root = new XamlMarkupExtension("Binding",
            Array.Empty<XamlMarkupValue>(),
            new[]
            {
                new XamlMarkupNamedArgument("Source", new XamlMarkupExtensionValue(nested)),
                new XamlMarkupNamedArgument("Path", new XamlMarkupTextValue("A B"))
            });
        var generated = XamlMarkupSyntaxGenerator.Generate(root);
        var parsed = new XamlMarkupExtensionParser().Parse(generated, new TextSpan(0, generated.Length));
        Assert.False(parsed.HasErrors);
        Assert.Equal("A B", ((XamlMarkupTextValue)parsed.Root!.NamedArguments[1].Value).Text);
    }

    [Fact]
    public void TriggerIndexedCustomRecognizerProducesCustomToken()
    {
        var source = SourceText.From("%name");
        var result = XamlMarkupTokenizer.Tokenize(source, new TextSpan(0, source.Length), options:
            new XamlMarkupParseOptions { TokenRecognizers = new[] { new PercentRecognizer() } });
        Assert.True(result.Tokens[0].IsCustom);
        Assert.Equal("%name", result.Tokens[0].Text);
    }

    [Fact]
    public void ConfiguredBracketPairsProtectNestedCommasAndEquals()
    {
        const string value =
            "{Binding Path=Items[(key,value)=selected], Mode=OneWay}";
        var source = SourceText.From(value);
        var options = new XamlMarkupParseOptions
        {
            BracketPairs = new Dictionary<char, char>
            {
                ['['] = ']',
                ['('] = ')'
            }
        };
        var result = new XamlMarkupExtensionParser().Parse(
            source,
            new TextSpan(0, source.Length),
            options: options);
        Assert.False(result.HasErrors, string.Join(
            Environment.NewLine,
            result.Diagnostics));
        Assert.Collection(
            result.Root!.NamedArguments,
            argument =>
            {
                Assert.Equal("Path", argument.Name);
                Assert.Equal(
                    "Items[(key,value)=selected]",
                    Assert.IsType<XamlMarkupTextValue>(argument.Value).Text);
            },
            argument =>
            {
                Assert.Equal("Mode", argument.Name);
                Assert.Equal(
                    "OneWay",
                    Assert.IsType<XamlMarkupTextValue>(argument.Value).Text);
            });
        Assert.Equal(
            value,
            string.Concat(result.Tokens
                .Where(token => token.Kind != XamlMarkupTokenKind.EndOfFile)
                .Select(token => token.Text)));

        var malformed = SourceText.From(
            "{Binding Path=Items[(key,value), Mode=OneWay}");
        Assert.True(new XamlMarkupExtensionParser().Parse(
            malformed,
            new TextSpan(0, malformed.Length),
            options: options).HasErrors);
    }

    [Fact]
    public void TypeNameParserSupportsNestedGenericArgumentsAndRecovery()
    {
        const string text = "local:Pair(x:String, local:Box(x:Int32)), x:Boolean";
        var result = new XamlTypeNameParser().Parse(text);
        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Types.Length);
        Assert.Equal("local:Pair", result.Types[0].QualifiedName);
        Assert.Equal(2, result.Types[0].TypeArguments.Length);
        Assert.Equal("local:Box", result.Types[0].TypeArguments[1].QualifiedName);
        Assert.Equal("x:Int32", Assert.Single(result.Types[0].TypeArguments[1].TypeArguments).QualifiedName);
        Assert.Equal(text.IndexOf("local:Pair", StringComparison.Ordinal), result.Types[0].Span.Start);

        var malformed = new XamlTypeNameParser().Parse("local:Pair(x:String,");
        Assert.True(malformed.HasErrors);
        Assert.Contains(malformed.Diagnostics, diagnostic =>
            diagnostic.Id == "PGXAML1160" && diagnostic.Properties["MSXamlSection"] == "7.4.16");

        Assert.True(new XamlTypeNameParser().Parse("local:Pair()").HasErrors);
        Assert.True(new XamlTypeNameParser().Parse("local:Pair(x:String,)").HasErrors);
        Assert.True(new XamlTypeNameParser().Parse("x:String,").HasErrors);
    }

    [Fact]
    public void BindingPathParserPreservesTokensAndProducesStableMemberSegments()
    {
        const string path = "  ViewModel . Customer_1 . DisplayName  ";
        var syntax = new XamlBindingPathParser().Parse(path);

        Assert.False(syntax.HasErrors);
        Assert.Equal(
            new[] { "ViewModel", "Customer_1", "DisplayName" },
            syntax.Segments.Select(segment => segment.Text));
        Assert.Equal(
            "ViewModel.Customer_1.DisplayName",
            string.Concat(syntax.Tokens
                .Where(token => token.Kind is
                    XamlBindingPathTokenKind.Identifier or XamlBindingPathTokenKind.Dot)
                .Select(token => token.Text)));
        Assert.Equal(
            path,
            string.Concat(syntax.Tokens
                .Where(token => token.Kind != XamlBindingPathTokenKind.EndOfInput)
                .Select(token => token.Text)));
        Assert.Equal(path.IndexOf("ViewModel", StringComparison.Ordinal), syntax.Segments[0].Span.Start);
    }

    [Fact]
    public void BindingPathParserProducesTypedLosslessIndexerSteps()
    {
        const string path = " Teams [ 2 ] . Players [ 'John ^'Smith' ] . Name ";
        var syntax = new XamlBindingPathParser().Parse(path);

        Assert.False(syntax.HasErrors);
        Assert.Equal(
            new[]
            {
                XamlBindingPathStepKind.Member,
                XamlBindingPathStepKind.IntegerIndexer,
                XamlBindingPathStepKind.Member,
                XamlBindingPathStepKind.StringIndexer,
                XamlBindingPathStepKind.Member
            },
            syntax.Steps.Select(static step => step.Kind));
        Assert.Equal(2, syntax.Steps[1].IntegerValue);
        Assert.Equal("John 'Smith", syntax.Steps[3].StringValue);
        Assert.Equal(
            path,
            string.Concat(syntax.Tokens
                .Where(token => token.Kind != XamlBindingPathTokenKind.EndOfInput)
                .Select(token => token.Text)));
        Assert.Equal("[ 2 ]", path.Substring(
            syntax.Steps[1].Span.Start,
            syntax.Steps[1].Span.Length));
    }

    [Theory]
    [InlineData(
        "((local:Derived)Model).Name",
        "Member:Model|Cast:local:Derived|Member:Name")]
    [InlineData(
        "Model.(local:Derived.Name)",
        "Member:Model|QualifiedMember:local:Derived.Name")]
    [InlineData(
        "Element.(Grid.Row)",
        "Member:Element|QualifiedMember:Grid.Row")]
    [InlineData(
        "(local:Item).Name",
        "Cast:local:Item|Member:Name")]
    public void BindingPathParserFlattensGroupedCastsAndQualifiedMembers(
        string path,
        string expected)
    {
        var syntax = new XamlBindingPathParser().Parse(path);

        Assert.False(syntax.HasErrors, string.Join(", ", syntax.ErrorSpans));
        Assert.Equal(
            expected,
            string.Join(
                "|",
                syntax.Steps.Select(static step => step.Kind switch
                {
                    XamlBindingPathStepKind.Cast =>
                        "Cast:" + step.TypeName,
                    XamlBindingPathStepKind.QualifiedMember =>
                        "QualifiedMember:" + step.TypeName + "." + step.MemberName,
                    _ => step.Kind + ":" + step.ValueToken.Text
                })));
        Assert.Equal(
            path,
            string.Concat(syntax.Tokens
                .Where(token => token.Kind != XamlBindingPathTokenKind.EndOfInput)
                .Select(token => token.Text)));
    }

    [Theory]
    [InlineData(
        "Format(Items[0].Title, 'prefix ^'value', -2.5, x:True)",
        false,
        null,
        "Format",
        "BindingPath|StringLiteral|NumericLiteral|BooleanLiteral")]
    [InlineData(
        "ViewModel.Format(Name, 42)",
        false,
        null,
        "Format",
        "BindingPath|NumericLiteral")]
    [InlineData(
        "local:Formatter.Format(Name, x:False)",
        true,
        "local:Formatter",
        "Format",
        "BindingPath|BooleanLiteral")]
    public void BindingPathParserProducesLosslessTypedFunctionCalls(
        string path,
        bool isStatic,
        string? typeName,
        string methodName,
        string argumentKinds)
    {
        var syntax = new XamlBindingPathParser().Parse(path);

        Assert.False(syntax.HasErrors, string.Join(", ", syntax.ErrorSpans));
        var function = Assert.Single(
            syntax.Steps.Where(static step =>
                step.Kind == XamlBindingPathStepKind.FunctionCall));
        Assert.Equal(isStatic, function.IsStaticFunction);
        Assert.Equal(typeName, function.TypeName);
        Assert.Equal(methodName, function.MemberName);
        Assert.Equal(
            argumentKinds,
            string.Join("|", function.Arguments.Select(static argument => argument.Kind)));
        Assert.Equal(
            path,
            string.Concat(syntax.Tokens
                .Where(token => token.Kind != XamlBindingPathTokenKind.EndOfInput)
                .Select(token => token.Text)));
    }

    [Theory]
    [InlineData(".Name")]
    [InlineData("Model..Name")]
    [InlineData("Model Name")]
    [InlineData("Model[]")]
    [InlineData("Model[999999999999999999999]")]
    [InlineData("Model['unterminated]")]
    [InlineData("((local:Type)Model.Name")]
    [InlineData("Model.(Grid.)")]
    [InlineData("Model.(local:.Name)")]
    [InlineData("Model.")]
    [InlineData("Format(Name, )")]
    [InlineData("Format(, Name)")]
    [InlineData("Format(Name")]
    [InlineData("local:Formatter.Format(Name,")]
    public void BindingPathParserRecoversUnsupportedOrMalformedSyntax(string path)
    {
        var syntax = new XamlBindingPathParser().Parse(path);
        Assert.True(syntax.HasErrors);
        Assert.NotEmpty(syntax.Tokens);
        Assert.Equal(XamlBindingPathTokenKind.EndOfInput, syntax.Tokens[^1].Kind);
    }

    private sealed class PercentRecognizer : IXamlMarkupTokenRecognizer
    {
        public string Id => "test.percent";
        public int Version => 1;
        public int Priority => 0;
        public IReadOnlyList<char> TriggerCharacters { get; } = new[] { '%' };
        public bool TryRecognize(SourceText source, TextSpan remaining, out XamlMarkupTokenRecognition recognition)
        {
            var length = 1;
            while (length < remaining.Length && char.IsLetter(source[remaining.Start + length])) length++;
            recognition = new XamlMarkupTokenRecognition((int)XamlMarkupTokenKind.FirstCustom, length);
            return true;
        }
    }
}
