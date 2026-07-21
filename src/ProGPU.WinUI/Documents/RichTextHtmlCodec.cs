using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>
/// Bounded, reflection-free HTML fragment adapter for the shared rich-document model.
/// It intentionally owns interchange only; shaping, bidi, layout, and rendering remain
/// in <see cref="TextLayoutEngine"/>. Unknown elements retain their textual content.
/// </summary>
public sealed class HtmlDocumentCodec : IRichDocumentFormatCodec
{
    private static readonly string[] Extensions = [".html", ".htm"];
    public static HtmlDocumentCodec Default { get; } = new();

    public string FormatId => "text/html";
    public IReadOnlyList<string> FileExtensions => Extensions;
    public bool CanImport => true;
    public bool CanExport => true;

    public RichDocument Import(ReadOnlySpan<byte> source, in RichDocumentImportContext context) =>
        ImportHtml(Encoding.UTF8.GetString(source), context);

    public byte[] Export(RichDocument document) => Encoding.UTF8.GetBytes(ExportHtml(document));

    private static RichDocument ImportHtml(string html, in RichDocumentImportContext context)
    {
        HtmlNode root = ParseHtml(html);
        var blocks = new List<Block>();
        AppendBlockNodes(root.Children, blocks, context, HtmlInlineStyle.Default(context));
        if (blocks.Count == 0) blocks.Add(new Paragraph { MarginBottom = 0f });
        var document = new RichDocument();
        document.ReplaceBlocks(blocks);
        return document;
    }

    private static void AppendBlockNodes(
        IReadOnlyList<HtmlNode> nodes,
        List<Block> blocks,
        in RichDocumentImportContext context,
        HtmlInlineStyle inherited)
    {
        Paragraph? implicitParagraph = null;
        void FlushImplicit()
        {
            if (implicitParagraph is null) return;
            if (implicitParagraph.Inlines.Count > 0) blocks.Add(implicitParagraph);
            implicitParagraph = null;
        }

        for (int index = 0; index < nodes.Count; index++)
        {
            HtmlNode node = nodes[index];
            if (node.Name == "#text" && string.IsNullOrWhiteSpace(node.Text)) continue;
            HtmlInlineStyle style = ApplyNodeStyle(inherited, node, context);
            if (IsParagraphElement(node.Name))
            {
                FlushImplicit();
                var paragraph = CreateParagraph(node, style);
                AppendInlineNodes(node.Children, paragraph.Inlines, context, style);
                blocks.Add(paragraph);
            }
            else if (node.Name is "ul" or "ol")
            {
                FlushImplicit();
                blocks.Add(CreateList(node, context, style));
            }
            else if (node.Name == "table")
            {
                FlushImplicit();
                blocks.Add(CreateTable(node, context, style));
            }
            else if (node.Name is "script" or "style" or "head" or "template")
            {
                continue;
            }
            else if (ContainsBlockElement(node))
            {
                FlushImplicit();
                AppendBlockNodes(node.Children, blocks, context, style);
            }
            else
            {
                implicitParagraph ??= CreateParagraph(node, style);
                AppendInlineNode(node, implicitParagraph.Inlines, context, style, applyOwnStyle: false);
            }
        }
        FlushImplicit();
    }

    private static Paragraph CreateParagraph(HtmlNode node, HtmlInlineStyle style)
    {
        var paragraph = new Paragraph { MarginBottom = 0f, FlowDirection = style.Direction };
        if (style.Alignment is { } alignment) paragraph.TextAlignment = alignment;
        string css = ReadAttribute(node.Attributes, "style") ?? string.Empty;
        if (TryReadCssLength(css, "margin-left", style.FontSize, out float left)) paragraph.LeftIndent = left;
        if (TryReadCssLength(css, "margin-right", style.FontSize, out float right)) paragraph.RightIndent = right;
        if (TryReadCssLength(css, "text-indent", style.FontSize, out float first)) paragraph.FirstLineIndent = first;
        if (TryReadCssLength(css, "margin-top", style.FontSize, out float before)) paragraph.SpaceBefore = before;
        if (TryReadCssLength(css, "margin-bottom", style.FontSize, out float after)) paragraph.MarginBottom = after;
        if (TryReadCssLength(css, "line-height", style.FontSize, out float lineHeight))
        {
            paragraph.LineSpacingRule = Microsoft.UI.Text.LineSpacingRule.Exactly;
            paragraph.LineSpacing = lineHeight;
        }
        return paragraph;
    }

