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

public class TableRow
{
    internal event Action? Changed;
    public RichElementCollection<TableCell> Cells { get; }

    public TableRow()
    {
        Cells = new RichElementCollection<TableCell>(() => Changed?.Invoke());
    }
    public TableRow(params TableCell[] cells) : this()
    {
        Cells.AddRange(cells);
    }
}

} // namespace Microsoft.UI.Xaml.Documents
