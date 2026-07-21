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

public interface ITextCharacterFormat
{
    FormatEffect AllCaps { get; set; }
    Windows.UI.Color BackgroundColor { get; set; }
    FormatEffect Bold { get; set; }
    Windows.UI.Text.FontStretch FontStretch { get; set; }
    Windows.UI.Text.FontStyle FontStyle { get; set; }
    Windows.UI.Color ForegroundColor { get; set; }
    FormatEffect Hidden { get; set; }
    FormatEffect Italic { get; set; }
    float Kerning { get; set; }
    string LanguageTag { get; set; }
    LinkType LinkType { get; }
    string Name { get; set; }
    FormatEffect Outline { get; set; }
    float Position { get; set; }
    FormatEffect ProtectedText { get; set; }
    float Size { get; set; }
    FormatEffect SmallCaps { get; set; }
    float Spacing { get; set; }
    FormatEffect Strikethrough { get; set; }
    FormatEffect Subscript { get; set; }
    FormatEffect Superscript { get; set; }
    TextScript TextScript { get; set; }
    UnderlineType Underline { get; set; }
    int Weight { get; set; }
    ITextCharacterFormat GetClone();
    bool IsEqual(ITextCharacterFormat format);
    void SetClone(ITextCharacterFormat value);
}
