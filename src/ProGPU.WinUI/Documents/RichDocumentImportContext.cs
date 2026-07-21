using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>Typed styling and platform context supplied to document importers.</summary>
public readonly record struct RichDocumentImportContext(
    TtfFont DefaultFont,
    TtfFont CodeFont,
    float DefaultFontSize,
    Brush DefaultForeground,
    ElementTheme Theme);
