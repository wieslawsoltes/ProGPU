using System;
using System.IO;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Tests.Headless;
using Xunit;
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace ProGPU.Tests;

public class ControlRenderTests
{
    private static HeadlessWindow SharedWindow
    {
        get
        {
            var window = HeadlessWindow.Shared;
            window.Resize(300, 150);
            return window;
        }
    }

    [Fact]
    public void NavigationViewItem_PageFactory_IsLazyAndCached()
    {
        var nav = new NavigationView();
        var firstCreated = 0;
        var secondCreated = 0;

        var firstItem = new NavigationViewItem("First", "", () =>
        {
            firstCreated++;
            return new Border();
        });
        var secondItem = new NavigationViewItem("Second", "", () =>
        {
            secondCreated++;
            return new Border();
        });

        nav.MenuItems.Add(firstItem);
        nav.MenuItems.Add(secondItem);

        Assert.Equal(0, firstCreated);
        Assert.Equal(0, secondCreated);
        Assert.Null(firstItem.Page);

        nav.SelectedItem = firstItem;
        var firstPage = firstItem.Page;

        Assert.Equal(1, firstCreated);
        Assert.Equal(0, secondCreated);
        Assert.Same(firstPage, nav.Content);

        nav.SelectedItem = secondItem;
        nav.SelectedItem = firstItem;

        Assert.Equal(1, firstCreated);
        Assert.Equal(1, secondCreated);
        Assert.Same(firstPage, nav.Content);
    }

    [Fact]
    public void NavigationViewPaneToggleInvalidatesRetainedItemChrome()
    {
        var nav = new NavigationView();
        var item = new NavigationViewItem("Page", "icon", new Border());
        nav.MenuItems.Add(item);
        long compactVersion = item.ChangeVersion;

        nav.IsPaneOpen = true;

        Assert.True(item.ChangeVersion > compactVersion);
    }

    private void VerifyControlStates<T>(T control, string namePrefix) where T : Control
    {
        PopupService.Clear();
        InputSystem.Current = new WindowInputState();
        var window = SharedWindow;
        window.Content = control;

        // 1. Normal State
        window.Render();
        byte[] normalPixels = window.ReadPixels();
        Assert.NotNull(normalPixels);
        string normalPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{namePrefix}_normal.png");
        window.SaveScreenshot(normalPath);
        Assert.True(File.Exists(normalPath));

        // 2. Hover State
        control.OnPointerEntered(new PointerRoutedEventArgs { Position = new Vector2(50f, 10f) });
        window.Render();
        byte[] hoverPixels = window.ReadPixels();
        Assert.NotNull(hoverPixels);
        string hoverPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{namePrefix}_hover.png");
        window.SaveScreenshot(hoverPath);
        Assert.True(File.Exists(hoverPath));
        control.OnPointerExited(new PointerRoutedEventArgs { Position = new Vector2(-10f, -10f) });

        // 3. Pressed State
        control.OnPointerPressed(new PointerRoutedEventArgs { Position = new Vector2(50f, 10f), IsLeftButtonPressed = true });
        window.Render();
        byte[] pressedPixels = window.ReadPixels();
        Assert.NotNull(pressedPixels);
        string pressedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{namePrefix}_pressed.png");
        window.SaveScreenshot(pressedPath);
        Assert.True(File.Exists(pressedPath));
        control.OnPointerReleased(new PointerRoutedEventArgs { Position = new Vector2(50f, 10f), IsLeftButtonPressed = false });

        // 4. Focus State
        InputSystem.SetFocus(control);
        window.Render();
        byte[] focusPixels = window.ReadPixels();
        Assert.NotNull(focusPixels);
        string focusPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{namePrefix}_focus.png");
        window.SaveScreenshot(focusPath);
        Assert.True(File.Exists(focusPath));
        InputSystem.SetFocus(null);

        // 5. Disabled State
        control.IsEnabled = false;
        window.Render();
        byte[] disabledPixels = window.ReadPixels();
        Assert.NotNull(disabledPixels);
        string disabledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{namePrefix}_disabled.png");
        window.SaveScreenshot(disabledPath);
        Assert.True(File.Exists(disabledPath));

        // Cleanup shared state
        control.IsEnabled = true;
        window.Content = null;
        PopupService.Clear();
        InputSystem.Current = new WindowInputState();
    }

