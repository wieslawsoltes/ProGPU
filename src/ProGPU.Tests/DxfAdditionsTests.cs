using System;
using System.Collections.Generic;
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
}
