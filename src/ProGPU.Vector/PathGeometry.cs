using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace ProGPU.Vector;

public abstract class PathSegment
{
}

public class LineSegment : PathSegment
{
    public Vector2 Point { get; set; }

    public LineSegment(Vector2 point)
    {
        Point = point;
    }
}

public class QuadraticBezierSegment : PathSegment
{
    public Vector2 ControlPoint { get; set; }
    public Vector2 Point { get; set; }

    public QuadraticBezierSegment(Vector2 controlPoint, Vector2 point)
    {
        ControlPoint = controlPoint;
        Point = point;
    }
}

public class CubicBezierSegment : PathSegment
{
    public Vector2 ControlPoint1 { get; set; }
    public Vector2 ControlPoint2 { get; set; }
    public Vector2 Point { get; set; }

    public CubicBezierSegment(Vector2 controlPoint1, Vector2 controlPoint2, Vector2 point)
    {
        ControlPoint1 = controlPoint1;
        ControlPoint2 = controlPoint2;
        Point = point;
    }
}

public enum SweepDirection
{
    Counterclockwise = 0,
    Clockwise = 1
}

public class ArcSegment : PathSegment
{
    public Vector2 Point { get; set; }
    public Vector2 Size { get; set; }
    public float RotationAngle { get; set; }
    public bool IsLargeArc { get; set; }
    public SweepDirection SweepDirection { get; set; }

    public ArcSegment(Vector2 point, Vector2 size, float rotationAngle, bool isLargeArc, SweepDirection sweepDirection)
    {
        Point = point;
        Size = size;
        RotationAngle = rotationAngle;
        IsLargeArc = isLargeArc;
        SweepDirection = sweepDirection;
    }
}

public class PathFigure
{
    public Vector2 StartPoint { get; set; }
    public List<PathSegment> Segments { get; } = new();
    public bool IsClosed { get; set; }
    public bool IsFilled { get; set; } = true;

    public PathFigure() { }

    public PathFigure(Vector2 startPoint, bool isClosed = false)
    {
        StartPoint = startPoint;
        IsClosed = isClosed;
    }
}

public class PathGeometry
{
    public List<PathFigure> Figures { get; } = new();

    public bool IsCombined { get; set; }
    public PathGeometry? PathA { get; set; }
    public PathGeometry? PathB { get; set; }
    public int Op { get; set; }

