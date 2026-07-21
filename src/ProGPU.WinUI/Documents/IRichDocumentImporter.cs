using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>
/// Reflection-free external-format import seam. Implementations translate their source
/// into the shared semantic block/inline model; shaping and rendering remain centralized.
/// </summary>
public interface IRichDocumentImporter<in TSource>
{
    RichDocument Import(TSource source, in RichDocumentImportContext context);
}
