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

public class Table : Inline
{
    private float _cellPadding = 8f;
    private float _borderThickness = 1f;
    private Brush? _borderBrush;
    private RichValueCollection<float>? _columnWidths;
    private FlowDirection? _flowDirection;

    public RichElementCollection<TableRow> Rows { get; }
    public float CellPadding
    {
        get => _cellPadding;
        set { if (_cellPadding != value) { _cellPadding = value; OnChanged(); } }
    }
    public float BorderThickness
    {
        get => _borderThickness;
        set { if (_borderThickness != value) { _borderThickness = value; OnChanged(); } }
    }
    public Brush? BorderBrush
    {
        get => _borderBrush;
        set { if (!ReferenceEquals(_borderBrush, value)) { _borderBrush = value; OnChanged(); } }
    }
    public FlowDirection? FlowDirection
    {
        get => _flowDirection;
        set { if (_flowDirection != value) { _flowDirection = value; OnChanged(); } }
    }
    public IList<float>? ColumnWidths
    {
        get => _columnWidths;
        set
        {
            if (ReferenceEquals(_columnWidths, value)) return;
            if (value is null)
            {
                if (_columnWidths is null) return;
                _columnWidths = null;
            }
            else
            {
                _columnWidths = new RichValueCollection<float>(OnChanged);
                _columnWidths.AddRange(value);
                return;
            }
            OnChanged();
        }
    }

    public Table()
    {
        Rows = new RichElementCollection<TableRow>(OnChanged);
    }
    public Table(params TableRow[] rows) : this()
    {
        Rows.AddRange(rows);
    }
}

} // namespace Microsoft.UI.Xaml.Documents
