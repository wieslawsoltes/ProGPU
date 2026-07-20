using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Media3D;
using ProGPU.Vector;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using System.IO;

namespace ProGPU.Samples;

public static class MeshBuilder
{
    public static MeshGeometry3D CreateCube()
    {
        var positions = new Vector3[]
        {
            // Front Face
            new Vector3(-1f, -1f,  1f), new Vector3( 1f, -1f,  1f), new Vector3( 1f,  1f,  1f), new Vector3(-1f,  1f,  1f),
            // Back Face
            new Vector3(-1f, -1f, -1f), new Vector3(-1f,  1f, -1f), new Vector3( 1f,  1f, -1f), new Vector3( 1f, -1f, -1f),
            // Top Face
            new Vector3(-1f,  1f, -1f), new Vector3(-1f,  1f,  1f), new Vector3( 1f,  1f,  1f), new Vector3( 1f,  1f, -1f),
            // Bottom Face
            new Vector3(-1f, -1f, -1f), new Vector3( 1f, -1f, -1f), new Vector3( 1f, -1f,  1f), new Vector3(-1f, -1f,  1f),
            // Right Face
            new Vector3( 1f, -1f, -1f), new Vector3( 1f,  1f, -1f), new Vector3( 1f,  1f,  1f), new Vector3( 1f, -1f,  1f),
            // Left Face
            new Vector3(-1f, -1f, -1f), new Vector3(-1f, -1f,  1f), new Vector3(-1f,  1f,  1f), new Vector3(-1f,  1f, -1f)
        };

        var normals = new Vector3[]
        {
            new Vector3(0, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 1),
            new Vector3(0, 0, -1), new Vector3(0, 0, -1), new Vector3(0, 0, -1), new Vector3(0, 0, -1),
            new Vector3(0, 1, 0), new Vector3(0, 1, 0), new Vector3(0, 1, 0), new Vector3(0, 1, 0),
            new Vector3(0, -1, 0), new Vector3(0, -1, 0), new Vector3(0, -1, 0), new Vector3(0, -1, 0),
            new Vector3(1, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 0),
            new Vector3(-1, 0, 0), new Vector3(-1, 0, 0), new Vector3(-1, 0, 0), new Vector3(-1, 0, 0)
        };

        var indices = new int[]
        {
            0, 1, 2, 0, 2, 3,       // Front
            4, 5, 6, 4, 6, 7,       // Back
            8, 9, 10, 8, 10, 11,    // Top
            12, 13, 14, 12, 14, 15, // Bottom
            16, 17, 18, 16, 18, 19, // Right
            20, 21, 22, 20, 22, 23  // Left
        };

        return new MeshGeometry3D { Positions = positions, Normals = normals, TriangleIndices = indices };
    }

    public static MeshGeometry3D CreateSphere(float radius, int slices, int stacks)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<int>();

        for (int stack = 0; stack <= stacks; stack++)
        {
            float phi = MathF.PI * (float)stack / stacks;
            float sinPhi = MathF.Sin(phi);
            float cosPhi = MathF.Cos(phi);

            for (int slice = 0; slice <= slices; slice++)
            {
                float theta = 2f * MathF.PI * (float)slice / slices;
                float sinTheta = MathF.Sin(theta);
                float cosTheta = MathF.Cos(theta);

                var normal = new Vector3(cosTheta * sinPhi, cosPhi, sinTheta * sinPhi);
                var pos = normal * radius;

                positions.Add(pos);
                normals.Add(normal);
            }
        }

        for (int stack = 0; stack < stacks; stack++)
        {
            for (int slice = 0; slice < slices; slice++)
            {
                int r0 = stack * (slices + 1) + slice;
                int r1 = r0 + 1;
                int r2 = (stack + 1) * (slices + 1) + slice;
                int r3 = r2 + 1;

                indices.Add(r0);
                indices.Add(r1);
                indices.Add(r2);

                indices.Add(r1);
                indices.Add(r3);
                indices.Add(r2);
            }
        }

        return new MeshGeometry3D { Positions = positions.ToArray(), Normals = normals.ToArray(), TriangleIndices = indices.ToArray() };
    }

    public static MeshGeometry3D CreateTorus(float majorRadius, float minorRadius, int majorSegments, int minorSegments)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<int>();

        for (int i = 0; i <= majorSegments; i++)
        {
            float u = (float)i / majorSegments * 2f * MathF.PI;
            float cosU = MathF.Cos(u);
            float sinU = MathF.Sin(u);

            for (int j = 0; j <= minorSegments; j++)
            {
                float v = (float)j / minorSegments * 2f * MathF.PI;
                float cosV = MathF.Cos(v);
                float sinV = MathF.Sin(v);

                var pos = new Vector3(
                    (majorRadius + minorRadius * cosV) * cosU,
                    minorRadius * sinV,
                    (majorRadius + minorRadius * cosV) * sinU
                );

                var center = new Vector3(majorRadius * cosU, 0f, majorRadius * sinU);
                var normal = Vector3.Normalize(pos - center);

                positions.Add(pos);
                normals.Add(normal);
            }
        }

        for (int i = 0; i < majorSegments; i++)
        {
            for (int j = 0; j < minorSegments; j++)
            {
                int r0 = i * (minorSegments + 1) + j;
                int r1 = r0 + 1;
                int r2 = (i + 1) * (minorSegments + 1) + j;
                int r3 = r2 + 1;

                indices.Add(r0);
                indices.Add(r1);
                indices.Add(r2);

                indices.Add(r1);
                indices.Add(r3);
                indices.Add(r2);
            }
        }

        return new MeshGeometry3D { Positions = positions.ToArray(), Normals = normals.ToArray(), TriangleIndices = indices.ToArray() };
    }

    public static MeshGeometry3D CreateTerrain(float size, int gridSegments, float heightMultiplier)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        float halfSize = size * 0.5f;
        float segmentSize = size / gridSegments;

        for (int z = 0; z <= gridSegments; z++)
        {
            float zPos = -halfSize + z * segmentSize;
            for (int x = 0; x <= gridSegments; x++)
            {
                float xPos = -halfSize + x * segmentSize;

                // Procedural sinusoidal terrain waves
                float d = MathF.Sqrt(xPos * xPos + zPos * zPos);
                float yPos = MathF.Sin(d * 1.5f) * heightMultiplier / (1.0f + d * 0.2f);

                positions.Add(new Vector3(xPos, yPos, zPos));
            }
        }

        for (int z = 0; z < gridSegments; z++)
        {
            for (int x = 0; x < gridSegments; x++)
            {
                int r0 = z * (gridSegments + 1) + x;
                int r1 = r0 + 1;
                int r2 = (z + 1) * (gridSegments + 1) + x;
                int r3 = r2 + 1;

                indices.Add(r0);
                indices.Add(r2);
                indices.Add(r1);

                indices.Add(r1);
                indices.Add(r2);
                indices.Add(r3);
            }
        }

        var mesh = new MeshGeometry3D { Positions = positions.ToArray(), TriangleIndices = indices.ToArray() };
        // Force calculation of correct face normals on procedural terrain
        mesh.Normals = mesh.GetNormalsOrCompute();
        return mesh;
    }
}

public enum LayoutMode3D
{
    Quad,
    Top,
    Front,
    Right,
    Perspective
}

public class Mesh3DViewerPageGrid : ResponsiveSplitView, IAnimatedElement
{
    public Mesh3DViewerPageGrid()
    {
        OpenPaneLength = 300f;
        CompactModeThreshold = 720f;
    }

