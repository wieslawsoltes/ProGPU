using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Animation;
using ProGPU.WinUI.Themes.Fluent;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

[Collection(PlatformThemeResourceCollection.Name)]
public sealed class FluentThemeResourcesTests
{
    [Fact]
    public void GeneratedUpstreamDictionaryRegistersAndBuilds()
    {
        var dictionary = FluentThemeResources.CreateDictionary();

        Assert.NotNull(dictionary);
        Assert.True(dictionary.ThemeDictionaries.ContainsKey("Default"));
        Assert.True(dictionary.ThemeDictionaries.ContainsKey("Light"));
        Assert.True(dictionary.ThemeDictionaries.ContainsKey("HighContrast"));
        var defaultTheme = Assert.IsType<ResourceDictionary>(
            dictionary.ThemeDictionaries["Default"]);
        var lightTheme = Assert.IsType<ResourceDictionary>(
            dictionary.ThemeDictionaries["Light"]);
        var highContrastTheme = Assert.IsType<ResourceDictionary>(
            dictionary.ThemeDictionaries["HighContrast"]);
        Assert.True(defaultTheme.Count > 500);
        Assert.True(lightTheme.Count > 500);
        Assert.True(highContrastTheme.Count > 500);
        Assert.True(dictionary.TryLookup(
            "ButtonBackground",
            ElementTheme.Default,
            out var buttonBackground));
        Assert.NotNull(buttonBackground);
        Assert.True(dictionary.TryLookup(
            "ButtonBackground",
            ElementTheme.Light,
            out var lightButtonBackground));
        Assert.NotNull(lightButtonBackground);
        Assert.IsType<Style>(dictionary[typeof(Button)]);
        Assert.IsAssignableFrom<Brush>(
            highContrastTheme["SystemControlFocusVisualPrimaryBrush"]);
        Assert.IsType<ProGPU.Vector.Color>(
            highContrastTheme["ScrollBarThumbBackgroundColor"]);
    }

    [Fact]
    public void FluentThemeCanBeAppliedToApplicationResources()
    {
        var application = new Application();

        var applied = FluentThemeResources.Apply(application);

        Assert.Contains(applied, application.Resources.MergedDictionaries);
        Assert.True(application.Resources.TryLookup(
            "ButtonBackground",
            ElementTheme.Light,
            out var buttonBackground));
        Assert.NotNull(buttonBackground);
    }

    [Fact]
    public void RetainedElementSwitchesBetweenCompiledLightAndHighContrastPartitions()
    {
        var previousApplication = Application.Current;
        var previousProvider = XamlPlatformResources.Provider;
        var previousTheme = ThemeManager.CurrentTheme;
        var previousHighContrast = ThemeManager.IsHighContrast;
        var provider = new PlatformThemeResourceTests.TestPlatformResourceProvider();
        provider.Set(
            "SystemColorButtonTextColor",
            new Color(0xFF, 0xFF, 0x00));
        provider.Set(
            "SystemColorButtonFaceColor",
            new Color(0x00, 0x00, 0x00));
        provider.Set(
            "SystemColorGrayTextColor",
            new Color(0x80, 0x80, 0x80));
        provider.Set(
            "SystemColorHighlightColor",
            new Color(0x00, 0xFF, 0xFF));
        provider.Set(
            "SystemColorHighlightTextColor",
            new Color(0x00, 0x00, 0x00));
        provider.Set(
            "SystemColorHotlightColor",
            new Color(0x00, 0xFF, 0xFF));
        provider.Set(
            "SystemColorWindowColor",
            new Color(0x00, 0x00, 0x00));
        provider.Set(
            "SystemColorWindowTextColor",
            new Color(0xFF, 0xFF, 0x00));
        Window? window = null;

        try
        {
            ThemeManager.CurrentTheme = ElementTheme.Light;
            XamlPlatformResources.Provider = provider;
            var application = new Application();
            Application.Current = application;
            FluentThemeResources.Apply(application);
            var target = new Border();
            window = new Window { Content = target };

            Assert.True(application.Resources.TryLookup(
                "ButtonBackground",
                ElementTheme.Light,
                isHighContrast: false,
                out var expectedLight));
            Assert.True(application.Resources.TryLookup(
                "ButtonBackground",
                ElementTheme.Light,
                isHighContrast: true,
                out var expectedHighContrast));
            Assert.NotSame(expectedLight, expectedHighContrast);

            target.Background = new ThemeResourceBrush(target, "ButtonBackground");
            Assert.Same(expectedLight, target.Background);

            provider.IsHighContrast = true;
            provider.PublishResourcesChanged();
            var resolvedHighContrast = XamlResourceResolver.ResolveTheme(
                application.Resources,
                target,
                "ButtonBackground",
                ElementTheme.Light,
                VisualThemeFamily.WinUI);
            Assert.IsType<ThemeResourceBrush>(expectedHighContrast);
            Assert.Same(resolvedHighContrast, target.Background);
            Assert.Equal(
                new System.Numerics.Vector4(0f, 0f, 0f, 1f),
                Assert.IsType<SolidColorBrush>(target.Background).Color);

            provider.IsHighContrast = false;
            provider.PublishResourcesChanged();
            Assert.Same(expectedLight, target.Background);
        }
        finally
        {
            window?.Close();
            Application.Current = previousApplication;
            XamlPlatformResources.Provider = previousProvider;
            ThemeManager.CurrentTheme = previousTheme;
            ThemeManager.IsHighContrast = previousHighContrast;
        }
    }

