using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Text;

public interface ITextSelection : ITextRange
{
    SelectionOptions Options { get; set; }
    SelectionType Type { get; }
    int EndKey(TextRangeUnit unit, bool extend);
    int HomeKey(TextRangeUnit unit, bool extend);
    int MoveDown(TextRangeUnit unit, int count, bool extend);
    int MoveLeft(TextRangeUnit unit, int count, bool extend);
    int MoveRight(TextRangeUnit unit, int count, bool extend);
    int MoveUp(TextRangeUnit unit, int count, bool extend);
    void TypeText(string value);
}