    private static ListBlock CreateList(
        HtmlNode node,
        in RichDocumentImportContext context,
        HtmlInlineStyle inherited)
    {
        var list = new ListBlock { IsOrdered = node.Name == "ol" };
        string css = ReadAttribute(node.Attributes, "style") ?? string.Empty;
        if (TryReadCssLength(css, "margin-left", inherited.FontSize, out float indentation))
            list.Indentation = Math.Max(0f, indentation);
        foreach (HtmlNode child in node.Children)
        {
            if (child.Name != "li") continue;
            HtmlInlineStyle style = ApplyNodeStyle(inherited, child, context);
            var item = new ListItem();
            foreach (HtmlNode itemChild in child.Children)
            {
                if (itemChild.Name is "ul" or "ol")
                    item.Inlines.Add(CreateList(itemChild, context, ApplyNodeStyle(style, itemChild, context)));
                else
                    AppendInlineNode(itemChild, item.Inlines, context, style);
            }
            list.Items.Add(item);
        }
        return list;
    }

    private static Table CreateTable(
        HtmlNode node,
        in RichDocumentImportContext context,
        HtmlInlineStyle inherited)
    {
        var table = new Table { FlowDirection = inherited.Direction };
        string css = ReadAttribute(node.Attributes, "style") ?? string.Empty;
        if (TryReadCssLength(css, "border-width", inherited.FontSize, out float border))
            table.BorderThickness = Math.Max(0f, border);
        if (TryReadCssLength(css, "padding", inherited.FontSize, out float padding))
            table.CellPadding = Math.Max(0f, padding);
        if (TryReadCssColor(css, "border-color", out Brush? borderBrush)) table.BorderBrush = borderBrush;

        var rows = new List<HtmlNode>();
        CollectRows(node, rows);
        List<float>? widths = ReadColumnWidths(node, inherited.FontSize);
        bool collectCellWidths = widths is null;
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            HtmlNode rowNode = rows[rowIndex];
            var row = new TableRow();
            foreach (HtmlNode cellNode in rowNode.Children)
            {
                if (cellNode.Name is not ("td" or "th")) continue;
                HtmlInlineStyle style = ApplyNodeStyle(inherited, cellNode, context);
                if (cellNode.Name == "th") style = style with { Bold = true };
                var cell = new TableCell();
                if (int.TryParse(
                    ReadAttribute(cellNode.Attributes, "colspan"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int columnSpan))
                {
                    cell.ColumnSpan = columnSpan;
                }
                if (int.TryParse(
                    ReadAttribute(cellNode.Attributes, "rowspan"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int rowSpan))
                {
                    cell.RowSpan = rowSpan == 0
                        ? Math.Max(1, rows.Count - rowIndex)
                        : rowSpan;
                }
                string cellCss = ReadAttribute(cellNode.Attributes, "style") ?? string.Empty;
                if (TryReadCssColor(cellCss, "background-color", out Brush? background))
                    cell.Background = background;
                AppendInlineNodes(cellNode.Children, cell.Inlines, context, style);
                row.Cells.Add(cell);

                if (rowIndex == 0 && collectCellWidths)
                {
                    widths ??= new List<float>();
                    float logicalWidth = 0f;
                    if (TryReadHtmlLength(ReadAttribute(cellNode.Attributes, "width"), style.FontSize, out float width) ||
                        TryReadCssLength(cellCss, "width", style.FontSize, out width))
                        logicalWidth = Math.Max(1f, width) / cell.ColumnSpan;
                    for (int column = 0; column < cell.ColumnSpan; column++) widths.Add(logicalWidth);
                }
            }
            if (row.Cells.Count > 0) table.Rows.Add(row);
        }
        if (widths is { Count: > 0 })
        {
            bool hasWidth = false;
            for (int index = 0; index < widths.Count; index++) hasWidth |= widths[index] > 0f;
            if (hasWidth)
            {
                for (int index = 0; index < widths.Count; index++)
                    if (widths[index] <= 0f) widths[index] = 80f;
                table.ColumnWidths = widths;
            }
        }
        return table;
    }

    private static List<float>? ReadColumnWidths(HtmlNode table, float emSize)
    {
        foreach (HtmlNode child in table.Children)
        {
            if (child.Name != "colgroup") continue;
            var widths = new List<float>();
            foreach (HtmlNode column in child.Children)
            {
                if (column.Name != "col") continue;
                string css = ReadAttribute(column.Attributes, "style") ?? string.Empty;
                if (TryReadHtmlLength(ReadAttribute(column.Attributes, "width"), emSize, out float width) ||
                    TryReadCssLength(css, "width", emSize, out width))
                    widths.Add(Math.Max(1f, width));
            }
            if (widths.Count > 0) return widths;
        }
        return null;
    }

    private static void CollectRows(HtmlNode node, List<HtmlNode> rows)
    {
        foreach (HtmlNode child in node.Children)
        {
            if (child.Name == "tr") rows.Add(child);
            else if (child.Name is "thead" or "tbody" or "tfoot") CollectRows(child, rows);
        }
    }

    private static void AppendInlineNodes(
        IReadOnlyList<HtmlNode> nodes,
        RichElementCollection<Inline> destination,
        in RichDocumentImportContext context,
        HtmlInlineStyle inherited)
    {
        for (int index = 0; index < nodes.Count; index++)
            AppendInlineNode(nodes[index], destination, context, inherited);
    }

    private static void AppendInlineNode(
        HtmlNode node,
        RichElementCollection<Inline> destination,
        in RichDocumentImportContext context,
        HtmlInlineStyle inherited,
        bool applyOwnStyle = true)
    {
        if (node.Name == "#text")
        {
            string text = WebUtility.HtmlDecode(node.Text ?? string.Empty);
            text = inherited.PreserveWhitespace ? text : CollapseHtmlWhitespace(text);
            if (text.Length == 0) return;
            var retainedStyle = new RichTextStyle(
                inherited.Foreground,
                inherited.FontSize,
                inherited.Code ? context.CodeFont : context.DefaultFont,
                inherited.Bold,
                inherited.Italic,
                inherited.Underline,
                inherited.Link,
                inherited.Background,
                inherited.Strikethrough,
                LanguageTag: inherited.Language,
                IsSubscript: inherited.Subscript,
                IsSuperscript: inherited.Superscript,
                FlowDirection: inherited.Direction);
            var run = new Run(text)
            {
                Foreground = inherited.Foreground,
                FontSize = inherited.FontSize,
                Font = inherited.Code ? context.CodeFont : context.DefaultFont,
                RetainedStyle = retainedStyle
            };
            run.ApplyRetainedFlowDirection(inherited.Direction);
            destination.Add(run);
            return;
        }
        if (node.Name is "script" or "style" or "template") return;
        if (node.Name == "br")
        {
            destination.Add(new LineBreak());
            return;
        }
        if (node.Name == "img")
        {
            if (TryCreateEmbeddedImage(node, inherited.FontSize, out InlineUIContainer? image))
                destination.Add(image!);
            else
            {
                string alternateText = ReadAttribute(node.Attributes, "alt") ?? string.Empty;
                if (alternateText.Length > 0)
                    AppendInlineNode(HtmlNode.TextNode(alternateText), destination, context, inherited);
            }
            return;
        }
        if (node.Name is "ul" or "ol")
        {
            destination.Add(CreateList(node, context, ApplyNodeStyle(inherited, node, context)));
            return;
        }
        if (node.Name == "table")
        {
            destination.Add(CreateTable(node, context, ApplyNodeStyle(inherited, node, context)));
            return;
        }

        HtmlInlineStyle style = applyOwnStyle ? ApplyNodeStyle(inherited, node, context) : inherited;
        Span? container = node.Name switch
        {
            "a" => new Hyperlink { Uri = style.Link ?? string.Empty },
            "b" or "strong" => new Bold(),
            "i" or "em" => new Italic(),
            "u" or "ins" => new Underline(),
            _ => null
        };
        RichElementCollection<Inline> target = container?.Inlines ?? destination;
        AppendInlineNodes(node.Children, target, context, style);
        if (container is not null && container.Inlines.Count > 0) destination.Add(container);
    }

    private static bool TryCreateEmbeddedImage(
        HtmlNode node,
        float emSize,
        out InlineUIContainer? container)
    {
        container = null;
        string? source = ReadAttribute(node.Attributes, "src");
        if (string.IsNullOrWhiteSpace(source) || !source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return false;
        int comma = source.IndexOf(',');
        if (comma < 0 || !source.AsSpan(0, comma).Contains(";base64", StringComparison.OrdinalIgnoreCase)) return false;
        byte[] data;
        try
        {
            data = Convert.FromBase64String(source[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }
        const int maximumImageBytes = 128 * 1024 * 1024;
        if (data.Length == 0 || data.Length > maximumImageBytes) return false;
        _ = TryReadHtmlLength(ReadAttribute(node.Attributes, "width"), emSize, out float widthValue);
        _ = TryReadHtmlLength(ReadAttribute(node.Attributes, "height"), emSize, out float heightValue);
        int width = Math.Max(0, (int)MathF.Round(widthValue));
        int height = Math.Max(0, (int)MathF.Round(heightValue));
        var embedded = new RichTextEmbeddedObject(
            width,
            height,
            height,
            Microsoft.UI.Text.VerticalCharacterAlignment.Baseline,
            ReadAttribute(node.Attributes, "alt"),
            data);
        if (embedded.ImageSource is null) return false;
        container = new InlineUIContainer(new Image
        {
            Source = embedded.ImageSource,
            Width = width > 0 ? width : float.NaN,
            Height = height > 0 ? height : float.NaN
        })
        {
            RetainedEmbeddedObject = embedded
        };
        return true;
    }

    private static string CollapseHtmlWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool inWhitespace = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (!inWhitespace) builder.Append(' ');
                inWhitespace = true;
            }
            else
            {
                System.Span<char> buffer = stackalloc char[2];
                int written = rune.EncodeToUtf16(buffer);
                builder.Append(buffer[..written]);
                inWhitespace = false;
            }
        }
        return builder.ToString();
    }

    private static HtmlInlineStyle ApplyNodeStyle(
        HtmlInlineStyle inherited,
        HtmlNode node,
        in RichDocumentImportContext context)
    {
        HtmlInlineStyle style = inherited;
        string tag = node.Name;
        if (tag is "b" or "strong" or "th" || tag.StartsWith('h')) style = style with { Bold = true };
        if (tag is "i" or "em") style = style with { Italic = true };
        if (tag is "u" or "ins") style = style with { Underline = true };
        if (tag is "s" or "strike" or "del") style = style with { Strikethrough = true };
        if (tag is "code" or "pre" or "kbd" or "samp") style = style with { Code = true };
        if (tag == "pre") style = style with { PreserveWhitespace = true };
        if (tag == "sub") style = style with { Subscript = true, Superscript = false };
        if (tag == "sup") style = style with { Superscript = true, Subscript = false };
        if (tag == "a") style = style with { Link = ReadAttribute(node.Attributes, "href") };
        if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
        {
            int level = tag[1] - '0';
            float scale = level switch { 1 => 2f, 2 => 1.5f, 3 => 1.25f, 4 => 1f, 5 => 0.875f, _ => 0.75f };
            style = style with { FontSize = context.DefaultFontSize * scale };
        }

        string? language = ReadAttribute(node.Attributes, "lang") ?? ReadAttribute(node.Attributes, "xml:lang");
        if (!string.IsNullOrWhiteSpace(language)) style = style with { Language = language };
        string? direction = ReadAttribute(node.Attributes, "dir");
        if (direction?.Equals("rtl", StringComparison.OrdinalIgnoreCase) == true)
            style = style with { Direction = FlowDirection.RightToLeft };
        else if (direction?.Equals("ltr", StringComparison.OrdinalIgnoreCase) == true)
            style = style with { Direction = FlowDirection.LeftToRight };
        string? align = ReadAttribute(node.Attributes, "align");
        if (TryParseAlignment(align, out TextAlignment alignment)) style = style with { Alignment = alignment };

        string css = ReadAttribute(node.Attributes, "style") ?? string.Empty;
        if (TryReadCssLength(css, "font-size", inherited.FontSize, out float fontSize)) style = style with { FontSize = fontSize };
        if (TryReadCssColor(css, "color", out Brush? foreground)) style = style with { Foreground = foreground };
        if (TryReadCssColor(css, "background-color", out Brush? background)) style = style with { Background = background };
        string? weight = ReadCssProperty(css, "font-weight");
        if (weight is not null && (weight.Equals("bold", StringComparison.OrdinalIgnoreCase) ||
            int.TryParse(weight, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericWeight) && numericWeight >= 600))
            style = style with { Bold = true };
        string? fontStyle = ReadCssProperty(css, "font-style");
        if (fontStyle is not null && !fontStyle.Equals("normal", StringComparison.OrdinalIgnoreCase)) style = style with { Italic = true };
        string? decoration = ReadCssProperty(css, "text-decoration") ?? ReadCssProperty(css, "text-decoration-line");
        if (decoration is not null)
        {
            if (decoration.Contains("underline", StringComparison.OrdinalIgnoreCase)) style = style with { Underline = true };
            if (decoration.Contains("line-through", StringComparison.OrdinalIgnoreCase)) style = style with { Strikethrough = true };
        }
        string? cssDirection = ReadCssProperty(css, "direction");
        if (cssDirection?.Equals("rtl", StringComparison.OrdinalIgnoreCase) == true) style = style with { Direction = FlowDirection.RightToLeft };
        else if (cssDirection?.Equals("ltr", StringComparison.OrdinalIgnoreCase) == true) style = style with { Direction = FlowDirection.LeftToRight };
        if (TryParseAlignment(ReadCssProperty(css, "text-align"), out alignment)) style = style with { Alignment = alignment };
        return style;
    }

    private static bool TryParseAlignment(string? value, out TextAlignment alignment)
    {
        alignment = value?.Trim().ToLowerInvariant() switch
        {
            "center" => TextAlignment.Center,
            "right" or "end" => TextAlignment.Right,
            "justify" => TextAlignment.Justify,
            "left" or "start" => TextAlignment.Left,
            _ => (TextAlignment)(-1)
        };
        return (int)alignment >= 0;
    }

    private static bool IsParagraphElement(string name) =>
        name is "p" or "address" or "blockquote" or "pre" or
        "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

    private static bool ContainsBlockElement(HtmlNode node)
    {
        if (node.Name is "div" or "section" or "article" or "main" or "header" or "footer" or "nav" or "aside")
            return true;
        for (int index = 0; index < node.Children.Count; index++)
        {
            string name = node.Children[index].Name;
            if (IsParagraphElement(name) || name is "ul" or "ol" or "table" || ContainsBlockElement(node.Children[index]))
                return true;
        }
        return false;
    }

    private static HtmlNode ParseHtml(string html)
    {
        var root = new HtmlNode("#document", string.Empty);
        var stack = new Stack<HtmlNode>();
        stack.Push(root);
        int cursor = 0;
        while (cursor < html.Length)
        {
            int tagStart = html.IndexOf('<', cursor);
            if (tagStart < 0)
            {
                if (cursor < html.Length) stack.Peek().Children.Add(HtmlNode.TextNode(html[cursor..]));
                break;
            }
            if (tagStart > cursor) stack.Peek().Children.Add(HtmlNode.TextNode(html[cursor..tagStart]));
            if (html.AsSpan(tagStart).StartsWith("<!--", StringComparison.Ordinal))
            {
                int commentEnd = html.IndexOf("-->", tagStart + 4, StringComparison.Ordinal);
                cursor = commentEnd < 0 ? html.Length : commentEnd + 3;
                continue;
            }
            int tagEnd = FindTagEnd(html, tagStart + 1);
            if (tagEnd < 0)
            {
                stack.Peek().Children.Add(HtmlNode.TextNode(html[tagStart..]));
                break;
            }
            ReadOnlySpan<char> token = html.AsSpan(tagStart + 1, tagEnd - tagStart - 1).Trim();
            cursor = tagEnd + 1;
            if (token.Length == 0 || token[0] is '!' or '?') continue;
            bool closing = token[0] == '/';
            if (closing) token = token[1..].TrimStart();
            bool selfClosing = token.Length > 0 && token[^1] == '/';
            if (selfClosing) token = token[..^1].TrimEnd();
            int nameLength = 0;
            while (nameLength < token.Length && (char.IsLetterOrDigit(token[nameLength]) || token[nameLength] is ':' or '-')) nameLength++;
            if (nameLength == 0) continue;
            string name = token[..nameLength].ToString().ToLowerInvariant();
            if (closing)
            {
                while (stack.Count > 1)
                {
                    HtmlNode closed = stack.Pop();
                    if (closed.Name == name) break;
                }
                continue;
            }
            var node = new HtmlNode(name, token[nameLength..].ToString());
            stack.Peek().Children.Add(node);
            if (!selfClosing && !IsVoidElement(name)) stack.Push(node);
        }
        return root;
    }

    private static int FindTagEnd(string html, int start)
    {
        char quote = '\0';
        for (int index = start; index < html.Length; index++)
        {
            char value = html[index];
            if (quote != '\0')
            {
                if (value == quote) quote = '\0';
            }
            else if (value is '\'' or '"') quote = value;
            else if (value == '>') return index;
        }
        return -1;
    }

    private static bool IsVoidElement(string name) =>
        name is "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or
        "input" or "link" or "meta" or "param" or "source" or "track" or "wbr";

    private sealed class HtmlNode
    {
        public HtmlNode(string name, string attributes)
        {
            Name = name;
            Attributes = attributes;
        }
        public string Name { get; }
        public string Attributes { get; }
        public string? Text { get; private init; }
        public List<HtmlNode> Children { get; } = new();
        public static HtmlNode TextNode(string text) => new("#text", string.Empty) { Text = text };
    }

    private static string ExportHtml(RichDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder("<div class=\"progpu-rich-document\">");
        for (int index = 0; index < document.Blocks.Count; index++) AppendBlock(document.Blocks[index], builder);
        return builder.Append("</div>").ToString();
    }

    private static void AppendBlock(Block block, StringBuilder builder)
    {
        switch (block)
        {
            case Paragraph paragraph:
                AppendParagraphStart(paragraph, builder);
                AppendInlines(paragraph.Inlines, builder);
                builder.Append("</p>");
                break;
            case ListBlock list:
                builder.Append(list.IsOrdered ? "<ol>" : "<ul>");
                foreach (ListItem item in list.Items)
                {
                    builder.Append("<li>");
                    AppendInlines(item.Inlines, builder);
                    builder.Append("</li>");
                }
                builder.Append(list.IsOrdered ? "</ol>" : "</ul>");
                break;
            case Table table:
                builder.Append("<table");
                if (table.FlowDirection is { } tableDirection)
                    builder.Append(" dir=\"").Append(tableDirection == FlowDirection.RightToLeft ? "rtl" : "ltr").Append('"');
                var tableCss = new StringBuilder();
                if (table.BorderThickness != 0f) AppendCss(tableCss, "border-width", FormatPixels(table.BorderThickness));
                if (table.BorderBrush is SolidColorBrush borderBrush)
                {
                    AppendCss(tableCss, "border-style", "solid");
                    AppendCss(tableCss, "border-color", FormatColor(borderBrush));
                }
                if (table.CellPadding != 0f) AppendCss(tableCss, "padding", FormatPixels(table.CellPadding));
                if (tableCss.Length > 0) builder.Append(" style=\"").Append(tableCss).Append('"');
                builder.Append('>');
                if (table.ColumnWidths is { Count: > 0 } widths)
                {
                    builder.Append("<colgroup>");
                    for (int index = 0; index < widths.Count; index++)
                        builder.Append("<col style=\"width:").Append(FormatPixels(widths[index])).Append("\">");
                    builder.Append("</colgroup>");
                }
                foreach (TableRow row in table.Rows)
                {
                    builder.Append("<tr>");
                    foreach (TableCell cell in row.Cells)
                    {
                        builder.Append("<td");
                        if (cell.ColumnSpan > 1)
                            builder.Append(" colspan=\"").Append(cell.ColumnSpan).Append('"');
                        if (cell.RowSpan > 1)
                            builder.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');
                        if (cell.Background is SolidColorBrush cellBackground)
                            builder.Append(" style=\"background-color:").Append(FormatColor(cellBackground)).Append('"');
                        builder.Append('>');
                        AppendInlines(cell.Inlines, builder);
                        builder.Append("</td>");
                    }
                    builder.Append("</tr>");
                }
                builder.Append("</table>");
                break;
            case Inline inline:
                builder.Append("<p>");
                AppendInline(inline, builder);
                builder.Append("</p>");
                break;
        }
    }

    private static void AppendInlines(IEnumerable<Inline> inlines, StringBuilder builder)
    {
        foreach (Inline inline in inlines) AppendInline(inline, builder);
    }

    private static void AppendInline(Inline inline, StringBuilder builder)
    {
        string? tag = inline switch
        {
            Bold => "strong",
            Italic => "em",
            Underline => "u",
            Hyperlink => "a",
            _ => null
        };
        if (tag is not null)
        {
            builder.Append('<').Append(tag);
            if (inline is Hyperlink link)
                builder.Append(" href=\"").Append(WebUtility.HtmlEncode(link.Uri)).Append('"');
            builder.Append('>');
        }
        switch (inline)
        {
            case Run run: AppendRun(run, builder); break;
            case LineBreak: builder.Append("<br>"); break;
            case Table table: AppendBlock(table, builder); break;
            case Span span: AppendInlines(span.Inlines, builder); break;
            case InlineUIContainer container: AppendEmbeddedObject(container, builder); break;
        }
        if (tag is not null) builder.Append("</").Append(tag).Append('>');
    }

    private static void AppendEmbeddedObject(InlineUIContainer container, StringBuilder builder)
    {
        if (container.RetainedEmbeddedObject is not { } embedded || embedded.Data.IsEmpty)
        {
            builder.Append("<span aria-label=\"embedded object\">&#xfffc;</span>");
            return;
        }
        string mime = GetImageMimeType(embedded.Data.Span);
        builder.Append("<img src=\"data:").Append(mime).Append(";base64,")
            .Append(Convert.ToBase64String(embedded.Data.Span)).Append('"');
        if (embedded.Width > 0) builder.Append(" width=\"").Append(embedded.Width).Append('"');
        if (embedded.Height > 0) builder.Append(" height=\"").Append(embedded.Height).Append('"');
        if (!string.IsNullOrEmpty(embedded.AlternateText))
            builder.Append(" alt=\"").Append(WebUtility.HtmlEncode(embedded.AlternateText)).Append('"');
        builder.Append('>');
    }

    private static string GetImageMimeType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M') return "image/bmp";
        return "application/octet-stream";
    }

