using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>
/// Reflection-free file-format seam for rich documents. Codecs own only semantic
/// import/export; shaping, layout, editing, and rendering remain format-independent.
/// </summary>
public interface IRichDocumentFormatCodec
{
    string FormatId { get; }
    IReadOnlyList<string> FileExtensions { get; }
    bool CanImport { get; }
    bool CanExport { get; }
    RichDocument Import(ReadOnlySpan<byte> source, in RichDocumentImportContext context);
    byte[] Export(RichDocument document);
}
