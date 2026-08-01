using Microsoft.UI.Text;

namespace ProGPU.WinUI.Text;

/// <summary>
/// ProGPU extensions for retained WinUI text ranges.
/// </summary>
public static class RichEditTextRangeExtensions
{
    /// <summary>
    /// Replaces the range with a retained rich-text table.
    /// </summary>
    public static void InsertTable(
        this ITextRange range,
        int columnCount,
        int rowCount,
        bool autoFit = true)
    {
        ArgumentNullException.ThrowIfNull(range);
        switch (range)
        {
            case RichEditTextRange retainedRange:
                retainedRange.InsertTable(
                    columnCount,
                    rowCount,
                    autoFit);
                break;
            case RichEditTextSelection selection:
                selection.InsertTable(
                    columnCount,
                    rowCount,
                    autoFit);
                break;
            default:
                throw new ArgumentException(
                    "The range is not backed by a ProGPU rich-text document.",
                    nameof(range));
        }
    }
}