    private static void AppendParagraphStart(Paragraph paragraph, StringBuilder builder)
    {
        builder.Append("<p");
        if (paragraph.FlowDirection is { } direction)
            builder.Append(" dir=\"").Append(direction == FlowDirection.RightToLeft ? "rtl" : "ltr").Append('"');
        var css = new StringBuilder();
        if (paragraph.HasExplicitTextAlignment)
        {
            string alignment = paragraph.TextAlignment switch
            {
                TextAlignment.Center => "center",
                TextAlignment.Right => "right",
                TextAlignment.Justify => "justify",
                _ => "left"
            };
            AppendCss(css, "text-align", alignment);
        }
        if (paragraph.LeftIndent != 0f) AppendCss(css, "margin-left", FormatPixels(paragraph.LeftIndent));
        if (paragraph.RightIndent != 0f) AppendCss(css, "margin-right", FormatPixels(paragraph.RightIndent));
        if (paragraph.FirstLineIndent != 0f) AppendCss(css, "text-indent", FormatPixels(paragraph.FirstLineIndent));
        if (paragraph.SpaceBefore != 0f) AppendCss(css, "margin-top", FormatPixels(paragraph.SpaceBefore));
        if (paragraph.MarginBottom != 0f) AppendCss(css, "margin-bottom", FormatPixels(paragraph.MarginBottom));
        if (paragraph.LineSpacingRule == Microsoft.UI.Text.LineSpacingRule.Exactly && paragraph.LineSpacing > 0f)
            AppendCss(css, "line-height", FormatPixels(paragraph.LineSpacing));
        if (css.Length > 0) builder.Append(" style=\"").Append(css).Append('"');
        builder.Append('>');
    }

