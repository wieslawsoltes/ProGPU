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


public abstract class TextElement : Block
{
    private Brush? _foreground;
    private float? _fontSize;
    private TtfFont? _font;

    public Brush? Foreground
    {
        get => _foreground;
        set
        {
            if (!ReferenceEquals(_foreground, value))
            {
                _foreground = value;
                OnChanged();
            }
        }
    }

    public float? FontSize
    {
        get => _fontSize;
        set
        {
            if (_fontSize != value)
            {
                _fontSize = value;
                OnChanged();
            }
        }
    }

    public TtfFont? Font
    {
        get => _font;
        set
        {
            if (!ReferenceEquals(_font, value))
            {
                _font = value;
                OnChanged();
            }
        }
    }
}

} // namespace Microsoft.UI.Xaml.Documents
