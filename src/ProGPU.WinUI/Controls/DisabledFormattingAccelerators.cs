using System;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>Specifies rich-text formatting keyboard shortcuts that are disabled.</summary>
[Flags]
public enum DisabledFormattingAccelerators : uint
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    All = uint.MaxValue
}
