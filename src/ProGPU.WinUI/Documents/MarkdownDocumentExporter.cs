using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

public sealed class MarkdownDocumentExporter : IRichDocumentExporter<string>
{
    public static MarkdownDocumentExporter Default { get; } = new();

    public string Export(RichDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        for (int index = 0; index < document.Blocks.Count; index++)
        {
            if (index > 0) builder.AppendLine().AppendLine();
            AppendBlock(document.Blocks[index], builder);
        }
        return builder.ToString();
    }

    private static void AppendBlock(Block block, StringBuilder builder)
    {
        switch (block)
        {
            case Paragraph paragraph:
                AppendInlines(paragraph.Inlines, builder);
                break;
            case ListBlock list:
                for (int index = 0; index < list.Items.Count; index++)
                {
                    if (index > 0) builder.AppendLine();
                    builder.Append(list.IsOrdered ? $"{index + 1}. " : "- ");
                    AppendInlines(list.Items[index].Inlines, builder);
                }
                break;
            case Table table:
                AppendTable(table, builder);
                break;
            case Inline inline:
                AppendInline(inline, builder);
                break;
        }
    }

    private static void AppendTable(Table table, StringBuilder builder)
    {
        if (table.Rows.Count == 0) return;
        AppendTableRow(table.Rows[0], builder);
        builder.AppendLine();
        builder.Append('|');
        for (int cell = 0; cell < table.Rows[0].Cells.Count; cell++) builder.Append(" --- |");
        for (int row = 1; row < table.Rows.Count; row++)
        {
            builder.AppendLine();
            AppendTableRow(table.Rows[row], builder);
        }
    }

    private static void AppendTableRow(TableRow row, StringBuilder builder)
    {
        builder.Append('|');
        for (int cell = 0; cell < row.Cells.Count; cell++)
        {
            builder.Append(' ');
            AppendInlines(row.Cells[cell].Inlines, builder);
            builder.Append(" |");
        }
    }

    private static void AppendInlines(IEnumerable<Inline> inlines, StringBuilder builder)
    {
        foreach (Inline inline in inlines) AppendInline(inline, builder);
    }

    private static void AppendInline(Inline inline, StringBuilder builder)
    {
        switch (inline)
        {
            case Run run:
                AppendEscaped(run.Text, builder);
                break;
            case LineBreak:
                builder.Append("  ").AppendLine();
                break;
            case Hyperlink link:
                builder.Append('[');
                AppendInlines(link.Inlines, builder);
                builder.Append("](");
                builder.Append(link.Uri.Replace(")", "\\)", StringComparison.Ordinal));
                builder.Append(')');
                break;
            case Bold bold:
                builder.Append("**");
                AppendInlines(bold.Inlines, builder);
                builder.Append("**");
                break;
            case Italic italic:
                builder.Append('*');
                AppendInlines(italic.Inlines, builder);
                builder.Append('*');
                break;
            case Underline underline:
                builder.Append("<u>");
                AppendInlines(underline.Inlines, builder);
                builder.Append("</u>");
                break;
            case Span span:
                AppendInlines(span.Inlines, builder);
                break;
            case InlineUIContainer:
                builder.Append("<!-- embedded-object -->");
                break;
        }
    }

    private static void AppendEscaped(string text, StringBuilder builder)
    {
        foreach (char character in text)
        {
            if (character is '\\' or '*' or '_' or '[' or ']' or '`' or '<' or '>') builder.Append('\\');
            builder.Append(character);
        }
    }
}
