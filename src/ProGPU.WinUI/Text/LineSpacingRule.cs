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

public enum LineSpacingRule
{
    Undefined = 0, Single = 1, OneAndHalf = 2, Double = 3, AtLeast = 4,
    Exactly = 5, Multiple = 6, Percent = 7
}
