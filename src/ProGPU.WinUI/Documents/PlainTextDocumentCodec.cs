using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

public sealed class PlainTextDocumentCodec : IRichDocumentFormatCodec
{
    private static readonly string[] Extensions = [".txt", ".text"];
    public static PlainTextDocumentCodec Default { get; } = new();
    public string FormatId => "text/plain";
    public IReadOnlyList<string> FileExtensions => Extensions;
    public bool CanImport => true;
    public bool CanExport => true;

    public RichDocument Import(ReadOnlySpan<byte> source, in RichDocumentImportContext context) =>
        PlainTextDocumentImporter.Default.Import(Encoding.UTF8.GetString(source), context);

    public byte[] Export(RichDocument document) =>
        Encoding.UTF8.GetBytes(PlainTextDocumentExporter.Default.Export(document));
}
