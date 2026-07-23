using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using System.Diagnostics;

namespace ProGPU.Samples;

/// <summary>
/// Editorial, retained-mode specimen for the official Inter release. The page intentionally
/// updates variation instances and shaping options only in response to user input; steady
/// scrolling and replay reuse the same text layouts, glyph IDs, and atlas entries.
/// </summary>
public static class InterShowcasePage
{
    private const string SurfaceBrush = "TypographySpecimenSurface";
    private const string InkBrush = "TypographySpecimenInk";
    private const string MutedBrush = "TypographySpecimenMuted";
    private const string RuleBrush = "TypographySpecimenRule";
    private const string AccentBrush = "TypographySpecimenAccent";
    private const string AccentInkBrush = "TypographySpecimenAccentInk";

    private static readonly TtfFont Inter = InterFontFamily.Regular;
    private static readonly TtfFont InterDisplay = InterFontFamily.GetVariableFont(500f, 32f);
    private static ScrollViewer? _benchmarkScrollViewer;
    private static float _benchmarkScrollDirection = 1f;

    private static readonly (int Weight, string Name)[] Weights =
    [
        (100, "Thin"),
        (200, "Extra Light"),
        (300, "Light"),
        (400, "Regular"),
        (500, "Medium"),
        (600, "Semi Bold"),
        (700, "Bold"),
        (800, "Extra Bold"),
        (900, "Black")
    ];

    // This is the complete feature inventory in the official Inter 4.1 variable fonts.
    private static readonly (string Tag, string Name, string Sample)[] CoreFeatures =
    [
        ("aalt", "Access all alternates", "1 3 4 6 9  I l a G t"),
        ("calt", "Contextual alternates", "3*9  12:34  3–8  ->  -->  --->  =>  ==>  <->"),
        ("case", "Case-sensitive forms", "abc (def) [ghi] {ui} @ 12:34"),
        ("ccmp", "Glyph composition", "A\u030A  a\u0308  o\u0302  S\u0326  T\u0326"),
        ("cpsp", "Capital spacing", "INTER VARIABLE TYPEFACE"),
        ("dlig", "Discretionary ligatures", "Difficult affine fjord — ff ffi fft ft fi tt tf df dt"),
        ("dnom", "Denominators", "0123456789  12  345"),
        ("frac", "Automatic fractions", "1/2  3/4  5/8  12/25  123/456"),
        ("kern", "Kerning", "AVATAR  To Wa Yo  Type"),
        ("locl", "Localized Romanian forms", "ş ţ Ş Ţ  ș ț Ș Ț"),
        ("mark", "Mark positioning", "a\u0308  o\u0302  S\u0326  A\u030A"),
        ("mkmk", "Mark-to-mark positioning", "a\u0308\u0301  q\u0308\u0301  A\u030A\u0301"),
        ("numr", "Numerators", "0123456789  12  345"),
        ("ordn", "Ordinals", "1a 2o 3a 4o 21a 42o"),
        ("pnum", "Proportional numbers", "000.45   12.91   805.02   2024"),
        ("salt", "Stylistic alternates", "1 3 4 6 9  I l a G t"),
        ("sinf", "Scientific inferiors", "H2O  SF6  H2SO4  CO2"),
        ("subs", "Subscript", "H0123456789 (+)-[=] abcdefghijklmnopqrstuvwxyz"),
        ("sups", "Superscript", "X0123456789 (+)-[=] abcdefghijklmnopqrstuvwxyz"),
        ("tnum", "Tabular numbers", "000.45   12.91   805.02   2024"),
        ("zero", "Slashed zero", "O0  00102030405060708090  code 0x00FF")
    ];

