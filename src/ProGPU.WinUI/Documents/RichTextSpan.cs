using System;
using System.Collections.Generic;
using System.Text;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>Immutable styled span; text storage is shared by undo snapshots.</summary>
public readonly record struct RichTextSpan(string Text, RichTextStyle Style);
