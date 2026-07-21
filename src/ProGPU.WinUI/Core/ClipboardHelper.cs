using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.UI.Xaml;

public static class ClipboardHelper
{
    private static string _fallbackText = string.Empty;
    private static string? _richPlainText;
    private static RichTextSpan[]? _richSpans;
    private static RichTextRtfCodec.ParagraphSpan[]? _richParagraphs;
    /// <summary>Optional synchronous platform seam used by hosts without process access.</summary>
    public static Action<string>? PlatformSetText { get; set; }

    /// <summary>Optional synchronous platform seam used by hosts without process access.</summary>
    public static Func<string>? PlatformGetText { get; set; }

    /// <summary>Optional typed host seam for publishing native plain-text and RTF flavors together.</summary>
    public static Action<RichClipboardPayload>? PlatformSetRichText { get; set; }

    /// <summary>Optional typed host seam for reading native RTF with its plain-text fallback.</summary>
    public static Func<RichClipboardPayload?>? PlatformGetRichText { get; set; }

    public static void SetText(string text)
    {
        if (text == null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        _richPlainText = null;
        _richSpans = null;
        _richParagraphs = null;
        _fallbackText = text;
        SetPlatformText(text);
    }

    internal static void SetRichText(
        string text,
        IReadOnlyList<RichTextSpan> spans,
        IReadOnlyList<RichTextRtfCodec.ParagraphSpan>? paragraphs = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spans);
        string rtf = RichTextRtfCodec.Encode(spans, paragraphs);
        RichTextStyle fallback = spans.Count > 0 ? spans[0].Style : default;
        RichTextRtfCodec.DecodedDocument decoded = RichTextRtfCodec.DecodeDocument(rtf, fallback);
        string html = Encoding.UTF8.GetString(HtmlDocumentCodec.Default.Export(RtfDocumentCodec.BuildDocument(decoded)));
        var payload = new RichClipboardPayload(text, rtf, html);
        _fallbackText = text;
        if (PlatformSetRichText is { } platformSetRichText)
            platformSetRichText(payload);
        else if (!MacOsRichClipboard.TrySet(payload) && !WindowsRichClipboard.TrySet(payload))
            SetPlatformText(text);
        _richPlainText = text;
        _richSpans = spans.ToArray();
        _richParagraphs = paragraphs is null ? null : CloneParagraphs(paragraphs);
    }

    internal static bool TryGetRichText(
        RichTextStyle fallback,
        out RichTextSpan[] spans,
        out RichTextRtfCodec.ParagraphSpan[] paragraphs)
    {
        RichClipboardPayload? nativePayload = PlatformGetRichText?.Invoke();
        if (nativePayload is null) MacOsRichClipboard.TryGet(out nativePayload);
        if (nativePayload is null) WindowsRichClipboard.TryGet(out nativePayload);
        if (nativePayload is { } platformPayload)
        {
            if (!string.IsNullOrWhiteSpace(platformPayload.Rtf))
            {
                RichTextRtfCodec.DecodedDocument decoded = RichTextRtfCodec.DecodeDocument(platformPayload.Rtf, fallback);
                spans = decoded.Spans;
                paragraphs = decoded.Paragraphs;
                return spans.Length > 0;
            }
            if (!string.IsNullOrWhiteSpace(platformPayload.Html) &&
                fallback.Font is { } fallbackFont &&
                fallback.Foreground is { } fallbackForeground)
            {
                var context = new RichDocumentImportContext(
                    fallbackFont,
                    fallbackFont,
                    fallback.FontSize > 0f ? fallback.FontSize : 14f,
                    fallbackForeground,
                    ElementTheme.Default);
                RichDocument document = HtmlDocumentCodec.Default.Import(
                    Encoding.UTF8.GetBytes(platformPayload.Html),
                    context);
                (spans, paragraphs) = RtfDocumentCodec.CollectRtfContent(document);
                return spans.Length > 0;
            }
        }
        string text = GetText();
        if (_richSpans is not null && string.Equals(text, _richPlainText, StringComparison.Ordinal))
        {
            spans = (RichTextSpan[])_richSpans.Clone();
            paragraphs = _richParagraphs is null
                ? Array.Empty<RichTextRtfCodec.ParagraphSpan>()
                : CloneParagraphs(_richParagraphs);
            return true;
        }
        spans = Array.Empty<RichTextSpan>();
        paragraphs = Array.Empty<RichTextRtfCodec.ParagraphSpan>();
        return false;
    }

    internal static bool HasRichText()
    {
        RichClipboardPayload? payload = PlatformGetRichText?.Invoke();
        if (payload is null) MacOsRichClipboard.TryGet(out payload);
        if (payload is null) WindowsRichClipboard.TryGet(out payload);
        if (payload is { } &&
            (payload.Rtf.Length > 0 || payload.Html.Length > 0)) return true;
        return _richSpans is { Length: > 0 } &&
            string.Equals(GetText(), _richPlainText, StringComparison.Ordinal);
    }

    private static RichTextRtfCodec.ParagraphSpan[] CloneParagraphs(
        IReadOnlyList<RichTextRtfCodec.ParagraphSpan> paragraphs)
    {
        var result = new RichTextRtfCodec.ParagraphSpan[paragraphs.Count];
        for (int index = 0; index < paragraphs.Count; index++)
        {
            RichTextRtfCodec.ParagraphSpan paragraph = paragraphs[index];
            result[index] = new RichTextRtfCodec.ParagraphSpan(
                paragraph.Start,
                paragraph.Length,
                paragraph.Format.Clone())
            {
                IsTableRow = paragraph.IsTableRow,
                TableCellRightEdges = paragraph.TableCellRightEdges is null
                    ? null
                    : (float[])paragraph.TableCellRightEdges.Clone()
            };
        }
        return result;
    }

    private static void SetPlatformText(string text)
    {
        if (PlatformSetText is { } platformSetText)
        {
            platformSetText(text);
            return;
        }

        if (WindowsRichClipboard.TrySetText(text) ||
            LinuxTextClipboard.TrySet(text) ||
            MacOsTextClipboard.TrySet(text)) return;

        // Native clipboard access is optional for headless and sandboxed hosts. The
        // process-local value remains available when no platform transport exists.
    }

    public static string GetText()
    {
        if (PlatformGetText is { } platformGetText) return platformGetText() ?? string.Empty;
        if (WindowsRichClipboard.TryGetText(out string windowsText)) return windowsText;
        if (LinuxTextClipboard.TryGet(out string linuxText)) return linuxText;
        if (MacOsTextClipboard.TryGet(out string macOsText)) return macOsText;
        return _fallbackText;
    }
}
