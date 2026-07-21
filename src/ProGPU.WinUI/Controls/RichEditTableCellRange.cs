using System;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Identifies one content range in a rectangular editable-table selection.
/// Positions exclude the row and cell delimiters so editing the range preserves
/// the table grid.
/// </summary>
public readonly record struct RichEditTableCellRange(
    int Row,
    int Column,
    int ColumnSpan,
    int StartPosition,
    int EndPosition);
