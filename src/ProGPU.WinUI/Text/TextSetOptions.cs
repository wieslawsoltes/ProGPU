using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Text;

[Flags]
public enum TextSetOptions
{
    None = 0,
    UnicodeBidi = 1,
    Unlink = 8,
    Unhide = 16,
    CheckTextLimit = 32,
    FormatRtf = 8192,
    ApplyRtfDocumentDefaults = 16384
}
