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

public class ListBlock : Inline
{
    private bool _isOrdered;
    private float _indentation = 24f;

    public RichElementCollection<ListItem> Items { get; }
    public bool IsOrdered
    {
        get => _isOrdered;
        set { if (_isOrdered != value) { _isOrdered = value; OnChanged(); } }
    }
    public float Indentation
    {
        get => _indentation;
        set { if (_indentation != value) { _indentation = value; OnChanged(); } }
    }

    public ListBlock()
    {
        Items = new RichElementCollection<ListItem>(OnChanged);
    }
}

} // namespace Microsoft.UI.Xaml.Documents
