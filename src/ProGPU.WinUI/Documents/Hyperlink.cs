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

public class Hyperlink : Span
{
    private string _uri = string.Empty;

    public string Uri
    {
        get => _uri;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_uri, value, StringComparison.Ordinal)) return;
            _uri = value;
            OnChanged();
        }
    }

    public event EventHandler<RoutedEventArgs>? Click;

    public Hyperlink() { }
    public Hyperlink(params Inline[] inlines) : base(inlines) { }

    public void RaiseClick()
    {
        Click?.Invoke(this, new RoutedEventArgs { OriginalSource = this });
    }
}

} // namespace Microsoft.UI.Xaml.Documents
