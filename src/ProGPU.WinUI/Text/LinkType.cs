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

public enum LinkType
{
    Undefined = 0, NotALink = 1, ClientLink = 2, FriendlyLinkName = 3,
    FriendlyLinkAddress = 4, AutoLink = 5, AutoLinkEmail = 6,
    AutoLinkPhone = 7, AutoLinkPath = 8
}
