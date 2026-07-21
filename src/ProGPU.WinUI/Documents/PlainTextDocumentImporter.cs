using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

public sealed class PlainTextDocumentImporter : IRichDocumentImporter<string>
{
    public static PlainTextDocumentImporter Default { get; } = new();

    public RichDocument Import(string source, in RichDocumentImportContext context)
    {
        var document = new RichDocument();
        document.Add(new Paragraph(new Run(source ?? string.Empty)) { MarginBottom = 0f });
        return document;
    }
}
