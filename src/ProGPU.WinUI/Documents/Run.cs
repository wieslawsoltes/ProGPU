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

public class Run : Inline
{
    private string _text = string.Empty;
    private FlowDirection _flowDirection = FlowDirection.LeftToRight;
    internal RichTextStyle? RetainedStyle { get; set; }
    internal bool HasExplicitFlowDirection { get; private set; }

    public string Text
    {
        get => _text;
        set
        {
            value ??= string.Empty;
            if (!string.Equals(_text, value, StringComparison.Ordinal))
            {
                _text = value;
                OnChanged();
            }
        }
    }

    public FlowDirection FlowDirection
    {
        get => _flowDirection;
        set
        {
            if (_flowDirection == value && HasExplicitFlowDirection) return;
            _flowDirection = value;
            HasExplicitFlowDirection = true;
            OnChanged();
        }
    }

    internal void ApplyRetainedFlowDirection(FlowDirection? direction)
    {
        if (direction is not { } value) return;
        _flowDirection = value;
        HasExplicitFlowDirection = true;
    }

    public Run() { }
    public Run(string text) { Text = text; }
}

} // namespace Microsoft.UI.Xaml.Documents
