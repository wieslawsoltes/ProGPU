using System;
using System.Linq;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Text;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Text.Bidi;
using ProGPU.Text.Shaping;
using Silk.NET.Input;
using Xunit;

namespace ProGPU.Tests;

public class BidiAndFlowDirectionTests
{
    [Fact]
    public void BidiParagraphResolvesMixedLtrAndRtlRuns()
    {
        BidiParagraph paragraph = BidiParagraph.Resolve("abc אבג", ShapingDirection.LeftToRight);

        Assert.Equal(0, paragraph.ParagraphLevel);
        Assert.Equal(new sbyte[] { 0, 0, 0, 0, 1, 1, 1 }, paragraph.Utf16Levels);
        Assert.Equal(new[] { 0, 1, 2, 3, 6, 5, 4 }, BidiParagraph.GetVisualOrder(paragraph.Utf16Levels));
    }

    [Fact]
    public void BidiParagraphUsesExplicitRtlBaseForLeadingLatin()
    {
        BidiParagraph paragraph = BidiParagraph.Resolve("abc אבג", ShapingDirection.RightToLeft);

        Assert.Equal(1, paragraph.ParagraphLevel);
        Assert.Equal(new sbyte[] { 2, 2, 2, 1, 1, 1, 1 }, paragraph.Utf16Levels);
        Assert.Equal(new[] { 6, 5, 4, 3, 0, 1, 2 }, BidiParagraph.GetVisualOrder(paragraph.Utf16Levels));
    }

    [Fact]
    public void BidiParagraphAppliesInlineDirectionAsAnIsolateWithoutChangingTextLength()
    {
        const string text = "abc";
        var directions = Enumerable.Repeat(ShapingDirection.RightToLeft, text.Length).ToArray();

        // Resolve an ordinary paragraph first to exercise thread-local state reuse.
        _ = BidiParagraph.Resolve("plain", ShapingDirection.LeftToRight);

        BidiParagraph paragraph = BidiParagraph.Resolve(
            text,
            directions,
            ShapingDirection.LeftToRight);

        Assert.Equal(text.Length, paragraph.Utf16Levels.Length);
        Assert.Equal(0, paragraph.ParagraphLevel);
        Assert.All(paragraph.Utf16Levels, level => Assert.True(level >= 2));
        Assert.All(paragraph.Utf16Levels, level => Assert.Equal(0, level & 1));
    }

    [Fact]
    public void BidiParagraphDetectsBaseDirectionAndMapsSurrogatePairs()
    {
        const string text = "אב 😀 abc";
        BidiParagraph paragraph = BidiParagraph.Resolve(text, ShapingDirection.Unspecified);

        Assert.Equal(1, paragraph.ParagraphLevel);
        int emoji = text.IndexOf("😀", StringComparison.Ordinal);
        Assert.Equal(paragraph.Utf16Levels[emoji], paragraph.Utf16Levels[emoji + 1]);
        Assert.Contains(paragraph.Runs, run => run.IsRightToLeft);
        Assert.Contains(paragraph.Runs, run => !run.IsRightToLeft);
    }

    [Fact]
    public void BidiParagraphHonorsIsolatesAndPairedBrackets()
    {
        BidiParagraph paragraph = BidiParagraph.Resolve(
            "אבג \u2066(abc 123)\u2069 דהו",
            ShapingDirection.RightToLeft);

        Assert.Equal(1, paragraph.ParagraphLevel);
        Assert.True(paragraph.Utf16Levels.Max() >= 2);
        Assert.Equal(paragraph.Utf16Levels.Length, "אבג \u2066(abc 123)\u2069 דהו".Length);
    }

    [Fact]
    public void TextLayoutShapesLogicalRunsAndPlacesThemInVisualOrder()
    {
        var layout = new TextLayout(
            "abc אבג",
            InterFontFamily.Regular,
            24f,
            shapingOptions: new TextShapingOptions { Direction = ShapingDirection.LeftToRight });

        Assert.Equal(new[] { 0, 1, 2, 3, 6, 5, 4 }, layout.Glyphs.Select(static glyph => glyph.Cluster));
        Assert.Equal(new sbyte[] { 0, 0, 0, 0, 1, 1, 1 }, layout.Glyphs.Select(static glyph => glyph.BidiLevel));
        Assert.True(layout.Glyphs.Zip(layout.Glyphs.Skip(1), static (left, right) => left.Position.X <= right.Position.X).All(static value => value));
    }

    [Fact]
    public void TextLayoutHitTestingCaretStopsAndSelectionUseShapedBidiClusters()
    {
        var layout = new TextLayout(
            "abc אבג",
            InterFontFamily.Regular,
            24f,
            300f,
            ProGPU.Text.TextAlignment.Left,
            shapingOptions: new TextShapingOptions { Direction = ShapingDirection.LeftToRight });

        IReadOnlyList<TextCaretStop> stops = layout.GetVisualCaretStops();
        Assert.NotEmpty(stops);
        Assert.True(stops.Zip(stops.Skip(1), static (left, right) => left.Position.X <= right.Position.X).All(static ordered => ordered));

        TextRunGlyph hebrewGimel = Assert.Single(layout.Glyphs, static glyph => glyph.Cluster == 6);
        TextBounds gimelBounds = Assert.Single(layout.GetSelectionRectangles(6, 1));
        TextHitTestResult leftHalf = layout.HitTestPoint(new System.Numerics.Vector2(
            gimelBounds.X + gimelBounds.Width * 0.25f,
            gimelBounds.Y + gimelBounds.Height * 0.5f));
        TextHitTestResult rightHalf = layout.HitTestPoint(new System.Numerics.Vector2(
            gimelBounds.X + gimelBounds.Width * 0.75f,
            gimelBounds.Y + gimelBounds.Height * 0.5f));
        Assert.Equal(7, leftHalf.TextPosition);
        Assert.True(leftHalf.IsTrailingHit);
        Assert.Equal(6, rightHalf.TextPosition);
        Assert.False(rightHalf.IsTrailingHit);

        IReadOnlyList<TextBounds> selection = layout.GetSelectionRectangles(4, 3);
        Assert.Single(selection);
        Assert.True(selection[0].Width > hebrewGimel.Glyph.Advance);
    }

    [Fact]
    public void FlowDirectionInheritsSupportsOverrideAndClearValue()
    {
        var root = new Grid();
        var child = new Grid();
        var leaf = new Border();
        root.AddChild(child);
        child.AddChild(leaf);

        root.FlowDirection = FlowDirection.RightToLeft;
        Assert.Equal(FlowDirection.RightToLeft, child.FlowDirection);
        Assert.Equal(FlowDirection.RightToLeft, leaf.FlowDirection);
        Assert.True(child.IsRightToLeftLayout);
        Assert.True(leaf.IsRightToLeftLayout);

        child.FlowDirection = FlowDirection.LeftToRight;
        Assert.Equal(FlowDirection.LeftToRight, leaf.FlowDirection);
        Assert.False(child.IsRightToLeftLayout);
        Assert.False(leaf.IsRightToLeftLayout);

        child.ClearValue(FrameworkElement.FlowDirectionProperty);
        Assert.Equal(FlowDirection.RightToLeft, child.FlowDirection);
        Assert.Equal(FlowDirection.RightToLeft, leaf.FlowDirection);
    }

    [Fact]
    public void RtlParentMirrorsLogicalChildCoordinates()
    {
        var root = new Grid { FlowDirection = FlowDirection.RightToLeft };
        var child = new Border
        {
            Width = 20f,
            Height = 10f,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        root.AddChild(child);

        root.Measure(new System.Numerics.Vector2(200f, 50f));
        root.Arrange(new Rect(0f, 0f, 200f, 50f));

        Assert.Equal(180f, child.Offset.X, 3);
        Assert.Equal(0f, child.Offset.Y, 3);
    }

    [Fact]
    public void RtlCoordinateFrameTransformsPointerAndVisualCoordinatesFromTopRight()
    {
        var root = new Grid
        {
            FlowDirection = FlowDirection.RightToLeft,
            Size = new System.Numerics.Vector2(200f, 50f),
            Offset = new System.Numerics.Vector2(10f, 5f)
        };
        var inheritedRtlChild = new Border
        {
            Size = new System.Numerics.Vector2(20f, 10f),
            Offset = new System.Numerics.Vector2(180f, 0f),
            Background = new ProGPU.Vector.SolidColorBrush(0xFFFFFFFF)
        };
        root.AddChild(inheritedRtlChild);

        Assert.Equal(0f, InputSystem.GetLocalPosition(root, new System.Numerics.Vector2(210f, 5f)).X, 3);
        Assert.Equal(200f, InputSystem.GetLocalPosition(root, new System.Numerics.Vector2(10f, 5f)).X, 3);
        Assert.Equal(0f, InputSystem.GetLocalPosition(inheritedRtlChild, new System.Numerics.Vector2(210f, 5f)).X, 3);
        Assert.Equal(20f, InputSystem.GetLocalPosition(inheritedRtlChild, new System.Numerics.Vector2(190f, 5f)).X, 3);

        var args = new PointerRoutedEventArgs { ScreenPosition = new System.Numerics.Vector2(210f, 5f) };
        Assert.Equal(0f, args.GetCurrentPoint(inheritedRtlChild).Position.X, 3);
        Assert.Equal(0f, inheritedRtlChild.TransformToVisual(root).TransformPoint(System.Numerics.Vector2.Zero).X, 3);

        InputSystem.Current = new WindowInputState { Root = root };
        Assert.Same(inheritedRtlChild, InputSystem.HitTest(new System.Numerics.Vector2(209f, 7f)));
        InputSystem.Current = new WindowInputState();
    }

    [Fact]
    public void LtrOverridePreservesItsLocalFrameInsideRtlParent()
    {
        var root = new Grid
        {
            FlowDirection = FlowDirection.RightToLeft,
            Size = new System.Numerics.Vector2(200f, 50f)
        };
        var ltrChild = new Border
        {
            FlowDirection = FlowDirection.LeftToRight,
            Size = new System.Numerics.Vector2(20f, 10f),
            Offset = new System.Numerics.Vector2(180f, 0f)
        };
        root.AddChild(ltrChild);

        Assert.Equal(0f, InputSystem.GetLocalPosition(ltrChild, new System.Numerics.Vector2(180f, 0f)).X, 3);
        Assert.Equal(20f, ltrChild.TransformToVisual(root).TransformPoint(System.Numerics.Vector2.Zero).X, 3);
        Assert.Equal(0f, ltrChild.TransformToVisual(root).TransformPoint(new System.Numerics.Vector2(20f, 0f)).X, 3);
    }

    [Fact]
    public void FlowDocumentRtlDefaultAlignmentDoesNotOverrideExplicitLeft()
    {
        static FlowDocument Create(bool assignLeft)
        {
            var document = new FlowDocument
            {
                Font = InterFontFamily.Regular,
                FontSize = 18f,
                FlowDirection = FlowDirection.RightToLeft,
                TextReadingOrder = TextReadingOrder.UseFlowDirection,
                ColumnCount = 1,
                Padding = new Thickness(0f)
            };
            if (assignLeft) document.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Left;
            document.Blocks.Add(new Paragraph(new Microsoft.UI.Xaml.Documents.Run("abc")));
            document.Measure(new System.Numerics.Vector2(200f, 60f));
            document.Arrange(new Rect(0f, 0f, 200f, 60f));
            return document;
        }

        static float MinimumX(FlowDocument document)
        {
            var characters = Assert.IsType<System.Collections.Generic.List<PositionedRichChar>>(
                typeof(FlowDocument).GetField("_positionedChars", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(document));
            return characters.Min(static character => character.Position.X);
        }

        Assert.True(MinimumX(Create(assignLeft: false)) > 100f);
        Assert.True(MinimumX(Create(assignLeft: true)) < 1f);
    }

    [Fact]
    public void RunFlowDirectionFeedsSharedLayoutAndRoundTripsHtmlAndRtf()
    {
        var directionalRun = new Microsoft.UI.Xaml.Documents.Run("123")
        {
            FlowDirection = FlowDirection.RightToLeft
        };
        var paragraph = new Paragraph(
            new Microsoft.UI.Xaml.Documents.Run("A "),
            directionalRun,
            new Microsoft.UI.Xaml.Documents.Run(" Z"));
        var document = new RichDocument();
        document.Add(paragraph);
        var block = new RichTextBlock
        {
            Document = document,
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            TextReadingOrder = TextReadingOrder.UseFlowDirection,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Thickness(0f)
        };

        block.Measure(new System.Numerics.Vector2(300f, 60f));
        block.Arrange(new Rect(0f, 0f, 300f, 60f));

        PositionedRichChar[] digits = block.PositionedChars
            .Where(character => ReferenceEquals(character.Info.SourceInline, directionalRun))
            .ToArray();
        Assert.Equal(3, digits.Length);
        Assert.All(digits, character => Assert.True(character.BidiLevel >= 2));

        var context = new RichDocumentImportContext(
            InterFontFamily.Regular,
            InterFontFamily.Regular,
            18f,
            new ProGPU.Vector.ThemeResourceBrush("TextPrimary"),
            ElementTheme.Light);
        byte[] html = HtmlDocumentCodec.Default.Export(document);
        Assert.Contains("dir=\"rtl\"", System.Text.Encoding.UTF8.GetString(html), StringComparison.Ordinal);
        Paragraph htmlParagraph = Assert.IsType<Paragraph>(Assert.Single(HtmlDocumentCodec.Default.Import(html, context).Blocks));
        Microsoft.UI.Xaml.Documents.Run htmlRun = htmlParagraph.Inlines
            .OfType<Microsoft.UI.Xaml.Documents.Run>()
            .Single(run => run.Text == "123");
        Assert.Equal(FlowDirection.RightToLeft, htmlRun.FlowDirection);

        byte[] rtf = RtfDocumentCodec.Default.Export(document);
        Assert.Contains("\\rtlch", System.Text.Encoding.UTF8.GetString(rtf), StringComparison.Ordinal);
        Paragraph rtfParagraph = Assert.IsType<Paragraph>(Assert.Single(RtfDocumentCodec.Default.Import(rtf, context).Blocks));
        Microsoft.UI.Xaml.Documents.Run rtfRun = rtfParagraph.Inlines
            .SelectMany(EnumerateRuns)
            .Single(run => run.Text == "123");
        Assert.Equal(FlowDirection.RightToLeft, rtfRun.FlowDirection);

        static System.Collections.Generic.IEnumerable<Microsoft.UI.Xaml.Documents.Run> EnumerateRuns(Inline inline)
        {
            if (inline is Microsoft.UI.Xaml.Documents.Run run) yield return run;
            if (inline is Span span)
            {
                foreach (Inline child in span.Inlines)
                foreach (Microsoft.UI.Xaml.Documents.Run nested in EnumerateRuns(child))
                    yield return nested;
            }
        }
    }

    [Fact]
    public void DirectionalValueControlsMirrorTheirVisualProgressAndKeyboardSemantics()
    {
        static float ProgressFillX(FlowDirection direction)
        {
            var progress = new ProgressBar
            {
                FlowDirection = direction,
                Value = 25f,
                Size = new System.Numerics.Vector2(200f, 10f)
            };
            var context = new DrawingContext();
            progress.OnRender(context);
            return context.Commands[1].Rect.X;
        }

        static float SliderThumbCenter(FlowDirection direction)
        {
            var chrome = new SliderChrome
            {
                FlowDirection = direction,
                Minimum = 0f,
                Maximum = 100f,
                Value = 25f,
                Size = new System.Numerics.Vector2(200f, 30f)
            };
            var context = new DrawingContext();
            chrome.OnRender(context);
            RenderCommand thumb = context.Commands.Last(command => command.Type == RenderCommandType.DrawRoundedRect);
            return thumb.Rect.X + thumb.Rect.Width * 0.5f;
        }

        static float ToggleThumbCenter(FlowDirection direction)
        {
            var chrome = new ToggleSwitchChrome
            {
                FlowDirection = direction,
                IsOn = true,
                Size = new System.Numerics.Vector2(40f, 20f)
            };
            var context = new DrawingContext();
            chrome.OnRender(context);
            RenderCommand thumb = context.Commands.Last(command => command.Type == RenderCommandType.DrawRoundedRect);
            return thumb.Rect.X + thumb.Rect.Width * 0.5f;
        }

        Assert.Equal(0f, ProgressFillX(FlowDirection.LeftToRight), 3);
        Assert.Equal(150f, ProgressFillX(FlowDirection.RightToLeft), 3);
        Assert.True(SliderThumbCenter(FlowDirection.LeftToRight) < 100f);
        Assert.True(SliderThumbCenter(FlowDirection.RightToLeft) > 100f);
        Assert.True(ToggleThumbCenter(FlowDirection.LeftToRight) > 20f);
        Assert.True(ToggleThumbCenter(FlowDirection.RightToLeft) < 20f);

        var rating = new RatingControl
        {
            FlowDirection = FlowDirection.RightToLeft,
            Value = 2d,
            MaxRating = 5
        };
        InputSystem.Current = new WindowInputState();
        InputSystem.SetFocus(rating);
        InputSystem.InjectKeyDown(Key.Left);
        InputSystem.InjectKeyUp(Key.Left);
        Assert.Equal(3d, rating.Value);
        InputSystem.InjectKeyDown(Key.Right);
        InputSystem.InjectKeyUp(Key.Right);
        Assert.Equal(2d, rating.Value);
        InputSystem.SetFocus(null);
    }

    [Fact]
    public void PasswordRevealGlyphMovesToTheLeadingEdgeInRtl()
    {
        static Rect RevealBounds(FlowDirection direction)
        {
            var password = new PasswordBox
            {
                FlowDirection = direction,
                Size = new System.Numerics.Vector2(200f, 32f),
                Font = InterFontFamily.Regular
            };
            password.OnRender(new DrawingContext());
            var geometry = Assert.IsType<ProGPU.Vector.PathGeometry>(
                typeof(PasswordBox).GetField("_eyelidGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(password));
            Assert.True(geometry.TryGetBounds(out System.Numerics.Vector2 minimum, out System.Numerics.Vector2 maximum));
            return new Rect(minimum, maximum - minimum);
        }

        Assert.True(RevealBounds(FlowDirection.LeftToRight).X > 150f);
        Assert.True(RevealBounds(FlowDirection.RightToLeft).Right < 50f);
    }

    [Fact]
    public void DirectionalPickerAndTreeChromeUsesLogicalLeadingAndTrailingEdges()
    {
        ProGPU.Text.TtfFont? previousPopupFont = PopupService.DefaultFont;
        PopupService.DefaultFont = InterFontFamily.Regular;
        try
        {
            static DrawingContext Render(FrameworkElement element)
            {
                var drawing = new DrawingContext();
                element.OnRender(drawing);
                return drawing;
            }

            var ltrCombo = new ComboBox
            {
                FlowDirection = FlowDirection.LeftToRight,
                Font = InterFontFamily.Regular,
                Size = new System.Numerics.Vector2(180f, 32f)
            };
            var rtlCombo = new ComboBox
            {
                FlowDirection = FlowDirection.RightToLeft,
                Font = InterFontFamily.Regular,
                Size = new System.Numerics.Vector2(180f, 32f)
            };
            DrawingContext ltrComboDrawing = Render(ltrCombo);
            DrawingContext rtlComboDrawing = Render(rtlCombo);
            RenderCommand ltrComboText = ltrComboDrawing.Commands.Single(command => command.Text == ltrCombo.PlaceholderText);
            RenderCommand rtlComboText = rtlComboDrawing.Commands.Single(command => command.Text == rtlCombo.PlaceholderText);
            Assert.Equal(ProGPU.Text.TextAlignment.Left, ltrComboText.TextAlignment);
            Assert.Equal(ProGPU.Text.TextAlignment.Right, rtlComboText.TextAlignment);
            Assert.True(ltrComboText.Rect.X < rtlComboText.Rect.X);
            Assert.True(ltrComboDrawing.Commands.Where(command => command.Type == RenderCommandType.DrawLine).Average(command => command.Position2.X) > 100f);
            Assert.True(rtlComboDrawing.Commands.Where(command => command.Type == RenderCommandType.DrawLine).Average(command => command.Position2.X) < 50f);

            var ltrDate = new DatePicker
            {
                FlowDirection = FlowDirection.LeftToRight,
                Size = new System.Numerics.Vector2(180f, 32f)
            };
            var rtlDate = new DatePicker
            {
                FlowDirection = FlowDirection.RightToLeft,
                Size = new System.Numerics.Vector2(180f, 32f)
            };
            Rect ltrIcon = Render(ltrDate).Commands.Single(command =>
                command.Type == RenderCommandType.DrawRoundedRect && Math.Abs(command.Rect.Width - 13f) < 0.01f).Rect;
            Rect rtlIcon = Render(rtlDate).Commands.Single(command =>
                command.Type == RenderCommandType.DrawRoundedRect && Math.Abs(command.Rect.Width - 13f) < 0.01f).Rect;
            Assert.True(ltrIcon.X > 100f);
            Assert.True(rtlIcon.Right < 50f);

            var ltrCalendar = new CalendarView
            {
                FlowDirection = FlowDirection.LeftToRight,
                SelectedDate = new DateTime(2024, 9, 1),
                Size = new System.Numerics.Vector2(240f, 270f)
            };
            var rtlCalendar = new CalendarView
            {
                FlowDirection = FlowDirection.RightToLeft,
                SelectedDate = new DateTime(2024, 9, 1),
                Size = new System.Numerics.Vector2(240f, 270f)
            };
            Rect ltrSelectedDay = Render(ltrCalendar).Commands.Single(command =>
                command.Type == RenderCommandType.DrawRoundedRect && command.Rect.Y > 70f && command.Brush != null).Rect;
            Rect rtlSelectedDay = Render(rtlCalendar).Commands.Single(command =>
                command.Type == RenderCommandType.DrawRoundedRect && command.Rect.Y > 70f && command.Brush != null).Rect;
            Assert.True(ltrSelectedDay.X < 50f);
            Assert.True(rtlSelectedDay.X > 150f);

            var ltrTreeItem = new TreeViewItem("Parent")
            {
                FlowDirection = FlowDirection.LeftToRight,
                IsSelected = true,
                Size = new System.Numerics.Vector2(200f, 24f)
            };
            ltrTreeItem.Items.Add(new TreeViewItem("Child"));
            var rtlTreeItem = new TreeViewItem("Parent")
            {
                FlowDirection = FlowDirection.RightToLeft,
                IsSelected = true,
                Size = new System.Numerics.Vector2(200f, 24f)
            };
            rtlTreeItem.Items.Add(new TreeViewItem("Child"));
            Rect ltrAccent = Render(ltrTreeItem).Commands.Single(command =>
                command.Type == RenderCommandType.DrawRect && Math.Abs(command.Rect.Width - 2f) < 0.01f).Rect;
            Rect rtlAccent = Render(rtlTreeItem).Commands.Single(command =>
                command.Type == RenderCommandType.DrawRect && Math.Abs(command.Rect.Width - 2f) < 0.01f).Rect;
            Assert.True(ltrAccent.X < 10f);
            Assert.True(rtlAccent.X > 190f);
        }
        finally
        {
            PopupService.DefaultFont = previousPopupFont;
        }
    }

    [Fact]
    public void RtlPanelsArrangeLogicalColumnZeroAndFirstItemFromTheRightEdge()
    {
        static Border Child(float width = 20f) => new()
        {
            Width = width,
            Height = 10f
        };

        var canvas = new Canvas { FlowDirection = FlowDirection.RightToLeft };
        Border canvasChild = Child();
        Canvas.SetLeft(canvasChild, 10f);
        canvas.AddChild(canvasChild);
        canvas.Measure(new System.Numerics.Vector2(200f, 40f));
        canvas.Arrange(new Rect(0f, 0f, 200f, 40f));
        Assert.Equal(170f, canvasChild.Offset.X, 3);

        var grid = new Grid { FlowDirection = FlowDirection.RightToLeft };
        grid.ColumnDefinitions.Add(new GridLength(30f));
        grid.ColumnDefinitions.Add(new GridLength(70f));
        Border gridChild = Child();
        Grid.SetColumn(gridChild, 0);
        grid.AddChild(gridChild);
        grid.Measure(new System.Numerics.Vector2(100f, 40f));
        grid.Arrange(new Rect(0f, 0f, 100f, 40f));
        Assert.Equal(80f, gridChild.Offset.X, 3);

        var stack = new StackPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Orientation = Orientation.Horizontal,
            Spacing = 5f
        };
        Border firstStackChild = Child();
        Border secondStackChild = Child(30f);
        stack.AddChild(firstStackChild);
        stack.AddChild(secondStackChild);
        stack.Measure(new System.Numerics.Vector2(100f, 40f));
        stack.Arrange(new Rect(0f, 0f, 100f, 40f));
        Assert.Equal(80f, firstStackChild.Offset.X, 3);
        Assert.Equal(45f, secondStackChild.Offset.X, 3);

        var dock = new DockPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            LastChildFill = false
        };
        Border dockChild = Child();
        DockPanel.SetDock(dockChild, Dock.Left);
        dock.AddChild(dockChild);
        dock.Measure(new System.Numerics.Vector2(100f, 40f));
        dock.Arrange(new Rect(0f, 0f, 100f, 40f));
        Assert.Equal(80f, dockChild.Offset.X, 3);

        var wrap = new WrapPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Orientation = Orientation.Horizontal,
            ItemWidth = 20f,
            ItemHeight = 10f,
            HorizontalSpacing = 5f
        };
        Border firstWrapChild = Child();
        Border secondWrapChild = Child();
        wrap.AddChild(firstWrapChild);
        wrap.AddChild(secondWrapChild);
        wrap.Measure(new System.Numerics.Vector2(100f, 40f));
        wrap.Arrange(new Rect(0f, 0f, 100f, 40f));
        Assert.Equal(80f, firstWrapChild.Offset.X, 3);
        Assert.Equal(55f, secondWrapChild.Offset.X, 3);
    }

    [Fact]
    public void RtlScrollViewerStartsAtTheRightEdgeAndMirrorsItsScrollbarChrome()
    {
        static (ScrollViewer Viewer, Border Content, DrawingContext Drawing) Create(FlowDirection direction)
        {
            var content = new Border { Width = 300f, Height = 300f };
            var viewer = new ScrollViewer
            {
                FlowDirection = direction,
                Width = 100f,
                Height = 100f,
                Content = content
            };
            viewer.Measure(new System.Numerics.Vector2(100f, 100f));
            viewer.Arrange(new Rect(0f, 0f, 100f, 100f));
            var drawing = new DrawingContext();
            viewer.OnRender(drawing);
            return (viewer, content, drawing);
        }

        var ltr = Create(FlowDirection.LeftToRight);
        var rtl = Create(FlowDirection.RightToLeft);

        Assert.Equal(0f, ltr.Content.Offset.X, 3);
        Assert.Equal(-200f, rtl.Content.Offset.X, 3);
        ltr.Viewer.HorizontalOffset = 50f;
        rtl.Viewer.HorizontalOffset = 50f;
        Assert.Equal(-50f, ltr.Content.LayoutTranslation.X, 3);
        Assert.Equal(50f, rtl.Content.LayoutTranslation.X, 3);

        Rect ltrVerticalTrack = ltr.Drawing.Commands.Single(command =>
            command.Type == RenderCommandType.DrawRect && Math.Abs(command.Rect.Width - 3f) < 0.01f).Rect;
        Rect rtlVerticalTrack = rtl.Drawing.Commands.Single(command =>
            command.Type == RenderCommandType.DrawRect && Math.Abs(command.Rect.Width - 3f) < 0.01f).Rect;
        Assert.True(ltrVerticalTrack.X > 90f);
        Assert.True(rtlVerticalTrack.X < 10f);

        Rect ltrHorizontalThumb = ltr.Drawing.Commands.Single(command =>
            command.Type == RenderCommandType.DrawRoundedRect && Math.Abs(command.Rect.Height - 3f) < 0.01f).Rect;
        Rect rtlHorizontalThumb = rtl.Drawing.Commands.Single(command =>
            command.Type == RenderCommandType.DrawRoundedRect && Math.Abs(command.Rect.Height - 3f) < 0.01f).Rect;
        Assert.True(ltrHorizontalThumb.X < 1f);
        Assert.True(rtlHorizontalThumb.Right > 99f);
    }

    [Fact]
    public void RtlDataGridPlacesLogicalColumnZeroOnThePhysicalRight()
    {
        static DrawingContext Render(FlowDirection direction)
        {
            var grid = new DataGrid
            {
                FlowDirection = direction,
                Font = InterFontFamily.Regular,
                Size = new System.Numerics.Vector2(200f, 100f)
            };
            grid.Columns.Add(new DataGridColumn("First", 80f, "Value"));
            var drawing = new DrawingContext();
            grid.OnRender(drawing);
            return drawing;
        }

        DrawingContext ltr = Render(FlowDirection.LeftToRight);
        DrawingContext rtl = Render(FlowDirection.RightToLeft);
        Rect ltrColumn = ltr.Commands.Single(command =>
            command.Type == RenderCommandType.DrawRect &&
            command.Pen != null &&
            Math.Abs(command.Rect.Width - 80f) < 0.01f &&
            Math.Abs(command.Rect.Height - 32f) < 0.01f).Rect;
        Rect rtlColumn = rtl.Commands.Single(command =>
            command.Type == RenderCommandType.DrawRect &&
            command.Pen != null &&
            Math.Abs(command.Rect.Width - 80f) < 0.01f &&
            Math.Abs(command.Rect.Height - 32f) < 0.01f).Rect;
        RenderCommand rtlHeader = rtl.Commands.Single(command => command.Text == "First");

        Assert.Equal(0f, ltrColumn.X, 3);
        Assert.Equal(120f, rtlColumn.X, 3);
        Assert.Equal(ProGPU.Text.TextAlignment.Right, rtlHeader.TextAlignment);
        Assert.Equal(ProGPU.Text.Shaping.ShapingDirection.RightToLeft, rtlHeader.TextShapingOptions?.Direction);
    }

    [Fact]
    public void RtlTextInputPlaceholdersUseTheSameDefaultAlignmentAsContent()
    {
        var textBox = new TextBox
        {
            FlowDirection = FlowDirection.RightToLeft,
            Font = InterFontFamily.Regular,
            PlaceholderText = "placeholder",
            Size = new System.Numerics.Vector2(200f, 32f)
        };
        var textDrawing = new DrawingContext();
        textBox.OnRender(textDrawing);
        RenderCommand textPlaceholder = textDrawing.Commands.Single(command => command.Text == "placeholder");

        var passwordBox = new PasswordBox
        {
            FlowDirection = FlowDirection.RightToLeft,
            Font = InterFontFamily.Regular,
            PlaceholderText = "password",
            Size = new System.Numerics.Vector2(200f, 32f)
        };
        var passwordDrawing = new DrawingContext();
        passwordBox.OnRender(passwordDrawing);
        RenderCommand passwordPlaceholder = passwordDrawing.Commands.Single(command => command.Text == "password");

        Assert.Equal(ProGPU.Text.TextAlignment.Right, textPlaceholder.TextAlignment);
        Assert.Equal(ProGPU.Text.TextAlignment.Right, passwordPlaceholder.TextAlignment);
    }

    [Fact]
    public void RtlPivotAndTabHeadersMirrorVisualOrderAndDirectionalKeys()
    {
        var pivot = new Pivot
        {
            FlowDirection = FlowDirection.RightToLeft,
            Font = InterFontFamily.Regular,
            Width = 300f,
            Height = 160f
        };
        pivot.Items.Add(new PivotItem { Header = "First" });
        pivot.Items.Add(new PivotItem { Header = "Second" });
        pivot.SelectedIndex = 0;
        pivot.Measure(new System.Numerics.Vector2(300f, 160f));
        pivot.Arrange(new Rect(0f, 0f, 300f, 160f));
        var pivotDrawing = new DrawingContext();
        pivot.OnRender(pivotDrawing);
        Rect activeStripe = pivotDrawing.Commands.Single(command =>
            command.Type == RenderCommandType.DrawRect && Math.Abs(command.Rect.Height - 3f) < 0.01f).Rect;
        Assert.True(activeStripe.X > 150f);

        InputSystem.Current = new WindowInputState();
        InputSystem.SetFocus(pivot);
        InputSystem.InjectKeyDown(Key.Left);
        InputSystem.InjectKeyUp(Key.Left);
        Assert.Equal(1, pivot.SelectedIndex);
        InputSystem.SetFocus(null);

        static (RenderCommand Text, float CloseCenter) RenderTab(FlowDirection direction)
        {
            var tab = new TabViewItem("Header")
            {
                FlowDirection = direction,
                Font = InterFontFamily.Regular,
                IsSelected = true,
                Size = new System.Numerics.Vector2(150f, 36f)
            };
            var drawing = new DrawingContext();
            tab.OnRender(drawing);
            RenderCommand text = drawing.Commands.Single(command => command.Text == "Header");
            float closeCenter = drawing.Commands
                .Where(command => command.Type == RenderCommandType.DrawLine)
                .Average(command => (command.Position.X + command.Position2.X) * 0.5f);
            return (text, closeCenter);
        }

        var ltrTab = RenderTab(FlowDirection.LeftToRight);
        var rtlTab = RenderTab(FlowDirection.RightToLeft);
        Assert.Equal(ProGPU.Text.TextAlignment.Right, rtlTab.Text.TextAlignment);
        Assert.True(ltrTab.CloseCenter > 100f);
        Assert.True(rtlTab.CloseCenter < 50f);

        var radioGroup = new StackPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Orientation = Orientation.Horizontal
        };
        var firstRadio = new RadioButton { IsChecked = true };
        var secondRadio = new RadioButton();
        radioGroup.AddChild(firstRadio);
        radioGroup.AddChild(secondRadio);
        InputSystem.Current = new WindowInputState();
        InputSystem.SetFocus(firstRadio);
        InputSystem.InjectKeyDown(Key.Left);
        InputSystem.InjectKeyUp(Key.Left);
        Assert.True(secondRadio.IsChecked);
        InputSystem.SetFocus(null);

        var navItem = new NavigationViewItem
        {
            FlowDirection = FlowDirection.RightToLeft,
            IsSelected = true,
            Size = new System.Numerics.Vector2(200f, 40f)
        };
        var navDrawing = new DrawingContext();
        navItem.OnRender(navDrawing);
        Rect navAccent = navDrawing.Commands.Single(command =>
            command.Type == RenderCommandType.DrawRect && Math.Abs(command.Rect.Width - 3f) < 0.01f).Rect;
        Assert.True(navAccent.X > 190f);
    }