    public void Update(float delta)
    {
        Mesh3DViewerPage.UpdateAnimations((float)delta);
    }

}

public static class Mesh3DViewerPage
{
    private static Viewport3D? _viewport1;
    private static Viewport3D? _viewport2;
    private static Viewport3D? _viewport3;
    private static Viewport3D? _viewport4;
    
    private static ModelVisual3D? _model1;
    private static ModelVisual3D? _model2;
    private static ModelVisual3D? _model3;
    private static ModelVisual3D? _model4;

    private static Border? _card1;
    private static Border? _card2;
    private static Border? _card3;
    private static Border? _card4;
    private static Grid? _viewportsGrid;
    private static Grid? _leftGrid;
    private static Grid? _rightGrid;
    private static GridSplitter? _colSplitter;
    private static GridSplitter? _rowSplitterLeft;
    private static GridSplitter? _rowSplitterRight;

    private static Button? _layoutBtnQuad;
    private static Button? _layoutBtnTop;
    private static Button? _layoutBtnFront;
    private static Button? _layoutBtnRight;
    private static Button? _layoutBtnPersp;

    private static Button? _maxBtn1;
    private static Button? _maxBtn2;
    private static Button? _maxBtn3;
    private static Button? _maxBtn4;

    private static LayoutMode3D _currentLayoutMode = LayoutMode3D.Quad;

    private static float _rotationAngle = 0f;
    private static bool _animateRotation = false;
    
    private static Vector3 _lightDirection = new Vector3(0.5f, 1f, -0.5f);
    private static float _lightIntensity = 1.0f;
    private static float _ambientIntensity = 0.25f;

    private static string _selectedMeshType = "Torus";
    private static MeshGeometry3D? _activeMeshGeometry;
    private static LoadedObjModel? _activeLoadedModel;
    private static MeshGeometry3D? _lastMeshGeometry;

    private static Vector4 _activeModelColor = new Vector4(0.70f, 0.70f, 0.72f, 1.0f); // Default premium clay warm gray
    private static Border? _pickerPopup;
    private static RenderMode3D _selectedRenderMode = RenderMode3D.SolidWireframe;
    private static ShadingMode3D _selectedShadingMode = ShadingMode3D.Realistic;

    private static Run? _statsNameRun;
    private static Run? _statsVerticesRun;
    private static Run? _statsTrianglesRun;
    private static Run? _statsBoundsRun;
    private static Run? _statsMemoryRun;
    private static string _customObjPath = "";

    private static RichTextBlock? _statsNameBlock;
    private static RichTextBlock? _statsVerticesBlock;
    private static RichTextBlock? _statsTrianglesBlock;
    private static RichTextBlock? _statsBoundsBlock;
    private static RichTextBlock? _statsMemoryBlock;

