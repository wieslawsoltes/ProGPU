using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ProGPU.Xaml.Roslyn;

/// <summary>
/// Normalizes a workspace compilation to the same input boundary observed by an
/// incremental generator before its own outputs are added.
/// </summary>
public static class RoslynXamlHostCompilation
{
    public const string GeneratedXamlTreeSuffix = ".ProGPU.Xaml.g.cs";

    public static Compilation WithoutGeneratedXamlTrees(
        Compilation compilation)
    {
        if (compilation == null)
            throw new ArgumentNullException(nameof(compilation));
        var generatedTrees = compilation.SyntaxTrees
            .Where(IsGeneratedXamlTree)
            .ToArray();
        return generatedTrees.Length == 0
            ? compilation
            : compilation.RemoveSyntaxTrees(generatedTrees);
    }

    public static bool IsGeneratedXamlTree(SyntaxTree syntaxTree)
    {
        if (syntaxTree == null)
            throw new ArgumentNullException(nameof(syntaxTree));
        return syntaxTree.FilePath.EndsWith(
            GeneratedXamlTreeSuffix,
            StringComparison.OrdinalIgnoreCase);
    }
}
