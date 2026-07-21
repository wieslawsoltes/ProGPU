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

public enum TextScript
{
    Undefined = 0, Ansi, EastEurope, Cyrillic, Greek, Turkish, Hebrew, Arabic,
    Baltic, Vietnamese, Default, Symbol, Thai, ShiftJis, GB2312, Hangul, Big5,
    PC437, Oem, Mac, Armenian, Syriac, Thaana, Devanagari, Bengali, Gurmukhi,
    Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam, Sinhala, Lao, Tibetan,
    Myanmar, Georgian, Jamo, Ethiopic, Cherokee, Aboriginal, Ogham, Runic,
    Khmer, Mongolian, Braille, Yi, Limbu, TaiLe, NewTaiLue, SylotiNagri,
    Kharoshthi, Kayahli, UnicodeSymbol, Emoji, Glagolitic, Lisu, Vai, NKo,
    Osmanya, PhagsPa, Gothic, Deseret, Tifinagh
}