    [Fact]
    public void GeneratedButtonAndCheckBoxStylesInstantiateCompleteTemplates()
    {
        var previousApplication = Application.Current;
        var previousHighContrast = ThemeManager.IsHighContrast;
        try
        {
            ThemeManager.IsHighContrast = false;
            var application = new Application();
            Application.Current = application;
            var dictionary = FluentThemeResources.Apply(application);

            var button = new Button { Content = "Generated Fluent button" };
            button.Style = Assert.IsType<Style>(dictionary[typeof(Button)]);
            Assert.NotNull(button.Template);
            Assert.True(button.ApplyTemplate());
            var buttonPresenter = Assert.IsType<ContentPresenter>(
                button.GetTemplateChild("ContentPresenter"));
            Assert.Equal("Generated Fluent button", buttonPresenter.Content);
            var commonStates = Assert.Single(
                VisualStateManager.GetVisualStateGroups(buttonPresenter),
                group => group.Name == "CommonStates");
            Assert.Contains(commonStates.States, state => state.Name == "Normal");
            Assert.Contains(commonStates.States, state => state.Name == "PointerOver");
            Assert.Contains(commonStates.States, state => state.Name == "Pressed");
            Assert.Contains(commonStates.States, state => state.Name == "Disabled");

            var checkBox = new CheckBox { Content = "Generated Fluent check box" };
            checkBox.Style = Assert.IsType<Style>(dictionary[typeof(CheckBox)]);
            Assert.NotNull(checkBox.Template);
            Assert.True(checkBox.ApplyTemplate());
            var checkBoxRoot = Assert.IsType<Grid>(
                checkBox.GetTemplateChild("RootGrid"));
            Assert.NotNull(checkBox.GetTemplateChild("NormalRectangle"));
            Assert.NotNull(checkBox.GetTemplateChild("CheckGlyph"));
            var combinedStates = Assert.Single(
                VisualStateManager.GetVisualStateGroups(checkBoxRoot),
                group => group.Name == "CombinedStates");
            Assert.Contains(combinedStates.States, state => state.Name == "UncheckedNormal");
            Assert.Contains(combinedStates.States, state => state.Name == "CheckedNormal");
            Assert.Contains(combinedStates.States, state => state.Name == "IndeterminateNormal");
            var uncheckedPressed = Assert.Single(
                combinedStates.States,
                state => state.Name == "UncheckedPressed");
            var strokeAnimation = Assert.IsType<DoubleAnimation>(Assert.Single(
                Assert.IsType<Storyboard>(uncheckedPressed.Storyboard).Children,
                timeline =>
                    timeline is DoubleAnimation &&
                    Storyboard.GetTargetName(timeline) == "NormalRectangle" &&
                    Storyboard.GetTargetProperty(timeline) == "StrokeThickness"));
            Assert.Equal<double?>(0d, strokeAnimation.To);

            button.Measure(new System.Numerics.Vector2(320f, 120f));
            button.Arrange(new ProGPU.Scene.Rect(0f, 0f, 200f, 48f));
            checkBox.Measure(new System.Numerics.Vector2(320f, 120f));
            checkBox.Arrange(new ProGPU.Scene.Rect(0f, 0f, 240f, 48f));
        }
        finally
        {
            Application.Current = previousApplication;
            ThemeManager.IsHighContrast = previousHighContrast;
        }
    }

