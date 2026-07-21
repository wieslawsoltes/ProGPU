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

public enum UnderlineType
{
    Undefined = 0, None = 1, Single = 2, Words = 3, Double = 4, Dotted = 5,
    Dash = 6, DashDot = 7, DashDotDot = 8, Wave = 9, Thick = 10, Thin = 11,
    DoubleWave = 12, HeavyWave = 13, LongDash = 14, ThickDash = 15,
    ThickDashDot = 16, ThickDashDotDot = 17, ThickDotted = 18, ThickLongDash = 19
}