    private static readonly (string Tag, string Name, string Sample)[] StylisticSets =
    [
        ("ss01", "Open digits", "1234567890  3469"),
        ("ss02", "Disambiguation with zero", "WP0ACO9XSI1lO0  βeta  ßeta"),
        ("ss03", "Round quotes and commas", "“Inter,”  ‘interface,’  Sara Monroe"),
        ("ss04", "Disambiguation without zero", "I1l  O0  S5  G6  B8"),
        ("ss05", "Circled characters", "0123456789  ABCDEFG"),
        ("ss06", "Squared characters", "0123456789  ABCDEFG"),
        ("ss07", "Square punctuation", ". , : ; ! ?  ¿ ¡"),
        ("ss08", "Square quotes", "“Inter”  ‘UI’  «type»")
    ];

    private static readonly (string Tag, string Name, string Sample)[] CharacterVariants =
    [
        ("cv01", "Alternate one", "1  11  101"),
        ("cv02", "Open four", "4  44  404"),
        ("cv03", "Open six", "6  66  606"),
        ("cv04", "Open nine", "9  99  909"),
        ("cv05", "Lowercase l with tail", "l ł ƚ ɫ ɬ ŀ ĺ ļ ľ ḷ"),
        ("cv06", "Simplified u", "u U  ui  interface"),
        ("cv07", "German double-s", "ß  Straße  groß"),
        ("cv08", "Uppercase I with serif", "I Ï Ḯ Ɨ Ḭ Ì Í Î Ĩ"),
        ("cv09", "Flat-top three", "3  33  303"),
        ("cv10", "Capital G with spur", "G Ǥ Ɠ Ĝ Ğ Ġ Ģ"),
        ("cv11", "Single-storey a", "a á ă ä å ã  interface"),
        ("cv12", "Compact f", "f ff fi fj  afford"),
        ("cv13", "Compact t", "t tt tf  interface"),
        ("cv14", "Alternate capital sharp-S", "ẞ  GROẞ  MAẞE")
    ];

    private static readonly (string Name, string Sample)[] LanguageSamples =
    [
        ("Latin", "À bientôt · Smörgåsbord · Pchnąć w tę łódź jeża lub ośm skrzyń fig"),
        ("Greek", "Ταχίστη αλώπηξ βαφής ψημένη γη, δρασκελίζει υπέρ νωθρού κυνός"),
        ("Cyrillic", "Съешь же ещё этих мягких французских булок, да выпей чаю")
    ];