    [Fact]
    public void GeneratedComboBoxTemplateActivatesFluentBindings()
    {
        var previousApplication = Application.Current;
        var previousTheme = ThemeManager.CurrentTheme;
        var previousHighContrast = ThemeManager.IsHighContrast;
        try
        {
            ThemeManager.CurrentTheme = ElementTheme.Light;
            ThemeManager.IsHighContrast = false;
            var application = new Application();
            Application.Current = application;
            var dictionary = FluentThemeResources.Apply(application);
            var customForeground = new SolidColorBrush(
                new System.Numerics.Vector4(0.25f, 0.5f, 0.75f, 1f));
            var comboBox = new ComboBox
            {
                IsEditable = true,
                PlaceholderText = "Choose a value",
                PlaceholderForeground = customForeground,
                Text = "Initial value",
                Style = Assert.IsType<Style>(dictionary[typeof(ComboBox)])
            };

            var replacedEditableText = Assert.IsType<TextBox>(
                comboBox.GetTemplateChild("EditableText"));
            Assert.True(comboBox.ApplyTemplate());
            var placeholder = Assert.IsType<TextBlock>(
                comboBox.GetTemplateChild("PlaceholderTextBlock"));
            var editableText = Assert.IsType<TextBox>(
                comboBox.GetTemplateChild("EditableText"));

            Assert.NotNull(BindingOperations.GetBindingExpression(
                placeholder,
                RichTextBlock.ForegroundProperty));
            Assert.NotNull(BindingOperations.GetBindingExpression(
                editableText,
                Control.ForegroundProperty));
            var textBinding = Assert.IsType<BindingExpression>(
                BindingOperations.GetBindingExpression(
                editableText,
                TextBox.TextProperty));
            Assert.Equal(BindingExpressionStatus.Active, textBinding.Status);
            Assert.Equal("Choose a value", placeholder.Text);
            Assert.Same(customForeground, placeholder.Foreground);
            Assert.Same(customForeground, editableText.Foreground);
            Assert.Equal("Initial value", editableText.Text);

            comboBox.Text = "Source update";
            Assert.Equal(BindingExpressionStatus.Active, textBinding.Status);
            Assert.Equal("Source update", editableText.Text);
            Assert.Equal("Initial value", replacedEditableText.Text);

            editableText.Text = "Target update";
            Assert.Equal("Target update", comboBox.Text);
        }
        finally
        {
            Application.Current = previousApplication;
            ThemeManager.CurrentTheme = previousTheme;
            ThemeManager.IsHighContrast = previousHighContrast;
        }
    }

    [Fact]
    public void ThemedNullableAnimationValueUsesSameConversionWhenReevaluated()
    {
        var previousTheme = ThemeManager.CurrentTheme;
        var previousHighContrast = ThemeManager.IsHighContrast;
        try
        {
            ThemeManager.CurrentTheme = ElementTheme.Light;
            ThemeManager.IsHighContrast = false;
            var dictionary = FluentThemeResources.CreateDictionary();
            var animation = new DoubleAnimation();

            animation.SetValue(
                DoubleAnimation.ToProperty,
                new ThemeResource(dictionary, "CheckBoxCheckedStrokeThickness"));

            Assert.Equal<double?>(0d, animation.To);

            ThemeManager.IsHighContrast = true;
            animation.NotifyThemeChanged();
            Assert.Equal<double?>(2d, animation.To);

            ThemeManager.IsHighContrast = false;
            animation.NotifyThemeChanged();
            Assert.Equal<double?>(0d, animation.To);
        }
        finally
        {
            ThemeManager.CurrentTheme = previousTheme;
            ThemeManager.IsHighContrast = previousHighContrast;
        }
    }