    [Fact]
    public void PopupInheritsOwnerDirectionUnlessItHasAnExplicitOverride()
    {
        var owner = new Border { FlowDirection = FlowDirection.RightToLeft };
        var inheritedPopup = new Border { Width = 80f, Height = 40f };
        var explicitPopup = new Border
        {
            FlowDirection = FlowDirection.LeftToRight,
            Width = 80f,
            Height = 40f
        };
        try
        {
            PopupService.ShowPopup(inheritedPopup, System.Numerics.Vector2.Zero, owner);
            PopupService.ShowPopup(explicitPopup, new System.Numerics.Vector2(0f, 50f), owner);

            Assert.Equal(FlowDirection.RightToLeft, inheritedPopup.FlowDirection);
            Assert.Equal(FlowDirection.LeftToRight, explicitPopup.FlowDirection);
        }
        finally
        {
            PopupService.HidePopup(inheritedPopup);
            PopupService.HidePopup(explicitPopup);
        }
    }

    [Fact]
    public void RtlToolTipShapesFromTheRightAndPathIconMirrorsItsGeometry()
    {
        ProGPU.Text.TtfFont? previousPopupFont = PopupService.DefaultFont;
        PopupService.DefaultFont = InterFontFamily.Regular;
        try
        {
            var toolTip = new ToolTip
            {
                FlowDirection = FlowDirection.RightToLeft,
                Content = "tooltip",
                Size = new System.Numerics.Vector2(120f, 32f)
            };
            var toolTipDrawing = new DrawingContext();
            toolTip.OnRender(toolTipDrawing);
            RenderCommand tooltipText = toolTipDrawing.Commands.Single(command => command.Text == "tooltip");
            Assert.Equal(ProGPU.Text.TextAlignment.Right, tooltipText.TextAlignment);
            Assert.Equal(ProGPU.Text.Shaping.ShapingDirection.RightToLeft, tooltipText.TextShapingOptions?.Direction);

            var pathIcon = new PathIcon
            {
                FlowDirection = FlowDirection.RightToLeft,
                Data = "M 2 1 L 6 1",
                Size = new System.Numerics.Vector2(20f, 20f)
            };
            var pathDrawing = new DrawingContext();
            pathIcon.OnRender(pathDrawing);
            RenderCommand path = pathDrawing.Commands.Single(command => command.Type == RenderCommandType.DrawPath);
            Assert.Equal(-1f, path.Transform.M11, 3);
            Assert.Equal(20f, path.Transform.M41, 3);
        }
        finally
        {
            PopupService.DefaultFont = previousPopupFont;
        }
    }

    [Fact]
    public void RtlColorSlidersAndVirtualizedEditorMirrorDirectionalChrome()
    {
        static float RenderHueThumbX(FlowDirection direction)
        {
            var picker = new ColorPicker { FlowDirection = direction };
            var slider = Assert.IsAssignableFrom<Slider>(
                typeof(ColorPicker).GetField("_hueSlider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker));
            slider.FlowDirection = direction;
            slider.Value = 90f;
            slider.Size = new System.Numerics.Vector2(100f, 24f);
            var drawing = new DrawingContext();
            slider.OnRender(drawing);
            return drawing.Commands.Single(command => command.Type == RenderCommandType.DrawCircle).Position2.X;
        }

        Assert.True(RenderHueThumbX(FlowDirection.LeftToRight) < 50f);
        Assert.True(RenderHueThumbX(FlowDirection.RightToLeft) > 50f);

        var editor = new ProGPU.WinUI.Designer.VirtualizedCodeEditor(useLightweightSyntaxHighlighting: true)
        {
            FlowDirection = FlowDirection.RightToLeft,
            Font = InterFontFamily.Regular,
            Size = new System.Numerics.Vector2(200f, 100f)
        };
        editor.SetCode(string.Join('\n', Enumerable.Repeat("line", 20)));
        var editorDrawing = new DrawingContext();
        editor.OnRender(editorDrawing);
        Rect gutter = editorDrawing.Commands.Single(command =>
            command.Type == RenderCommandType.DrawRect && Math.Abs(command.Rect.Width - 45f) < 0.01f).Rect;
        Rect scrollbar = editorDrawing.Commands.Single(command =>
            command.Type == RenderCommandType.DrawRect && Math.Abs(command.Rect.Width - 6f) < 0.01f).Rect;

        Assert.True(gutter.X > 150f);
        Assert.True(scrollbar.X < 10f);
    }

    [Fact]
    public void ImageDoesNotInheritFlowDirectionButAcceptsAnExplicitValue()
    {
        var root = new Grid { FlowDirection = FlowDirection.RightToLeft };
        var image = new Image();
        root.AddChild(image);

        Assert.Equal(FlowDirection.LeftToRight, image.FlowDirection);
        Assert.False(image.IsRightToLeftLayout);

        image.FlowDirection = FlowDirection.RightToLeft;
        Assert.Equal(FlowDirection.RightToLeft, image.FlowDirection);
        Assert.True(image.IsRightToLeftLayout);
    }

    [Fact]
    public void RtlShapeMirrorsItsVisualGeometryWithoutChangingGlyphSemantics()
    {
        var line = new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = 2f,
            X2 = 18f,
            Y1 = 5f,
            Y2 = 5f,
            Width = 20f,
            Height = 10f,
            Stroke = new ProGPU.Vector.SolidColorBrush(0xFFFFFFFF),
            FlowDirection = FlowDirection.RightToLeft
        };
        line.Measure(new System.Numerics.Vector2(20f, 10f));
        line.Arrange(new Rect(0f, 0f, 20f, 10f));
        var context = new DrawingContext();

        line.OnRender(context);

        RenderCommand command = Assert.Single(context.Commands);
        Assert.Equal(18f, command.Position.X, 3);
        Assert.Equal(2f, command.Position2.X, 3);
    }

    [Fact]
    public void RichTextBlockUsesBidiVisualPositionsWithoutChangingLogicalInlineOrder()
    {
        var block = new RichTextBlock
        {
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            FlowDirection = FlowDirection.RightToLeft,
            TextReadingOrder = TextReadingOrder.UseFlowDirection
        };
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run("abc אבג"));

        block.Measure(new System.Numerics.Vector2(300f, 80f));
        block.Arrange(new Rect(0f, 0f, 300f, 80f));

        Assert.Equal("abc אבג", new string(block.PositionedChars.Select(static character => character.Info.Character).ToArray()));
        Assert.Equal(
            new[] { 6, 5, 4, 3, 0, 1, 2 },
            block.PositionedChars
                .Select((character, logicalIndex) => (character.Position.X, logicalIndex))
                .OrderBy(static item => item.X)
                .Select(static item => item.logicalIndex));
        Assert.True(block.PositionedChars.Min(static character => character.Position.X) > 0f);
    }

