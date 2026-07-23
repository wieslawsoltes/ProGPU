using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Fonts.Inter;
using ProGPU.Fonts.Noto;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using ProGPU.Vector;
using Border = Microsoft.UI.Xaml.Controls.Border;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using TextBox = Microsoft.UI.Xaml.Controls.TextBox;

namespace ProGPU.Samples;

/// <summary>
/// An interactive text-shaping laboratory inspired by HarfBuzz's hb-shape and
/// hb-view utilities. The page renders the value-only shaping result directly,
/// so the preview and the glyph diagnostics describe the same buffer.
/// </summary>
public static class TextShapingShowcasePage
{
    private const string AccentBrush = "SystemAccentColor";
    private const string CardBrush = "CardBackground";
    private const string ControlBrush = "ControlBackground";
    private const string BorderBrush = "ControlBorder";
    private const string PrimaryBrush = "TextPrimary";
    private const string SecondaryBrush = "TextSecondary";

    private sealed record ShapingPreset(
        string Name,
        string Text,
        string Script,
        string Language,
        ShapingDirection Direction,
        ShapingClusterLevel ClusterLevel,
        ShapingBufferFlags Flags,
        string Features,
        int RepresentativeCodePoint,
        string Description,
        bool PreferJapaneseFont = false,
        string PreContext = "",
        string PostContext = "");

    private static readonly ShapingPreset[] Presets =
    [
        new(
            "Latin layout features",
            "office affinity · AVATAR · 12/25",
            "latn", "en", ShapingDirection.LeftToRight,
            ShapingClusterLevel.MonotoneGraphemes, ShapingBufferFlags.None,
            "liga=1,kern=1,frac=1", 'A',
            "Ligature substitution, pair positioning, automatic fractions, and retained glyph output."),
        new(
            "Ranged stylistic alternate",
            "333333",
            "latn", "en", ShapingDirection.LeftToRight,
            ShapingClusterLevel.Characters, ShapingBufferFlags.None,
            "ss01[0:3]=1", '3',
            "The ss01 alternate is applied only to UTF-16 input range [0, 3), demonstrating ranged features."),
        new(
            "Romanian localized forms",
            "Ş ş  Ţ ţ",
            "latn", "ro", ShapingDirection.LeftToRight,
            ShapingClusterLevel.MonotoneGraphemes, ShapingBufferFlags.None,
            "locl=1", 0x015e,
            "Language-system selection activates Romanian locl forms from the font's GSUB table."),
        new(
            "Arabic joining and marks",
            "السَّلَامُ عَلَيْكُمْ",
            "arab", "ar", ShapingDirection.RightToLeft,
            ShapingClusterLevel.MonotoneGraphemes, ShapingBufferFlags.None,
            "rlig=1,calt=1,mark=1,mkmk=1", 0x0627,
            "Right-to-left joining forms, required ligatures, cursive attachment, and mark positioning."),
        new(
            "Arabic item context",
            "تَابُ",
            "arab", "ar", ShapingDirection.RightToLeft,
            ShapingClusterLevel.MonotoneGraphemes, ShapingBufferFlags.None,
            "rlig=1,calt=1,mark=1,mkmk=1", 0x062A,
            "Pre/post context changes boundary joining without emitting the surrounding characters.",
            PreContext: "كِ", PostContext: "نَ"),
        new(
            "Unsafe-to-concat diagnostics",
            "ب‌ب",
            "arab", "ar", ShapingDirection.RightToLeft,
            ShapingClusterLevel.MonotoneGraphemes, ShapingBufferFlags.ProduceUnsafeToConcat,
            "", 0x0628,
            "ZWNJ stays a granular cluster while output flags identify boundaries that require local reshaping after concatenation."),
        new(
            "Paragraph-start dotted circle",
            "َ",
            "arab", "ar", ShapingDirection.RightToLeft,
            ShapingClusterLevel.MonotoneGraphemes,
            ShapingBufferFlags.BeginningOfText | ShapingBufferFlags.EndOfText,
            "mark=1", 0x064E,
            "BOT enables paragraph-start recovery for a leading mark; adding pre-context suppresses the synthetic dotted circle."),
        new(
            "Devanagari reordering",
            "नमस्ते दुनिया",
            "deva", "hi", ShapingDirection.LeftToRight,
            ShapingClusterLevel.MonotoneGraphemes, ShapingBufferFlags.None,
            "locl=1,ccmp=1", 0x0928,
            "Indic syllable discovery, reph/pre-base reordering, conjunct formation, and mark placement."),
        new(
            "Khmer syllable shaping",
            "សួស្តី ពិភពលោក",
            "khmr", "km", ShapingDirection.LeftToRight,
            ShapingClusterLevel.MonotoneGraphemes, ShapingBufferFlags.None,
            "pref=1,blwf=1,pstf=1", 0x179f,
            "Khmer coeng handling, pre-base reordering, dotted-circle recovery, and presentation features."),
        new(
            "Myanmar syllable shaping",
            "မင်္ဂလာပါ ကမ္ဘာ",
            "mymr", "my", ShapingDirection.LeftToRight,
            ShapingClusterLevel.MonotoneGraphemes, ShapingBufferFlags.None,
            "rphf=1,pref=1,pstf=1", 0x1019,
            "Myanmar syllable classification, kinzi/reph handling, reordering, and positional forms."),
        new(
            "Hangul Jamo composition",
            "한글 자모",
            "hang", "ko", ShapingDirection.LeftToRight,
            ShapingClusterLevel.MonotoneGraphemes, ShapingBufferFlags.None,
            "ljmo=1,vjmo=1,tjmo=1", 0x1112,
            "Leading, vowel, and trailing Jamo features plus canonical Hangul composition.",
            PreferJapaneseFont: true),
        new(
            "Vertical Japanese",
            "日本語の縦書き",
            "kana", "ja", ShapingDirection.TopToBottom,
            ShapingClusterLevel.MonotoneGraphemes, ShapingBufferFlags.None,
            "vert=1,vrt2=1,vkrn=1", 0x65e5,
            "Vertical origins and advances, vertical substitutions, and top-to-bottom placement.",
            PreferJapaneseFont: true),
        new(
            "Canonical normalization",
            "é  e\u0301  a\u0308\u0301",
            "latn", "en", ShapingDirection.LeftToRight,
            ShapingClusterLevel.Graphemes, ShapingBufferFlags.None,
            "ccmp=1,mark=1,mkmk=1", 'e',
            "Composed and decomposed Unicode sequences converge while grapheme clusters retain source indices."),
        new(
            "Preserve default ignorables",
            "A\u200DB  A\u200CB",
            "latn", "en", ShapingDirection.LeftToRight,
            ShapingClusterLevel.Characters, ShapingBufferFlags.PreserveDefaultIgnorables,
            "", 'A',
            "ZWJ and ZWNJ remain visible in the output buffer for diagnostics instead of being hidden."),
        new(
            "Unicode variation selectors",
            "❤︎  ❤️",
            "DFLT", "und", ShapingDirection.LeftToRight,
            ShapingClusterLevel.Graphemes, ShapingBufferFlags.PreserveDefaultIgnorables,
            "", 0x2764,
            "Text and emoji variation selectors exercise cmap variation mapping while remaining in the source cluster."),
        new(
            "Broken-syllable recovery",
            "ि",
            "deva", "hi", ShapingDirection.LeftToRight,
            ShapingClusterLevel.MonotoneGraphemes, ShapingBufferFlags.None,
            "", 0x093f,
            "A standalone Indic mark demonstrates automatic dotted-circle insertion; choose the no-dotted-circle policy to compare.")
    ];

