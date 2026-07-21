using System;
using System.Collections.Generic;
using System.Text;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>Immutable semantic payload for a TOM inline image/object replacement character.</summary>
public sealed class RichTextEmbeddedObject
{
    private readonly byte[] _data;

    public RichTextEmbeddedObject(
        int width,
        int height,
        int ascent,
        Microsoft.UI.Text.VerticalCharacterAlignment verticalAlignment,
        string? alternateText,
        ReadOnlySpan<byte> data)
    {
        Width = width;
        Height = height;
        Ascent = ascent;
        VerticalAlignment = verticalAlignment;
        AlternateText = alternateText ?? string.Empty;
        _data = data.ToArray();
        if (IsSupportedEncodedImage(_data))
            ImageSource = new Microsoft.UI.Xaml.Controls.EncodedImageSource(_data, width, height);
    }

    public int Width { get; }
    public int Height { get; }
    public int Ascent { get; }
    public Microsoft.UI.Text.VerticalCharacterAlignment VerticalAlignment { get; }
    public string AlternateText { get; }
    public ReadOnlyMemory<byte> Data => _data;
    internal Microsoft.UI.Xaml.Controls.EncodedImageSource? ImageSource { get; }

    private static bool IsSupportedEncodedImage(ReadOnlySpan<byte> data) =>
        (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) ||
        (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) ||
        (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) ||
        (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M');
}
