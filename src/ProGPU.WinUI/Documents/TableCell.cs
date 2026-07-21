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

public class TableCell : Span
{
    private Brush? _background;
    private int _columnSpan = 1;
    private int _rowSpan = 1;
    internal byte VerticalMergeFlag { get; set; }
    public Brush? Background
    {
        get => _background;
        set { if (!ReferenceEquals(_background, value)) { _background = value; OnChanged(); } }
    }
    public int ColumnSpan
    {
        get => _columnSpan;
        set
        {
            int normalized = Math.Max(1, value);
            if (_columnSpan == normalized) return;
            _columnSpan = normalized;
            OnChanged();
        }
    }
    public int RowSpan
    {
        get => _rowSpan;
        set
        {
            int normalized = Math.Max(1, value);
            if (_rowSpan == normalized) return;
            _rowSpan = normalized;
            OnChanged();
        }
    }

    public TableCell() { }
    public TableCell(params Inline[] inlines) : base(inlines) { }
    public TableCell(string text) : base(new Run(text)) { }
}

} // namespace Microsoft.UI.Xaml.Documents