    public static FrameworkElement Create()
    {
        var mainGrid = new Mesh3DViewerPageGrid();

        // Load default shape
        RegenerateMeshGeometry();

        // 1. LEFT SIDEBAR PANEL
        var sidebar = new Border
        {
            Background = new ThemeResourceBrush("ControlBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(0, 0, 1f, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var sidebarScroll = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var sidebarStack = new Microsoft.UI.Xaml.Controls.StackPanel 
        { 
            Orientation = Orientation.Vertical,
            Padding = new Thickness(16f),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        sidebarScroll.Content = sidebarStack;
        sidebar.Child = sidebarScroll;

        var title = new RichTextBlock { Font = AppState.GetFont(), FontSize = 16f, Margin = new Thickness(0, 0, 0, 8f) };
        title.Inlines.Add(new Bold(new Run("WebGPU 3D Mesh Viewer")));
        sidebarStack.AddChild(title);

        var description = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11.5f, Margin = new Thickness(0, 0, 0, 16f), Foreground = new ThemeResourceBrush("TextSecondary") };
        description.Inlines.Add(new Run("A declarative, WPF-style retained 3D media layout engine. Lighting transforms and matrix math are fully offloaded to the GPU."));
        sidebarStack.AddChild(description);

        // Viewport Layout Segmented Toolbar
        var layoutHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 6f) };
        layoutHeader.Inlines.Add(new Bold(new Run("Viewport Layout:")));
        sidebarStack.AddChild(layoutHeader);

        var layoutGrid = new Grid { Margin = new Thickness(0, 0, 0, 16f) };
        layoutGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        layoutGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        layoutGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        layoutGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        layoutGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        _layoutBtnQuad = CreateLayoutButton("Quad", LayoutMode3D.Quad);
        _layoutBtnTop = CreateLayoutButton("Top", LayoutMode3D.Top);
        _layoutBtnFront = CreateLayoutButton("Front", LayoutMode3D.Front);
        _layoutBtnRight = CreateLayoutButton("Right", LayoutMode3D.Right);
        _layoutBtnPersp = CreateLayoutButton("Persp", LayoutMode3D.Perspective);

        layoutGrid.AddChild(_layoutBtnQuad); Grid.SetColumn(_layoutBtnQuad, 0);
        layoutGrid.AddChild(_layoutBtnTop); Grid.SetColumn(_layoutBtnTop, 1);
        layoutGrid.AddChild(_layoutBtnFront); Grid.SetColumn(_layoutBtnFront, 2);
        layoutGrid.AddChild(_layoutBtnRight); Grid.SetColumn(_layoutBtnRight, 3);
        layoutGrid.AddChild(_layoutBtnPersp); Grid.SetColumn(_layoutBtnPersp, 4);

        sidebarStack.AddChild(layoutGrid);

        // Combobox for Shape selection
        var shapeHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        shapeHeader.Inlines.Add(new Bold(new Run("Mesh Geometry:")));
        sidebarStack.AddChild(shapeHeader);

        var shapeCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 16f)
        };
        shapeCombo.Items.Add(new ComboBoxItem { Text = "Cube" });
        shapeCombo.Items.Add(new ComboBoxItem { Text = "Sphere" });
        shapeCombo.Items.Add(new ComboBoxItem { Text = "Torus" });
        shapeCombo.Items.Add(new ComboBoxItem { Text = "Procedural Terrain" });
        shapeCombo.Items.Add(new ComboBoxItem { Text = "OBJ: Spacecraft" });
        shapeCombo.Items.Add(new ComboBoxItem { Text = "OBJ: Faceted Jewel" });
        shapeCombo.Items.Add(new ComboBoxItem { Text = "OBJ: Custom Path" });
        shapeCombo.SelectedItem = shapeCombo.Items[2]; // Default to Torus

        // Combobox for Render Mode selection
        var renderModeHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        renderModeHeader.Inlines.Add(new Bold(new Run("Render Mode:")));
        
        var renderModeCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 16f)
        };
        renderModeCombo.Items.Add(new ComboBoxItem { Text = "Solid Shaded" });
        renderModeCombo.Items.Add(new ComboBoxItem { Text = "Wireframe Only" });
        renderModeCombo.Items.Add(new ComboBoxItem { Text = "Solid + Wireframe" });
        renderModeCombo.SelectedItem = renderModeCombo.Items[2]; // Default to Solid + Wireframe

        renderModeCombo.SelectionChanged += (s, e) =>
        {
            if (renderModeCombo.SelectedItem != null)
            {
                _selectedRenderMode = renderModeCombo.SelectedItem.Text switch
                {
                    "Solid Shaded" => RenderMode3D.Solid,
                    "Wireframe Only" => RenderMode3D.Wireframe,
                    "Solid + Wireframe" => RenderMode3D.SolidWireframe,
                    _ => RenderMode3D.Solid
                };
                UpdateViewportModels();
            }
        };

        // Combobox for Shading Mode selection
        var shadingModeHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        shadingModeHeader.Inlines.Add(new Bold(new Run("Shading & Diagnostic:")));

        var shadingModeCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 16f)
        };
        shadingModeCombo.Items.Add(new ComboBoxItem { Text = "Realistic (PBR GGX)" });
        shadingModeCombo.Items.Add(new ComboBoxItem { Text = "Conceptual (Gooch)" });
        shadingModeCombo.Items.Add(new ComboBoxItem { Text = "Flat / Unlit" });
        shadingModeCombo.Items.Add(new ComboBoxItem { Text = "Hidden Line" });
        shadingModeCombo.Items.Add(new ComboBoxItem { Text = "Shades of Gray" });
        shadingModeCombo.Items.Add(new ComboBoxItem { Text = "X-Ray Mode" });
        shadingModeCombo.Items.Add(new ComboBoxItem { Text = "Normals Diagnostic" });
        shadingModeCombo.SelectedItem = shadingModeCombo.Items[0]; // Default to Realistic

        shadingModeCombo.SelectionChanged += (s, e) =>
        {
            if (shadingModeCombo.SelectedItem != null)
            {
                _selectedShadingMode = shadingModeCombo.SelectedItem.Text switch
                {
                    "Realistic (PBR GGX)" => ShadingMode3D.Realistic,
                    "Conceptual (Gooch)" => ShadingMode3D.Conceptual,
                    "Flat / Unlit" => ShadingMode3D.Flat,
                    "Hidden Line" => ShadingMode3D.HiddenLine,
                    "Shades of Gray" => ShadingMode3D.ShadesOfGray,
                    "X-Ray Mode" => ShadingMode3D.XRay,
                    "Normals Diagnostic" => ShadingMode3D.Normals,
                    _ => ShadingMode3D.Realistic
                };
                UpdateViewportModels();
            }
        };

        var pathInputStack = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 0, 16f),
            Visibility = Visibility.Visible
        };
        
        var pathHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, Margin = new Thickness(0, 0, 0, 4) };
        pathHeader.Inlines.Add(new Bold(new Run("OBJ FILE PATH:")));
        pathInputStack.AddChild(pathHeader);

        var pathErrorText = new RichTextBlock 
        { 
            Font = AppState.GetFont(), 
            FontSize = 10.5f, 
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(new Vector4(0.9f, 0.3f, 0.3f, 1f)),
            Visibility = Visibility.Collapsed
        };

        var pathGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        pathGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        pathGrid.ColumnDefinitions.Add(new GridLength(8f, GridUnitType.Absolute));
        pathGrid.ColumnDefinitions.Add(new GridLength(72f, GridUnitType.Absolute));

        var pathTextBox = new TextBox
        {
            PlaceholderText = "Enter .obj path...",
            HeightConstraint = 32f,
            Font = AppState.GetFont()
        };
        pathTextBox.KeyDown += (s, e) =>
        {
            if (e.Key == Silk.NET.Input.Key.Enter && !string.IsNullOrEmpty(pathTextBox.Text))
            {
                try
                {
                    _customObjPath = pathTextBox.Text;
                    shapeCombo.SelectedItem = shapeCombo.Items[6]; // OBJ: Custom Path
                    _selectedMeshType = "OBJ: Custom Path";
                    _activeLoadedModel = ObjReader.LoadObj(_customObjPath);
                    _activeMeshGeometry = ObjReader.MergeParts(_activeLoadedModel);
                    UpdateViewportModels();
                }
                catch (Exception ex)
                {
                    pathErrorText.Inlines.Clear();
                    pathErrorText.Inlines.Add(new Run($"File load failed: {ex.Message}"));
                    pathErrorText.Visibility = Visibility.Visible;
                    pathErrorText.Invalidate();
                }
            }
        };
        pathGrid.AddChild(pathTextBox);
        Grid.SetColumn(pathTextBox, 0);

        var browseBtn = new Button
        {
            HeightConstraint = 32f,
            CornerRadius = 4f,
            Background = new ThemeResourceBrush("ControlBackgroundHover")
        };
        var browseBtnRun = new Run("Browse") { FontSize = 12f, Foreground = new ThemeResourceBrush("TextPrimary") };
        browseBtn.Content = new RichTextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Inlines = { new Bold(browseBtnRun) } };
        pathGrid.AddChild(browseBtn);
        Grid.SetColumn(browseBtn, 2);
        
        pathInputStack.AddChild(pathGrid);
        pathInputStack.AddChild(pathErrorText);
        
        shapeCombo.SelectionChanged += (s, e) =>
        {
            if (shapeCombo.SelectedItem != null)
            {
                _selectedMeshType = shapeCombo.SelectedItem.Text;
                
                pathErrorText.Visibility = Visibility.Collapsed;
                
                RegenerateMeshGeometry();
                UpdateViewportModels();
            }
        };

        browseBtn.Click += async (s, e) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".obj");
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    pathTextBox.Text = file.Path;
                    pathErrorText.Visibility = Visibility.Collapsed;
                    
                    _customObjPath = file.Path;
                    shapeCombo.SelectedItem = shapeCombo.Items[6]; // OBJ: Custom Path
                    _selectedMeshType = "OBJ: Custom Path";
                    
                    _activeLoadedModel = ObjReader.LoadObj(file.Path);
                    _activeMeshGeometry = ObjReader.MergeParts(_activeLoadedModel);
                    UpdateViewportModels();
                }
            }
            catch (Exception ex)
            {
                pathErrorText.Inlines.Clear();
                pathErrorText.Inlines.Add(new Run($"File pick failed: {ex.Message}"));
                pathErrorText.Visibility = Visibility.Visible;
                pathErrorText.Invalidate();
            }
        };

        sidebarStack.AddChild(shapeCombo);
        sidebarStack.AddChild(renderModeHeader);
        sidebarStack.AddChild(renderModeCombo);
        sidebarStack.AddChild(shadingModeHeader);
        sidebarStack.AddChild(shadingModeCombo);
        sidebarStack.AddChild(pathInputStack);

        // 1.5 Mesh Statistics Card
        var statsCard = new Border
        {
            Background = new ThemeResourceBrush("ControlBackgroundHover"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 6f,
            Padding = new Thickness(12f),
            Margin = new Thickness(0, 0, 0, 16f),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var statsStack = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Vertical };
        statsCard.Child = statsStack;

        var statsTitle = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 8f) };
        statsTitle.Inlines.Add(new Bold(new Run("Mesh Statistics")));
        statsStack.AddChild(statsTitle);

        _statsNameRun = new Run("-");
        _statsNameBlock = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, Foreground = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Right };
        _statsNameBlock.Inlines.Add(new Bold(_statsNameRun));

        _statsVerticesRun = new Run("0");
        _statsVerticesBlock = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, Foreground = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Right };
        _statsVerticesBlock.Inlines.Add(new Bold(_statsVerticesRun));

        _statsTrianglesRun = new Run("0");
        _statsTrianglesBlock = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, Foreground = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Right };
        _statsTrianglesBlock.Inlines.Add(new Bold(_statsTrianglesRun));

        _statsBoundsRun = new Run("0.0 x 0.0 x 0.0");
        _statsBoundsBlock = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, Foreground = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Right };
        _statsBoundsBlock.Inlines.Add(new Bold(_statsBoundsRun));

        _statsMemoryRun = new Run("0 KB");
        _statsMemoryBlock = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, Foreground = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Right };
        _statsMemoryBlock.Inlines.Add(new Bold(_statsMemoryRun));

        statsStack.AddChild(CreateStatRow("Name:", _statsNameBlock));
        statsStack.AddChild(CreateStatRow("Vertices:", _statsVerticesBlock));
        statsStack.AddChild(CreateStatRow("Triangles:", _statsTrianglesBlock));
        statsStack.AddChild(CreateStatRow("Size (Bounds):", _statsBoundsBlock));
        statsStack.AddChild(CreateStatRow("GPU Memory:", _statsMemoryBlock));

        sidebarStack.AddChild(statsCard);

        // Color selection header
        var colorHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 6) };
        colorHeader.Inlines.Add(new Bold(new Run("Mesh Color Palette:")));
        sidebarStack.AddChild(colorHeader);

        var colorGrid = new Grid { Margin = new Thickness(0, 0, 0, 16f) };
        colorGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        colorGrid.ColumnDefinitions.Add(new GridLength(2f, GridUnitType.Star));

        var defaultBtn = new Button
        {
            HeightConstraint = 36f,
            WidthConstraint = 60f,
            CornerRadius = 4f,
            Margin = new Thickness(4f),
            Background = new SolidColorBrush(new Vector4(0.70f, 0.70f, 0.72f, 1.0f))
        };
        defaultBtn.Click += (s, e) =>
        {
            _activeModelColor = new Vector4(0.70f, 0.70f, 0.72f, 1.0f);
            UpdateViewportModels();
        };
        colorGrid.AddChild(defaultBtn);
        Grid.SetColumn(defaultBtn, 0);

        var customBtn = new Button
        {
            HeightConstraint = 36f,
            WidthConstraint = 100f,
            CornerRadius = 4f,
            Margin = new Thickness(4f),
            Background = new ThemeResourceBrush("ControlBackgroundHover")
        };
        var customBtnRun = new Run("Custom") { FontSize = 11f, Foreground = new ThemeResourceBrush("TextPrimary") };
        customBtn.Content = new RichTextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Inlines = { new Bold(customBtnRun) } };

        customBtn.Click += (s, e) => { ShowColorPickerPopup(customBtn); };
        colorGrid.AddChild(customBtn);
        Grid.SetColumn(customBtn, 1);

        sidebarStack.AddChild(colorGrid);

        // Lighting settings header
        var lightingHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 8, 0, 4) };
        lightingHeader.Inlines.Add(new Bold(new Run("Directional Light Angle (X/Y/Z):")));
        sidebarStack.AddChild(lightingHeader);

        var lightXSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = -2f, Maximum = 2f, Value = 0.5f, Margin = new Thickness(0, 0, 0, 8f) };
        lightXSlider.ValueChanged += (s, e) => { _lightDirection.X = lightXSlider.Value; SyncLighting(); };
        sidebarStack.AddChild(lightXSlider);

        var lightYSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0.1f, Maximum = 3f, Value = 1.0f, Margin = new Thickness(0, 0, 0, 8f) };
        lightYSlider.ValueChanged += (s, e) => { _lightDirection.Y = lightYSlider.Value; SyncLighting(); };
        sidebarStack.AddChild(lightYSlider);

        var lightZSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = -2f, Maximum = 2f, Value = -0.5f, Margin = new Thickness(0, 0, 0, 16f) };
        lightZSlider.ValueChanged += (s, e) => { _lightDirection.Z = lightZSlider.Value; SyncLighting(); };
        sidebarStack.AddChild(lightZSlider);

        // Intensity settings
        var intensityHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        intensityHeader.Inlines.Add(new Bold(new Run("Diffuse Light Intensity:")));
        sidebarStack.AddChild(intensityHeader);

        var intensitySlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 2f, Value = 1.0f, Margin = new Thickness(0, 0, 0, 16f) };
        intensitySlider.ValueChanged += (s, e) => { _lightIntensity = intensitySlider.Value; SyncLighting(); };
        sidebarStack.AddChild(intensitySlider);

        var ambientHeader = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        ambientHeader.Inlines.Add(new Bold(new Run("Ambient Light Intensity:")));
        sidebarStack.AddChild(ambientHeader);

        var ambientSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 0f, Maximum = 1f, Value = 0.25f, Margin = new Thickness(0, 0, 0, 20f) };
        ambientSlider.ValueChanged += (s, e) => { _ambientIntensity = ambientSlider.Value; SyncLighting(); };
        sidebarStack.AddChild(ambientSlider);

        // Animation toggle checkbox
        var animStack = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16f) };
        var animText = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f };
        animText.Inlines.Add(new Run("Animate Mesh Rotation"));
        var animChk = new CheckBox
        {
            Content = animText,
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        animChk.CheckedChanged += (s, e) =>
        {
            _animateRotation = animChk.IsChecked;
        };
        animStack.AddChild(animChk);
        sidebarStack.AddChild(animStack);

        mainGrid.PaneContent = sidebar;

        // 2. MAIN 4-WAY SPLIT VIEW WORKSPACE (3x3 CAD NESTED GRIDS FOR SPLITTING)
        _viewportsGrid = new Grid { Margin = new Thickness(16f) };
        _viewportsGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        _viewportsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        _viewportsGrid.ColumnDefinitions.Add(new GridLength(6f, GridUnitType.Absolute));
        _viewportsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        _leftGrid = new Grid();
        _leftGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        _leftGrid.RowDefinitions.Add(new GridLength(6f, GridUnitType.Absolute));
        _leftGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        _rightGrid = new Grid();
        _rightGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        _rightGrid.RowDefinitions.Add(new GridLength(6f, GridUnitType.Absolute));
        _rightGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        _viewportsGrid.AddChild(_leftGrid);
        Grid.SetColumn(_leftGrid, 0);
        Grid.SetRow(_leftGrid, 0);

        _viewportsGrid.AddChild(_rightGrid);
        Grid.SetColumn(_rightGrid, 2);
        Grid.SetRow(_rightGrid, 0);

        // 2.A Viewport 1: Top Orthographic View
        _card1 = new Border
        {
            CornerRadius = 8f,
            BorderThickness = new Thickness(1f),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            Background = new ThemeResourceBrush("CardBackground"),
            Margin = new Thickness(0)
        };
        var viewportStack1 = new Grid();
        _card1.Child = viewportStack1;

        _viewport1 = new Viewport3D
        {
            Camera = new OrthographicCamera
            {
                Position = new Vector3(0f, 7f, 0f),
                LookDirection = new Vector3(0f, -1f, 0f),
                UpDirection = new Vector3(0f, 0f, -1f),
                Width = 7f
            }
        };
        _model1 = new ModelVisual3D();
        _viewport1.Children.Add(_model1);
        viewportStack1.AddChild(_viewport1);

        var headerStack1 = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12f)
        };
        var overlayText1 = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, VerticalAlignment = VerticalAlignment.Center };
        overlayText1.Inlines.Add(new Bold(new Run("Top View (Orthographic)")));
        headerStack1.AddChild(overlayText1);

        _maxBtn1 = new Button
        {
            HeightConstraint = 24f,
            WidthConstraint = 24f,
            CornerRadius = 4f,
            Margin = new Thickness(8f, 0, 0, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new ThemeResourceBrush("ControlBackgroundHover")
        };
        _maxBtn1.Content = new ViewportLayoutIcon();
        _maxBtn1.Click += (s, e) =>
        {
            SetLayoutMode(_currentLayoutMode == LayoutMode3D.Top ? LayoutMode3D.Quad : LayoutMode3D.Top);
        };
        headerStack1.AddChild(_maxBtn1);
        viewportStack1.AddChild(headerStack1);

        _leftGrid.AddChild(_card1);
        Grid.SetRow(_card1, 0);
        Grid.SetColumn(_card1, 0);

        // 2.B Viewport 2: Front Orthographic View
        _card2 = new Border
        {
            CornerRadius = 8f,
            BorderThickness = new Thickness(1f),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            Background = new ThemeResourceBrush("CardBackground"),
            Margin = new Thickness(0)
        };
        var viewportStack2 = new Grid();
        _card2.Child = viewportStack2;

        _viewport2 = new Viewport3D
        {
            Camera = new OrthographicCamera
            {
                Position = new Vector3(0f, 0f, -7f),
                LookDirection = new Vector3(0f, 0f, 1f),
                UpDirection = new Vector3(0f, 1f, 0f),
                Width = 7f
            }
        };
        _model2 = new ModelVisual3D();
        _viewport2.Children.Add(_model2);
        viewportStack2.AddChild(_viewport2);

        var headerStack2 = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12f)
        };
        var overlayText2 = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, VerticalAlignment = VerticalAlignment.Center };
        overlayText2.Inlines.Add(new Bold(new Run("Front View (Orthographic)")));
        headerStack2.AddChild(overlayText2);

        _maxBtn2 = new Button
        {
            HeightConstraint = 24f,
            WidthConstraint = 24f,
            CornerRadius = 4f,
            Margin = new Thickness(8f, 0, 0, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new ThemeResourceBrush("ControlBackgroundHover")
        };
        _maxBtn2.Content = new ViewportLayoutIcon();
        _maxBtn2.Click += (s, e) =>
        {
            SetLayoutMode(_currentLayoutMode == LayoutMode3D.Front ? LayoutMode3D.Quad : LayoutMode3D.Front);
        };
        headerStack2.AddChild(_maxBtn2);
        viewportStack2.AddChild(headerStack2);

        _rightGrid.AddChild(_card2);
        Grid.SetRow(_card2, 0);
        Grid.SetColumn(_card2, 0);

        // 2.C Viewport 3: Right Orthographic View
        _card3 = new Border
        {
            CornerRadius = 8f,
            BorderThickness = new Thickness(1f),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            Background = new ThemeResourceBrush("CardBackground"),
            Margin = new Thickness(0)
        };
        var viewportStack3 = new Grid();
        _card3.Child = viewportStack3;

        _viewport3 = new Viewport3D
        {
            Camera = new OrthographicCamera
            {
                Position = new Vector3(-7f, 0f, 0f),
                LookDirection = new Vector3(1f, 0f, 0f),
                UpDirection = new Vector3(0f, 1f, 0f),
                Width = 7f
            }
        };
        _model3 = new ModelVisual3D();
        _viewport3.Children.Add(_model3);
        viewportStack3.AddChild(_viewport3);

        var headerStack3 = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12f)
        };
        var overlayText3 = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, VerticalAlignment = VerticalAlignment.Center };
        overlayText3.Inlines.Add(new Bold(new Run("Right View (Orthographic)")));
        headerStack3.AddChild(overlayText3);

        _maxBtn3 = new Button
        {
            HeightConstraint = 24f,
            WidthConstraint = 24f,
            CornerRadius = 4f,
            Margin = new Thickness(8f, 0, 0, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new ThemeResourceBrush("ControlBackgroundHover")
        };
        _maxBtn3.Content = new ViewportLayoutIcon();
        _maxBtn3.Click += (s, e) =>
        {
            SetLayoutMode(_currentLayoutMode == LayoutMode3D.Right ? LayoutMode3D.Quad : LayoutMode3D.Right);
        };
        headerStack3.AddChild(_maxBtn3);
        viewportStack3.AddChild(headerStack3);

        _leftGrid.AddChild(_card3);
        Grid.SetRow(_card3, 2);
        Grid.SetColumn(_card3, 0);

        // 2.D Viewport 4: 3D Perspective View
        _card4 = new Border
        {
            CornerRadius = 8f,
            BorderThickness = new Thickness(1f),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            Background = new ThemeResourceBrush("CardBackground"),
            Margin = new Thickness(0)
        };
        var viewportStack4 = new Grid();
        _card4.Child = viewportStack4;

        _viewport4 = new Viewport3D
        {
            Camera = new PerspectiveCamera
            {
                Position = new Vector3(4f, 4f, -6f),
                LookAt = new Vector3(0f, 0f, 0f),
                FieldOfView = 50f
            }
        };
        _model4 = new ModelVisual3D();
        _viewport4.Children.Add(_model4);
        viewportStack4.AddChild(_viewport4);

        var headerStack4 = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12f)
        };
        var overlayText4 = new RichTextBlock { Font = AppState.GetFont(), FontSize = 12f, VerticalAlignment = VerticalAlignment.Center };
        overlayText4.Inlines.Add(new Bold(new Run("3D Perspective View")));
        headerStack4.AddChild(overlayText4);

        _maxBtn4 = new Button
        {
            HeightConstraint = 24f,
            WidthConstraint = 24f,
            CornerRadius = 4f,
            Margin = new Thickness(8f, 0, 0, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new ThemeResourceBrush("ControlBackgroundHover")
        };
        _maxBtn4.Content = new ViewportLayoutIcon();
        _maxBtn4.Click += (s, e) =>
        {
            SetLayoutMode(_currentLayoutMode == LayoutMode3D.Perspective ? LayoutMode3D.Quad : LayoutMode3D.Perspective);
        };
        headerStack4.AddChild(_maxBtn4);
        viewportStack4.AddChild(headerStack4);

        _rightGrid.AddChild(_card4);
        Grid.SetRow(_card4, 2);
        Grid.SetColumn(_card4, 0);

        // 2.E GridSplitters
        _colSplitter = new GridSplitter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new ThemeResourceBrush("GridSplitterBackground"),
            ResizeDirection = GridSplitterResizeDirection.Columns
        };
        _viewportsGrid.AddChild(_colSplitter);
        Grid.SetRow(_colSplitter, 0);
        Grid.SetColumn(_colSplitter, 1);

        _rowSplitterLeft = new GridSplitter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new ThemeResourceBrush("GridSplitterBackground"),
            ResizeDirection = GridSplitterResizeDirection.Rows
        };
        _leftGrid.AddChild(_rowSplitterLeft);
        Grid.SetRow(_rowSplitterLeft, 1);
        Grid.SetColumn(_rowSplitterLeft, 0);

        _rowSplitterRight = new GridSplitter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new ThemeResourceBrush("GridSplitterBackground"),
            ResizeDirection = GridSplitterResizeDirection.Rows
        };
        _rightGrid.AddChild(_rowSplitterRight);
        Grid.SetRow(_rowSplitterRight, 1);
        Grid.SetColumn(_rowSplitterRight, 0);

        mainGrid.MainContent = _viewportsGrid;

        // Populate initial 3D geometries and register layout animation callbacks
        UpdateViewportModels();
        SyncLighting();

        return mainGrid;
    }

    private static void ShowColorPickerPopup(Button triggerBtn)
    {
        if (_pickerPopup == null)
        {
            var cp = new ColorPicker
            {
                WidthConstraint = 280f,
                HeightConstraint = 330f,
                Color = _activeModelColor
            };

            cp.ColorChanged += (s, e) =>
            {
                _activeModelColor = e.NewColor;
                UpdateViewportModels();
            };

            _pickerPopup = new Border
            {
                Background = new ThemeResourceBrush("CardBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(10),
                Child = cp
            };
        }
        else
        {
            if (_pickerPopup.Child is ColorPicker cp)
            {
                cp.Color = _activeModelColor;
            }
        }

        Vector2 absPos = triggerBtn.Offset;
        Visual? current = triggerBtn.Parent;
        while (current != null)
        {
            absPos += current.Offset;
            current = current.Parent;
        }

        _pickerPopup.Width = 300f;
        _pickerPopup.Height = 350f;
        _pickerPopup.NotifyThemeChanged();
        
        float popX = Math.Max(10f, absPos.X - 100f);
        PopupService.ShowPopup(_pickerPopup, new Vector2(popX, absPos.Y + triggerBtn.Size.Y + 2f), triggerBtn);
    }

    private static Grid CreateStatRow(string label, RichTextBlock valBlock)
    {
        var rowGrid = new Grid { Margin = new Thickness(0, 2f, 0, 2f) };
        rowGrid.ColumnDefinitions.Add(new GridLength(100f, GridUnitType.Absolute));
        rowGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        var labelBlock = new RichTextBlock { Font = AppState.GetFont(), FontSize = 11f, Foreground = new ThemeResourceBrush("TextSecondary") };
        labelBlock.Inlines.Add(new Run(label));
        rowGrid.AddChild(labelBlock);
        Grid.SetColumn(labelBlock, 0);

        rowGrid.AddChild(valBlock);
        Grid.SetColumn(valBlock, 1);

        return rowGrid;
    }

    private static void RegenerateMeshGeometry()
    {
        _activeLoadedModel = null;

        if (_selectedMeshType == "Cube")
        {
            _activeMeshGeometry = MeshBuilder.CreateCube();
        }
        else if (_selectedMeshType == "Sphere")
        {
            _activeMeshGeometry = MeshBuilder.CreateSphere(2.0f, 24, 24);
        }
        else if (_selectedMeshType == "Torus")
        {
            _activeMeshGeometry = MeshBuilder.CreateTorus(2.2f, 0.7f, 24, 24);
        }
        else if (_selectedMeshType == "Procedural Terrain")
        {
            _activeMeshGeometry = MeshBuilder.CreateTerrain(5.0f, 24, 1.8f);
        }
        else if (_selectedMeshType == "OBJ: Spacecraft")
        {
            try
            {
                var path = Path.Combine("models", "spacecraft.obj");
                _activeLoadedModel = ObjReader.LoadObj(path);
                _activeMeshGeometry = ObjReader.MergeParts(_activeLoadedModel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mesh3DViewerPage] Error loading spacecraft.obj: {ex.Message}");
                _activeMeshGeometry = MeshBuilder.CreateCube(); // Fallback
            }
        }
        else if (_selectedMeshType == "OBJ: Faceted Jewel")
        {
            try
            {
                var path = Path.Combine("models", "jewel.obj");
                _activeLoadedModel = ObjReader.LoadObj(path);
                _activeMeshGeometry = ObjReader.MergeParts(_activeLoadedModel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mesh3DViewerPage] Error loading jewel.obj: {ex.Message}");
                _activeMeshGeometry = MeshBuilder.CreateCube(); // Fallback
            }
        }
    }

    private static void UpdateViewportModels()
    {
        if (_viewport1 == null || _viewport2 == null || _viewport3 == null || _viewport4 == null ||
            _model1 == null || _model2 == null || _model3 == null || _model4 == null ||
            _activeMeshGeometry == null) return;

        // Calculate and update mesh statistics in real time
        int vertCount = _activeMeshGeometry.Positions.Length;
        int triCount = _activeMeshGeometry.TriangleIndices.Length / 3;

        Vector3 min = new Vector3(float.MaxValue);
        Vector3 max = new Vector3(float.MinValue);
        foreach (var pos in _activeMeshGeometry.Positions)
        {
            min = Vector3.Min(min, pos);
            max = Vector3.Max(max, pos);
        }
        Vector3 size = max - min;
        if (vertCount == 0)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
            size = Vector3.Zero;
        }

        // Positions (12 bytes) + Normals (12 bytes) = 24 bytes per vertex
        // Indices (4 bytes) = 4 bytes per index
        float memKb = (vertCount * 24f + _activeMeshGeometry.TriangleIndices.Length * 4f) / 1024f;

        if (_statsNameRun != null)
        {
            string name = _selectedMeshType;
            if (name == "OBJ: Custom Path")
            {
                name = string.IsNullOrEmpty(_customObjPath) ? "custom.obj" : Path.GetFileName(_customObjPath);
            }
            else if (name.StartsWith("OBJ: "))
            {
                name = name.Substring(5).ToLower() + ".obj";
            }
            _statsNameRun.Text = name;
        }
        if (_statsVerticesRun != null) _statsVerticesRun.Text = vertCount.ToString("N0");
        if (_statsTrianglesRun != null) _statsTrianglesRun.Text = triCount.ToString("N0");
        if (_statsBoundsRun != null) _statsBoundsRun.Text = $"{size.X:F2} x {size.Y:F2} x {size.Z:F2}";
        if (_statsMemoryRun != null) _statsMemoryRun.Text = $"{memKb:F1} KB";

        // Invalidate containing RichTextBlocks to force visual layout updating and repaint
        if (_statsNameBlock != null) _statsNameBlock.Invalidate();
        if (_statsVerticesBlock != null) _statsVerticesBlock.Invalidate();
        if (_statsTrianglesBlock != null) _statsTrianglesBlock.Invalidate();
        if (_statsBoundsBlock != null) _statsBoundsBlock.Invalidate();
        if (_statsMemoryBlock != null) _statsMemoryBlock.Invalidate();

        if (_activeMeshGeometry != _lastMeshGeometry)
        {
            _lastMeshGeometry = _activeMeshGeometry;
            FitViewportsToBounds();
        }

        _viewport1.RenderMode = _selectedRenderMode;
        _viewport2.RenderMode = _selectedRenderMode;
        _viewport3.RenderMode = _selectedRenderMode;
        _viewport4.RenderMode = _selectedRenderMode;

        _viewport1.ShadingMode = _selectedShadingMode;
        _viewport2.ShadingMode = _selectedShadingMode;
        _viewport3.ShadingMode = _selectedShadingMode;
        _viewport4.ShadingMode = _selectedShadingMode;

        // Clear all model children
        _model1.Children.Clear();
        _model2.Children.Clear();
        _model3.Children.Clear();
        _model4.Children.Clear();

        if (_activeLoadedModel != null && _activeLoadedModel.Parts.Count > 0)
        {
            bool isDefaultClayColor = _activeModelColor == new Vector4(0.70f, 0.70f, 0.72f, 1.0f);

            foreach (var part in _activeLoadedModel.Parts)
            {
                Vector4 color = part.Color;
                if (!isDefaultClayColor)
                {
                    // Multiply custom picked color to act as a tint overlay
                    color = color * _activeModelColor;
                }

                var brush = new SolidColorBrush(color) { Opacity = part.Opacity };
                var material = new DiffuseMaterial(brush)
                {
                    SpecularColor = part.SpecularColor,
                    Shininess = part.Shininess,
                    AmbientColor = part.AmbientColor
                };

                _model1.Children.Add(new ModelVisual3D { Content = new GeometryModel3D { Geometry = part.Geometry, Material = material } });
                _model2.Children.Add(new ModelVisual3D { Content = new GeometryModel3D { Geometry = part.Geometry, Material = material } });
                _model3.Children.Add(new ModelVisual3D { Content = new GeometryModel3D { Geometry = part.Geometry, Material = material } });
                _model4.Children.Add(new ModelVisual3D { Content = new GeometryModel3D { Geometry = part.Geometry, Material = material } });
            }
        }
        else
        {
            var brush = new SolidColorBrush(_activeModelColor);
            var material = new DiffuseMaterial(brush);

            _model1.Children.Add(new ModelVisual3D { Content = new GeometryModel3D { Geometry = _activeMeshGeometry, Material = material } });
            _model2.Children.Add(new ModelVisual3D { Content = new GeometryModel3D { Geometry = _activeMeshGeometry, Material = material } });
            _model3.Children.Add(new ModelVisual3D { Content = new GeometryModel3D { Geometry = _activeMeshGeometry, Material = material } });
            _model4.Children.Add(new ModelVisual3D { Content = new GeometryModel3D { Geometry = _activeMeshGeometry, Material = material } });
        }

        _viewport1.Invalidate();
        _viewport2.Invalidate();
        _viewport3.Invalidate();
        _viewport4.Invalidate();
    }

    private static void FitViewportsToBounds()
    {
        if (_activeMeshGeometry == null) return;
        var positions = _activeMeshGeometry.Positions;
        if (positions.Length == 0) return;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in positions)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z;
            if (p.Z > maxZ) maxZ = p.Z;
        }

        var center = new Vector3((maxX + minX) * 0.5f, (maxY + minY) * 0.5f, (maxZ + minZ) * 0.5f);
        float sizeX = maxX - minX;
        float sizeY = maxY - minY;
        float sizeZ = maxZ - minZ;
        float maxDim = Math.Max(sizeX, Math.Max(sizeY, sizeZ));
        if (maxDim < 0.1f) maxDim = 2.0f;

        float orthoWidth = maxDim * 1.5f;
        float camDist = maxDim * 1.8f;

        // Update Top View
        if (_viewport1?.Camera is OrthographicCamera topCam)
        {
            topCam.Position = center + new Vector3(0f, camDist, 0f);
            topCam.LookDirection = new Vector3(0f, -1f, 0f);
            topCam.UpDirection = new Vector3(0f, 0f, -1f);
            topCam.Width = orthoWidth;
        }

        // Update Front View
        if (_viewport2?.Camera is OrthographicCamera frontCam)
        {
            frontCam.Position = center + new Vector3(0f, 0f, -camDist);
            frontCam.LookDirection = new Vector3(0f, 0f, 1f);
            frontCam.UpDirection = new Vector3(0f, 1f, 0f);
            frontCam.Width = orthoWidth;
        }

        // Update Right View
        if (_viewport3?.Camera is OrthographicCamera rightCam)
        {
            rightCam.Position = center + new Vector3(-camDist, 0f, 0f);
            rightCam.LookDirection = new Vector3(1f, 0f, 0f);
            rightCam.UpDirection = new Vector3(0f, 1f, 0f);
            rightCam.Width = orthoWidth;
        }

        // Update Perspective View
        if (_viewport4?.Camera is PerspectiveCamera persCam)
        {
            persCam.Position = center + new Vector3(maxDim * 1.1f, maxDim * 1.1f, -maxDim * 1.5f);
            persCam.LookAt = center;
        }

        _viewport1?.Invalidate();
        _viewport2?.Invalidate();
        _viewport3?.Invalidate();
        _viewport4?.Invalidate();
    }

    private static void SyncLighting()
    {
        if (_viewport1 == null || _viewport2 == null || _viewport3 == null || _viewport4 == null) return;

        _viewport1.LightDirection = _lightDirection;
        _viewport1.LightIntensity = _lightIntensity;
        _viewport1.AmbientIntensity = _ambientIntensity;

        _viewport2.LightDirection = _lightDirection;
        _viewport2.LightIntensity = _lightIntensity;
        _viewport2.AmbientIntensity = _ambientIntensity;

        _viewport3.LightDirection = _lightDirection;
        _viewport3.LightIntensity = _lightIntensity;
        _viewport3.AmbientIntensity = _ambientIntensity;

        _viewport4.LightDirection = _lightDirection;
        _viewport4.LightIntensity = _lightIntensity;
        _viewport4.AmbientIntensity = _ambientIntensity;

        _viewport1.Invalidate();
        _viewport2.Invalidate();
        _viewport3.Invalidate();
        _viewport4.Invalidate();
    }

    public static void UpdateAnimations(float delta)
    {
        if (!_animateRotation) return;

        // Apply continuous Y-axis rotation over time
        _rotationAngle += (float)delta * 0.7f;
        if (_rotationAngle > MathF.PI * 2f) _rotationAngle -= MathF.PI * 2f;

        // Sync rotations across all views
        var rotation = Matrix4x4.CreateRotationY(_rotationAngle);

        if (_model1 != null)
        {
            for (int i = 0; i < _model1.Children.Count; i++)
            {
                if (_model1.Children[i] is ModelVisual3D mv && mv.Content != null)
                {
                    mv.Content.Transform = rotation;
                }
            }
        }

        if (_model2 != null)
        {
            for (int i = 0; i < _model2.Children.Count; i++)
            {
                if (_model2.Children[i] is ModelVisual3D mv && mv.Content != null)
                {
                    mv.Content.Transform = rotation;
                }
            }
        }

        if (_model3 != null)
        {
            for (int i = 0; i < _model3.Children.Count; i++)
            {
                if (_model3.Children[i] is ModelVisual3D mv && mv.Content != null)
                {
                    mv.Content.Transform = rotation;
                }
            }
        }

        if (_model4 != null)
        {
            var rotation4 = rotation * Matrix4x4.CreateRotationX(_rotationAngle * 0.15f);
            for (int i = 0; i < _model4.Children.Count; i++)
            {
                if (_model4.Children[i] is ModelVisual3D mv && mv.Content != null)
                {
                    mv.Content.Transform = rotation4;
                }
            }
        }

        _viewport1?.Invalidate();
        _viewport2?.Invalidate();
        _viewport3?.Invalidate();
        _viewport4?.Invalidate();
    }

    private static void SetLayoutMode(LayoutMode3D mode)
    {
        _currentLayoutMode = mode;
        
        // Update layout button backgrounds to show selected state
        if (_layoutBtnQuad != null) _layoutBtnQuad.Background = new ThemeResourceBrush(mode == LayoutMode3D.Quad ? "ControlBackgroundPressed" : "ControlBackgroundHover");
        if (_layoutBtnTop != null) _layoutBtnTop.Background = new ThemeResourceBrush(mode == LayoutMode3D.Top ? "ControlBackgroundPressed" : "ControlBackgroundHover");
        if (_layoutBtnFront != null) _layoutBtnFront.Background = new ThemeResourceBrush(mode == LayoutMode3D.Front ? "ControlBackgroundPressed" : "ControlBackgroundHover");
        if (_layoutBtnRight != null) _layoutBtnRight.Background = new ThemeResourceBrush(mode == LayoutMode3D.Right ? "ControlBackgroundPressed" : "ControlBackgroundHover");
        if (_layoutBtnPersp != null) _layoutBtnPersp.Background = new ThemeResourceBrush(mode == LayoutMode3D.Perspective ? "ControlBackgroundPressed" : "ControlBackgroundHover");

        // Update the retained vector maximize/restore glyphs in place.
        SetViewportIconState(_maxBtn1, mode == LayoutMode3D.Top);
        SetViewportIconState(_maxBtn2, mode == LayoutMode3D.Front);
        SetViewportIconState(_maxBtn3, mode == LayoutMode3D.Right);
        SetViewportIconState(_maxBtn4, mode == LayoutMode3D.Perspective);

        if (_card1 == null || _card2 == null || _card3 == null || _card4 == null || 
            _colSplitter == null || _rowSplitterLeft == null || _rowSplitterRight == null ||
            _leftGrid == null || _rightGrid == null || _viewportsGrid == null)
            return;

        // 1. Reset visibility of all components first
        _leftGrid.Visibility = Visibility.Visible;
        _rightGrid.Visibility = Visibility.Visible;
        _colSplitter.Visibility = Visibility.Visible;

        _card1.Visibility = Visibility.Visible;
        _card2.Visibility = Visibility.Visible;
        _card3.Visibility = Visibility.Visible;
        _card4.Visibility = Visibility.Visible;

        _rowSplitterLeft.Visibility = Visibility.Visible;
        _rowSplitterRight.Visibility = Visibility.Visible;

        // 2. Clear definitions and rebuild to standard resizable quad layout
        _viewportsGrid.RowDefinitions.Clear();
        _viewportsGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        _viewportsGrid.ColumnDefinitions.Clear();
        _viewportsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        _viewportsGrid.ColumnDefinitions.Add(new GridLength(6f, GridUnitType.Absolute));
        _viewportsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        _leftGrid.RowDefinitions.Clear();
        _leftGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        _leftGrid.RowDefinitions.Add(new GridLength(6f, GridUnitType.Absolute));
        _leftGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        _rightGrid.RowDefinitions.Clear();
        _rightGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        _rightGrid.RowDefinitions.Add(new GridLength(6f, GridUnitType.Absolute));
        _rightGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        // 3. Reset standard column/row assignments
        Grid.SetColumn(_leftGrid, 0); Grid.SetRow(_leftGrid, 0);
        Grid.SetColumn(_rightGrid, 2); Grid.SetRow(_rightGrid, 0);
        Grid.SetColumn(_colSplitter, 1); Grid.SetRow(_colSplitter, 0);

        Grid.SetColumn(_card1, 0); Grid.SetRow(_card1, 0);
        Grid.SetColumn(_rowSplitterLeft, 0); Grid.SetRow(_rowSplitterLeft, 1);
        Grid.SetColumn(_card3, 0); Grid.SetRow(_card3, 2);

        Grid.SetColumn(_card2, 0); Grid.SetRow(_card2, 0);
        Grid.SetColumn(_rowSplitterRight, 0); Grid.SetRow(_rowSplitterRight, 1);
        Grid.SetColumn(_card4, 0); Grid.SetRow(_card4, 2);

        // 4. Apply layout modifications based on maximized selection
        switch (mode)
        {
            case LayoutMode3D.Quad:
                break;
            case LayoutMode3D.Top:
                _rightGrid.Visibility = Visibility.Collapsed;
                _colSplitter.Visibility = Visibility.Collapsed;
                _rowSplitterLeft.Visibility = Visibility.Collapsed;
                _card3.Visibility = Visibility.Collapsed;
                
                // Set leftGrid to take entire main grid width
                _viewportsGrid.ColumnDefinitions.Clear();
                _viewportsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
                
                // Set card1 to take entire height inside leftGrid
                _leftGrid.RowDefinitions.Clear();
                _leftGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
                break;

            case LayoutMode3D.Front:
                _leftGrid.Visibility = Visibility.Collapsed;
                _colSplitter.Visibility = Visibility.Collapsed;
                _rowSplitterRight.Visibility = Visibility.Collapsed;
                _card4.Visibility = Visibility.Collapsed;
                
                // Set rightGrid to take entire main grid width and move it to Col 0
                _viewportsGrid.ColumnDefinitions.Clear();
                _viewportsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
                Grid.SetColumn(_rightGrid, 0);
                
                // Set card2 to take entire height inside rightGrid
                _rightGrid.RowDefinitions.Clear();
                _rightGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
                break;

            case LayoutMode3D.Right:
                _rightGrid.Visibility = Visibility.Collapsed;
                _colSplitter.Visibility = Visibility.Collapsed;
                _rowSplitterLeft.Visibility = Visibility.Collapsed;
                _card1.Visibility = Visibility.Collapsed;
                
                // Set leftGrid to take entire main grid width
                _viewportsGrid.ColumnDefinitions.Clear();
                _viewportsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
                
                // Move card3 to Row 0 to fill leftGrid
                _leftGrid.RowDefinitions.Clear();
                _leftGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
                Grid.SetRow(_card3, 0);
                break;

            case LayoutMode3D.Perspective:
                _leftGrid.Visibility = Visibility.Collapsed;
                _colSplitter.Visibility = Visibility.Collapsed;
                _rowSplitterRight.Visibility = Visibility.Collapsed;
                _card2.Visibility = Visibility.Collapsed;
                
                // Set rightGrid to take entire main grid width and move it to Col 0
                _viewportsGrid.ColumnDefinitions.Clear();
                _viewportsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
                Grid.SetColumn(_rightGrid, 0);
                
                // Move card4 to Row 0 to fill rightGrid
                _rightGrid.RowDefinitions.Clear();
                _rightGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
                Grid.SetRow(_card4, 0);
                break;
        }

        // Force all views to redraw
        _viewportsGrid.Invalidate();
        _viewportsGrid.InvalidateMeasure();
        _leftGrid.Invalidate();
        _leftGrid.InvalidateMeasure();
        _rightGrid.Invalidate();
        _rightGrid.InvalidateMeasure();

        _viewport1?.Invalidate();
        _viewport2?.Invalidate();
        _viewport3?.Invalidate();
        _viewport4?.Invalidate();
    }

    private static void SetViewportIconState(Button? button, bool isRestore)
    {
        if (button?.Content is ViewportLayoutIcon icon)
        {
            icon.IsRestore = isRestore;
        }
    }

    private sealed class ViewportLayoutIcon : FrameworkElement
    {
        private bool _isRestore;

        public bool IsRestore
        {
            get => _isRestore;
            set
            {
                if (_isRestore == value) return;
                _isRestore = value;
                Invalidate();
            }
        }

        public ViewportLayoutIcon()
        {
            WidthConstraint = 14f;
            HeightConstraint = 14f;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize) => new(14f, 14f);

        public override void OnRender(DrawingContext context)
        {
            var pen = ThemeManager.GetPen("TextPrimary", 1.25f);
            if (_isRestore)
            {
                context.DrawLine(pen, new Vector2(9.5f, 4.5f), new Vector2(4.5f, 9.5f));
                context.DrawLine(pen, new Vector2(4.5f, 9.5f), new Vector2(4.5f, 6f));
                context.DrawLine(pen, new Vector2(4.5f, 9.5f), new Vector2(8f, 9.5f));
            }
            else
            {
                context.DrawLine(pen, new Vector2(4.5f, 9.5f), new Vector2(9.5f, 4.5f));
                context.DrawLine(pen, new Vector2(9.5f, 4.5f), new Vector2(6f, 4.5f));
                context.DrawLine(pen, new Vector2(9.5f, 4.5f), new Vector2(9.5f, 8f));
            }
        }
    }

    private static Button CreateLayoutButton(string label, LayoutMode3D mode)
    {
        var btn = new Button
        {
            HeightConstraint = 28f,
            CornerRadius = 4f,
            Margin = new Thickness(2f),
            Padding = new Thickness(0),
            Background = new ThemeResourceBrush(mode == LayoutMode3D.Quad ? "ControlBackgroundPressed" : "ControlBackgroundHover")
        };
        var textRun = new Run(label) { FontSize = 10f, Foreground = new ThemeResourceBrush("TextPrimary") };
        btn.Content = new RichTextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Inlines = { new Bold(textRun) } };
        btn.Click += (s, e) => { SetLayoutMode(mode); };
        return btn;
    }
}
