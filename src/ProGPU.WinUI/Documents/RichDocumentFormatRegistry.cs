using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

public sealed class RichDocumentFormatRegistry
{
    private readonly Dictionary<string, IRichDocumentFormatCodec> _formats =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IRichDocumentFormatCodec> _extensions =
        new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<IRichDocumentFormatCodec> Formats => _formats.Values;

    public static RichDocumentFormatRegistry CreateDefault()
    {
        var registry = new RichDocumentFormatRegistry();
        registry.Register(PlainTextDocumentCodec.Default);
        registry.Register(MarkdownDocumentCodec.Default);
        registry.Register(RtfDocumentCodec.Default);
        registry.Register(HtmlDocumentCodec.Default);
        registry.Register(WordDocumentCodec.Default);
        return registry;
    }

    public void Register(IRichDocumentFormatCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (string.IsNullOrWhiteSpace(codec.FormatId))
            throw new ArgumentException("A format codec must have a stable identifier.", nameof(codec));

        _formats[codec.FormatId] = codec;
        foreach (string extension in codec.FileExtensions)
        {
            if (string.IsNullOrWhiteSpace(extension)) continue;
            string normalized = extension[0] == '.' ? extension : $".{extension}";
            _extensions[normalized] = codec;
        }
    }

    public bool TryGetFormat(string formatId, out IRichDocumentFormatCodec? codec) =>
        _formats.TryGetValue(formatId, out codec);

    public bool TryGetFileExtension(string extension, out IRichDocumentFormatCodec? codec)
    {
        extension ??= string.Empty;
        string normalized = extension.Length > 0 && extension[0] != '.' ? $".{extension}" : extension;
        return _extensions.TryGetValue(normalized, out codec);
    }
}
