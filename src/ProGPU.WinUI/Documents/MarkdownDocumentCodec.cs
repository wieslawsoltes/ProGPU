using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

public sealed class MarkdownDocumentCodec : IRichDocumentFormatCodec
{
    private static readonly string[] Extensions = [".md", ".markdown"];
    public static MarkdownDocumentCodec Default { get; } = new();
    public string FormatId => "text/markdown";
    public IReadOnlyList<string> FileExtensions => Extensions;
    public bool CanImport => true;
    public bool CanExport => true;

    public RichDocument Import(ReadOnlySpan<byte> source, in RichDocumentImportContext context) =>
        MarkdownDocumentImporter.Default.Import(Encoding.UTF8.GetString(source), context);

    public byte[] Export(RichDocument document) =>
        Encoding.UTF8.GetBytes(MarkdownDocumentExporter.Default.Export(document));
}
