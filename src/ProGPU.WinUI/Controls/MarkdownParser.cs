using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Layout;

using Block = Microsoft.UI.Xaml.Documents.Block;
using Inline = Microsoft.UI.Xaml.Documents.Inline;
using ListBlock = Microsoft.UI.Xaml.Documents.ListBlock;
using Table = Microsoft.UI.Xaml.Documents.Table;
using TableRow = Microsoft.UI.Xaml.Documents.TableRow;
using TableCell = Microsoft.UI.Xaml.Documents.TableCell;

namespace Microsoft.UI.Xaml.Controls
{
    public static class MarkdownParser
    {
        private static readonly Lazy<MarkdownPipeline> s_pipeline = new(
            static () => new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .Build(),
            LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<Task> s_warmUpTask = new(
            static () => Task.Run(static () =>
            {
                _ = Markdown.Parse("# ProGPU", s_pipeline.Value);
            }),
            LazyThreadSafetyMode.ExecutionAndPublication);

        public static Func<string, string, FrameworkElement>? CodeBlockFactory { get; set; }

        public static void WarmUp()
        {
            _ = s_warmUpTask.Value;
        }

        public static List<Block> Parse(string markdownText, Brush defaultFg, float baseFontSize, TtfFont defaultFont, TtfFont codeFont, ElementTheme theme)
        {
            var blocks = new List<Block>();
            if (string.IsNullOrEmpty(markdownText))
            {
                return blocks;
            }

            try
            {
                var document = Markdown.Parse(markdownText, s_pipeline.Value);

                foreach (var blockNode in document)
                {
                    var block = ConvertBlock(blockNode, defaultFg, baseFontSize, defaultFont, codeFont, theme);
                    if (block != null)
                    {
                        blocks.Add(block);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MarkdownParser] Error parsing markdown: {ex.Message}");
                var fallbackPara = new Paragraph(new Run(markdownText) { Foreground = defaultFg, FontSize = baseFontSize });
                blocks.Add(fallbackPara);
            }

            return blocks;
        }

        private static Block? ConvertBlock(object blockNode, Brush defaultFg, float baseFontSize, TtfFont defaultFont, TtfFont codeFont, ElementTheme theme)
        {
            if (blockNode is ParagraphBlock paragraphBlock)
            {
                var para = new Paragraph { MarginBottom = 12f };
                if (paragraphBlock.Inline != null)
                {
                    foreach (var inlineNode in paragraphBlock.Inline)
                    {
                        var inline = ConvertInline(inlineNode, defaultFg, baseFontSize, defaultFont, codeFont, theme);
                        if (inline != null)
                        {
                            para.Inlines.Add(inline);
                        }
                    }
                }
                return para;
            }
            else if (blockNode is HeadingBlock headingBlock)
            {
                var para = new Paragraph { MarginBottom = 16f };
                float sizeMultiplier = headingBlock.Level switch
                {
                    1 => 2.0f,
                    2 => 1.6f,
                    3 => 1.3f,
                    4 => 1.15f,
                    _ => 1.0f
                };

                float headingSize = baseFontSize * sizeMultiplier;

                var headingSpan = new Bold();
                headingSpan.FontSize = headingSize;

                if (headingBlock.Inline != null)
                {
                    foreach (var inlineNode in headingBlock.Inline)
                    {
                        var inline = ConvertInline(inlineNode, defaultFg, headingSize, defaultFont, codeFont, theme);
                        if (inline != null)
                        {
                            headingSpan.Inlines.Add(inline);
                        }
                    }
                }

                para.Inlines.Add(headingSpan);
                return para;
            }
            else if (blockNode is Markdig.Syntax.ListBlock listBlock)
            {
                var xamlList = new ListBlock
                {
                    IsOrdered = listBlock.IsOrdered,
                    Indentation = 20f,
                    MarginBottom = 12f
                };

                foreach (var itemNode in listBlock)
                {
                    if (itemNode is ListItemBlock listItemBlock)
                    {
                        var listItem = new ListItem();
                        foreach (var subBlockNode in listItemBlock)
                        {
                            var subBlock = ConvertBlock(subBlockNode, defaultFg, baseFontSize, defaultFont, codeFont, theme);
                            if (subBlock is Paragraph p)
                            {
                                foreach (var inline in p.Inlines)
                                {
                                    listItem.Inlines.Add(inline);
                                }
                            }
                            else if (subBlock is Inline inlineBlock)
                            {
                                listItem.Inlines.Add(inlineBlock);
                            }
                        }
                        xamlList.Items.Add(listItem);
                    }
                }

                return xamlList;
            }
            else if (blockNode is ThematicBreakBlock)
            {
                var border = new Border
                {
                    HeightConstraint = 1f,
                    Background = ThemeManager.GetBrush("ControlBorder", theme),
                    Margin = new Thickness(0, 12, 0, 12),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                
                var uic = new InlineUIContainer(border);
                var para = new Paragraph(uic) { MarginBottom = 12f };
                return para;
            }
            else if (blockNode is CodeBlock codeBlock)
            {
                var sb = new StringBuilder();
                if (codeBlock.Lines.Lines != null)
                {
                    for (int k = 0; k < codeBlock.Lines.Count; k++)
                    {
                        sb.AppendLine(codeBlock.Lines.Lines[k].ToString());
                    }
                }
                var textLines = sb.ToString().TrimEnd();

                string language = "";
                if (codeBlock is FencedCodeBlock fencedCodeBlock && fencedCodeBlock.Info != null)
                {
                    language = fencedCodeBlock.Info;
                }

                FrameworkElement element;
                if (CodeBlockFactory != null)
                {
                    element = CodeBlockFactory(textLines, language);
                }
                else
                {
                    var border = new Border
                    {
                        Background = ThemeManager.GetBrush("ControlBackground", theme),
                        BorderBrush = ThemeManager.GetBrush("ControlBorder", theme),
                        BorderThickness = new Thickness(1f),
                        CornerRadius = 6f,
                        Padding = new Thickness(10f),
                        Margin = new Thickness(0, 4, 0, 12),
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };

                    var richText = new RichTextBlock
                    {
                        FontSize = baseFontSize * 0.9f,
                        Foreground = ThemeManager.GetBrush("TextSecondary", theme),
                        Font = codeFont
                    };

                    richText.Inlines.Add(new Run(textLines));
                    border.Child = richText;
                    element = border;
                }

                var uic = new InlineUIContainer(element);
                return new Paragraph(uic) { MarginBottom = 12f };
            }
            else if (blockNode is Markdig.Extensions.Tables.Table tableBlock)
            {
                var xamlTable = new Table
                {
                    CellPadding = 6f,
                    BorderThickness = 1f,
                    BorderBrush = ThemeManager.GetBrush("ControlBorder", theme),
                    MarginBottom = 14f
                };

                foreach (var rowNode in tableBlock)
                {
                    if (rowNode is Markdig.Extensions.Tables.TableRow tableRowBlock)
                    {
                        var xamlRow = new TableRow();
                        bool isHeader = tableRowBlock.IsHeader;

                        foreach (var cellNode in tableRowBlock)
                        {
                            if (cellNode is Markdig.Extensions.Tables.TableCell tableCellBlock)
                            {
                                var xamlCell = new TableCell();
                                if (isHeader)
                                {
                                    xamlCell.Background = ThemeManager.GetBrush("ControlBackgroundHover", theme);
                                }

                                foreach (var subBlockNode in tableCellBlock)
                                {
                                    var subBlock = ConvertBlock(subBlockNode, defaultFg, baseFontSize, defaultFont, codeFont, theme);
                                    if (subBlock is Paragraph p)
                                    {
                                        foreach (var inline in p.Inlines)
                                        {
                                            if (isHeader)
                                            {
                                                xamlCell.Inlines.Add(new Bold(inline));
                                            }
                                            else
                                            {
                                                xamlCell.Inlines.Add(inline);
                                            }
                                        }
                                    }
                                }
                                xamlRow.Cells.Add(xamlCell);
                            }
                        }
                        xamlTable.Rows.Add(xamlRow);
                    }
                }

                return xamlTable;
            }
            else if (blockNode is QuoteBlock quoteBlock)
            {
                var border = new Border
                {
                    BorderBrush = ThemeManager.GetBrush("SystemAccentColor", theme),
                    BorderThickness = new Thickness(4f, 0, 0, 0),
                    Padding = new Thickness(12f, 4f, 4f, 4f),
                    Margin = new Thickness(0, 4, 0, 12),
                    Background = ThemeManager.GetBrush("ControlBackground", theme),
                    CornerRadius = 2f,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                var quoteStack = new StackPanel { Orientation = Orientation.Vertical };
                border.Child = quoteStack;

                foreach (var subBlockNode in quoteBlock)
                {
                    var subBlock = ConvertBlock(subBlockNode, defaultFg, baseFontSize, defaultFont, codeFont, theme);
                    if (subBlock is Paragraph p)
                    {
                        var quoteText = new RichTextBlock
                        {
                            FontSize = baseFontSize,
                            Foreground = defaultFg,
                            Margin = new Thickness(0, 0, 0, 6f)
                        };
                        foreach (var inline in p.Inlines)
                        {
                            quoteText.Inlines.Add(new Italic(inline));
                        }
                        quoteStack.AddChild(quoteText);
                    }
                }

                var uic = new InlineUIContainer(border);
                return new Paragraph(uic) { MarginBottom = 12f };
            }

            return null;
        }

        private static Inline? ConvertInline(object inlineNode, Brush defaultFg, float fontSize, TtfFont defaultFont, TtfFont codeFont, ElementTheme theme)
        {
            if (inlineNode is LiteralInline literalInline)
            {
                return new Run(literalInline.Content.ToString()) { Foreground = defaultFg, FontSize = fontSize };
            }
            else if (inlineNode is EmphasisInline emphasisInline)
            {
                var span = (emphasisInline.DelimiterCount == 2) ? (Span)new Bold() : (Span)new Italic();
                span.Foreground = defaultFg;
                span.FontSize = fontSize;

                foreach (var childNode in emphasisInline)
                {
                    var childInline = ConvertInline(childNode, defaultFg, fontSize, defaultFont, codeFont, theme);
                    if (childInline != null)
                    {
                        span.Inlines.Add(childInline);
                    }
                }
                return span;
            }
            else if (inlineNode is LineBreakInline)
            {
                return new LineBreak();
            }
            else if (inlineNode is LinkInline linkInline)
            {
                if (linkInline.IsImage)
                {
                    var img = new Image
                    {
                        Stretch = Stretch.Uniform,
                        WidthConstraint = 240f,
                        HeightConstraint = 160f,
                        Margin = new Thickness(4)
                    };

                    if (!string.IsNullOrEmpty(linkInline.Url))
                    {
                        img.Source = linkInline.Url;
                    }

                    var border = new Border
                    {
                        BorderBrush = ThemeManager.GetBrush("ControlBorder", theme),
                        BorderThickness = new Thickness(1f),
                        CornerRadius = 6f,
                        Child = img,
                        Padding = new Thickness(2f)
                    };

                    return new InlineUIContainer(border);
                }
                else
                {
                    var hyperlink = new Hyperlink { Uri = linkInline.Url ?? string.Empty };
                    hyperlink.Foreground = defaultFg;
                    hyperlink.FontSize = fontSize;

                    foreach (var childNode in linkInline)
                    {
                        var childInline = ConvertInline(childNode, defaultFg, fontSize, defaultFont, codeFont, theme);
                        if (childInline != null)
                        {
                            hyperlink.Inlines.Add(childInline);
                        }
                    }

                    hyperlink.Click += (s, e) =>
                    {
                        ProGpuWinUiDiagnostics.WriteLine($"[MarkdownParser] Clicked hyperlink Uri: {hyperlink.Uri}");
                    };

                    return hyperlink;
                }
            }
            else if (inlineNode is CodeInline codeInline)
            {
                var run = new Run(codeInline.Content)
                {
                    Font = codeFont,
                    FontSize = fontSize * 0.95f,
                    Foreground = new SolidColorBrush(0xE06C75FF)
                };

                return run;
            }

            return null;
        }
    }
}
