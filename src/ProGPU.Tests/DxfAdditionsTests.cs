using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ProGPU.Dxf;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public class DxfAdditionsTests
{
    [Fact]
    public void AcisSatParser_ParseSampleSat_ReconstructsEdgesCorrectly()
    {
        // A minimal, valid ACIS SAT textual block containing vertex, point, and edge definitions
        string sampleSat = @"ACIS 20800 0 1 0
body $-1 $-1 $-1 $-1
lump $-1 $-1 $-1 $-1
shell $-1 $-1 $-1 $-1
face $-1 $-1 $-1 $-1
loop $-1 $-1 $-1 $-1
coedge $-1 $-1 $-1 $-1
edge $-1 $-1 $7 $8 $11
vertex $-1 $9
vertex $-1 $10
point $-1 0.0 0.0 0.0
point $-1 10.0 5.0 3.0
straight $-1
End of ACIS Solid";

        var edges = AcisSatParser.ParseSat(sampleSat);

        Assert.Single(edges);
        var edge = edges[0];
        Assert.Equal(new Vector3(0f, 0f, 0f), edge.StartPoint);
        Assert.Equal(new Vector3(10f, 5f, 3f), edge.EndPoint);
        Assert.Equal("straight", edge.CurveType.ToLowerInvariant());
    }

    [Fact]
    public void AcisSabParser_ParseSampleSab_ReconstructsEdgesCorrectly()
    {
        // Construct a manual binary SAB stream with standard tags
        var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms))
        {
            // Write a helper to write tagged string
            void WriteTaggedString(string s)
            {
                writer.Write((byte)0x03); // Short string tag
                writer.Write((byte)s.Length);
                writer.Write(System.Text.Encoding.ASCII.GetBytes(s));
            }

            // Write a helper to write tagged double
            void WriteTaggedDouble(double d)
            {
                writer.Write((byte)0x02); // Double tag
                writer.Write(d);
            }

            // Write a helper to write tagged pointer
            void WriteTaggedPointer(int p)
            {
                writer.Write((byte)0x0C); // Pointer tag
                writer.Write(p);
            }

            // 0: body $-1 $-1 $-1 $-1
            WriteTaggedString("body");
            WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1);

            // 1: lump $-1 $-1 $-1 $-1
            WriteTaggedString("lump");
            WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1);

            // 2: shell $-1 $-1 $-1 $-1
            WriteTaggedString("shell");
            WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1);

            // 3: face $-1 $-1 $-1 $-1
            WriteTaggedString("face");
            WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1);

            // 4: loop $-1 $-1 $-1 $-1
            WriteTaggedString("loop");
            WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1);

            // 5: coedge $-1 $-1 $-1 $-1
            WriteTaggedString("coedge");
            WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(-1);

            // 6: edge $-1 $-1 $7 $8 $11
            WriteTaggedString("edge");
            WriteTaggedPointer(-1); WriteTaggedPointer(-1); WriteTaggedPointer(7); WriteTaggedPointer(8); WriteTaggedPointer(11);

            // 7: vertex $-1 $9
            WriteTaggedString("vertex");
            WriteTaggedPointer(-1); WriteTaggedPointer(9);

            // 8: vertex $-1 $10
            WriteTaggedString("vertex");
            WriteTaggedPointer(-1); WriteTaggedPointer(10);

            // 9: point $-1 0.0 0.0 0.0
            WriteTaggedString("point");
            WriteTaggedPointer(-1); WriteTaggedDouble(0.0); WriteTaggedDouble(0.0); WriteTaggedDouble(0.0);

            // 10: point $-1 10.0 5.0 3.0
            WriteTaggedString("point");
            WriteTaggedPointer(-1); WriteTaggedDouble(10.0); WriteTaggedDouble(5.0); WriteTaggedDouble(3.0);

            // 11: straight $-1
            WriteTaggedString("straight");
            WriteTaggedPointer(-1);
        }

        byte[] sabBytes = ms.ToArray();
        var edges = AcisSabParser.ParseSab(sabBytes);

        Assert.Single(edges);
        var edge = edges[0];
        Assert.Equal(new Vector3(0f, 0f, 0f), edge.StartPoint);
        Assert.Equal(new Vector3(10f, 5f, 3f), edge.EndPoint);
        Assert.Equal("straight", edge.CurveType.ToLowerInvariant());
    }

    [Fact]
    public void AcisSabParser_ParseTaglessSab_ReconstructsEdgesCorrectly()
    {
        // Construct a manual binary SAB stream WITHOUT standard tags (direct raw format)
        var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms))
        {
            // Write a helper to write raw length-prefixed string
            void WriteRawString(string s)
            {
                writer.Write((byte)s.Length);
                writer.Write(System.Text.Encoding.ASCII.GetBytes(s));
            }

            // 0: body $-1 $-1 $-1 $-1 (4 pointers)
            WriteRawString("body");
            writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);

            // 1: lump $-1 $-1 $-1 $-1 (4 pointers)
            WriteRawString("lump");
            writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);

            // 2: shell $-1 $-1 $-1 $-1 (4 pointers)
            WriteRawString("shell");
            writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);

            // 3: face $-1 $-1 $-1 $-1 (4 pointers)
            WriteRawString("face");
            writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);

            // 4: loop $-1 $-1 $-1 $-1 (4 pointers)
            WriteRawString("loop");
            writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);

            // 5: coedge $-1 $-1 $-1 $-1 (4 pointers)
            WriteRawString("coedge");
            writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);

            // 6: edge $-1 $-1 $7 $8 $11 (5 pointers)
            WriteRawString("edge");
            writer.Write(-1); writer.Write(-1); writer.Write(7); writer.Write(8); writer.Write(11);

            // 7: vertex $-1 $9 (2 pointers)
            WriteRawString("vertex");
            writer.Write(-1); writer.Write(9);

            // 8: vertex $-1 $10 (2 pointers)
            WriteRawString("vertex");
            writer.Write(-1); writer.Write(10);

            // 9: point $-1 0.0 0.0 0.0 (1 pointer, 3 doubles)
            WriteRawString("point");
            writer.Write(-1); writer.Write(0.0); writer.Write(0.0); writer.Write(0.0);

            // 10: point $-1 10.0 5.0 3.0 (1 pointer, 3 doubles)
            WriteRawString("point");
            writer.Write(-1); writer.Write(10.0); writer.Write(5.0); writer.Write(3.0);

            // 11: straight $-1 (1 pointer)
            WriteRawString("straight");
            writer.Write(-1);
        }

        byte[] sabBytes = ms.ToArray();
        var edges = AcisSabParser.ParseSab(sabBytes);

        Assert.Single(edges);
        var edge = edges[0];
        Assert.Equal(new Vector3(0f, 0f, 0f), edge.StartPoint);
        Assert.Equal(new Vector3(10f, 5f, 3f), edge.EndPoint);
        Assert.Equal("straight", edge.CurveType.ToLowerInvariant());
    }

    [Fact]
    public void SplineEvaluation_RationalBSpline_ProducesWeightedInterpolation()
    {
        var drawingContext = new ProGPU.Scene.DrawingContext();
        var pen = new Pen(new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)), 2f);
        
        var controlPoints = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(5f, 10f),
            new Vector2(10f, 0f)
        };
        var knots = new double[] { 0, 0, 0, 1, 1, 1 };
        var weights = new double[] { 1.0, 2.0, 1.0 }; // Weighted NURBS

        // Should not throw and successfully record the command
        drawingContext.DrawSpline(pen, controlPoints, knots, weights, 2, false);

        Assert.Single(drawingContext.Commands);
        var cmd = drawingContext.Commands[0];
        Assert.Equal(ProGPU.Scene.RenderCommandType.DrawSpline, cmd.Type);
        Assert.NotNull(cmd.SplineWeights);
        Assert.Equal(2.0, cmd.SplineWeights[1]);
    }

    [Fact]
    public void DrawLine3D_AddsCommandCorrectly()
    {
        var drawingContext = new ProGPU.Scene.DrawingContext();
        var pen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 1.5f);
        var p1 = new Vector3(10f, 20f, 30f);
        var p2 = new Vector3(40f, 50f, 60f);

        drawingContext.DrawLine3D(pen, p1, p2);

        Assert.Single(drawingContext.Commands);
        var cmd = drawingContext.Commands[0];
        Assert.Equal(ProGPU.Scene.RenderCommandType.DrawLine3D, cmd.Type);
        Assert.Equal(p1, cmd.Position3D1);
        Assert.Equal(p2, cmd.Position3D2);
        Assert.NotNull(cmd.Pen);
        Assert.Equal(1.5f, cmd.Pen.Thickness);
    }

    [Fact]
    public void CrossHatchBrush_Creation_And_Properties()
    {
        var color = new Vector4(0f, 1f, 0f, 1f);
        var brush = new CrossHatchBrush(45f, 10f, 2f, color);

        Assert.Equal(45f, brush.Angle);
        Assert.Equal(10f, brush.Spacing);
        Assert.Equal(2f, brush.Thickness);
        Assert.Equal(color, brush.Color);
    }

    [Fact]
    public void DxfRenderContext_MLeaderScanning_CachesCorrectly()
    {
        // Construct a mock DXF stream with a MULTILEADER block
        string mockDxf = @"  0
SECTION
  2
ENTITIES
  0
MULTILEADER
  8
test-layer
300
CONTEXT_DATA{
 10
100.0
 20
200.0
 30
300.0
140
4.5
304
{\fArial;Hello MLeader}
301
}
302
LEADER{
304
LEADER_LINE{
 10
1.0
 20
2.0
 30
3.0
 10
10.0
 20
20.0
 30
30.0
305
}
303
}
  0
ENDSEC
  0
EOF";

        string tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, mockDxf);

        try
        {
            var ctx = new DxfRenderContext(new ProGPU.Scene.DrawingContext(), null!);
            ctx.FilePath = tempPath;

            Assert.Single(ctx.CachedMLeaders);
            var mleader = ctx.CachedMLeaders[0];
            Assert.Equal("test-layer", mleader.Layer);
            Assert.Equal(new Vector3(100f, 200f, 300f), mleader.TextInsertionPoint);
            Assert.Equal(4.5f, mleader.TextHeight);
            Assert.Equal(@"{\fArial;Hello MLeader}", mleader.TextValue);
            
            Assert.Single(mleader.LeaderLines);
            var line = mleader.LeaderLines[0];
            Assert.Equal(2, line.Count);
            Assert.Equal(new Vector3(1f, 2f, 3f), line[0]);
            Assert.Equal(new Vector3(10f, 20f, 30f), line[1]);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void DxfRenderContext_AcisSabHexScanning_CachesCorrectly()
    {
        // 1. Create binary SAB data
        var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms))
        {
            void WriteRawString(string s)
            {
                writer.Write((byte)s.Length);
                writer.Write(System.Text.Encoding.ASCII.GetBytes(s));
            }
            // Minimal body -> lump -> shell -> face -> loop -> coedge -> edge -> vertex -> point -> straight
            WriteRawString("body"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("lump"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("shell"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("face"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("loop"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("coedge"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("edge"); writer.Write(-1); writer.Write(-1); writer.Write(7); writer.Write(8); writer.Write(11);
            WriteRawString("vertex"); writer.Write(-1); writer.Write(9);
            WriteRawString("vertex"); writer.Write(-1); writer.Write(10);
            WriteRawString("point"); writer.Write(-1); writer.Write(0.0); writer.Write(0.0); writer.Write(0.0);
            WriteRawString("point"); writer.Write(-1); writer.Write(5.0); writer.Write(6.0); writer.Write(7.0);
            WriteRawString("straight"); writer.Write(-1);
        }

        byte[] sabBytes = ms.ToArray();
        string hex = BitConverter.ToString(sabBytes).Replace("-", "");

        // 2. Wrap inside DXF format with 3DSOLID entity and group code 310
        string mockDxf = $@"  0
SECTION
  2
ENTITIES
  0
3DSOLID
  8
0
310
{hex}
  0
ENDSEC
  0
EOF";

        string tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, mockDxf);

        try
        {
            var ctx = new DxfRenderContext(new ProGPU.Scene.DrawingContext(), null!);
            ctx.FilePath = tempPath;

            Assert.Single(ctx.Cached3dSolids);
            var solid = ctx.Cached3dSolids[0];
            Assert.Single(solid.Edges);
            var edge = solid.Edges[0];
            Assert.Equal(new Vector3(0f, 0f, 0f), edge.StartPoint);
            Assert.Equal(new Vector3(5f, 6f, 7f), edge.EndPoint);
            Assert.Equal("straight", edge.CurveType.ToLowerInvariant());
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void CalculateBounds_WithMLeaderAnd3dSolid_ComputesCorrectBounds()
    {
        // 1. Create binary SAB data for a 3D Solid
        var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms))
        {
            void WriteRawString(string s)
            {
                writer.Write((byte)s.Length);
                writer.Write(System.Text.Encoding.ASCII.GetBytes(s));
            }
            WriteRawString("body"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("lump"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("shell"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("face"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("loop"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("coedge"); writer.Write(-1); writer.Write(-1); writer.Write(-1); writer.Write(-1);
            WriteRawString("edge"); writer.Write(-1); writer.Write(-1); writer.Write(7); writer.Write(8); writer.Write(11);
            WriteRawString("vertex"); writer.Write(-1); writer.Write(9);
            WriteRawString("vertex"); writer.Write(-1); writer.Write(10);
            WriteRawString("point"); writer.Write(-1); writer.Write(10.0); writer.Write(20.0); writer.Write(30.0);
            WriteRawString("point"); writer.Write(-1); writer.Write(50.0); writer.Write(60.0); writer.Write(70.0);
            WriteRawString("straight"); writer.Write(-1);
        }

        byte[] sabBytes = ms.ToArray();
        string hex = BitConverter.ToString(sabBytes).Replace("-", "");

        // 2. Combine MULTILEADER and 3DSOLID into one mock DXF
        string mockDxf = $@"  0
SECTION
  2
ENTITIES
  0
MULTILEADER
  8
0
300
CONTEXT_DATA{{
 10
100.0
 20
200.0
 30
300.0
140
4.5
304
Hello MLeader
301
}}
302
LEADER{{
304
LEADER_LINE{{
 10
5.0
 20
15.0
 30
25.0
 10
15.0
 20
25.0
 30
35.0
305
}}
303
}}
  0
3DSOLID
  8
0
310
{hex}
  0
ENDSEC
  0
EOF";

        string tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, mockDxf);

        try
        {
            var doc = new netDxf.DxfDocument();
            var ctx = new DxfRenderContext(new ProGPU.Scene.DrawingContext(), null!);
            ctx.FilePath = tempPath;

            // Let's set a non-identity current transform to verify correct transform application!
            // E.g. translation by (10, 20, 0)
            ctx.PushTransform(Matrix4x4.CreateTranslation(10f, 20f, 0f));

            var (min, max) = DxfDocumentRenderer.CalculateBounds(doc, ctx);

            // With translation (10, 20):
            // Multileader TextInsertionPoint: (100, 200, 300) -> transformed (110, 220, 300)
            // Multileader LeaderLines:
            //   (5, 15, 25) -> transformed (15, 35, 25)
            //   (15, 25, 35) -> transformed (25, 45, 35)
            // 3D Solid points:
            //   (10, 20, 30) -> transformed (20, 40, 30)
            //   (50, 60, 70) -> transformed (60, 80, 70)
            //
            // Overall min/max bounds:
            // X min = 15, X max = 110
            // Y min = 35, Y max = 220

            Assert.Equal(15f, min.X, 1);
            Assert.Equal(35f, min.Y, 1);
            Assert.Equal(110f, max.X, 1);
            Assert.Equal(220f, max.Y, 1);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void DrawingContext_DrawHatch_CreatesCorrectCommand()
    {
        var drawingContext = new ProGPU.Scene.DrawingContext();
        var brush = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f));
        var boundaries = new PathGeometry();
        var figure = new PathFigure(new Vector2(0f, 0f), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(100f, 0f)));
        figure.Segments.Add(new LineSegment(new Vector2(100f, 100f)));
        figure.Segments.Add(new LineSegment(new Vector2(0f, 100f)));
        boundaries.Figures.Add(figure);

        drawingContext.DrawHatch(brush, boundaries);

        Assert.Single(drawingContext.Commands);
        var cmd = drawingContext.Commands[0];
        Assert.Equal(ProGPU.Scene.RenderCommandType.DrawHatch, cmd.Type);
        Assert.Equal(brush, cmd.Brush);
        Assert.Equal(boundaries, cmd.Path);
    }

    [Fact]
    public void DrawingContext_DrawAcisSolid_CreatesCorrectCommand()
    {
        var drawingContext = new ProGPU.Scene.DrawingContext();
        var brush = new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f));
        var pen = new Pen(brush, 2.0f);
        var edges = new List<ProGPU.Scene.Line3D>
        {
            new ProGPU.Scene.Line3D(new Vector3(0f, 0f, 0f), new Vector3(10f, 20f, 30f)),
            new ProGPU.Scene.Line3D(new Vector3(10f, 20f, 30f), new Vector3(40f, 50f, 60f))
        };
        var matrix = Matrix4x4.Identity;

        drawingContext.DrawAcisSolid(pen, edges, matrix);

        Assert.Single(drawingContext.Commands);
        var cmd = drawingContext.Commands[0];
        Assert.Equal(ProGPU.Scene.RenderCommandType.DrawAcisSolid, cmd.Type);
        Assert.Equal(pen, cmd.Pen);
        Assert.Equal(edges, cmd.Edges3D);
        Assert.Equal(matrix, cmd.Transform);
    }

    [Fact]
    public void DxfDocumentRenderer_Render_UsesPreFlattenedEntities()
    {
        var doc = new netDxf.DxfDocument();
        var line = new netDxf.Entities.Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(100, 200, 0));
        doc.AddEntity(line);

        var drawingContext = new ProGPU.Scene.DrawingContext();
        var ctx = new DxfRenderContext(drawingContext, null!);

        // Pre-populate layer in active layers
        ctx.ActiveLayers.Clear();
        ctx.ActiveLayers.Add("0");

        DxfDocumentRenderer.Render(doc, ctx);

        Assert.NotEmpty(ctx.FlatWcsEntities);
        Assert.Single(ctx.FlatWcsEntities);
        var flat = ctx.FlatWcsEntities[0];
        Assert.Equal(line, flat.Entity);
        Assert.Equal(Matrix4x4.Identity, flat.Transform);

        // Ensure it rendered the line command
        Assert.NotEmpty(drawingContext.Commands);
    }

    [Fact]
    public void DxfDocumentRenderer_Render_LargeDxfFile()
    {
        string path = "/Users/wieslawsoltes/Downloads/dwg/dxf/Schemat IOS Karvina CZ.dxf";
        if (!File.Exists(path)) return;

        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!File.Exists(fontPath)) fontPath = "Arial.ttf";
        var font = File.Exists(fontPath) ? new ProGPU.Text.TtfFont(fontPath) : null!;

        var doc = netDxf.DxfDocument.Load(path);
        var drawingContext = new ProGPU.Scene.DrawingContext();
        var ctx = new DxfRenderContext(drawingContext, font);

        ctx.ActiveLayers.Clear();
        foreach (var l in doc.Layers) ctx.ActiveLayers.Add(l.Name);

        DxfDocumentRenderer.Render(doc, ctx);

        Console.WriteLine($"[Diagnostic] Large DXF rendering output:");
        Console.WriteLine($"  ActiveLayout: '{doc.ActiveLayout}'");
        Console.WriteLine($"  FlatWcsEntities.Count: {ctx.FlatWcsEntities.Count}");
        Console.WriteLine($"  DrawingContext commands count: {drawingContext.Commands.Count}");
        
        Assert.NotEmpty(ctx.FlatWcsEntities);
        Assert.NotEmpty(drawingContext.Commands);
    }

    [Fact]
    public void DxfRenderContext_DocumentPropertyChange_InvalidatesFlatteningCache()
    {
        var ctx = new DxfRenderContext(new ProGPU.Scene.DrawingContext(), null!);
        var doc1 = new netDxf.DxfDocument();
        var doc2 = new netDxf.DxfDocument();

        // Add some dummy lines to doc1 to produce a flat list
        doc1.AddEntity(new netDxf.Entities.Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(10, 10, 0)));
        doc1.AddEntity(new netDxf.Entities.Line(new netDxf.Vector3(10, 10, 0), new netDxf.Vector3(20, 20, 0)));

        // Flatten doc1
        ctx.Document = doc1;
        ctx.FlattenDxfEntities(doc1);
        Assert.NotEmpty(ctx.FlatWcsEntities);

        // Change Document to doc2
        ctx.Document = doc2;
        
        // The cache must be automatically cleared!
        Assert.Empty(ctx.FlatWcsEntities);
        Assert.Null(ctx.CachedActiveLayout);
    }

    [Fact]
    public void DxfGpuTransforms_DiagnosticMatrix()
    {
        string path = "/Users/wieslawsoltes/Downloads/dwg/dxf/Schemat IOS Karvina CZ.dxf";
        if (!File.Exists(path)) return;

        var doc = netDxf.DxfDocument.Load(path);
        var drawingContext = new ProGPU.Scene.DrawingContext();
        var ctx = new DxfRenderContext(drawingContext, null!);
        ctx.EnableGpuTransforms = true;

        ctx.ActiveLayers.Clear();
        foreach (var l in doc.Layers) ctx.ActiveLayers.Add(l.Name);

        // Compute bounds
        var (min, max) = DxfDocumentRenderer.CalculateBounds(doc, ctx);
        float dxfWidth = max.X - min.X;
        float dxfHeight = max.Y - min.Y;

        ctx.Center = (min + max) * 0.5f;
        
        var Size = new Vector2(1676f, 960f); // Typical screen/layout size in the screenshot
        ctx.ScreenCenter = Size * 0.5f;

        float padding = 0.88f;
        float scaleX = (Size.X * padding) / dxfWidth;
        float scaleY = (Size.Y * padding) / dxfHeight;
        ctx.Zoom = Math.Min(scaleX, scaleY);
        ctx.Pan = Vector2.Zero;

        // Build View Matrix (CPU-side View Matrix for GPU Transforms)
        var viewMatrix = new Matrix4x4(
            ctx.Zoom, 0f, 0f, 0f,
            0f, -ctx.Zoom, 0f, 0f,
            0f, 0f, 1f, 0f,
            -ctx.Center.X * ctx.Zoom + ctx.ScreenCenter.X + ctx.Pan.X,
            ctx.Center.Y * ctx.Zoom + ctx.ScreenCenter.Y + ctx.Pan.Y,
            0f, 1f
        );

        // Let's project min and max bounds to verify their screen coordinates
        var screenMin = Vector2.Transform(min, viewMatrix);
        var screenMax = Vector2.Transform(max, viewMatrix);

        Console.WriteLine($"[Diagnostic] dxfWidth={dxfWidth}, dxfHeight={dxfHeight}, Zoom={ctx.Zoom}");
        Console.WriteLine($"[Diagnostic] Center={ctx.Center}, ScreenCenter={ctx.ScreenCenter}");
        Console.WriteLine($"[Diagnostic] min={min} -> screenMin={screenMin}");
        Console.WriteLine($"[Diagnostic] max={max} -> screenMax={screenMax}");

        // The screen coordinate of min and max should be perfectly centered on the 1676x960 layout!
        // X screen coordinates should be roughly in the [Size.X * 0.06, Size.X * 0.94] range
        Assert.True(screenMin.X >= 0f && screenMin.X <= Size.X);
        Assert.True(screenMax.X >= 0f && screenMax.X <= Size.X);
        Assert.True(screenMin.Y >= 0f && screenMin.Y <= Size.Y);
        Assert.True(screenMax.Y >= 0f && screenMax.Y <= Size.Y);
    }

    [Fact]
    public void DxfDocumentRenderer_Flattening_RespectsInsertLayerVisibility()
    {
        var doc = new netDxf.DxfDocument();
        var insertLayer = new netDxf.Tables.Layer("LayerInsert");
        var lineLayer = new netDxf.Tables.Layer("LayerLine");
        doc.Layers.Add(insertLayer);
        doc.Layers.Add(lineLayer);

        var block = new netDxf.Blocks.Block("TestBlock");
        var line = new netDxf.Entities.Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(100, 100, 0))
        {
            Layer = lineLayer
        };
        block.Entities.Add(line);
        doc.Blocks.Add(block);

        var insert = new netDxf.Entities.Insert(block, new netDxf.Vector3(0, 0, 0))
        {
            Layer = insertLayer
        };
        doc.AddEntity(insert);

        var drawingContext = new ProGPU.Scene.DrawingContext();
        var ctx = new DxfRenderContext(drawingContext, null!);

        // Scenario 1: Only line layer is active, insert layer is hidden
        ctx.ActiveLayers.Clear();
        ctx.ActiveLayers.Add("LayerLine");

        DxfDocumentRenderer.Render(doc, ctx);

        // Flattening is enabled by default, flat entities should exist
        Assert.NotEmpty(ctx.FlatWcsEntities);
        // But since the parent insert layer is inactive, nothing should be drawn!
        Assert.Empty(drawingContext.Commands);

        // Scenario 2: Active layers include both line layer and insert layer
        var drawingContext2 = new ProGPU.Scene.DrawingContext();
        var ctx2 = new DxfRenderContext(drawingContext2, null!);
        ctx2.ActiveLayers.Add("LayerLine");
        ctx2.ActiveLayers.Add("LayerInsert");

        DxfDocumentRenderer.Render(doc, ctx2);

        // Now it should render the line segment successfully!
        Assert.NotEmpty(drawingContext2.Commands);
    }

    [Fact]
    public void DxfShaders_WGSL_StaticLine3DTransforms_Check()
    {
        var shaderText = ProGPU.Backend.Shaders.VectorShader;
        
        // Assert that the static line 3D projection is correctly implemented inside sType == 8u branch
        Assert.Contains("else if (isStatic) {", shaderText);
        Assert.Contains("pos3D = (uniforms.mvp * vec4<f32>(local3D, 1.0)).xyz;", shaderText);
    }
}


