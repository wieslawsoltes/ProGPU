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
public enum MarkerStyle { Undefined = 0, Parenthesis = 1, Parentheses = 2, Period = 3, Plain = 4, Minus = 5, NoNumber = 6 }