    private static void AppendRun(Run run, StringBuilder builder)
    {
        if (run.RetainedStyle is not { } style && !run.HasExplicitFlowDirection)
        {
            builder.Append(WebUtility.HtmlEncode(run.Text));
            return;
        }
        style = run.RetainedStyle ?? default;
        var css = new StringBuilder();
        if (style.IsBold) AppendCss(css, "font-weight", "700");
        if (style.IsItalic) AppendCss(css, "font-style", "italic");
        if (style.IsUnderline || style.UnderlineType is not (Microsoft.UI.Text.UnderlineType.None or Microsoft.UI.Text.UnderlineType.Undefined))
            AppendCss(css, "text-decoration", style.IsStrikethrough ? "underline line-through" : "underline");
        else if (style.IsStrikethrough) AppendCss(css, "text-decoration", "line-through");
        if (style.IsHidden) AppendCss(css, "display", "none");
        if (style.IsAllCaps) AppendCss(css, "text-transform", "uppercase");
        if (style.IsSmallCaps) AppendCss(css, "font-variant", "small-caps");
        if (style.IsSubscript) AppendCss(css, "vertical-align", "sub");
        else if (style.IsSuperscript) AppendCss(css, "vertical-align", "super");
        else if (style.BaselineOffset != 0f) AppendCss(css, "vertical-align", FormatPixels(style.BaselineOffset));
        if (style.CharacterSpacing != 0f) AppendCss(css, "letter-spacing", FormatPixels(style.CharacterSpacing));
        if (style.FontSize > 0f && float.IsFinite(style.FontSize)) AppendCss(css, "font-size", FormatPixels(style.FontSize));
        if (!string.IsNullOrWhiteSpace(style.FontName)) AppendCss(css, "font-family", WebUtility.HtmlEncode(style.FontName));
        if (style.Foreground is SolidColorBrush foreground) AppendCss(css, "color", FormatColor(foreground));
        if (style.Background is SolidColorBrush background) AppendCss(css, "background-color", FormatColor(background));

        FlowDirection? direction = run.HasExplicitFlowDirection
            ? run.FlowDirection
            : style.FlowDirection;
        bool hasSpan = css.Length > 0 || !string.IsNullOrWhiteSpace(style.LanguageTag) || direction.HasValue;
        if (hasSpan)
        {
            builder.Append("<span");
            if (direction is { } inlineDirection)
                builder.Append(" dir=\"").Append(inlineDirection == FlowDirection.RightToLeft ? "rtl" : "ltr").Append('"');
            if (!string.IsNullOrWhiteSpace(style.LanguageTag))
                builder.Append(" lang=\"").Append(WebUtility.HtmlEncode(style.LanguageTag)).Append('"');
            if (css.Length > 0) builder.Append(" style=\"").Append(css).Append('"');
            builder.Append('>');
        }
        builder.Append(WebUtility.HtmlEncode(run.Text));
        if (hasSpan) builder.Append("</span>");
    }

