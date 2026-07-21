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

/// <summary>
/// Retained, typed Text Object Model facade over <see cref="RichTextBuffer"/>.
/// Range instances store only endpoints and edit the same buffer used by rendering.
/// </summary>
public sealed class RichEditTextDocument
{
    private readonly RichEditBox _owner;
    private readonly RichEditTextSelection _selection;
    private readonly List<WeakReference<RichEditTextRange>> _trackedRanges = new();
    private uint _undoLimit = 100;
    private RichEditMathMode _mathMode;
    private string _mathMl = string.Empty;
    private float _defaultTabStop = 36f;
    private bool _alignmentIncludesTrailingWhitespace;
    private CaretType _caretType = CaretType.Normal;
    private bool _ignoreTrailingCharacterSpacing;

    internal RichEditTextDocument(RichEditBox owner)
    {
        _owner = owner;
        _selection = new RichEditTextSelection(this);
        owner.TextChanged += (_, _) => ContentsChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ContentsChanged;

    public bool AlignmentIncludesTrailingWhitespace
    {
        get => _alignmentIncludesTrailingWhitespace;
        set
        {
            if (_alignmentIncludesTrailingWhitespace == value) return;
            _alignmentIncludesTrailingWhitespace = value;
            _owner.OnDocumentAlignmentIncludesTrailingWhitespaceChanged(value);
        }
    }
    public CaretType CaretType
    {
        get => _caretType;
        set
        {
            if (_caretType == value) return;
            _caretType = value;
            _owner.OnDocumentCaretTypeChanged();
        }
    }
    public float DefaultTabStop
    {
        get => _defaultTabStop;
        set
        {
            if (!float.IsFinite(value) || value <= 0f) throw new ArgumentOutOfRangeException(nameof(value));
            if (_defaultTabStop == value) return;
            _defaultTabStop = value;
            _owner.OnDocumentDefaultTabStopChanged(value);
        }
    }
    public bool IgnoreTrailingCharacterSpacing
    {
        get => _ignoreTrailingCharacterSpacing;
        set
        {
            if (_ignoreTrailingCharacterSpacing == value) return;
            _ignoreTrailingCharacterSpacing = value;
            _owner.OnDocumentIgnoreTrailingCharacterSpacingChanged(value);
        }
    }
    public uint UndoLimit
    {
        get => _undoLimit;
        set
        {
            _undoLimit = value;
            _owner.TrimDocumentUndoHistory(value);
        }
    }
    public ITextSelection Selection => _selection;

    internal RichEditBox Owner => _owner;
    internal RichEditTextSelection SelectionModel => _selection;
    internal int TextLength => _owner.GetDocumentText().Length;
    internal int Length => checked(TextLength + 1);
    internal string Text => _owner.GetDocumentText();
    internal string StoryText => string.Concat(Text, "\r");

    internal void TrackRange(RichEditTextRange range)
    {
        _trackedRanges.Add(new WeakReference<RichEditTextRange>(range));
    }

    internal void OnTextReplaced(RichTextBufferChange change)
    {
        if (_trackedRanges.Count == 0) return;
        for (int index = _trackedRanges.Count - 1; index >= 0; index--)
        {
            if (_trackedRanges[index].TryGetTarget(out RichEditTextRange? range))
                range.OnDocumentTextReplaced(change.Start, change.OldLength, change.NewLength);
            else
                _trackedRanges.RemoveAt(index);
        }
    }

    public bool CanCopy() => _owner.SelectionLength > 0;
    public bool CanPaste() => Microsoft.UI.Xaml.ClipboardHelper.HasRichText() ||
        !string.IsNullOrEmpty(Microsoft.UI.Xaml.ClipboardHelper.GetText());
    public bool CanRedo() => _owner.CanRedoDocument;
    public bool CanUndo() => _owner.CanUndoDocument;
    public void ClearUndoRedoHistory() => _owner.ClearDocumentUndoRedoHistory();
    public void Undo() => _owner.Undo();
    public void Redo() => _owner.Redo();

    public ITextRange GetRange(int startPosition, int endPosition) =>
        new RichEditTextRange(this, startPosition, endPosition);

    /// <summary>
    /// Returns the concrete retained range, including ProGPU's TOM2 table extensions.
    /// </summary>
    public RichEditTextRange GetRange2(int startPosition, int endPosition) =>
        new(this, startPosition, endPosition);

    public ITextRange GetRangeFromPoint(Windows.Foundation.Point point, PointOptions options)
    {
        int position = _owner.GetDocumentPositionFromPoint(
            point,
            (options & PointOptions.ClientCoordinates) != 0);
        return new RichEditTextRange(this, position, position);
    }

    public void GetText(TextGetOptions options, out string value)
    {
        ValidateGetOptions(options);
        if ((options & TextGetOptions.FormatRtf) != 0)
        {
            value = RichTextRtfCodec.Encode(
                _owner.GetDocumentSpans(0, TextLength),
                _owner.GetDocumentParagraphSpans(0, TextLength),
                (options & TextGetOptions.AllowFinalEop) != 0);
            return;
        }
        string text = (options & (TextGetOptions.NoHidden | TextGetOptions.UseObjectText | TextGetOptions.IncludeNumbering)) != 0
            ? _owner.GetDocumentText(0, TextLength, options)
            : Text;
        if ((options & TextGetOptions.AllowFinalEop) != 0) text += '\r';
        value = NormalizeNewlinesForGet(text, options);
    }

    public void SetText(TextSetOptions options, string? value)
    {
        if ((options & TextSetOptions.FormatRtf) != 0)
        {
            RichTextRtfCodec.DecodedDocument decoded = RichTextRtfCodec.DecodeDocument(
                value,
                _owner.GetDocumentStyleAt(0));
            RichTextSpan[] spans = ApplySetOptions(decoded.Spans, options);
            bool applyDefaults = (options & TextSetOptions.ApplyRtfDocumentDefaults) != 0;
            if (applyDefaults)
            {
                BeginUndoGroup();
                _owner.SaveDocumentUndoState();
            }
            try
            {
                if (applyDefaults) ApplyRtfDocumentDefaults(decoded);
                _owner.ReplaceDocumentRangeWithSpans(
                    0,
                    TextLength,
                    spans,
                    (options & TextSetOptions.CheckTextLimit) != 0);
                _owner.ApplyDecodedParagraphFormats(0, decoded.Paragraphs);
            }
            finally
            {
                if (applyDefaults) EndUndoGroup();
            }
            return;
        }
        _owner.ReplaceDocumentRange(
            0,
            TextLength,
            value,
            (options & TextSetOptions.CheckTextLimit) != 0,
            options);
    }

    public void LoadFromStream(TextSetOptions options, Windows.Storage.Streams.IRandomAccessStream value)
    {
        ArgumentNullException.ThrowIfNull(value);
        SetText(options, ReadStreamText(value));
    }

    public void SaveToStream(TextGetOptions options, Windows.Storage.Streams.IRandomAccessStream value)
    {
        ArgumentNullException.ThrowIfNull(value);
        GetText(options, out string text);
        WriteStreamText(value, text);
    }

    public void SetDefaultCharacterFormat(ITextCharacterFormat value)
    {
        ArgumentNullException.ThrowIfNull(value);
        GetDefaultCharacterFormat().SetClone(value);
    }

    public void SetDefaultParagraphFormat(ITextParagraphFormat value)
    {
        ArgumentNullException.ThrowIfNull(value);
        GetDefaultParagraphFormat().SetClone(value);
    }

    public RichEditMathMode GetMathMode() => _mathMode;
    public void SetMathMode(RichEditMathMode mode) => _mathMode = mode;
    public void GetMathML(out string value) => value = _mathMl;
    public void SetMathML(string value) => _mathMl = value ?? string.Empty;

    public void BeginUndoGroup() => _owner.BeginDocumentUndoGroup();
    public void EndUndoGroup() => _owner.EndDocumentUndoGroup();
    public int BatchDisplayUpdates() => _owner.BeginDocumentDisplayUpdates();
    public int ApplyDisplayUpdates() => _owner.ApplyDocumentDisplayUpdates();

    public ITextCharacterFormat GetDefaultCharacterFormat() =>
        new RichEditTextCharacterFormat(new RichEditTextRange(this, 0, 0));

    public ITextParagraphFormat GetDefaultParagraphFormat() =>
        new RichEditTextParagraphFormat(_owner);

    internal static string NormalizeNewlinesForGet(string text, TextGetOptions options)
    {
        ValidateGetOptions(options);
        if ((options & TextGetOptions.UseLf) != 0)
            return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if ((options & TextGetOptions.UseCrlf) != 0)
            return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);
        return text;
    }

