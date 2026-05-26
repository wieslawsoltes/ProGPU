using System.Collections.Generic;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using netDxf.Header;
using Vector2 = netDxf.Vector2;
using Vector3 = netDxf.Vector3;
using Layer = netDxf.Tables.Layer;

namespace ProGPU.Samples;

public static class SampleDxfGenerator
{
    public static DxfDocument GenerateSample()
    {
        var doc = new DxfDocument(DxfVersion.AutoCad2018);

        // 1. Create CAD Layers with distinct vibrant colors
        var layerBorders = new Layer("Borders") { Color = AciColor.Red };
        var layerShapes = new Layer("Shapes") { Color = AciColor.Cyan };
        var layerSplines = new Layer("Splines") { Color = AciColor.Green };
        var layerText = new Layer("TextLabels") { Color = AciColor.Yellow };
        var layerInserts = new Layer("NestedBlocks") { Color = AciColor.Magenta };

        doc.Layers.Add(layerBorders);
        doc.Layers.Add(layerShapes);
        doc.Layers.Add(layerSplines);
        doc.Layers.Add(layerText);
        doc.Layers.Add(layerInserts);

        // 2. Add outer borders (Red)
        doc.AddEntity(new Line(new Vector2(-250, -200), new Vector2(250, -200)) { Layer = layerBorders });
        doc.AddEntity(new Line(new Vector2(250, -200), new Vector2(250, 200)) { Layer = layerBorders });
        doc.AddEntity(new Line(new Vector2(250, 200), new Vector2(-250, 200)) { Layer = layerBorders });
        doc.AddEntity(new Line(new Vector2(-250, 200), new Vector2(-250, -200)) { Layer = layerBorders });

        // 3. Add standard shapes: Circle, Arc, Ellipse (Cyan)
        doc.AddEntity(new Circle(new Vector2(-150, 50), 35) { Layer = layerShapes });
        doc.AddEntity(new Arc(new Vector2(-150, 50), 45, 30, 270) { Layer = layerShapes });
        
        // Ellipse centered at (150, 50), major radius 40, minor radius 16, rotation 15 degrees
        doc.AddEntity(new Ellipse(new Vector2(150, 50), 40, 16) { Rotation = 15, Layer = layerShapes });

        // 4. Add Polylines with arcs (bulges)
        var poly = new LwPolyline
        {
            Layer = layerShapes,
            IsClosed = true
        };
        poly.Vertexes.Add(new LwPolylineVertex(new Vector2(-60, 20), 0));
        poly.Vertexes.Add(new LwPolylineVertex(new Vector2(60, 20), 0.4)); // Concave arc
        poly.Vertexes.Add(new LwPolylineVertex(new Vector2(60, 90), 0));
        poly.Vertexes.Add(new LwPolylineVertex(new Vector2(-60, 90), -0.4)); // Convex arc
        doc.AddEntity(poly);

        // 5. Add B-Splines (Green)
        var fitPoints = new List<Vector3>
        {
            new(-200, -100, 0),
            new(-120, -150, 0),
            new(-50, -60, 0),
            new(50, -140, 0),
            new(120, -70, 0),
            new(200, -120, 0)
        };
        var spline = new Spline(fitPoints) { Layer = layerSplines };
        doc.AddEntity(spline);

        // 6. Add CAD Text and multi-line formatted MText (Yellow)
        doc.AddEntity(new netDxf.Entities.Text("ProGPU.Dxf Vector Engine", new Vector2(-220, 150), 10f) { Layer = layerText });
        doc.AddEntity(new netDxf.Entities.Text("Zero-triangulation GPU composites", new Vector2(-220, 130), 7.5f) { Layer = layerText });

        var mtext = new MText("Vibrant High-Performance CAD Viewer\\PUsing netDxf & .NET 10 SIMD Vectors\\PZoom preserves sharp lines!", new Vector2(-220, -20), 8f)
        {
            Layer = layerText
        };
        doc.AddEntity(mtext);

        // 7. Create a Block Definition ("GearWheel") for insertion
        var block = new Block("GearWheel");
        // Inner circle
        block.Entities.Add(new Circle(Vector2.Zero, 12) { Layer = layerInserts });
        // Center mark
        block.Entities.Add(new Line(new Vector2(-18, 0), new Vector2(18, 0)) { Layer = layerInserts });
        block.Entities.Add(new Line(new Vector2(0, -18), new Vector2(0, 18)) { Layer = layerInserts });
        
        // 8-tooth gear profile via polyline
        var gear = new LwPolyline { IsClosed = true, Layer = layerInserts };
        for (int i = 0; i < 8; i++)
        {
            float angle1 = i * MathF.PI / 4f;
            float angle2 = angle1 + MathF.PI / 8f;

            float rOuter = 24f;
            float rInner = 16f;

            gear.Vertexes.Add(new LwPolylineVertex(new Vector2(MathF.Cos(angle1) * rInner, MathF.Sin(angle1) * rInner)));
            gear.Vertexes.Add(new LwPolylineVertex(new Vector2(MathF.Cos(angle1) * rOuter, MathF.Sin(angle1) * rOuter)));
            gear.Vertexes.Add(new LwPolylineVertex(new Vector2(MathF.Cos(angle2) * rOuter, MathF.Sin(angle2) * rOuter)));
            gear.Vertexes.Add(new LwPolylineVertex(new Vector2(MathF.Cos(angle2) * rInner, MathF.Sin(angle2) * rInner)));
        }
        block.Entities.Add(gear);

        // Register Block to Document
        doc.Blocks.Add(block);

        // 8. Add nested Insert references (Magenta) with translations, scaling, and rotations
        var insert1 = new Insert(block, new Vector2(-150, -50))
        {
            Scale = new Vector3(1.2, 1.2, 1),
            Rotation = 15,
            Layer = layerInserts
        };
        doc.AddEntity(insert1);

        var insert2 = new Insert(block, new Vector2(0, -50))
        {
            Scale = new Vector3(1.0, 1.0, 1),
            Rotation = 45,
            Layer = layerInserts
        };
        doc.AddEntity(insert2);

        var insert3 = new Insert(block, new Vector2(150, -50))
        {
            Scale = new Vector3(0.8, 1.5, 1), // Non-uniform scaling!
            Rotation = -30,
            Layer = layerInserts
        };
        doc.AddEntity(insert3);

        return doc;
    }
}