    /// <summary>
    /// Parses SVG path data string (e.g. "M 10,10 L 20,20 C ... Z") into a PathGeometry object.
    /// </summary>
    public static PathGeometry Parse(string svgPathData)
    {
        var geometry = new PathGeometry();
        if (string.IsNullOrWhiteSpace(svgPathData)) return geometry;

        var tokens = Tokenize(svgPathData);
        int index = 0;

        PathFigure? currentFigure = null;
        Vector2 currentPoint = Vector2.Zero;
        Vector2 lastControlPoint = Vector2.Zero;
        char lastCommand = '\0';

        while (index < tokens.Count)
        {
            string token = tokens[index];
            char command = token[0];

            if (char.IsLetter(command))
            {
                index++;
                lastCommand = command;
            }
            else
            {
                // Implicit command (same as last)
                if (lastCommand == '\0')
                {
                    throw new FormatException($"Invalid path data: expected command at token '{token}'");
                }
                command = lastCommand;
            }

            bool isRelative = char.IsLower(command);
            char cmdUpper = char.ToUpperInvariant(command);

            switch (cmdUpper)
            {
                case 'M': // MoveTo
                    {
                        var pt = ReadVector2(tokens, ref index);
                        if (isRelative) pt += currentPoint;
                        
                        currentFigure = new PathFigure(pt);
                        geometry.Figures.Add(currentFigure);
                        currentPoint = pt;
                        lastCommand = isRelative ? 'l' : 'L'; // Subsequent points are lines
                    }
                    break;

                case 'L': // LineTo
                    {
                        var pt = ReadVector2(tokens, ref index);
                        if (isRelative) pt += currentPoint;

                        if (currentFigure == null)
                        {
                            currentFigure = new PathFigure(currentPoint);
                            geometry.Figures.Add(currentFigure);
                        }
                        currentFigure.Segments.Add(new LineSegment(pt));
                        currentPoint = pt;
                    }
                    break;

                case 'H': // Horizontal LineTo
                    {
                        float x = ReadFloat(tokens, ref index);
                        if (isRelative) x += currentPoint.X;

                        var pt = new Vector2(x, currentPoint.Y);
                        if (currentFigure == null)
                        {
                            currentFigure = new PathFigure(currentPoint);
                            geometry.Figures.Add(currentFigure);
                        }
                        currentFigure.Segments.Add(new LineSegment(pt));
                        currentPoint = pt;
                    }
                    break;

                case 'V': // Vertical LineTo
                    {
                        float y = ReadFloat(tokens, ref index);
                        if (isRelative) y += currentPoint.Y;

                        var pt = new Vector2(currentPoint.X, y);
                        if (currentFigure == null)
                        {
                            currentFigure = new PathFigure(currentPoint);
                            geometry.Figures.Add(currentFigure);
                        }
                        currentFigure.Segments.Add(new LineSegment(pt));
                        currentPoint = pt;
                    }
                    break;

                case 'Q': // Quadratic Bezier
                    {
                        var ctrl = ReadVector2(tokens, ref index);
                        var to = ReadVector2(tokens, ref index);
                        if (isRelative)
                        {
                            ctrl += currentPoint;
                            to += currentPoint;
                        }

                        if (currentFigure == null)
                        {
                            currentFigure = new PathFigure(currentPoint);
                            geometry.Figures.Add(currentFigure);
                        }
                        currentFigure.Segments.Add(new QuadraticBezierSegment(ctrl, to));
                        lastControlPoint = ctrl;
                        currentPoint = to;
                    }
                    break;

                case 'C': // Cubic Bezier
                    {
                        var ctrl1 = ReadVector2(tokens, ref index);
                        var ctrl2 = ReadVector2(tokens, ref index);
                        var to = ReadVector2(tokens, ref index);
                        if (isRelative)
                        {
                            ctrl1 += currentPoint;
                            ctrl2 += currentPoint;
                            to += currentPoint;
                        }

                        if (currentFigure == null)
                        {
                            currentFigure = new PathFigure(currentPoint);
                            geometry.Figures.Add(currentFigure);
                        }
                        currentFigure.Segments.Add(new CubicBezierSegment(ctrl1, ctrl2, to));
                        lastControlPoint = ctrl2;
                        currentPoint = to;
                    }
                    break;

                case 'A': // Elliptical Arc
                    {
                        float rx = ReadFloat(tokens, ref index);
                        float ry = ReadFloat(tokens, ref index);
                        float xAxisRotation = ReadFloat(tokens, ref index);
                        float largeArcFlagVal = ReadFloat(tokens, ref index);
                        float sweepFlagVal = ReadFloat(tokens, ref index);
                        var pt = ReadVector2(tokens, ref index);
                        if (isRelative) pt += currentPoint;

                        if (currentFigure == null)
                        {
                            currentFigure = new PathFigure(currentPoint);
                            geometry.Figures.Add(currentFigure);
                        }
                        bool isLargeArc = largeArcFlagVal != 0f;
                        SweepDirection sweepDirection = sweepFlagVal != 0f ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
                        currentFigure.Segments.Add(new ArcSegment(pt, new Vector2(rx, ry), xAxisRotation, isLargeArc, sweepDirection));
                        currentPoint = pt;
                    }
                    break;

                case 'Z': // ClosePath
                    {
                        if (currentFigure != null)
                        {
                            currentFigure.IsClosed = true;
                            currentPoint = currentFigure.StartPoint;
                        }
                    }
                    break;

                default:
                    throw new NotSupportedException($"Command '{command}' is not supported in path data parsing.");
            }
        }

        return geometry;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }

            if (char.IsLetter(c))
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            // Parse numbers (including negative numbers and floats)
            int start = i;
            if (text[i] == '-' || text[i] == '+') i++;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == 'e' || text[i] == 'E' || (i > 0 && (text[i-1] == 'e' || text[i-1] == 'E') && (text[i] == '-' || text[i] == '+'))))
            {
                i++;
            }
            if (i > start)
            {
                tokens.Add(text.Substring(start, i - start));
            }
            else
            {
                i++; // Fallback to avoid infinite loops on invalid chars
            }
        }
        return tokens;
    }

    private static float ReadFloat(List<string> tokens, ref int index)
    {
        if (index >= tokens.Count) throw new FormatException("Missing number in path data");
        float value = float.Parse(tokens[index++], CultureInfo.InvariantCulture);
        return value;
    }

    private static Vector2 ReadVector2(List<string> tokens, ref int index)
    {
        float x = ReadFloat(tokens, ref index);
        float y = ReadFloat(tokens, ref index);
        return new Vector2(x, y);
    }
}
