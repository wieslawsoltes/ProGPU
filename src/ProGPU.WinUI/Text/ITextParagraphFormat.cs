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

public interface ITextParagraphFormat
{
    ParagraphAlignment Alignment { get; set; }
    float FirstLineIndent { get; }
    FormatEffect KeepTogether { get; set; }
    FormatEffect KeepWithNext { get; set; }
    float LeftIndent { get; }
    float LineSpacing { get; }
    LineSpacingRule LineSpacingRule { get; }
    MarkerAlignment ListAlignment { get; set; }
    int ListLevelIndex { get; set; }
    int ListStart { get; set; }
    MarkerStyle ListStyle { get; set; }
    float ListTab { get; set; }
    MarkerType ListType { get; set; }
    FormatEffect NoLineNumber { get; set; }
    FormatEffect PageBreakBefore { get; set; }
    float RightIndent { get; set; }
    FormatEffect RightToLeft { get; set; }
    float SpaceAfter { get; set; }
    float SpaceBefore { get; set; }
    ParagraphStyle Style { get; set; }
    int TabCount { get; }
    FormatEffect WidowControl { get; set; }
    void AddTab(float position, TabAlignment align, TabLeader leader);
    void ClearAllTabs();
    void DeleteTab(float position);
    ITextParagraphFormat GetClone();
    void GetTab(int index, out float position, out TabAlignment align, out TabLeader leader);
    bool IsEqual(ITextParagraphFormat format);
    void SetClone(ITextParagraphFormat format);
    void SetIndents(float start, float left, float right);
    void SetLineSpacing(LineSpacingRule rule, float spacing);
}
