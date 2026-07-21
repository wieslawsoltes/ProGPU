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

namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml.Documents;

public class PositionedRichChar
{
    public RichChar Info;
    public Vector2 Position;
    public sbyte BidiLevel;
    public int ClusterStart;
    public int ClusterLength = 1;
    public float ShapedAdvance;
    public float ShapedAdvanceWithoutCharacterSpacing;
    public bool HasShapedAdvance;
    public ShapingGlyphFlags ShapingFlags;
}

} // namespace Microsoft.UI.Xaml.Controls