    [Fact]
    public void GeneratedFluentButtonAndCheckBoxExecuteInteractiveVisualStates()
    {
        var previousApplication = Application.Current;
        var previousTheme = ThemeManager.CurrentTheme;
        var previousHighContrast = ThemeManager.IsHighContrast;
        try
        {
            ThemeManager.CurrentTheme = ElementTheme.Light;
            ThemeManager.IsHighContrast = false;
            var application = new Application();
            Application.Current = application;
            var dictionary = FluentThemeResources.Apply(application);

            var button = new Button { Content = "Stateful Fluent button" };
            button.Style = Assert.IsType<Style>(dictionary[typeof(Button)]);
            var buttonPresenter = Assert.IsType<ContentPresenter>(
                button.GetTemplateChild("ContentPresenter"));
            var buttonStates = Assert.Single(
                VisualStateManager.GetVisualStateGroups(buttonPresenter),
                group => group.Name == "CommonStates");
            Assert.Equal("Normal", buttonStates.CurrentState?.Name);
            var normalBackground = buttonPresenter.Background;

            button.OnPointerEntered(new PointerRoutedEventArgs());
            Assert.Equal("PointerOver", buttonStates.CurrentState?.Name);
            Assert.True(dictionary.TryLookup(
                "ButtonBackgroundPointerOver",
                ElementTheme.Light,
                out var pointerOverBackground));
            Assert.Same(pointerOverBackground, buttonPresenter.Background);

            button.OnPointerPressed(new PointerRoutedEventArgs());
            Assert.Equal("Pressed", buttonStates.CurrentState?.Name);
            Assert.True(dictionary.TryLookup(
                "ButtonBackgroundPressed",
                ElementTheme.Light,
                out var pressedBackground));
            Assert.Same(pressedBackground, buttonPresenter.Background);

            button.OnPointerReleased(new PointerRoutedEventArgs());
            Assert.Equal("PointerOver", buttonStates.CurrentState?.Name);
            Assert.Same(pointerOverBackground, buttonPresenter.Background);
            button.OnPointerExited(new PointerRoutedEventArgs());
            Assert.Equal("Normal", buttonStates.CurrentState?.Name);
            Assert.Same(normalBackground, buttonPresenter.Background);

            button.IsEnabled = false;
            Assert.Equal("Disabled", buttonStates.CurrentState?.Name);
            button.IsEnabled = true;
            Assert.Equal("Normal", buttonStates.CurrentState?.Name);

            var checkBox = new CheckBox { Content = "Stateful Fluent check box" };
            checkBox.Style = Assert.IsType<Style>(dictionary[typeof(CheckBox)]);
            var checkBoxRoot = Assert.IsType<Grid>(
                checkBox.GetTemplateChild("RootGrid"));
            var checkGlyph = Assert.IsType<FontIcon>(
                checkBox.GetTemplateChild("CheckGlyph"));
            var checkBoxStates = Assert.Single(
                VisualStateManager.GetVisualStateGroups(checkBoxRoot),
                group => group.Name == "CombinedStates");

            Assert.Equal("UncheckedNormal", checkBoxStates.CurrentState?.Name);
            Assert.Equal(0d, checkGlyph.Opacity);

            checkBox.IsChecked = true;
            Assert.Equal("CheckedNormal", checkBoxStates.CurrentState?.Name);
            Assert.Equal(1d, checkGlyph.Opacity);

            checkBox.OnPointerEntered(new PointerRoutedEventArgs());
            Assert.Equal("CheckedPointerOver", checkBoxStates.CurrentState?.Name);
            checkBox.OnPointerPressed(new PointerRoutedEventArgs());
            Assert.Equal("CheckedPressed", checkBoxStates.CurrentState?.Name);
            checkBox.OnPointerReleased(new PointerRoutedEventArgs());
            Assert.False(checkBox.IsChecked);
            Assert.Equal("UncheckedPointerOver", checkBoxStates.CurrentState?.Name);

            checkBox.OnPointerExited(new PointerRoutedEventArgs());
            Assert.Equal("UncheckedNormal", checkBoxStates.CurrentState?.Name);
            Assert.Equal(0d, checkGlyph.Opacity);
            checkBox.IsEnabled = false;
            Assert.Equal("UncheckedDisabled", checkBoxStates.CurrentState?.Name);
        }
        finally
        {
            Application.Current = previousApplication;
            ThemeManager.CurrentTheme = previousTheme;
            ThemeManager.IsHighContrast = previousHighContrast;
        }
    }
}
