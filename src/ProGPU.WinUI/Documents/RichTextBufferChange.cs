using System;
using System.Collections.Generic;
using System.Text;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

internal readonly record struct RichTextBufferChange(
    int Start,
    int OldLength,
    int NewLength,
    bool TextChanged,
    bool RequiresFullProjection);
