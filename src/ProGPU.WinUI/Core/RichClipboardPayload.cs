using System;

namespace Microsoft.UI.Xaml;

/// <summary>Portable clipboard payload that hosts can map to native text and RTF formats.</summary>
public sealed class RichClipboardPayload
{
    public RichClipboardPayload(string plainText, string rtf, string html = "")
    {
        PlainText = plainText ?? throw new ArgumentNullException(nameof(plainText));
        Rtf = rtf ?? throw new ArgumentNullException(nameof(rtf));
        Html = html ?? throw new ArgumentNullException(nameof(html));
    }

    public string PlainText { get; }
    public string Rtf { get; }
    public string Html { get; }
}