    [Fact]
    public void Button_AllStates_RenderCorrectly()
    {
        var button = new Button
        {
            Width = 150f,
            Height = 50f,
            Content = new Border { Width = 60f, Height = 20f, Background = new SolidColorBrush(0xFF0000FF) },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        VerifyControlStates(button, "button");
    }

    [Fact]
    public void DesktopMouseInputChangesRenderedButtonVisualStates()
    {
        PopupService.Clear();
        var window = SharedWindow;
        var button = new Button
        {
            Width = 150f,
            Height = 50f,
            Content = "Action",
            Background = ThemeManager.GetBrush("ButtonBackground"),
            Foreground = ThemeManager.GetBrush("ButtonForeground"),
            BorderBrush = ThemeManager.GetBrush("ButtonBorderBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        window.Content = button;
        window.Render();
        byte[] normalPixels = window.ReadPixels();

        InputSystem.Current = InputSystem.CreateExternalState(button);
        var pointerPosition = button.Offset + new Vector2(10f, 10f);
        InputSystem.InjectPointer(new PointerInputEvent(
            PointerInputKind.Moved,
            1,
            Windows.Devices.Input.PointerDeviceType.Mouse,
            pointerPosition,
            1_000,
            IsPrimary: true));
        window.Render();
        byte[] hoverPixels = window.ReadPixels();

        InputSystem.InjectPointer(new PointerInputEvent(
            PointerInputKind.Pressed,
            1,
            Windows.Devices.Input.PointerDeviceType.Mouse,
            pointerPosition,
            2_000,
            IsPrimary: true,
            IsInContact: true,
            IsLeftButtonPressed: true,
            Pressure: 0.5f));
        window.Render();
        byte[] pressedPixels = window.ReadPixels();

        Assert.False(normalPixels.AsSpan().SequenceEqual(hoverPixels), "Hover pixels should differ from normal pixels.");
        Assert.False(hoverPixels.AsSpan().SequenceEqual(pressedPixels), "Pressed pixels should differ from hover pixels.");

        window.Content = null;
        PopupService.Clear();
        InputSystem.Current = new WindowInputState();
    }

    [Fact]
    public void Button_AccentStyle_RenderCorrectly()
    {
        var button = new Button
        {
            Width = 150f,
            Height = 50f,
            Content = new Border { Width = 60f, Height = 20f, Background = new SolidColorBrush(0xFF0000FF) },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var style = ThemeManager.GetResource("AccentButtonStyle") as Style;
        Assert.NotNull(style);
        button.Style = style;
        VerifyControlStates(button, "button_accent");
    }

    [Fact]
    public void StyleSettersApplyThroughDependencyProperties()
    {
        var button = new Button();
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(nameof(FrameworkElement.Width), 200f));
        style.Setters.Add(new Setter(nameof(FrameworkElement.Height), 44f));
        style.Setters.Add(new Setter(nameof(Control.CornerRadius), 10f));
        style.Setters.Add(new Setter(nameof(FrameworkElement.Padding), new Thickness(1f, 2f, 3f, 4f)));

        button.Style = style;

        Assert.Equal(200f, button.Width);
        Assert.Equal(44f, button.Height);
        Assert.Equal(10f, button.CornerRadius);
        Assert.Equal(new Thickness(1f, 2f, 3f, 4f), button.Padding);
    }

    [Fact]
    public void FrameworkElementStyleApplicationDoesNotUseClrPropertyReflection()
    {
        string source = File.ReadAllText(FindRepoFile("src", "ProGPU.WinUI", "Core", "FrameworkElement.cs"));
        int applyStyleIndex = source.IndexOf("private void ApplyStyle()", StringComparison.Ordinal);
        int marginPropertyIndex = source.IndexOf("public static readonly Microsoft.UI.Xaml.DependencyProperty MarginProperty", StringComparison.Ordinal);

        Assert.True(applyStyleIndex >= 0, "ApplyStyle was not found.");
        Assert.True(marginPropertyIndex > applyStyleIndex, "Could not isolate ApplyStyle body.");

        string applyStyleSource = source[applyStyleIndex..marginPropertyIndex];
        Assert.DoesNotContain("GetProperty(", applyStyleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetValue(this", applyStyleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", applyStyleSource, StringComparison.Ordinal);
        Assert.Contains("DependencyProperty.Lookup", applyStyleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreControlChromeDoesNotDependOnUnicodeFontGlyphs()
    {
        var controls = new[]
        {
            "ComboBox.cs",
            "TabViewItem.cs",
            "CalendarView.cs",
            "DatePicker.cs",
            "TreeView.cs",
            "DataGrid.cs"
        };

        foreach (string control in controls)
        {
            string source = File.ReadAllText(FindRepoFile("src", "ProGPU.WinUI", "Controls", control));
            Assert.DoesNotContain("DrawText(\"▼\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DrawText(\"▶\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DrawText(\"◀\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DrawText(\"×\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DrawText(\"📅\"", source, StringComparison.Ordinal);
        }

        string navigationSource = File.ReadAllText(FindRepoFile("src", "ProGPU.WinUI", "Controls", "NavigationViewItem.cs"));
        Assert.DoesNotContain("context.DrawText(Icon", navigationSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatButton_RaisesClickThroughTypedButtonHook()
    {
        var button = new RepeatButton
        {
            Width = 120f,
            Height = 32f,
            Delay = 10_000,
            Interval = 10_000
        };
        var clicks = 0;
        button.Click += (_, _) => clicks++;

        button.OnPointerEntered(new PointerRoutedEventArgs { Position = new Vector2(10f, 10f) });
        button.OnPointerPressed(new PointerRoutedEventArgs { Position = new Vector2(10f, 10f), IsLeftButtonPressed = true });
        button.OnPointerReleased(new PointerRoutedEventArgs { Position = new Vector2(10f, 10f), IsLeftButtonPressed = false });

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Slider_AllStates_RenderCorrectly()
    {
        var slider = new Slider
        {
            Width = 200f,
            Height = 32f,
            Minimum = 0f,
            Maximum = 100f,
            Value = 40f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        VerifyControlStates(slider, "slider");
    }

    [Fact]
    public void ToggleSwitch_AllStates_RenderCorrectly()
    {
        var toggle = new ToggleSwitch
        {
            Width = 150f,
            Height = 40f,
            IsOn = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        VerifyControlStates(toggle, "toggle");
    }

    [Fact]
    public void CheckBox_AllStates_RenderCorrectly()
    {
        var checkbox = new CheckBox
        {
            Width = 150f,
            Height = 32f,
            IsChecked = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        VerifyControlStates(checkbox, "checkbox");
    }

    [Fact]
    public void TextBox_AllStates_RenderCorrectly()
    {
        var textbox = new TextBox
        {
            Width = 200f,
            Height = 36f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        VerifyControlStates(textbox, "textbox");
    }

    [Fact]
    public void ComboBox_AllStates_RenderCorrectly()
    {
        var combo = new ComboBox
        {
            Width = 200f,
            Height = 36f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        combo.Items.Add(new ComboBoxItem("Item 1"));
        combo.Items.Add(new ComboBoxItem("Item 2"));
        VerifyControlStates(combo, "combo");
    }

    [Fact]
    public void ComboBox_PointerSelection_WorksCorrectly()
    {
        var window = SharedWindow;
        var combo = new ComboBox
        {
            Width = 200f,
            Height = 32f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var item1 = new ComboBoxItem("Item 1");
        var item2 = new ComboBoxItem("Item 2");
        combo.Items.Add(item1);
        combo.Items.Add(item2);

        window.Content = combo;
        window.Render(); // Measure & Arrange

        // 1. Initially nothing is selected
        Assert.Null(combo.SelectedItem);

        // 2. Open dropdown
        combo.IsDropDownOpen = true;
        Assert.True(combo.IsDropDownOpen);
        Assert.NotNull(combo.DropDownPopup);

        // 3. Simulate pointer entering and pressing on item2
        var pointerEnteredArgs = new PointerRoutedEventArgs { Position = new Vector2(10f, 10f) };
        item2.OnPointerEntered(pointerEnteredArgs);

        var pointerPressedArgs = new PointerRoutedEventArgs { Position = new Vector2(10f, 10f), IsLeftButtonPressed = true };
        item2.OnPointerPressed(pointerPressedArgs);

        // Dropdown should still be open (the bug was that it collapsed immediately on focus lost)
        Assert.True(combo.IsDropDownOpen);

        // 4. Simulate pointer release on item2
        var pointerReleasedArgs = new PointerRoutedEventArgs { Position = new Vector2(10f, 10f), IsLeftButtonPressed = false };
        item2.OnPointerReleased(pointerReleasedArgs);

        // Dropdown should now be closed and item2 selected!
        Assert.False(combo.IsDropDownOpen);
        Assert.Equal(item2, combo.SelectedItem);
        Assert.True(item2.IsSelected);
        Assert.False(item1.IsSelected);

        // Cleanup
        window.Content = null;
    }

    [Fact]
    public void ComboBox_InsideScrollViewer_DropdownStaysOpen()
    {
        var window = SharedWindow;
        var scroll = new ScrollViewer
        {
            Width = 300f,
            Height = 150f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var combo = new ComboBox
        {
            Width = 200f,
            Height = 32f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        combo.Items.Add(new ComboBoxItem("Item 1"));
        combo.Items.Add(new ComboBoxItem("Item 2"));

        scroll.Content = combo;
        window.Content = scroll;
        window.Render(); // Measure & Arrange

        // 1. Dropdown initially closed
        Assert.False(combo.IsDropDownOpen);

        // 2. Simulate pointer press on ComboBox header inside ScrollViewer
        var pointerPressedArgs = new PointerRoutedEventArgs { Position = new Vector2(10f, 10f), IsLeftButtonPressed = true };
        combo.OnPointerPressed(pointerPressedArgs);

        // 3. Dropdown should open and stay open
        Assert.True(combo.IsDropDownOpen);
        Assert.True(combo.IsFocused);
        Assert.False(scroll.IsFocused); // ScrollViewer should not have stolen focus

        // Cleanup
        window.Content = null;
    }

    [Fact]
    public void ComboBox_DropdownScrollViewer_ScrollsWithoutDismissal()
    {
        var window = SharedWindow;
        var combo = new ComboBox
        {
            Width = 200f,
            Height = 32f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        combo.Items.Add(new ComboBoxItem("Item 1"));
        combo.Items.Add(new ComboBoxItem("Item 2"));
        combo.Items.Add(new ComboBoxItem("Item 3"));
        combo.Items.Add(new ComboBoxItem("Item 4"));
        combo.Items.Add(new ComboBoxItem("Item 5"));

        window.Content = combo;
        window.Render();

        // 1. Open dropdown
        combo.IsDropDownOpen = true;
        Assert.True(combo.IsDropDownOpen);
        Assert.NotNull(combo.DropDownPopup);

        // Get inner ScrollViewer
        var border = combo.DropDownPopup;
        var scrollViewer = border.Child as ScrollViewer;
        Assert.NotNull(scrollViewer);

        // 2. Change scroll offset of the popup's inner ScrollViewer
        scrollViewer.VerticalOffset = 10f;

        // 3. Dropdown must still be open!
        Assert.True(combo.IsDropDownOpen);

        // Cleanup
        window.Content = null;
    }

    [Fact]
    public void ProgressBar_AllStates_RenderCorrectly()
    {
        var progress = new ProgressBar
        {
            Width = 200f,
            Height = 10f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        VerifyControlStates(progress, "progress");
    }

    [Fact]
    public void ScrollViewer_AllStates_RenderCorrectly()
    {
        var scroll = new ScrollViewer
        {
            Width = 150f,
            Height = 150f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        scroll.Content = new Border { Width = 300f, Height = 300f, Background = new SolidColorBrush(0x00FF00FF) };
        VerifyControlStates(scroll, "scroll");
    }

    [Fact]
    public void AttachedProperties_WorkCorrectly()
    {
        // 1. Register a test attached property
        var testProperty = DependencyProperty.RegisterAttached(
            "TestAttachedValue",
            typeof(string),
            typeof(ControlRenderTests),
            new PropertyMetadata("Default"));

        Assert.True(testProperty.IsAttached);
        Assert.Equal("Default", testProperty.Metadata?.DefaultValue);

        // 2. Set/Get on a DependencyObject
        var border = new Border();
        Assert.Equal("Default", border.GetValue(testProperty));

        bool callbackCalled = false;
        object? oldVal = null;
        object? newVal = null;

        var testPropertyWithCallback = DependencyProperty.RegisterAttached(
            "TestAttachedValueWithCallback",
            typeof(int),
            typeof(ControlRenderTests),
            new PropertyMetadata(0, (d, e) => {
                callbackCalled = true;
                oldVal = e.OldValue;
                newVal = e.NewValue;
            }));

        border.SetValue(testPropertyWithCallback, 42);
        Assert.Equal(42, border.GetValue(testPropertyWithCallback));
        Assert.True(callbackCalled);
        Assert.Equal(0, oldVal);
        Assert.Equal(42, newVal);

        // 3. Grid Row/Column Attached Properties
        var child = new Border();
        Grid.SetRow(child, 2);
        Grid.SetColumn(child, 3);
        Assert.Equal(2, Grid.GetRow(child));
        Assert.Equal(3, Grid.GetColumn(child));

        // 4. Canvas Left/Top Attached Properties
        Canvas.SetLeft(child, 10.5f);
        Canvas.SetTop(child, 20.5f);
        Assert.Equal(10.5f, Canvas.GetLeft(child));
        Assert.Equal(20.5f, Canvas.GetTop(child));

        // 5. Fallback for non-DependencyObject Visual elements (backward compatibility)
        var rawVisual = new ProGPU.Scene.Visual();
        Grid.SetRow(rawVisual, 5);
        Grid.SetColumn(rawVisual, 6);
        Assert.Equal(5, Grid.GetRow(rawVisual));
        Assert.Equal(6, Grid.GetColumn(rawVisual));

        Canvas.SetLeft(rawVisual, 100.5f);
        Canvas.SetTop(rawVisual, 200.5f);
        Assert.Equal(100.5f, Canvas.GetLeft(rawVisual));
        Assert.Equal(200.5f, Canvas.GetTop(rawVisual));
    }

    [Fact]
    public void Test_HitTesting_Diagnostics()
    {
        PopupService.Clear();
        InputSystem.Current = new WindowInputState();
        var root = new Border { Width = 800f, Height = 600f };
        var canvas = new Canvas
        {
            Width = 600f,
            Height = 400f,
            Margin = new Thickness(100f, 100f, 0f, 0f),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        root.Child = canvas;
        
        var button = new Button { Width = 100f, Height = 50f };
        Canvas.SetLeft(button, 50f);
        Canvas.SetTop(button, 50f);
        canvas.Children.Add(button);
        
        // Measure and arrange
        root.Measure(new Vector2(800f, 600f));
        root.Arrange(new ProGPU.Scene.Rect(0f, 0f, 800f, 600f));
        
        // Simulate hit test
        InputSystem.Current.Root = root;
        var hit = InputSystem.HitTest(new Vector2(175f, 175f)); // inside button: 100 (canvas margin left) + 50 (button canvas left) + 25 = 175
        
        Assert.NotNull(hit);
        var target = hit;
        while (target != null && target != button)
        {
            target = target.Parent as FrameworkElement;
        }
        Assert.Equal(button, target);
    }

    [Fact]
    public void Test_PivotItem_TextBox_HitTesting()
    {
        PopupService.Clear();
        InputSystem.Current = new WindowInputState();
        var root = new Border { Width = 800f, Height = 600f };
        
        var pivot = new Pivot
        {
            Margin = new Thickness(20f, 4f, 20f, 20f)
        };
        root.Child = pivot;

        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new GridLength(280, GridUnitType.Absolute));
        mainGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));

        var sidebarBorder = new Border
        {
            Padding = new Thickness(16f),
            Margin = new Thickness(0f, 0f, 16f, 0f),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var sidebarStack = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Vertical };
        
        var ptLabel = new TextBlock { Text = "Point Count", FontSize = 10f, Margin = new Thickness(0f, 0f, 0f, 4f) };
        var pointCountInput = new TextBox { Text = "1000000", Width = 230f, Margin = new Thickness(0f, 0f, 0f, 12f) };
        
        sidebarStack.AddChild(ptLabel);
        sidebarStack.AddChild(pointCountInput);
        sidebarBorder.Child = sidebarStack;
        mainGrid.AddChild(sidebarBorder);
        Grid.SetColumn(sidebarBorder, 0);

        for (int i = 0; i < 15; i++)
        {
            pivot.Items.Add(new PivotItem($"Item {i}", new Border()));
        }
        var pivotItem = new PivotItem("Ultimate Benchmark", mainGrid);
        pivot.Items.Add(pivotItem);
        for (int i = 16; i < 18; i++)
        {
            pivot.Items.Add(new PivotItem($"Item {i}", new Border()));
        }
        pivot.SelectedIndex = 15;

        // Measure and arrange
        root.Measure(new Vector2(800f, 600f));
        root.Arrange(new ProGPU.Scene.Rect(0f, 0f, 800f, 600f));

        // Get global bounds/offset of pointCountInput to find where to click
        var transform = pointCountInput.TransformToVisual(null);
        var inputCenter = transform.TransformPoint(new Vector2(115f, 16f)); // Center of TextBox (230x32)

        // Simulate hit test at center of TextBox
        InputSystem.Current.Root = root;
        var hit = InputSystem.HitTest(inputCenter);

        Assert.NotNull(hit);
        var target = hit;
        while (target != null && target != pointCountInput)
        {
            target = target.Parent as FrameworkElement;
        }
        Assert.Equal(pointCountInput, target);
    }

    private FrameworkElement? FindElementByText(FrameworkElement root, string text)
    {
        if (root is TextBox tb && tb.Text == text) return tb;
        foreach (var child in root.Children)
        {
            if (child is FrameworkElement fe)
            {
                var found = FindElementByText(fe, text);
                if (found != null) return found;
            }
        }
        return null;
    }

    private Pivot? FindPivot(FrameworkElement root)
    {
        if (root is Pivot p) return p;
        foreach (var child in root.Children)
        {
            if (child is FrameworkElement fe)
            {
                var found = FindPivot(fe);
                if (found != null) return found;
            }
        }
        return null;
    }

    [Fact]
    public void Test_RealShowcasePage_TextBox_HitTesting()
    {
        PopupService.Clear();
        InputSystem.Current = new WindowInputState();
        // 1. Create the actual showcase page layout
        var page = ProGPU.Samples.ChartShowcasePage.Create();
        var root = new Border { Width = 1280f, Height = 800f, Child = page };

        // 2. Locate the Pivot control and set selected index to 15 ("Ultimate Benchmark")
        var pivot = FindPivot(page);
        Assert.NotNull(pivot);
        
        // Find "Ultimate Benchmark" index
        int targetIdx = -1;
        for (int i = 0; i < pivot.Items.Count; i++)
        {
            if (pivot.Items[i].Header?.ToString() == "Ultimate Benchmark")
            {
                targetIdx = i;
                break;
            }
        }
        Assert.True(targetIdx >= 0);
        
        // Force selection and direct transition completion (DispatcherQueue is null in tests)
        pivot.SelectedIndex = targetIdx;

        // 3. Measure and arrange the whole window visual tree
        root.Measure(new Vector2(1280f, 800f));
        root.Arrange(new ProGPU.Scene.Rect(0f, 0f, 1280f, 800f));

        // 4. Find the actual Point Count textbox
        var pointCountInput = FindElementByText(page, "1000000");
        Assert.NotNull(pointCountInput);

        // 5. Check coordinates of pointCountInput
        var transform = pointCountInput.TransformToVisual(null);
        var inputCenter = transform.TransformPoint(new Vector2(115f, 16f)); // Center of TextBox (230x32)

        // 6. Hit-test at the center of the TextBox
        InputSystem.Current.Root = root;
        var hit = InputSystem.HitTest(inputCenter);

        Assert.NotNull(hit);
        var target = hit;
        while (target != null && target != pointCountInput)
        {
            target = target.Parent as FrameworkElement;
        }
        Assert.Equal(pointCountInput, target);
    }

    public struct TestEdge : IEquatable<TestEdge>
    {
        public uint U;
        public uint V;

        public TestEdge(uint u, uint v)
        {
            if (u < v) { U = u; V = v; }
            else { U = v; V = u; }
        }

        public bool Equals(TestEdge other) => U == other.U && V == other.V;
        public override bool Equals(object? obj) => obj is TestEdge other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                uint hash = 17;
                hash = hash * 31 + U;
                hash = hash * 31 + V;
                return (int)hash;
            }
        }
    }

    [Fact]
    public void Test_EdgeDeduplication()
    {
        // Define a simple quad with two triangles sharing an edge (1, 2)
        // Triangle 1: (0, 1, 2)
        // Triangle 2: (2, 1, 3)
        uint[] indices = new uint[] { 0, 1, 2, 2, 1, 3 };

        var uniqueEdges = new HashSet<TestEdge>();
        for (int t = 0; t < indices.Length; t += 3)
        {
            uint i0 = indices[t];
            uint i1 = indices[t + 1];
            uint i2 = indices[t + 2];

            uniqueEdges.Add(new TestEdge(i0, i1));
            uniqueEdges.Add(new TestEdge(i1, i2));
            uniqueEdges.Add(new TestEdge(i2, i0));
        }

        // Expected edges:
        // (0, 1), (1, 2), (2, 0)
        // (2, 1) -> duplicate of (1, 2)
        // (1, 3), (3, 2)
        // Total expected: 5 unique edges: (0,1), (1,2), (0,2), (1,3), (2,3)
        Assert.Equal(5, uniqueEdges.Count);

        Assert.Contains(new TestEdge(0, 1), uniqueEdges);
        Assert.Contains(new TestEdge(1, 2), uniqueEdges);
        Assert.Contains(new TestEdge(0, 2), uniqueEdges);
        Assert.Contains(new TestEdge(1, 3), uniqueEdges);
        Assert.Contains(new TestEdge(2, 3), uniqueEdges);
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(pathParts)}.");
    }
}