    public static FrameworkElement Create()
    {
        var page = new Border
        {
            RequestedTheme = ElementTheme.Light,
            Background = new ThemeResourceBrush(SurfaceBrush),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var scroll = new ScrollViewer
        {
            Background = new ThemeResourceBrush(SurfaceBrush),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _benchmarkScrollViewer = scroll;
        page.Child = scroll;

        var document = new StackPanel
        {
            Font = Inter,
            Orientation = Orientation.Vertical,
            Margin = new Thickness(40f, 28f, 40f, 64f)
        };
        scroll.Content = document;

        document.AddChild(CreateTopNavigation());
        var hero = new RichTextBlock
        {
            Font = InterDisplay,
            FontSize = 108f,
            Margin = new Thickness(0, 92f, 0, 24f),
            Foreground = new ThemeResourceBrush(InkBrush)
        };
        hero.Inlines.Add(new Run("The Inter\ntypeface family"));
        document.AddChild(hero);
        document.AddChild(CreateIntroduction());
        document.AddChild(CreateTypeLab());

        document.AddChild(SectionHeading("Nine weights. True italics.",
            "Inter’s two variable-font files cover a continuous 100–900 weight range. The italic is a drawn face, not a synthetic shear."));
        for (var index = 0; index < Weights.Length; index++)
        {
            document.AddChild(CreateWeightRow(Weights[index]));
        }

        document.AddChild(CreateOpticalSizeSection());
        document.AddChild(CreateFeatureCatalog());
        document.AddChild(CreateGlyphSection());
        document.AddChild(CreateLanguageSection());
        document.AddChild(CreateClosingStatement());

        StageInvisibleSpecimenLayouts(page);

        return page;
    }

    private static void StageInvisibleSpecimenLayouts(FrameworkElement page)
    {
        var specimens = new List<TextVisual>();
        CollectFixedHeightSpecimens(page, specimens);
        for (var index = 0; index < specimens.Count; index++)
        {
            specimens[index].DeferLayoutUntilRender = true;
        }
        if (specimens.Count == 0)
        {
            return;
        }

        void WarmInBackground()
        {
            for (var index = 0; index < specimens.Count; index++)
            {
                if (page.Parent == null)
                {
                    // Navigation may cache a detached page. Do not keep warming content
                    // that cannot become visible; a later activation shapes on demand.
                    return;
                }

                specimens[index].WarmDeferredLayout();
            }
        }

        var next = 0;
        var retriesBeforeLayout = 0;
        void WarmBrowserSlice()
        {
            if (page.Parent == null)
            {
                return;
            }

            long start = Stopwatch.GetTimestamp();
            do
            {
                if (!specimens[next].WarmDeferredLayout())
                {
                    if (++retriesBeforeLayout < 4)
                    {
                        UIThread.Post(WarmBrowserSlice);
                    }
                    return;
                }

                retriesBeforeLayout = 0;
                next++;
            }
            while (next < specimens.Count && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 4d);

            if (next < specimens.Count)
            {
                UIThread.Post(WarmBrowserSlice);
            }
        }

        // Let the lightweight visible page measure, arrange, and present before starting
        // one sequential CPU-only worker for content below the viewport. Atlas allocation
        // remains demand-driven on the compositor thread when a specimen becomes visible.
        UIThread.Post(() => UIThread.Post(() =>
        {
            if (OperatingSystem.IsBrowser())
            {
                // Browser builds without worker-thread support preserve a bounded UI slice.
                WarmBrowserSlice();
            }
            else
            {
                _ = Task.Run(WarmInBackground);
            }
        }));
    }

    private static void CollectFixedHeightSpecimens(Visual visual, List<TextVisual> specimens)
    {
        if (visual is TextVisual { HeightConstraint: not null } specimen)
        {
            specimens.Add(specimen);
        }

        if (visual is not ContainerVisual container)
        {
            return;
        }

        var children = container.Children;
        for (var index = 0; index < children.Count; index++)
        {
            CollectFixedHeightSpecimens(children[index], specimens);
        }
    }

    internal static void AdvanceBenchmarkScroll(float step)
    {
        if (_benchmarkScrollViewer == null)
        {
            return;
        }

        float maxOffset = Math.Max(
            0f,
            _benchmarkScrollViewer.ContentHeight - _benchmarkScrollViewer.Size.Y);
        if (maxOffset <= 0f)
        {
            return;
        }

        float nextOffset = _benchmarkScrollViewer.VerticalOffset + _benchmarkScrollDirection * step;
        if (nextOffset >= maxOffset)
        {
            nextOffset = maxOffset;
            _benchmarkScrollDirection = -1f;
        }
        else if (nextOffset <= 0f)
        {
            nextOffset = 0f;
            _benchmarkScrollDirection = 1f;
        }

        _benchmarkScrollViewer.VerticalOffset = nextOffset;
    }

    internal static bool TryGetBenchmarkScrollState(out float verticalOffset, out float maximumOffset)
    {
        if (_benchmarkScrollViewer == null)
        {
            verticalOffset = 0f;
            maximumOffset = 0f;
            return false;
        }

        verticalOffset = _benchmarkScrollViewer.VerticalOffset;
        maximumOffset = Math.Max(
            0f,
            _benchmarkScrollViewer.ContentHeight - _benchmarkScrollViewer.Size.Y);
        return true;
    }

    private static FrameworkElement CreateTopNavigation()
    {
        var grid = new Grid { HeightConstraint = 28f };
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new GridLength(360f, GridUnitType.Absolute));

        grid.AddChild(Label($"Inter {InterFontFamily.Version} · variable specimen", 12f, MutedBrush));
        var nav = new Grid();
        for (var index = 0; index < 5; index++)
        {
            nav.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        }

        string[] items = ["Features", "Glyphs", "Axes", "Languages", "OpenType"];
        for (var index = 0; index < items.Length; index++)
        {
            FrameworkElement item = Label(items[index], 12f, InkBrush);
            nav.AddChild(item);
            Grid.SetColumn(item, index);
        }

        grid.AddChild(nav);
        Grid.SetColumn(nav, 1);
        return grid;
    }

    private static FrameworkElement CreateIntroduction()
    {
        var grid = new Grid { Margin = new Thickness(0, 4f, 0, 92f) };
        grid.ColumnDefinitions.Add(new GridLength(0.75f, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new GridLength(1.35f, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new GridLength(1.35f, GridUnitType.Star));

        grid.AddChild(Body("The 21st century standard", 16f, InkBrush, new Thickness(0, 0, 28f, 0)));

        FrameworkElement overview = Body(
            $"Inter is a workhorse typeface for interfaces, editorial text, and display typography. This sample uses the unmodified official {InterFontFamily.Version} files and renders {Inter.NumGlyphs:N0} glyphs through ProGPU’s retained text pipeline.",
            15f,
            InkBrush,
            new Thickness(0, 0, 28f, 0));
        grid.AddChild(overview);
        Grid.SetColumn(overview, 1);

        FrameworkElement optical = Body(
            "The text design keeps a tall x-height and open details at small sizes. The display design tightens spacing and refines curves as the standard optical-size axis moves from 14 to 32.",
            15f,
            InkBrush);
        grid.AddChild(optical);
        Grid.SetColumn(optical, 2);
        return grid;
    }

    private static FrameworkElement CreateTypeLab()
    {
        var panel = new Border
        {
            Background = new ThemeResourceBrush(InkBrush),
            Padding = new Thickness(28f),
            Margin = new Thickness(0, 0, 0, 104f)
        };
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        panel.Child = stack;

        var heading = new Grid { Margin = new Thickness(0, 0, 0, 20f) };
        heading.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        heading.ColumnDefinitions.Add(new GridLength(220f, GridUnitType.Absolute));
        heading.AddChild(Label("Variable type lab", 24f, SurfaceBrush, weight: 650f));
        FrameworkElement axisSummary = Label("wght 450 · opsz 28 · 72 px", 12f, SurfaceBrush);
        heading.AddChild(axisSummary);
        Grid.SetColumn(axisSummary, 1);
        stack.AddChild(heading);

        var preview = new TextVisual
        {
            Font = InterFontFamily.GetVariableFont(450f, 28f),
            Text = "Make something\nclear and beautiful.",
            FontSize = 72f,
            HeightConstraint = 180f,
            Brush = new ThemeResourceBrush(SurfaceBrush)
        };
        stack.AddChild(preview);

        var input = new TextBox
        {
            Font = Inter,
            Text = preview.Text.Replace('\n', ' '),
            PlaceholderText = "Type your own specimen",
            WidthConstraint = 460f,
            HeightConstraint = 38f,
            Margin = new Thickness(0, 16f, 0, 18f)
        };
        stack.AddChild(input);

        var controls = new Grid();
        controls.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        controls.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        controls.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        controls.ColumnDefinitions.Add(new GridLength(96f, GridUnitType.Absolute));
        var weight = AxisControl("WEIGHT", 100f, 900f, 450f);
        var opticalSize = AxisControl("OPTICAL SIZE", 14f, 32f, 28f);
        var fontSize = AxisControl("FONT SIZE", 32f, 112f, 72f);
        controls.AddChild(weight.Panel);
        controls.AddChild(opticalSize.Panel);
        controls.AddChild(fontSize.Panel);
        Grid.SetColumn(opticalSize.Panel, 1);
        Grid.SetColumn(fontSize.Panel, 2);

        var italic = new ToggleButton
        {
            Content = Label("ITALIC", 10f, InkBrush, weight: 650f),
            HeightConstraint = 34f,
            Margin = new Thickness(12f, 18f, 0, 0),
            Background = new ThemeResourceBrush(SurfaceBrush),
            BorderBrush = new ThemeResourceBrush(SurfaceBrush)
        };
        controls.AddChild(italic);
        Grid.SetColumn(italic, 3);
        stack.AddChild(controls);

        var featureBar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalSpacing = 8f,
            VerticalSpacing = 8f,
            Margin = new Thickness(0, 20f, 0, 0)
        };
        stack.AddChild(featureBar);
        string[] liveFeatureTags = ["calt", "dlig", "frac", "tnum", "zero", "ss01", "ss02", "cv11"];
        var featureButtons = new List<(string Tag, ToggleButton Button)>();
        for (var index = 0; index < liveFeatureTags.Length; index++)
        {
            string tag = liveFeatureTags[index];
            var button = new ToggleButton
            {
                Content = Label(tag, 11f, InkBrush, weight: 650f),
                WidthConstraint = 62f,
                HeightConstraint = 30f,
                Background = new ThemeResourceBrush(SurfaceBrush),
                BorderBrush = new ThemeResourceBrush(SurfaceBrush)
            };
            featureButtons.Add((tag, button));
            featureBar.AddChild(button);
        }

        void UpdatePreview()
        {
            float resolvedWeight = MathF.Round((float)weight.Slider.Value);
            float resolvedOpticalSize = MathF.Round((float)opticalSize.Slider.Value * 2f) / 2f;
            float resolvedFontSize = MathF.Round((float)fontSize.Slider.Value);
            FontSlant slant = italic.IsChecked ? FontSlant.Italic : FontSlant.Upright;

            preview.Font = InterFontFamily.GetVariableFont(resolvedWeight, resolvedOpticalSize, slant);
            preview.FontSize = resolvedFontSize;
            preview.Text = string.IsNullOrWhiteSpace(input.Text) ? "Make something clear and beautiful." : input.Text;

            var settings = new List<OpenTypeFeatureSetting>(featureButtons.Count);
            for (var index = 0; index < featureButtons.Count; index++)
            {
                (string tag, ToggleButton button) = featureButtons[index];
                settings.Add(new OpenTypeFeatureSetting(tag, button.IsChecked ? 1 : 0));
                button.Background = new ThemeResourceBrush(button.IsChecked ? AccentBrush : SurfaceBrush);
            }
            preview.TextShapingOptions = TextShapingOptions.WithFeatures(settings.ToArray());

            var summary = (RichTextBlock)axisSummary;
            summary.Inlines.Clear();
            summary.Inlines.Add(new Run(
                $"wght {resolvedWeight:0} · opsz {resolvedOpticalSize:0.#} · {resolvedFontSize:0} px"));
            summary.Invalidate();
        }

        input.TextChanged += (_, _) => UpdatePreview();
        weight.Slider.ValueChanged += (_, _) => UpdatePreview();
        opticalSize.Slider.ValueChanged += (_, _) => UpdatePreview();
        fontSize.Slider.ValueChanged += (_, _) => UpdatePreview();
        italic.CheckedChanged += (_, _) =>
        {
            italic.Background = new ThemeResourceBrush(italic.IsChecked ? AccentBrush : SurfaceBrush);
            UpdatePreview();
        };
        for (var index = 0; index < featureButtons.Count; index++)
        {
            featureButtons[index].Button.CheckedChanged += (_, _) => UpdatePreview();
        }

        return panel;
    }

    private static (FrameworkElement Panel, Slider Slider) AxisControl(
        string name,
        float minimum,
        float maximum,
        float value)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 18f, 0) };
        stack.AddChild(Label(name, 10f, SurfaceBrush, new Thickness(0, 0, 0, 4f), 650f));
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            HeightConstraint = 28f
        };
        stack.AddChild(slider);
        return (stack, slider);
    }

    private static FrameworkElement CreateWeightRow((int Weight, string Name) style)
    {
        var row = new Border
        {
            BorderBrush = new ThemeResourceBrush(RuleBrush),
            BorderThickness = new Thickness(0, 1f, 0, 0),
            Padding = new Thickness(0, 14f, 0, 16f)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new GridLength(120f, GridUnitType.Absolute));
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        row.Child = grid;

        grid.AddChild(Label($"{style.Name}\n{style.Weight}", 11f, MutedBrush));
        FrameworkElement upright = SpecimenText(
            "Designing clear interfaces 0123456789",
            InterFontFamily.GetVariableFont(style.Weight, 18f),
            26f,
            42f,
            new Thickness(0, 0, 18f, 0));
        FrameworkElement italic = SpecimenText(
            "Designing clear interfaces 0123456789",
            InterFontFamily.GetVariableFont(style.Weight, 18f, FontSlant.Italic),
            26f,
            42f);
        grid.AddChild(upright);
        grid.AddChild(italic);
        Grid.SetColumn(upright, 1);
        Grid.SetColumn(italic, 2);
        return row;
    }

    private static FrameworkElement CreateOpticalSizeSection()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 100f, 0, 104f) };
        stack.AddChild(SectionHeading(
            "Optical size, built in.",
            "The opsz axis interpolates the official text and display designs. It changes outlines, spacing, kerning, and metrics rather than scaling one master."));

        var comparison = new Grid();
        comparison.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        comparison.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        FrameworkElement text = OpticalSpecimen("TEXT · OPSZ 14", 14f, new Thickness(0, 0, 24f, 0));
        FrameworkElement display = OpticalSpecimen("DISPLAY · OPSZ 32", 32f);
        comparison.AddChild(text);
        comparison.AddChild(display);
        Grid.SetColumn(display, 1);
        stack.AddChild(comparison);
        return stack;
    }

    private static FrameworkElement OpticalSpecimen(string label, float opticalSize, Thickness margin = default)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = margin };
        stack.AddChild(Label(label, 11f, MutedBrush, new Thickness(0, 0, 0, 10f), 650f));
        stack.AddChild(SpecimenText(
            "Interface\nat scale",
            InterFontFamily.GetVariableFont(450f, opticalSize),
            68f,
            172f));
        stack.AddChild(Body(
            opticalSize < 20f
                ? "Open counters, taller x-height, wider spacing, and sturdy details for compact text."
                : "Tighter rhythm, finer joins, and display proportions for large editorial type.",
            14f,
            MutedBrush,
            new Thickness(0, 8f, 0, 0)));
        return stack;
    }

    private static FrameworkElement CreateFeatureCatalog()
    {
        var band = new Border
        {
            Background = new ThemeResourceBrush(AccentBrush),
            Padding = new Thickness(34f, 42f, 34f, 54f),
            Margin = new Thickness(-40f, 0, -40f, 104f)
        };
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        band.Child = stack;
        stack.AddChild(Label("OpenType features", 50f, AccentInkBrush, new Thickness(0, 0, 0, 8f), 500f));
        stack.AddChild(Body(
            "Every feature present in Inter 4.1 is shaped below. OFF and ON are retained glyph runs generated from the same text; no browser CSS or platform text renderer is involved.",
            14f,
            AccentInkBrush,
            new Thickness(0, 0, 0, 32f)));

        stack.AddChild(FeatureGroupHeading("Core layout features"));
        AddFeatureRows(stack, CoreFeatures);
        stack.AddChild(FeatureGroupHeading("Stylistic sets"));
        AddFeatureRows(stack, StylisticSets);
        stack.AddChild(FeatureGroupHeading("Character variants"));
        AddFeatureRows(stack, CharacterVariants);
        return band;
    }

    private static void AddFeatureRows(
        StackPanel stack,
        IReadOnlyList<(string Tag, string Name, string Sample)> features)
    {
        for (var index = 0; index < features.Count; index++)
        {
            stack.AddChild(FeatureRow(features[index]));
        }
    }

    private static FrameworkElement FeatureGroupHeading(string text) =>
        Label(text, 22f, AccentInkBrush, new Thickness(0, 34f, 0, 10f), 650f);

    private static FrameworkElement FeatureRow((string Tag, string Name, string Sample) feature)
    {
        var row = new Border
        {
            BorderBrush = new ThemeResourceBrush(AccentInkBrush),
            BorderThickness = new Thickness(0, 1f, 0, 0),
            Padding = new Thickness(0, 12f, 0, 14f)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new GridLength(180f, GridUnitType.Absolute));
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        row.Child = grid;

        grid.AddChild(Label($"{feature.Tag}\n{feature.Name}", 11f, AccentInkBrush));
        FrameworkElement off = FeatureSpecimen("OFF", feature.Sample, feature.Tag, enabled: false);
        FrameworkElement on = FeatureSpecimen("ON", feature.Sample, feature.Tag, enabled: true);
        grid.AddChild(off);
        grid.AddChild(on);
        Grid.SetColumn(off, 1);
        Grid.SetColumn(on, 2);
        return row;
    }

    private static FrameworkElement FeatureSpecimen(string state, string text, string tag, bool enabled)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = enabled ? new Thickness(12f, 0, 0, 0) : new Thickness(0, 0, 12f, 0)
        };
        stack.AddChild(Label(state, 9f, AccentInkBrush, new Thickness(0, 0, 0, 3f), 650f));
        stack.AddChild(new TextVisual
        {
            Font = Inter,
            Text = text,
            FontSize = 20f,
            HeightConstraint = 44f,
            Brush = new ThemeResourceBrush(AccentInkBrush),
            TextShapingOptions = FeatureOptions(tag, enabled)
        });
        return stack;
    }

    private static TextShapingOptions FeatureOptions(string tag, bool enabled)
    {
        if (tag == "locl")
        {
            return new TextShapingOptions
            {
                Script = "latn",
                Language = "ro",
                Features = [new OpenTypeFeatureSetting("locl", enabled ? 1 : 0)]
            };
        }

        if (tag is "mark" or "mkmk")
        {
            return TextShapingOptions.WithFeatures(
                new OpenTypeFeatureSetting("ccmp", 0),
                new OpenTypeFeatureSetting(tag, enabled ? 1 : 0));
        }

        if (tag == "pnum")
        {
            return TextShapingOptions.WithFeatures(
                new OpenTypeFeatureSetting("tnum", enabled ? 0 : 1),
                new OpenTypeFeatureSetting("pnum", enabled ? 1 : 0));
        }

        if (tag == "case")
        {
            return TextShapingOptions.WithFeatures(
                new OpenTypeFeatureSetting("calt", 0),
                new OpenTypeFeatureSetting(tag, enabled ? 1 : 0));
        }

        return TextShapingOptions.WithFeatures(new OpenTypeFeatureSetting(tag, enabled ? 1 : 0));
    }

    private static FrameworkElement CreateGlyphSection()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 104f) };
        stack.AddChild(SectionHeading(
            $"{Inter.NumGlyphs:N0} glyphs.",
            "Latin Extended, Greek, Cyrillic, phonetics, mathematics, currencies, arrows, symbols, enclosed forms, and the complete alternate repertoire are available from the same official font data."));

        stack.AddChild(SpecimenText(
            "Aa Bb Cc Dd Ee Ff Gg Hh Ii Jj Kk Ll Mm\nNn Oo Pp Qq Rr Ss Tt Uu Vv Ww Xx Yy Zz",
            InterFontFamily.GetVariableFont(480f, 32f),
            45f,
            128f,
            new Thickness(0, 8f, 0, 24f)));
        stack.AddChild(GlyphLine("LATIN EXTENDED", "À Á Â Ã Ä Å Ā Ă Ą Æ Ç Ć Ĉ Č Ð Ď È É Ê Ë Ē Ė Ę Ě Ğ Ġ Ģ Ħ Ĩ Ī İ Ķ Ł Ń Ň Ŋ Ø Œ Ř Ś Š Ţ Ť Ŧ Ũ Ū Ů Ű Ų Ž"));
        stack.AddChild(GlyphLine("NUMBERS + CURRENCY", "0 1 2 3 4 5 6 7 8 9  ½ ⅓ ¼ ⅔ ¾ ⅛  % ‰  $ ¢ £ ¥ € ₩ ₹ ₺ ₿"));
        stack.AddChild(GlyphLine("SYMBOLS + ARROWS", "← ↑ → ↓ ↔ ↕ ↖ ↗ ↘ ↙  − × ÷ ≠ ≤ ≥ ≈ ∞ ∑ ∏ √ ∂ ∆  © ® ™ @ # &"));
        return stack;
    }

    private static FrameworkElement GlyphLine(string name, string glyphs)
    {
        var row = new Border
        {
            BorderBrush = new ThemeResourceBrush(RuleBrush),
            BorderThickness = new Thickness(0, 1f, 0, 0),
            Padding = new Thickness(0, 14f, 0, 18f)
        };
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        row.Child = stack;
        stack.AddChild(Label(name, 10f, MutedBrush, new Thickness(0, 0, 0, 7f), 650f));
        stack.AddChild(SpecimenText(glyphs, Inter, 27f, 72f));
        return row;
    }

    private static FrameworkElement CreateLanguageSection()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 104f) };
        stack.AddChild(SectionHeading(
            "Made for language.",
            "The bundled Inter files travel with the application, so the same shaped glyphs and metrics render in browser AOT, desktop, screenshots, and offscreen export."));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        for (var index = 0; index < LanguageSamples.Length; index++)
        {
            (string name, string sample) = LanguageSamples[index];
            var language = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = index == LanguageSamples.Length - 1
                    ? new Thickness(0)
                    : new Thickness(0, 0, 24f, 0)
            };
            language.AddChild(Label(name, 11f, MutedBrush, new Thickness(0, 0, 0, 10f), 650f));
            language.AddChild(Body(sample, 24f, InkBrush));
            grid.AddChild(language);
            Grid.SetColumn(language, index);
        }
        stack.AddChild(grid);
        return stack;
    }

    private static FrameworkElement CreateClosingStatement()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.AddChild(new Border
        {
            Background = new ThemeResourceBrush(InkBrush),
            HeightConstraint = 2f,
            Margin = new Thickness(0, 0, 0, 30f)
        });
        stack.AddChild(SpecimenText(
            "One typeface.\nEverywhere.",
            InterFontFamily.GetVariableFont(600f, 32f),
            86f,
            210f));
        stack.AddChild(Body(
            "Official Inter 4.1 · OpenType variable outlines · retained shaping · GPU atlas rendering",
            12f,
            MutedBrush,
            new Thickness(0, 20f, 0, 0)));
        return stack;
    }

    private static FrameworkElement SectionHeading(string title, string description)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 32f) };
        stack.AddChild(Label(title, 50f, InkBrush, new Thickness(0, 0, 0, 10f), 500f));
        stack.AddChild(Body(description, 15f, MutedBrush));
        return stack;
    }

    private static TextVisual SpecimenText(
        string text,
        TtfFont font,
        float size,
        float height,
        Thickness margin = default) =>
        new()
        {
            Font = font,
            Text = text,
            FontSize = size,
            HeightConstraint = height,
            Margin = margin,
            Brush = new ThemeResourceBrush(InkBrush)
        };

    private static RichTextBlock Body(
        string text,
        float size,
        string brush,
        Thickness margin = default) =>
        Label(text, size, brush, margin, 400f);

    private static RichTextBlock Label(
        string text,
        float size,
        string brush,
        Thickness margin = default,
        float weight = 400f)
    {
        var label = new RichTextBlock
        {
            Font = weight == 400f ? Inter : InterFontFamily.GetVariableFont(weight, size >= 28f ? 32f : 14f),
            FontSize = size,
            Foreground = new ThemeResourceBrush(brush),
            Margin = margin
        };
        label.Inlines.Add(new Run(text));
        return label;
    }
}
