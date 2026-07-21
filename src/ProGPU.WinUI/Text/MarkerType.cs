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
public enum MarkerType
{
    Undefined = 0, None, Bullet, Arabic, LowercaseEnglishLetter, UppercaseEnglishLetter,
    LowercaseRoman, UppercaseRoman, UnicodeSequence, CircledNumber, BlackCircleWingding,
    WhiteCircleWingding, ArabicWide, SimplifiedChinese, TraditionalChinese,
    JapanSimplifiedChinese, JapanKorea, ArabicDictionary, ArabicAbjad, Hebrew,
    ThaiAlphabetic, ThaiNumeric, DevanagariVowel, DevanagariConsonant, DevanagariNumeric
}
