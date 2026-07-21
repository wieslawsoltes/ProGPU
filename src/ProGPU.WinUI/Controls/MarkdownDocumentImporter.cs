using System;
using Microsoft.UI.Xaml.Documents;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Markdig-backed adapter into the shared rich-document model. Markdown parsing stops
/// at semantic blocks/inlines; all shaping, bidi, wrapping, virtualization, and drawing
/// are owned by the common document layout engine.
/// </summary>
public sealed class MarkdownDocumentImporter : IRichDocumentImporter<string>
{
    public static MarkdownDocumentImporter Default { get; } = new();

    public RichDocument Import(string source, in RichDocumentImportContext context)
    {
        source ??= string.Empty;
        var document = new RichDocument();
        document.ReplaceBlocks(MarkdownParser.Parse(
            source,
            context.DefaultForeground,
            context.DefaultFontSize,
            context.DefaultFont,
            context.CodeFont,
            context.Theme));
        return document;
    }
}