    public static FrameworkElement Create()
    {
        TtfFont uiFont = AppState.GetFont() ?? InterFontFamily.Regular;
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(28f, 24f, 34f, 50f)
        };

        content.AddChild(Label("TEXT SHAPING / OPEN TYPE", 11f, AccentBrush, new Thickness(0, 0, 0, 8f), bold: true));
        content.AddChild(new TextVisual
        {
            Font = InterFontFamily.Display,
            Text = "From Unicode to positioned glyphs.",
            FontSize = 42f,
            HeightConstraint = 62f,
            Brush = new ThemeResourceBrush(PrimaryBrush)
        });
        content.AddChild(Body(
            "A live, HarfBuzz-inspired laboratory for ProGPU's managed OpenType shaper. Edit a run, select its segment properties, apply global or ranged features, and inspect the exact glyph IDs, source code points, UTF-16 clusters, safety flags, advances, and offsets sent to retained rendering.",
            15f,
            SecondaryBrush,
            new Thickness(0, 2f, 0, 18f)));

        var badges = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalSpacing = 8f,
            VerticalSpacing = 8f,
            Margin = new Thickness(0, 0, 0, 28f)
        };
        badges.AddChild(Badge("GSUB 1–8"));
        badges.AddChild(Badge("GPOS 1–9"));
        badges.AddChild(Badge("Unicode 17"));
        badges.AddChild(Badge("4 directions"));
        badges.AddChild(Badge("ranged features"));
        badges.AddChild(Badge("variable fonts"));
        badges.AddChild(Badge("pooled buffers"));
        content.AddChild(badges);

        content.AddChild(CreateLiveLaboratory(uiFont));
        content.AddChild(SectionHeading(
            "OpenType, one switch at a time.",
            "Each pair is shaped from identical input and retained as one glyph run. Only the named feature changes."));
        content.AddChild(CreateFeatureWall());

        content.AddChild(SectionHeading(
            "Script engines, not character maps.",
            "These specimens exercise language-specific substitution, joining, syllable reordering, mark attachment, normalization, and vertical metrics. System fallback faces are used where the bundled font set does not cover a script."));
        content.AddChild(CreateScriptAtlas());

        content.AddChild(SectionHeading(
            "What the shaper understands.",
            "The surface below maps the implemented shaping stages to their observable behavior in the laboratory."));
        content.AddChild(CreateCapabilityMap());

        content.AddChild(CreateArchitectureStrip());

        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    private static FrameworkElement CreateLiveLaboratory(TtfFont uiFont)
    {
        var panel = new Border
        {
            Background = new ThemeResourceBrush(CardBrush),
            BorderBrush = new ThemeResourceBrush(BorderBrush),
            BorderThickness = new Thickness(1f),
            CornerRadius = 12f,
            Padding = new Thickness(18f),
            Margin = new Thickness(0, 0, 0, 70f)
        };
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        panel.Child = stack;

        stack.AddChild(Label("LIVE SHAPING BUFFER", 11f, AccentBrush, new Thickness(0, 0, 0, 4f), bold: true));
        stack.AddChild(Body(
            "The preview and diagnostics consume the same CpuOpenTypeShaper buffer in signed font-design units.",
            13f, SecondaryBrush, new Thickness(0, 0, 0, 16f)));

        var controls = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalSpacing = 12f,
            VerticalSpacing = 12f,
            Margin = new Thickness(0, 0, 0, 12f)
        };
        stack.AddChild(controls);

        ComboBox preset = AddCombo(controls, "PRESET", Presets.Select(static item => (item.Name, (object)item)).ToArray(), 250f);
        ComboBox direction = AddCombo(controls, "DIRECTION",
            [("Left to right", (object)ShapingDirection.LeftToRight),
             ("Right to left", ShapingDirection.RightToLeft),
             ("Top to bottom", ShapingDirection.TopToBottom),
             ("Bottom to top", ShapingDirection.BottomToTop)], 150f);
        ComboBox clusters = AddCombo(controls, "CLUSTERS",
            [("Monotone graphemes", (object)ShapingClusterLevel.MonotoneGraphemes),
             ("Monotone characters", ShapingClusterLevel.MonotoneCharacters),
             ("Characters", ShapingClusterLevel.Characters),
             ("Graphemes", ShapingClusterLevel.Graphemes)], 180f);
        ComboBox ignorables = AddCombo(controls, "BUFFER POLICY",
            [("Default", (object)ShapingBufferFlags.None),
             ("Full paragraph (BOT + EOT)", ShapingBufferFlags.BeginningOfText | ShapingBufferFlags.EndOfText),
             ("Preserve ignorables", ShapingBufferFlags.PreserveDefaultIgnorables),
             ("Remove ignorables", ShapingBufferFlags.RemoveDefaultIgnorables),
             ("No dotted circle", ShapingBufferFlags.DoNotInsertDottedCircle),
             ("Verify safe fragments", ShapingBufferFlags.Verify),
             ("Unsafe concat flags", ShapingBufferFlags.ProduceUnsafeToConcat),
             ("Safe tatweel flags", ShapingBufferFlags.ProduceSafeToInsertTatweel)], 210f);

        var textInput = new TextBox
        {
            Font = uiFont,
            FontSize = 15f,
            WidthConstraint = 720f,
            HeightConstraint = 38f,
            Margin = new Thickness(0, 0, 0, 12f)
        };
        stack.AddChild(Field("TEXT", textInput));

        var segmentFields = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalSpacing = 12f,
            VerticalSpacing = 10f,
            Margin = new Thickness(0, 0, 0, 12f)
        };
        var scriptInput = new TextBox { Font = uiFont, WidthConstraint = 96f, HeightConstraint = 34f };
        var languageInput = new TextBox { Font = uiFont, WidthConstraint = 96f, HeightConstraint = 34f };
        var featuresInput = new TextBox { Font = uiFont, WidthConstraint = 360f, HeightConstraint = 34f };
        var preContextInput = new TextBox { Font = uiFont, WidthConstraint = 150f, HeightConstraint = 34f };
        var postContextInput = new TextBox { Font = uiFont, WidthConstraint = 150f, HeightConstraint = 34f };
        segmentFields.AddChild(Field("SCRIPT", scriptInput));
        segmentFields.AddChild(Field("LANGUAGE", languageInput));
        segmentFields.AddChild(Field("FEATURES · tag=0, tag[start:end]=value", featuresInput));
        segmentFields.AddChild(Field("PRE-CONTEXT", preContextInput));
        segmentFields.AddChild(Field("POST-CONTEXT", postContextInput));
        stack.AddChild(segmentFields);

        var status = Label(string.Empty, 12f, SecondaryBrush, new Thickness(0, 0, 0, 8f));
        stack.AddChild(status);

        var previewFrame = new Border
        {
            Background = new ThemeResourceBrush(ControlBrush),
            BorderBrush = new ThemeResourceBrush(BorderBrush),
            BorderThickness = new Thickness(1f),
            CornerRadius = 9f,
            Padding = new Thickness(8f),
            Margin = new Thickness(0, 0, 0, 12f)
        };
        var preview = new ShapingBufferPreview { HeightConstraint = 210f };
        previewFrame.Child = preview;
        stack.AddChild(previewFrame);

        var diagnosticsFrame = new Border
        {
            Background = new ThemeResourceBrush(ControlBrush),
            BorderBrush = new ThemeResourceBrush(BorderBrush),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(12f)
        };
        var diagnostics = new RichTextBlock
        {
            Font = AppState.GetFontCourier() ?? uiFont,
            FontSize = 11.5f,
            TextWrapping = TextWrapping.Wrap
        };
        diagnosticsFrame.Child = diagnostics;
        stack.AddChild(diagnosticsFrame);

        var applyingPreset = false;
        TtfFont activeFont = InterFontFamily.Regular;

        void UpdateShape()
        {
            if (applyingPreset) return;
            if (!TryCreateRequest(
                    scriptInput.Text,
                    languageInput.Text,
                    (direction.SelectedItem as ComboBoxItem)?.Tag,
                    (clusters.SelectedItem as ComboBoxItem)?.Tag,
                    (ignorables.SelectedItem as ComboBoxItem)?.Tag,
                    featuresInput.Text,
                    preContextInput.Text,
                    postContextInput.Text,
                    out ShapingRequest request,
                    out string error))
            {
                SetText(status, error, AccentBrush);
                SetText(diagnostics, "Fix the shaping request to inspect its output.");
                preview.Clear();
                return;
            }

            try
            {
                ShapingSnapshot snapshot = Shape(textInput.Text, activeFont, request);
                preview.SetSnapshot(snapshot, activeFont, request.Direction, 48f);
                SetText(status,
                    $"{activeFont.FamilyName} · {snapshot.Glyphs.Length} glyphs from {snapshot.ScalarCount} Unicode scalars · {request.Direction}",
                    SecondaryBrush);
                SetText(diagnostics, FormatDiagnostics(snapshot, request), PrimaryBrush);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                SetText(status, $"Shaping failed: {exception.Message}", AccentBrush);
                preview.Clear();
            }
        }

        void ApplyPreset(ShapingPreset selected)
        {
            applyingPreset = true;
            textInput.Text = selected.Text;
            scriptInput.Text = selected.Script;
            languageInput.Text = selected.Language;
            featuresInput.Text = selected.Features;
            preContextInput.Text = selected.PreContext;
            postContextInput.Text = selected.PostContext;
            SelectByTag(direction, selected.Direction);
            SelectByTag(clusters, selected.ClusterLevel);
            SelectByTag(ignorables, selected.Flags);
            activeFont = ResolvePresetFont(selected, out bool exactMatch);
            applyingPreset = false;
            SetText(status,
                exactMatch
                    ? selected.Description
                    : $"{selected.Description} A matching face is unavailable, so missing glyphs use {activeFont.FamilyName}.",
                exactMatch ? SecondaryBrush : AccentBrush);
            UpdateShape();
        }

        preset.SelectionChanged += (_, _) =>
        {
            if ((preset.SelectedItem as ComboBoxItem)?.Tag is ShapingPreset selected)
                ApplyPreset(selected);
        };
        direction.SelectionChanged += (_, _) => UpdateShape();
        clusters.SelectionChanged += (_, _) => UpdateShape();
        ignorables.SelectionChanged += (_, _) => UpdateShape();
        textInput.TextChanged += (_, _) => UpdateShape();
        scriptInput.TextChanged += (_, _) => UpdateShape();
        languageInput.TextChanged += (_, _) => UpdateShape();
        featuresInput.TextChanged += (_, _) => UpdateShape();
        preContextInput.TextChanged += (_, _) => UpdateShape();
        postContextInput.TextChanged += (_, _) => UpdateShape();

        preset.SelectedItem = (ComboBoxItem)preset.Items[0];
        ApplyPreset(Presets[0]);
        return panel;
    }

    private static FrameworkElement CreateFeatureWall()
    {
        var wall = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 286f,
            HorizontalSpacing = 12f,
            VerticalSpacing = 12f,
            Margin = new Thickness(0, 0, 0, 70f)
        };
        wall.AddChild(FeatureCard("liga", "Standard ligatures", "office affinity", "latn", "en"));
        wall.AddChild(FeatureCard("kern", "Pair positioning", "AVATAR · To Wa Yo", "latn", "en"));
        wall.AddChild(FeatureCard("frac", "Automatic fractions", "1/2  12/25", "latn", "en"));
        wall.AddChild(FeatureCard("zero", "Slashed zero", "O0  1002003", "latn", "en"));
        wall.AddChild(FeatureCard("ss01", "Stylistic set 01", "1234567890", "latn", "en"));
        wall.AddChild(FeatureCard("calt", "Contextual alternates", "->  -->  <->  =>", "latn", "en"));
        wall.AddChild(FeatureCard("locl", "Localized Romanian", "Ş ş  Ţ ţ", "latn", "ro"));
        wall.AddChild(FeatureCard("mkmk", "Mark-to-mark", "a\u0308\u0301  o\u0302\u0301", "latn", "en"));
        wall.AddChild(VariationCard());
        return wall;
    }

    private static FrameworkElement VariationCard()
    {
        var card = Card();
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        card.Child = stack;
        stack.AddChild(Label("fvar / gvar / HVAR / GDEF", 11f, AccentBrush, new Thickness(0, 0, 0, 8f), bold: true));
        stack.AddChild(Label("VARIABLE LAYOUT", 9f, SecondaryBrush, new Thickness(0, 0, 0, 3f), bold: true));
        foreach (float weight in new[] { 200f, 500f, 800f })
        {
            stack.AddChild(new TextVisual
            {
                Font = InterFontFamily.GetVariableFont(weight, 18f),
                Text = $"wght {weight:0}  AVATAR ä́",
                FontSize = 17f,
                HeightConstraint = 25f,
                Brush = new ThemeResourceBrush(PrimaryBrush),
                TextShapingOptions = new TextShapingOptions { Script = "latn", Language = "en" }
            });
        }
        return card;
    }

    private static FrameworkElement FeatureCard(
        string tag,
        string name,
        string text,
        string script,
        string language)
    {
        var card = Card();
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        card.Child = stack;
        stack.AddChild(Label($"{tag}  /  {name}", 11f, AccentBrush, new Thickness(0, 0, 0, 10f), bold: true));
        stack.AddChild(FeatureLine("OFF", text, script, language, tag, enabled: false));
        stack.AddChild(FeatureLine("ON", text, script, language, tag, enabled: true));
        return card;
    }

    private static FrameworkElement FeatureLine(
        string state,
        string text,
        string script,
        string language,
        string tag,
        bool enabled)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 8f) };
        stack.AddChild(Label(state, 9f, SecondaryBrush, new Thickness(0, 0, 0, 2f), bold: true));
        stack.AddChild(new TextVisual
        {
            Font = InterFontFamily.Regular,
            Text = text,
            FontSize = 22f,
            HeightConstraint = 34f,
            Brush = new ThemeResourceBrush(PrimaryBrush),
            TextShapingOptions = new TextShapingOptions
            {
                Script = script,
                Language = language,
                Direction = ShapingDirection.LeftToRight,
                Features = ResolveFeatureSettings(tag, enabled)
            }
        });
        return stack;
    }

    private static IReadOnlyList<OpenTypeFeatureSetting> ResolveFeatureSettings(string tag, bool enabled)
    {
        var settings = TextShapingOptions.DefaultFeatures.ToList();
        if (tag == "mkmk") settings.Add(new OpenTypeFeatureSetting("ccmp", 0));
        settings.Add(new OpenTypeFeatureSetting(tag, enabled ? 1 : 0));
        return settings;
    }

    private static FrameworkElement CreateScriptAtlas()
    {
        var atlas = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 286f,
            HorizontalSpacing = 12f,
            VerticalSpacing = 12f,
            Margin = new Thickness(0, 0, 0, 70f)
        };
        atlas.AddChild(ScriptCard("ARABIC", "السَّلَامُ عَلَيْكُمْ", "arab", "ar", ShapingDirection.RightToLeft, 0x0627));
        atlas.AddChild(ScriptCard("DEVANAGARI", "नमस्ते दुनिया", "deva", "hi", ShapingDirection.LeftToRight, 0x0928));
        atlas.AddChild(ScriptCard("KHMER", "សួស្តី ពិភពលោក", "khmr", "km", ShapingDirection.LeftToRight, 0x179f));
        atlas.AddChild(ScriptCard("MYANMAR", "မင်္ဂလာပါ ကမ္ဘာ", "mymr", "my", ShapingDirection.LeftToRight, 0x1019));
        atlas.AddChild(ScriptCard("HEBREW", "שָׁלוֹם עוֹלָם", "hebr", "he", ShapingDirection.RightToLeft, 0x05e9));
        atlas.AddChild(ScriptCard("THAI", "สวัสดีชาวโลก", "thai", "th", ShapingDirection.LeftToRight, 0x0e2a));
        atlas.AddChild(ScriptCard("HANGUL JAMO", "한글 자모", "hang", "ko", ShapingDirection.LeftToRight, 0x1112, preferJapanese: true));
        atlas.AddChild(ScriptCard("JAPANESE · VERTICAL", "日本語の縦書き", "kana", "ja", ShapingDirection.TopToBottom, 0x65e5, preferJapanese: true));
        return atlas;
    }

    private static FrameworkElement ScriptCard(
        string name,
        string text,
        string script,
        string language,
        ShapingDirection direction,
        int representativeCodePoint,
        bool preferJapanese = false)
    {
        TtfFont font = ResolveFont(representativeCodePoint, language, preferJapanese, out bool exactMatch);
        var card = Card(height: direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop ? 238f : 156f);
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        card.Child = stack;
        stack.AddChild(Label(name, 10f, AccentBrush, new Thickness(0, 0, 0, 7f), bold: true));
        stack.AddChild(new TextVisual
        {
            Font = font,
            Text = text,
            FontSize = 26f,
            HeightConstraint = direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop ? 150f : 58f,
            Brush = new ThemeResourceBrush(PrimaryBrush),
            TextShapingOptions = new TextShapingOptions
            {
                Script = script,
                Language = language,
                Direction = direction
            }
        });
        stack.AddChild(Body(
            exactMatch ? $"{script} · {language} · {direction} · {font.FamilyName}" : $"{script} · {language} · matching face unavailable",
            10f,
            exactMatch ? SecondaryBrush : AccentBrush));
        return card;
    }

    private static FrameworkElement CreateCapabilityMap()
    {
        var map = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 286f,
            HorizontalSpacing = 12f,
            VerticalSpacing = 12f,
            Margin = new Thickness(0, 0, 0, 70f)
        };
        map.AddChild(CapabilityCard("UNICODE BUFFER",
            "Unicode 17 script and joining data\nNFC/NFD composition and decomposition\nGrapheme or character clusters\nVariation selectors and default ignorables\nBroken-syllable dotted-circle recovery"));
        map.AddChild(CapabilityCard("GSUB · SUBSTITUTION",
            "Single, multiple, alternate, ligature\nContextual and chained contextual\nReverse chaining and extensions\nNested lookups and lookup flags\nInteger, random, global, and ranged features"));
        map.AddChild(CapabilityCard("GPOS · POSITIONING",
            "Single and pair adjustment\nCursive attachment\nMark-to-base, ligature, and mark\nContextual, chaining, and extensions\nDevice tables and variation deltas"));
        map.AddChild(CapabilityCard("SCRIPT MODELS",
            "Arabic joining and stretching\nIndic old/new models and Sinhala\nUSE, Khmer, and Myanmar\nThai/Lao, Tibetan, and Hangul\nHebrew, Javanese, and joining scripts"));
        map.AddChild(CapabilityCard("DIRECTION + METRICS",
            "Left-to-right and right-to-left\nTop-to-bottom and bottom-to-top\nMirroring and vertical alternates\nHorizontal/vertical origins and advances\nDesign-unit deterministic positions"));
        map.AddChild(CapabilityCard("REUSE + EXECUTION",
            "Typed reflection-free font-face contract\nPool-backed reusable shaping buffers\nFont/segment/feature plan cache\nCPU reference and WebGPU plan contracts\nRetained glyph-run rendering"));
        return map;
    }

    private static FrameworkElement CapabilityCard(string title, string body)
    {
        var card = Card(height: 176f);
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        card.Child = stack;
        stack.AddChild(Label(title, 10f, AccentBrush, new Thickness(0, 0, 0, 8f), bold: true));
        stack.AddChild(Body(body, 12f, PrimaryBrush));
        return card;
    }

    private static FrameworkElement CreateArchitectureStrip()
    {
        var strip = new Border
        {
            Background = new ThemeResourceBrush(AccentBrush),
            CornerRadius = 12f,
            Padding = new Thickness(20f),
            Margin = new Thickness(0, 0, 0, 20f)
        };
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        strip.Child = stack;
        stack.AddChild(Label("ONE RESULT, MANY CONSUMERS", 10f, "TextOnAccent", new Thickness(0, 0, 0, 8f), bold: true));
        stack.AddChild(Label(
            "Unicode run  →  shaping request  →  cached OpenType plan  →  pooled glyph buffer  →  layout / hit testing / retained GPU draw",
            16f,
            "TextOnAccent",
            new Thickness(0, 0, 0, 7f),
            bold: true));
        stack.AddChild(Body(
            "Shaping remains a reusable CPU result; glyph upload, rasterization, batching, and compositing stay demand-driven on the GPU.",
            12f,
            "TextOnAccent"));
        return strip;
    }

    private static Border Card(float? height = null) => new()
    {
        Background = new ThemeResourceBrush(CardBrush),
        BorderBrush = new ThemeResourceBrush(BorderBrush),
        BorderThickness = new Thickness(1f),
        CornerRadius = 9f,
        Padding = new Thickness(14f),
        HeightConstraint = height
    };

    private static FrameworkElement Badge(string text) => new Border
    {
        Background = new ThemeResourceBrush(ControlBrush),
        BorderBrush = new ThemeResourceBrush(BorderBrush),
        BorderThickness = new Thickness(1f),
        CornerRadius = 12f,
        Padding = new Thickness(10f, 5f, 10f, 5f),
        Child = Label(text, 10f, SecondaryBrush, bold: true)
    };

    private static FrameworkElement SectionHeading(string title, string description)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 18f) };
        stack.AddChild(Label(title, 29f, PrimaryBrush, new Thickness(0, 0, 0, 5f), bold: true));
        stack.AddChild(Body(description, 14f, SecondaryBrush));
        return stack;
    }

    private static RichTextBlock Label(
        string text,
        float size,
        string brush,
        Thickness margin = default,
        bool bold = false)
    {
        var block = new RichTextBlock
        {
            Font = AppState.GetFont() ?? InterFontFamily.Regular,
            FontSize = size,
            Foreground = new ThemeResourceBrush(brush),
            Margin = margin,
            TextWrapping = TextWrapping.Wrap
        };
        block.Inlines.Add(bold ? new Bold(new Run(text)) : new Run(text));
        return block;
    }

    private static RichTextBlock Body(
        string text,
        float size,
        string brush,
        Thickness margin = default) => Label(text, size, brush, margin);

    private static FrameworkElement Field(string name, FrameworkElement control)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.AddChild(Label(name, 9f, SecondaryBrush, new Thickness(0, 0, 0, 4f), bold: true));
        stack.AddChild(control);
        return stack;
    }

    private static ComboBox AddCombo(
        WrapPanel owner,
        string name,
        (string Text, object Tag)[] choices,
        float width)
    {
        var combo = new ComboBox
        {
            Font = AppState.GetFont() ?? InterFontFamily.Regular,
            WidthConstraint = width,
            HeightConstraint = 34f
        };
        for (var index = 0; index < choices.Length; index++)
        {
            combo.Items.Add(new ComboBoxItem { Text = choices[index].Text, Tag = choices[index].Tag });
        }
        owner.AddChild(Field(name, combo));
        return combo;
    }

    private static void SelectByTag(ComboBox combo, object value)
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (combo.Items[index] is ComboBoxItem item &&
                Equals(item.Tag, value))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static bool TryCreateRequest(
        string scriptText,
        string languageText,
        object? directionValue,
        object? clusterValue,
        object? flagsValue,
        string featureText,
        string preContext,
        string postContext,
        out ShapingRequest request,
        out string error)
    {
        request = default;
        string script = scriptText.Trim();
        if (!OpenTypeTag.TryParse(script, out OpenTypeTag scriptTag))
        {
            error = "Script must be a four-character printable OpenType tag, such as latn, arab, or deva.";
            return false;
        }
        if (directionValue is not ShapingDirection direction || direction == ShapingDirection.Unspecified)
        {
            error = "Select a resolved shaping direction.";
            return false;
        }
        if (clusterValue is not ShapingClusterLevel clusterLevel || flagsValue is not ShapingBufferFlags flags)
        {
            error = "Select cluster and buffer policies.";
            return false;
        }
        if (!TryParseFeatures(featureText, out ShapingFeature[] features, out error))
        {
            return false;
        }

        try
        {
            request = new ShapingRequest(
                direction,
                scriptTag,
                string.IsNullOrWhiteSpace(languageText) ? null : languageText.Trim(),
                clusterLevel,
                flags,
                features,
                preContext.AsMemory(),
                postContext.AsMemory());
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryParseFeatures(string text, out ShapingFeature[] features, out string error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            features = [];
            error = string.Empty;
            return true;
        }

        var result = new List<ShapingFeature>();
        string[] entries = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < entries.Length; index++)
        {
            string entry = entries[index];
            int equals = entry.IndexOf('=');
            string selector = equals < 0 ? entry : entry[..equals].Trim();
            string valueText = equals < 0 ? "1" : entry[(equals + 1)..].Trim();
            if (!uint.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value))
            {
                features = [];
                error = $"Feature '{entry}' has an invalid unsigned value.";
                return false;
            }

            uint start = 0;
            uint end = uint.MaxValue;
            int bracket = selector.IndexOf('[');
            string tagText = bracket < 0 ? selector : selector[..bracket].Trim();
            if (!OpenTypeTag.TryParse(tagText, out OpenTypeTag tag))
            {
                features = [];
                error = $"Feature '{entry}' must start with a four-character tag.";
                return false;
            }

            if (bracket >= 0)
            {
                if (!selector.EndsWith(']'))
                {
                    features = [];
                    error = $"Feature range '{entry}' must end with ].";
                    return false;
                }
                string range = selector[(bracket + 1)..^1];
                string[] bounds = range.Split(':', StringSplitOptions.TrimEntries);
                if (bounds.Length != 2 ||
                    !uint.TryParse(bounds[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
                    (!string.IsNullOrEmpty(bounds[1]) &&
                     !uint.TryParse(bounds[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out end)))
                {
                    features = [];
                    error = $"Feature range '{entry}' must use [start:end] UTF-16 indices.";
                    return false;
                }
            }

            if (start > end)
            {
                features = [];
                error = $"Feature range '{entry}' starts after it ends.";
                return false;
            }
            result.Add(new ShapingFeature(tag, value, start, end));
        }

        features = result.ToArray();
        error = string.Empty;
        return true;
    }

    private static ShapingSnapshot Shape(string text, TtfFont font, in ShapingRequest request)
    {
        using var buffer = new ShapingBuffer(Math.Max(32, text.Length));
        CpuOpenTypeShaper.Instance.Shape(text, new TtfShapingFontFace(font), request, buffer);
        return new ShapingSnapshot(text.EnumerateRunes().Count(), buffer.Glyphs.ToArray());
    }

    private static string FormatDiagnostics(ShapingSnapshot snapshot, in ShapingRequest request)
    {
        var text = new StringBuilder();
        text.Append("gid     code point   cluster     safety flags             advance (x, y)       offset (x, y)\n");
        int visible = Math.Min(snapshot.Glyphs.Length, 28);
        for (var index = 0; index < visible; index++)
        {
            ShapingGlyph glyph = snapshot.Glyphs[index];
            text.Append(CultureInfo.InvariantCulture,
                $"{glyph.GlyphId,-7} U+{glyph.CodePoint:X6}   {glyph.Cluster,-11} {glyph.Flags,-24} ({glyph.AdvanceX,5}, {glyph.AdvanceY,5})       ({glyph.OffsetX,5}, {glyph.OffsetY,5})\n");
        }
        if (visible < snapshot.Glyphs.Length)
        {
            text.Append(CultureInfo.InvariantCulture, $"… {snapshot.Glyphs.Length - visible} more glyphs\n");
        }
        text.Append(CultureInfo.InvariantCulture,
            $"script={request.Script}  language={request.Language ?? "default"}  direction={request.Direction}  clusters={request.ClusterLevel}  flags={request.Flags}\n");
        text.Append(CultureInfo.InvariantCulture,
            $"pre-context={FormatContext(request.PreContext.Span)}  post-context={FormatContext(request.PostContext.Span)}");
        return text.ToString();
    }

    private static string FormatContext(ReadOnlySpan<char> context) =>
        context.IsEmpty ? "∅" : $"“{context.ToString()}”";

    private static TtfFont ResolvePresetFont(ShapingPreset preset, out bool exactMatch) =>
        ResolveFont(preset.RepresentativeCodePoint, preset.Language, preset.PreferJapaneseFont, out exactMatch);

    private static TtfFont ResolveFont(
        int representativeCodePoint,
        string language,
        bool preferJapanese,
        out bool exactMatch)
    {
        TtfFont primary = AppState.GetFont() ?? InterFontFamily.Regular;
        if (preferJapanese)
        {
            TtfFont japanese = NotoFontFamily.Japanese;
            exactMatch = japanese.GetGlyphIndex((uint)representativeCodePoint) != 0;
            return exactMatch ? japanese : primary;
        }
        if (primary.GetGlyphIndex((uint)representativeCodePoint) != 0)
        {
            exactMatch = true;
            return primary;
        }
        if (FontApi.Manager.TryMatchCharacter(
                null,
                FontStyleRequest.Normal,
                [language],
                representativeCodePoint,
                out TtfFont? matched,
                out _))
        {
            exactMatch = matched is not null;
            return matched ?? primary;
        }
        exactMatch = false;
        return primary;
    }

    private static void SetText(RichTextBlock block, string text, string? brush = null)
    {
        block.Inlines.Clear();
        block.Inlines.Add(new Run(text)
        {
            Foreground = brush is null ? null : new ThemeResourceBrush(brush)
        });
        block.Invalidate();
    }

    private readonly record struct ShapingSnapshot(int ScalarCount, ShapingGlyph[] Glyphs);

    private sealed class ShapingBufferPreview : FrameworkElement
    {
        private readonly Pen _guidePen = new(new ThemeResourceBrush(BorderBrush), 1f);
        private readonly Pen _accentPen = new(new ThemeResourceBrush(AccentBrush), 1f);
        private ushort[] _glyphIndices = [];
        private Vector2[] _glyphPositions = [];
        private TtfFont? _font;
        private float _fontSize;
        private ShapingDirection _direction;

        public ShapingBufferPreview()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
        }

        public void SetSnapshot(
            ShapingSnapshot snapshot,
            TtfFont font,
            ShapingDirection direction,
            float fontSize)
        {
            _font = font;
            _direction = direction;
            _fontSize = fontSize;
            _glyphIndices = new ushort[snapshot.Glyphs.Length];
            _glyphPositions = new Vector2[snapshot.Glyphs.Length];
            float scale = fontSize / font.UnitsPerEm;
            bool vertical = direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop;
            float cursor = 0f;
            float fixedAxis = vertical ? 118f : 112f;
            for (var index = 0; index < snapshot.Glyphs.Length; index++)
            {
                ShapingGlyph glyph = snapshot.Glyphs[index];
                _glyphIndices[index] = checked((ushort)glyph.GlyphId);
                _glyphPositions[index] = vertical
                    ? new Vector2(fixedAxis + glyph.OffsetX * scale, 14f + cursor + glyph.OffsetY * scale)
                    : new Vector2(18f + cursor + glyph.OffsetX * scale, fixedAxis + glyph.OffsetY * scale);
                cursor += (vertical ? glyph.AdvanceY : glyph.AdvanceX) * scale;
            }
            Invalidate();
        }

        public void Clear()
        {
            _glyphIndices = [];
            _glyphPositions = [];
            _font = null;
            Invalidate();
        }

        public override void OnRender(DrawingContext context)
        {
            bool vertical = _direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop;
            if (vertical)
            {
                context.DrawLine(_guidePen, new Vector2(118f, 8f), new Vector2(118f, Math.Max(8f, Size.Y - 8f)));
                context.DrawLine(_accentPen, new Vector2(112f, 14f), new Vector2(124f, 14f));
            }
            else
            {
                context.DrawLine(_guidePen, new Vector2(8f, 112f), new Vector2(Math.Max(8f, Size.X - 8f), 112f));
                context.DrawLine(_accentPen, new Vector2(18f, 106f), new Vector2(18f, 118f));
            }
            if (_font is null || _glyphIndices.Length == 0) return;
            context.DrawGlyphRun(
                _glyphIndices,
                _glyphPositions,
                _font,
                _fontSize,
                ThemeManager.GetBrush(PrimaryBrush),
                Vector2.Zero,
                preferGlyphAtlas: true);
        }
    }
}
