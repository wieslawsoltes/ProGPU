using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>
/// Reflection-free external-format export seam. The destination type can be a string,
/// byte buffer, stream-oriented writer, or a format-specific typed result.
/// </summary>
public interface IRichDocumentExporter<out TDestination>
{
    TDestination Export(RichDocument document);
}
