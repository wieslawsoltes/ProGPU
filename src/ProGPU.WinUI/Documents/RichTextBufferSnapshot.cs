using System;
using System.Collections.Generic;
using System.Text;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

public sealed class RichTextBufferSnapshot
{
    internal RichTextBufferSnapshot(RichTextSpan[] spans, int length)
    {
        Spans = spans;
        Length = length;
    }

    internal RichTextSpan[] Spans { get; }
    public int Length { get; }
}