    private static void AppendCss(StringBuilder builder, string name, string value)
    {
        if (builder.Length > 0) builder.Append(';');
        builder.Append(name).Append(':').Append(value);
    }

    private static string FormatPixels(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture) + "px";

    private static string FormatColor(SolidColorBrush brush)
    {
        int red = Math.Clamp((int)MathF.Round(brush.Color.X * 255f), 0, 255);
        int green = Math.Clamp((int)MathF.Round(brush.Color.Y * 255f), 0, 255);
        int blue = Math.Clamp((int)MathF.Round(brush.Color.Z * 255f), 0, 255);
        int alpha = Math.Clamp((int)MathF.Round(brush.Color.W * brush.Opacity * 255f), 0, 255);
        return alpha == 255
            ? $"#{red:X2}{green:X2}{blue:X2}"
            : $"#{alpha:X2}{red:X2}{green:X2}{blue:X2}";
    }

    private static string? ReadAttribute(string attributes, string name)
    {
        int index = attributes.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            int afterName = index + name.Length;
            if ((index == 0 || char.IsWhiteSpace(attributes[index - 1])) &&
                afterName < attributes.Length)
            {
                int equals = afterName;
                while (equals < attributes.Length && char.IsWhiteSpace(attributes[equals])) equals++;
                if (equals < attributes.Length && attributes[equals] == '=')
                {
                    equals++;
                    while (equals < attributes.Length && char.IsWhiteSpace(attributes[equals])) equals++;
                    if (equals >= attributes.Length) return string.Empty;
                    char quote = attributes[equals];
                    if (quote is '\'' or '"')
                    {
                        int end = attributes.IndexOf(quote, equals + 1);
                        return WebUtility.HtmlDecode(end < 0 ? attributes[(equals + 1)..] : attributes[(equals + 1)..end]);
                    }
                    int unquotedEnd = equals;
                    while (unquotedEnd < attributes.Length && !char.IsWhiteSpace(attributes[unquotedEnd])) unquotedEnd++;
                    return WebUtility.HtmlDecode(attributes[equals..unquotedEnd]);
                }
            }
            index = attributes.IndexOf(name, afterName, StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    private static string? ReadCssProperty(string css, string name)
    {
        int start = 0;
        while (start < css.Length)
        {
            int end = css.IndexOf(';', start);
            if (end < 0) end = css.Length;
            ReadOnlySpan<char> declaration = css.AsSpan(start, end - start).Trim();
            int colon = declaration.IndexOf(':');
            if (colon > 0 && declaration[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return declaration[(colon + 1)..].Trim().ToString();
            start = end + 1;
        }
        return null;
    }

    private static bool TryReadCssLength(string css, string name, float emSize, out float value) =>
        TryReadHtmlLength(ReadCssProperty(css, name), emSize, out value);

    private static bool TryReadHtmlLength(string? text, float emSize, out float value)
    {
        value = 0f;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        float scale = 1f;
        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase)) text = text[..^2].TrimEnd();
        else if (text.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2].TrimEnd();
            scale = 4f / 3f;
        }
        else if (text.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^3].TrimEnd();
            scale = emSize;
        }
        else if (text.EndsWith("em", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2].TrimEnd();
            scale = emSize;
        }
        else if (text.EndsWith('%'))
        {
            text = text[..^1].TrimEnd();
            scale = emSize / 100f;
        }
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float scalar) || !float.IsFinite(scalar))
            return false;
        value = scalar * scale;
        return true;
    }

    private static bool TryReadCssColor(string css, string name, out Brush? brush)
    {
        brush = null;
        string? value = ReadCssProperty(css, name);
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        uint rgba;
        if (value[0] == '#')
        {
            ReadOnlySpan<char> hex = value.AsSpan(1);
            if (hex.Length == 3 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint shortRgb))
            {
                uint red = (shortRgb >> 8) & 0xF;
                uint green = (shortRgb >> 4) & 0xF;
                uint blue = shortRgb & 0xF;
                rgba = ((red * 17) << 24) | ((green * 17) << 16) | ((blue * 17) << 8) | 0xFF;
            }
            else if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
                rgba = (rgb << 8) | 0xFF;
            else if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
                rgba = ((argb & 0x00FFFFFF) << 8) | (argb >> 24);
            else
                return false;
        }
        else
        {
            rgba = value.ToLowerInvariant() switch
            {
                "black" => 0x000000FF,
                "white" => 0xFFFFFFFF,
                "red" => 0xFF0000FF,
                "green" => 0x008000FF,
                "blue" => 0x0000FFFF,
                "yellow" => 0xFFFF00FF,
                "gray" or "grey" => 0x808080FF,
                "transparent" => 0x00000000,
                _ => uint.MaxValue
            };
            if (rgba == uint.MaxValue && !value.Equals("white", StringComparison.OrdinalIgnoreCase)) return false;
        }
        brush = new SolidColorBrush(rgba);
        return true;
    }

    private readonly record struct HtmlInlineStyle(
        bool Bold,
        bool Italic,
        bool Underline,
        string? Link,
        bool Code,
        bool Strikethrough,
        bool Subscript,
        bool Superscript,
        bool PreserveWhitespace,
        float FontSize,
        Brush? Foreground,
        Brush? Background,
        string? Language,
        FlowDirection? Direction,
        TextAlignment? Alignment)
    {
        public static HtmlInlineStyle Default(in RichDocumentImportContext context) => new(
            false,
            false,
            false,
            null,
            false,
            false,
            false,
            false,
            false,
            context.DefaultFontSize,
            context.DefaultForeground,
            null,
            null,
            null,
            null);
    }
}
