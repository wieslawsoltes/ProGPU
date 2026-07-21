using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Documents;

internal readonly record struct RichLogicalCaretAnchor(
    int TextPosition,
    float X,
    float Y,
    float Height);
