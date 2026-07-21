using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Text.Shaping;

namespace Microsoft.UI.Xaml.Documents {

public class InlineUIContainer : Inline
{
    private FrameworkElement? _child;
    internal RichTextEmbeddedObject? RetainedEmbeddedObject { get; set; }

    public FrameworkElement? Child
    {
        get => _child;
        set
        {
            if (ReferenceEquals(_child, value)) return;
            _child = value;
            OnChanged();
        }
    }

    public InlineUIContainer() { }
    public InlineUIContainer(FrameworkElement child)
    {
        Child = child;
    }
}

} // namespace Microsoft.UI.Xaml.Documents
