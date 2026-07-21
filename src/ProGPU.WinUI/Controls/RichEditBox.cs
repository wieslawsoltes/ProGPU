using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Text.Shaping;

namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml.Documents;

    public class RichEditBox : Control, ITextInputClient
    {
        private static readonly SolidColorBrush AmbientShadowBrush = new SolidColorBrush(0x0000000A);
        private static readonly SolidColorBrush PenumbraShadowBrush = new SolidColorBrush(0x00000014);

        public event RoutedEventHandler? TextChanged;
        public event RoutedEventHandler? SelectionChanged;
        public event EventHandler<RichEditBoxTextChangingEventArgs>? TextChanging;
        public event EventHandler<RichEditBoxSelectionChangingEventArgs>? SelectionChanging;
        public event EventHandler<TextControlCopyingToClipboardEventArgs>? CopyingToClipboard;
        public event EventHandler<TextControlCuttingToClipboardEventArgs>? CuttingToClipboard;
        public event TextControlPasteEventHandler? Paste;
        public event EventHandler<CandidateWindowBoundsChangedEventArgs>? CandidateWindowBoundsChanged;
        public event ContextMenuOpeningEventHandler? ContextMenuOpening;
        public event EventHandler<TextCompositionStartedEventArgs>? TextCompositionStarted;
        public event EventHandler<TextCompositionChangedEventArgs>? TextCompositionChanged;
        public event EventHandler<TextCompositionEndedEventArgs>? TextCompositionEnded;

        protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer? OnCreateAutomationPeer() =>
            new Microsoft.UI.Xaml.Automation.Peers.RichEditBoxAutomationPeer(this);

        public static readonly DependencyProperty AcceptsReturnProperty =
            DependencyProperty.Register(
                nameof(AcceptsReturn),
                typeof(bool),
                typeof(RichEditBox),
                new PropertyMetadata(true));

        public static readonly DependencyProperty CharacterCasingProperty =
            DependencyProperty.Register(
                nameof(CharacterCasing),
                typeof(CharacterCasing),
                typeof(RichEditBox),
                new PropertyMetadata(CharacterCasing.Normal));

        public static readonly DependencyProperty ClipboardCopyFormatProperty =
            DependencyProperty.Register(
                nameof(ClipboardCopyFormat),
                typeof(RichEditClipboardFormat),
                typeof(RichEditBox),
                new PropertyMetadata(RichEditClipboardFormat.AllFormats));

        public static readonly DependencyProperty DesiredCandidateWindowAlignmentProperty =
            DependencyProperty.Register(
                nameof(DesiredCandidateWindowAlignment),
                typeof(CandidateWindowAlignment),
                typeof(RichEditBox),
                new PropertyMetadata(CandidateWindowAlignment.Default));

        public static readonly DependencyProperty DisabledFormattingAcceleratorsProperty =
            DependencyProperty.Register(
                nameof(DisabledFormattingAccelerators),
                typeof(DisabledFormattingAccelerators),
                typeof(RichEditBox),
                new PropertyMetadata(DisabledFormattingAccelerators.None));

        public static readonly DependencyProperty InputScopeProperty =
            DependencyProperty.Register(
                nameof(InputScope),
                typeof(InputScope),
                typeof(RichEditBox),
                new PropertyMetadata(null));

        public static readonly DependencyProperty IsColorFontEnabledProperty =
            DependencyProperty.Register(
                nameof(IsColorFontEnabled),
                typeof(bool),
                typeof(RichEditBox),
                new PropertyMetadata(true, static (d, _) => ((RichEditBox)d).Invalidate()));

        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register(
                nameof(IsReadOnly),
                typeof(bool),
                typeof(RichEditBox),
                new PropertyMetadata(false, static (d, _) => ((RichEditBox)d).Invalidate()));

        public static readonly DependencyProperty IsSpellCheckEnabledProperty =
            DependencyProperty.Register(
                nameof(IsSpellCheckEnabled),
                typeof(bool),
                typeof(RichEditBox),
                new PropertyMetadata(true));

        public static readonly DependencyProperty IsTextPredictionEnabledProperty =
            DependencyProperty.Register(
                nameof(IsTextPredictionEnabled),
                typeof(bool),
                typeof(RichEditBox),
                new PropertyMetadata(true));

        public static readonly DependencyProperty MaxLengthProperty =
            DependencyProperty.Register(
                nameof(MaxLength),
                typeof(int),
                typeof(RichEditBox),
                new PropertyMetadata(0));

        public static readonly DependencyProperty PlaceholderTextProperty =
            DependencyProperty.Register(
                nameof(PlaceholderText),
                typeof(string),
                typeof(RichEditBox),
                new PropertyMetadata(string.Empty, static (d, _) => ((RichEditBox)d).Invalidate()));

        public static readonly DependencyProperty SelectionHighlightColorProperty =
            DependencyProperty.Register(
                nameof(SelectionHighlightColor),
                typeof(Brush),
                typeof(RichEditBox),
                new PropertyMetadata(null, static (d, e) => ((RichEditBox)d).OnSelectionHighlightColorChanged(e.NewValue as Brush)));

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(
                nameof(Header),
                typeof(object),
                typeof(RichEditBox),
                new PropertyMetadata(null, static (d, e) => ((RichEditBox)d).OnHeaderChanged(e.NewValue))
                {
                    AffectsMeasure = true,
                    AffectsArrange = true
                });

        public static readonly DependencyProperty HeaderPlacementProperty =
            DependencyProperty.Register(
                nameof(HeaderPlacement),
                typeof(ControlHeaderPlacement),
                typeof(RichEditBox),
                new PropertyMetadata(ControlHeaderPlacement.Top, static (d, _) => ((RichEditBox)d).InvalidateMeasure())
                {
                    AffectsMeasure = true,
                    AffectsArrange = true
                });

        public static readonly DependencyProperty HeaderTemplateProperty =
            DependencyProperty.Register(nameof(HeaderTemplate), typeof(object), typeof(RichEditBox), new PropertyMetadata(null));

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(object), typeof(RichEditBox), new PropertyMetadata(null));

        public static readonly DependencyProperty PreventKeyboardDisplayOnProgrammaticFocusProperty =
            DependencyProperty.Register(nameof(PreventKeyboardDisplayOnProgrammaticFocus), typeof(bool), typeof(RichEditBox), new PropertyMetadata(false));

        public static readonly DependencyProperty ProofingMenuFlyoutProperty =
            DependencyProperty.Register(nameof(ProofingMenuFlyout), typeof(object), typeof(RichEditBox), new PropertyMetadata(null));

        public static readonly DependencyProperty SelectionFlyoutProperty =
            DependencyProperty.Register(nameof(SelectionFlyout), typeof(object), typeof(RichEditBox), new PropertyMetadata(null));

        public static readonly DependencyProperty SelectionHighlightColorWhenNotFocusedProperty =
            DependencyProperty.Register(
                nameof(SelectionHighlightColorWhenNotFocused),
                typeof(Brush),
                typeof(RichEditBox),
                new PropertyMetadata(null, static (d, _) => ((RichEditBox)d).UpdateSelectionHighlightColor()));

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(
                nameof(TextAlignment),
                typeof(TextAlignment),
                typeof(RichEditBox),
                new PropertyMetadata(TextAlignment.Left, static (d, e) => ((RichEditBox)d).OnTextAlignmentChanged(e))
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public static readonly DependencyProperty HorizontalTextAlignmentProperty =
            DependencyProperty.Register(
                nameof(HorizontalTextAlignment),
                typeof(TextAlignment),
                typeof(RichEditBox),
                new PropertyMetadata(TextAlignment.Left, static (d, e) => ((RichEditBox)d).OnHorizontalTextAlignmentChanged(e))
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public static readonly DependencyProperty TextReadingOrderProperty =
            DependencyProperty.Register(
                nameof(TextReadingOrder),
                typeof(TextReadingOrder),
                typeof(RichEditBox),
                new PropertyMetadata(TextReadingOrder.DetectFromContent, static (d, _) => ((RichEditBox)d).ApplyTextLayoutProperties())
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public static readonly DependencyProperty TextWrappingProperty =
            DependencyProperty.Register(
                nameof(TextWrapping),
                typeof(TextWrapping),
                typeof(RichEditBox),
                new PropertyMetadata(TextWrapping.Wrap, static (d, _) => ((RichEditBox)d).ApplyTextLayoutProperties())
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        private bool _syncingTextAlignment;
        private readonly Microsoft.UI.Text.RichEditTextDocument _textDocument;
        internal Microsoft.UI.Text.RichParagraphFormatState ParagraphFormatState { get; } = new();

        /// <summary>Gets the retained text-object-model document used by this editor.</summary>
        public Microsoft.UI.Text.RichEditTextDocument TextDocument => _textDocument;

        /// <summary>Compatibility alias used by earlier WinUI/UWP RichEditBox APIs.</summary>
        public Microsoft.UI.Text.RichEditTextDocument Document => _textDocument;

        /// <summary>
        /// Creates a detached semantic snapshot that can be displayed by any rich-document
        /// presenter or exported by a registered format codec.
        /// </summary>
        public RichDocument CreateRichDocumentSnapshot()
        {
            EnsureBufferSynchronized();
            return RtfDocumentCodec.BuildDocument(new RichTextRtfCodec.DecodedDocument
            {
                Spans = GetDocumentSpans(0, _buffer.Length),
                Paragraphs = GetDocumentParagraphSpans(0, _buffer.Length)
            });
        }

        /// <summary>
        /// Replaces the editor contents from the shared semantic document model as one
        /// undo group. The source document remains independently mutable.
        /// </summary>
        public void SetRichDocument(RichDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            (RichTextSpan[] spans, RichTextRtfCodec.ParagraphSpan[] paragraphs) =
                RtfDocumentCodec.CollectRtfContent(document);
            _textDocument.BeginUndoGroup();
            try
            {
                int insertedLength = ReplaceDocumentRangeWithSpans(0, GetTotalCharacters(), spans);
                ApplyDecodedParagraphFormats(0, paragraphs);
                SetDocumentSelection(insertedLength, insertedLength);
            }
            finally
            {
                _textDocument.EndUndoGroup();
            }
        }

        /// <summary>Imports bytes through an extensible codec and opens the result for editing.</summary>
        public void LoadDocument(
            IRichDocumentFormatCodec codec,
            ReadOnlySpan<byte> source,
            in RichDocumentImportContext context)
        {
            ArgumentNullException.ThrowIfNull(codec);
            if (!codec.CanImport) throw new NotSupportedException($"The '{codec.FormatId}' codec does not support import.");
            SetRichDocument(codec.Import(source, context));
        }

        /// <summary>Exports a detached editor snapshot through an extensible codec.</summary>
        public byte[] SaveDocument(IRichDocumentFormatCodec codec)
        {
            ArgumentNullException.ThrowIfNull(codec);
            if (!codec.CanExport) throw new NotSupportedException($"The '{codec.FormatId}' codec does not support export.");
            return codec.Export(CreateRichDocumentSnapshot());
        }

        public bool AcceptsReturn
        {
            get => (bool)(GetValue(AcceptsReturnProperty) ?? true);
            set => SetValue(AcceptsReturnProperty, value);
        }

        public CharacterCasing CharacterCasing
        {
            get => (CharacterCasing)(GetValue(CharacterCasingProperty) ?? CharacterCasing.Normal);
            set => SetValue(CharacterCasingProperty, value);
        }

        public RichEditClipboardFormat ClipboardCopyFormat
        {
            get => (RichEditClipboardFormat)(GetValue(ClipboardCopyFormatProperty) ?? RichEditClipboardFormat.AllFormats);
            set => SetValue(ClipboardCopyFormatProperty, value);
        }

        public CandidateWindowAlignment DesiredCandidateWindowAlignment
        {
            get => (CandidateWindowAlignment)(GetValue(DesiredCandidateWindowAlignmentProperty) ?? CandidateWindowAlignment.Default);
            set => SetValue(DesiredCandidateWindowAlignmentProperty, value);
        }

        public DisabledFormattingAccelerators DisabledFormattingAccelerators
        {
            get => (DisabledFormattingAccelerators)(GetValue(DisabledFormattingAcceleratorsProperty) ?? DisabledFormattingAccelerators.None);
            set => SetValue(DisabledFormattingAcceleratorsProperty, value);
        }

        public InputScope InputScope
        {
            get
            {
                if (GetValue(InputScopeProperty) is InputScope scope) return scope;
                scope = new InputScope();
                SetValue(InputScopeProperty, scope);
                return scope;
            }
            set => SetValue(InputScopeProperty, value ?? throw new ArgumentNullException(nameof(value)));
        }

        public bool IsColorFontEnabled
        {
            get => (bool)(GetValue(IsColorFontEnabledProperty) ?? true);
            set => SetValue(IsColorFontEnabledProperty, value);
        }

        public bool IsReadOnly
        {
            get => (bool)(GetValue(IsReadOnlyProperty) ?? false);
            set => SetValue(IsReadOnlyProperty, value);
        }

        public bool IsSpellCheckEnabled
        {
            get => (bool)(GetValue(IsSpellCheckEnabledProperty) ?? true);
            set => SetValue(IsSpellCheckEnabledProperty, value);
        }

        public bool IsTextPredictionEnabled
        {
            get => (bool)(GetValue(IsTextPredictionEnabledProperty) ?? true);
            set => SetValue(IsTextPredictionEnabledProperty, value);
        }

        public int MaxLength
        {
            get => (int)(GetValue(MaxLengthProperty) ?? 0);
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                SetValue(MaxLengthProperty, value);
            }
        }

        public string PlaceholderText
        {
            get => (string)(GetValue(PlaceholderTextProperty) ?? string.Empty);
            set => SetValue(PlaceholderTextProperty, value ?? string.Empty);
        }

        public Brush? SelectionHighlightColor
        {
            get => GetValue(SelectionHighlightColorProperty) as Brush;
            set => SetValue(SelectionHighlightColorProperty, value);
        }

        private void OnSelectionHighlightColorChanged(Brush? brush)
        {
            UpdateSelectionHighlightColor();
            base.Invalidate();
        }

        private void UpdateSelectionHighlightColor()
        {
            _blockView.SelectionHighlightColor = IsFocused
                ? SelectionHighlightColor
                : SelectionHighlightColorWhenNotFocused ?? SelectionHighlightColor;
            base.Invalidate();
        }

        public object? Header
        {
            get => GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        private void OnHeaderChanged(object? value)
        {
            _headerPresenter.Content = value;
            _headerPresenter.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
            if (value is null)
            {
                if (ReferenceEquals(_headerPresenter.Parent, this)) RemoveChild(_headerPresenter);
            }
            else if (!ReferenceEquals(_headerPresenter.Parent, this))
            {
                AddChild(_headerPresenter);
            }
        }

        public ControlHeaderPlacement HeaderPlacement
        {
            get => (ControlHeaderPlacement)(GetValue(HeaderPlacementProperty) ?? ControlHeaderPlacement.Top);
            set => SetValue(HeaderPlacementProperty, value);
        }

        public object? HeaderTemplate
        {
            get => GetValue(HeaderTemplateProperty);
            set => SetValue(HeaderTemplateProperty, value);
        }

        public object? Description
        {
            get => GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        public bool PreventKeyboardDisplayOnProgrammaticFocus
        {
            get => (bool)(GetValue(PreventKeyboardDisplayOnProgrammaticFocusProperty) ?? false);
            set => SetValue(PreventKeyboardDisplayOnProgrammaticFocusProperty, value);
        }

        public object? ProofingMenuFlyout
        {
            get => GetValue(ProofingMenuFlyoutProperty);
        }

        public object? SelectionFlyout
        {
            get => GetValue(SelectionFlyoutProperty);
            set => SetValue(SelectionFlyoutProperty, value);
        }

        public Windows.Foundation.IAsyncOperation<IReadOnlyList<string>> GetLinguisticAlternativesAsync() =>
            new Windows.Foundation.CompletedAsyncOperation<IReadOnlyList<string>>(Array.Empty<string>());

        public Brush? SelectionHighlightColorWhenNotFocused
        {
            get => GetValue(SelectionHighlightColorWhenNotFocusedProperty) as Brush;
            set => SetValue(SelectionHighlightColorWhenNotFocusedProperty, value);
        }

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)(GetValue(TextAlignmentProperty) ?? TextAlignment.Left);
            set => SetValue(TextAlignmentProperty, value);
        }

        public TextAlignment HorizontalTextAlignment
        {
            get => (TextAlignment)(GetValue(HorizontalTextAlignmentProperty) ?? TextAlignment.Left);
            set => SetValue(HorizontalTextAlignmentProperty, value);
        }

        public TextReadingOrder TextReadingOrder
        {
            get => (TextReadingOrder)(GetValue(TextReadingOrderProperty) ?? TextReadingOrder.DetectFromContent);
            set => SetValue(TextReadingOrderProperty, value);
        }

        public TextWrapping TextWrapping
        {
            get => (TextWrapping)(GetValue(TextWrappingProperty) ?? TextWrapping.Wrap);
            set => SetValue(TextWrappingProperty, value);
        }

        private void OnTextAlignmentChanged(DependencyPropertyChangedEventArgs args)
        {
            if (!_syncingTextAlignment)
            {
                _syncingTextAlignment = true;
                SetValue(HorizontalTextAlignmentProperty, args.NewValue ?? TextAlignment.Left);
                _syncingTextAlignment = false;
            }

            ApplyTextLayoutProperties();
        }

        private void OnHorizontalTextAlignmentChanged(DependencyPropertyChangedEventArgs args)
        {
            if (!_syncingTextAlignment)
            {
                _syncingTextAlignment = true;
                SetValue(TextAlignmentProperty, args.NewValue ?? TextAlignment.Left);
                _syncingTextAlignment = false;
            }

            ApplyTextLayoutProperties();
        }

        private void ApplyTextLayoutProperties()
        {
            if (_blockView is null) return;
            _blockView.TextAlignment = ResolveEffectiveTextAlignment();
            _blockView.TextReadingOrder = TextReadingOrder;
            _blockView.TextWrapping = TextWrapping;
            _blockView.InvalidateLayout();
            InvalidateMeasure();
            base.Invalidate();
        }

        private TextAlignment ResolveEffectiveTextAlignment()
        {
            if (!IsPropertySetLocally(TextAlignmentProperty) &&
                !IsPropertySetInStyle(TextAlignmentProperty) &&
                FlowDirection == FlowDirection.RightToLeft)
            {
                return TextAlignment.Right;
            }

            return TextAlignment;
        }

        public string Text
        {
            get
            {
                EnsureBufferSynchronized();
                return _buffer.GetText();
            }
            set
            {
                RichTextStyle style = GetDefaultTextStyle();
                _buffer.SetText(value, style);
                _undoStack.Clear();
                _redoStack.Clear();
                _caretIndex = Math.Clamp(_caretIndex, 0, value?.Length ?? 0);
                SelectionStart = _caretIndex;
                SelectionLength = 0;
                CommitBufferChange(isContentChanging: true);
            }
        }

        private float _fontSize = 14f;

        protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
        {
            base.OnPropertyChanged(dp, oldValue, newValue);
            if (dp == FontProperty)
            {
                _blockView.Font = newValue as TtfFont;
                Invalidate();
            }
            else if (dp == ForegroundProperty)
            {
                _blockView.Foreground = newValue as Brush;
                Invalidate();
            }
            else if (dp == FlowDirectionProperty)
            {
                ApplyTextLayoutProperties();
            }
        }
        private int _caretIndex;
        private readonly ContentPresenter _headerPresenter;
        private readonly ScrollViewer _scrollViewer;
        private readonly RichTextBlock _blockView;
        private readonly RichTextBuffer _buffer = new();
        private readonly RichDocument _editorLayoutDocument = new();
        private readonly List<int> _editorBlockStarts = new();
        private bool _bufferNeedsSync = true;
        private bool _publishingBuffer;
        private RichTextStyle? _activeTypingStyle;

        private int _selectionStart = 0;
        private int _selectionLength = 0;
        private int _selectionAnchor = 0;
        private RichEditTableCellRange[] _selectedTableCells = Array.Empty<RichEditTableCellRange>();
        private bool _isDraggingSelection = false;
        private bool _caretTrailingAffinity;
        private readonly HashSet<Key> _pressedKeys = new();
        private int _compositionStart = -1;
        private int _compositionLength;
        private string _compositionOriginalText = string.Empty;
        private readonly List<RichCaretStop> _visualCaretStops = new();
        private readonly List<float> _visualLineYs = new();
        private int _displayUpdateDepth;
        private bool _displayUpdatePending;
        private bool _textChangedPending;

        private class UndoState
        {
            public RichTextBufferSnapshot Snapshot { get; }
            public RichTextRtfCodec.ParagraphSpan[]? Paragraphs { get; }
            public Microsoft.UI.Text.RichParagraphFormatState DefaultParagraphFormat { get; }
            public int CaretIndex { get; }
            public int SelectionStart { get; }
            public int SelectionLength { get; }
            public RichTextStyle? ActiveTypingStyle { get; }
            public float DefaultTabStop { get; }

            public UndoState(
                RichTextBufferSnapshot snapshot,
                RichTextRtfCodec.ParagraphSpan[]? paragraphs,
                Microsoft.UI.Text.RichParagraphFormatState defaultParagraphFormat,
                int caretIndex,
                int selectionStart,
                int selectionLength,
                RichTextStyle? activeTypingStyle,
                float defaultTabStop)
            {
                Snapshot = snapshot;
                Paragraphs = paragraphs;
                DefaultParagraphFormat = defaultParagraphFormat;
                CaretIndex = caretIndex;
                SelectionStart = selectionStart;
                SelectionLength = selectionLength;
                ActiveTypingStyle = activeTypingStyle;
                DefaultTabStop = defaultTabStop;
            }
        }

        private readonly Stack<UndoState> _undoStack = new();
        private readonly Stack<UndoState> _redoStack = new();
        private int _undoGroupDepth;
        private bool _undoGroupCaptured;

        private void SaveUndoState(bool includeParagraphFormats = false)
        {
            if (_undoGroupDepth > 0 && _undoGroupCaptured) return;
            _undoStack.Push(CaptureUndoState(includeParagraphFormats));
            if (_undoGroupDepth > 0) _undoGroupCaptured = true;
            TrimDocumentUndoHistory(_textDocument.UndoLimit);
            _redoStack.Clear();
        }

        private UndoState CaptureUndoState(bool includeParagraphFormats)
        {
            EnsureBufferSynchronized();
            return new UndoState(
                _buffer.CreateSnapshot(),
                includeParagraphFormats ? GetDocumentParagraphSpans(0, _buffer.Length) : null,
                ParagraphFormatState.Clone(),
                CaretIndex,
                SelectionStart,
                SelectionLength,
                _activeTypingStyle,
                _textDocument.DefaultTabStop);
        }

        public void Undo()
        {
            if (IsReadOnly) return;
            if (_undoStack.Count == 0) return;

            var previousState = _undoStack.Pop();
            UndoState currentState = CaptureUndoState(previousState.Paragraphs is not null);
            _redoStack.Push(currentState);
            ApplyUndoState(previousState);
        }

        public void Redo()
        {
            if (IsReadOnly) return;
            if (_redoStack.Count == 0) return;

            var nextState = _redoStack.Pop();
            UndoState currentState = CaptureUndoState(nextState.Paragraphs is not null);
            _undoStack.Push(currentState);
            ApplyUndoState(nextState);
        }

        private void ApplyUndoState(UndoState state)
        {
            _buffer.Restore(state.Snapshot);
            ParagraphFormatState.CopyFrom(state.DefaultParagraphFormat);
            SelectionStart = state.SelectionStart;
            SelectionLength = state.SelectionLength;
            CaretIndex = state.CaretIndex;
            _activeTypingStyle = state.ActiveTypingStyle;
            _textDocument.RestoreDefaultTabStop(state.DefaultTabStop);

            Invalidate();
            CommitBufferChange(isContentChanging: true);
            if (state.Paragraphs is not null) ApplyDecodedParagraphFormats(0, state.Paragraphs);
        }

        private string GetSelectedText()
        {
            if (_selectedTableCells.Length > 0) return GetSelectedTableCellText();
            if (SelectionLength <= 0) return string.Empty;
            EnsureBufferSynchronized();
            if (_buffer.Length == 0) return string.Empty;

            int start = Math.Clamp(SelectionStart, 0, _buffer.Length);
            int length = Math.Clamp(SelectionLength, 0, _buffer.Length - start);
            if (length == 0) return string.Empty;
            return _buffer.GetText(start, length);
        }

        private void InsertText(
            string text,
            bool applyCharacterCasing = false,
            bool allowOvertype = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            EnsureBufferSynchronized();

            if (applyCharacterCasing)
            {
                text = CharacterCasing switch
                {
                    CharacterCasing.Lower => text.ToLowerInvariant(),
                    CharacterCasing.Upper => text.ToUpperInvariant(),
                    _ => text
                };
            }

            if (_selectedTableCells.Length > 0)
            {
                ReplaceSelectedTableCells(text);
                return;
            }

            GetTextInputReplacementRange(text, allowOvertype, out int insertIdx, out int replacementEnd);
            int replacedLength = replacementEnd - insertIdx;

            if (MaxLength > 0)
            {
                EnsureBufferSynchronized();
                int available = MaxLength - (_buffer.Length - replacedLength);
                if (available <= 0) return;
                if (text.Length > available)
                {
                    int boundary = TextBoundaryHelper.PreviousGraphemeBoundary(text, available + 1);
                    if (boundary <= 0) return;
                    text = text[..boundary];
                }
            }
            if (text.Length == 0) return;

            // A grapheme-safe length truncation can reduce the number of characters
            // consumed in overtype mode, so resolve the final replacement once more.
            GetTextInputReplacementRange(text, allowOvertype, out insertIdx, out replacementEnd);
            replacedLength = replacementEnd - insertIdx;
            if (replacedLength > 0 &&
                _buffer.AnyStyle(insertIdx, replacedLength, static style => style.IsProtected))
                return;

            RichTextStyle style = GetDefaultTextStyle();

            if (_activeTypingStyle != null)
            {
                style = _activeTypingStyle.Value;
            }
            else if (_buffer.Length > 0)
            {
                style = _buffer.GetStyleAt(insertIdx > 0 ? insertIdx - 1 : 0, GetDefaultTextStyle());
            }

            SaveUndoStateForTextChange(insertIdx, replacementEnd, text);
            _buffer.Replace(insertIdx, replacedLength, [new RichTextSpan(text, style)]);
            CaretIndex = insertIdx + text.Length;
            SelectionStart = CaretIndex;
            SelectionLength = 0;
            CommitBufferChange(isContentChanging: true);
        }

        private void GetTextInputReplacementRange(
            string text,
            bool allowOvertype,
            out int start,
            out int end)
        {
            bool replaceSelection = _textDocument.SelectionModel.ReplacesSelection;
            // SelectionStart is the public TOM insertion position even if a host set it
            // directly before the visual caret has been synchronized.
            start = Math.Clamp(SelectionStart, 0, _buffer.Length);
            end = SelectionLength > 0 && replaceSelection
                ? Math.Clamp(SelectionStart + SelectionLength, start, _buffer.Length)
                : start;
            if (end != start || !allowOvertype || !_textDocument.SelectionModel.IsOvertype ||
                ContainsParagraphSeparator(text))
                return;

            int replacementClusters = 0;
            for (int position = 0; position < text.Length; replacementClusters++)
            {
                int next = TextBoundaryHelper.NextGraphemeBoundary(text, position);
                position = next > position ? next : position + 1;
            }
            for (int cluster = 0; cluster < replacementClusters && end < _buffer.Length; cluster++)
            {
                if (_buffer[end] is '\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029') break;
                int next = TextBoundaryHelper.NextGraphemeBoundary(Text, end);
                if (next <= end) break;
                end = next;
            }
        }

        private static class ClipboardHelper
        {
            public static void SetText(string text) => Microsoft.UI.Xaml.ClipboardHelper.SetText(text);

            public static void SetRichText(
                string text,
                IReadOnlyList<RichTextSpan> spans,
                IReadOnlyList<RichTextRtfCodec.ParagraphSpan> paragraphs) =>
                Microsoft.UI.Xaml.ClipboardHelper.SetRichText(text, spans, paragraphs);

            public static string GetText() => Microsoft.UI.Xaml.ClipboardHelper.GetText();

            public static bool TryGetRichText(
                RichTextStyle fallback,
                out RichTextSpan[] spans,
                out RichTextRtfCodec.ParagraphSpan[] paragraphs) =>
                Microsoft.UI.Xaml.ClipboardHelper.TryGetRichText(fallback, out spans, out paragraphs);
        }


        public int SelectionStart
        {
            get => _selectionStart;
            set
            {
                int total = GetTotalCharacters();
                int clamped = Math.Clamp(value, 0, total);
                int length = Math.Clamp(_selectionLength, 0, total - clamped);
                TryApplySelection(clamped, length);
            }
        }

        public int SelectionLength
        {
            get => _selectionLength;
            set
            {
                int clamped = Math.Clamp(value, 0, GetTotalCharacters() - SelectionStart);
                TryApplySelection(_selectionStart, clamped);
            }
        }

        private bool TryApplySelection(int start, int length)
        {
            int total = GetTotalCharacters();
            start = Math.Clamp(start, 0, total);
            length = Math.Clamp(length, 0, total - start);
            if (_selectionStart == start && _selectionLength == length)
            {
                ClearTableSelectionOverlay();
                return true;
            }

            var changing = new RichEditBoxSelectionChangingEventArgs(start, length);
            SelectionChanging?.Invoke(this, changing);
            if (changing.Cancel) return false;

            ClearTableSelectionOverlay();
            _selectionStart = start;
            _selectionLength = length;
            _blockView.SelectionStart = start;
            _blockView.SelectionLength = length;
            _blockView.InvalidateTextRendering();
            base.Invalidate();
            SelectionChanged?.Invoke(this, new RoutedEventArgs { OriginalSource = this });
            return true;
        }

        public RichElementCollection<Inline> Inlines => _blockView.Inlines;

        /// <summary>Presenter-local virtualization diagnostics for the editor document.</summary>
        public RichDocumentLayoutSession LayoutSession => _blockView.LayoutSession;

        /// <summary>
        /// Gets the content-only cell ranges in the active rectangular table selection.
        /// The returned snapshot is ordered by source position.
        /// </summary>
        public IReadOnlyList<RichEditTableCellRange> SelectedTableCells => _selectedTableCells;

        /// <summary>
        /// Selects the smallest table-cell rectangle containing the two document
        /// positions. Returns <see langword="false"/> when either position is outside
        /// an editable table or belongs to a different table.
        /// </summary>
        public bool SelectTableCells(int anchorPosition, int activePosition)
        {
            EnsureBufferSynchronized();
            if (!TryBuildTableCellSelection(anchorPosition, activePosition, out RichEditTableCellRange[] cells))
                return false;

            int start = cells[0].StartPosition;
            int end = cells[^1].EndPosition;
            int length = Math.Max(0, end - start);
            var changing = new RichEditBoxSelectionChangingEventArgs(start, length);
            SelectionChanging?.Invoke(this, changing);
            if (changing.Cancel) return false;

            ClearTableSelectionOverlay();
            _selectionStart = start;
            _selectionLength = length;
            _blockView.SelectionStart = start;
            _blockView.SelectionLength = length;
            _selectedTableCells = cells;
            _blockView.TableSelection = cells;
            _blockView.InvalidateTextRendering();
            CaretIndex = Math.Clamp(activePosition, 0, _buffer.Length);
            _selectionAnchor = Math.Clamp(anchorPosition, 0, _buffer.Length);
            base.Invalidate();
            SelectionChanged?.Invoke(this, new RoutedEventArgs { OriginalSource = this });
            return true;
        }

        /// <summary>Collapses an active table-cell selection at its active end.</summary>
        public void ClearTableCellSelection()
        {
            if (_selectedTableCells.Length == 0) return;
            int caret = CaretIndex;
            ClearTableSelectionOverlay();
            SetDocumentSelection(caret, caret);
        }



        public float FontSize
        {
            get => _fontSize;
            set { _fontSize = value; _blockView.FontSize = value; Invalidate(); }
        }

        public int CaretIndex
        {
            get => _caretIndex;
            set
            {
                int total = GetTotalCharacters();
                int clamped = Math.Clamp(value, 0, total);
                if (_caretIndex != clamped)
                {
                    _caretIndex = clamped;
                    base.Invalidate();
                    ScrollToCaret();
                }
            }
        }

        public new void Invalidate()
        {
            _blockView?.Invalidate();
            base.Invalidate();
        }

        private void ScrollToCaret()
        {
            if (Font == null) return;
            List<RichCaretStop> stops = GetVisualCaretStops();
            if (stops.Count == 0) return;

            RichCaretStop stop = GetCaretStop(stops, CaretIndex, _caretTrailingAffinity);
            float charY = stop.Y;
            float charH = stop.Height;

            float viewportH = Size.Y - Padding.Vertical;
            if (viewportH <= 0f) return;

            if (charY < _scrollViewer.VerticalOffset)
            {
                _scrollViewer.VerticalOffset = charY;
            }
            else if (charY + charH > _scrollViewer.VerticalOffset + viewportH)
            {
                _scrollViewer.VerticalOffset = charY + charH - viewportH;
            }
        }

        public override void OnVisualStateChanged()
        {
            base.OnVisualStateChanged();
            if (!IsFocused)
            {
                _pressedKeys.Clear();
                _isDraggingSelection = false;
            }

            UpdateSelectionHighlightColor();
        }

        public override void OnKeyUp(KeyRoutedEventArgs e)
        {
            _pressedKeys.Remove(e.Key);
            base.OnKeyUp(e);
        }

        public RichEditBox()
        {
            Padding = new Thickness(8);
            CornerRadius = 4f;
            _headerPresenter = new ContentPresenter
            {
                Visibility = Visibility.Collapsed,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _scrollViewer = new ScrollViewer { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(0x00000000) };
            _blockView = new RichTextBlock { Padding = new Thickness(0) };
            _scrollViewer.Content = _blockView;
            AddChild(_scrollViewer);
            ApplyTextLayoutProperties();

            _blockView.Inlines.Changed += OnEditorInlinesChanged;
            EnsureBufferSynchronized();
            _blockView.Document = _editorLayoutDocument;
            _textDocument = new Microsoft.UI.Text.RichEditTextDocument(this);
            _buffer.TextReplaced += _textDocument.OnTextReplaced;

            var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
            if (defaultStyle != null)
            {
                Style = defaultStyle;
            }
        }

        private int GetTotalCharacters()
        {
            EnsureBufferSynchronized();
            return _buffer.Length;
        }

        TextInputOptions ITextInputClient.GetTextInputOptions()
        {
            InputScopeNameValue scope = InputScope.Names.Count == 0
                ? InputScopeNameValue.Default
                : InputScope.Names[0].NameValue;
            Matrix4x4 matrix = GetGlobalTransformMatrix();
            Vector2 origin = Vector2.Transform(Vector2.Zero, matrix);
            Vector2 end = Vector2.Transform(Size, matrix);
            return new TextInputOptions(
                scope,
                "enter",
                CharacterCasing == CharacterCasing.Upper ? "characters" : "none",
                IsSpellCheckEnabled,
                false,
                AcceptsReturn,
                Text,
                SelectionStart,
                SelectionLength,
                new Rect(origin.X, origin.Y, Math.Max(1f, end.X - origin.X), Math.Max(1f, end.Y - origin.Y)));
        }

        void ITextInputClient.OnTextInput(TextInputRoutedEventArgs args)
        {
            if (!IsEnabled || !IsFocused || IsReadOnly) return;
            switch (args.Kind)
            {
                case TextInputEventKind.InsertText:
                    if (!string.IsNullOrEmpty(args.Text))
                    {
                        InsertText(args.Text, applyCharacterCasing: true);
                    }
                    break;
                case TextInputEventKind.DeleteContentBackward:
                    DeleteFromSoftwareKeyboard(backward: true);
                    break;
                case TextInputEventKind.DeleteContentForward:
                    DeleteFromSoftwareKeyboard(backward: false);
                    break;
                case TextInputEventKind.InsertLineBreak:
                    if (AcceptsReturn)
                    {
                        InsertText("\n");
                    }
                    break;
                case TextInputEventKind.CompositionStarted:
                    BeginComposition();
                    break;
                case TextInputEventKind.CompositionUpdated:
                    UpdateComposition(args.Text, completed: false);
                    break;
                case TextInputEventKind.CompositionCompleted:
                    UpdateComposition(args.Text, completed: true);
                    break;
                case TextInputEventKind.CompositionCanceled:
                    CancelComposition();
                    break;
                case TextInputEventKind.ReplaceText:
                    SelectionStart = Math.Clamp(args.ReplacementStart, 0, Text.Length);
                    SelectionLength = Math.Clamp(args.ReplacementLength, 0, Text.Length - SelectionStart);
                    InsertText(args.Text);
                    SelectionStart = Math.Clamp(args.SelectionStart, 0, Text.Length);
                    SelectionLength = Math.Clamp(args.SelectionLength, 0, Text.Length - SelectionStart);
                    break;
                case TextInputEventKind.SelectionChanged:
                    SelectionStart = Math.Clamp(args.SelectionStart, 0, Text.Length);
                    SelectionLength = Math.Clamp(args.SelectionLength, 0, Text.Length - SelectionStart);
                    break;
                case TextInputEventKind.Paste:
                    PasteFromClipboard();
                    break;
            }
            args.Handled = true;
        }

        private void DeleteFromSoftwareKeyboard(bool backward)
        {
            if (SelectionLength > 0)
            {
                SaveUndoStateForTextChange(
                    SelectionStart,
                    SelectionStart + SelectionLength,
                    string.Empty);
                DeleteSelection();
                return;
            }
            if (backward && CaretIndex > 0)
            {
                int previous = TextBoundaryHelper.PreviousGraphemeBoundary(Text, CaretIndex);
                SaveUndoStateForTextChange(previous, CaretIndex, string.Empty);
                DeleteCharsRange(previous, CaretIndex - previous);
                CaretIndex = previous;
            }
            else if (!backward && CaretIndex < GetTotalCharacters())
            {
                int next = TextBoundaryHelper.NextGraphemeBoundary(Text, CaretIndex);
                SaveUndoStateForTextChange(CaretIndex, next, string.Empty);
                DeleteCharsRange(CaretIndex, next - CaretIndex);
            }
            SelectionStart = CaretIndex;
            SelectionLength = 0;
        }

        private void BeginComposition()
        {
            if (_compositionStart >= 0) return;
            EnsureBufferSynchronized();
            if (SelectionLength > 0 && _buffer.AnyStyle(SelectionStart, SelectionLength, static style => style.IsProtected))
                return;
            SaveUndoState(includeParagraphFormats:
                DocumentRangeContainsParagraphSeparator(
                    SelectionStart,
                    SelectionStart + SelectionLength));
            _compositionStart = SelectionStart;
            _compositionOriginalText = GetSelectedText();
            int originalLength = SelectionLength;
            TextCompositionStarted?.Invoke(
                this,
                new TextCompositionStartedEventArgs(_compositionStart, originalLength));
            if (SelectionLength > 0) _buffer.Delete(SelectionStart, SelectionLength);
            _compositionLength = 0;
            CaretIndex = _compositionStart;
            SelectionStart = _compositionStart;
            SelectionLength = 0;
            if (originalLength > 0) CommitBufferChange(isContentChanging: true);
        }

        private void UpdateComposition(string? text, bool completed)
        {
            if (_compositionStart < 0) BeginComposition();
            if (_compositionStart < 0) return;
            EnsureBufferSynchronized();
            text ??= string.Empty;
            if (_compositionLength > 0) _buffer.Delete(_compositionStart, _compositionLength);
            if (MaxLength > 0 && text.Length > MaxLength - _buffer.Length)
            {
                int available = Math.Max(0, MaxLength - _buffer.Length);
                int boundary = available == 0 ? 0 : TextBoundaryHelper.PreviousGraphemeBoundary(text, available + 1);
                text = boundary > 0 ? text[..boundary] : string.Empty;
            }
            RichTextStyle style = _buffer.GetStyleAt(
                _compositionStart > 0 ? _compositionStart - 1 : _compositionStart,
                GetDefaultTextStyle());
            if (text.Length > 0) _buffer.Insert(_compositionStart, text, style);
            _compositionLength = text.Length;
            CaretIndex = _compositionStart + _compositionLength;
            SelectionStart = _compositionStart;
            SelectionLength = _compositionLength;
            CommitBufferChange(isContentChanging: true);
            TextCompositionChanged?.Invoke(
                this,
                new TextCompositionChangedEventArgs(_compositionStart, _compositionLength));
            Rect candidateBounds = GetDocumentClientRangeBounds(CaretIndex, CaretIndex);
            CandidateWindowBoundsChanged?.Invoke(
                this,
                new CandidateWindowBoundsChangedEventArgs(new Windows.Foundation.Rect(
                    candidateBounds.X,
                    candidateBounds.Y,
                    candidateBounds.Width,
                    candidateBounds.Height)));
            if (completed)
            {
                int completedStart = _compositionStart;
                int completedLength = _compositionLength;
                CaretIndex = _compositionStart + _compositionLength;
                SelectionStart = CaretIndex;
                SelectionLength = 0;
                _compositionStart = -1;
                _compositionLength = 0;
                _compositionOriginalText = string.Empty;
                TextCompositionEnded?.Invoke(
                    this,
                    new TextCompositionEndedEventArgs(completedStart, completedLength));
            }
        }

        private void CancelComposition()
        {
            if (_compositionStart < 0) return;
            EnsureBufferSynchronized();
            if (_compositionLength > 0) _buffer.Delete(_compositionStart, _compositionLength);
            if (_compositionOriginalText.Length > 0)
            {
                RichTextStyle style = _buffer.GetStyleAt(
                    _compositionStart > 0 ? _compositionStart - 1 : _compositionStart,
                    GetDefaultTextStyle());
                _buffer.Insert(_compositionStart, _compositionOriginalText, style);
            }
            int restoredEnd = _compositionStart + _compositionOriginalText.Length;
            int canceledStart = _compositionStart;
            int restoredLength = _compositionOriginalText.Length;
            CaretIndex = restoredEnd;
            SelectionStart = _compositionStart;
            SelectionLength = _compositionOriginalText.Length;
            _compositionStart = -1;
            _compositionLength = 0;
            _compositionOriginalText = string.Empty;
            CommitBufferChange(isContentChanging: true);
            TextCompositionEnded?.Invoke(
                this,
                new TextCompositionEndedEventArgs(canceledStart, restoredLength));
        }

        internal int GetCharacterIndexAt(float clickX, float clickY, out bool isTrailing)
        {
            List<RichCaretStop> stops = GetVisualCaretStops();
            if (stops.Count == 0)
            {
                isTrailing = false;
                return 0;
            }

            float targetLineY = stops[0].Y;
            float bestLineDist = float.PositiveInfinity;
            float previousLineY = float.NaN;
            for (int index = 0; index < stops.Count; index++)
            {
                RichCaretStop stop = stops[index];
                float lineY = stop.Y;
                if (float.IsFinite(previousLineY) && Math.Abs(lineY - previousLineY) < 3f) continue;
                previousLineY = lineY;
                float distY = clickY < lineY
                    ? lineY - clickY
                    : clickY > lineY + stop.Height
                        ? clickY - (lineY + stop.Height)
                        : 0f;

                if (distY < bestLineDist)
                {
                    bestLineDist = distY;
                    targetLineY = lineY;
                }
            }

            RichCaretStop best = stops[0];
            float bestDistance = float.PositiveInfinity;
            for (int index = 0; index < stops.Count; index++)
            {
                RichCaretStop candidate = stops[index];
                if (Math.Abs(candidate.Y - targetLineY) >= 3f) continue;
                float distance = Math.Abs(clickX - candidate.X);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            isTrailing = best.IsTrailing;
            return Math.Clamp(best.TextPosition, 0, GetTotalCharacters());
        }

        internal (int Start, int End) GetVisibleDocumentRange()
        {
            _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
            int start = int.MaxValue;
            int end = 0;
            float viewportTop = _scrollViewer.VerticalOffset;
            float viewportBottom = viewportTop + Math.Max(0f, _scrollViewer.Size.Y);
            foreach (PositionedRichChar character in _blockView.PositionedChars)
            {
                if (character.Position.Y + character.Info.FontSize < viewportTop ||
                    character.Position.Y > viewportBottom)
                {
                    continue;
                }
                int clusterStart = character.HasShapedAdvance
                    ? character.ClusterStart
                    : character.Info.TextPosition;
                int clusterEnd = clusterStart + (character.HasShapedAdvance
                    ? Math.Max(1, character.ClusterLength)
                    : 1);
                start = Math.Min(start, clusterStart);
                end = Math.Max(end, clusterEnd);
            }
            foreach (RichLogicalCaretAnchor anchor in _blockView.EmptyParagraphCaretAnchors)
            {
                if (anchor.Y + anchor.Height < viewportTop || anchor.Y > viewportBottom) continue;
                start = Math.Min(start, anchor.TextPosition);
                end = Math.Max(end, anchor.TextPosition);
            }
            if (start == int.MaxValue) start = end = Math.Clamp(CaretIndex, 0, GetTotalCharacters());
            return (start, Math.Clamp(end, start, GetTotalCharacters()));
        }

        internal FrameworkElement[] GetDocumentEmbeddedChildren(int start, int end)
        {
            _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
            start = Math.Clamp(start, 0, GetTotalCharacters());
            end = Math.Clamp(end, 0, GetTotalCharacters());
            if (end < start) (start, end) = (end, start);
            var result = new List<FrameworkElement>();
            var seen = new HashSet<FrameworkElement>(ReferenceEqualityComparer.Instance);
            foreach (PositionedRichChar character in _blockView.PositionedChars)
            {
                if (character.Info.EmbeddedElement is not { } child) continue;
                int position = character.Info.TextPosition;
                if (position < start || position >= end || !seen.Add(child)) continue;
                result.Add(child);
            }
            return result.ToArray();
        }

        internal bool TryGetDocumentRangeForChild(FrameworkElement child, out int start, out int end)
        {
            ArgumentNullException.ThrowIfNull(child);
            _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
            foreach (PositionedRichChar character in _blockView.PositionedChars)
            {
                if (!ReferenceEquals(character.Info.EmbeddedElement, child)) continue;
                start = character.Info.TextPosition;
                end = Math.Min(GetTotalCharacters(), start + 1);
                return true;
            }
            start = end = 0;
            return false;
        }

        internal int GetDocumentPositionFromPoint(Windows.Foundation.Point point, bool clientCoordinates)
        {
            Vector2 local = new((float)point.X, (float)point.Y);
            if (!clientCoordinates)
                local = InputSystem.GetLocalPosition(this, local);
            local = InputSystem.GetVisualLocalPosition(this, local);
            return GetCharacterIndexAt(
                local.X - Padding.Left + _scrollViewer.HorizontalOffset,
                local.Y - Padding.Top + _scrollViewer.VerticalOffset,
                out _);
        }

        internal Rect GetDocumentClientRangeBounds(int start, int end)
        {
            _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
            int total = GetTotalCharacters();
            start = Math.Clamp(start, 0, total);
            end = Math.Clamp(end, 0, total);
            if (end < start) (start, end) = (end, start);

            if (start == end)
            {
                RichCaretStop caret = GetCaretStop(start, trailingAffinity: false);
                return new Rect(
                    caret.X + Padding.Left - _scrollViewer.HorizontalOffset,
                    caret.Y + Padding.Top - _scrollViewer.VerticalOffset,
                    1f,
                    caret.Height);
            }

            float left = float.PositiveInfinity;
            float top = float.PositiveInfinity;
            float right = float.NegativeInfinity;
            float bottom = float.NegativeInfinity;
            foreach (PositionedRichChar character in _blockView.PositionedChars)
            {
                int clusterStart = character.HasShapedAdvance
                    ? character.ClusterStart
                    : character.Info.TextPosition;
                int clusterEnd = clusterStart + (character.HasShapedAdvance
                    ? Math.Max(1, character.ClusterLength)
                    : 1);
                if (clusterEnd <= start || clusterStart >= end) continue;
                float advance = GetPositionedCharacterAdvance(character, Font);
                left = Math.Min(left, character.Position.X);
                top = Math.Min(top, character.Position.Y);
                right = Math.Max(right, character.Position.X + Math.Max(1f, advance));
                bottom = Math.Max(bottom, character.Position.Y + character.Info.FontSize);
            }

            if (!float.IsFinite(left))
            {
                RichCaretStop caret = GetCaretStop(start, trailingAffinity: false);
                left = right = caret.X;
                top = caret.Y;
                bottom = caret.Y + caret.Height;
            }
            return new Rect(
                left + Padding.Left - _scrollViewer.HorizontalOffset,
                top + Padding.Top - _scrollViewer.VerticalOffset,
                Math.Max(1f, right - left),
                Math.Max(1f, bottom - top));
        }

        internal Rect[] GetDocumentClientRangeRectangles(int start, int end)
        {
            _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
            int total = GetTotalCharacters();
            start = Math.Clamp(start, 0, total);
            end = Math.Clamp(end, 0, total);
            if (end < start) (start, end) = (end, start);
            if (start == end) return [GetDocumentClientRangeBounds(start, end)];

            var lineGlyphs = new List<List<(Rect Bounds, int BidiLevel)>>();
            foreach (PositionedRichChar character in _blockView.PositionedChars)
            {
                int clusterStart = character.HasShapedAdvance ? character.ClusterStart : character.Info.TextPosition;
                int clusterEnd = clusterStart + (character.HasShapedAdvance ? Math.Max(1, character.ClusterLength) : 1);
                if (clusterEnd <= start || clusterStart >= end || character.Info.IsHidden) continue;
                float advance = GetPositionedCharacterAdvance(character, Font);
                var glyph = new Rect(
                    character.Position.X,
                    character.Position.Y,
                    Math.Max(1f, advance),
                    Math.Max(1f, character.Info.FontSize));
                int lineIndex = -1;
                for (int index = lineGlyphs.Count - 1; index >= 0; index--)
                {
                    if (lineGlyphs[index].Count > 0 && Math.Abs(lineGlyphs[index][0].Bounds.Y - glyph.Y) < 1f)
                    {
                        lineIndex = index;
                        break;
                    }
                }
                if (lineIndex < 0)
                {
                    lineGlyphs.Add([(glyph, character.BidiLevel)]);
                    continue;
                }
                lineGlyphs[lineIndex].Add((glyph, character.BidiLevel));
            }

            if (lineGlyphs.Count == 0) return [GetDocumentClientRangeBounds(start, end)];
            var lines = new List<Rect>(lineGlyphs.Count);
            foreach (List<(Rect Bounds, int BidiLevel)> glyphs in lineGlyphs)
            {
                glyphs.Sort(static (left, right) => left.Bounds.X.CompareTo(right.Bounds.X));
                Rect current = glyphs[0].Bounds;
                int currentBidiLevel = glyphs[0].BidiLevel;
                for (int index = 1; index < glyphs.Count; index++)
                {
                    Rect glyph = glyphs[index].Bounds;
                    int bidiLevel = glyphs[index].BidiLevel;
                    if ((bidiLevel & 1) == (currentBidiLevel & 1) && glyph.X <= current.Right + 1f)
                    {
                        float top = Math.Min(current.Y, glyph.Y);
                        float right = Math.Max(current.Right, glyph.Right);
                        float bottom = Math.Max(current.Bottom, glyph.Bottom);
                        current = new Rect(current.X, top, right - current.X, bottom - top);
                    }
                    else
                    {
                        lines.Add(current);
                        current = glyph;
                        currentBidiLevel = bidiLevel;
                    }
                }
                lines.Add(current);
            }
            lines.Sort(static (left, right) =>
            {
                int vertical = left.Y.CompareTo(right.Y);
                return vertical != 0 ? vertical : left.X.CompareTo(right.X);
            });
            float offsetX = Padding.Left - _scrollViewer.HorizontalOffset;
            float offsetY = Padding.Top - _scrollViewer.VerticalOffset;
            for (int index = 0; index < lines.Count; index++)
            {
                Rect line = lines[index];
                lines[index] = new Rect(line.X + offsetX, line.Y + offsetY, line.Width, line.Height);
            }
            return lines.ToArray();
        }

        internal void GetDocumentVisualLineBounds(int position, out int start, out int end)
        {
            EnsureBufferSynchronized();
            position = Math.Clamp(position, 0, _buffer.Length);
            List<RichCaretStop> stops = GetVisualCaretStops();
            int selected = -1;
            for (int index = 0; index < stops.Count; index++)
            {
                if (stops[index].TextPosition != position) continue;
                if (selected < 0) selected = index;
                if (!stops[index].IsTrailing)
                {
                    selected = index;
                    break;
                }
            }

            if (selected < 0)
            {
                start = _buffer.FindParagraphStart(position);
                end = _buffer.FindParagraphEndIncludingSeparator(position);
                return;
            }

            RichCaretStop reference = stops[selected];
            int first = selected;
            while (first > 0 && AreCaretStopsOnSameLine(reference, stops[first - 1])) first--;
            int last = selected + 1;
            while (last < stops.Count && AreCaretStopsOnSameLine(reference, stops[last])) last++;
            start = int.MaxValue;
            end = int.MinValue;
            for (int index = first; index < last; index++)
            {
                start = Math.Min(start, stops[index].TextPosition);
                end = Math.Max(end, stops[index].TextPosition);
            }
            if (start == int.MaxValue || end == int.MinValue)
            {
                start = end = position;
            }
        }

        internal Rect ClientToScreenBounds(Rect clientBounds)
        {
            Matrix4x4 transform = GetGlobalTransformMatrix();
            Vector2 topLeft = Vector2.Transform(new Vector2(clientBounds.X, clientBounds.Y), transform);
            Vector2 topRight = Vector2.Transform(new Vector2(clientBounds.Right, clientBounds.Y), transform);
            Vector2 bottomLeft = Vector2.Transform(new Vector2(clientBounds.X, clientBounds.Bottom), transform);
            Vector2 bottomRight = Vector2.Transform(new Vector2(clientBounds.Right, clientBounds.Bottom), transform);
            float left = Math.Min(Math.Min(topLeft.X, topRight.X), Math.Min(bottomLeft.X, bottomRight.X));
            float top = Math.Min(Math.Min(topLeft.Y, topRight.Y), Math.Min(bottomLeft.Y, bottomRight.Y));
            float right = Math.Max(Math.Max(topLeft.X, topRight.X), Math.Max(bottomLeft.X, bottomRight.X));
            float bottom = Math.Max(Math.Max(topLeft.Y, topRight.Y), Math.Max(bottomLeft.Y, bottomRight.Y));
            return new Rect(left, top, right - left, bottom - top);
        }

        internal void ScrollDocumentPositionIntoView(int position)
        {
            if (Font == null) return;
            EnsureBufferSynchronized();
            position = Math.Clamp(position, 0, _buffer.Length);
            int blockIndex = FindEditorBlockIndex(position);
            if ((uint)blockIndex < (uint)_editorLayoutDocument.Blocks.Count)
            {
                Block targetBlock = _editorLayoutDocument.Blocks[blockIndex];
                float viewportTop = _scrollViewer.VerticalOffset;
                float viewportBottom = viewportTop + Math.Max(0f, _scrollViewer.Size.Y);
                if (targetBlock.CachedYOffset < viewportTop || targetBlock.CachedYOffset >= viewportBottom)
                {
                    _scrollViewer.VerticalOffset = targetBlock.CachedYOffset;
                    _blockView.RefreshViewportLayout();
                }
            }
            List<RichCaretStop> stops = GetVisualCaretStops();
            if (stops.Count == 0) return;
            RichCaretStop stop = GetCaretStop(stops, position, trailingAffinity: false);
            float viewportHeight = Size.Y - Padding.Vertical;
            if (viewportHeight <= 0f) return;
            if (stop.Y < _scrollViewer.VerticalOffset)
                _scrollViewer.VerticalOffset = stop.Y;
            else if (stop.Y + stop.Height > _scrollViewer.VerticalOffset + viewportHeight)
                _scrollViewer.VerticalOffset = stop.Y + stop.Height - viewportHeight;
        }

        private readonly record struct RichCaretStop(
            int TextPosition,
            bool IsTrailing,
            float X,
            float Y,
            float Height)
        {
            public float CenterY => Y + Height * 0.5f;
        }

        private sealed class RichCaretStopXComparer : IComparer<RichCaretStop>
        {
            public static RichCaretStopXComparer Instance { get; } = new();
            public int Compare(RichCaretStop left, RichCaretStop right) => left.X.CompareTo(right.X);
        }

        private static bool AreCaretStopsOnSameLine(RichCaretStop left, RichCaretStop right) =>
            Math.Abs(left.CenterY - right.CenterY) <=
            Math.Max(1f, Math.Min(left.Height, right.Height) * 0.35f);

        private List<RichCaretStop> GetVisualCaretStops()
        {
            _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
            List<RichCaretStop> stops = _visualCaretStops;
            stops.Clear();
            int neededCapacity = _blockView.PositionedChars.Count * 2 + _blockView.EmptyParagraphCaretAnchors.Count;
            if (stops.Capacity < neededCapacity) stops.Capacity = neededCapacity;
            foreach (var pc in _blockView.PositionedChars)
            {
                float advance = GetPositionedCharacterAdvance(pc, Font);
                if (pc.HasShapedAdvance && advance <= 0f) continue;
                bool isRightToLeft = (pc.BidiLevel & 1) != 0;
                float startX = isRightToLeft ? pc.Position.X + advance : pc.Position.X;
                float endX = isRightToLeft ? pc.Position.X : pc.Position.X + advance;
                int clusterStart = pc.HasShapedAdvance ? pc.ClusterStart : pc.Info.TextPosition;
                int clusterEnd = clusterStart + (pc.HasShapedAdvance ? Math.Max(1, pc.ClusterLength) : 1);
                float height = pc.Info.EmbeddedElement?.DesiredSize.Y ?? pc.Info.FontSize;
                stops.Add(new RichCaretStop(clusterStart, false, startX, pc.Position.Y, height));
                stops.Add(new RichCaretStop(clusterEnd, true, endX, pc.Position.Y, height));
            }

            foreach (RichLogicalCaretAnchor anchor in _blockView.EmptyParagraphCaretAnchors)
            {
                stops.Add(new RichCaretStop(
                    anchor.TextPosition,
                    false,
                    anchor.X,
                    anchor.Y,
                    anchor.Height));
            }

            stops.Sort(static (left, right) =>
            {
                int centerComparison = left.CenterY.CompareTo(right.CenterY);
                return centerComparison != 0 ? centerComparison : left.X.CompareTo(right.X);
            });

            // Font fallback, superscript, and mixed sizes produce different glyph
            // tops on the same baseline. Bucket overlapping centers into a line,
            // then order that complete line by physical X.
            for (int lineStart = 0; lineStart < stops.Count;)
            {
                int lineEnd = lineStart + 1;
                RichCaretStop lineReference = stops[lineStart];
                while (lineEnd < stops.Count && AreCaretStopsOnSameLine(lineReference, stops[lineEnd])) lineEnd++;
                stops.Sort(lineStart, lineEnd - lineStart, RichCaretStopXComparer.Instance);
                lineStart = lineEnd;
            }

            for (int i = stops.Count - 1; i > 0; i--)
            {
                RichCaretStop current = stops[i];
                RichCaretStop previous = stops[i - 1];
                if (Math.Abs(current.X - previous.X) < 0.01f &&
                    Math.Abs(current.Y - previous.Y) < 0.01f &&
                    current.TextPosition == previous.TextPosition)
                {
                    // The leading edge of the next logical character is the canonical
                    // representation of an ordinary same-direction boundary.
                    if (!current.IsTrailing)
                    {
                        stops[i - 1] = current;
                    }
                    stops.RemoveAt(i);
                }
            }

            return stops;
        }

        private RichCaretStop GetCaretStop(int textPosition, bool trailingAffinity)
        {
            List<RichCaretStop> stops = GetVisualCaretStops();
            return GetCaretStop(stops, textPosition, trailingAffinity);
        }

        private RichCaretStop GetCaretStop(List<RichCaretStop> stops, int textPosition, bool trailingAffinity)
        {
            if (stops.Count == 0)
            {
                return new RichCaretStop(0, false, 0f, 0f, FontSize);
            }

            int bestIndex = 0;
            int bestPenalty = int.MaxValue;
            for (int i = 0; i < stops.Count; i++)
            {
                int penalty = Math.Abs(stops[i].TextPosition - textPosition) * 4 +
                    (stops[i].IsTrailing == trailingAffinity ? 0 : 1);
                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    bestIndex = i;
                }
            }

            return stops[bestIndex];
        }

        private void MoveCaretVisually(int direction, bool extendSelection)
        {
            List<RichCaretStop> stops = GetVisualCaretStops();
            if (stops.Count == 0) return;
            if (!extendSelection && CollapseSelectionVisually(stops, direction)) return;

            RichCaretStop current = GetCaretStop(stops, CaretIndex, _caretTrailingAffinity);
            int currentIndex = 0;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < stops.Count; i++)
            {
                float distance = Math.Abs(stops[i].X - current.X) + Math.Abs(stops[i].Y - current.Y) * 1000f;
                if (stops[i].TextPosition != current.TextPosition) distance += 0.25f;
                if (stops[i].IsTrailing != current.IsTrailing) distance += 0.125f;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    currentIndex = i;
                }
            }

            int targetIndex = Math.Clamp(currentIndex + direction, 0, stops.Count - 1);
            RichCaretStop target = stops[targetIndex];
            if (extendSelection) PrepareSelectionAnchorForExtension();

            CaretIndex = target.TextPosition;
            _caretTrailingAffinity = target.IsTrailing;
            if (extendSelection)
            {
                SelectionStart = Math.Min(_selectionAnchor, CaretIndex);
                SelectionLength = Math.Abs(_selectionAnchor - CaretIndex);
            }
            else
            {
                SelectionStart = CaretIndex;
                SelectionLength = 0;
            }
        }

        private void MoveCaretVisuallyByWord(int direction, bool extendSelection)
        {
            List<RichCaretStop> stops = GetVisualCaretStops();
            if (stops.Count == 0) return;
            if (!extendSelection && CollapseSelectionVisually(stops, direction)) return;

            RichCaretStop current = GetCaretStop(stops, CaretIndex, _caretTrailingAffinity);
            int currentIndex = 0;
            float bestDistance = float.PositiveInfinity;
            for (int index = 0; index < stops.Count; index++)
            {
                RichCaretStop candidate = stops[index];
                float distance = Math.Abs(candidate.X - current.X) + Math.Abs(candidate.Y - current.Y) * 1000f;
                if (candidate.TextPosition != current.TextPosition) distance += 0.25f;
                if (candidate.IsTrailing != current.IsTrailing) distance += 0.125f;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                currentIndex = index;
            }

            int step = Math.Sign(direction);
            int targetIndex = step < 0 ? 0 : stops.Count - 1;
            for (int index = currentIndex + step; step != 0 && (uint)index < (uint)stops.Count; index += step)
            {
                if (stops[index].TextPosition == CaretIndex) continue;
                if (!TextBoundaryHelper.IsWordNavigationBoundary(Text, stops[index].TextPosition)) continue;
                targetIndex = index;
                break;
            }

            RichCaretStop target = stops[targetIndex];
            if (extendSelection) PrepareSelectionAnchorForExtension();
            CaretIndex = target.TextPosition;
            _caretTrailingAffinity = target.IsTrailing;
            if (extendSelection)
            {
                SelectionStart = Math.Min(_selectionAnchor, CaretIndex);
                SelectionLength = Math.Abs(_selectionAnchor - CaretIndex);
            }
            else
            {
                SelectionStart = CaretIndex;
                SelectionLength = 0;
            }
        }

        private bool CollapseSelectionVisually(List<RichCaretStop> stops, int direction)
        {
            if (SelectionLength == 0) return false;
            RichCaretStop start = GetCaretStop(stops, SelectionStart, trailingAffinity: false);
            RichCaretStop end = GetCaretStop(stops, SelectionStart + SelectionLength, trailingAffinity: true);
            int startIndex = FindCaretStopIndex(stops, start);
            int endIndex = FindCaretStopIndex(stops, end);
            RichCaretStop target = direction < 0
                ? stops[Math.Min(startIndex, endIndex)]
                : stops[Math.Max(startIndex, endIndex)];
            CaretIndex = target.TextPosition;
            _caretTrailingAffinity = target.IsTrailing;
            SelectionStart = CaretIndex;
            SelectionLength = 0;
            return true;
        }

        private static int FindCaretStopIndex(List<RichCaretStop> stops, RichCaretStop target)
        {
            int bestIndex = 0;
            float bestDistance = float.PositiveInfinity;
            for (int index = 0; index < stops.Count; index++)
            {
                RichCaretStop candidate = stops[index];
                float distance = Math.Abs(candidate.X - target.X) + Math.Abs(candidate.CenterY - target.CenterY) * 1000f;
                if (candidate.TextPosition != target.TextPosition) distance += 0.25f;
                if (candidate.IsTrailing != target.IsTrailing) distance += 0.125f;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestIndex = index;
            }
            return bestIndex;
        }

        private void PrepareSelectionAnchorForExtension()
        {
            _selectionAnchor = SelectionLength == 0
                ? CaretIndex
                : CaretIndex <= SelectionStart
                    ? SelectionStart + SelectionLength
                    : SelectionStart;
        }

        private void ApplyKeyboardParagraphDirection(bool rightToLeft)
        {
            BeginDocumentUndoGroup();
            try
            {
                SaveUndoState(includeParagraphFormats: true);
                ITextParagraphFormat format = _textDocument
                    .GetRange(SelectionStart, SelectionStart + SelectionLength)
                    .ParagraphFormat;
                format.RightToLeft = rightToLeft ? FormatEffect.On : FormatEffect.Off;
                format.Alignment = rightToLeft ? ParagraphAlignment.Right : ParagraphAlignment.Left;
            }
            finally
            {
                EndDocumentUndoGroup();
            }
        }

        private void MoveCaretVertically(int direction, bool extendSelection)
        {
            List<RichCaretStop> stops = GetVisualCaretStops();
            if (stops.Count == 0) return;

            RichCaretStop current = GetCaretStop(stops, CaretIndex, _caretTrailingAffinity);
            List<float> lineYs = _visualLineYs;
            lineYs.Clear();
            foreach (RichCaretStop stop in stops)
            {
                if (lineYs.Count == 0 || Math.Abs(lineYs[^1] - stop.CenterY) >= Math.Max(1f, stop.Height * 0.35f))
                {
                    lineYs.Add(stop.CenterY);
                }
            }

            int currentLine = 0;
            float nearestLineDistance = float.PositiveInfinity;
            for (int i = 0; i < lineYs.Count; i++)
            {
                float distance = Math.Abs(lineYs[i] - current.CenterY);
                if (distance < nearestLineDistance)
                {
                    nearestLineDistance = distance;
                    currentLine = i;
                }
            }

            int targetLine = Math.Clamp(currentLine + direction, 0, lineYs.Count - 1);
            RichCaretStop target = current;
            float bestDistance = float.PositiveInfinity;
            foreach (RichCaretStop candidate in stops)
            {
                if (Math.Abs(candidate.CenterY - lineYs[targetLine]) >= Math.Max(1f, candidate.Height * 0.35f)) continue;
                float distance = Math.Abs(candidate.X - current.X);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    target = candidate;
                }
            }

            if (extendSelection) PrepareSelectionAnchorForExtension();

            CaretIndex = target.TextPosition;
            _caretTrailingAffinity = target.IsTrailing;
            if (extendSelection)
            {
                SelectionStart = Math.Min(_selectionAnchor, CaretIndex);
                SelectionLength = Math.Abs(_selectionAnchor - CaretIndex);
            }
            else
            {
                SelectionStart = CaretIndex;
                SelectionLength = 0;
            }
        }

        internal int MoveDocumentSelectionVertically(int direction, int count, bool extendSelection)
        {
            if (count <= 0 || direction == 0) return 0;
            int original = CaretIndex;
            for (int index = 0; index < count; index++)
            {
                int before = CaretIndex;
                MoveCaretVertically(Math.Sign(direction), extendSelection);
                if (CaretIndex == before) break;
            }
            return CaretIndex - original;
        }

        internal int MoveDocumentSelectionHorizontally(
            int direction,
            int count,
            bool extendSelection,
            bool byWord)
        {
            if (count <= 0 || direction == 0) return 0;
            int moved = 0;
            for (int index = 0; index < count; index++)
            {
                int beforeCaret = CaretIndex;
                bool beforeAffinity = _caretTrailingAffinity;
                int beforeSelectionLength = SelectionLength;
                if (byWord) MoveCaretVisuallyByWord(Math.Sign(direction), extendSelection);
                else MoveCaretVisually(Math.Sign(direction), extendSelection);
                if (CaretIndex == beforeCaret &&
                    _caretTrailingAffinity == beforeAffinity &&
                    SelectionLength == beforeSelectionLength)
                    break;
                moved++;
            }
            return moved;
        }

        internal int MoveDocumentSelectionToLineEdge(bool toEnd, bool extendSelection)
        {
            int beforeCaret = CaretIndex;
            bool beforeAffinity = _caretTrailingAffinity;
            int beforeSelectionLength = SelectionLength;
            MoveCaretToLineEdge(toEnd, extendSelection);
            return CaretIndex != beforeCaret ||
                   _caretTrailingAffinity != beforeAffinity ||
                   SelectionLength != beforeSelectionLength
                ? 1
                : 0;
        }

        private void MoveCaretToLineEdge(bool toEnd, bool extendSelection)
        {
            List<RichCaretStop> stops = GetVisualCaretStops();
            if (stops.Count == 0) return;
            RichCaretStop current = GetCaretStop(stops, CaretIndex, _caretTrailingAffinity);
            int first = 0;
            while (first < stops.Count && !AreCaretStopsOnSameLine(stops[first], current)) first++;
            if (first == stops.Count) return;
            int last = first;
            while (last + 1 < stops.Count && AreCaretStopsOnSameLine(stops[last + 1], current)) last++;

            sbyte paragraphLevel = 0;
            bool foundLevel = false;
            foreach (PositionedRichChar character in _blockView.PositionedChars)
            {
                float characterHeight = character.Info.EmbeddedElement?.DesiredSize.Y ?? character.Info.FontSize;
                var characterStop = new RichCaretStop(0, false, 0f, character.Position.Y, characterHeight);
                if (!AreCaretStopsOnSameLine(characterStop, current)) continue;
                if (!foundLevel || character.BidiLevel < paragraphLevel)
                {
                    paragraphLevel = character.BidiLevel;
                    foundLevel = true;
                }
            }
            bool rightToLeft = (paragraphLevel & 1) != 0;
            int targetIndex = (toEnd ^ rightToLeft) ? last : first;
            RichCaretStop target = stops[targetIndex];
            if (extendSelection) PrepareSelectionAnchorForExtension();
            CaretIndex = target.TextPosition;
            _caretTrailingAffinity = target.IsTrailing;
            if (extendSelection)
            {
                SelectionStart = Math.Min(_selectionAnchor, CaretIndex);
                SelectionLength = Math.Abs(_selectionAnchor - CaretIndex);
            }
            else
            {
                SelectionStart = CaretIndex;
                SelectionLength = 0;
            }
        }

        private static float GetPositionedCharacterAdvance(PositionedRichChar character, TtfFont? defaultFont)
        {
            if (character.HasShapedAdvance)
            {
                return character.ShapedAdvance;
            }

            if (character.Info.EmbeddedElement is { } embeddedElement)
            {
                return embeddedElement.DesiredSize.X + 4f;
            }

            TtfFont? font = character.Info.Font ?? defaultFont;
            if (font is null) return 0f;
            ushort glyphIndex = font.GetGlyphIndex(character.Info.Character);
            return font.GetAdvanceWidth(glyphIndex, character.Info.FontSize);
        }

        public override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                base.OnPointerPressed(e);

                if (e.IsRightButtonPressed)
                {
                    var args = new ContextMenuEventArgs(e.Position.X, e.Position.Y)
                    {
                        OriginalSource = this
                    };
                    ContextMenuOpening?.Invoke(this, args);
                    if (args.Handled)
                    {
                        e.Handled = true;
                        return;
                    }
                }

                Vector2 visualPosition = InputSystem.GetVisualLocalPosition(this, e.Position);
                float clickX = visualPosition.X - Padding.Left + _scrollViewer.HorizontalOffset;
                float clickY = visualPosition.Y - Padding.Top + _scrollViewer.VerticalOffset;

                int bestIdx = GetCharacterIndexAt(clickX, clickY, out _caretTrailingAffinity);

                _selectionAnchor = bestIdx;
                SelectionStart = bestIdx;
                SelectionLength = 0;
                _isDraggingSelection = true;
                InputSystem.CapturePointer(this);
                CaretIndex = bestIdx;
            }
        }

        public override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                base.OnPointerReleased(e);
                InputSystem.ReleasePointerCapture();
                _isDraggingSelection = false;
            }
        }

        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                base.OnPointerMoved(e);
                if (_isDraggingSelection)
                {
                    Vector2 visualPosition = InputSystem.GetVisualLocalPosition(this, e.Position);
                    float clickX = visualPosition.X - Padding.Left + _scrollViewer.HorizontalOffset;
                    float clickY = visualPosition.Y - Padding.Top + _scrollViewer.VerticalOffset;

                    int currentIdx = GetCharacterIndexAt(clickX, clickY, out _caretTrailingAffinity);

                    int start = Math.Min(_selectionAnchor, currentIdx);
                    int length = Math.Abs(_selectionAnchor - currentIdx);
                    SelectionStart = start;
                    SelectionLength = length;
                    CaretIndex = currentIdx;
                }
            }
        }

        public override void OnCharacterReceived(CharacterReceivedRoutedEventArgs e)
        {
            if (IsEnabled && IsFocused && !IsReadOnly)
            {
                // Avoid inserting control characters
                if (char.IsControl(e.Character) && e.Character != '\n' && e.Character != '\r' && e.Character != '\t')
                {
                    return;
                }

                if (!AcceptsReturn && e.Character is '\n' or '\r')
                {
                    e.Handled = true;
                    return;
                }

                InsertText(e.Character.ToString(), applyCharacterCasing: true);
                e.Handled = true;
            }
            base.OnCharacterReceived(e);
        }

        public override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (IsEnabled && IsFocused)
            {
                _pressedKeys.Add(e.Key);

                bool isCtrlOrCmd = _pressedKeys.Contains(Key.ControlLeft) ||
                                   _pressedKeys.Contains(Key.ControlRight) ||
                                   _pressedKeys.Contains(Key.SuperLeft) ||
                                   _pressedKeys.Contains(Key.SuperRight);

                bool isShift = _pressedKeys.Contains(Key.ShiftLeft) ||
                               _pressedKeys.Contains(Key.ShiftRight);

                if (isCtrlOrCmd)
                {
                    if (e.Key is Key.ShiftLeft or Key.ShiftRight && !IsReadOnly)
                    {
                        ApplyKeyboardParagraphDirection(e.Key == Key.ShiftRight);
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.A)
                    {
                        SelectionStart = 0;
                        SelectionLength = GetTotalCharacters();
                        CaretIndex = SelectionLength;
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Z && !IsReadOnly)
                    {
                        Undo();
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Y && !IsReadOnly)
                    {
                        Redo();
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.C)
                    {
                        Copy();
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.X && !IsReadOnly)
                    {
                        Cut();
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.V && !IsReadOnly)
                    {
                        PasteFromClipboard();
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.B && !IsReadOnly &&
                        (DisabledFormattingAccelerators & DisabledFormattingAccelerators.Bold) == 0)
                    {
                        if (SelectionLength > 0 && _selectedTableCells.Length == 0) SaveUndoState();
                        ToggleStyle("bold");
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.I && !IsReadOnly &&
                        (DisabledFormattingAccelerators & DisabledFormattingAccelerators.Italic) == 0)
                    {
                        if (SelectionLength > 0 && _selectedTableCells.Length == 0) SaveUndoState();
                        ToggleStyle("italic");
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.U && !IsReadOnly &&
                        (DisabledFormattingAccelerators & DisabledFormattingAccelerators.Underline) == 0)
                    {
                        if (SelectionLength > 0 && _selectedTableCells.Length == 0) SaveUndoState();
                        ToggleStyle("underline");
                        e.Handled = true;
                        return;
                    }
                }

                if (e.Key == Key.Insert && !isCtrlOrCmd && !isShift)
                {
                    Microsoft.UI.Text.ITextSelection selection = _textDocument.Selection;
                    selection.Options ^= Microsoft.UI.Text.SelectionOptions.Overtype;
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Backspace && !IsReadOnly)
                {
                    if (_selectedTableCells.Length > 0)
                    {
                        DeleteSelection();
                        e.Handled = true;
                        base.OnKeyDown(e);
                        return;
                    }
                    if (_selectedTableCells.Length == 0 && (SelectionLength > 0 || CaretIndex > 0))
                    {
                        int undoStart = SelectionLength > 0
                            ? SelectionStart
                            : isCtrlOrCmd
                                ? FindPreviousWordBoundary(CaretIndex)
                                : TextBoundaryHelper.PreviousGraphemeBoundary(Text, CaretIndex);
                        int undoEnd = SelectionLength > 0
                            ? SelectionStart + SelectionLength
                            : CaretIndex;
                        SaveUndoStateForTextChange(undoStart, undoEnd, string.Empty);
                    }
                    if (SelectionLength > 0)
                    {
                        DeleteSelection();
                        e.Handled = true;
                    }
                    else if (CaretIndex > 0)
                    {
                        if (isCtrlOrCmd)
                        {
                            int prevBoundary = FindPreviousWordBoundary(CaretIndex);
                            int len = CaretIndex - prevBoundary;
                            DeleteCharsRange(prevBoundary, len);
                            CaretIndex = prevBoundary;
                        }
                        else
                        {
                            int previous = TextBoundaryHelper.PreviousGraphemeBoundary(Text, CaretIndex);
                            DeleteCharsRange(previous, CaretIndex - previous);
                            CaretIndex = previous;
                        }
                        _activeTypingStyle = null;
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.Delete && !IsReadOnly)
                {
                    if (_selectedTableCells.Length > 0)
                    {
                        DeleteSelection();
                        e.Handled = true;
                        base.OnKeyDown(e);
                        return;
                    }
                    int total = GetTotalCharacters();
                    if (_selectedTableCells.Length == 0 && (SelectionLength > 0 || CaretIndex < total))
                    {
                        int undoStart = SelectionLength > 0 ? SelectionStart : CaretIndex;
                        int undoEnd = SelectionLength > 0
                            ? SelectionStart + SelectionLength
                            : isCtrlOrCmd
                                ? FindNextWordBoundary(CaretIndex)
                                : TextBoundaryHelper.NextGraphemeBoundary(Text, CaretIndex);
                        SaveUndoStateForTextChange(undoStart, undoEnd, string.Empty);
                    }
                    if (SelectionLength > 0)
                    {
                        DeleteSelection();
                        e.Handled = true;
                    }
                    else if (CaretIndex < total)
                    {
                        if (isCtrlOrCmd)
                        {
                            int nextBoundary = FindNextWordBoundary(CaretIndex);
                            int len = nextBoundary - CaretIndex;
                            DeleteCharsRange(CaretIndex, len);
                        }
                        else
                        {
                            int next = TextBoundaryHelper.NextGraphemeBoundary(Text, CaretIndex);
                            DeleteCharsRange(CaretIndex, next - CaretIndex);
                        }
                        _activeTypingStyle = null;
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.Left)
                {
                    if (isCtrlOrCmd)
                    {
                        MoveCaretVisuallyByWord(-1, isShift);
                        _activeTypingStyle = null;
                        e.Handled = true;
                    }
                    else
                    {
                        MoveCaretVisually(-1, isShift);
                        _activeTypingStyle = null;
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.Right)
                {
                    int total = GetTotalCharacters();
                    if (isCtrlOrCmd)
                    {
                        MoveCaretVisuallyByWord(1, isShift);
                        _activeTypingStyle = null;
                        e.Handled = true;
                    }
                    else
                    {
                        MoveCaretVisually(1, isShift);
                        _activeTypingStyle = null;
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.Up || e.Key == Key.Down)
                {
                    MoveCaretVertically(e.Key == Key.Up ? -1 : 1, isShift);
                    _activeTypingStyle = null;
                    e.Handled = true;
                }
                else if (e.Key == Key.Tab)
                {
                    if (isCtrlOrCmd && !IsReadOnly)
                    {
                        InsertText("\t");
                        e.Handled = true;
                    }
                    else if (TryMoveToAdjacentTableCell(backward: isShift))
                    {
                        _activeTypingStyle = null;
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.Home || e.Key == Key.End)
                {
                    if (isShift) PrepareSelectionAnchorForExtension();
                    if (isCtrlOrCmd)
                    {
                        CaretIndex = e.Key == Key.Home ? 0 : GetTotalCharacters();
                        if (isShift)
                        {
                            SelectionStart = Math.Min(_selectionAnchor, CaretIndex);
                            SelectionLength = Math.Abs(_selectionAnchor - CaretIndex);
                        }
                        else
                        {
                            SelectionStart = CaretIndex;
                            SelectionLength = 0;
                        }
                    }
                    else
                    {
                        MoveCaretToLineEdge(e.Key == Key.End, isShift);
                    }
                    _activeTypingStyle = null;
                    e.Handled = true;
                }
            }
            base.OnKeyDown(e);
        }

        private bool TryMoveToAdjacentTableCell(bool backward)
        {
            EnsureBufferSynchronized();
            int position = Math.Clamp(CaretIndex, 0, _buffer.Length);
            Microsoft.UI.Text.RichParagraphFormatState state = GetDocumentParagraphFormatState(position);
            if (!state.IsTableRow) return false;

            string text = _buffer.GetText();
            int paragraphStart = _buffer.FindParagraphStart(position);
            int paragraphEnd = _buffer.FindParagraphEndIncludingSeparator(position);
            int contentEnd = GetParagraphContentEnd(text, paragraphStart, paragraphEnd);
            List<int> cellStarts = GetNavigableTableCellStarts(text, paragraphStart, contentEnd, state);
            int cellIndex = 0;
            for (int index = 1; index < cellStarts.Count && cellStarts[index] <= position; index++)
                cellIndex = index;

            if (backward)
            {
                if (cellIndex > 0)
                {
                    SetDocumentSelection(cellStarts[cellIndex - 1], cellStarts[cellIndex - 1]);
                    return true;
                }
                if (paragraphStart == 0) return true;
                int previousProbe = paragraphStart - 1;
                Microsoft.UI.Text.RichParagraphFormatState previousState = GetDocumentParagraphFormatState(previousProbe);
                if (!previousState.IsTableRow) return true;
                int previousStart = _buffer.FindParagraphStart(previousProbe);
                int previousEnd = GetParagraphContentEnd(
                    text,
                    previousStart,
                    _buffer.FindParagraphEndIncludingSeparator(previousProbe));
                List<int> previousCells = GetNavigableTableCellStarts(
                    text,
                    previousStart,
                    previousEnd,
                    previousState);
                int target = previousCells[^1];
                SetDocumentSelection(target, target);
                return true;
            }

            if (cellIndex + 1 < cellStarts.Count)
            {
                int target = cellStarts[cellIndex + 1];
                SetDocumentSelection(target, target);
                return true;
            }

            if (paragraphEnd < _buffer.Length)
            {
                Microsoft.UI.Text.RichParagraphFormatState nextState = GetDocumentParagraphFormatState(paragraphEnd);
                if (nextState.IsTableRow)
                {
                    int nextContentEnd = GetParagraphContentEnd(
                        text,
                        paragraphEnd,
                        _buffer.FindParagraphEndIncludingSeparator(paragraphEnd));
                    List<int> nextCells = GetNavigableTableCellStarts(
                        text,
                        paragraphEnd,
                        nextContentEnd,
                        nextState);
                    int target = nextCells[0];
                    SetDocumentSelection(target, target);
                    return true;
                }
            }

            if (IsReadOnly) return true;
            int cellCount = Math.Max(
                cellStarts.Count,
                state.TableCellColumnSpans?.Length ?? 0);
            cellCount = Math.Max(1, cellCount);
            bool hasSeparator = paragraphEnd > contentEnd;
            int insertAt = paragraphEnd;
            string cells = new('\t', cellCount - 1);
            string insertion = hasSeparator ? cells + "\n" : "\n" + cells;
            int newRowStart = insertAt + (hasSeparator ? 0 : 1);

            _textDocument.BeginUndoGroup();
            SaveDocumentUndoState();
            try
            {
                RichTextStyle style = _buffer.GetStyleAt(
                    insertAt > 0 ? insertAt - 1 : insertAt,
                    GetDefaultTextStyle());
                int inserted = ReplaceDocumentRangeWithSpans(
                    insertAt,
                    insertAt,
                    [new RichTextSpan(insertion, style)],
                    checkTextLimit: true);
                if (inserted == insertion.Length)
                {
                    Microsoft.UI.Text.RichParagraphFormatState newRowState = state.Clone();
                    if (newRowState.TableCellVerticalMergeFlags is { } mergeFlags)
                        newRowState.TableCellVerticalMergeFlags = new byte[mergeFlags.Length];
                    ApplyDocumentParagraphFormat(
                        newRowStart,
                        newRowStart + cells.Length,
                        newRowState);
                    SetDocumentSelection(newRowStart, newRowStart);
                }
            }
            finally
            {
                _textDocument.EndUndoGroup();
            }
            return true;
        }

        private readonly record struct EditableTableCell(
            int TableStart,
            RichEditTableCellRange Range,
            byte VerticalMerge);

        private bool TryBuildTableCellSelection(
            int anchorPosition,
            int activePosition,
            out RichEditTableCellRange[] selection)
        {
            selection = Array.Empty<RichEditTableCellRange>();
            if (!TryGetEditableTableCell(anchorPosition, out EditableTableCell anchor) ||
                !TryGetEditableTableCell(activePosition, out EditableTableCell active) ||
                anchor.TableStart != active.TableStart)
            {
                return false;
            }

            List<EditableTableCell> tableCells = GetEditableTableCells(anchor.TableStart);
            int firstRow = Math.Min(anchor.Range.Row, active.Range.Row);
            int lastRow = Math.Max(anchor.Range.Row, active.Range.Row);
            int firstColumn = Math.Min(anchor.Range.Column, active.Range.Column);
            int lastColumn = Math.Max(
                anchor.Range.Column + anchor.Range.ColumnSpan - 1,
                active.Range.Column + active.Range.ColumnSpan - 1);

            selection = tableCells
                .Where(cell =>
                    cell.VerticalMerge < 2 &&
                    cell.Range.Row >= firstRow &&
                    cell.Range.Row <= lastRow &&
                    cell.Range.Column <= lastColumn &&
                    cell.Range.Column + cell.Range.ColumnSpan - 1 >= firstColumn)
                .Select(static cell => cell.Range)
                .OrderBy(static cell => cell.StartPosition)
                .ToArray();
            return selection.Length > 0;
        }

        private bool TryGetEditableTableCell(int position, out EditableTableCell result)
        {
            result = default;
            if (_buffer.Length == 0) return false;
            position = Math.Clamp(position, 0, _buffer.Length);
            int probe = position == _buffer.Length && position > 0 ? position - 1 : position;
            Microsoft.UI.Text.RichParagraphFormatState state = GetDocumentParagraphFormatState(probe);
            if (!state.IsTableRow) return false;

            int rowStart = _buffer.FindParagraphStart(probe);
            int tableStart = rowStart;
            while (tableStart > 0)
            {
                int previousProbe = tableStart - 1;
                if (!GetDocumentParagraphFormatState(previousProbe).IsTableRow) break;
                int previousStart = _buffer.FindParagraphStart(previousProbe);
                if (previousStart >= tableStart) break;
                tableStart = previousStart;
            }

            List<EditableTableCell> cells = GetEditableTableCells(tableStart);
            EditableTableCell? continuation = null;
            foreach (EditableTableCell cell in cells)
            {
                if (position < cell.Range.StartPosition || position > cell.Range.EndPosition) continue;
                if (cell.VerticalMerge < 2)
                {
                    result = cell;
                    return true;
                }
                continuation = cell;
                break;
            }
            if (continuation is not { } mergedContinuation) return false;

            // A vertical continuation is the same semantic cell as the nearest merge
            // start above it at the same logical column.
            for (int index = cells.Count - 1; index >= 0; index--)
            {
                EditableTableCell candidate = cells[index];
                if (candidate.Range.Row >= mergedContinuation.Range.Row ||
                    candidate.Range.Column != mergedContinuation.Range.Column)
                    continue;
                if (candidate.VerticalMerge == 1)
                {
                    result = candidate;
                    return true;
                }
            }
            return false;
        }

        private List<EditableTableCell> GetEditableTableCells(int tableStart)
        {
            string text = _buffer.GetText();
            var cells = new List<EditableTableCell>();
            int rowStart = tableStart;
            int rowIndex = 0;
            while (rowStart <= _buffer.Length)
            {
                int probe = rowStart == _buffer.Length && rowStart > 0 ? rowStart - 1 : rowStart;
                Microsoft.UI.Text.RichParagraphFormatState state = GetDocumentParagraphFormatState(probe);
                if (!state.IsTableRow) break;
                int rowEnd = _buffer.FindParagraphEndIncludingSeparator(probe);
                int contentEnd = GetParagraphContentEnd(text, rowStart, rowEnd);
                List<int> starts = GetTableCellStarts(text, rowStart, contentEnd);
                int logicalColumn = 0;
                for (int cellIndex = 0; cellIndex < starts.Count; cellIndex++)
                {
                    int start = starts[cellIndex];
                    int end = cellIndex + 1 < starts.Count ? starts[cellIndex + 1] - 1 : contentEnd;
                    int span = state.TableCellColumnSpans is { } spans && cellIndex < spans.Length
                        ? Math.Max(1, spans[cellIndex])
                        : 1;
                    byte merge = state.TableCellVerticalMergeFlags is { } flags && cellIndex < flags.Length
                        ? flags[cellIndex]
                        : (byte)0;
                    cells.Add(new EditableTableCell(
                        tableStart,
                        new RichEditTableCellRange(rowIndex, logicalColumn, span, start, Math.Max(start, end)),
                        merge));
                    logicalColumn += span;
                }

                if (rowEnd <= rowStart || rowEnd >= _buffer.Length) break;
                rowStart = rowEnd;
                rowIndex++;
            }
            return cells;
        }

        private string GetSelectedTableCellText()
        {
            var builder = new StringBuilder();
            int previousRow = _selectedTableCells[0].Row;
            for (int index = 0; index < _selectedTableCells.Length; index++)
            {
                RichEditTableCellRange cell = _selectedTableCells[index];
                if (index > 0) builder.Append(cell.Row == previousRow ? '\t' : '\n');
                int length = Math.Max(0, cell.EndPosition - cell.StartPosition);
                if (length > 0) builder.Append(_buffer.GetText(cell.StartPosition, length));
                previousRow = cell.Row;
            }
            return builder.ToString();
        }

        private void ReplaceSelectedTableCells(string replacement)
        {
            RichEditTableCellRange[] cells = _selectedTableCells;
            if (cells.Length == 0) return;
            replacement = replacement.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            string[][] matrix = replacement
                .Split('\n')
                .Select(static row => row.Split('\t'))
                .ToArray();
            int firstRow = cells.Min(static cell => cell.Row);
            int firstColumn = cells.Min(static cell => cell.Column);
            var values = new Dictionary<RichEditTableCellRange, string>(cells.Length);
            int newLength = _buffer.Length;
            foreach (RichEditTableCellRange cell in cells)
            {
                int row = cell.Row - firstRow;
                int column = cell.Column - firstColumn;
                string value = row < matrix.Length && column < matrix[row].Length
                    ? matrix[row][column]
                    : string.Empty;
                int oldLength = Math.Max(0, cell.EndPosition - cell.StartPosition);
                if (oldLength > 0 && _buffer.AnyStyle(cell.StartPosition, oldLength, static style => style.IsProtected))
                    return;
                values[cell] = value;
                newLength = checked(newLength - oldLength + value.Length);
            }
            if (MaxLength > 0 && newLength > MaxLength) return;

            bool changesText = cells.Any(cell =>
            {
                string current = _buffer.GetText(
                    cell.StartPosition,
                    Math.Max(0, cell.EndPosition - cell.StartPosition));
                return !string.Equals(current, values[cell], StringComparison.Ordinal);
            });
            if (!changesText)
            {
                int collapsedCaret = cells[0].StartPosition;
                ClearTableSelectionOverlay();
                SetDocumentSelection(collapsedCaret, collapsedCaret);
                return;
            }

            SaveUndoState(includeParagraphFormats: true);
            RichEditTableCellRange first = cells[0];
            string firstValue = values[first];
            foreach (RichEditTableCellRange cell in cells.OrderByDescending(static cell => cell.StartPosition))
            {
                string value = values[cell];
                RichTextStyle style = _buffer.GetStyleAt(
                    cell.StartPosition > 0 ? cell.StartPosition - 1 : cell.StartPosition,
                    GetDefaultTextStyle());
                _buffer.Replace(
                    cell.StartPosition,
                    Math.Max(0, cell.EndPosition - cell.StartPosition),
                    value.Length == 0 ? Array.Empty<RichTextSpan>() : [new RichTextSpan(value, style)]);
            }

            ClearTableSelectionOverlay();
            int caret = first.StartPosition + firstValue.Length;
            _selectionStart = caret;
            _selectionLength = 0;
            _blockView.SelectionStart = caret;
            _blockView.SelectionLength = 0;
            CaretIndex = caret;
            _activeTypingStyle = null;
            CommitBufferChange(isContentChanging: true);
            SelectionChanged?.Invoke(this, new RoutedEventArgs { OriginalSource = this });
        }

        private void ClearTableSelectionOverlay()
        {
            if (_selectedTableCells.Length == 0 && _blockView.TableSelection is null) return;
            _selectedTableCells = Array.Empty<RichEditTableCellRange>();
            _blockView.TableSelection = null;
            _blockView.InvalidateTextRendering();
        }

        private static List<int> GetTableCellStarts(string text, int start, int end)
        {
            var result = new List<int> { start };
            for (int index = start; index < end; index++)
                if (text[index] == '\t') result.Add(index + 1);
            return result;
        }

        internal (int Start, int End) InsertDocumentTable(
            int start,
            int end,
            int columnCount,
            int rowCount,
            bool autoFit)
        {
            if (columnCount <= 0) throw new ArgumentOutOfRangeException(nameof(columnCount));
            if (rowCount <= 0) throw new ArgumentOutOfRangeException(nameof(rowCount));
            if (columnCount > 1024) throw new ArgumentOutOfRangeException(nameof(columnCount));
            if (rowCount > 1_000_000) throw new ArgumentOutOfRangeException(nameof(rowCount));
            if (IsReadOnly) return (Math.Clamp(start, 0, GetTotalCharacters()), Math.Clamp(end, 0, GetTotalCharacters()));

            EnsureBufferSynchronized();
            start = Math.Clamp(start, 0, _buffer.Length);
            end = Math.Clamp(end, 0, _buffer.Length);
            if (end < start) (start, end) = (end, start);
            if (_buffer.AnyStyle(start, end - start, static style => style.IsProtected)) return (start, end);

            string text = _buffer.GetText();
            int paragraphProbe = start == _buffer.Length && start > 0 ? start - 1 : start;
            int paragraphStart = _buffer.FindParagraphStart(paragraphProbe);
            int endProbe = end == _buffer.Length && end > 0 ? end - 1 : end;
            int endParagraphStart = _buffer.FindParagraphStart(endProbe);
            int endParagraphEnd = _buffer.FindParagraphEndIncludingSeparator(endProbe);
            int endParagraphContentEnd = GetParagraphContentEnd(text, endParagraphStart, endParagraphEnd);
            bool isolateBefore = start > paragraphStart;
            bool isolateAfter = end < endParagraphContentEnd;
            long tableCharacterCount = checked((long)rowCount * (columnCount - 1L) + rowCount - 1L);
            long insertionCharacterCount = tableCharacterCount + (isolateBefore ? 1L : 0L) + (isolateAfter ? 1L : 0L);
            if (insertionCharacterCount > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(rowCount), "The requested table exceeds the document position range.");
            if (MaxLength > 0 && (long)_buffer.Length - (end - start) + insertionCharacterCount > MaxLength)
                return (start, end);
            string row = new('\t', columnCount - 1);
            string tableText = string.Join('\n', Enumerable.Repeat(row, rowCount));
            string insertion = (isolateBefore ? "\n" : string.Empty) +
                tableText +
                (isolateAfter ? "\n" : string.Empty);
            int tableStart = start + (isolateBefore ? 1 : 0);
            int tableEnd = tableStart + tableText.Length;

            Microsoft.UI.Text.RichParagraphFormatState rowFormat =
                GetDocumentParagraphFormatState(paragraphProbe).Clone();
            rowFormat.IsTableRow = true;
            rowFormat.ListType = Microsoft.UI.Text.MarkerType.None;
            rowFormat.ListLevelIndex = 0;
            rowFormat.TableCellPadding = 8f;
            rowFormat.TableBorderThickness = 0.5f;
            rowFormat.TableBorderBrush = new SolidColorBrush(0x000000FF);
            rowFormat.TableCellColumnSpans = Enumerable.Repeat(1, columnCount).ToArray();
            rowFormat.TableCellVerticalMergeFlags = new byte[columnCount];
            rowFormat.TableCellBackgrounds = new Brush?[columnCount];
            float availableWidth = Math.Max(
                1f,
                (_blockView.Size.X > 0f ? _blockView.Size.X : Size.X) - Padding.Horizontal -
                rowFormat.LeftIndent - rowFormat.RightIndent);
            float columnWidth = autoFit ? availableWidth / columnCount : 96f;
            rowFormat.TableCellRightEdges = new float[columnCount];
            for (int column = 0; column < columnCount; column++)
                rowFormat.TableCellRightEdges[column] = columnWidth * (column + 1);

            _textDocument.BeginUndoGroup();
            SaveDocumentUndoState();
            try
            {
                RichTextStyle style = _buffer.GetStyleAt(
                    start > 0 ? start - 1 : start,
                    GetDefaultTextStyle());
                ReplaceDocumentRangeWithSpans(
                    start,
                    end,
                    [new RichTextSpan(insertion, style)]);
                int insertedLength = Math.Min(insertion.Length, Math.Max(0, _buffer.Length - start));
                tableEnd = Math.Min(tableEnd, start + insertedLength);
                int currentRowStart = tableStart;
                for (int rowIndex = 0; rowIndex < rowCount && currentRowStart <= tableEnd; rowIndex++)
                {
                    int currentRowEnd = Math.Min(tableEnd, currentRowStart + row.Length);
                    ApplyDocumentParagraphFormat(currentRowStart, currentRowEnd, rowFormat);
                    currentRowStart = currentRowEnd + 1;
                }
                SetDocumentSelection(tableStart, tableStart);
            }
            finally
            {
                _textDocument.EndUndoGroup();
            }
            return (tableStart, tableEnd);
        }

        private static List<int> GetNavigableTableCellStarts(
            string text,
            int start,
            int end,
            Microsoft.UI.Text.RichParagraphFormatState state)
        {
            List<int> starts = GetTableCellStarts(text, start, end);
            if (state.TableCellVerticalMergeFlags is not { } flags) return starts;
            var navigable = new List<int>(starts.Count);
            for (int index = 0; index < starts.Count; index++)
            {
                if (index >= flags.Length || flags[index] < 2) navigable.Add(starts[index]);
            }
            if (navigable.Count == 0) navigable.Add(start);
            return navigable;
        }

        private static int GetParagraphContentEnd(string text, int start, int end)
        {
            int contentEnd = Math.Clamp(end, start, text.Length);
            if (contentEnd > start && text[contentEnd - 1] == '\n') contentEnd--;
            if (contentEnd > start && text[contentEnd - 1] == '\r') contentEnd--;
            return contentEnd;
        }

        private void DeleteCharsRange(int start, int len)
        {
            if (len <= 0) return;
            EnsureBufferSynchronized();
            if (_buffer.Length == 0) return;

            int idx = Math.Clamp(start, 0, _buffer.Length);
            int count = Math.Clamp(len, 0, _buffer.Length - idx);
            if (count <= 0) return;
            if (_buffer.AnyStyle(idx, count, static style => style.IsProtected)) return;

            _buffer.Delete(idx, count);
            CommitBufferChange(isContentChanging: true);
        }

        private int FindPreviousWordBoundary(int current)
        {
            if (current <= 0) return 0;
            EnsureBufferSynchronized();
            if (_buffer.Length == 0) return 0;

            int idx = Math.Clamp(current - 1, 0, _buffer.Length - 1);
            while (idx > 0 && char.IsWhiteSpace(_buffer[idx])) idx--;
            while (idx > 0 && !char.IsWhiteSpace(_buffer[idx])) idx--;

            return idx > 0 ? idx + 1 : 0;
        }

        private int FindNextWordBoundary(int current)
        {
            EnsureBufferSynchronized();
            if (_buffer.Length == 0 || current >= _buffer.Length) return _buffer.Length;

            int idx = Math.Clamp(current, 0, _buffer.Length - 1);
            while (idx < _buffer.Length && !char.IsWhiteSpace(_buffer[idx])) idx++;
            while (idx < _buffer.Length && char.IsWhiteSpace(_buffer[idx])) idx++;

            return idx;
        }

        private List<RichChar> ReadInlinesAsRichChars()
        {
            var list = new List<RichChar>();
            var defaultFg = _blockView.Foreground ?? ThemeManager.GetBrush("TextPrimary", _blockView.ActualTheme);
            foreach (var inline in Inlines)
            {
                _blockView.AccumulateInlines(inline, list, defaultFg, FontSize, false, false, false);
            }
            return list;
        }

        private void OnEditorInlinesChanged(object? sender, EventArgs e)
        {
            if (_publishingBuffer) return;
            _bufferNeedsSync = true;
            EnsureBufferSynchronized();
        }

        private void EnsureBufferSynchronized()
        {
            if (!_bufferNeedsSync) return;
            List<RichChar> characters = ReadInlinesAsRichChars();
            var spans = new List<RichTextSpan>();
            int index = 0;
            while (index < characters.Count)
            {
                int start = index;
                RichTextStyle style = ToTextStyle(characters[index]);
                index++;
                while (index < characters.Count && ToTextStyle(characters[index]).Equals(style))
                {
                    index++;
                }
                var builder = new System.Text.StringBuilder(index - start);
                for (int charIndex = start; charIndex < index; charIndex++)
                {
                    builder.Append(characters[charIndex].Character);
                }
                spans.Add(new RichTextSpan(builder.ToString(), style));
            }
            _buffer.Reset(spans);
            _bufferNeedsSync = false;
            BuildEditorLayoutDocument();
        }

        private RichTextStyle GetDefaultTextStyle() => new(
            Foreground ?? ThemeManager.GetBrush("TextPrimary", ActualTheme),
            FontSize,
            Font);

        private static RichTextStyle ToTextStyle(RichChar character)
        {
            if (character.RetainedStyle is { } retained)
                return retained with { FlowDirection = character.FlowDirection ?? retained.FlowDirection };
            return new RichTextStyle(
                character.Foreground,
                character.FontSize,
                character.Font,
                character.IsBold,
                character.IsItalic,
                character.IsUnderline,
                character.SourceInline is Hyperlink link ? link.Uri : null,
                character.Background,
                character.IsStrikethrough,
                character.CharacterSpacing,
                character.BaselineOffset,
                character.IsHidden,
                character.IsProtected,
                character.IsAllCaps,
                character.IsSmallCaps,
                character.IsOutline,
                character.LanguageTag,
                character.TextScript,
                character.UnderlineType,
                character.FontWeight,
                character.FontStretch,
                character.FontStyle,
                character.Kerning,
                character.FontName,
                character.IsSubscript,
                character.IsSuperscript,
                FlowDirection: character.FlowDirection);
        }

        private static RichChar ToRichChar(RichTextStyle style) => new()
        {
            Character = ' ',
            Foreground = style.Foreground!,
            FontSize = style.FontSize,
            Font = style.Font,
            IsBold = style.IsBold,
            IsItalic = style.IsItalic,
            IsUnderline = style.IsUnderline,
            Background = style.Background,
            IsStrikethrough = style.IsStrikethrough,
            CharacterSpacing = style.CharacterSpacing,
            BaselineOffset = style.BaselineOffset,
            IsHidden = style.IsHidden,
            IsProtected = style.IsProtected,
            IsAllCaps = style.IsAllCaps,
            IsSmallCaps = style.IsSmallCaps,
            IsOutline = style.IsOutline,
            LanguageTag = style.LanguageTag,
            TextScript = style.TextScript,
            UnderlineType = style.UnderlineType,
            FontWeight = style.FontWeight,
            FontStretch = style.FontStretch,
            FontStyle = style.FontStyle,
            Kerning = style.Kerning,
            FontName = style.FontName,
            IsSubscript = style.IsSubscript,
            IsSuperscript = style.IsSuperscript,
            FlowDirection = style.FlowDirection,
            RetainedStyle = style
        };

        private void PublishBuffer()
        {
            if (_displayUpdateDepth > 0)
            {
                _displayUpdatePending = true;
                return;
            }

            PublishBufferCore();
        }

        private void PublishBufferCore()
        {
            var inlines = new List<Inline>(_buffer.Spans.Count);
            for (int index = 0; index < _buffer.Spans.Count; index++)
            {
                RichTextSpan span = _buffer.Spans[index];
                inlines.Add(CreateInline(span.Text, span.Style));
            }

            _publishingBuffer = true;
            try
            {
                Inlines.ReplaceAll(inlines);
            }
            finally
            {
                _publishingBuffer = false;
            }
            _bufferNeedsSync = false;
            BuildEditorLayoutDocument();
            InvalidateMeasure();
            base.Invalidate();
        }

        private void CommitBufferChange(bool isContentChanging)
        {
            TextChanging?.Invoke(this, new RichEditBoxTextChangingEventArgs(isContentChanging));
            PublishBuffer();
            RaiseTextChanged();
        }

        private void RaiseTextChanged()
        {
            if (_displayUpdateDepth > 0)
            {
                _textChangedPending = true;
                return;
            }

            TextChanged?.Invoke(this, new RoutedEventArgs { OriginalSource = this });
        }

        internal int BeginDocumentDisplayUpdates()
        {
            if (_displayUpdateDepth == int.MaxValue) return _displayUpdateDepth;
            return ++_displayUpdateDepth;
        }

        internal int ApplyDocumentDisplayUpdates()
        {
            if (_displayUpdateDepth == 0) return 0;
            _displayUpdateDepth--;
            if (_displayUpdateDepth != 0) return _displayUpdateDepth;

            if (_displayUpdatePending)
            {
                _displayUpdatePending = false;
                PublishBufferCore();
            }

            if (_textChangedPending)
            {
                _textChangedPending = false;
                TextChanged?.Invoke(this, new RoutedEventArgs { OriginalSource = this });
            }

            return 0;
        }

        private void BuildEditorLayoutDocument()
        {
            bool hasChange = _buffer.TryConsumeChange(out RichTextBufferChange change);
            if (hasChange &&
                !change.RequiresFullProjection &&
                _editorBlockStarts.Count == _editorLayoutDocument.Blocks.Count &&
                _editorBlockStarts.Count > 0)
            {
                UpdateEditorLayoutDocument(change);
                return;
            }

            var starts = new List<int>();
            List<Block> blocks = BuildEditorParagraphs(
                _buffer.Spans,
                baseOffset: 0,
                includeTrailingParagraph: true,
                starts,
                ParagraphFormatState);
            IReadOnlyList<Block> previous = _editorLayoutDocument.Blocks;
            int prefix = 0;
            int comparable = Math.Min(previous.Count, blocks.Count);
            while (prefix < comparable && AreEditorBlocksEquivalent(previous[prefix], blocks[prefix]))
            {
                blocks[prefix] = previous[prefix];
                prefix++;
            }

            int suffix = 0;
            while (suffix < comparable - prefix &&
                   AreEditorBlocksEquivalent(previous[previous.Count - 1 - suffix], blocks[blocks.Count - 1 - suffix]))
            {
                blocks[blocks.Count - 1 - suffix] = previous[previous.Count - 1 - suffix];
                suffix++;
            }

            int removeCount = previous.Count - prefix - suffix;
            int insertCount = blocks.Count - prefix - suffix;
            _editorBlockStarts.Clear();
            _editorBlockStarts.AddRange(starts);
            if (removeCount == 0 && insertCount == 0) return;
            var replacement = new List<Block>(insertCount);
            for (int index = 0; index < insertCount; index++) replacement.Add(blocks[prefix + index]);
            _editorLayoutDocument.ReplaceBlockRange(prefix, removeCount, replacement);
        }

        private void UpdateEditorLayoutDocument(RichTextBufferChange change)
        {
            int oldLength = _buffer.Length - change.NewLength + change.OldLength;
            int oldStart = Math.Clamp(change.Start, 0, oldLength);
            int oldLastPosition = change.TextChanged
                ? Math.Clamp(oldStart + change.OldLength, 0, Math.Max(0, oldLength - 1))
                : change.OldLength > 0
                    ? Math.Clamp(oldStart + change.OldLength - 1, 0, Math.Max(0, oldLength - 1))
                    : oldStart;
            int firstBlock = FindEditorBlockIndex(oldStart);
            int lastBlock = FindEditorBlockIndex(oldLastPosition);

            int newStart = _buffer.FindParagraphStart(Math.Clamp(change.Start, 0, _buffer.Length));
            int probe = change.TextChanged
                ? Math.Clamp(change.Start + change.NewLength, 0, _buffer.Length)
                : Math.Clamp(change.Start + Math.Max(0, change.NewLength - 1), 0, _buffer.Length);
            int newEnd = _buffer.FindParagraphEndIncludingSeparator(probe);
            Microsoft.UI.Text.RichParagraphFormatState inheritedParagraphFormat =
                (_editorLayoutDocument.Blocks[firstBlock] as Paragraph)?.EditorFormatState?.Clone() ??
                ParagraphFormatState.Clone();
            RichTextSpan[] spans = _buffer.GetSpans(newStart, newEnd - newStart);
            var replacementStarts = new List<int>();
            List<Block> replacement = BuildEditorParagraphs(
                spans,
                newStart,
                includeTrailingParagraph: newEnd == _buffer.Length,
                replacementStarts,
                inheritedParagraphFormat);

            // The retained blocks still describe the pre-edit document here. Map every
            // rebuilt paragraph start back through the exact buffer delta so deleting a
            // complete paragraph adopts the following paragraph's format instead of the
            // deleted paragraph's format. Starts inside newly inserted text inherit the
            // paragraph at the insertion point; imported formats are applied afterwards.
            for (int index = 0; index < replacement.Count && index < replacementStarts.Count; index++)
            {
                if (replacement[index] is not Paragraph paragraph) continue;
                int replacementStart = replacementStarts[index];
                int oldPosition;
                if (replacementStart < change.Start)
                {
                    oldPosition = replacementStart;
                }
                else if (replacementStart >= change.Start + change.NewLength)
                {
                    oldPosition = replacementStart - (change.NewLength - change.OldLength);
                }
                else
                {
                    oldPosition = change.Start;
                }

                int oldBlockIndex = FindEditorBlockIndex(Math.Clamp(oldPosition, 0, oldLength));
                Microsoft.UI.Text.RichParagraphFormatState mappedFormat =
                    (_editorLayoutDocument.Blocks[oldBlockIndex] as Paragraph)?.EditorFormatState?.Clone() ??
                    inheritedParagraphFormat.Clone();
                paragraph.EditorFormatState = mappedFormat;
                ApplyParagraphFormatState(paragraph, mappedFormat);
            }

            int removeCount = Math.Max(1, lastBlock - firstBlock + 1);
            _editorLayoutDocument.ReplaceBlockRange(firstBlock, removeCount, replacement);
            _editorBlockStarts.RemoveRange(firstBlock, removeCount);
            _editorBlockStarts.InsertRange(firstBlock, replacementStarts);
            int delta = change.NewLength - change.OldLength;
            for (int index = firstBlock + replacementStarts.Count; index < _editorBlockStarts.Count; index++)
                _editorBlockStarts[index] += delta;
        }

        private int FindEditorBlockIndex(int position)
        {
            int low = 0;
            int high = _editorBlockStarts.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                if (_editorBlockStarts[middle] <= position) low = middle + 1;
                else high = middle - 1;
            }
            return Math.Clamp(high, 0, _editorBlockStarts.Count - 1);
        }

        private List<Block> BuildEditorParagraphs(
            IReadOnlyList<RichTextSpan> spans,
            int baseOffset,
            bool includeTrailingParagraph,
            List<int> starts,
            Microsoft.UI.Text.RichParagraphFormatState paragraphFormat)
        {
            var blocks = new List<Block>();
            Paragraph paragraph = CreateEditorParagraph(paragraphFormat);
            int paragraphStart = baseOffset;
            int absolute = baseOffset;
            bool skipLeadingLf = false;
            starts.Add(paragraphStart);
            for (int spanIndex = 0; spanIndex < spans.Count; spanIndex++)
            {
                RichTextSpan span = spans[spanIndex];
                int segmentStart = 0;
                if (skipLeadingLf && span.Text.Length > 0 && span.Text[0] == '\n')
                {
                    segmentStart = 1;
                    skipLeadingLf = false;
                }
                for (int index = segmentStart; index < span.Text.Length; index++)
                {
                    char character = span.Text[index];
                    if (character is not ('\r' or '\n')) continue;
                    if (index > segmentStart)
                        paragraph.Inlines.Add(CreateInline(span.Text[segmentStart..index], span.Style));

                    bool crlf = character == '\r' &&
                        (index + 1 < span.Text.Length
                            ? span.Text[index + 1] == '\n'
                            : spanIndex + 1 < spans.Count && spans[spanIndex + 1].Text.StartsWith('\n'));
                    int separatorLength = crlf ? 2 : 1;
                    paragraph.LogicalTextSeparatorLength = separatorLength;
                    blocks.Add(paragraph);
                    paragraphStart = absolute + index + separatorLength;
                    starts.Add(paragraphStart);
                    paragraph = CreateEditorParagraph(paragraphFormat);
                    if (crlf)
                    {
                        if (index + 1 < span.Text.Length) index++;
                        else skipLeadingLf = true;
                    }
                    segmentStart = index + 1;
                }

                if (segmentStart < span.Text.Length)
                    paragraph.Inlines.Add(CreateInline(span.Text[segmentStart..], span.Style));
                absolute += span.Text.Length;
            }

            if (includeTrailingParagraph || paragraph.Inlines.Count > 0)
            {
                paragraph.LogicalTextSeparatorLength = 0;
                blocks.Add(paragraph);
            }
            else if (starts.Count > blocks.Count)
            {
                starts.RemoveAt(starts.Count - 1);
            }
            return blocks;
        }

        private Paragraph CreateEditorParagraph(Microsoft.UI.Text.RichParagraphFormatState format)
        {
            Microsoft.UI.Text.RichParagraphFormatState state = format.Clone();
            var paragraph = new Paragraph { EditorFormatState = state };
            ApplyParagraphFormatState(paragraph, state);
            return paragraph;
        }

        private void ApplyParagraphFormatState(
            Paragraph paragraph,
            Microsoft.UI.Text.RichParagraphFormatState state)
        {
            FlowDirection? flowDirection = state.RightToLeft switch
            {
                Microsoft.UI.Text.FormatEffect.On => FlowDirection.RightToLeft,
                Microsoft.UI.Text.FormatEffect.Off => FlowDirection.LeftToRight,
                _ => null
            };
            TextAlignment alignment = state.Alignment switch
            {
                Microsoft.UI.Text.ParagraphAlignment.Center => TextAlignment.Center,
                Microsoft.UI.Text.ParagraphAlignment.Right => TextAlignment.Right,
                Microsoft.UI.Text.ParagraphAlignment.Justify => TextAlignment.Justify,
                Microsoft.UI.Text.ParagraphAlignment.Left => TextAlignment.Left,
                _ => ResolveEffectiveTextAlignment()
            };
            paragraph.ApplyEditorFormat(
                state,
                alignment,
                flowDirection,
                state.Alignment != Microsoft.UI.Text.ParagraphAlignment.Undefined);
        }

        internal Microsoft.UI.Text.RichParagraphFormatState GetDocumentParagraphFormatState(int position)
        {
            EnsureBufferSynchronized();
            if (_editorLayoutDocument.Blocks.Count == 0) return ParagraphFormatState;
            int blockIndex = FindEditorBlockIndex(Math.Clamp(position, 0, _buffer.Length));
            return (_editorLayoutDocument.Blocks[blockIndex] as Paragraph)?.EditorFormatState ?? ParagraphFormatState;
        }

        internal RichTextRtfCodec.ParagraphSpan[] GetDocumentParagraphSpans(int start, int end)
        {
            EnsureBufferSynchronized();
            start = Math.Clamp(start, 0, _buffer.Length);
            end = Math.Clamp(end, 0, _buffer.Length);
            if (end < start) (start, end) = (end, start);
            if (_editorLayoutDocument.Blocks.Count == 0)
            {
                return [new RichTextRtfCodec.ParagraphSpan(
                0,
                0,
                ParagraphFormatState.Clone())
            {
                IsTableRow = ParagraphFormatState.IsTableRow,
                TableCellRightEdges = ParagraphFormatState.TableCellRightEdges is null
                    ? null
                    : (float[])ParagraphFormatState.TableCellRightEdges.Clone()
            }];
            }

            int first = FindEditorBlockIndex(start);
            int last = FindEditorBlockIndex(end > start ? end - 1 : end);
            var result = new List<RichTextRtfCodec.ParagraphSpan>(last - first + 1);
            for (int index = first; index <= last; index++)
            {
                int blockStart = _editorBlockStarts[index];
                int contentEnd = index + 1 < _editorBlockStarts.Count
                    ? Math.Max(blockStart, _editorBlockStarts[index + 1] - 1)
                    : _buffer.Length;
                int selectedStart = Math.Max(start, blockStart);
                int selectedEnd = Math.Max(selectedStart, Math.Min(end, contentEnd));
                Microsoft.UI.Text.RichParagraphFormatState state =
                    (_editorLayoutDocument.Blocks[index] as Paragraph)?.EditorFormatState ?? ParagraphFormatState;
                result.Add(new RichTextRtfCodec.ParagraphSpan(
                    Math.Max(0, selectedStart - start),
                    selectedEnd - selectedStart,
                    state.Clone())
                {
                    IsTableRow = state.IsTableRow,
                    TableCellRightEdges = state.TableCellRightEdges is null
                        ? null
                        : (float[])state.TableCellRightEdges.Clone()
                });
            }
            return result.ToArray();
        }

        internal void ApplyDecodedParagraphFormats(
            int insertionStart,
            IReadOnlyList<RichTextRtfCodec.ParagraphSpan> paragraphs)
        {
            for (int index = 0; index < paragraphs.Count; index++)
            {
                RichTextRtfCodec.ParagraphSpan paragraph = paragraphs[index];
                int start = insertionStart + paragraph.Start;
                Microsoft.UI.Text.RichParagraphFormatState format = paragraph.Format.Clone();
                format.IsTableRow = paragraph.IsTableRow;
                format.TableCellRightEdges = paragraph.TableCellRightEdges is null
                    ? null
                    : (float[])paragraph.TableCellRightEdges.Clone();
                ApplyDocumentParagraphFormat(start, start + paragraph.Length, format);
            }
        }

        internal bool TryGetUniformDocumentParagraphValue<T>(
            int start,
            int end,
            Func<Microsoft.UI.Text.RichParagraphFormatState, T> selector,
            out T value)
        {
            EnsureBufferSynchronized();
            start = Math.Clamp(start, 0, _buffer.Length);
            end = Math.Clamp(end, 0, _buffer.Length);
            if (end < start) (start, end) = (end, start);
            int first = FindEditorBlockIndex(start);
            int last = FindEditorBlockIndex(end > start ? end - 1 : end);
            Microsoft.UI.Text.RichParagraphFormatState firstState =
                (_editorLayoutDocument.Blocks[first] as Paragraph)?.EditorFormatState ?? ParagraphFormatState;
            value = selector(firstState);
            for (int index = first + 1; index <= last; index++)
            {
                Microsoft.UI.Text.RichParagraphFormatState state =
                    (_editorLayoutDocument.Blocks[index] as Paragraph)?.EditorFormatState ?? ParagraphFormatState;
                if (!EqualityComparer<T>.Default.Equals(value, selector(state))) return false;
            }
            return true;
        }

        internal void ApplyDocumentParagraphFormat(
            int start,
            int end,
            Microsoft.UI.Text.RichParagraphFormatState source)
        {
            EnsureBufferSynchronized();
            if (_editorLayoutDocument.Blocks.Count == 0) return;
            start = Math.Clamp(start, 0, _buffer.Length);
            end = Math.Clamp(end, 0, _buffer.Length);
            if (end < start) (start, end) = (end, start);
            int first = FindEditorBlockIndex(start);
            int last = FindEditorBlockIndex(end > start ? end - 1 : end);
            var changed = new List<Block>(last - first + 1);
            for (int index = first; index <= last; index++)
            {
                if (_editorLayoutDocument.Blocks[index] is not Paragraph paragraph) continue;
                Microsoft.UI.Text.RichParagraphFormatState state = paragraph.EditorFormatState ?? ParagraphFormatState.Clone();
                if (!ReferenceEquals(state, source)) state.CopyFrom(source);
                paragraph.EditorFormatState = state;
                ApplyParagraphFormatState(paragraph, state);
                changed.Add(paragraph);
            }
            _editorLayoutDocument.NotifyBlocksChanged(changed);
        }

        internal void OnDocumentParagraphFormatChanged()
        {
            BuildEditorLayoutDocument();
            InvalidateMeasure();
            base.Invalidate();
        }

        internal void OnDocumentDefaultTabStopChanged(float value)
        {
            ParagraphFormatState.DefaultTabStop = value;
            var changed = new List<Block>(_editorLayoutDocument.Blocks.Count);
            foreach (Block block in _editorLayoutDocument.Blocks)
            {
                if (block is not Paragraph paragraph) continue;
                Microsoft.UI.Text.RichParagraphFormatState state =
                    paragraph.EditorFormatState ?? ParagraphFormatState.Clone();
                state.DefaultTabStop = value;
                paragraph.EditorFormatState = state;
                changed.Add(paragraph);
            }
            if (changed.Count > 0) _editorLayoutDocument.NotifyBlocksChanged(changed);
        }

        internal void OnDocumentAlignmentIncludesTrailingWhitespaceChanged(bool value)
        {
            _blockView.AlignmentIncludesTrailingWhitespace = value;
            InvalidateMeasure();
            base.Invalidate();
        }

        internal void OnDocumentIgnoreTrailingCharacterSpacingChanged(bool value)
        {
            _blockView.IgnoreTrailingCharacterSpacing = value;
            InvalidateMeasure();
            base.Invalidate();
        }

        internal void OnDocumentCaretTypeChanged() => base.Invalidate();

        private static Inline CreateInline(string text, RichTextStyle style)
        {
            if (text == "\uFFFC" && style.EmbeddedObject is { } embedded)
            {
                float width = Math.Max(1f, embedded.Width);
                float height = Math.Max(1f, embedded.Height);
                if (embedded.ImageSource is { } imageSource)
                {
                    var image = new Image
                    {
                        Width = width,
                        Height = height,
                        Stretch = Stretch.Uniform,
                        Source = imageSource
                    };
                    return new InlineUIContainer(image);
                }
                var label = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(embedded.AlternateText) ? "Image" : embedded.AlternateText,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(4f),
                    Foreground = new ThemeResourceBrush("TextSecondary")
                };
                var placeholder = new Border
                {
                    Width = width,
                    Height = height,
                    BorderThickness = new Thickness(1f),
                    BorderBrush = new ThemeResourceBrush("TextControlBorderBrush"),
                    Background = new ThemeResourceBrush("ControlFillColorSecondaryBrush"),
                    Child = label
                };
                return new InlineUIContainer(placeholder);
            }
            var run = new Run(text)
            {
                Foreground = style.Foreground,
                FontSize = style.FontSize,
                Font = style.Font,
                RetainedStyle = style
            };
            run.ApplyRetainedFlowDirection(style.FlowDirection);
            Inline element = run;
            if (style.IsBold) element = new Bold(element);
            if (style.IsItalic) element = new Italic(element);
            if (style.IsUnderline) element = new Underline(element);
            if (!string.IsNullOrEmpty(style.Link)) element = new Hyperlink(element) { Uri = style.Link };
            return element;
        }

        private static bool AreEditorBlocksEquivalent(Block left, Block right)
        {
            if (left is not Paragraph leftParagraph || right is not Paragraph rightParagraph) return false;
            if (leftParagraph.EditorFormatState is { } leftState &&
                rightParagraph.EditorFormatState is { } rightState)
            {
                if (!Microsoft.UI.Text.RichEditTextParagraphFormat.StateEquals(leftState, rightState)) return false;
            }
            else if (leftParagraph.EditorFormatState is not null || rightParagraph.EditorFormatState is not null)
            {
                return false;
            }
            if (leftParagraph.MarginBottom != rightParagraph.MarginBottom ||
                leftParagraph.LogicalTextSeparatorLength != rightParagraph.LogicalTextSeparatorLength ||
                leftParagraph.TextAlignment != rightParagraph.TextAlignment ||
                leftParagraph.FirstLineIndent != rightParagraph.FirstLineIndent ||
                leftParagraph.LeftIndent != rightParagraph.LeftIndent ||
                leftParagraph.RightIndent != rightParagraph.RightIndent ||
                leftParagraph.SpaceBefore != rightParagraph.SpaceBefore ||
                leftParagraph.LineSpacing != rightParagraph.LineSpacing ||
                leftParagraph.LineSpacingRule != rightParagraph.LineSpacingRule ||
                leftParagraph.Inlines.Count != rightParagraph.Inlines.Count)
                return false;
            for (int index = 0; index < leftParagraph.Inlines.Count; index++)
                if (!AreEditorInlinesEquivalent(leftParagraph.Inlines[index], rightParagraph.Inlines[index])) return false;
            return true;
        }

        private static bool AreEditorInlinesEquivalent(Inline left, Inline right)
        {
            if (left.GetType() != right.GetType() ||
                !Equals(left.Foreground, right.Foreground) ||
                left.FontSize != right.FontSize ||
                !ReferenceEquals(left.Font, right.Font))
                return false;
            if (left is Run leftRun && right is Run rightRun)
                return string.Equals(leftRun.Text, rightRun.Text, StringComparison.Ordinal) &&
                       Nullable.Equals(leftRun.RetainedStyle, rightRun.RetainedStyle);
            if (left is Hyperlink leftLink && right is Hyperlink rightLink &&
                !string.Equals(leftLink.Uri, rightLink.Uri, StringComparison.Ordinal))
                return false;
            if (left is Span leftSpan && right is Span rightSpan)
            {
                if (leftSpan.Inlines.Count != rightSpan.Inlines.Count) return false;
                for (int index = 0; index < leftSpan.Inlines.Count; index++)
                    if (!AreEditorInlinesEquivalent(leftSpan.Inlines[index], rightSpan.Inlines[index])) return false;
            }
            return true;
        }

        internal string GetDocumentText()
        {
            EnsureBufferSynchronized();
            return _buffer.GetText();
        }

        internal RichTextStyle GetDocumentStyleAt(int position)
        {
            EnsureBufferSynchronized();
            return _buffer.GetStyleAt(position, GetDefaultTextStyle());
        }

        internal void ApplyRtfDocumentDefaults(
            RichTextStyle? style,
            Microsoft.UI.Text.RichParagraphFormatState? paragraphFormat)
        {
            if (style is { } defaultStyle) _activeTypingStyle = defaultStyle;
            if (paragraphFormat is null) return;
            ParagraphFormatState.CopyFrom(paragraphFormat);
        }

        internal int GetDocumentCharacterFormatRunEnd(
            int position,
            Microsoft.UI.Text.TextRangeUnit unit)
        {
            EnsureBufferSynchronized();
            position = Math.Clamp(position, 0, _buffer.Length);
            if (position == _buffer.Length) return position;
            RichTextStyle first = _buffer.GetStyleAt(position, GetDefaultTextStyle());
            int cursor = 0;
            bool found = false;
            for (int index = 0; index < _buffer.Spans.Count; index++)
            {
                RichTextSpan span = _buffer.Spans[index];
                int spanEnd = cursor + span.Text.Length;
                if (!found && position < spanEnd) found = true;
                if (found && !IsSameCharacterFormatUnit(first, span.Style, unit)) return cursor;
                cursor = spanEnd;
            }
            return _buffer.Length;
        }

        private static bool IsSameCharacterFormatUnit(
            RichTextStyle left,
            RichTextStyle right,
            Microsoft.UI.Text.TextRangeUnit unit) => unit switch
            {
                Microsoft.UI.Text.TextRangeUnit.Bold =>
                (left.IsBold || left.FontWeight >= 600) == (right.IsBold || right.FontWeight >= 600),
                Microsoft.UI.Text.TextRangeUnit.Italic =>
                (left.IsItalic || left.FontStyle is Windows.UI.Text.FontStyle.Italic or Windows.UI.Text.FontStyle.Oblique) ==
                (right.IsItalic || right.FontStyle is Windows.UI.Text.FontStyle.Italic or Windows.UI.Text.FontStyle.Oblique),
                Microsoft.UI.Text.TextRangeUnit.Underline =>
                (left.IsUnderline || left.UnderlineType != Microsoft.UI.Text.UnderlineType.None) ==
                (right.IsUnderline || right.UnderlineType != Microsoft.UI.Text.UnderlineType.None),
                Microsoft.UI.Text.TextRangeUnit.Strikethrough => left.IsStrikethrough == right.IsStrikethrough,
                Microsoft.UI.Text.TextRangeUnit.ProtectedText => left.IsProtected == right.IsProtected,
                Microsoft.UI.Text.TextRangeUnit.Link or Microsoft.UI.Text.TextRangeUnit.LinkProtected =>
                string.Equals(left.Link, right.Link, StringComparison.Ordinal),
                Microsoft.UI.Text.TextRangeUnit.SmallCaps => left.IsSmallCaps == right.IsSmallCaps,
                Microsoft.UI.Text.TextRangeUnit.AllCaps => left.IsAllCaps == right.IsAllCaps,
                Microsoft.UI.Text.TextRangeUnit.Hidden => left.IsHidden == right.IsHidden,
                Microsoft.UI.Text.TextRangeUnit.Outline => left.IsOutline == right.IsOutline,
                Microsoft.UI.Text.TextRangeUnit.Subscript => left.IsSubscript == right.IsSubscript,
                Microsoft.UI.Text.TextRangeUnit.Superscript => left.IsSuperscript == right.IsSuperscript,
                _ => left.Equals(right)
            };

        internal RichTextStyle GetDocumentStyleForRange(int start, int end)
            => GetDocumentStyleForRange(start, end, Microsoft.UI.Text.RangeGravity.UIBehavior);

        internal RichTextStyle GetDocumentStyleForRange(
            int start,
            int end,
            Microsoft.UI.Text.RangeGravity gravity)
        {
            if (start == end && _activeTypingStyle is { } typingStyle)
                return typingStyle;
            if (start == end && start > 0 &&
                gravity is Microsoft.UI.Text.RangeGravity.UIBehavior or
                    Microsoft.UI.Text.RangeGravity.Backward or
                    Microsoft.UI.Text.RangeGravity.Outward)
            {
                return GetDocumentStyleAt(start - 1);
            }
            return GetDocumentStyleAt(start);
        }

        internal RichTextSpan[] GetDocumentSpans(int start, int end)
        {
            EnsureBufferSynchronized();
            start = Math.Clamp(start, 0, _buffer.Length);
            end = Math.Clamp(end, 0, _buffer.Length);
            if (end < start) (start, end) = (end, start);
            return _buffer.GetSpans(start, end - start);
        }

        internal string GetDocumentText(int start, int end, Microsoft.UI.Text.TextGetOptions options)
        {
            EnsureBufferSynchronized();
            start = Math.Clamp(start, 0, _buffer.Length);
            end = Math.Clamp(end, 0, _buffer.Length);
            if (end < start) (start, end) = (end, start);
            if ((options & Microsoft.UI.Text.TextGetOptions.IncludeNumbering) == 0)
                return Microsoft.UI.Text.RichEditTextDocument.GetPlainText(
                    _buffer.GetSpans(start, end - start),
                    options);

            var builder = new System.Text.StringBuilder(Math.Max(0, end - start));
            int firstBlock = _editorBlockStarts.Count == 0 ? 0 : FindEditorBlockIndex(start);
            for (int blockIndex = firstBlock; blockIndex < _editorLayoutDocument.Blocks.Count; blockIndex++)
            {
                int blockStart = _editorBlockStarts[blockIndex];
                if (blockStart >= end) break;
                int blockEnd = blockIndex + 1 < _editorBlockStarts.Count
                    ? _editorBlockStarts[blockIndex + 1]
                    : _buffer.Length;
                int segmentStart = Math.Max(start, blockStart);
                int segmentEnd = Math.Min(end, blockEnd);
                if (segmentEnd <= segmentStart) continue;
                if (segmentStart == blockStart &&
                    _editorLayoutDocument.Blocks[blockIndex] is Paragraph { EditorFormatState: { } state } &&
                    TextLayoutEngine.FormatListMarker(state) is { } marker)
                {
                    builder.Append(marker).Append('\t');
                }
                builder.Append(Microsoft.UI.Text.RichEditTextDocument.GetPlainText(
                    _buffer.GetSpans(segmentStart, segmentEnd - segmentStart),
                    options));
            }
            return builder.ToString();
        }

        internal int ReplaceDocumentRangeWithSpans(
            int start,
            int end,
            IReadOnlyList<RichTextSpan> spans,
            bool checkTextLimit = false)
        {
            ArgumentNullException.ThrowIfNull(spans);
            EnsureBufferSynchronized();
            start = Math.Clamp(start, 0, _buffer.Length);
            end = Math.Clamp(end, 0, _buffer.Length);
            if (end < start) (start, end) = (end, start);
            if (_buffer.AnyStyle(start, end - start, static style => style.IsProtected)) return 0;

            if (checkTextLimit && MaxLength > 0)
            {
                int available = Math.Max(0, MaxLength - (_buffer.Length - (end - start)));
                int requested = 0;
                for (int index = 0; index < spans.Count; index++) requested += spans[index].Text.Length;
                if (requested > available) spans = TruncateDocumentSpans(spans, available);
            }

            bool changesParagraphStructure = DocumentRangeContainsParagraphSeparator(start, end);
            if (!changesParagraphStructure)
            {
                for (int index = 0; index < spans.Count; index++)
                {
                    if (!ContainsParagraphSeparator(spans[index].Text)) continue;
                    changesParagraphStructure = true;
                    break;
                }
            }
            SaveUndoState(includeParagraphFormats: changesParagraphStructure);
            int insertedLength = _buffer.Replace(start, end - start, spans);
            CaretIndex = start + insertedLength;
            TryApplySelection(CaretIndex, 0);
            CommitBufferChange(isContentChanging: true);
            return insertedLength;
        }

        private static IReadOnlyList<RichTextSpan> TruncateDocumentSpans(
            IReadOnlyList<RichTextSpan> spans,
            int maximumLength)
        {
            if (maximumLength <= 0) return Array.Empty<RichTextSpan>();
            var result = new List<RichTextSpan>(Math.Min(spans.Count, 8));
            int remaining = maximumLength;
            for (int index = 0; index < spans.Count && remaining > 0; index++)
            {
                RichTextSpan span = spans[index];
                if (span.Text.Length <= remaining)
                {
                    result.Add(span);
                    remaining -= span.Text.Length;
                    continue;
                }
                int boundary = TextBoundaryHelper.PreviousGraphemeBoundary(span.Text, remaining + 1);
                if (boundary > 0) result.Add(new RichTextSpan(span.Text[..boundary], span.Style));
                break;
            }
            return result;
        }

        internal void ReplaceDocumentRange(
            int start,
            int end,
            string? text,
            bool checkTextLimit = false,
            Microsoft.UI.Text.TextSetOptions setOptions = Microsoft.UI.Text.TextSetOptions.None)
        {
            EnsureBufferSynchronized();
            start = Math.Clamp(start, 0, _buffer.Length);
            end = Math.Clamp(end, 0, _buffer.Length);
            if (end < start) (start, end) = (end, start);
            if (_buffer.AnyStyle(start, end - start, static style => style.IsProtected)) return;
            text ??= string.Empty;
            if (checkTextLimit && MaxLength > 0)
            {
                int available = MaxLength - (_buffer.Length - (end - start));
                if (available <= 0) text = string.Empty;
                else if (text.Length > available)
                {
                    int boundary = TextBoundaryHelper.PreviousGraphemeBoundary(text, available + 1);
                    text = boundary > 0 ? text[..boundary] : string.Empty;
                }
            }

            SaveUndoState(includeParagraphFormats:
                DocumentRangeContainsParagraphSeparator(start, end) ||
                ContainsParagraphSeparator(text));
            RichTextStyle style = _buffer.GetStyleAt(start > 0 ? start - 1 : start, GetDefaultTextStyle());
            if ((setOptions & Microsoft.UI.Text.TextSetOptions.Unlink) != 0) style = style with { Link = null };
            if ((setOptions & Microsoft.UI.Text.TextSetOptions.Unhide) != 0) style = style with { IsHidden = false };
            _buffer.Replace(
                start,
                end - start,
                text.Length == 0 ? Array.Empty<RichTextSpan>() : [new RichTextSpan(text, style)]);
            CaretIndex = start + text.Length;
            SelectionStart = CaretIndex;
            SelectionLength = 0;
            CommitBufferChange(isContentChanging: true);
        }

        private bool DocumentRangeContainsParagraphSeparator(int start, int end)
        {
            start = Math.Clamp(start, 0, _buffer.Length);
            end = Math.Clamp(end, start, _buffer.Length);
            for (int index = start; index < end; index++)
                if (_buffer[index] is '\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029') return true;
            return false;
        }

        private void SaveUndoStateForTextChange(int start, int end, string? replacement)
        {
            EnsureBufferSynchronized();
            start = Math.Clamp(start, 0, _buffer.Length);
            end = Math.Clamp(end, 0, _buffer.Length);
            if (end < start) (start, end) = (end, start);
            SaveUndoState(includeParagraphFormats:
                DocumentRangeContainsParagraphSeparator(start, end) ||
                ContainsParagraphSeparator(replacement));
        }

        private static bool ContainsParagraphSeparator(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int index = 0; index < text.Length; index++)
                if (text[index] is '\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029') return true;
            return false;
        }

        internal void SetDocumentStyle(int start, int end, Func<RichTextStyle, RichTextStyle> transform)
        {
            EnsureBufferSynchronized();
            start = Math.Clamp(start, 0, _buffer.Length);
            end = Math.Clamp(end, 0, _buffer.Length);
            if (end < start) (start, end) = (end, start);
            if (end == start)
            {
                RichTextStyle insertionStyle = _buffer.GetStyleAt(
                    start > 0 ? start - 1 : start,
                    GetDefaultTextStyle());
                _activeTypingStyle = transform(insertionStyle);
                _blockView.InvalidateTextRendering();
                return;
            }
            if (_selectedTableCells.Length > 0 &&
                start == SelectionStart &&
                end == SelectionStart + SelectionLength)
            {
                SaveUndoState();
                foreach (RichEditTableCellRange cell in _selectedTableCells)
                {
                    int length = Math.Max(0, cell.EndPosition - cell.StartPosition);
                    if (length > 0) _buffer.SetStyle(cell.StartPosition, length, transform);
                }
                CommitBufferChange(isContentChanging: false);
                return;
            }
            SaveUndoState();
            _buffer.SetStyle(start, end - start, transform);
            CommitBufferChange(isContentChanging: false);
        }

        internal void SetDocumentSelection(int start, int end)
        {
            int length = GetTotalCharacters();
            start = Math.Clamp(start, 0, length);
            end = Math.Clamp(end, 0, length);
            if (TryApplySelection(Math.Min(start, end), Math.Abs(end - start)))
                CaretIndex = end;
        }

        internal bool DocumentCaretTrailingAffinity => _caretTrailingAffinity;

        internal void SetDocumentCaretTrailingAffinity(bool trailing)
        {
            if (_caretTrailingAffinity == trailing) return;
            _caretTrailingAffinity = trailing;
            base.Invalidate();
        }

        internal void SetDocumentSelectionActiveEnd(bool startActive)
        {
            if (SelectionLength == 0) return;
            CaretIndex = startActive ? SelectionStart : SelectionStart + SelectionLength;
            _selectionAnchor = startActive ? SelectionStart + SelectionLength : SelectionStart;
        }

        internal void TypeDocumentText(string text) => InsertText(text);

        internal bool IsDocumentInlineObjectRange(int start, int end)
        {
            EnsureBufferSynchronized();
            if (end < start) (start, end) = (end, start);
            return end - start == 1 &&
                start >= 0 && start < _buffer.Length &&
                _buffer[start] == '\uFFFC' &&
                _buffer.GetStyleAt(start, GetDefaultTextStyle()).EmbeddedObject is not null;
        }

        internal bool CanUndoDocument => _undoStack.Count > 0;
        internal bool CanRedoDocument => _redoStack.Count > 0;

        internal void SaveDocumentUndoState() => SaveUndoState(includeParagraphFormats: true);

        internal void ClearDocumentUndoRedoHistory()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        internal void BeginDocumentUndoGroup()
        {
            if (_undoGroupDepth++ == 0) _undoGroupCaptured = false;
        }

        internal void EndDocumentUndoGroup()
        {
            if (_undoGroupDepth == 0) return;
            _undoGroupDepth--;
            if (_undoGroupDepth == 0) _undoGroupCaptured = false;
        }

        internal void TrimDocumentUndoHistory(uint undoLimit)
        {
            int limit = undoLimit > int.MaxValue ? int.MaxValue : (int)undoLimit;
            if (_undoStack.Count <= limit) return;
            if (limit == 0)
            {
                _undoStack.Clear();
                return;
            }

            UndoState[] states = _undoStack.ToArray();
            _undoStack.Clear();
            for (int index = limit - 1; index >= 0; index--) _undoStack.Push(states[index]);
        }

        public void Copy()
        {
            var args = new TextControlCopyingToClipboardEventArgs();
            CopyingToClipboard?.Invoke(this, args);
            if (args.Handled) return;

            string text = GetSelectedText();
            if (!string.IsNullOrEmpty(text) || _selectedTableCells.Length > 0)
            {
                if (_selectedTableCells.Length > 0)
                    ClipboardHelper.SetText(text);
                else if (ClipboardCopyFormat == RichEditClipboardFormat.AllFormats)
                    ClipboardHelper.SetRichText(
                        text,
                        GetDocumentSpans(SelectionStart, SelectionStart + SelectionLength),
                        GetDocumentParagraphSpans(SelectionStart, SelectionStart + SelectionLength));
                else
                    ClipboardHelper.SetText(text);
            }
        }

        public void Cut()
        {
            if (IsReadOnly) return;
            var args = new TextControlCuttingToClipboardEventArgs();
            CuttingToClipboard?.Invoke(this, args);
            if (args.Handled) return;

            string text = GetSelectedText();
            if (!string.IsNullOrEmpty(text) || _selectedTableCells.Length > 0)
            {
                bool tableSelection = _selectedTableCells.Length > 0;
                if (tableSelection)
                    ClipboardHelper.SetText(text);
                else if (ClipboardCopyFormat == RichEditClipboardFormat.AllFormats)
                    ClipboardHelper.SetRichText(
                        text,
                        GetDocumentSpans(SelectionStart, SelectionStart + SelectionLength),
                        GetDocumentParagraphSpans(SelectionStart, SelectionStart + SelectionLength));
                else
                    ClipboardHelper.SetText(text);
                if (!tableSelection)
                    SaveUndoStateForTextChange(
                        SelectionStart,
                        SelectionStart + SelectionLength,
                        string.Empty);
                DeleteSelection();
            }
        }

        public void PasteFromClipboard()
        {
            if (IsReadOnly) return;
            var args = new TextControlPasteEventArgs();
            Paste?.Invoke(this, args);
            if (args.Handled) return;

            if (_selectedTableCells.Length > 0)
            {
                string tableText = ClipboardHelper.GetText();
                if (!string.IsNullOrEmpty(tableText)) ReplaceSelectedTableCells(tableText);
                return;
            }

            if (ClipboardCopyFormat == RichEditClipboardFormat.AllFormats &&
                ClipboardHelper.TryGetRichText(
                    GetDocumentStyleAt(SelectionStart),
                    out RichTextSpan[] richSpans,
                    out RichTextRtfCodec.ParagraphSpan[] richParagraphs) &&
                richSpans.Length > 0)
            {
                GetTextInputReplacementRange(string.Empty, allowOvertype: false, out int insertionStart, out int insertionEnd);
                ReplaceDocumentRangeWithSpans(
                    insertionStart,
                    insertionEnd,
                    richSpans);
                ApplyDecodedParagraphFormats(insertionStart, richParagraphs);
                return;
            }

            string text = ClipboardHelper.GetText();
            if (!string.IsNullOrEmpty(text))
            {
                InsertText(text, allowOvertype: false);
            }
        }

        public void ToggleStyle(string styleType)
        {
            if (IsReadOnly) return;
            if (SelectionLength == 0 && _selectedTableCells.Length == 0)
            {
                if (_activeTypingStyle == null)
                {
                    EnsureBufferSynchronized();
                    RichTextStyle baseStyle = GetDefaultTextStyle();
                    if (_buffer.Length > 0)
                    {
                        int refIdx = CaretIndex > 0 ? CaretIndex - 1 : 0;
                        baseStyle = _buffer.GetStyleAt(
                            Math.Clamp(refIdx, 0, _buffer.Length - 1),
                            GetDefaultTextStyle());
                    }
                    _activeTypingStyle = baseStyle;
                }

                var ts = _activeTypingStyle.Value;
                if (styleType == "bold") ts = ts with { IsBold = !ts.IsBold };
                else if (styleType == "italic") ts = ts with { IsItalic = !ts.IsItalic };
                else if (styleType == "underline") ts = ts with
                {
                    IsUnderline = !ts.IsUnderline,
                    UnderlineType = ts.IsUnderline ? Microsoft.UI.Text.UnderlineType.None : Microsoft.UI.Text.UnderlineType.Single
                };
                _activeTypingStyle = ts;
                return;
            }

            EnsureBufferSynchronized();
            if (_buffer.Length == 0) return;

            if (_selectedTableCells.Length > 0)
            {
                SaveUndoState();
                bool allSelectedHaveStyle = _selectedTableCells.All(cell =>
                {
                    int length = Math.Max(0, cell.EndPosition - cell.StartPosition);
                    return length == 0 || _buffer.AllStyles(cell.StartPosition, length, style => styleType switch
                    {
                        "bold" => style.IsBold,
                        "italic" => style.IsItalic,
                        "underline" => style.IsUnderline,
                        _ => false
                    });
                });
                bool selectedTargetState = !allSelectedHaveStyle;
                foreach (RichEditTableCellRange cell in _selectedTableCells)
                {
                    int length = Math.Max(0, cell.EndPosition - cell.StartPosition);
                    if (length == 0) continue;
                    _buffer.SetStyle(cell.StartPosition, length, style => styleType switch
                    {
                        "bold" => style with { IsBold = selectedTargetState },
                        "italic" => style with { IsItalic = selectedTargetState },
                        "underline" => style with
                        {
                            IsUnderline = selectedTargetState,
                            UnderlineType = selectedTargetState
                                ? Microsoft.UI.Text.UnderlineType.Single
                                : Microsoft.UI.Text.UnderlineType.None
                        },
                        _ => style
                    });
                }
                CommitBufferChange(isContentChanging: false);
                return;
            }

            int start = Math.Clamp(SelectionStart, 0, _buffer.Length);
            int end = Math.Clamp(SelectionStart + SelectionLength, 0, _buffer.Length);
            if (start >= end) return;

            bool allHaveStyle = _buffer.AllStyles(start, end - start, style => styleType switch
            {
                "bold" => style.IsBold,
                "italic" => style.IsItalic,
                "underline" => style.IsUnderline,
                _ => false
            });

            bool targetState = !allHaveStyle;
            _buffer.SetStyle(start, end - start, style => styleType switch
            {
                "bold" => style with { IsBold = targetState },
                "italic" => style with { IsItalic = targetState },
                "underline" => style with { IsUnderline = targetState },
                _ => style
            });
            CommitBufferChange(isContentChanging: false);
        }

        private void DeleteSelection(bool raiseTextChanged = true)
        {
            if (_selectedTableCells.Length > 0)
            {
                ReplaceSelectedTableCells(string.Empty);
                return;
            }
            if (SelectionLength == 0) return;

            EnsureBufferSynchronized();
            if (_buffer.Length == 0) return;

            int start = Math.Clamp(SelectionStart, 0, _buffer.Length);
            int length = Math.Clamp(SelectionLength, 0, _buffer.Length - start);
            if (length == 0) return;
            if (_buffer.AnyStyle(start, length, static style => style.IsProtected)) return;

            _buffer.Delete(start, length);

            CaretIndex = start;
            SelectionStart = start;
            SelectionLength = 0;

            if (raiseTextChanged) CommitBufferChange(isContentChanging: true);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            float w = WidthConstraint ?? availableSize.X;
            if (float.IsInfinity(w)) w = 200f;
            float h = HeightConstraint ?? availableSize.Y;
            if (float.IsInfinity(h)) h = 120f;
            Vector2 contentSize = new(
                Math.Max(0f, w - Padding.Horizontal),
                Math.Max(0f, h - Padding.Vertical));
            const float headerGap = 6f;
            if (Header is not null)
            {
                _headerPresenter.Measure(contentSize);
                if (HeaderPlacement == ControlHeaderPlacement.Left)
                    contentSize.X = Math.Max(0f, contentSize.X - _headerPresenter.DesiredSize.X - headerGap);
                else
                    contentSize.Y = Math.Max(0f, contentSize.Y - _headerPresenter.DesiredSize.Y - headerGap);
            }
            _scrollViewer.Measure(contentSize);
            return new Vector2(w - Padding.Horizontal, h - Padding.Vertical);
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            const float headerGap = 6f;
            if (Header is null)
            {
                _scrollViewer.Arrange(arrangeRect);
                return;
            }
            if (HeaderPlacement == ControlHeaderPlacement.Left)
            {
                float headerWidth = Math.Min(arrangeRect.Width, _headerPresenter.DesiredSize.X);
                _headerPresenter.Arrange(new Rect(arrangeRect.X, arrangeRect.Y, headerWidth, arrangeRect.Height));
                _scrollViewer.Arrange(new Rect(
                    arrangeRect.X + headerWidth + headerGap,
                    arrangeRect.Y,
                    Math.Max(0f, arrangeRect.Width - headerWidth - headerGap),
                    arrangeRect.Height));
            }
            else
            {
                float headerHeight = Math.Min(arrangeRect.Height, _headerPresenter.DesiredSize.Y);
                _headerPresenter.Arrange(new Rect(arrangeRect.X, arrangeRect.Y, arrangeRect.Width, headerHeight));
                _scrollViewer.Arrange(new Rect(
                    arrangeRect.X,
                    arrangeRect.Y + headerHeight + headerGap,
                    arrangeRect.Width,
                    Math.Max(0f, arrangeRect.Height - headerHeight - headerGap)));
            }
        }

        public override void OnRender(DrawingContext context)
        {
            EnsureBufferSynchronized();
            // 1. Draw background card and border under premium Fluent specs
            Brush bg;
            Pen borderPen;

            var activeTheme = this.ActualTheme;
            bool hasLocalBg = IsPropertySetLocally(BackgroundProperty) || IsPropertySetInStyle(BackgroundProperty);
            bool hasLocalBorder = IsPropertySetLocally(BorderBrushProperty) || IsPropertySetInStyle(BorderBrushProperty);

            if (!IsEnabled)
            {
                bg = hasLocalBg ? (Background ?? ThemeManager.GetBrush("TextControlBackground", activeTheme)) : ThemeManager.GetBrush("TextControlBackground", activeTheme);
                borderPen = hasLocalBorder && BorderBrush != null
                    ? new Pen(BorderBrush, 1f)
                    : ThemeManager.GetPen("TextControlBorderBrush", 1f, activeTheme);
            }
            else if (IsFocused)
            {
                bg = hasLocalBg ? (Background ?? ThemeManager.GetBrush("TextControlBackgroundFocused", activeTheme)) : ThemeManager.GetBrush("TextControlBackgroundFocused", activeTheme);
                borderPen = hasLocalBorder && BorderBrush != null
                    ? new Pen(BorderBrush, 2f)
                    : ThemeManager.GetPen("TextControlBorderBrushFocused", 2f, activeTheme);
            }
            else if (IsPointerOver)
            {
                bg = hasLocalBg ? (Background ?? ThemeManager.GetBrush("TextControlBackgroundPointerOver", activeTheme)) : ThemeManager.GetBrush("TextControlBackgroundPointerOver", activeTheme);
                borderPen = hasLocalBorder && BorderBrush != null
                    ? new Pen(BorderBrush, 1f)
                    : ThemeManager.GetPen("TextControlBorderBrushPointerOver", 1f, activeTheme);
            }
            else
            {
                bg = hasLocalBg ? (Background ?? ThemeManager.GetBrush("TextControlBackground", activeTheme)) : ThemeManager.GetBrush("TextControlBackground", activeTheme);
                borderPen = hasLocalBorder && BorderBrush != null
                    ? new Pen(BorderBrush, 1f)
                    : ThemeManager.GetPen("TextControlBorderBrush", 1f, activeTheme);
            }

            // Draw soft 3D elevation shadows (ambient & penumbra layers)
            if (IsEnabled)
            {
                float shadowR = CornerRadius;

                // Ambient shadow (offset Y=2, very soft, low opacity)
                var ambientRect = new Rect(0, 2, Size.X, Size.Y);
                context.DrawRoundedRectangle(AmbientShadowBrush, null, ambientRect, shadowR);

                // Penumbra shadow (offset Y=1, tighter, slightly higher opacity)
                var penumbraRect = new Rect(0, 1, Size.X, Size.Y);
                context.DrawRoundedRectangle(PenumbraShadowBrush, null, penumbraRect, shadowR);
            }

            context.DrawRoundedRectangle(bg, borderPen, new Rect(Vector2.Zero, Size), CornerRadius);

            base.OnRender(context);

            // Draw caret using modern Segoe Blue active color
            if (IsFocused &&
                !IsReadOnly &&
                Font != null &&
                _textDocument.CaretType != Microsoft.UI.Text.CaretType.Null &&
                (DateTime.Now.Millisecond / 500) % 2 == 0)
            {
                List<RichCaretStop> stops = GetVisualCaretStops();

                Vector2 caretPos = new Vector2(Padding.Left, Padding.Top);
                float caretH = FontSize;
                if (stops.Count > 0)
                {
                    RichCaretStop stop = GetCaretStop(stops, CaretIndex, _caretTrailingAffinity);
                    caretPos = new Vector2(stop.X, stop.Y) + new Vector2(Padding.Left, Padding.Top) -
                        new Vector2(_scrollViewer.HorizontalOffset, _scrollViewer.VerticalOffset);
                    caretH = stop.Height;
                }

                Rect editClip = new Rect(Padding.Left, Padding.Top, Size.X - Padding.Horizontal, Size.Y - Padding.Vertical);
                context.PushClip(editClip);
                Rect caretRect = new Rect(caretPos.X, caretPos.Y, 1.5f, caretH + 2f);
                context.DrawRectangle(ThemeManager.GetBrush("TextControlBorderBrushFocused", activeTheme), null, caretRect);
                context.PopClip();
            }

            if (_buffer.Length == 0 && !string.IsNullOrEmpty(PlaceholderText) && Font != null)
            {
                context.DrawText(
                    PlaceholderText,
                    Font,
                    FontSize,
                    ThemeManager.GetBrush("TextBoxForegroundDisabled", activeTheme),
                    new Vector2(Padding.Left, Padding.Top));
            }
        }
    }

} // namespace Microsoft.UI.Xaml.Controls