    [Fact]
    public void TextBoxMatchesWinUiDirectionAndAlignmentPropertyContract()
    {
        var textBox = new TextBox();

        Assert.Equal(TextReadingOrder.DetectFromContent, textBox.TextReadingOrder);
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Left, textBox.TextAlignment);
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Left, textBox.HorizontalTextAlignment);

        textBox.HorizontalTextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Center, textBox.TextAlignment);

        textBox.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right;
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Right, textBox.HorizontalTextAlignment);
    }

    [Fact]
    public void RichEditBoxMatchesWinUiDirectionAndAlignmentPropertyContract()
    {
        var richEditBox = new RichEditBox();

        Assert.Equal(TextReadingOrder.DetectFromContent, richEditBox.TextReadingOrder);
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Left, richEditBox.TextAlignment);
        Assert.Equal(TextWrapping.Wrap, richEditBox.TextWrapping);

        richEditBox.HorizontalTextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Center, richEditBox.TextAlignment);

        richEditBox.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right;
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Right, richEditBox.HorizontalTextAlignment);
    }

    [Fact]
    public void PasswordBoxMatchesWinUiReadingOrderAndDeletesWholeGraphemes()
    {
        const string family = "👨‍👩‍👧‍👦";
        var passwordBox = new PasswordBox
        {
            Password = $"A{family}B",
            CaretIndex = 1 + family.Length
        };

        Assert.Equal(TextReadingOrder.DetectFromContent, passwordBox.TextReadingOrder);
        InputSystem.SetFocus(passwordBox);
        passwordBox.OnKeyDown(new KeyRoutedEventArgs { Key = Key.Backspace });
        InputSystem.SetFocus(null);

        Assert.Equal("AB", passwordBox.Password);
        Assert.Equal(1, passwordBox.CaretIndex);
    }

    [Fact]
    public void PasswordBoxArrowKeysFollowVisualRtlCaretOrder()
    {
        const string text = "אבג";
        var passwordBox = new PasswordBox
        {
            Password = text,
            PasswordChar = '•',
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            FlowDirection = FlowDirection.RightToLeft,
            TextReadingOrder = TextReadingOrder.UseFlowDirection,
            Width = 300f,
            Height = 60f,
            CaretIndex = 0
        };
        passwordBox.Measure(new System.Numerics.Vector2(300f, 60f));
        passwordBox.Arrange(new Rect(0f, 0f, 300f, 60f));
        InputSystem.SetFocus(passwordBox);

        passwordBox.OnKeyDown(new KeyRoutedEventArgs { Key = Key.Left });
        InputSystem.SetFocus(null);

        Assert.Equal(1, passwordBox.CaretIndex);
    }

    [Fact]
    public void TextBlockHorizontalTextAlignmentUsesLastValueSet()
    {
        var textBlock = new TextBlock();

        textBlock.HorizontalTextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Center, textBlock.TextAlignment);

        textBlock.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right;
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Right, textBlock.HorizontalTextAlignment);
    }

    [Fact]
    public void TextBoxDeletesAnExtendedGraphemeAsOneEditingUnit()
    {
        const string family = "👨‍👩‍👧‍👦";
        var textBox = new TextBox
        {
            Text = $"A{family}B",
            CaretIndex = 1 + family.Length
        };
        InputSystem.SetFocus(textBox);

        textBox.OnKeyDown(new KeyRoutedEventArgs { Key = Key.Backspace });
        InputSystem.SetFocus(null);

        Assert.Equal("AB", textBox.Text);
        Assert.Equal(1, textBox.CaretIndex);
    }

    [Fact]
    public void RichEditBoxDeletesAnExtendedGraphemeAsOneEditingUnit()
    {
        const string combining = "a\u0301";
        var richEditBox = new RichEditBox
        {
            Text = $"X{combining}Y",
            CaretIndex = 1 + combining.Length
        };
        InputSystem.SetFocus(richEditBox);

        richEditBox.OnKeyDown(new KeyRoutedEventArgs { Key = Key.Backspace });
        InputSystem.SetFocus(null);

        Assert.Equal("XY", richEditBox.Text);
        Assert.Equal(1, richEditBox.CaretIndex);
    }

    [Fact]
    public void RichEditBoxHomeAndEndFollowVisualRtlLineEdges()
    {
        var editor = new RichEditBox
        {
            Text = "אבג",
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            FlowDirection = FlowDirection.RightToLeft,
            TextReadingOrder = TextReadingOrder.UseFlowDirection,
            Width = 240f,
            Height = 80f,
            CaretIndex = 2
        };
        editor.Measure(new System.Numerics.Vector2(240f, 80f));
        editor.Arrange(new Rect(0f, 0f, 240f, 80f));
        InputSystem.SetFocus(editor);

        editor.OnKeyDown(new KeyRoutedEventArgs { Key = Key.Home });
        Assert.Equal(0, editor.CaretIndex);
        editor.OnKeyDown(new KeyRoutedEventArgs { Key = Key.End });
        InputSystem.SetFocus(null);

        Assert.Equal(3, editor.CaretIndex);
    }

    [Fact]
    public void TextBoxArrowKeysFollowVisualBidiCaretOrder()
    {
        const string text = "abc אבג";
        var textBox = new TextBox
        {
            Text = text,
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            Width = 300f,
            Height = 60f,
            CaretIndex = 4
        };
        textBox.Measure(new System.Numerics.Vector2(300f, 60f));
        textBox.Arrange(new Rect(0f, 0f, 300f, 60f));
        InputSystem.SetFocus(textBox);

        var layout = new TextLayout(
            text,
            InterFontFamily.Regular,
            24f,
            284f,
            shapingOptions: new TextShapingOptions { Direction = ShapingDirection.Unspecified });
        TextCaretStop expected = layout.MoveCaretVisually(4, false, -1);

        textBox.OnKeyDown(new KeyRoutedEventArgs { Key = Key.Left });
        InputSystem.SetFocus(null);

        Assert.Equal(expected.TextPosition, textBox.CaretIndex);
    }

    [Fact]
    public void RichTextAndMarkdownShareShapedClusterMetrics()
    {
        const string text = "office a\u0301";
        var richText = new RichTextBlock
        {
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            Padding = new Thickness(0f)
        };
        richText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run(text));
        richText.Measure(new System.Numerics.Vector2(400f, 80f));
        richText.Arrange(new Rect(0f, 0f, 400f, 80f));

        var markdown = new MarkdownTextBlock
        {
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            Padding = new Thickness(0f),
            Markdown = text
        };
        markdown.Measure(new System.Numerics.Vector2(400f, 80f));
        markdown.Arrange(new Rect(0f, 0f, 400f, 80f));

        var richMetrics = richText.PositionedChars
            .Select(static character => (character.ClusterStart, character.ClusterLength, character.ShapedAdvance))
            .ToArray();
        var markdownMetrics = markdown.PositionedChars
            .Select(static character => (character.ClusterStart, character.ClusterLength, character.ShapedAdvance))
            .ToArray();

        Assert.Equal(richMetrics, markdownMetrics);
        Assert.Contains(richText.PositionedChars, static character => character.ClusterLength > 1);

        var expected = new TextLayout(text, InterFontFamily.Regular, 24f);
        float left = richText.PositionedChars.Min(static character => character.Position.X);
        float right = richText.PositionedChars.Max(static character => character.Position.X + character.ShapedAdvance);
        Assert.Equal(expected.ContentSize.X, right - left, 2);
    }

    [Fact]
    public void RichTextAndMarkdownDoNotWrapInsideShapingClusters()
    {
        const string text = "e\u0301x";
        var richText = new RichTextBlock
        {
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            Padding = new Thickness(0f),
            TextWrapping = TextWrapping.Wrap
        };
        richText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run(text));
        richText.Measure(new System.Numerics.Vector2(20f, 200f));
        richText.Arrange(new Rect(0f, 0f, 20f, 200f));

        var markdown = new MarkdownTextBlock
        {
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            Padding = new Thickness(0f),
            Markdown = text
        };
        markdown.Measure(new System.Numerics.Vector2(20f, 200f));
        markdown.Arrange(new Rect(0f, 0f, 20f, 200f));

        AssertClusterRemainsOnOneLine(richText.PositionedChars);
        AssertClusterRemainsOnOneLine(markdown.PositionedChars);

        static void AssertClusterRemainsOnOneLine(IReadOnlyList<PositionedRichChar> characters)
        {
            Assert.Equal(3, characters.Count);
            Assert.Equal(characters[0].Position.Y, characters[1].Position.Y);
            Assert.True(characters[2].Position.Y > characters[1].Position.Y);
            Assert.Equal(characters[0].ClusterStart, characters[1].ClusterStart);
            Assert.Equal(2, characters[0].ClusterLength);
            Assert.Equal(2, characters[1].ClusterLength);
        }
    }

    [Fact]
    public void WrappedRichTextRetainsWholeParagraphBidiLevels()
    {
        var richText = new RichTextBlock
        {
            Font = InterFontFamily.Regular,
            FontSize = 20f,
            Padding = new Thickness(0f),
            TextWrapping = TextWrapping.Wrap,
            TextReadingOrder = TextReadingOrder.DetectFromContent,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.DetectFromContent
        };
        richText.Inlines.Add(new Run("אבג אבג abc"));
        richText.Measure(new System.Numerics.Vector2(78f, 160f));
        richText.Arrange(new Rect(0f, 0f, 78f, 160f));
        PositionedRichChar hebrew = richText.PositionedChars.First(static value => value.Info.Character == 'א');
        PositionedRichChar latin = richText.PositionedChars.First(static value => value.Info.Character == 'a');

        Assert.True(latin.Position.Y > hebrew.Position.Y);
        Assert.Equal(2, latin.BidiLevel);
        Assert.True(latin.Position.X > 20f);
    }

    [Fact]
    public void VersionedRichDocumentDrivesTheSharedMarkdownPresenter()
    {
        var document = new RichDocument();
        document.Add(new Paragraph(new Run("first")));
        var presenter = new MarkdownTextBlock
        {
            Document = document,
            Markdown = "ignored",
            Font = InterFontFamily.Regular,
            FontSize = 20f,
            Padding = new Thickness(0f)
        };
        presenter.Measure(new System.Numerics.Vector2(300f, 80f));
        presenter.Arrange(new Rect(0f, 0f, 300f, 80f));
        Assert.Equal("first", new string(presenter.PositionedChars.Select(static value => value.Info.Character).ToArray()));

        long firstVersion = document.Version;
        document.ReplaceBlocks([new Paragraph(new Run("second"))]);
        Assert.True(document.Version > firstVersion);
        presenter.Measure(new System.Numerics.Vector2(300f, 80f));
        presenter.Arrange(new Rect(0f, 0f, 300f, 80f));

        Assert.Equal("second", new string(presenter.PositionedChars.Select(static value => value.Info.Character).ToArray()));
    }

    [Fact]
    public void NestedDocumentMutationsAdvanceVersionAndInvalidatePresenterLayout()
    {
        var run = new Run("before");
        var paragraph = new Paragraph(run);
        var document = new RichDocument();
        document.Add(paragraph);
        var presenter = new MarkdownTextBlock
        {
            Document = document,
            Font = InterFontFamily.Regular,
            FontSize = 20f,
            Padding = new Thickness(0f)
        };
        presenter.Measure(new System.Numerics.Vector2(300f, 80f));
        presenter.Arrange(new Rect(0f, 0f, 300f, 80f));

        long version = document.Version;
        run.Text = "after";

        Assert.True(document.Version > version);
        presenter.Measure(new System.Numerics.Vector2(300f, 80f));
        presenter.Arrange(new Rect(0f, 0f, 300f, 80f));
        Assert.Equal("after", new string(presenter.PositionedChars.Select(static value => value.Info.Character).ToArray()));
    }

    [Fact]
    public void SharedDocumentUsesIndependentPresenterLayoutSessions()
    {
        var document = new RichDocument();
        document.Add(new Paragraph(new Run("A shared document with enough words to wrap differently.")));
        var narrow = new MarkdownTextBlock
        {
            Document = document,
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            Padding = new Thickness(0f)
        };
        var wide = new MarkdownTextBlock
        {
            Document = document,
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            Padding = new Thickness(0f)
        };

        narrow.Measure(new System.Numerics.Vector2(120f, 300f));
        narrow.Arrange(new Rect(0f, 0f, 120f, 300f));
        wide.Measure(new System.Numerics.Vector2(420f, 300f));
        wide.Arrange(new Rect(0f, 0f, 420f, 300f));

        Assert.NotSame(narrow.LayoutSession, wide.LayoutSession);
        Assert.Equal(1, narrow.LayoutSession.RealizedBlockCount);
        Assert.Equal(1, wide.LayoutSession.RealizedBlockCount);
        Assert.True(narrow.PositionedChars.Max(static value => value.Position.Y) >
                    wide.PositionedChars.Max(static value => value.Position.Y));
    }

    [Fact]
    public void RichDocumentFormatsRoundTripThroughTypedRegistry()
    {
        var document = new RichDocument();
        var sourceParagraph = new Paragraph(
            new Run("Hello "),
            new Bold(new Run("world")),
            new Run(" "),
            new Hyperlink(new Run("link")) { Uri = "https://example.test" })
        {
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right,
            FlowDirection = FlowDirection.RightToLeft,
            FirstLineIndent = 9f,
            LeftIndent = 18f,
            SpaceBefore = 5f,
            MarginBottom = 7f
        };
        document.Add(sourceParagraph);
        RichDocumentFormatRegistry registry = RichDocumentFormatRegistry.CreateDefault();

        Assert.True(registry.TryGetFileExtension("md", out IRichDocumentFormatCodec? markdown));
        byte[] encoded = Assert.IsType<MarkdownDocumentCodec>(markdown).Export(document);
        string output = System.Text.Encoding.UTF8.GetString(encoded);

        Assert.Contains("**world**", output, StringComparison.Ordinal);
        Assert.Contains("[link](https://example.test)", output, StringComparison.Ordinal);
        Assert.True(registry.TryGetFormat("text/plain", out IRichDocumentFormatCodec? plain));
        Assert.Equal("Hello world link", System.Text.Encoding.UTF8.GetString(plain!.Export(document)));
        Assert.True(registry.TryGetFileExtension(".rtf", out IRichDocumentFormatCodec? rtf));
        byte[] rtfBytes = Assert.IsType<RtfDocumentCodec>(rtf).Export(document);
        var context = new RichDocumentImportContext(
            InterFontFamily.Regular,
            InterFontFamily.Regular,
            16f,
            new ProGPU.Vector.SolidColorBrush(0x000000FF),
            ElementTheme.Light);
        RichDocument imported = rtf.Import(rtfBytes, context);
        Assert.Equal("Hello world link", PlainTextDocumentExporter.Default.Export(imported));
        Paragraph importedParagraph = Assert.IsType<Paragraph>(Assert.Single(imported.Blocks));
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Right, importedParagraph.TextAlignment);
        Assert.Equal(FlowDirection.RightToLeft, importedParagraph.FlowDirection);
        Assert.Equal(9f, importedParagraph.FirstLineIndent);
        Assert.Equal(18f, importedParagraph.LeftIndent);
        Assert.Equal(5f, importedParagraph.SpaceBefore);
        Assert.Equal(7f, importedParagraph.MarginBottom);
        Assert.Contains("href=\"https://example.test\"", System.Text.Encoding.UTF8.GetString(HtmlDocumentCodec.Default.Export(imported)), StringComparison.Ordinal);
        Assert.True(registry.TryGetFileExtension("html", out IRichDocumentFormatCodec? html));
        byte[] htmlBytes = Assert.IsType<HtmlDocumentCodec>(html).Export(document);
        string htmlText = System.Text.Encoding.UTF8.GetString(htmlBytes);
        Assert.Contains("<strong>world</strong>", htmlText, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.test\"", htmlText, StringComparison.Ordinal);
        RichDocument htmlImported = html.Import(htmlBytes, context);
        Assert.Equal("Hello world link", PlainTextDocumentExporter.Default.Export(htmlImported));
    }

    [Fact]
    public void HtmlDocumentCodecPreservesSemanticBlocksDirectionAndCssFormatting()
    {
        const string html = """
            <div dir="rtl">
              <h2 style="text-align:right;margin-bottom:8px">כותרת</h2>
              <ol><li><strong>First</strong></li><li><em>Second</em></li></ol>
              <table><tbody><tr><th width="72" style="background-color:#ffeecc">A</th><th width="96">B</th></tr><tr><td>C</td><td>D</td></tr></tbody></table>
              <p dir="ltr" lang="ar" style="text-align:center;margin-left:12px;text-indent:4px"><span style="color:#123456;text-decoration:underline line-through">مرحبا</span></p>
            </div>
            """;
        var context = new RichDocumentImportContext(
            InterFontFamily.Regular,
            InterFontFamily.Regular,
            16f,
            new ProGPU.Vector.SolidColorBrush(0x000000FF),
            ElementTheme.Light);

        RichDocument document = HtmlDocumentCodec.Default.Import(
            System.Text.Encoding.UTF8.GetBytes(html),
            context);

        Assert.Equal("Paragraph,ListBlock,Table,Paragraph", string.Join(',', document.Blocks.Select(static block => block.GetType().Name)));
        Paragraph heading = Assert.IsType<Paragraph>(document.Blocks[0]);
        Assert.Equal(FlowDirection.RightToLeft, heading.FlowDirection);
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Right, heading.TextAlignment);
        Assert.Equal(8f, heading.MarginBottom);
        Run headingRun = Assert.IsType<Run>(Assert.Single(heading.Inlines));
        Assert.Equal(24f, headingRun.FontSize);

        ListBlock list = Assert.IsType<ListBlock>(document.Blocks[1]);
        Assert.True(list.IsOrdered);
        Assert.Equal(2, list.Items.Count);
        Assert.IsType<Bold>(Assert.Single(list.Items[0].Inlines));
        Assert.IsType<Italic>(Assert.Single(list.Items[1].Inlines));

        Table table = Assert.IsType<Table>(document.Blocks[2]);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new[] { 72f, 96f }, table.ColumnWidths);
        Assert.NotNull(table.Rows[0].Cells[0].Background);
        Assert.IsType<Run>(table.Rows[0].Cells[0].Inlines[0]);

        Paragraph paragraph = Assert.IsType<Paragraph>(document.Blocks[3]);
        Assert.Equal(FlowDirection.LeftToRight, paragraph.FlowDirection);
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Center, paragraph.TextAlignment);
        Assert.Equal(12f, paragraph.LeftIndent);
        Assert.Equal(4f, paragraph.FirstLineIndent);
        Assert.IsType<Run>(Assert.Single(paragraph.Inlines));

        string exported = System.Text.Encoding.UTF8.GetString(HtmlDocumentCodec.Default.Export(document));
        Assert.Contains("dir=\"rtl\"", exported, StringComparison.Ordinal);
        Assert.Contains("<ol>", exported, StringComparison.Ordinal);
        Assert.Contains("<table", exported, StringComparison.Ordinal);
        Assert.Contains("lang=\"ar\"", exported, StringComparison.Ordinal);
        Assert.Contains("color:#123456", exported, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text-decoration:underline line-through", exported, StringComparison.Ordinal);
        Assert.Contains("font-weight:700", exported, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlAndRtfDocumentCodecsShareRetainedInlineImageObjects()
    {
        const string pngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        string html = $"<p>before<img src=\"data:image/png;base64,{pngBase64}\" width=\"20\" height=\"10\" alt=\"pixel\">after</p>";
        var context = new RichDocumentImportContext(
            InterFontFamily.Regular,
            InterFontFamily.Regular,
            16f,
            new ProGPU.Vector.SolidColorBrush(0x000000FF),
            ElementTheme.Light);

        RichDocument fromHtml = HtmlDocumentCodec.Default.Import(System.Text.Encoding.UTF8.GetBytes(html), context);
        Paragraph paragraph = Assert.IsType<Paragraph>(Assert.Single(fromHtml.Blocks));
        InlineUIContainer imageContainer = Assert.IsType<InlineUIContainer>(paragraph.Inlines[1]);
        Image image = Assert.IsType<Image>(imageContainer.Child);
        Assert.Equal(20f, image.Width);
        Assert.Equal(10f, image.Height);

        string exportedHtml = System.Text.Encoding.UTF8.GetString(HtmlDocumentCodec.Default.Export(fromHtml));
        Assert.Contains("data:image/png;base64,", exportedHtml, StringComparison.Ordinal);
        Assert.Contains("alt=\"pixel\"", exportedHtml, StringComparison.Ordinal);

        byte[] rtf = RtfDocumentCodec.Default.Export(fromHtml);
        Assert.Contains(@"\pict", System.Text.Encoding.UTF8.GetString(rtf), StringComparison.Ordinal);
        RichDocument fromRtf = RtfDocumentCodec.Default.Import(rtf, context);
        Paragraph rtfParagraph = Assert.IsType<Paragraph>(Assert.Single(fromRtf.Blocks));
        Assert.IsType<Image>(Assert.IsType<InlineUIContainer>(rtfParagraph.Inlines[1]).Child);
        Assert.Equal("beforepixelafter", PlainTextDocumentExporter.Default.Export(fromRtf).Replace("\uFFFC", "pixel", StringComparison.Ordinal));
    }

    [Fact]
    public void RichEditBoxLoadsEditsSnapshotsAndExportsTheSharedDocumentModel()
    {
        var source = new RichDocument();
        source.Add(new Paragraph(new Run("שלום"))
        {
            FlowDirection = FlowDirection.RightToLeft,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right
        });
        var list = new ListBlock { IsOrdered = true };
        list.Items.Add(new ListItem(new Bold(new Run("First"))));
        list.Items.Add(new ListItem(new Run("Second")));
        source.Add(list);
        var firstCell = new TableCell("A")
        {
            Background = new ProGPU.Vector.ThemeResourceBrush("AccentFill")
        };
        var table = new Table(
            new TableRow(firstCell, new TableCell("B")),
            new TableRow(new TableCell("C"), new TableCell("D")))
        {
            ColumnWidths = new System.Collections.Generic.List<float> { 72f, 96f },
            CellPadding = 6f,
            BorderThickness = 2f,
            BorderBrush = new ProGPU.Vector.ThemeResourceBrush("ControlStroke")
        };
        source.Add(table);
        var editor = new RichEditBox { Text = "old" };
        editor.TextDocument.ClearUndoRedoHistory();

        editor.SetRichDocument(source);
        RichDocument snapshot = editor.CreateRichDocumentSnapshot();

        Assert.Equal("Paragraph,ListBlock,Table", string.Join(',', snapshot.Blocks.Select(static block => block.GetType().Name)));
        Paragraph paragraph = Assert.IsType<Paragraph>(snapshot.Blocks[0]);
        Assert.Equal(FlowDirection.RightToLeft, paragraph.FlowDirection);
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Right, paragraph.TextAlignment);
        Assert.Equal(2, Assert.IsType<ListBlock>(snapshot.Blocks[1]).Items.Count);
        Assert.Equal(new[] { 72f, 96f }, Assert.IsType<Table>(snapshot.Blocks[2]).ColumnWidths);
        string html = System.Text.Encoding.UTF8.GetString(editor.SaveDocument(HtmlDocumentCodec.Default));
        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("<ol>", html, StringComparison.Ordinal);
        Assert.Contains("<table", html, StringComparison.Ordinal);

        editor.TextDocument.Undo();
        Assert.Equal("old", editor.Text);
    }

    [Fact]
    public void RichEditTableRowsUseRetainedColumnsAndTabCellNavigation()
    {
        var source = new RichDocument();
        var styledCell = new TableCell("A")
        {
            Background = new ProGPU.Vector.ThemeResourceBrush("AccentFill")
        };
        source.Add(new Table(
            new TableRow(styledCell, new TableCell("B")),
            new TableRow(new TableCell("C"), new TableCell("D")))
        {
            ColumnWidths = new System.Collections.Generic.List<float> { 72f, 96f },
            CellPadding = 6f,
            BorderThickness = 2f,
            BorderBrush = new ProGPU.Vector.ThemeResourceBrush("ControlStroke")
        });
        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            Width = 320f,
            Height = 180f
        };
        editor.SetRichDocument(source);
        editor.TextDocument.ClearUndoRedoHistory();
        editor.Measure(new System.Numerics.Vector2(320f, 180f));
        editor.Arrange(new Rect(0f, 0f, 320f, 180f));

        RichTextBlock presenter = Assert.IsType<RichTextBlock>(
            typeof(RichEditBox).GetField("_blockView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        PositionedRichChar firstCellChar = presenter.PositionedChars.First(static value => value.Info.Character == 'A');
        PositionedRichChar secondCellChar = presenter.PositionedChars.First(static value => value.Info.Character == 'B');
        Assert.True(secondCellChar.Position.X - firstCellChar.Position.X >= 65f);
        var decorations = Assert.IsType<System.Collections.Generic.List<TableVisualDecoration>>(
            typeof(RichTextBlock).GetField("_tableDecorations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter));
        Assert.Equal(4, decorations.Count);
        Assert.All(decorations, static decoration => Assert.Equal(2f, decoration.BorderThickness));
        Assert.NotNull(decorations[0].Background);

        Table projectedTable = Assert.IsType<Table>(Assert.Single(editor.CreateRichDocumentSnapshot().Blocks));
        Assert.Equal(6f, projectedTable.CellPadding);
        Assert.Equal(2f, projectedTable.BorderThickness);
        Assert.NotNull(projectedTable.BorderBrush);
        Assert.NotNull(projectedTable.Rows[0].Cells[0].Background);

        InputSystem.Current = new WindowInputState();
        InputSystem.SetFocus(editor);
        ITextSelection selection = editor.TextDocument.Selection;
        selection.SetRange(0, 0);
        InputSystem.InjectKeyDown(Key.Tab);
        InputSystem.InjectKeyUp(Key.Tab);
        Assert.Equal(2, editor.CaretIndex);

        InputSystem.InjectKeyDown(Key.ShiftLeft);
        InputSystem.InjectKeyDown(Key.Tab);
        InputSystem.InjectKeyUp(Key.Tab);
        InputSystem.InjectKeyUp(Key.ShiftLeft);
        Assert.Equal(0, editor.CaretIndex);

        selection.SetRange(2, 2);
        InputSystem.InjectKeyDown(Key.Tab);
        InputSystem.InjectKeyUp(Key.Tab);
        Assert.Equal(4, editor.CaretIndex);

        selection.SetRange(6, 6);
        InputSystem.InjectKeyDown(Key.Tab);
        InputSystem.InjectKeyUp(Key.Tab);
        InputSystem.SetFocus(null);

        Assert.Equal("A\tB\nC\tD\n\t", editor.Text);
        Assert.Equal(8, editor.CaretIndex);
        Assert.Equal(3, Assert.IsType<Table>(Assert.Single(editor.CreateRichDocumentSnapshot().Blocks)).Rows.Count);

        editor.Undo();
        Assert.Equal("A\tB\nC\tD", editor.Text);
    }

    [Fact]
    public void RichEditCtrlTabInsertsLiteralTabThroughInputSystem()
    {
        InputSystem.Current = new WindowInputState();
        var editor = new RichEditBox { Text = "AB" };
        InputSystem.SetFocus(editor);
        editor.TextDocument.Selection.SetRange(1, 1);

        InputSystem.InjectKeyDown(Key.ControlLeft);
        InputSystem.InjectKeyDown(Key.Tab);
        InputSystem.InjectKeyUp(Key.Tab);
        InputSystem.InjectKeyUp(Key.ControlLeft);
        InputSystem.SetFocus(null);

        Assert.Equal("A\tB", editor.Text);
        Assert.Equal(2, editor.CaretIndex);
    }

    [Fact]
    public void RichEditRectangularTableSelectionCopiesFormatsDeletesAndUndoesAsOneEdit()
    {
        var source = new RichDocument();
        source.Add(new Table(
            new TableRow(new TableCell("A"), new TableCell("B")),
            new TableRow(new TableCell("C"), new TableCell("D"))));
        var editor = new RichEditBox();
        editor.SetRichDocument(source);
        editor.TextDocument.ClearUndoRedoHistory();

        Assert.True(editor.SelectTableCells(0, 6));
        Assert.Equal(4, editor.SelectedTableCells.Count);
        Assert.Equal(
            new[] { (0, 0, 0, 1), (0, 1, 2, 3), (1, 0, 4, 5), (1, 1, 6, 7) },
            editor.SelectedTableCells.Select(static cell =>
                (cell.Row, cell.Column, cell.StartPosition, cell.EndPosition)));

        editor.Copy();
        Assert.Equal("A\tB\nC\tD", Microsoft.UI.Xaml.ClipboardHelper.GetText());

        editor.ToggleStyle("bold");
        Assert.All(editor.SelectedTableCells, cell =>
            Assert.Equal(
                FormatEffect.On,
                editor.TextDocument.GetRange(cell.StartPosition, cell.EndPosition).CharacterFormat.Bold));
        editor.TextDocument.ClearUndoRedoHistory();

        InputSystem.Current = new WindowInputState();
        InputSystem.SetFocus(editor);
        InputSystem.InjectKeyDown(Key.Delete);
        InputSystem.InjectKeyUp(Key.Delete);
        InputSystem.SetFocus(null);

        Assert.Equal("\t\n\t", editor.Text);
        Assert.Empty(editor.SelectedTableCells);
        Assert.True(editor.TextDocument.CanUndo());

        editor.Undo();
        Assert.Equal("A\tB\nC\tD", editor.Text);
        Assert.False(editor.TextDocument.CanUndo());
    }

    [Fact]
    public void RichEditRectangularTableSelectionCutsAndPastesTabularTextWithoutRemovingGrid()
    {
        var source = new RichDocument();
        source.Add(new Table(
            new TableRow(new TableCell("A"), new TableCell("B"), new TableCell("C")),
            new TableRow(new TableCell("D"), new TableCell("E"), new TableCell("F"))));
        var editor = new RichEditBox();
        editor.SetRichDocument(source);
        editor.TextDocument.ClearUndoRedoHistory();

        Assert.True(editor.SelectTableCells(2, 8));
        Assert.Equal(2, editor.SelectedTableCells.Count);
        editor.Cut();
        Assert.Equal("B\nE", Microsoft.UI.Xaml.ClipboardHelper.GetText());
        Assert.Equal("A\t\tC\nD\t\tF", editor.Text);

        editor.Undo();
        Assert.Equal("A\tB\tC\nD\tE\tF", editor.Text);
        editor.TextDocument.ClearUndoRedoHistory();

        Assert.True(editor.SelectTableCells(0, 8));
        Microsoft.UI.Xaml.ClipboardHelper.SetText("1\t2\n3\t4");
        editor.PasteFromClipboard();
        Assert.Equal("1\t2\tC\n3\t4\tF", editor.Text);
        Assert.Empty(editor.SelectedTableCells);

        editor.Undo();
        Assert.Equal("A\tB\tC\nD\tE\tF", editor.Text);
        Assert.False(editor.TextDocument.CanUndo());
    }

    [Fact]
    public void RichEditTableSelectionMapsVerticalContinuationsAndHonorsSelectionCancellation()
    {
        var source = new RichDocument();
        source.Add(new Table(
            new TableRow(new TableCell("A") { RowSpan = 2 }, new TableCell("B")),
            new TableRow(new TableCell("C"))));
        var editor = new RichEditBox();
        editor.SetRichDocument(source);
        Assert.Equal("A\tB\n\tC", editor.Text);

        Assert.True(editor.SelectTableCells(4, 4));
        RichEditTableCellRange merged = Assert.Single(editor.SelectedTableCells);
        Assert.Equal((0, 0, 0, 1),
            (merged.Row, merged.Column, merged.StartPosition, merged.EndPosition));

        int changed = 0;
        editor.SelectionChanged += (_, _) => changed++;
        editor.SelectionChanging += (_, args) => args.Cancel = true;
        Assert.False(editor.SelectTableCells(4, 5));
        Assert.Single(editor.SelectedTableCells);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void RichEditTom2InsertTableReplacesRangeIsolatesParagraphsAndUndoesAtomically()
    {
        var editor = new RichEditBox { Text = "before selected after" };
        editor.TextDocument.ClearUndoRedoHistory();
        RichEditTextRange range = editor.TextDocument.GetRange2(7, 15);

        range.InsertTable(columnCount: 2, rowCount: 2);

        Assert.Equal("before \n\t\n\t\n after", editor.Text);
        Assert.Equal("\t\n\t", range.Text);
        Assert.Equal(8, range.StartPosition);
        Assert.Equal(11, range.EndPosition);
        Assert.Equal(8, editor.CaretIndex);
        RichDocument snapshot = editor.CreateRichDocumentSnapshot();
        Assert.Equal("Paragraph,Table,Paragraph", string.Join(',', snapshot.Blocks.Select(static block => block.GetType().Name)));
        Table table = Assert.IsType<Table>(snapshot.Blocks[1]);
        Assert.Equal(2, table.Rows.Count);
        Assert.All(table.Rows, static row => Assert.Equal(2, row.Cells.Count));
        Assert.Equal(0.5f, table.BorderThickness);
        Assert.NotNull(table.BorderBrush);

        editor.Undo();
        Assert.Equal("before selected after", editor.Text);
        Assert.False(editor.TextDocument.CanUndo());
    }

    [Fact]
    public void RichEditTom2InsertTableValidatesDimensionsAndSupportsFixedColumns()
    {
        var editor = new RichEditBox();
        RichEditTextRange range = editor.TextDocument.GetRange2(0, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => range.InsertTable(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => range.InsertTable(1, 0));
        range.InsertTable(columnCount: 3, rowCount: 2, autoFit: false);

        Assert.Equal("\t\t\n\t\t", editor.Text);
        Table table = Assert.IsType<Table>(Assert.Single(editor.CreateRichDocumentSnapshot().Blocks));
        Assert.Equal(new[] { 96f, 96f, 96f }, table.ColumnWidths);
        Assert.True(editor.SelectTableCells(0, editor.Text.Length));
        Assert.Equal(6, editor.SelectedTableCells.Count);
    }

    [Fact]
    public void RichEditTableCellsWrapIndependentlyAndKeepLogicalCaretPositions()
    {
        const string firstText = "abcdefghijklmno";
        var document = new RichDocument();
        document.Add(new Table(
            new TableRow(new TableCell(firstText), new TableCell("B")))
        {
            ColumnWidths = new System.Collections.Generic.List<float> { 60f, 80f },
            CellPadding = 4f
        });
        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            Width = 250f,
            Height = 160f
        };
        editor.SetRichDocument(document);
        editor.Measure(new System.Numerics.Vector2(250f, 160f));
        editor.Arrange(new Rect(0f, 0f, 250f, 160f));

        RichTextBlock presenter = Assert.IsType<RichTextBlock>(
            typeof(RichEditBox).GetField("_blockView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        PositionedRichChar[] firstCellChars = presenter.PositionedChars
            .Where(static value => value.Info.Character is >= 'a' and <= 'z')
            .ToArray();
        Assert.True(firstCellChars.Select(static value => value.Position.Y).Distinct().Count() > 1);
        PositionedRichChar secondCell = presenter.PositionedChars.Single(static value => value.Info.Character == 'B');
        Assert.True(secondCell.Position.X >= 64f);
        Assert.Equal(
            Enumerable.Range(0, editor.Text.Length),
            presenter.PositionedChars.Select(static value => value.Info.TextPosition).OrderBy(static value => value));

        var decorations = Assert.IsType<System.Collections.Generic.List<TableVisualDecoration>>(
            typeof(RichTextBlock).GetField("_tableDecorations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter));
        Assert.Equal(2, decorations.Count);
        Assert.True(decorations[0].Rect.Height > 30f);
    }

    [Fact]
    public void RtlTableDirectionFlowsThroughSharedLayoutEditorAndHtml()
    {
        var table = new Table(
            new TableRow(new TableCell("אב"), new TableCell("B")))
        {
            ColumnWidths = new System.Collections.Generic.List<float> { 80f, 80f },
            FlowDirection = FlowDirection.RightToLeft
        };
        var document = new RichDocument();
        document.Add(table);
        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            FlowDirection = FlowDirection.LeftToRight,
            Width = 260f,
            Height = 100f
        };
        editor.SetRichDocument(document);
        editor.Measure(new System.Numerics.Vector2(260f, 100f));
        editor.Arrange(new Rect(0f, 0f, 260f, 100f));

        RichTextBlock presenter = Assert.IsType<RichTextBlock>(
            typeof(RichEditBox).GetField("_blockView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        PositionedRichChar hebrew = presenter.PositionedChars.First(static value => value.Info.Character == 'א');
        PositionedRichChar latin = presenter.PositionedChars.Single(static value => value.Info.Character == 'B');
        Assert.True(hebrew.Position.X > latin.Position.X);
        var decorations = Assert.IsType<System.Collections.Generic.List<TableVisualDecoration>>(
            typeof(RichTextBlock).GetField("_tableDecorations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter));
        Assert.True(decorations[0].Rect.X > decorations[1].Rect.X);

        editor.TextDocument.Selection.SetRange(0, 0);
        InputSystem.Current = new WindowInputState();
        InputSystem.SetFocus(editor);
        InputSystem.InjectKeyDown(Key.Tab);
        InputSystem.InjectKeyUp(Key.Tab);
        InputSystem.SetFocus(null);
        Assert.Equal(3, editor.CaretIndex);

        Table snapshot = Assert.IsType<Table>(Assert.Single(editor.CreateRichDocumentSnapshot().Blocks));
        Assert.Equal(FlowDirection.RightToLeft, snapshot.FlowDirection);
        byte[] html = HtmlDocumentCodec.Default.Export(editor.CreateRichDocumentSnapshot());
        Assert.Contains("<table dir=\"rtl\"", System.Text.Encoding.UTF8.GetString(html), StringComparison.Ordinal);
        RichDocument imported = HtmlDocumentCodec.Default.Import(
            html,
            new RichDocumentImportContext(
                InterFontFamily.Regular,
                InterFontFamily.Regular,
                18f,
                new ProGPU.Vector.ThemeResourceBrush("TextPrimary"),
                ElementTheme.Light));
        Assert.Equal(
            FlowDirection.RightToLeft,
            Assert.IsType<Table>(Assert.Single(imported.Blocks)).FlowDirection);
    }

    [Fact]
    public void HorizontalMergedCellsShareSemanticEditorHtmlAndRtfLayout()
    {
        var merged = new TableCell("Merged") { ColumnSpan = 2 };
        var table = new Table(new TableRow(merged, new TableCell("C")))
        {
            ColumnWidths = new System.Collections.Generic.List<float> { 50f, 60f, 70f },
            CellPadding = 4f,
            FlowDirection = FlowDirection.RightToLeft
        };
        var document = new RichDocument();
        document.Add(table);

        var markdownPresenter = new MarkdownTextBlock
        {
            Document = document,
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            Padding = new Thickness(0f)
        };
        markdownPresenter.Measure(new System.Numerics.Vector2(260f, 120f));
        markdownPresenter.Arrange(new Rect(0f, 0f, 260f, 120f));
        var semanticDecorations = Assert.IsType<System.Collections.Generic.List<TableVisualDecoration>>(
            typeof(MarkdownTextBlock).GetField("_tableDecorations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(markdownPresenter));
        Assert.Equal(2, semanticDecorations.Count);
        Assert.Equal(110f, semanticDecorations[0].Rect.Width);
        Assert.Equal(70f, semanticDecorations[1].Rect.Width);
        Assert.True(semanticDecorations[0].Rect.X > semanticDecorations[1].Rect.X);

        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            Width = 260f,
            Height = 120f
        };
        editor.SetRichDocument(document);
        editor.Measure(new System.Numerics.Vector2(260f, 120f));
        editor.Arrange(new Rect(0f, 0f, 260f, 120f));
        Assert.Equal("Merged\tC", editor.Text);
        Table editorSnapshot = Assert.IsType<Table>(Assert.Single(editor.CreateRichDocumentSnapshot().Blocks));
        Assert.Equal(2, editorSnapshot.Rows[0].Cells.Count);
        Assert.Equal(2, editorSnapshot.Rows[0].Cells[0].ColumnSpan);
        RichTextBlock editorPresenter = Assert.IsType<RichTextBlock>(
            typeof(RichEditBox).GetField("_blockView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        var editorDecorations = Assert.IsType<System.Collections.Generic.List<TableVisualDecoration>>(
            typeof(RichTextBlock).GetField("_tableDecorations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editorPresenter));
        Assert.Equal(2, editorDecorations.Count);
        Assert.Equal(110f, editorDecorations[0].Rect.Width);

        editor.TextDocument.Selection.SetRange(0, 0);
        InputSystem.Current = new WindowInputState();
        InputSystem.SetFocus(editor);
        InputSystem.InjectKeyDown(Key.Tab);
        InputSystem.InjectKeyUp(Key.Tab);
        InputSystem.SetFocus(null);
        Assert.Equal(7, editor.CaretIndex);

        byte[] html = HtmlDocumentCodec.Default.Export(document);
        Assert.Contains("colspan=\"2\"", System.Text.Encoding.UTF8.GetString(html), StringComparison.Ordinal);
        byte[] rtf = RtfDocumentCodec.Default.Export(document);
        string rtfText = System.Text.Encoding.UTF8.GetString(rtf);
        Assert.Contains(@"\clmgf", rtfText, StringComparison.Ordinal);
        Assert.Contains(@"\clmrg", rtfText, StringComparison.Ordinal);

        var context = new RichDocumentImportContext(
            InterFontFamily.Regular,
            InterFontFamily.Regular,
            18f,
            new ProGPU.Vector.ThemeResourceBrush("TextPrimary"),
            ElementTheme.Light);
        Table fromHtml = Assert.IsType<Table>(Assert.Single(HtmlDocumentCodec.Default.Import(html, context).Blocks));
        RichDocument fromRtfDocument = RtfDocumentCodec.Default.Import(rtf, context);
        Table fromRtf = Assert.IsType<Table>(Assert.Single(fromRtfDocument.Blocks));
        Assert.Equal(2, fromHtml.Rows[0].Cells[0].ColumnSpan);
        Assert.Equal(2, fromRtf.Rows[0].Cells[0].ColumnSpan);
        Assert.Equal("Merged\tC", PlainTextDocumentExporter.Default.Export(fromRtfDocument));
    }

    [Fact]
    public void VerticalMergedCellsOccupyTheSharedSemanticGridAndHtmlRowSpan()
    {
        var table = new Table(
            new TableRow(
                new TableCell("A") { RowSpan = 2 },
                new TableCell("B")),
            new TableRow(new TableCell("C")),
            new TableRow(new TableCell("D"), new TableCell("E")))
        {
            ColumnWidths = new System.Collections.Generic.List<float> { 70f, 90f },
            CellPadding = 3f,
            BorderThickness = 1f
        };
        var document = new RichDocument();
        document.Add(table);
        var presenter = new MarkdownTextBlock
        {
            Document = document,
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            Padding = new Thickness(0f)
        };
        presenter.Measure(new System.Numerics.Vector2(220f, 180f));
        presenter.Arrange(new Rect(0f, 0f, 220f, 180f));
        var decorations = Assert.IsType<System.Collections.Generic.List<TableVisualDecoration>>(
            typeof(MarkdownTextBlock).GetField("_tableDecorations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter));

        Assert.Equal(5, decorations.Count);
        Assert.Equal(70f, decorations[0].Rect.Width, 3);
        Assert.True(decorations[0].Rect.Height > decorations[1].Rect.Height);
        Assert.Equal(decorations[1].Rect.X, decorations[2].Rect.X, 3);
        Assert.True(decorations[2].Rect.X > decorations[0].Rect.X);

        byte[] html = HtmlDocumentCodec.Default.Export(document);
        string markup = System.Text.Encoding.UTF8.GetString(html);
        Assert.Contains("rowspan=\"2\"", markup, StringComparison.Ordinal);
        Table roundTrip = Assert.IsType<Table>(Assert.Single(HtmlDocumentCodec.Default.Import(
            html,
            new RichDocumentImportContext(
                InterFontFamily.Regular,
                InterFontFamily.Regular,
                18f,
                new ProGPU.Vector.ThemeResourceBrush("TextPrimary"),
                ElementTheme.Light)).Blocks));
        Assert.Equal(2, roundTrip.Rows[0].Cells[0].RowSpan);
        Assert.Single(roundTrip.Rows[1].Cells);

        byte[] rtf = RtfDocumentCodec.Default.Export(document);
        string rtfText = System.Text.Encoding.UTF8.GetString(rtf);
        Assert.Contains(@"\clvmgf", rtfText, StringComparison.Ordinal);
        Assert.Contains(@"\clvmrg", rtfText, StringComparison.Ordinal);
        Table rtfRoundTrip = Assert.IsType<Table>(Assert.Single(RtfDocumentCodec.Default.Import(
            rtf,
            new RichDocumentImportContext(
                InterFontFamily.Regular,
                InterFontFamily.Regular,
                18f,
                new ProGPU.Vector.ThemeResourceBrush("TextPrimary"),
                ElementTheme.Light)).Blocks));
        Assert.Equal(2, rtfRoundTrip.Rows[0].Cells[0].RowSpan);
        Assert.Single(rtfRoundTrip.Rows[1].Cells);

        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            Width = 220f,
            Height = 180f
        };
        editor.SetRichDocument(document);
        editor.Measure(new System.Numerics.Vector2(220f, 180f));
        editor.Arrange(new Rect(0f, 0f, 220f, 180f));
        Assert.Equal("A\tB\n\tC\nD\tE", editor.Text);
        RichTextBlock editorPresenter = Assert.IsType<RichTextBlock>(
            typeof(RichEditBox).GetField("_blockView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        var editorDecorations = Assert.IsType<System.Collections.Generic.List<TableVisualDecoration>>(
            typeof(RichTextBlock).GetField("_tableDecorations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editorPresenter));
        Assert.Equal(6, editorDecorations.Count);
        Assert.False(editorDecorations[0].SuppressTopBorder);
        Assert.True(editorDecorations[0].SuppressBottomBorder);
        Assert.True(editorDecorations[2].SuppressTopBorder);
        Assert.False(editorDecorations[2].SuppressBottomBorder);
        Assert.Equal(editorDecorations[0].Rect.X, editorDecorations[2].Rect.X, 3);
        Assert.Equal(editorDecorations[0].Rect.Y + editorDecorations[0].Rect.Height, editorDecorations[2].Rect.Y, 3);
        Table editorSnapshot = Assert.IsType<Table>(Assert.Single(editor.CreateRichDocumentSnapshot().Blocks));
        Assert.Equal(2, editorSnapshot.Rows[0].Cells[0].RowSpan);
        Assert.Single(editorSnapshot.Rows[1].Cells);

        editor.TextDocument.Selection.SetRange(2, 2);
        InputSystem.Current = new WindowInputState();
        InputSystem.SetFocus(editor);
        InputSystem.InjectKeyDown(Key.Tab);
        InputSystem.InjectKeyUp(Key.Tab);
        Assert.Equal(5, editor.CaretIndex);
        InputSystem.InjectKeyDown(Key.ShiftLeft);
        InputSystem.InjectKeyDown(Key.Tab);
        InputSystem.InjectKeyUp(Key.Tab);
        InputSystem.InjectKeyUp(Key.ShiftLeft);
        InputSystem.SetFocus(null);
        Assert.Equal(2, editor.CaretIndex);
    }

    [Fact]
    public void WordStyleRtfVerticalMergeControlsCollapseContinuationCells()
    {
        const string rtf = @"{\rtf1\ansi
            \pard\trowd\clvmgf\cellx1000\cellx2200\intbl A\cell B\cell\row
            \pard\trowd\clvmrg\cellx1000\cellx2200\intbl \cell C\cell\row}";
        RichDocument document = RtfDocumentCodec.Default.Import(
            System.Text.Encoding.UTF8.GetBytes(rtf),
            new RichDocumentImportContext(
                InterFontFamily.Regular,
                InterFontFamily.Regular,
                18f,
                new ProGPU.Vector.ThemeResourceBrush("TextPrimary"),
                ElementTheme.Light));
        Table table = Assert.IsType<Table>(Assert.Single(document.Blocks));

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(2, table.Rows[0].Cells[0].RowSpan);
        Assert.Single(table.Rows[1].Cells);
        Assert.Equal("C", Assert.IsType<Microsoft.UI.Xaml.Documents.Run>(table.Rows[1].Cells[0].Inlines[0]).Text.Trim());
    }

    [Fact]
    public void NestedHtmlTablesUseTheRecursiveSharedTableLayout()
    {
        var nested = new Table(new TableRow(new TableCell("X"), new TableCell("Y")))
        {
            ColumnWidths = new System.Collections.Generic.List<float> { 42f, 46f },
            CellPadding = 2f,
            BorderThickness = 1f
        };
        var hostCell = new TableCell(new Microsoft.UI.Xaml.Documents.Run("before"), nested, new Microsoft.UI.Xaml.Documents.Run("after"));
        var outer = new Table(new TableRow(hostCell, new TableCell("Z")))
        {
            ColumnWidths = new System.Collections.Generic.List<float> { 120f, 70f },
            CellPadding = 4f,
            BorderThickness = 1f
        };
        var document = new RichDocument();
        document.Add(outer);
        var presenter = new MarkdownTextBlock
        {
            Document = document,
            Font = InterFontFamily.Regular,
            FontSize = 16f,
            Padding = new Thickness(0f)
        };
        presenter.Measure(new System.Numerics.Vector2(240f, 200f));
        presenter.Arrange(new Rect(0f, 0f, 240f, 200f));
        var decorations = Assert.IsType<System.Collections.Generic.List<TableVisualDecoration>>(
            typeof(MarkdownTextBlock).GetField("_tableDecorations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter));

        Assert.Equal(4, decorations.Count);
        Assert.Contains(presenter.PositionedChars, static character => character.Info.Character == 'X');
        Assert.Contains(presenter.PositionedChars, static character => character.Info.Character == 'Y');
        PositionedRichChar nestedX = presenter.PositionedChars.Single(static character => character.Info.Character == 'X');
        Assert.InRange(nestedX.Position.X, decorations[0].Rect.X, decorations[0].Rect.X + decorations[0].Rect.Width);
        Assert.InRange(nestedX.Position.Y, decorations[0].Rect.Y, decorations[0].Rect.Y + decorations[0].Rect.Height);

        byte[] html = HtmlDocumentCodec.Default.Export(document);
        string markup = System.Text.Encoding.UTF8.GetString(html);
        Assert.Equal(2, markup.Split("<table", StringSplitOptions.None).Length - 1);
        Table importedOuter = Assert.IsType<Table>(Assert.Single(HtmlDocumentCodec.Default.Import(
            html,
            new RichDocumentImportContext(
                InterFontFamily.Regular,
                InterFontFamily.Regular,
                16f,
                new ProGPU.Vector.ThemeResourceBrush("TextPrimary"),
                ElementTheme.Light)).Blocks));
        Assert.Contains(importedOuter.Rows[0].Cells[0].Inlines, static inline => inline is Table);
    }

    [Fact]
    public void DeletingTableRowPreservesFollowingParagraphKind()
    {
        var source = new RichDocument();
        source.Add(new Table(new TableRow(new TableCell("A"), new TableCell("B")))
        {
            ColumnWidths = new System.Collections.Generic.List<float> { 72f, 96f }
        });
        source.Add(new Paragraph(new Run("plain")));
        var editor = new RichEditBox();
        editor.SetRichDocument(source);
        editor.TextDocument.ClearUndoRedoHistory();

        ITextRange row = editor.TextDocument.GetRange(0, 4);
        row.Delete(TextRangeUnit.Character, 0);

        RichDocument snapshot = editor.CreateRichDocumentSnapshot();
        Paragraph paragraph = Assert.IsType<Paragraph>(Assert.Single(snapshot.Blocks));
        Assert.Equal("plain", Assert.IsType<Run>(Assert.Single(paragraph.Inlines)).Text);

        editor.Undo();
        Assert.Equal("A\tB\nplain", editor.Text);
        Assert.Equal("Table,Paragraph", string.Join(',', editor.CreateRichDocumentSnapshot().Blocks.Select(static block => block.GetType().Name)));

        editor.TextDocument.ClearUndoRedoHistory();
        editor.TextDocument.Selection.SetRange(4, 4);
        InputSystem.Current = new WindowInputState();
        InputSystem.SetFocus(editor);
        InputSystem.InjectKeyDown(Key.Backspace);
        InputSystem.InjectKeyUp(Key.Backspace);
        InputSystem.SetFocus(null);
        Assert.Equal("A\tBplain", editor.Text);

        editor.Undo();
        Assert.Equal("A\tB\nplain", editor.Text);
        Assert.Equal("Table,Paragraph", string.Join(',', editor.CreateRichDocumentSnapshot().Blocks.Select(static block => block.GetType().Name)));
    }

    [Fact]
    public void RtfDocumentCodecRoundTripsStructuralListsAndTables()
    {
        var list = new ListBlock { IsOrdered = true, Indentation = 28f };
        list.Items.Add(new ListItem(new Bold(new Run("First"))));
        list.Items.Add(new ListItem(new Italic(new Run("Second"))));
        var highlightedCell = new TableCell(new Bold(new Run("A")))
        {
            Background = new ProGPU.Vector.SolidColorBrush(new System.Numerics.Vector4(0.2f, 0.7f, 0.3f, 1f))
        };
        var table = new Table(
            new TableRow(highlightedCell, new TableCell("B")),
            new TableRow(new TableCell("C"), new TableCell(new Italic(new Run("D")))))
        {
            ColumnWidths = new System.Collections.Generic.List<float> { 70f, 110f },
            CellPadding = 5f,
            BorderThickness = 2f,
            BorderBrush = new ProGPU.Vector.SolidColorBrush(new System.Numerics.Vector4(0.8f, 0.1f, 0.2f, 1f)),
            FlowDirection = FlowDirection.RightToLeft
        };
        var source = new RichDocument();
        source.Add(list);
        source.Add(table);

        byte[] encoded = RtfDocumentCodec.Default.Export(source);
        string rtf = System.Text.Encoding.UTF8.GetString(encoded);
        Assert.Contains(@"\pn", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\trowd", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\cellx1400", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\cellx3600", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\cell", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\row", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\rtlrow", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\clpadl100\clpadfl3", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\brdrw40", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\clcbpat", rtf, StringComparison.Ordinal);
        Assert.Equal(2, rtf.Split(@"\row", StringSplitOptions.None).Length - 1);

        var context = new RichDocumentImportContext(
            InterFontFamily.Regular,
            InterFontFamily.Regular,
            16f,
            new ProGPU.Vector.SolidColorBrush(0x000000FF),
            ElementTheme.Light);
        RichDocument imported = RtfDocumentCodec.Default.Import(encoded, context);

        Assert.Equal("ListBlock,Table", string.Join(',', imported.Blocks.Select(static block => block.GetType().Name)));
        ListBlock importedList = Assert.IsType<ListBlock>(imported.Blocks[0]);
        Assert.True(importedList.IsOrdered);
        Assert.Equal(2, importedList.Items.Count);
        Assert.IsType<Bold>(importedList.Items[0].Inlines[0]);
        Assert.IsType<Italic>(importedList.Items[1].Inlines[0]);
        Table importedTable = Assert.IsType<Table>(imported.Blocks[1]);
        Assert.Equal("A,B|C,D", string.Join('|', importedTable.Rows.Select(static row =>
            string.Join(',', row.Cells.Select(static cell => string.Concat(cell.Inlines.Select(GetInlineText)))))));
        Assert.Equal(2, importedTable.Rows[0].Cells.Count);
        Assert.Equal(new[] { 70f, 110f }, importedTable.ColumnWidths);
        Assert.Equal(5f, importedTable.CellPadding);
        Assert.Equal(2f, importedTable.BorderThickness);
        Assert.Equal(FlowDirection.RightToLeft, importedTable.FlowDirection);
        Assert.IsType<ProGPU.Vector.SolidColorBrush>(importedTable.BorderBrush);
        Assert.IsType<ProGPU.Vector.SolidColorBrush>(importedTable.Rows[0].Cells[0].Background);
        Assert.IsType<Bold>(importedTable.Rows[0].Cells[0].Inlines[0]);
        Assert.IsType<Italic>(importedTable.Rows[1].Cells[1].Inlines[0]);
        Assert.Equal("A", Assert.IsType<Run>(Assert.IsType<Bold>(importedTable.Rows[0].Cells[0].Inlines[0]).Inlines[0]).Text);
        Assert.Equal("D", Assert.IsType<Run>(Assert.IsType<Italic>(importedTable.Rows[1].Cells[1].Inlines[0]).Inlines[0]).Text);

        static string GetInlineText(Inline inline) => inline switch
        {
            Run run => run.Text,
            Span span => string.Concat(span.Inlines.Select(GetInlineText)),
            _ => string.Empty
        };
    }

    [Fact]
    public void RtfDocumentCodecImportsStandardWordListsAndTableRows()
    {
        const string rtf = """
            {\rtf1\ansi\uc1
            {\colortbl;\red20\green40\blue60;\red100\green150\blue200;}
            {\*\listtable{\list\listtemplateid1{\listlevel\levelnfc0\levelnfcn0\leveljc0\levelfollow0{\leveltext\'02\'00.;}{\levelnumbers\'01;}}\listid42}}
            {\*\listoverridetable{\listoverride\listid42\listoverridecount0\ls7}}
            \pard\plain\ls7\ilvl0{\listtext 1.\tab}First\par
            \pard\plain\ls7\ilvl0{\listtext 2.\tab}\b Second\b0\par
            \pard\trowd\rtlrow\clmgf\clpadl100\clpadfl3\clbrdrt\brdrs\brdrw40\brdrcf1\clcbpat2\cellx1000\clmrg\cellx2200\cellx3000\intbl A\cell\cell \i B\i0\cell\row}
            """;
        var context = new RichDocumentImportContext(
            InterFontFamily.Regular,
            InterFontFamily.Regular,
            16f,
            new ProGPU.Vector.SolidColorBrush(0x000000FF),
            ElementTheme.Light);

        RichDocument imported = RtfDocumentCodec.Default.Import(
            System.Text.Encoding.UTF8.GetBytes(rtf),
            context);

        Assert.Equal("ListBlock,Table", string.Join(',', imported.Blocks.Select(static block => block.GetType().Name)));
        ListBlock list = Assert.IsType<ListBlock>(imported.Blocks[0]);
        Assert.True(list.IsOrdered);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("First", Assert.IsType<Run>(list.Items[0].Inlines[0]).Text);
        Bold second = Assert.IsType<Bold>(list.Items[1].Inlines[0]);
        Assert.Equal("Second", Assert.IsType<Run>(second.Inlines[0]).Text);
        Table table = Assert.IsType<Table>(imported.Blocks[1]);
        TableRow row = Assert.Single(table.Rows);
        Assert.Equal(2, row.Cells.Count);
        Assert.Equal(new[] { 50f, 60f, 40f }, table.ColumnWidths);
        Assert.Equal(2, row.Cells[0].ColumnSpan);
        Assert.Equal(5f, table.CellPadding);
        Assert.Equal(2f, table.BorderThickness);
        Assert.Equal(FlowDirection.RightToLeft, table.FlowDirection);
        Assert.IsType<ProGPU.Vector.SolidColorBrush>(table.BorderBrush);
        Assert.IsType<ProGPU.Vector.SolidColorBrush>(row.Cells[0].Background);
        Assert.Equal("A", Assert.IsType<Run>(row.Cells[0].Inlines[0]).Text);
        Italic secondCell = Assert.IsType<Italic>(row.Cells[1].Inlines[0]);
        Assert.Equal("B", Assert.IsType<Run>(secondCell.Inlines[0]).Text);
    }

    [Fact]
    public void RichTextBufferEditsAndUndoSnapshotsScaleWithStyledRuns()
    {
        var normal = new RichTextStyle(null, 16f);
        var bold = normal with { IsBold = true };
        var buffer = new RichTextBuffer();
        buffer.SetText(new string('a', 100_000), normal);
        RichTextBufferSnapshot snapshot = buffer.CreateSnapshot();

        buffer.Insert(50_000, "B", bold);

        Assert.Equal(100_001, buffer.Length);
        Assert.Equal(3, buffer.Spans.Count);
        Assert.Equal('B', buffer[50_000]);
        buffer.Restore(snapshot);
        Assert.Equal(100_000, buffer.Length);
        Assert.Single(buffer.Spans);

        buffer.Insert(50_000, "x", normal);
        Assert.Equal(3, buffer.Spans.Count);
        Assert.Equal(50_000, buffer.Spans[0].Text.Length);
        Assert.Equal(50_000, buffer.Spans[2].Text.Length);
    }

    [Fact]
    public void RichEditTextDocumentEditsTheSameRunBufferUsedByThePresenter()
    {
        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            MaxLength = 9,
            AcceptsReturn = false,
            CharacterCasing = CharacterCasing.Upper,
            DisabledFormattingAccelerators = DisabledFormattingAccelerators.Italic
        };

        editor.TextDocument.SetText(TextSetOptions.None, "abc אבג");
        ITextRange latin = editor.TextDocument.GetRange(0, 3);
        latin.ChangeCase(LetterCase.Upper);
        latin.CharacterFormat.Bold = FormatEffect.On;
        editor.TextDocument.Selection.SetRange(3, 3);
        editor.TextDocument.Selection.TypeText("12");
        editor.TextDocument.GetText(TextGetOptions.UseLf, out string value);

        Assert.Equal("ABC12 אבג", value);
        Assert.Equal(value, editor.Text);
        Assert.Equal(5, editor.SelectionStart);
        Assert.Equal(0, editor.SelectionLength);
        Assert.True(editor.TextDocument.CanUndo());
        Assert.Equal(FormatEffect.On, editor.TextDocument.GetRange(0, 1).CharacterFormat.Bold);
        Assert.Same(editor.TextDocument, editor.Document);
    }

    [Fact]
    public void RichEditBoxWinUiEditingPropertiesExposeExpectedDefaults()
    {
        var editor = new RichEditBox();

        Assert.True(editor.AcceptsReturn);
        Assert.Equal(CharacterCasing.Normal, editor.CharacterCasing);
        Assert.Equal(RichEditClipboardFormat.AllFormats, editor.ClipboardCopyFormat);
        Assert.Equal(ControlHeaderPlacement.Top, editor.HeaderPlacement);
        Assert.Equal(DisabledFormattingAccelerators.None, editor.DisabledFormattingAccelerators);
        Assert.True(editor.IsColorFontEnabled);
        Assert.True(editor.IsSpellCheckEnabled);
        Assert.True(editor.IsTextPredictionEnabled);
        Assert.False(editor.IsReadOnly);
        Assert.Equal(0, editor.MaxLength);

        editor.HeaderPlacement = ControlHeaderPlacement.Left;
        Assert.Equal(ControlHeaderPlacement.Left, editor.HeaderPlacement);
        editor.Header = new Border { Width = 32f, Height = 14f };
        editor.Font = InterFontFamily.Regular;
        editor.Measure(new System.Numerics.Vector2(240f, 120f));
        editor.Arrange(new Rect(0f, 0f, 240f, 120f));
        ScrollViewer editorScroll = Assert.Single(editor.Children.OfType<ScrollViewer>());
        Assert.True(editorScroll.Offset.X > 0f);

        editor.HeaderPlacement = ControlHeaderPlacement.Top;
        editor.Measure(new System.Numerics.Vector2(240f, 120f));
        editor.Arrange(new Rect(0f, 0f, 240f, 120f));
        Assert.True(editorScroll.Offset.Y > 0f);
        Assert.Equal(string.Empty, editor.Text);
        Assert.Null(editor.HeaderTemplate);
        Assert.False(editor.PreventKeyboardDisplayOnProgrammaticFocus);
        Assert.Null(editor.ProofingMenuFlyout);
        Assert.Null(editor.SelectionFlyout);
        Assert.Null(editor.SelectionHighlightColorWhenNotFocused);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.MaxLength = -1);
    }

    [Fact]
    public void RichEditDisplayUpdatesBatchProjectionAndPreserveSynchronousDocumentAccess()
    {
        var editor = new RichEditBox { Text = "base" };
        RichEditTextDocument document = editor.TextDocument;
        int changing = 0;
        int changed = 0;
        bool sawUpdatedDocument = false;
        editor.TextChanging += (_, args) =>
        {
            changing++;
            sawUpdatedDocument = args.IsContentChanging && editor.Text == "base one";
        };
        editor.TextChanged += (_, _) => changed++;

        Assert.Equal(1, document.BatchDisplayUpdates());
        Assert.Equal(2, document.BatchDisplayUpdates());
        document.GetRange(4, 4).Text = " one";

        Assert.Equal("base one", editor.Text);
        Assert.True(sawUpdatedDocument);
        Assert.Equal(1, changing);
        Assert.Equal(0, changed);
        Assert.Equal(1, document.ApplyDisplayUpdates());
        Assert.Equal(0, changed);
        Assert.Equal(0, document.ApplyDisplayUpdates());
        Assert.Equal(1, changed);
        Assert.Equal(0, document.ApplyDisplayUpdates());
    }

    [Fact]
    public void RichEditSelectionChangingCanCancelTheProposedSelection()
    {
        var editor = new RichEditBox { Text = "abcdef" };
        RichEditBoxSelectionChangingEventArgs? proposed = null;
        editor.SelectionChanging += (_, args) =>
        {
            proposed = args;
            args.Cancel = true;
        };

        editor.TextDocument.Selection.SetRange(2, 5);

        Assert.NotNull(proposed);
        Assert.Equal(2, proposed!.SelectionStart);
        Assert.Equal(3, proposed.SelectionLength);
        Assert.Equal(0, editor.SelectionStart);
        Assert.Equal(0, editor.SelectionLength);
    }

    [Fact]
    public void RichEditFormattedTextAssignmentPreservesStyledRuns()
    {
        var editor = new RichEditBox { Text = "ab--" };
        ITextRange source = editor.TextDocument.GetRange(0, 2);
        source.CharacterFormat.Bold = FormatEffect.On;
        ITextRange target = editor.TextDocument.GetRange(2, 4);

        target.FormattedText = source;

        Assert.Equal("abab", editor.Text);
        Assert.Equal(FormatEffect.On, editor.TextDocument.GetRange(2, 4).CharacterFormat.Bold);
    }

    [Fact]
    public void RichEditRtfRoundTripsUnicodeParagraphsAndRunFormatting()
    {
        var source = new RichEditBox { Text = "Hello אבג\nworld" };
        source.TextDocument.GetRange(6, 9).CharacterFormat.Bold = FormatEffect.On;
        source.TextDocument.GetRange(10, 15).CharacterFormat.Italic = FormatEffect.On;
        source.TextDocument.GetText(TextGetOptions.FormatRtf, out string rtf);

        var destination = new RichEditBox();
        destination.TextDocument.SetText(TextSetOptions.FormatRtf, rtf);

        Assert.StartsWith(@"{\rtf1", rtf, StringComparison.Ordinal);
        Assert.Equal(source.Text, destination.Text);
        Assert.Equal(FormatEffect.On, destination.TextDocument.GetRange(6, 9).CharacterFormat.Bold);
        Assert.Equal(FormatEffect.On, destination.TextDocument.GetRange(10, 15).CharacterFormat.Italic);
    }

    [Fact]
    public void RichEditRtfRoundTripsFontAndSolidColors()
    {
        var source = new RichEditBox { Text = "styled" };
        ITextCharacterFormat format = source.TextDocument.GetRange(0, 6).CharacterFormat;
        format.Name = InterFontFamily.Regular.FamilyName;
        format.ForegroundColor = Windows.UI.Color.FromArgb(255, 17, 34, 51);
        format.BackgroundColor = Windows.UI.Color.FromArgb(255, 204, 221, 238);
        source.TextDocument.GetText(TextGetOptions.FormatRtf, out string rtf);

        var destination = new RichEditBox();
        destination.TextDocument.SetText(TextSetOptions.FormatRtf, rtf);
        ITextCharacterFormat restored = destination.TextDocument.GetRange(0, 6).CharacterFormat;

        Assert.Contains(@"\colortbl", rtf, StringComparison.Ordinal);
        Assert.Equal(format.Name, restored.Name);
        Assert.Equal(format.ForegroundColor, restored.ForegroundColor);
        Assert.Equal(format.BackgroundColor, restored.BackgroundColor);
    }

    [Fact]
    public void RichEditClipboardEventsCanReplaceDefaultOperations()
    {
        var editor = new RichEditBox { Text = "text" };
        editor.SelectionStart = 4;
        bool pasteRaised = false;
        editor.Paste += (_, args) =>
        {
            pasteRaised = true;
            args.Handled = true;
            editor.TextDocument.Selection.TypeText(" custom");
        };

        editor.PasteFromClipboard();

        Assert.True(pasteRaised);
        Assert.Equal("text custom", editor.Text);
    }

    [Fact]
    public void RichEditClipboardAllFormatsPreservesStyledSpans()
    {
        string clipboard = string.Empty;
        RichClipboardPayload? richClipboard = null;
        Action<string>? priorSet = ClipboardHelper.PlatformSetText;
        Func<string>? priorGet = ClipboardHelper.PlatformGetText;
        Action<RichClipboardPayload>? priorRichSet = ClipboardHelper.PlatformSetRichText;
        Func<RichClipboardPayload?>? priorRichGet = ClipboardHelper.PlatformGetRichText;
        ClipboardHelper.PlatformSetText = value => clipboard = value;
        ClipboardHelper.PlatformGetText = () => clipboard;
        ClipboardHelper.PlatformSetRichText = value => richClipboard = value;
        ClipboardHelper.PlatformGetRichText = () => richClipboard;
        try
        {
            var source = new RichEditBox { Text = "bold\nnext" };
            source.TextDocument.GetRange(0, 4).CharacterFormat.Bold = FormatEffect.On;
            ITextParagraphFormat sourceParagraph = source.TextDocument.GetRange(0, 4).ParagraphFormat;
            sourceParagraph.Alignment = ParagraphAlignment.Center;
            sourceParagraph.RightToLeft = FormatEffect.On;
            source.TextDocument.Selection.SetRange(0, source.Text.Length);
            source.Copy();
            Assert.NotNull(richClipboard);
            Assert.Equal("bold\nnext", richClipboard.PlainText);
            Assert.StartsWith(@"{\rtf1", richClipboard.Rtf, StringComparison.Ordinal);
            Assert.Contains("<strong>", richClipboard.Html, StringComparison.Ordinal);
            Assert.Contains("bold", richClipboard.Html, StringComparison.Ordinal);

            var destination = new RichEditBox();
            destination.PasteFromClipboard();

            Assert.Equal("bold\nnext", destination.Text);
            Assert.Equal(FormatEffect.On, destination.TextDocument.GetRange(0, 4).CharacterFormat.Bold);
            Assert.Equal(ParagraphAlignment.Center, destination.TextDocument.GetRange(0, 4).ParagraphFormat.Alignment);
            Assert.Equal(FormatEffect.On, destination.TextDocument.GetRange(0, 4).ParagraphFormat.RightToLeft);
        }
        finally
        {
            ClipboardHelper.PlatformSetText = priorSet;
            ClipboardHelper.PlatformGetText = priorGet;
            ClipboardHelper.PlatformSetRichText = priorRichSet;
            ClipboardHelper.PlatformGetRichText = priorRichGet;
        }
    }

    [Fact]
    public void RichEditClipboardImportsNativeHtmlWhenRtfIsUnavailable()
    {
        Action<string>? priorSet = ClipboardHelper.PlatformSetText;
        Func<string>? priorGet = ClipboardHelper.PlatformGetText;
        Action<RichClipboardPayload>? priorRichSet = ClipboardHelper.PlatformSetRichText;
        Func<RichClipboardPayload?>? priorRichGet = ClipboardHelper.PlatformGetRichText;
        ClipboardHelper.PlatformSetText = _ => { };
        ClipboardHelper.PlatformGetText = () => "native HTML";
        ClipboardHelper.PlatformSetRichText = _ => { };
        ClipboardHelper.PlatformGetRichText = () => new RichClipboardPayload(
            "native HTML",
            string.Empty,
            "<p><strong>native</strong> <em>HTML</em></p>");
        try
        {
            var editor = new RichEditBox
            {
                Font = InterFontFamily.Regular,
                FontSize = 16f,
                Foreground = new ProGPU.Vector.SolidColorBrush(0x000000FF)
            };

            editor.PasteFromClipboard();

            Assert.Equal("native HTML", editor.Text);
            Assert.Equal(FormatEffect.On, editor.TextDocument.GetRange(0, 6).CharacterFormat.Bold);
            Assert.Equal(FormatEffect.On, editor.TextDocument.GetRange(7, 11).CharacterFormat.Italic);
        }
        finally
        {
            ClipboardHelper.PlatformSetText = priorSet;
            ClipboardHelper.PlatformGetText = priorGet;
            ClipboardHelper.PlatformSetRichText = priorRichSet;
            ClipboardHelper.PlatformGetRichText = priorRichGet;
        }
    }

    [Fact]
    public void WindowsClipboardHtmlOffsetsRoundTripUnicodeFragments()
    {
        const string fragment = "<p dir=\"rtl\"><strong>אבג</strong> 😀</p>";
        Type adapter = typeof(ClipboardHelper).Assembly.GetType(
            "Microsoft.UI.Xaml.WindowsRichClipboard",
            throwOnError: true)!;
        MethodInfo encode = adapter.GetMethod("BuildCfHtml", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo decode = adapter.GetMethod("DecodeCfHtml", BindingFlags.Static | BindingFlags.NonPublic)!;

        byte[] encoded = Assert.IsType<byte[]>(encode.Invoke(null, [fragment]));

        Assert.Equal(fragment, Assert.IsType<string>(decode.Invoke(null, [encoded])));
        Assert.Contains("StartFragment:", System.Text.Encoding.ASCII.GetString(encoded), StringComparison.Ordinal);
    }

    [Fact]
    public void RichEditAutomationPeerExposesValueSelectionAndVirtualizedVisibleText()
    {
        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            Text = string.Join('\n', Enumerable.Range(0, 500).Select(static index => $"line {index}"))
        };
        editor.Measure(new System.Numerics.Vector2(360f, 180f));
        editor.Arrange(new Rect(0f, 0f, 360f, 180f));
        editor.TextDocument.Selection.SetRange(2, 6);

        var peer = Assert.IsType<Microsoft.UI.Xaml.Automation.Peers.RichEditBoxAutomationPeer>(
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(editor));
        var value = Assert.IsAssignableFrom<Microsoft.UI.Xaml.Automation.Provider.IValueProvider>(peer);
        var text = Assert.IsAssignableFrom<Microsoft.UI.Xaml.Automation.Provider.ITextProvider>(peer);

        Assert.Equal(editor.Text, value.Value);
        Assert.Equal("ne 0", text.GetSelection()[0].GetText());
        Assert.True(text.GetVisibleRanges()[0].GetText().Length < editor.Text.Length);
        Assert.Equal(Microsoft.UI.Xaml.Automation.Peers.AutomationControlType.Document, peer.GetAutomationControlType());

        Microsoft.UI.Xaml.Automation.Provider.ITextRangeProvider documentRange = text.DocumentRange;
        Microsoft.UI.Xaml.Automation.Provider.ITextRangeProvider word = documentRange.FindText("line 12", backward: false, ignoreCase: false)!;
        Assert.Equal("line 12", word.GetText());
        word.ExpandToEnclosingUnit(Microsoft.UI.Xaml.Automation.Text.TextUnit.Line);
        Assert.StartsWith("line 12", word.GetText(), StringComparison.Ordinal);
        word.GetBoundingRectangles(out double[] bounds);
        Assert.Equal(0, bounds.Length % 4);
        Assert.True(word.Compare(word.Clone()));
    }

    [Fact]
    public void RichEditAutomationReportsFormattingAndPerLineRectangles()
    {
        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            Text = "one two three four five six seven eight nine ten"
        };
        ITextRange styled = editor.TextDocument.GetRange(4, 7);
        styled.CharacterFormat.Italic = FormatEffect.On;
        styled.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(255, 12, 34, 56);
        editor.Measure(new System.Numerics.Vector2(95f, 300f));
        editor.Arrange(new Rect(0f, 0f, 95f, 300f));

        var provider = Assert.IsAssignableFrom<Microsoft.UI.Xaml.Automation.Provider.ITextProvider>(
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(editor));
        var three = provider.DocumentRange.FindText("two", backward: false, ignoreCase: false)!;
        Assert.Equal(true, three.GetAttributeValue((int)AutomationTextAttributesEnum.IsItalicAttribute));
        Assert.NotNull(provider.DocumentRange.FindAttribute(
            (int)AutomationTextAttributesEnum.IsItalicAttribute,
            true,
            backward: false));
        Assert.Equal(2, provider.DocumentRange.Clone().Move(
            Microsoft.UI.Xaml.Automation.Text.TextUnit.Word,
            2));

        provider.DocumentRange.GetBoundingRectangles(out double[] rectangles);
        Assert.True(rectangles.Length > 4);
        Assert.Equal(0, rectangles.Length % 4);
    }

    [Fact]
    public async System.Threading.Tasks.Task RichEditBoxExposesContextMenuAndLinguisticParitySurface()
    {
        var editor = new RichEditBox { Text = "text" };
        bool contextOpening = false;
        editor.ContextMenuOpening += (_, args) =>
        {
            contextOpening = true;
            args.Handled = true;
        };
        var pointer = new PointerRoutedEventArgs
        {
            Position = new System.Numerics.Vector2(4f, 5f),
            IsRightButtonPressed = true
        };

        editor.OnPointerPressed(pointer);

        Assert.True(contextOpening);
        Assert.True(pointer.Handled);
        Assert.Empty(await editor.GetLinguisticAlternativesAsync().AsTask());
    }

    [Fact]
    public void TableColumnWidthMutationInvalidatesOwningDocument()
    {
        var table = new Table(new TableRow(new TableCell("A"), new TableCell("B")))
        {
            ColumnWidths = new System.Collections.Generic.List<float> { 60f, 80f }
        };
        var document = new RichDocument();
        document.Add(table);
        long version = document.Version;

        table.ColumnWidths![1] = 120f;

        Assert.True(document.Version > version);
    }

    [Fact]
    public void RichEditBoxVirtualizesLargeMultilineDocumentsByParagraph()
    {
        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            FontSize = 16f,
            Text = string.Join('\n', Enumerable.Range(0, 2_000).Select(static index => $"line {index}"))
        };

        editor.Measure(new System.Numerics.Vector2(420f, 240f));
        editor.Arrange(new Rect(0f, 0f, 420f, 240f));

        Assert.InRange(editor.LayoutSession.RealizedBlockCount, 1, 300);
    }

    [Fact]
    public void RichEditorParagraphProjectionPreservesGlobalUtf16OffsetsAndBlankLines()
    {
        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            FontSize = 16f,
            Text = "a\n\nb"
        };
        editor.Measure(new System.Numerics.Vector2(240f, 180f));
        editor.Arrange(new Rect(0f, 0f, 240f, 180f));
        RichTextBlock presenter = Assert.IsType<RichTextBlock>(
            typeof(RichEditBox).GetField("_blockView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        PositionedRichChar first = presenter.PositionedChars.Single(static character => character.Info.Character == 'a');
        PositionedRichChar last = presenter.PositionedChars.Single(static character => character.Info.Character == 'b');

        Assert.Equal(0, first.Info.TextPosition);
        Assert.Equal(3, last.Info.TextPosition);
        Assert.True(last.Position.Y >= first.Position.Y + editor.FontSize * 2f);

        MethodInfo hitTest = typeof(RichEditBox).GetMethod(
            "GetCharacterIndexAt",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] args = { 0f, (first.Position.Y + last.Position.Y) * 0.5f, false };
        int blankLinePosition = (int)hitTest.Invoke(editor, args)!;
        Assert.Equal(2, blankLinePosition);
    }

    [Fact]
    public void RichEditDocumentUndoGroupsAndLimitApplyToRunSnapshots()
    {
        var editor = new RichEditBox { Text = "base" };
        RichEditTextDocument document = editor.TextDocument;
        document.ClearUndoRedoHistory();
        document.BeginUndoGroup();
        document.GetRange(4, 4).Text = " one";
        document.GetRange(8, 8).Text = " two";
        document.EndUndoGroup();

        Assert.Equal("base one two", editor.Text);
        document.Undo();
        Assert.Equal("base", editor.Text);
        Assert.False(document.CanUndo());

        document.UndoLimit = 1;
        document.GetRange(4, 4).Text = " A";
        document.GetRange(6, 6).Text = " B";
        document.Undo();
        Assert.Equal("base A", editor.Text);
        Assert.False(document.CanUndo());
    }

    [Fact]
    public void RichEditTextRangesMoveFindExpandAndDeleteLogicalUnits()
    {
        const string family = "👨‍👩‍👧‍👦";
        var editor = new RichEditBox { Text = $"one two\n{family} three" };
        ITextRange range = editor.TextDocument.GetRange(5, 5);

        range.Expand(TextRangeUnit.Word);
        Assert.Equal("two", range.Text);
        Assert.Equal(5, range.FindText("THREE", editor.Text.Length, FindOptions.None));
        Assert.Equal("three", range.Text);
        range.SetRange(8 + family.Length, 8 + family.Length);
        int removed = range.Delete(TextRangeUnit.Character, -1);

        Assert.Equal(-1, removed);
        Assert.Equal("one two\n three", editor.Text);

        range.SetRange(0, 0);
        Assert.Equal(2, range.Move(TextRangeUnit.Word, 2));
        Assert.Equal(7, range.StartPosition);
    }

    [Fact]
    public void RichEditTextRangesMatchTomWordSentenceAndParagraphUnits()
    {
        var editor = new RichEditBox { Text = "One,  two!\r\nNext." };
        ITextRange range = editor.TextDocument.GetRange(0, 0);

        range.SetIndex(TextRangeUnit.Word, 2, extend: true);
        Assert.Equal(",  ", range.Text);
        Assert.Equal(2, range.GetIndex(TextRangeUnit.Word));

        // The final implicit EOP is the last TOM word unit; -2 addresses the
        // preceding punctuation unit while ordinary Text retrieval hides the EOP.
        range.SetIndex(TextRangeUnit.Word, -2, extend: true);
        Assert.Equal(".", range.Text);
        range.SetIndex(TextRangeUnit.Word, 3, extend: false);
        Assert.Equal(6, range.StartPosition);
        Assert.Equal(0, range.Length);

        range.SetRange(4, 4);
        Assert.Equal(12, range.Expand(TextRangeUnit.Sentence));
        Assert.Equal("One,  two!\r\n", range.Text);

        range.SetIndex(TextRangeUnit.Paragraph, 1, extend: true);
        Assert.Equal("One,  two!\r\n", range.Text);
        range.SetIndex(TextRangeUnit.Paragraph, -1, extend: true);
        Assert.Equal("Next.", range.Text);
    }

    [Fact]
    public void RichEditTextRangeMoveCollapseAndEndpointsMatchTomContracts()
    {
        var editor = new RichEditBox { Text = "one two" };
        ITextRange range = editor.TextDocument.GetRange(1, 4);

        Assert.Equal(1, range.Move(TextRangeUnit.Word, 1));
        Assert.Equal(4, range.StartPosition);
        Assert.Equal(0, range.Length);

        range.SetRange(1, 2);
        Assert.Equal(1, range.Move(TextRangeUnit.Word, 1));
        Assert.Equal(4, range.StartPosition);

        range.SetRange(5, 6);
        Assert.Equal(-1, range.Move(TextRangeUnit.Word, -1));
        Assert.Equal(4, range.StartPosition);

        range.SetRange(2, 5);
        range.StartPosition = 6;
        Assert.Equal(6, range.StartPosition);
        Assert.Equal(6, range.EndPosition);
        range.SetRange(2, 5);
        range.EndPosition = 1;
        Assert.Equal(1, range.StartPosition);
        Assert.Equal(1, range.EndPosition);
    }

    [Fact]
    public void RichEditTextRangeCharacterDeleteAndStoryIdentityMatchTomContracts()
    {
        var editor = new RichEditBox { Text = "abcdef" };
        ITextRange range = editor.TextDocument.GetRange(1, 4);

        Assert.Equal('b', range.Character);
        range.Character = 'X';
        Assert.Equal("aXcdef", editor.Text);
        Assert.Equal(1, range.StartPosition);
        Assert.Equal(4, range.EndPosition);

        editor.Text = "one two three";
        range = editor.TextDocument.GetRange(0, 4);
        Assert.Equal(2, range.Delete(TextRangeUnit.Word, 2));
        Assert.Equal("three", editor.Text);

        var other = new RichEditBox { Text = "three" };
        ITextRange samePositionsElsewhere = other.TextDocument.GetRange(0, 5);
        range.SetRange(0, 5);
        Assert.False(range.InRange(samePositionsElsewhere));
        Assert.False(range.IsEqual(samePositionsElsewhere));
    }

    [Fact]
    public void RichEditTextRangeEndOfDoesNotCrossAnAlignedUnitBoundary()
    {
        var editor = new RichEditBox { Text = "one two" };
        ITextRange range = editor.TextDocument.GetRange(0, 4);

        Assert.Equal(0, range.EndOf(TextRangeUnit.Word, extend: true));
        Assert.Equal("one ", range.Text);
        Assert.Equal(0, range.EndOf(TextRangeUnit.Word, extend: false));
        Assert.Equal(4, range.StartPosition);
        Assert.Equal(0, range.Length);
    }

    [Fact]
    public void RichEditTextRangeFindTextSkipsCurrentMatchAndHonorsZeroAndWholeWordScopes()
    {
        var editor = new RichEditBox { Text = "alpha a\u0301 alpha beta alpha" };
        ITextRange range = editor.TextDocument.GetRange(0, 5);

        Assert.Equal(5, range.FindText("alpha", editor.Text.Length, FindOptions.Word));
        Assert.Equal(9, range.StartPosition);
        Assert.Equal(5, range.FindText("alpha", -editor.Text.Length, FindOptions.Word));
        Assert.Equal(0, range.StartPosition);

        range.SetRange(0, 20);
        Assert.Equal(4, range.FindText("beta", 0, FindOptions.Word));
        Assert.Equal(15, range.StartPosition);

        range.SetRange(0, 0);
        Assert.Equal(2, range.FindText("a\u0301", 0, FindOptions.Word));
        Assert.Equal(6, range.StartPosition);
    }

    [Fact]
    public void RichEditTextGetOptionsAdjustClustersAndRejectConflictingNewlineFlags()
    {
        const string emoji = "😀";
        var editor = new RichEditBox { Text = $"A\r\n{emoji}B" };
        ITextRange crlfTail = editor.TextDocument.GetRange(2, 3);
        crlfTail.GetText(TextGetOptions.None, out string unadjustedCrlf);
        crlfTail.GetText(TextGetOptions.AdjustCrlf, out string adjustedCrlf);
        Assert.Equal("\n", unadjustedCrlf);
        Assert.Equal("\r\n", adjustedCrlf);

        ITextRange lowSurrogate = editor.TextDocument.GetRange(4, 5);
        lowSurrogate.GetText(TextGetOptions.AdjustCrlf, out string adjustedScalar);
        Assert.Equal(emoji, adjustedScalar);

        Assert.Throws<ArgumentException>(() =>
            editor.TextDocument.GetText(TextGetOptions.UseLf | TextGetOptions.UseCrlf, out _));
        Assert.Throws<ArgumentException>(() =>
            crlfTail.GetText(TextGetOptions.UseLf | TextGetOptions.UseCrlf, out _));
    }

    [Fact]
    public void RichEditTextSetOptionsUnlinkUnhideAndLimitStyledRtf()
    {
        var source = new RichEditBox { Text = "😀ab" };
        ITextRange styled = source.TextDocument.GetRange(0, source.Text.Length);
        styled.Link = "https://example.test";
        styled.CharacterFormat.Hidden = FormatEffect.On;
        source.TextDocument.GetText(TextGetOptions.FormatRtf, out string rtf);

        var destination = new RichEditBox { MaxLength = 3 };
        destination.TextDocument.SetText(
            TextSetOptions.FormatRtf | TextSetOptions.Unlink | TextSetOptions.Unhide | TextSetOptions.CheckTextLimit,
            rtf);

        Assert.Equal("😀a", destination.Text);
        ITextRange result = destination.TextDocument.GetRange(0, destination.Text.Length);
        Assert.Equal(string.Empty, result.Link);
        Assert.Equal(FormatEffect.Off, result.CharacterFormat.Hidden);

        result.Link = "https://example.test/inherited";
        result.CharacterFormat.Hidden = FormatEffect.On;
        destination.TextDocument.GetRange(destination.Text.Length, destination.Text.Length).SetText(
            TextSetOptions.Unlink | TextSetOptions.Unhide,
            "x");
        Assert.Equal(string.Empty, destination.TextDocument.GetRange(3, 4).Link);
        Assert.Equal(FormatEffect.Off, destination.TextDocument.GetRange(3, 4).CharacterFormat.Hidden);
    }

    [Fact]
    public void RichEditSelectionEndpointSettersKeepTheAssignedEndActive()
    {
        var editor = new RichEditBox { Text = "abcdef" };
        ITextSelection selection = editor.TextDocument.Selection;
        selection.SetRange(1, 5);

        selection.StartPosition = 2;
        Assert.Equal(2, selection.StartPosition);
        Assert.Equal(5, selection.EndPosition);
        Assert.Equal(2, editor.CaretIndex);

        selection.EndPosition = 4;
        Assert.Equal(2, selection.StartPosition);
        Assert.Equal(4, selection.EndPosition);
        Assert.Equal(4, editor.CaretIndex);

        selection.StartPosition = 6;
        Assert.Equal(6, selection.StartPosition);
        Assert.Equal(6, selection.EndPosition);
        Assert.Equal(6, editor.CaretIndex);
    }

    [Fact]
    public void RichEditTextRangesExposeUtf32CloneLinksSelectionKeysAndGeometry()
    {
        const string emoji = "😀";
        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            Text = $"abc {emoji}\nאבג"
        };
        editor.Measure(new System.Numerics.Vector2(320f, 160f));
        editor.Arrange(new Rect(0f, 0f, 320f, 160f));
        ITextRange first = editor.TextDocument.GetRange(0, 3);
        first.Link = "https://example.test";
        ITextRange clone = first.GetClone();
        ITextRange emojiRange = editor.TextDocument.GetRange(4, 4 + emoji.Length);
        emojiRange.GetCharacterUtf32(out uint scalar, 0);
        first.GetRect(PointOptions.ClientCoordinates, out Windows.Foundation.Rect rect, out int hit);
        ITextRange multiline = editor.TextDocument.GetRange(0, editor.Text.Length);
        multiline.GetPoint(
            HorizontalCharacterAlignment.Left,
            VerticalCharacterAlignment.Top,
            PointOptions.ClientCoordinates | PointOptions.Start,
            out Windows.Foundation.Point startPoint);
        multiline.GetPoint(
            HorizontalCharacterAlignment.Left,
            VerticalCharacterAlignment.Top,
            PointOptions.ClientCoordinates,
            out Windows.Foundation.Point endPoint);

        Assert.Equal(0x1F600u, scalar);
        Assert.True(first.InStory(clone));
        Assert.True(first.IsEqual(clone));
        Assert.Equal("https://example.test", clone.Link);
        Assert.True(rect.Width > 0d);
        Assert.Equal(1, hit);
        Assert.True(endPoint.Y > startPoint.Y);

        editor.TextDocument.Selection.SetRange(3, 3);
        Assert.True(editor.TextDocument.Selection.MoveLeft(TextRangeUnit.Character, 1, extend: true) < 0);
        Assert.Equal(1, editor.SelectionLength);
        Assert.Equal(SelectionType.Normal, editor.TextDocument.Selection.Type);

        editor.TextDocument.GetRange(0, 3).ParagraphFormat.Alignment = ParagraphAlignment.Center;
        var formattedDestination = new RichEditBox { Text = "replace" };
        formattedDestination.TextDocument.GetRange(0, 7).FormattedText = editor.TextDocument.GetRange(0, 3);
        Assert.Equal("abc", formattedDestination.Text);
        Assert.Equal(
            ParagraphAlignment.Center,
            formattedDestination.TextDocument.GetRange(0, 3).ParagraphFormat.Alignment);
    }

    [Fact]
    public void SharedDocumentEngineMirrorsColumnsAndTableColumnsInRtl()
    {
        var document = new RichDocument();
        document.Add(new Paragraph(new Run("first paragraph fills the first visual column with enough text to wrap")));
        document.Add(new Paragraph(new Run("second paragraph")));
        var table = new Table(
            new TableRow(
                new TableCell("A"),
                new TableCell("B")));
        document.Add(table);

        var presenter = new MarkdownTextBlock
        {
            Document = document,
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            Padding = new Thickness(0f),
            ColumnCount = 2,
            ColumnGap = 20f,
            FlowDirection = FlowDirection.RightToLeft,
            TextReadingOrder = TextReadingOrder.UseFlowDirection,
            Width = 420f,
            Height = 100f
        };
        presenter.Measure(new System.Numerics.Vector2(420f, 100f));
        presenter.Arrange(new Rect(0f, 0f, 420f, 100f));

        PositionedRichChar first = presenter.PositionedChars.First(static value => value.Info.Character == 'f');
        Assert.True(first.Position.X > 210f);

        PositionedRichChar cellA = presenter.PositionedChars.First(static value => value.Info.Character == 'A');
        PositionedRichChar cellB = presenter.PositionedChars.First(static value => value.Info.Character == 'B');
        Assert.True(cellA.Position.X > cellB.Position.X);
    }

    [Fact]
    public void SharedMultiColumnEngineUsesRightRelativeRtlTabStops()
    {
        var editor = new RichEditBox { Text = "א\tב" };
        ITextParagraphFormat format = editor.TextDocument.GetRange(0, editor.Text.Length).ParagraphFormat;
        format.RightToLeft = FormatEffect.On;
        format.Alignment = ParagraphAlignment.Left;
        format.AddTab(80f, TabAlignment.Right, TabLeader.Spaces);
        var document = Assert.IsType<RichDocument>(typeof(RichEditBox)
            .GetField("_editorLayoutDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor));
        var presenter = new MarkdownTextBlock
        {
            Document = document,
            Font = InterFontFamily.Regular,
            FontSize = 18f,
            Padding = new Thickness(0f),
            ColumnCount = 2,
            ColumnGap = 20f,
            FlowDirection = FlowDirection.RightToLeft,
            TextReadingOrder = TextReadingOrder.UseFlowDirection,
            Width = 420f,
            Height = 100f
        };
        presenter.Measure(new System.Numerics.Vector2(420f, 100f));
        presenter.Arrange(new Rect(0f, 0f, 420f, 100f));
        PositionedRichChar following = Assert.Single(
            presenter.PositionedChars,
            static character => character.Info.Character == 'ב');

        float logicalStartEdge = following.Position.X + following.ShapedAdvance;
        Assert.InRange(logicalStartEdge, 338f, 342f);
    }

    [Fact]
    public void RichEditCharacterFormatMatchesTomSurfaceAndProtectsStyledText()
    {
        var editor = new RichEditBox { Font = InterFontFamily.Regular, Text = "alpha beta" };
        ITextRange alpha = editor.TextDocument.GetRange(0, 5);
        ITextCharacterFormat format = alpha.CharacterFormat;
        format.BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 240, 0);
        format.ForegroundColor = Windows.UI.Color.FromArgb(255, 10, 20, 30);
        format.Underline = UnderlineType.Double;
        format.Strikethrough = FormatEffect.On;
        format.Spacing = 1.5f;
        format.Position = 2f;
        format.Weight = 650;
        format.FontStyle = Windows.UI.Text.FontStyle.Oblique;
        format.LanguageTag = "tr-TR";
        format.ProtectedText = FormatEffect.On;

        ITextCharacterFormat clone = format.GetClone();
        Assert.True(format.IsEqual(clone));
        Assert.Equal(UnderlineType.Double, clone.Underline);
        Assert.Equal(650, clone.Weight);
        Assert.Equal("tr-TR", clone.LanguageTag);
        Assert.Equal(1.5f, clone.Spacing);

        alpha.Text = "changed";
        Assert.Equal("alpha beta", editor.Text);

        ITextRange beta = editor.TextDocument.GetRange(6, 10);
        beta.CharacterFormat.Hidden = FormatEffect.On;
        editor.TextDocument.GetText(TextGetOptions.NoHidden, out string visible);
        Assert.Equal("alpha ", visible);
    }

    [Fact]
    public void RichEditCharacterFormatReturnsTomUndefinedSentinelsForMixedRanges()
    {
        var editor = new RichEditBox { Text = "ab" };
        ITextCharacterFormat first = editor.TextDocument.GetRange(0, 1).CharacterFormat;
        first.Bold = FormatEffect.On;
        first.Size = 24f;
        first.ForegroundColor = Windows.UI.Color.FromArgb(255, 1, 2, 3);

        ITextCharacterFormat mixed = editor.TextDocument.GetRange(0, 2).CharacterFormat;

        Assert.Equal(FormatEffect.Undefined, mixed.Bold);
        Assert.Equal(TextConstants.UndefinedFloatValue, mixed.Size);
        Assert.Equal(TextConstants.UndefinedColor, mixed.ForegroundColor);
        Assert.Equal(TextConstants.UndefinedInt32Value, mixed.Weight);
    }

    [Fact]
    public void RichEditParagraphFormatSupportsTabsIndentsLineSpacingAndDirection()
    {
        var editor = new RichEditBox { Text = "first\nsecond" };
        ITextParagraphFormat format = editor.TextDocument.GetRange(0, editor.Text.Length).ParagraphFormat;
        format.Alignment = ParagraphAlignment.Justify;
        format.SetIndents(8f, 12f, 16f);
        format.SetLineSpacing(LineSpacingRule.Multiple, 1.25f);
        format.SpaceBefore = 3f;
        format.SpaceAfter = 5f;
        format.RightToLeft = FormatEffect.On;
        format.AddTab(48f, TabAlignment.Decimal, TabLeader.Dots);

        ITextParagraphFormat clone = format.GetClone();
        Assert.True(format.IsEqual(clone));
        Assert.Equal(8f, clone.FirstLineIndent);
        Assert.Equal(12f, clone.LeftIndent);
        Assert.Equal(16f, clone.RightIndent);
        Assert.Equal(LineSpacingRule.Multiple, clone.LineSpacingRule);
        Assert.Equal(1.25f, clone.LineSpacing);
        Assert.Equal(FlowDirection.LeftToRight, editor.FlowDirection);
        RichDocument layoutDocument = (RichDocument)typeof(RichEditBox)
            .GetField("_editorLayoutDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        Assert.All(layoutDocument.Blocks.Cast<Paragraph>(), paragraph =>
            Assert.Equal(FlowDirection.RightToLeft, paragraph.FlowDirection));
        Assert.Equal(1, clone.TabCount);
        clone.GetTab(0, out float position, out TabAlignment alignment, out TabLeader leader);
        Assert.Equal(48f, position);
        Assert.Equal(TabAlignment.Decimal, alignment);
        Assert.Equal(TabLeader.Dots, leader);
    }

    [Fact]
    public void RichEditParagraphCustomTabStopsParticipateInSharedLayout()
    {
        var editor = new RichEditBox { Font = InterFontFamily.Regular, Text = "a\tb" };
        ITextParagraphFormat format = editor.TextDocument.GetRange(0, editor.Text.Length).ParagraphFormat;
        format.AddTab(
            80f,
            TabAlignment.Left,
            TabLeader.Spaces);
        editor.Measure(new System.Numerics.Vector2(260f, 100f));
        editor.Arrange(new Rect(0f, 0f, 260f, 100f));
        var blockView = Assert.IsType<RichTextBlock>(typeof(RichEditBox)
            .GetField("_blockView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor));
        PositionedRichChar b = Assert.Single(blockView.PositionedChars, static character => character.Info.Character == 'b');

        Assert.InRange(b.Position.X, 79f, 84f);

        format.ClearAllTabs();
        editor.TextDocument.DefaultTabStop = 60f;
        editor.Measure(new System.Numerics.Vector2(260f, 100f));
        editor.Arrange(new Rect(0f, 0f, 260f, 100f));
        b = Assert.Single(blockView.PositionedChars, static character => character.Info.Character == 'b');
        Assert.InRange(b.Position.X, 59f, 64f);
    }

    [Fact]
    public void RichEditDocumentTrailingWhitespaceOptionChangesTheAlignmentBox()
    {
        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            Text = "ab  ",
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right
        };
        editor.Measure(new System.Numerics.Vector2(260f, 100f));
        editor.Arrange(new Rect(0f, 0f, 260f, 100f));
        var blockView = Assert.IsType<RichTextBlock>(typeof(RichEditBox)
            .GetField("_blockView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor));
        float ignoredWhitespaceX = Assert.Single(
            blockView.PositionedChars,
            static character => character.Info.Character == 'b').Position.X;

        editor.TextDocument.AlignmentIncludesTrailingWhitespace = true;
        editor.Measure(new System.Numerics.Vector2(260f, 100f));
        editor.Arrange(new Rect(0f, 0f, 260f, 100f));
        float includedWhitespaceX = Assert.Single(
            blockView.PositionedChars,
            static character => character.Info.Character == 'b').Position.X;

        Assert.True(
            ignoredWhitespaceX > includedWhitespaceX,
            $"Expected ignored trailing whitespace ({ignoredWhitespaceX}) to align after included whitespace ({includedWhitespaceX}).");
        Assert.True(editor.TextDocument.AlignmentIncludesTrailingWhitespace);
    }

    [Fact]
    public void RichEditDocumentCanSuppressTerminalCharacterSpacingAndCaret()
    {
        var editor = new RichEditBox { Font = InterFontFamily.Regular, Text = "ab" };
        editor.TextDocument.GetRange(0, 2).CharacterFormat.Spacing = 12f;
        editor.Measure(new System.Numerics.Vector2(260f, 100f));
        editor.Arrange(new Rect(0f, 0f, 260f, 100f));
        var blockView = Assert.IsType<RichTextBlock>(typeof(RichEditBox)
            .GetField("_blockView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor));
        float spacedAdvance = Assert.Single(
            blockView.PositionedChars,
            static character => character.Info.Character == 'b').ShapedAdvance;

        editor.TextDocument.IgnoreTrailingCharacterSpacing = true;
        editor.TextDocument.CaretType = CaretType.Null;
        editor.Measure(new System.Numerics.Vector2(260f, 100f));
        editor.Arrange(new Rect(0f, 0f, 260f, 100f));
        float terminalAdvance = Assert.Single(
            blockView.PositionedChars,
            static character => character.Info.Character == 'b').ShapedAdvance;

        Assert.Equal(12f, spacedAdvance - terminalAdvance, 3);
        Assert.True(editor.TextDocument.IgnoreTrailingCharacterSpacing);
        Assert.Equal(CaretType.Null, editor.TextDocument.CaretType);

        InputSystem.SetFocus(editor);
        try
        {
            var drawing = new DrawingContext();
            editor.OnRender(drawing);
            Assert.DoesNotContain(
                drawing.Commands,
                static command => command.Type == RenderCommandType.DrawRect &&
                    Math.Abs(command.Rect.Width - 1.5f) < 0.01f);
        }
        finally
        {
            InputSystem.SetFocus(null);
        }
    }

    [Fact]
    public void RichEditRtlTabPositionsAreMeasuredFromTheRightPageEdge()
    {
        var editor = new RichEditBox { Font = InterFontFamily.Regular, Text = "א\tב" };
        ITextParagraphFormat format = editor.TextDocument.GetRange(0, editor.Text.Length).ParagraphFormat;
        format.RightToLeft = FormatEffect.On;
        format.Alignment = ParagraphAlignment.Left;
        format.AddTab(80f, TabAlignment.Right, TabLeader.Spaces);
        editor.Measure(new System.Numerics.Vector2(260f, 100f));
        editor.Arrange(new Rect(0f, 0f, 260f, 100f));
        var blockView = Assert.IsType<RichTextBlock>(typeof(RichEditBox)
            .GetField("_blockView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor));
        PositionedRichChar following = Assert.Single(
            blockView.PositionedChars,
            static character => character.Info.Character == 'ב');

        float logicalStartEdge = following.Position.X + following.ShapedAdvance;
        Assert.InRange(logicalStartEdge, blockView.Size.X - 82f, blockView.Size.X - 78f);
    }

    [Fact]
    public void RichEditParagraphListFormatRendersMarkerWithoutChangingLogicalText()
    {
        var editor = new RichEditBox { Font = InterFontFamily.Regular, Text = "Item" };
        ITextParagraphFormat format = editor.TextDocument.GetRange(0, 4).ParagraphFormat;
        format.ListLevelIndex = 1;
        format.ListType = MarkerType.Arabic;
        format.ListStart = 3;
        format.ListStyle = MarkerStyle.Parenthesis;
        editor.Measure(new System.Numerics.Vector2(220f, 100f));
        editor.Arrange(new Rect(0f, 0f, 220f, 100f));
        var blockView = Assert.IsType<RichTextBlock>(typeof(RichEditBox)
            .GetField("_blockView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor));
        var decorations = Assert.IsType<System.Collections.Generic.List<TableVisualDecoration>>(
            typeof(RichTextBlock).GetField("_tableDecorations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(blockView));

        Assert.Contains(decorations, static decoration => decoration.Text == "3)");
        Assert.Equal("Item", editor.Text);
        Assert.Equal(4, editor.TextDocument.GetRange(0, 4).Length);
        editor.TextDocument.GetText(TextGetOptions.IncludeNumbering, out string numbered);
        Assert.Equal("3)\tItem", numbered);
    }

    [Fact]
    public void RichEditDocumentAndRangesRoundTripUtf8AndRtfStreams()
    {
        var editor = new RichEditBox { Text = "stream אבג" };
        editor.TextDocument.GetRange(0, 6).CharacterFormat.Bold = FormatEffect.On;

        using var rtfBytes = new System.IO.MemoryStream();
        using var rtfStream = new Windows.Storage.Streams.RandomAccessStream(rtfBytes, leaveOpen: true);
        editor.TextDocument.SaveToStream(TextGetOptions.FormatRtf, rtfStream);
        Assert.Contains(@"\rtf1", System.Text.Encoding.UTF8.GetString(rtfBytes.ToArray()), StringComparison.Ordinal);

        var imported = new RichEditBox();
        imported.TextDocument.LoadFromStream(TextSetOptions.FormatRtf, rtfStream);
        Assert.Equal(editor.Text, imported.Text);
        Assert.Equal(FormatEffect.On, imported.TextDocument.GetRange(0, 6).CharacterFormat.Bold);

        using var plainBytes = new System.IO.MemoryStream();
        using var plainStream = new Windows.Storage.Streams.RandomAccessStream(plainBytes, leaveOpen: true);
        imported.TextDocument.GetRange(7, imported.Text.Length).GetTextViaStream(TextGetOptions.None, plainStream);
        Assert.Equal("אבג", System.Text.Encoding.UTF8.GetString(plainBytes.ToArray()));
    }

    [Fact]
    public void RichEditRtfRoundTripsParagraphDirectionSpacingTabsAndLists()
    {
        var editor = new RichEditBox { Text = "first\nsecond" };
        ITextParagraphFormat first = editor.TextDocument.GetRange(0, 5).ParagraphFormat;
        first.Alignment = ParagraphAlignment.Center;
        first.RightToLeft = FormatEffect.On;
        first.SetIndents(8f, 20f, 12f);
        first.SpaceBefore = 6f;
        first.SpaceAfter = 10f;
        first.SetLineSpacing(LineSpacingRule.Exactly, 22f);
        first.AddTab(96f, TabAlignment.Decimal, TabLeader.Dots);
        first.ListType = MarkerType.UppercaseRoman;
        first.ListStart = 4;
        first.ListLevelIndex = 2;
        ITextParagraphFormat second = editor.TextDocument.GetRange(6, 12).ParagraphFormat;
        second.Alignment = ParagraphAlignment.Right;
        second.RightToLeft = FormatEffect.Off;

        editor.TextDocument.GetText(TextGetOptions.FormatRtf, out string rtf);
        Assert.Contains(@"\rtlpar", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\tx1920", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\pnucrm", rtf, StringComparison.Ordinal);

        var imported = new RichEditBox();
        imported.TextDocument.SetText(TextSetOptions.FormatRtf, rtf);

        Assert.Equal(editor.Text, imported.Text);
        ITextParagraphFormat importedFirst = imported.TextDocument.GetRange(0, 5).ParagraphFormat;
        Assert.Equal(ParagraphAlignment.Center, importedFirst.Alignment);
        Assert.Equal(FormatEffect.On, importedFirst.RightToLeft);
        Assert.Equal(8f, importedFirst.FirstLineIndent);
        Assert.Equal(20f, importedFirst.LeftIndent);
        Assert.Equal(12f, importedFirst.RightIndent);
        Assert.Equal(6f, importedFirst.SpaceBefore);
        Assert.Equal(10f, importedFirst.SpaceAfter);
        Assert.Equal(LineSpacingRule.Exactly, importedFirst.LineSpacingRule);
        Assert.Equal(22f, importedFirst.LineSpacing);
        Assert.Equal(1, importedFirst.TabCount);
        importedFirst.GetTab(0, out float tabPosition, out TabAlignment tabAlignment, out TabLeader tabLeader);
        Assert.Equal(96f, tabPosition);
        Assert.Equal(TabAlignment.Decimal, tabAlignment);
        Assert.Equal(TabLeader.Dots, tabLeader);
        Assert.Equal(MarkerType.UppercaseRoman, importedFirst.ListType);
        Assert.Equal(4, importedFirst.ListStart);
        Assert.Equal(2, importedFirst.ListLevelIndex);
        ITextParagraphFormat importedSecond = imported.TextDocument.GetRange(6, 12).ParagraphFormat;
        Assert.Equal(ParagraphAlignment.Right, importedSecond.Alignment);
        Assert.Equal(FormatEffect.Off, importedSecond.RightToLeft);
    }

    [Fact]
    public void RichEditRangeInsertImageRetainsObjectPayloadAndProjectsInlineElement()
    {
        var editor = new RichEditBox { Text = "before after" };
        ITextRange insertion = editor.TextDocument.GetRange(7, 7);
        using var bytes = new System.IO.MemoryStream([1, 2, 3, 4]);
        using var stream = new Windows.Storage.Streams.RandomAccessStream(bytes, leaveOpen: true);

        insertion.InsertImage(48, 32, 24, VerticalCharacterAlignment.Baseline, "diagram", stream);

        Assert.Equal("before \uFFFCafter", editor.Text);
        editor.TextDocument.Selection.SetRange(7, 8);
        Assert.Equal(SelectionType.InlineShape, editor.TextDocument.Selection.Type);
        RichDocument document = (RichDocument)typeof(RichEditBox)
            .GetField("_editorLayoutDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        Paragraph paragraph = Assert.IsType<Paragraph>(document.Blocks[0]);
        InlineUIContainer container = Assert.IsType<InlineUIContainer>(paragraph.Inlines[1]);
        Border placeholder = Assert.IsType<Border>(container.Child);
        Assert.Equal(48f, placeholder.Width);
        Assert.Equal(32f, placeholder.Height);
        Assert.Equal("diagram", Assert.IsType<TextBlock>(placeholder.Child).Text);
        editor.TextDocument.GetText(TextGetOptions.UseObjectText, out string accessibleText);
        Assert.Equal("before diagramafter", accessibleText);

        editor.TextDocument.GetText(TextGetOptions.FormatRtf, out string pictureRtf);
        Assert.Contains(@"\pict", pictureRtf, StringComparison.Ordinal);
        var imported = new RichEditBox();
        imported.TextDocument.SetText(TextSetOptions.FormatRtf, pictureRtf);
        Assert.Equal(editor.Text, imported.Text);
        RichTextBuffer importedBuffer = (RichTextBuffer)typeof(RichEditBox)
            .GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(imported)!;
        RichTextEmbeddedObject importedObject = Assert.Single(
            importedBuffer.Spans,
            static span => span.Style.EmbeddedObject is not null).Style.EmbeddedObject!;
        Assert.Equal(48, importedObject.Width);
        Assert.Equal(32, importedObject.Height);
        Assert.Equal("diagram", importedObject.AlternateText);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, importedObject.Data.ToArray());

        editor.Font = InterFontFamily.Regular;
        editor.Measure(new System.Numerics.Vector2(400f, 160f));
        editor.Arrange(new Rect(0f, 0f, 400f, 160f));
        var automation = Assert.IsType<Microsoft.UI.Xaml.Automation.Peers.RichEditBoxAutomationPeer>(
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(editor));
        Microsoft.UI.Xaml.Automation.Provider.IRawElementProviderSimple embeddedChild =
            Assert.Single(automation.DocumentRange.GetChildren());
        Microsoft.UI.Xaml.Automation.Provider.ITextRangeProvider embeddedRange =
            automation.RangeFromChild(embeddedChild);
        Assert.Equal(7, embeddedRange.Start);
        Assert.Equal(8, embeddedRange.End);
        Assert.Equal("\uFFFC", embeddedRange.GetText());
    }

    [Fact]
    public void RichEditValidEncodedImageUsesDemandUploadedImageRendering()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var editor = new RichEditBox();
        using var bytes = new System.IO.MemoryStream(png);
        using var stream = new Windows.Storage.Streams.RandomAccessStream(bytes, leaveOpen: true);
        editor.TextDocument.GetRange(0, 0).InsertImage(
            40,
            30,
            30,
            VerticalCharacterAlignment.Baseline,
            "pixel",
            stream);

        RichDocument document = (RichDocument)typeof(RichEditBox)
            .GetField("_editorLayoutDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        Paragraph paragraph = Assert.IsType<Paragraph>(document.Blocks[0]);
        Image image = Assert.IsType<Image>(Assert.IsType<InlineUIContainer>(paragraph.Inlines[0]).Child);
        Assert.IsType<EncodedImageSource>(image.Source);
        image.Measure(new System.Numerics.Vector2(40f, 30f));
        image.Arrange(new Rect(0f, 0f, 40f, 30f));

        ProGPU.Backend.WgpuContext? prior = ProGPU.Backend.WgpuContext.Current;
        try
        {
            ProGPU.Backend.WgpuContext.Current = ProGPU.Tests.Headless.HeadlessWindow.Shared.Context;
            var drawing = new DrawingContext();
            image.OnRender(drawing);
            Assert.Contains(drawing.Commands, static command => command.Type == RenderCommandType.DrawTexture);
        }
        finally
        {
            ProGPU.Backend.WgpuContext.Current = prior;
        }
    }

    [Fact]
    public void RichEditorEditsRetainUnchangedParagraphIdentityAndVirtualizedCaches()
    {
        var editor = new RichEditBox
        {
            Font = InterFontFamily.Regular,
            Text = string.Join('\n', Enumerable.Range(0, 2_000).Select(static index => $"paragraph {index}"))
        };
        editor.Measure(new System.Numerics.Vector2(420f, 220f));
        editor.Arrange(new Rect(0f, 0f, 420f, 220f));
        RichDocument document = (RichDocument)typeof(RichEditBox)
            .GetField("_editorLayoutDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        Block retainedTail = document.Blocks[1_500];

        editor.TextDocument.GetRange(0, 9).Text = "updated";
        editor.Measure(new System.Numerics.Vector2(420f, 220f));
        editor.Arrange(new Rect(0f, 0f, 420f, 220f));

        Assert.Same(retainedTail, document.Blocks[1_500]);
        Assert.InRange(editor.LayoutSession.RealizedBlockCount, 1, 300);
    }

    [Fact]
    public void IncrementalEditorProjectionHandlesParagraphSplitAndJoin()
    {
        var editor = new RichEditBox { Text = "aa\nbb\ncc" };
        RichDocument document = (RichDocument)typeof(RichEditBox)
            .GetField("_editorLayoutDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;

        editor.TextDocument.GetRange(4, 4).Text = "\n";
        Assert.Equal(4, document.Blocks.Count);
        Assert.Equal(editor.Text, PlainTextDocumentExporter.Default.Export(document));

        editor.TextDocument.GetRange(4, 5).Text = string.Empty;
        Assert.Equal(3, document.Blocks.Count);
        Assert.Equal(editor.Text, PlainTextDocumentExporter.Default.Export(document));
    }

    [Fact]
    public void ParagraphFormattingIsRangeLocalAndSurvivesIncrementalSplits()
    {
        var editor = new RichEditBox { Text = "one\ntwo\nthree" };
        ITextParagraphFormat secondFormat = editor.TextDocument.GetRange(4, 7).ParagraphFormat;
        secondFormat.RightToLeft = FormatEffect.On;
        secondFormat.SetIndents(3f, 9f, 4f);
        RichDocument document = (RichDocument)typeof(RichEditBox)
            .GetField("_editorLayoutDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;

        Assert.Null(Assert.IsType<Paragraph>(document.Blocks[0]).FlowDirection);
        Assert.Equal(FlowDirection.RightToLeft, Assert.IsType<Paragraph>(document.Blocks[1]).FlowDirection);
        Assert.Null(Assert.IsType<Paragraph>(document.Blocks[2]).FlowDirection);
        ITextParagraphFormat mixed = editor.TextDocument.GetRange(0, editor.Text.Length).ParagraphFormat;
        Assert.Equal(FormatEffect.Undefined, mixed.RightToLeft);
        Assert.Equal(TextConstants.UndefinedFloatValue, mixed.LeftIndent);

        editor.TextDocument.GetRange(5, 5).Text = "\n";
        Assert.Equal(4, document.Blocks.Count);
        Assert.Equal(FlowDirection.RightToLeft, Assert.IsType<Paragraph>(document.Blocks[1]).FlowDirection);
        Assert.Equal(FlowDirection.RightToLeft, Assert.IsType<Paragraph>(document.Blocks[2]).FlowDirection);
        Assert.Equal(9f, Assert.IsType<Paragraph>(document.Blocks[2]).LeftIndent);
    }

    [Fact]
    public void TextBoxControlArrowMovesBetweenVisualWordStartsInMixedText()
    {
        var textBox = new TextBox
        {
            Text = "abc אבג def",
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            Width = 320f,
            Height = 60f,
            CaretIndex = 4
        };
        textBox.Measure(new System.Numerics.Vector2(320f, 60f));
        textBox.Arrange(new Rect(0f, 0f, 320f, 60f));
        InputSystem.SetFocus(textBox);

        textBox.OnKeyDown(new KeyRoutedEventArgs { Key = Key.ControlLeft });
        textBox.OnKeyDown(new KeyRoutedEventArgs { Key = Key.Right });
        Assert.Equal(8, textBox.CaretIndex);
        textBox.OnKeyDown(new KeyRoutedEventArgs { Key = Key.Left });
        InputSystem.SetFocus(null);

        Assert.Equal(4, textBox.CaretIndex);
    }

    [Fact]
    public void PasswordBoxControlArrowUsesUnderlyingWordBoundaries()
    {
        var passwordBox = new PasswordBox
        {
            Password = "one two",
            PasswordChar = '•',
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            Width = 240f,
            Height = 60f,
            CaretIndex = 7
        };
        passwordBox.Measure(new System.Numerics.Vector2(240f, 60f));
        passwordBox.Arrange(new Rect(0f, 0f, 240f, 60f));
        InputSystem.SetFocus(passwordBox);

        passwordBox.OnKeyDown(new KeyRoutedEventArgs { Key = Key.ControlLeft });
        passwordBox.OnKeyDown(new KeyRoutedEventArgs { Key = Key.Left });
        InputSystem.SetFocus(null);

        Assert.Equal(4, passwordBox.CaretIndex);
    }

    [Fact]
    public void RichEditControlArrowMovesByPhysicalWordOrderAcrossBidiRuns()
    {
        var editor = new RichEditBox
        {
            Text = "abc אבג def",
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            Width = 320f,
            Height = 80f,
            CaretIndex = 4
        };
        editor.Measure(new System.Numerics.Vector2(320f, 80f));
        editor.Arrange(new Rect(0f, 0f, 320f, 80f));
        InputSystem.SetFocus(editor);

        editor.OnKeyDown(new KeyRoutedEventArgs { Key = Key.ControlLeft });
        editor.OnKeyDown(new KeyRoutedEventArgs { Key = Key.Right });
        Assert.Equal(8, editor.CaretIndex);
        editor.OnKeyDown(new KeyRoutedEventArgs { Key = Key.Left });
        InputSystem.SetFocus(null);

        Assert.Equal(4, editor.CaretIndex);
    }

    [Fact]
    public void RichEditTextSelectionMoveLeftAndRightUseVisualBidiOrder()
    {
        var editor = new RichEditBox
        {
            Text = "abc אבג def",
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            Width = 320f,
            Height = 80f
        };
        editor.Measure(new System.Numerics.Vector2(320f, 80f));
        editor.Arrange(new Rect(0f, 0f, 320f, 80f));
        ITextSelection selection = editor.TextDocument.Selection;
        selection.SetRange(4, 4);

        Assert.Equal(1, selection.MoveRight(TextRangeUnit.Word, 1, extend: false));
        Assert.Equal(8, editor.CaretIndex);
        Assert.Equal(-1, selection.MoveLeft(TextRangeUnit.Word, 1, extend: false));
        Assert.Equal(4, editor.CaretIndex);

        selection.SetRange(4, 8);
        Assert.Equal(-1, selection.MoveLeft(TextRangeUnit.Character, 1, extend: true));
        Assert.Equal(4, editor.SelectionStart);
        Assert.True(editor.SelectionLength < 4);

        selection.SetRange(4, 8);
        Assert.Equal(1, selection.MoveRight(TextRangeUnit.Character, 1, extend: false));
        Assert.Equal(8, editor.CaretIndex);
        Assert.Equal(0, editor.SelectionLength);
    }

    [Fact]
    public void RichEditTextSelectionHomeAndEndFollowRtlLineDirection()
    {
        var editor = new RichEditBox
        {
            Text = "אבג",
            Font = InterFontFamily.Regular,
            FontSize = 24f,
            FlowDirection = FlowDirection.RightToLeft,
            TextReadingOrder = TextReadingOrder.UseFlowDirection,
            Width = 240f,
            Height = 80f
        };
        editor.Measure(new System.Numerics.Vector2(240f, 80f));
        editor.Arrange(new Rect(0f, 0f, 240f, 80f));
        ITextSelection selection = editor.TextDocument.Selection;
        selection.SetRange(2, 2);

        Assert.Equal(1, selection.HomeKey(TextRangeUnit.Line, extend: false));
        Assert.Equal(0, editor.CaretIndex);
        Assert.Equal(1, selection.EndKey(TextRangeUnit.Line, extend: false));
        Assert.Equal(3, editor.CaretIndex);
    }

    [Fact]
    public void RichEditControlShiftDirectionShortcutIsRangeLocalAndUndoable()
    {
        var editor = new RichEditBox
        {
            Text = "left\nימין",
            SelectionStart = 5,
            SelectionLength = 4,
            CaretIndex = 9
        };
        InputSystem.SetFocus(editor);

        editor.OnKeyDown(new KeyRoutedEventArgs { Key = Key.ControlLeft });
        editor.OnKeyDown(new KeyRoutedEventArgs { Key = Key.ShiftRight });
        editor.OnKeyUp(new KeyRoutedEventArgs { Key = Key.ShiftRight });
        editor.OnKeyUp(new KeyRoutedEventArgs { Key = Key.ControlLeft });

        ITextParagraphFormat first = editor.TextDocument.GetRange(0, 4).ParagraphFormat;
        ITextParagraphFormat second = editor.TextDocument.GetRange(5, 9).ParagraphFormat;
        Assert.Equal(FormatEffect.Undefined, first.RightToLeft);
        Assert.Equal(FormatEffect.On, second.RightToLeft);
        Assert.Equal(ParagraphAlignment.Right, second.Alignment);

        editor.Undo();
        Assert.Equal(FormatEffect.Undefined, editor.TextDocument.GetRange(5, 9).ParagraphFormat.RightToLeft);

        editor.Redo();
        InputSystem.SetFocus(null);
        Assert.Equal(FormatEffect.On, editor.TextDocument.GetRange(5, 9).ParagraphFormat.RightToLeft);
        Assert.Equal(ParagraphAlignment.Right, editor.TextDocument.GetRange(5, 9).ParagraphFormat.Alignment);
    }

    [Fact]
    public void RichEditParagraphFormatEnforcesWinUiTabStopLimit()
    {
        var editor = new RichEditBox { Text = "tabs" };
        ITextParagraphFormat format = editor.TextDocument.GetRange(0, 4).ParagraphFormat;
        for (int index = 0; index < 63; index++)
            format.AddTab(index + 1, TabAlignment.Left, TabLeader.Spaces);

        Assert.Equal(63, format.TabCount);
        Assert.Throws<InvalidOperationException>(() =>
            format.AddTab(64f, TabAlignment.Left, TabLeader.Spaces));
    }

    [Fact]
    public void RichEditTextRangesTrackInsertionsUsingBoundaryGravity()
    {
        var editor = new RichEditBox { Text = "abcd" };
        ITextRange inward = editor.TextDocument.GetRange(1, 3);
        inward.Gravity = RangeGravity.Inward;

        editor.TextDocument.GetRange(1, 1).Text = "X";
        Assert.Equal((2, 4, "bc"), (inward.StartPosition, inward.EndPosition, inward.Text));

        editor.TextDocument.GetRange(4, 4).Text = "Y";
        Assert.Equal((2, 4, "bc"), (inward.StartPosition, inward.EndPosition, inward.Text));

        ITextRange outward = editor.TextDocument.GetRange(2, 4);
        outward.Gravity = RangeGravity.Outward;
        editor.TextDocument.GetRange(2, 2).Text = "L";
        editor.TextDocument.GetRange(outward.EndPosition, outward.EndPosition).Text = "R";

        Assert.Equal("LbcR", outward.Text);
    }

    [Fact]
    public void RichEditInsertionPointUsesUiForwardTrackingAcrossExternalEdits()
    {
        var editor = new RichEditBox { Text = "abc" };
        ITextRange caret = editor.TextDocument.GetRange(1, 1);

        editor.TextDocument.GetRange(1, 1).Text = "XY";

        Assert.Equal(3, caret.StartPosition);
        Assert.Equal(3, caret.EndPosition);

        ITextRange endCaret = editor.TextDocument.GetRange(editor.Text.Length, editor.Text.Length);
        editor.TextDocument.GetRange(editor.Text.Length, editor.Text.Length).Text = "Z";
        Assert.Equal(editor.Text.Length, endCaret.StartPosition);

        editor.Text = "aaaa";
        ITextRange repeated = editor.TextDocument.GetRange(1, 3);
        repeated.Gravity = RangeGravity.Inward;
        editor.TextDocument.GetRange(1, 1).Text = "a";
        Assert.Equal((2, 4, "aa"), (repeated.StartPosition, repeated.EndPosition, repeated.Text));
    }

    [Fact]
    public void RichEditRangeGravityChoosesFormattingAtRunBoundary()
    {
        var editor = new RichEditBox { Text = "ab" };
        editor.TextDocument.GetRange(0, 1).CharacterFormat.Bold = FormatEffect.On;
        editor.TextDocument.GetRange(1, 2).CharacterFormat.Italic = FormatEffect.On;
        ITextRange insertion = editor.TextDocument.GetRange(1, 1);

        insertion.Gravity = RangeGravity.Backward;
        Assert.Equal(FormatEffect.On, insertion.CharacterFormat.Bold);
        Assert.Equal(FormatEffect.Off, insertion.CharacterFormat.Italic);

        insertion.Gravity = RangeGravity.Forward;
        Assert.Equal(FormatEffect.Off, insertion.CharacterFormat.Bold);
        Assert.Equal(FormatEffect.On, insertion.CharacterFormat.Italic);
    }

    [Fact]
    public void RichEditTomFormattingUnitsFollowRetainedStyleRuns()
    {
        var editor = new RichEditBox { Text = "aabbcc" };
        editor.TextDocument.GetRange(0, 2).CharacterFormat.Bold = FormatEffect.On;
        editor.TextDocument.GetRange(2, 4).CharacterFormat.Italic = FormatEffect.On;

        ITextRange range = editor.TextDocument.GetRange(1, 1);
        Assert.Equal(2, range.Expand(TextRangeUnit.Bold));
        Assert.Equal((0, 2, "aa"), (range.StartPosition, range.EndPosition, range.Text));

        range.SetIndex(TextRangeUnit.CharacterFormat, 2, extend: true);
        Assert.Equal((2, 4, "bb"), (range.StartPosition, range.EndPosition, range.Text));
        Assert.Equal(2, range.GetIndex(TextRangeUnit.CharacterFormat));
    }

    [Fact]
    public void RichEditTomHardParagraphIgnoresSoftParagraphSeparators()
    {
        var editor = new RichEditBox { Text = "soft\ncontinued\r\nhard" };
        ITextRange hardParagraph = editor.TextDocument.GetRange(2, 2);

        hardParagraph.Expand(TextRangeUnit.HardParagraph);

        Assert.Equal("soft\ncontinued\r\n", hardParagraph.Text);
    }

    [Fact]
    public void RichEditRtfDocumentDefaultsAreAppliedOnlyWhenRequestedAndUndoable()
    {
        const string rtf = @"{\rtf1\ansi\deff1\deflang1037\deftab1440{\fonttbl{\f0\fnil Segoe UI;}{\f1\fmodern Courier New;}}sample}";
        var insertionEditor = new RichEditBox { Text = "old" };
        insertionEditor.TextDocument.SetText(TextSetOptions.FormatRtf, rtf);

        Assert.Equal(36f, insertionEditor.TextDocument.DefaultTabStop);
        Assert.Equal("Courier New", insertionEditor.TextDocument.GetRange(0, 6).CharacterFormat.Name);
        Assert.Equal("he-IL", insertionEditor.TextDocument.GetRange(0, 6).CharacterFormat.LanguageTag);

        var documentEditor = new RichEditBox { Text = "old" };
        documentEditor.TextDocument.SetText(
            TextSetOptions.FormatRtf | TextSetOptions.ApplyRtfDocumentDefaults,
            rtf);

        Assert.Equal(72f, documentEditor.TextDocument.DefaultTabStop);
        Assert.Equal("Courier New", documentEditor.TextDocument.GetDefaultCharacterFormat().Name);
        Assert.Equal("he-IL", documentEditor.TextDocument.GetDefaultCharacterFormat().LanguageTag);

        documentEditor.Undo();
        Assert.Equal("old", documentEditor.Text);
        Assert.Equal(36f, documentEditor.TextDocument.DefaultTabStop);
    }

    [Fact]
    public void RichEditRtfExportRetainsDefaultTabAndLanguageMetadata()
    {
        var editor = new RichEditBox { Text = "שלום" };
        editor.TextDocument.DefaultTabStop = 72f;
        editor.TextDocument.GetRange(0, 4).CharacterFormat.LanguageTag = "he-IL";

        editor.TextDocument.GetText(TextGetOptions.FormatRtf, out string rtf);

        Assert.Contains(@"\deftab1440", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\deflang1037", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\lang1037", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void RichEditTomStoryExposesAnUndeletableVirtualFinalEop()
    {
        var editor = new RichEditBox { Text = "abc" };
        ITextRange story = editor.TextDocument.GetRange(0, int.MaxValue);

        Assert.Equal(4, story.StoryLength);
        Assert.Equal((0, 4), (story.StartPosition, story.EndPosition));
        Assert.Equal("abc", story.Text);
        story.GetText(TextGetOptions.None, out string ordinary);
        story.GetText(TextGetOptions.AllowFinalEop, out string withEop);
        story.GetText(TextGetOptions.AllowFinalEop | TextGetOptions.UseLf, out string withLf);
        story.GetText(TextGetOptions.AllowFinalEop | TextGetOptions.UseCrlf, out string withCrlf);
        Assert.Equal("abc", ordinary);
        Assert.Equal("abc\r", withEop);
        Assert.Equal("abc\n", withLf);
        Assert.Equal("abc\r\n", withCrlf);
        editor.TextDocument.GetText(TextGetOptions.FormatRtf, out string ordinaryRtf);
        editor.TextDocument.GetText(
            TextGetOptions.FormatRtf | TextGetOptions.AllowFinalEop,
            out string rtfWithEop);
        Assert.DoesNotContain(@"\par ", ordinaryRtf, StringComparison.Ordinal);
        Assert.Contains(@"\par ", rtfWithEop, StringComparison.Ordinal);

        ITextRange eop = editor.TextDocument.GetRange(3, 4);
        Assert.Equal('\r', eop.Character);
        eop.GetCharacterUtf32(out uint eopValue, 0);
        Assert.Equal(13u, eopValue);
        Assert.Equal(0, eop.Delete(TextRangeUnit.Character, 0));
        Assert.Equal("abc", editor.Text);

        ITextRange afterStory = editor.TextDocument.GetRange(int.MaxValue, int.MaxValue);
        Assert.Equal((3, 3), (afterStory.StartPosition, afterStory.EndPosition));
        Assert.Equal(0, afterStory.Move(TextRangeUnit.Character, 1));
        Assert.Equal(3, afterStory.StartPosition);

        story.Collapse(start: false);
        Assert.Equal((3, 3), (story.StartPosition, story.EndPosition));
        story.SetRange(0, 4);
        Assert.Equal(-1, story.EndOf(TextRangeUnit.Story, extend: false));
        Assert.Equal((3, 3), (story.StartPosition, story.EndPosition));

        ITextRange beforeEop = editor.TextDocument.GetRange(3, 3);
        Assert.Equal(0, beforeEop.Delete(TextRangeUnit.Character, 1));
        beforeEop.Character = '!';
        Assert.Equal("abc!", editor.Text);
    }

    [Fact]
    public void RichEditVirtualFinalEopTracksEndInsertionsAndEmptyStories()
    {
        var editor = new RichEditBox { Text = "abc" };
        ITextRange finalEop = editor.TextDocument.GetRange(3, 4);
        finalEop.Gravity = RangeGravity.Inward;

        editor.TextDocument.GetRange(3, 3).Text = "Z";

        Assert.Equal((4, 5), (finalEop.StartPosition, finalEop.EndPosition));
        finalEop.GetText(TextGetOptions.AllowFinalEop, out string eop);
        Assert.Equal("\r", eop);

        editor.Text = string.Empty;
        ITextRange emptyStory = editor.TextDocument.GetRange(0, int.MaxValue);
        Assert.Equal(1, emptyStory.StoryLength);
        Assert.Equal(string.Empty, emptyStory.Text);
        emptyStory.GetText(TextGetOptions.AllowFinalEop, out string emptyEop);
        Assert.Equal("\r", emptyEop);
    }

    [Fact]
    public void RichEditTomLineUnitUsesTheShapedSoftWrappedLine()
    {
        var editor = new RichEditBox
        {
            Text = "one two three four five six",
            Font = InterFontFamily.Regular,
            FontSize = 22f,
            Width = 105f,
            Height = 240f,
            TextWrapping = TextWrapping.Wrap
        };
        editor.Measure(new System.Numerics.Vector2(105f, 240f));
        editor.Arrange(new Rect(0f, 0f, 105f, 240f));
        ITextSelection selection = editor.TextDocument.Selection;

        selection.SetRange(8, 8);
        selection.HomeKey(TextRangeUnit.Line, extend: false);
        int expectedStart = editor.CaretIndex;
        selection.SetRange(8, 8);
        selection.EndKey(TextRangeUnit.Line, extend: false);
        int expectedEnd = editor.CaretIndex;

        ITextRange line = editor.TextDocument.GetRange(8, 8);
        line.Expand(TextRangeUnit.Line);

        Assert.Equal((expectedStart, expectedEnd), (line.StartPosition, line.EndPosition));
        Assert.True(line.Length < editor.Text.Length);
    }

    [Fact]
    public void RichEditSelectionOptionsControlActiveEndReplaceAndUnicodeOvertype()
    {
        var editor = new RichEditBox { Text = "abcd" };
        ITextSelection selection = editor.TextDocument.Selection;
        selection.SetRange(1, 3);
        selection.Options = SelectionOptions.Replace |
            SelectionOptions.StartActive |
            SelectionOptions.AtEndOfLine;

        Assert.Equal(1, editor.CaretIndex);
        Assert.True((selection.Options & SelectionOptions.StartActive) != 0);
        Assert.True((selection.Options & SelectionOptions.AtEndOfLine) != 0);

        selection.Options = SelectionOptions.Replace;
        Assert.Equal(3, editor.CaretIndex);
        InputSystem.Current = new WindowInputState();
        InputSystem.SetFocus(editor);
        InputSystem.InjectKeyDown(Key.Insert);
        InputSystem.InjectKeyUp(Key.Insert);
        Assert.True((selection.Options & SelectionOptions.Overtype) != 0);
        InputSystem.InjectKeyDown(Key.Insert);
        InputSystem.InjectKeyUp(Key.Insert);
        InputSystem.SetFocus(null);
        Assert.True((selection.Options & SelectionOptions.Overtype) == 0);
        selection.TypeText("X");
        Assert.Equal("aXd", editor.Text);

        editor.Text = "abcd";
        selection.SetRange(1, 3);
        selection.Options = SelectionOptions.None;
        selection.TypeText("X");
        Assert.Equal("aXbcd", editor.Text);

        const string unicode = "a👩‍💻bc";
        editor.Text = unicode;
        selection.SetRange(1, 1);
        selection.Options = SelectionOptions.Replace | SelectionOptions.Overtype;
        selection.TypeText("Z");
        Assert.Equal("aZbc", editor.Text);
        editor.TextDocument.Undo();
        Assert.Equal(unicode, editor.Text);

        editor.Text = "a\nb";
        selection.SetRange(1, 1);
        selection.Options = SelectionOptions.Replace | SelectionOptions.Overtype;
        selection.TypeText("X");
        Assert.Equal("aX\nb", editor.Text);
    }
}
