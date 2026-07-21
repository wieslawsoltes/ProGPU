using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

public sealed class PlainTextDocumentExporter : IRichDocumentExporter<string>
{
    public static PlainTextDocumentExporter Default { get; } = new();

    public string Export(RichDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        for (int index = 0; index < document.Blocks.Count; index++)
        {
            if (index > 0) builder.AppendLine();
            AppendBlockText(document.Blocks[index], builder);
        }
        return builder.ToString();
    }

    internal static void AppendBlockText(Block block, StringBuilder builder)
    {
        switch (block)
        {
            case Paragraph paragraph:
                AppendInlinesText(paragraph.Inlines, builder);
                break;
            case ListBlock list:
                for (int index = 0; index < list.Items.Count; index++)
                {
                    if (index > 0) builder.AppendLine();
                    AppendInlinesText(list.Items[index].Inlines, builder);
                }
                break;
            case Table table:
                for (int row = 0; row < table.Rows.Count; row++)
                {
                    if (row > 0) builder.AppendLine();
                    for (int cell = 0; cell < table.Rows[row].Cells.Count; cell++)
                    {
                        if (cell > 0) builder.Append('\t');
                        AppendInlinesText(table.Rows[row].Cells[cell].Inlines, builder);
                    }
                }
                break;
            case Inline inline:
                AppendInlineText(inline, builder);
                break;
        }
    }

    internal static void AppendInlinesText(IEnumerable<Inline> inlines, StringBuilder builder)
    {
        foreach (Inline inline in inlines) AppendInlineText(inline, builder);
    }

    internal static void AppendInlineText(Inline inline, StringBuilder builder)
    {
        switch (inline)
        {
            case Run run:
                builder.Append(run.Text);
                break;
            case LineBreak:
                builder.AppendLine();
                break;
            case Span span:
                AppendInlinesText(span.Inlines, builder);
                break;
            case InlineUIContainer:
                builder.Append('\uFFFC');
                break;
        }
    }
}
