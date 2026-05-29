using System;
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
    public void Test_DesignerCanvas_Drop_Reflection_Instantiation_And_GridSnap()
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
        var adorner = canvas.AdornerSurface.Children[0] as SelectionAdorner;
        Assert.NotNull(adorner);
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
        var adorner = canvas.AdornerSurface.Children[0] as SelectionAdorner;
        Assert.NotNull(adorner);

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
}