    internal static void ValidateGetOptions(TextGetOptions options)
    {
        if ((options & TextGetOptions.UseLf) != 0 && (options & TextGetOptions.UseCrlf) != 0)
            throw new ArgumentException("UseLf and UseCrlf cannot be combined.", nameof(options));
    }

    internal static string GetVisibleText(IReadOnlyList<RichTextSpan> spans)
        => GetPlainText(spans, TextGetOptions.NoHidden);

    internal static RichTextSpan[] ApplySetOptions(
        IReadOnlyList<RichTextSpan> spans,
        TextSetOptions options)
    {
        bool unlink = (options & TextSetOptions.Unlink) != 0;
        bool unhide = (options & TextSetOptions.Unhide) != 0;
        if (!unlink && !unhide && spans is RichTextSpan[] array) return array;
        var result = new RichTextSpan[spans.Count];
        for (int index = 0; index < spans.Count; index++)
        {
            RichTextSpan span = spans[index];
            result[index] = new RichTextSpan(
                span.Text,
                span.Style with
                {
                    Link = unlink ? null : span.Style.Link,
                    IsHidden = unhide ? false : span.Style.IsHidden
                });
        }
        return result;
    }

    internal static string GetPlainText(IReadOnlyList<RichTextSpan> spans, TextGetOptions options)
    {
        var builder = new StringBuilder();
        for (int index = 0; index < spans.Count; index++)
        {
            RichTextSpan span = spans[index];
            if ((options & TextGetOptions.NoHidden) != 0 && span.Style.IsHidden) continue;
            if ((options & TextGetOptions.UseObjectText) != 0 && span.Style.EmbeddedObject is { } embedded)
            {
                foreach (char character in span.Text)
                    builder.Append(character == '\uFFFC' ? embedded.AlternateText : character);
            }
            else
            {
                builder.Append(span.Text);
            }
        }
        return builder.ToString();
    }

    internal void ApplyRtfDocumentDefaults(RichTextRtfCodec.DecodedDocument decoded)
    {
        _owner.ApplyRtfDocumentDefaults(
            decoded.DocumentDefaultStyle,
            decoded.DocumentDefaultParagraphFormat);
        if (decoded.DocumentDefaultParagraphFormat is { DefaultTabStop: > 0f } paragraphFormat)
            _defaultTabStop = paragraphFormat.DefaultTabStop;
    }

    internal void RestoreDefaultTabStop(float value) => _defaultTabStop = value;

    internal static string ReadStreamText(Windows.Storage.Streams.IRandomAccessStream value)
    {
        Stream stream = value.AsStream();
        if (!stream.CanRead) throw new InvalidOperationException("The stream is not readable.");
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }

    internal static void WriteStreamText(Windows.Storage.Streams.IRandomAccessStream value, string text)
    {
        Stream stream = value.AsStream();
        if (!stream.CanWrite) throw new InvalidOperationException("The stream is not writable.");
        stream.Position = 0;
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        writer.Write(text);
        writer.Flush();
        stream.Position = 0;
    }

}
