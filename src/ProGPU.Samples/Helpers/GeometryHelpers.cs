using System;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Samples;

public static class GeometryHelpers
{
    public static PathGeometry CreateGearPath(Vector2 center, float innerRadius, float outerRadius, int teethCount, float toothDepth)
    {
        var path = new PathGeometry();
        var fig = new PathFigure { IsClosed = true, IsFilled = true };

        float angleStep = (float)(Math.PI * 2.0 / teethCount);
        
        for (int i = 0; i < teethCount; i++)
        {
            float angle = i * angleStep;
            
            float a0 = angle;
            float a1 = angle + angleStep * 0.25f;
            float a2 = angle + angleStep * 0.55f;
            float a3 = angle + angleStep * 0.8f;

            Vector2 pt0 = center + new Vector2((float)Math.Cos(a0), (float)Math.Sin(a0)) * innerRadius;
            Vector2 pt1 = center + new Vector2((float)Math.Cos(a1), (float)Math.Sin(a1)) * outerRadius;
            Vector2 pt2 = center + new Vector2((float)Math.Cos(a2), (float)Math.Sin(a2)) * outerRadius;
            Vector2 pt3 = center + new Vector2((float)Math.Cos(a3), (float)Math.Sin(a3)) * innerRadius;

            if (i == 0)
            {
                fig.StartPoint = pt0;
            }
            else
            {
                fig.Segments.Add(new LineSegment(pt0));
            }
            
            fig.Segments.Add(new LineSegment(pt1));
            fig.Segments.Add(new LineSegment(pt2));
            fig.Segments.Add(new LineSegment(pt3));
        }
        
        path.Figures.Add(fig);

        var cutoutFig = new PathFigure { IsClosed = true, IsFilled = true };
        float cutRadius = innerRadius * 0.6f;
        int circleSegments = 32;
        for (int i = 0; i < circleSegments; i++)
        {
            float a = -(float)(i * Math.PI * 2.0 / circleSegments);
            Vector2 pt = center + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * cutRadius;
            
            if (i == 0)
                cutoutFig.StartPoint = pt;
            else
                cutoutFig.Segments.Add(new LineSegment(pt));
        }
        path.Figures.Add(cutoutFig);

        return path;
    }

    public static PathGeometry CreateGearPathWithRotation(Vector2 center, float innerRadius, float outerRadius, int teethCount, float toothDepth, float rotation)
    {
        var path = new PathGeometry();
        var fig = new PathFigure { IsClosed = true, IsFilled = true };

        float angleStep = (float)(Math.PI * 2.0 / teethCount);

        for (int i = 0; i < teethCount; i++)
        {
            float angle = i * angleStep + rotation;

            float a0 = angle;
            float a1 = angle + angleStep * 0.25f;
            float a2 = angle + angleStep * 0.55f;
            float a3 = angle + angleStep * 0.8f;

            Vector2 pt0 = center + new Vector2((float)Math.Cos(a0), (float)Math.Sin(a0)) * innerRadius;
            Vector2 pt1 = center + new Vector2((float)Math.Cos(a1), (float)Math.Sin(a1)) * outerRadius;
            Vector2 pt2 = center + new Vector2((float)Math.Cos(a2), (float)Math.Sin(a2)) * outerRadius;
            Vector2 pt3 = center + new Vector2((float)Math.Cos(a3), (float)Math.Sin(a3)) * innerRadius;

            if (i == 0)
            {
                fig.StartPoint = pt0;
            }
            else
            {
                fig.Segments.Add(new LineSegment(pt0));
            }

            fig.Segments.Add(new LineSegment(pt1));
            fig.Segments.Add(new LineSegment(pt2));
            fig.Segments.Add(new LineSegment(pt3));
        }

        path.Figures.Add(fig);

        var cutoutFig = new PathFigure { IsClosed = true, IsFilled = true };
        float cutRadius = innerRadius * 0.6f;
        int circleSegments = 32;
        for (int i = 0; i < circleSegments; i++)
        {
            float a = -(float)(i * Math.PI * 2.0 / circleSegments) + rotation;
            Vector2 pt = center + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * cutRadius;

            if (i == 0)
                cutoutFig.StartPoint = pt;
            else
                cutoutFig.Segments.Add(new LineSegment(pt));
        }
        path.Figures.Add(cutoutFig);

        return path;
    }
}
