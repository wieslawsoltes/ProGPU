using System;
using System.IO;
using System.Numerics;
using Xunit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.WinUI.Designer;
using DataPackage = Microsoft.UI.Xaml.DataPackage;
using DragEventArgs = ProGPU.WinUI.Designer.DragEventArgs;
using StandardDataFormats = ProGPU.WinUI.Designer.StandardDataFormats;
using ProGPU.Vector;
using ProGPU.Layout;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public class DesignerCanvasTests
{
    [Fact]
    public void Test_DesignerCanvas_Drop_TypedRegistryInstantiation_And_GridSnap()
    {
        var canvas = new DesignerCanvas
        {
            Width = 800,
            Height = 600,
            AllowDrop = true
        };

        // Prepare data package holding the "Button" tool
        var data = new DataPackage();
        data.SetData(StandardDataFormats.Tool, "Button");

        // Prepare drop coordinates: e.g. at (53, 104) which should snap to (50, 100) on 10px grid
        var dropPos = new Vector2(53f, 104f);
        var args = new DragEventArgs(data, dropPos);

        // Act
        canvas.OnDrop(args);

        // Assert
        Assert.Single(canvas.DesignSurface.Children);
        var instantiatedControl = canvas.DesignSurface.Children[0] as Button;
        Assert.NotNull(instantiatedControl);
        
        // Assert grid snapping worked
        float left = Canvas.GetLeft(instantiatedControl);
        float top = Canvas.GetTop(instantiatedControl);
        Assert.Equal(50f, left);
        Assert.Equal(100f, top);

        // Assert that the instantiated control is selected
        Assert.Same(instantiatedControl, canvas.SelectedElement);
    }

    [Fact]
    public void Test_DesignerCanvas_Magnetic_Alignment_Snapping()
    {
        var canvas = new DesignerCanvas
        {
            Width = 800,
            Height = 600
        };

        // Add a static control A at (100, 100) with size (100, 50)
        var controlA = new Border();
        Canvas.SetLeft(controlA, 100f);
        Canvas.SetTop(controlA, 100f);
        controlA.Width = 100f;
        controlA.Height = 50f;
        canvas.DesignSurface.Children.Add(controlA);

        // Create control B which we will drag
        var controlB = new Border();
        Canvas.SetLeft(controlB, 300f);
        Canvas.SetTop(controlB, 300f);
        controlB.Width = 100f;
        controlB.Height = 50f;
        canvas.DesignSurface.Children.Add(controlB);

        // Set selected element
        canvas.SelectElement(controlB);

        // Drag B close to A vertical left alignment (e.g. drag B to x = 105f which is within 8px of A left=100f)
        var targetPos = new Vector2(105f, 300f);
        var snappedPos = canvas.SnapPosition(controlB, targetPos);

        // Assert B left snapped to exactamente A left (100f)
        Assert.Equal(100f, snappedPos.X);
        Assert.Equal(100f, canvas.ActiveVerticalSnapX);

        // Drag B close to A center alignment (e.g. A center is 150f, drag B center near it)
        // Control B width is 100, so my center is x + 50.
        // If my left is 102f, my center is 152f, which is within 8px of 150f.
        targetPos = new Vector2(102f, 300f);
        snappedPos = canvas.SnapPosition(controlB, targetPos);
        Assert.Equal(100f, snappedPos.X); // snaps my center to A center, so my left becomes 150 - 50 = 100f
    }

    [Fact]
    public void Test_SelectionAdorner_Handles_Layout()
    {
        var canvas = new DesignerCanvas
        {
            Width = 800,
            Height = 600
        };

        var button = new Button();
        Canvas.SetLeft(button, 150f);
        Canvas.SetTop(button, 120f);
        button.Width = 120f;
        button.Height = 40f;
        canvas.DesignSurface.Children.Add(button);

        // Act - Select button
        canvas.SelectElement(button);

        // Assert selection adorner was added to AdornerSurface
        Assert.Single(canvas.AdornerSurface.Children);
        var adorner = Assert.IsType<SelectionAdorner>(canvas.AdornerSurface.Children[0]);
        Assert.Same(button, adorner.AssociatedElement);

        // Assert 9 thumbs exist as children in SelectionAdorner (8 resize + 1 rotate)
        Assert.Equal(9, adorner.Children.Count);
        foreach (var child in adorner.Children)
        {
            Assert.IsType<Thumb>(child);
        }
    }

    [Fact]
    public void Test_DesignerHost_Measure_Arrange_Hang()
    {
        var designerHost = new DesignerHost
        {
            Height = 700f,
            DesignerFont = Microsoft.UI.Xaml.Controls.PopupService.DefaultFont,
            DesignerFontCourier = Microsoft.UI.Xaml.Controls.PopupService.DefaultFont,
            GetDpiScale = () => 1.0f
        };

        designerHost.InitializeFonts(Microsoft.UI.Xaml.Controls.PopupService.DefaultFont, Microsoft.UI.Xaml.Controls.PopupService.DefaultFont);

        designerHost.AddControlToCanvas("Button", 100f, 80f);
        designerHost.AddControlToCanvas("TextBox", 300f, 80f);
        designerHost.AddControlToCanvas("CheckBox", 100f, 160f);
        designerHost.AddControlToCanvas("Slider", 300f, 160f);

        designerHost.Measure(new Vector2(1024, 768));
        designerHost.Arrange(new ProGPU.Scene.Rect(0, 0, 1024, 768));
    }

    [Fact]
    public void Test_DesignerCanvas_InteractionMode_RestoresDesignedControlHitTesting()
    {
        var canvas = new DesignerCanvas();
        var textBox = new TextBox { Width = 120f, Height = 36f };
        canvas.AddChild(textBox);
        canvas.SelectElement(textBox);

        Assert.False(textBox.IsHitTestVisible);
        Assert.Single(canvas.AdornerSurface.Children);

        canvas.IsInteractionMode = true;

        Assert.True(textBox.IsHitTestVisible);
        Assert.Null(canvas.SelectedElement);
        Assert.False(canvas.AdornerSurface.IsVisible);
        Assert.False(canvas.AdornerSurface.IsHitTestVisible);

        canvas.IsInteractionMode = false;

        Assert.False(textBox.IsHitTestVisible);
        Assert.True(canvas.AdornerSurface.IsVisible);
        Assert.True(canvas.AdornerSurface.IsHitTestVisible);
    }

    [Fact]
    public void Test_DesignerHost_InteractionMode_RoutesTextAndButtonInput()
    {
        var font = PopupService.DefaultFont;
        var host = new DesignerHost();
        host.InitializeFonts(font, font);
        host.AddControlToCanvas("TextBox", 100f, 80f);
        host.AddControlToCanvas("Button", 300f, 80f);
        host.AddControlToCanvas("Slider", 500f, 80f);

        var textBox = Assert.IsType<TextBox>(host.WorkspaceCanvas.DesignSurface.Children[0]);
        var button = Assert.IsType<Button>(host.WorkspaceCanvas.DesignSurface.Children[1]);
        var slider = Assert.IsType<Slider>(host.WorkspaceCanvas.DesignSurface.Children[2]);
        int clickCount = 0;
        button.Click += (_, _) => clickCount++;

        host.Measure(new Vector2(1280f, 768f));
        host.Arrange(new ProGPU.Scene.Rect(0f, 0f, 1280f, 768f));
        host.SetInteractionMode(true);

        Assert.True(host.IsInteractionMode);
        Assert.True(textBox.IsHitTestVisible);
        Assert.True(button.IsHitTestVisible);
        Assert.True(slider.IsHitTestVisible);

        InputSystem.Current = InputSystem.CreateExternalState(host);
        try
        {
            Vector3 textOrigin = Vector3.Transform(Vector3.Zero, textBox.GetGlobalTransformMatrix());
            var textPoint = new Vector2(textOrigin.X + 12f, textOrigin.Y + 12f);
            InputSystem.InjectMouseMove(textPoint);
            InputSystem.InjectMouseDown(Silk.NET.Input.MouseButton.Left);
            InputSystem.InjectMouseUp(Silk.NET.Input.MouseButton.Left);

            Assert.Same(textBox, InputSystem.FocusedElement);
            InputSystem.InjectKeyChar('A');
            Assert.Equal("A", textBox.Text);

            Vector3 buttonOrigin = Vector3.Transform(Vector3.Zero, button.GetGlobalTransformMatrix());
            var buttonPoint = new Vector2(buttonOrigin.X + 20f, buttonOrigin.Y + 18f);
            InputSystem.InjectMouseMove(buttonPoint);
            InputSystem.InjectMouseDown(Silk.NET.Input.MouseButton.Left);
            InputSystem.InjectMouseUp(Silk.NET.Input.MouseButton.Left);

            Assert.Equal(1, clickCount);

            Vector3 sliderOrigin = Vector3.Transform(Vector3.Zero, slider.GetGlobalTransformMatrix());
            var sliderPoint = new Vector2(sliderOrigin.X + slider.Size.X * 0.75f, sliderOrigin.Y + 18f);
            InputSystem.InjectMouseMove(sliderPoint);
            InputSystem.InjectMouseDown(Silk.NET.Input.MouseButton.Left);
            InputSystem.InjectMouseUp(Silk.NET.Input.MouseButton.Left);

            Assert.InRange(slider.Value, 70f, 80f);
        }
        finally
        {
            InputSystem.SetFocus(null);
            InputSystem.Current = InputSystem.CreateExternalState();
        }

        host.SetInteractionMode(false);
        Assert.False(textBox.IsHitTestVisible);
        Assert.False(button.IsHitTestVisible);
        Assert.False(slider.IsHitTestVisible);
    }

    [Fact]
    public void Test_DesignerCanvas_SnapCache_Usage()
    {
        var canvas = new DesignerCanvas
        {
            Width = 800,
            Height = 600
        };

        var controlA = new Border();
        Canvas.SetLeft(controlA, 100f);
        Canvas.SetTop(controlA, 100f);
        controlA.Width = 100f;
        controlA.Height = 50f;
        canvas.DesignSurface.Children.Add(controlA);

        var controlB = new Border();
        Canvas.SetLeft(controlB, 300f);
        Canvas.SetTop(controlB, 300f);
        controlB.Width = 100f;
        controlB.Height = 50f;
        canvas.DesignSurface.Children.Add(controlB);

        // Initially no snap cache
        Assert.Null(canvas.CachedSnapElements);

        // Prepare snap cache for dragging controlB (excluding controlB itself)
        canvas.PrepareSnapCache(controlB);

        var cache = canvas.CachedSnapElements;
        Assert.NotNull(cache);
        Assert.Single(cache);
        Assert.Same(controlA, cache[0].Element);

        // Snap B close to A vertical left alignment (drag B to x = 105f near A left = 100f)
        var targetPos = new Vector2(105f, 300f);
        var snappedPos = canvas.SnapPosition(controlB, targetPos);

        Assert.Equal(100f, snappedPos.X);

        // Clear snap cache
        canvas.ClearSnapCache();
        Assert.Null(canvas.CachedSnapElements);
    }

    [Fact]
    public void Test_Toolbox_Search_Filtering()
    {
        var font = Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        var toolbox = new Toolbox(font);

        // Find the TextBox in the toolbox layout
        var grid = toolbox.Child as Grid;
        Assert.NotNull(grid);
        
        var headerPanel = grid.Children[0] as Microsoft.UI.Xaml.Controls.StackPanel;
        Assert.NotNull(headerPanel);
        
        var searchBox = headerPanel.Children[1] as TextBox;
        Assert.NotNull(searchBox);

        var scrollViewer = grid.Children[1] as ScrollViewer;
        Assert.NotNull(scrollViewer);
        
        var listPanel = scrollViewer.Content as Microsoft.UI.Xaml.Controls.StackPanel;
        Assert.NotNull(listPanel);

        // Check initially all 26 items are present
        Assert.Equal(26, listPanel.Children.Count);

        // Type "StackPanel" in search box
        searchBox.Text = "StackPanel";

        // Check that only 1 item matches and is present in listPanel
        Assert.Single(listPanel.Children);
        var matchItem = listPanel.Children[0] as ToolboxItem;
        Assert.NotNull(matchItem);
        Assert.Equal("StackPanel", matchItem.ControlName);

        // Clear search
        searchBox.Text = "";
        Assert.Equal(26, listPanel.Children.Count);
    }

    [Fact]
    public void Test_SelectionAdorner_Size_Stretched_In_Panel()
    {
        var canvas = new DesignerCanvas
        {
            Width = 800,
            Height = 600
        };

        // Create a parent StackPanel that stretches horizontally (default)
        var stackPanel = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Width = 400f,
            Height = 300f
        };
        Canvas.SetLeft(stackPanel, 50f);
        Canvas.SetTop(stackPanel, 50f);
        canvas.DesignSurface.Children.Add(stackPanel);

        // Create a Button that should stretch horizontally inside the StackPanel
        var button = new Button
        {
            Height = 40f
        };
        stackPanel.Children.Add(button);

        // Select the button
        canvas.SelectElement(button);

        // Measure and Arrange the canvas (simulating the layout pass)
        canvas.Measure(new Vector2(800f, 600f));
        canvas.Arrange(new ProGPU.Scene.Rect(0, 0, 800f, 600f));

        // Get the selection adorner
        Assert.Single(canvas.AdornerSurface.Children);
        var adorner = Assert.IsType<SelectionAdorner>(canvas.AdornerSurface.Children[0]);

        // The button size should be stretched to the StackPanel's width (400f)
        Assert.Equal(400f, button.Size.X);

        // The selection adorner's width should be updated to match the button's stretched width (400f)
        Assert.Equal(400f, adorner.Width);
    }

    [Fact]
    public void Test_DesignerHost_NudgeShortcuts()
    {
        var host = new DesignerHost();
        host.AddControlToCanvas("Button", 100f, 100f);
        var button = host.WorkspaceCanvas.SelectedElement;
        Assert.NotNull(button);

        // Act & Assert 1: Nudge Right by 1 unit
        host.OnKeyDown(new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.Right });
        Assert.Equal(101f, Canvas.GetLeft(button));

        // Act & Assert 2: Nudge Down with Shift by 10 units
        Microsoft.UI.Xaml.Input.InputSystem.Current.IsShiftPressed = true;
        host.OnKeyDown(new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.Down });
        Assert.Equal(110f, Canvas.GetTop(button));
        Microsoft.UI.Xaml.Input.InputSystem.Current.IsShiftPressed = false;
    }

    [Fact]
    public void Test_DesignerHost_Clipboard()
    {
        var host = new DesignerHost();
        host.AddControlToCanvas("Button", 100f, 100f);
        var button = host.WorkspaceCanvas.SelectedElement;
        Assert.NotNull(button);
        button.Width = 150f;

        // Copy with Ctrl pressed
        Microsoft.UI.Xaml.Input.InputSystem.Current.IsControlPressed = true;
        host.OnKeyDown(new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.C });

        // Paste
        host.OnKeyDown(new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.V });
        Microsoft.UI.Xaml.Input.InputSystem.Current.IsControlPressed = false;

        // Verify pasted duplicate is selected and has offset + width copied
        var pasted = host.WorkspaceCanvas.SelectedElement;
        Assert.NotNull(pasted);
        Assert.NotSame(button, pasted);
        Assert.Equal(120f, Canvas.GetLeft(pasted)); // 100 + 20
        Assert.Equal(120f, Canvas.GetTop(pasted));  // 100 + 20
        Assert.Equal(150f, pasted.Width);
    }

    [Fact]
    public void Test_DesignerHost_UndoRedo()
    {
        var host = new DesignerHost();
        host.AddControlToCanvas("Button", 100f, 100f);
        var button = host.WorkspaceCanvas.SelectedElement;
        Assert.NotNull(button);

        // Perform nudges (this will trigger SaveUndoState inside nudge method)
        host.OnKeyDown(new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.Right });
        Assert.Equal(101f, Canvas.GetLeft(button));

        // Undo with Ctrl pressed
        Microsoft.UI.Xaml.Input.InputSystem.Current.IsControlPressed = true;
        host.OnKeyDown(new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.Z });
        Microsoft.UI.Xaml.Input.InputSystem.Current.IsControlPressed = false;

        // Left coordinate should revert to 100f
        var currentButton = host.WorkspaceCanvas.DesignSurface.Children[0] as FrameworkElement;
        Assert.NotNull(currentButton);
        Assert.Equal(100f, Canvas.GetLeft(currentButton));

        // Redo with Ctrl pressed
        Microsoft.UI.Xaml.Input.InputSystem.Current.IsControlPressed = true;
        host.OnKeyDown(new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.Y });
        Microsoft.UI.Xaml.Input.InputSystem.Current.IsControlPressed = false;

        currentButton = host.WorkspaceCanvas.DesignSurface.Children[0] as FrameworkElement;
        Assert.NotNull(currentButton);
        Assert.Equal(101f, Canvas.GetLeft(currentButton));
    }

    [Fact]
    public void Test_DesignerCanvas_WebflowMode_LeafRejectionAndAutoStretch()
    {
        var canvas = new DesignerCanvas
        {
            Width = 800,
            Height = 600,
            AllowDrop = true,
            IsResponsiveMode = true
        };

        // 1. Try to drop a leaf control (Button) on root DesignSurface
        var dataButton = new DataPackage();
        dataButton.SetData(StandardDataFormats.Tool, "Button");
        var argsButton = new DragEventArgs(dataButton, new Vector2(100f, 100f));

        canvas.OnDrop(argsButton);

        // Assert it is rejected
        Assert.Empty(canvas.DesignSurface.Children);

        // 2. Drop a responsive container (StackPanel) on root DesignSurface
        var dataPanel = new DataPackage();
        dataPanel.SetData(StandardDataFormats.Tool, "StackPanel");
        var argsPanel = new DragEventArgs(dataPanel, new Vector2(100f, 100f));

        canvas.OnDrop(argsPanel);

        // Assert it is allowed and correctly configured
        Assert.Single(canvas.DesignSurface.Children);
        var panel = canvas.DesignSurface.Children[0] as Microsoft.UI.Xaml.Controls.StackPanel;
        Assert.NotNull(panel);
        Assert.Equal(HorizontalAlignment.Stretch, panel.HorizontalAlignment);
        Assert.True(float.IsNaN(panel.Width));
        Assert.Equal(100f, panel.Height); // placeholder height
    }

    [Fact]
    public void Test_DesignerHost_WebflowMode_NudgeMargin()
    {
        var host = new DesignerHost();
        host.WorkspaceCanvas.IsResponsiveMode = true;

        // Add responsive panel first (StackPanel)
        host.AddControlToCanvas("StackPanel", 0f, 0f);
        var panel = host.WorkspaceCanvas.SelectedElement;
        Assert.NotNull(panel);

        // Standard positions should be clean
        Assert.Equal(0f, Canvas.GetLeft(panel));
        Assert.Equal(0f, Canvas.GetTop(panel));

        // Act - Nudge Right by 1 unit
        host.OnKeyDown(new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.Right });

        // Assert - absolute coordinates are unmodified, but Margin is adjusted!
        Assert.Equal(0f, Canvas.GetLeft(panel));
        Assert.Equal(1f, panel.Margin.Left);
        Assert.Equal(0f, panel.Margin.Top);
    }

    [Fact]
    public void Test_DesignerSerializer_WebflowMode_CoordinatesSuppression()
    {
        var canvas = new Canvas { Width = 800f, Height = 600f };
        var panel = new Microsoft.UI.Xaml.Controls.StackPanel { Name = "myStack" };
        Canvas.SetLeft(panel, 150f);
        Canvas.SetTop(panel, 100f);
        canvas.Children.Add(panel);

        // Act - Serialize in responsive mode
        string csharpScript = DesignerSerializer.SerializeToCSharp(canvas, isResponsiveMode: true);

        // Assert - coordinate initializers are suppressed
        Assert.DoesNotContain("Canvas.SetLeft", csharpScript);
        Assert.DoesNotContain("Canvas.SetTop", csharpScript);
        Assert.Contains("var myStack = new StackPanel", csharpScript);
    }

    [Fact]
    public void Test_DesignerCanvas_WebflowViewport_MeasureAndCenter()
    {
        var canvas = new DesignerCanvas
        {
            ViewportWidth = 768f,
            AllowDrop = true
        };

        // Act - measure and arrange with 1024x768 available bounds
        canvas.Measure(new Vector2(1024f, 768f));
        canvas.Arrange(new ProGPU.Scene.Rect(0f, 0f, 1024f, 768f));

        // Assert - DesignSurface's measured size is restricted to 768f horizontally
        Assert.Equal(768f, canvas.DesignSurface.Size.X);

        // Assert - DesignSurface's arrangement left position is centered: (1024 - 768) / 2 = 128f
        Assert.Equal(128f, canvas.DesignSurface.Offset.X);
    }

    [Fact]
    public void Test_SelectionAdorner_BoxModelOverlays_VerifyRendering()
    {
        var canvas = new DesignerCanvas
        {
            Width = 800f,
            Height = 600f,
            IsResponsiveMode = true
        };

        var button = new Button
        {
            Width = 120f,
            Height = 36f,
            Margin = new Microsoft.UI.Xaml.Thickness(10f, 15f, 10f, 15f),
            Padding = new Microsoft.UI.Xaml.Thickness(5f, 5f, 5f, 5f)
        };
        canvas.DesignSurface.Children.Add(button);

        // Select to trigger SelectionAdorner instantiation
        canvas.SelectElement(button);

        // Act - Verify selection adorner captures sizes
        Assert.Single(canvas.AdornerSurface.Children);
        var adorner = Assert.IsType<SelectionAdorner>(canvas.AdornerSurface.Children[0]);
        var associatedElement = Assert.IsType<Button>(adorner.AssociatedElement);

        // Assert spacing properties are verified
        Assert.Equal(10f, associatedElement.Margin.Left);
        Assert.Equal(15f, associatedElement.Margin.Top);
        Assert.Equal(5f, associatedElement.Padding.Left);
        Assert.Equal(5f, associatedElement.Padding.Top);
    }

    [Fact]
    public void Test_PivotTabs_Navigation()
    {
        var pivot = new Pivot();
        var tab1 = new PivotItem("Style", new Border());
        var tab2 = new PivotItem("Settings", new Border());
        pivot.Items.Add(tab1);
        pivot.Items.Add(tab2);

        Assert.Equal(0, pivot.SelectedIndex);

        pivot.SelectedIndex = 1;
        Assert.Equal(1, pivot.SelectedIndex);
    }

    [Fact]
    public void Test_StylePanel_SpacingBoxModelUpdates()
    {
        var font = Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        var stylePanel = new StylePanel(font);

        var button = new Button
        {
            Width = 100f,
            Height = 30f,
            Margin = new Microsoft.UI.Xaml.Thickness(0),
            Padding = new Microsoft.UI.Xaml.Thickness(0)
        };

        stylePanel.SelectedElement = button;

        // Verify initial state
        Assert.Equal(0f, button.Margin.Left);
        Assert.Equal(0f, button.Padding.Top);

        var type = typeof(StylePanel);
        var txtMarginLeftField = type.GetField("_txtMarginLeft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var txtPaddingTopField = type.GetField("_txtPaddingTop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(txtMarginLeftField);
        Assert.NotNull(txtPaddingTopField);

        var txtMarginLeft = txtMarginLeftField.GetValue(stylePanel) as TextBox;
        var txtPaddingTop = txtPaddingTopField.GetValue(stylePanel) as TextBox;

        Assert.NotNull(txtMarginLeft);
        Assert.NotNull(txtPaddingTop);

        // Act - Simulate typing into boxes
        txtMarginLeft.Text = "20";
        txtPaddingTop.Text = "15";

        // Assert - Spacing updates were written back to the element!
        Assert.Equal(20f, button.Margin.Left);
        Assert.Equal(15f, button.Padding.Top);
    }

    [Fact]
    public void Test_StylePanel_TextBox_HitTesting_And_Focus()
    {
        var font = Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        var host = new DesignerHost();
        host.InitializeFonts(font, font);

        // Select the canvas button to make StylePanel fields active
        var button = new Button { Width = 100f, Height = 30f };
        host.WorkspaceCanvas.DesignSurface.Children.Add(button);
        host.WorkspaceCanvas.SelectElement(button);

        // Lay out host
        host.Measure(new Vector2(1024f, 768f));
        host.Arrange(new ProGPU.Scene.Rect(0f, 0f, 1024f, 768f));

        // Retrieve _stylePanel and _txtWidth field via reflection
        var type = typeof(StylePanel);
        var stylePanelField = typeof(DesignerHost).GetField("_stylePanel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(stylePanelField);
        var stylePanel = stylePanelField.GetValue(host) as StylePanel;
        Assert.NotNull(stylePanel);

        var txtWidthField = type.GetField("_txtWidth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(txtWidthField);
        var txtWidth = txtWidthField.GetValue(stylePanel) as TextBox;
        Assert.NotNull(txtWidth);

        // Verify the textbox is hit-test visible and enabled
        Assert.True(txtWidth.IsHitTestVisible);
        Assert.True(txtWidth.IsEnabled);

        // Get global transform position of txtWidth
        var transform = txtWidth.GetGlobalTransformMatrix();
        var globalPos = Vector3.Transform(Vector3.Zero, transform);
        var testPoint = new Vector2(globalPos.X + 10f, globalPos.Y + 10f);

        // Hit test
        InputSystem.Current.Root = host;
        var hit = InputSystem.HitTest(testPoint);
        Assert.NotNull(hit);

        // Verify the hit element bubbles up to txtWidth
        var current = hit;
        TextBox? targetTextBox = null;
        while (current != null)
        {
            if (current is TextBox tb)
            {
                targetTextBox = tb;
                break;
            }
            current = current.Parent as FrameworkElement;
        }
        Assert.Same(txtWidth, targetTextBox);

        // Simulate clicking
        var pointerArgs = new PointerRoutedEventArgs { Position = testPoint, ScreenPosition = testPoint };
        hit.OnPointerPressed(pointerArgs);

        // Verify focus is set to txtWidth
        Assert.Same(txtWidth, InputSystem.FocusedElement);
        Assert.True(txtWidth.IsFocused);

        // Simulate typing character '5'
        var charArgs = new CharacterReceivedRoutedEventArgs { Character = '5' };
        txtWidth.OnCharacterReceived(charArgs);

        // Assert text changed
        Assert.Equal("5100", txtWidth.Text); // Since button width is initialized to 100, typing '5' at index 0 makes it "5100"

        // Clean up static focus state to avoid affecting subsequent tests
        InputSystem.SetFocus(null);
    }

    [Fact]
    public void Test_StylePanel_Spacing_ValueScrubbing()
    {
        var font = Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        var stylePanel = new StylePanel(font);

        var button = new Button
        {
            Width = 100f,
            Height = 30f,
            Margin = new Microsoft.UI.Xaml.Thickness(0),
            Padding = new Microsoft.UI.Xaml.Thickness(0)
        };

        stylePanel.SelectedElement = button;

        var type = typeof(StylePanel);
        var txtMarginTopField = type.GetField("_txtMarginTop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(txtMarginTopField);
        var txtMarginTop = txtMarginTopField.GetValue(stylePanel) as TextBox;
        Assert.NotNull(txtMarginTop);

        // Verify initial state
        Assert.Equal("0", txtMarginTop.Text);
        Assert.Equal(0f, button.Margin.Top);

        // 1. Simulate drag scrubbing horizontally by 15 pixels (normal speed 1x)
        txtMarginTop.OnPointerPressed(new PointerRoutedEventArgs { Position = new Vector2(10f, 10f) });
        txtMarginTop.OnPointerMoved(new PointerRoutedEventArgs { Position = new Vector2(25f, 10f) }); // dx = 15f
        txtMarginTop.OnPointerReleased(new PointerRoutedEventArgs { Position = new Vector2(25f, 10f) });

        // Assert margin top updated to 15
        Assert.Equal("15", txtMarginTop.Text);
        Assert.Equal(15f, button.Margin.Top);

        // 2. Simulate drag scrubbing with SHIFT key pressed (10x speed acceleration)
        InputSystem.Current.IsShiftPressed = true;
        txtMarginTop.OnPointerPressed(new PointerRoutedEventArgs { Position = new Vector2(25f, 10f) });
        txtMarginTop.OnPointerMoved(new PointerRoutedEventArgs { Position = new Vector2(35f, 10f) }); // dx = 10f * 10x = 100
        txtMarginTop.OnPointerReleased(new PointerRoutedEventArgs { Position = new Vector2(35f, 10f) });
        InputSystem.Current.IsShiftPressed = false;

        // Assert margin top updated to 15 + 100 = 115
        Assert.Equal("115", txtMarginTop.Text);
        Assert.Equal(115f, button.Margin.Top);

        // 3. Simulate drag scrubbing with ALT key pressed (0.1x micro-tuning deceleration)
        InputSystem.Current.IsAltPressed = true;
        txtMarginTop.OnPointerPressed(new PointerRoutedEventArgs { Position = new Vector2(35f, 10f) });
        txtMarginTop.OnPointerMoved(new PointerRoutedEventArgs { Position = new Vector2(45f, 10f) }); // dx = 10f * 0.1x = 1
        txtMarginTop.OnPointerReleased(new PointerRoutedEventArgs { Position = new Vector2(45f, 10f) });
        InputSystem.Current.IsAltPressed = false;

        // Assert margin top updated to 115 + 1 = 116
        Assert.Equal("116", txtMarginTop.Text);
        Assert.Equal(116f, button.Margin.Top);
    }

    [Fact]
    public void Test_StylePanel_AllStyleInputs_ValueScrubbing()
    {
        var font = Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        var stylePanel = new StylePanel(font);

        var button = new Button
        {
            Width = 100f,
            Height = 30f,
            Opacity = 1f
        };

        stylePanel.SelectedElement = button;

        var type = typeof(StylePanel);
        var txtWidthField = type.GetField("_txtWidth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var txtOpacityField = type.GetField("_txtOpacity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(txtWidthField);
        Assert.NotNull(txtOpacityField);

        var txtWidth = txtWidthField.GetValue(stylePanel) as TextBox;
        var txtOpacity = txtOpacityField.GetValue(stylePanel) as TextBox;

        Assert.NotNull(txtWidth);
        Assert.NotNull(txtOpacity);

        // Verify initial state
        Assert.Equal("100", txtWidth.Text);
        Assert.Equal("100", txtOpacity.Text);

        // 1. Scrub Width horizontally by +50 pixels (normal speed 1x)
        txtWidth.OnPointerPressed(new PointerRoutedEventArgs { Position = new Vector2(10f, 10f) });
        txtWidth.OnPointerMoved(new PointerRoutedEventArgs { Position = new Vector2(60f, 10f) }); // dx = 50
        txtWidth.OnPointerReleased(new PointerRoutedEventArgs { Position = new Vector2(60f, 10f) });

        // Assert width updated to 150
        Assert.Equal("150", txtWidth.Text);
        Assert.Equal(150f, button.Width);

        // 2. Scrub Opacity horizontally by -30 pixels (normal speed 1x)
        txtOpacity.OnPointerPressed(new PointerRoutedEventArgs { Position = new Vector2(10f, 10f) });
        txtOpacity.OnPointerMoved(new PointerRoutedEventArgs { Position = new Vector2(-20f, 10f) }); // dx = -30
        txtOpacity.OnPointerReleased(new PointerRoutedEventArgs { Position = new Vector2(-20f, 10f) });

        // Assert opacity updated to 100 - 30 = 70%, and element Opacity is 0.7f
        Assert.Equal("70", txtOpacity.Text);
        Assert.Equal(0.7f, button.Opacity);
    }

    [Fact]
    public void Test_PropertyGrid_PropertyItem_Uses_DataGrid_ValueProvider()
    {
        bool changed = false;
        var item = new ProGPU.WinUI.Designer.PropertyItem("Width", "100", value => changed = value == "125");

        Assert.True(item.TryGetDataGridValue("Name", out var name));
        Assert.Equal("Width", name);
        Assert.True(item.TryGetDataGridValue("Value", out var value));
        Assert.Equal("100", value);
        Assert.Equal(typeof(string), item.GetDataGridValueType("Value"));

        Assert.True(item.TrySetDataGridValue("Value", "125"));
        Assert.True(changed);
        Assert.Equal("125", item.Value);
    }

    [Fact]
    public void Test_DesignerElementRegistry_TypedFactories_And_ContentHosts()
    {
        var font = Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;

        Assert.True(DesignerElementRegistry.TryCreate("Button", font, out var buttonFe));
        var button = Assert.IsType<Button>(buttonFe);
        Assert.IsType<RichTextBlock>(button.Content);
        Assert.False(DesignerElementRegistry.IsDropContainer(button));

        Assert.True(DesignerElementRegistry.TryCreate("StackPanel", font, out var panelFe));
        Assert.IsType<Microsoft.UI.Xaml.Controls.StackPanel>(panelFe);
        Assert.True(DesignerElementRegistry.IsDropContainer(panelFe));

        Assert.True(DesignerElementRegistry.TryCreateLike(button, out var clonedButton));
        Assert.IsType<Button>(clonedButton);

        var border = new Border();
        var textBlock = new TextBlock { Text = "Child" };
        Assert.True(DesignerElementRegistry.TryAddChild(border, textBlock));
        Assert.Same(textBlock, border.Child);
        Assert.True(DesignerElementRegistry.IsLogicalChild(border, textBlock));
        Assert.Collection(DesignerElementRegistry.GetLogicalChildren(border), child => Assert.Same(textBlock, child));
        Assert.True(DesignerElementRegistry.RemoveFromParent(textBlock));
        Assert.Null(border.Child);

        var splitView = new SplitView { Pane = new Border() };
        var splitContent = new TextBox();
        Assert.True(DesignerElementRegistry.TryAddChild(splitView, splitContent));
        Assert.Same(splitContent, splitView.Content);
        Assert.Collection(
            DesignerElementRegistry.GetLogicalChildren(splitView),
            child => Assert.Same(splitView.Pane, child),
            child => Assert.Same(splitContent, child));
    }

    [Fact]
    public void Test_Designer_PropertyEditors_Do_Not_Use_Clr_PropertyReflection()
    {
        string propertyGrid = File.ReadAllText(FindRepoFile("src/ProGPU.WinUI.Designer/PropertyGrid.cs"));
        string stylePanel = File.ReadAllText(FindRepoFile("src/ProGPU.WinUI.Designer/StylePanel.cs"));

        Assert.DoesNotContain("System.Reflection", propertyGrid);
        Assert.DoesNotContain("PropertyInfo", propertyGrid);
        Assert.DoesNotContain("BindingFlags", propertyGrid);
        Assert.DoesNotContain("GetProperty(", propertyGrid);

        Assert.DoesNotContain("System.Reflection", stylePanel);
        Assert.DoesNotContain("PropertyInfo", stylePanel);
        Assert.DoesNotContain("BindingFlags", stylePanel);
        Assert.DoesNotContain("GetProperty(", stylePanel);
    }

    [Fact]
    public void Test_Designer_Factory_Content_And_Serializer_Do_Not_Use_Clr_Reflection()
    {
        string[] files =
        [
            "src/ProGPU.WinUI.Designer/DesignerElementRegistry.cs",
            "src/ProGPU.WinUI.Designer/DesignerCanvas.cs",
            "src/ProGPU.WinUI.Designer/VisualTreeOutline.cs",
            "src/ProGPU.WinUI.Designer/DesignerHost.cs",
            "src/ProGPU.WinUI.Designer/Toolbox.cs",
            "src/ProGPU.WinUI.Designer/DesignerSerializer.cs"
        ];

        string[] forbiddenTokens =
        [
            "System.Reflection",
            "BindingFlags",
            "GetProperty(",
            "GetProperties(",
            "GetField(",
            "GetMethod(",
            "GetEvent(",
            "Activator.CreateInstance",
            "MethodInfo",
            "PropertyInfo",
            "FieldInfo",
            "GetCustomAttribute",
            "AppDomain.CurrentDomain"
        ];

        foreach (var file in files)
        {
            string source = File.ReadAllText(FindRepoFile(file));
            foreach (var token in forbiddenTokens)
            {
                Assert.DoesNotContain(token, source);
            }
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        DirectoryInfo? current = new(Directory.GetCurrentDirectory());
        while (current != null)
        {
            string candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {Directory.GetCurrentDirectory()}.");
    }
}
