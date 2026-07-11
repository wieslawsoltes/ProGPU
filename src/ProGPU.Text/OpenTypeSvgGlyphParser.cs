using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
using ProGPU.Vector;

namespace ProGPU.Text;

internal sealed class OpenTypeSvgGlyphParser
{
    private const int MaxReferenceDepth = 64;

    private readonly ushort _unitsPerEm;
    private readonly Dictionary<string, XElement> _elementsById;
    private readonly HashSet<XElement> _activeReferences = new();
    private readonly List<FontColorLayer> _layers = new();

    private OpenTypeSvgGlyphParser(XDocument document, ushort unitsPerEm)
    {
        _unitsPerEm = unitsPerEm;
        _elementsById = document.Root!
            .DescendantsAndSelf()
            .Select(static element => (Element: element, Id: (string?)element.Attribute("id")))
            .Where(static item => !string.IsNullOrEmpty(item.Id))
            .GroupBy(static item => item.Id!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Element, StringComparer.Ordinal);
    }

    public static List<FontColorLayer>? Parse(string xml, ushort glyphId, ushort unitsPerEm)
    {
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        if (document.Root is null)
        {
            return null;
        }

        var parser = new OpenTypeSvgGlyphParser(document, unitsPerEm);
        return parser.ParseGlyph(glyphId);
    }

    private List<FontColorLayer>? ParseGlyph(ushort glyphId)
    {
        if (!_elementsById.TryGetValue($"glyph{glyphId}", out var glyphElement))
        {
            return null;
        }

        var state = SvgRenderState.Default;
        foreach (var ancestor in glyphElement.Ancestors().Reverse())
        {
            state = ApplyState(ancestor, state);
        }

        RenderElement(glyphElement, state, 0, allowDefinition: true);
        return _layers.Count == 0 ? null : _layers;
    }

    private void RenderElement(
        XElement element,
        SvgRenderState parentState,
        int depth,
        bool allowDefinition = false)
    {
        if (depth > MaxReferenceDepth)
        {
            return;
        }

        var name = element.Name.LocalName;
        if (name == "defs" && !allowDefinition)
        {
            return;
        }

        var state = ApplyState(element, parentState);
        switch (name)
        {
            case "svg":
            case "g":
            case "defs":
                foreach (var child in element.Elements())
                {
                    RenderElement(child, state, depth + 1);
                }
                break;

            case "use":
                RenderUse(element, state, depth + 1);
                break;

            case "path":
            case "circle":
            case "ellipse":
            case "rect":
            case "polygon":
                RenderShape(element, state);
                break;
        }
    }

    private void RenderUse(XElement use, SvgRenderState state, int depth)
    {
        var href = use.Attributes().FirstOrDefault(static attribute => attribute.Name.LocalName == "href")?.Value;
        if (string.IsNullOrEmpty(href) || href[0] != '#' ||
            !_elementsById.TryGetValue(href[1..], out var referenced) ||
            !_activeReferences.Add(referenced))
        {
            return;
        }

        try
        {
            RenderElement(referenced, state, depth, allowDefinition: true);
        }
        finally
        {
            _activeReferences.Remove(referenced);
        }
    }

    private void RenderShape(XElement element, SvgRenderState state)
    {
        PathGeometry? geometry;
        try
        {
            geometry = CreateGeometry(element);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or OverflowException)
        {
            return;
        }

        if (geometry is null || geometry.Figures.Count == 0)
        {
            return;
        }

        if (string.Equals((string?)element.Attribute("fill-rule"), "evenodd", StringComparison.OrdinalIgnoreCase))
        {
            geometry.FillRule = FillRule.EvenOdd;
        }

        geometry = geometry.CreateTransformed(state.Transform);
        var brush = ResolveBrush(state.Fill, state.Opacity * state.FillOpacity, state.Transform);
        if (brush is null)
        {
            return;
        }

        _layers.Add(new FontColorLayer
        {
            Geometry = geometry,
            Brush = brush,
            UsesSvgCoordinates = true
        });
    }

    private static PathGeometry? CreateGeometry(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "path":
                var data = (string?)element.Attribute("d");
                return string.IsNullOrWhiteSpace(data) ? null : PathGeometry.Parse(data);

            case "circle":
                var radius = ReadFloat(element, "r");
                return PrimitivePathGeometry.CreateEllipse(
                    new Vector2(ReadFloat(element, "cx"), ReadFloat(element, "cy")),
                    radius,
                    radius);

            case "ellipse":
                return PrimitivePathGeometry.CreateEllipse(
                    new Vector2(ReadFloat(element, "cx"), ReadFloat(element, "cy")),
                    ReadFloat(element, "rx"),
                    ReadFloat(element, "ry"));

            case "rect":
                return PrimitivePathGeometry.CreateRectangle(
                    ReadFloat(element, "x"),
                    ReadFloat(element, "y"),
                    ReadFloat(element, "width"),
                    ReadFloat(element, "height"));

            case "polygon":
                return CreatePolygon((string?)element.Attribute("points"));

            default:
                return null;
        }
    }

    private static PathGeometry? CreatePolygon(string? pointsText)
    {
        if (string.IsNullOrWhiteSpace(pointsText))
        {
            return null;
        }

        var values = ParseNumberList(pointsText);
        if (values.Count < 6 || values.Count % 2 != 0)
        {
            return null;
        }

        var geometry = new PathGeometry();
        var figure = new PathFigure(new Vector2(values[0], values[1]), isClosed: true);
        for (var index = 2; index < values.Count; index += 2)
        {
            figure.Segments.Add(new LineSegment(new Vector2(values[index], values[index + 1])));
        }
        geometry.Figures.Add(figure);
        return geometry;
    }

    private SvgRenderState ApplyState(XElement element, SvgRenderState parent)
    {
        var localTransform = ParseTransform((string?)element.Attribute("transform"));
        if (element.Name.LocalName == "use")
        {
            var x = ReadFloat(element, "x");
            var y = ReadFloat(element, "y");
            if (x != 0f || y != 0f)
            {
                localTransform = Matrix4x4.CreateTranslation(x, y, 0f) * localTransform;
            }
        }

        var fill = (string?)element.Attribute("fill") ?? parent.Fill;
        var opacity = parent.Opacity * ReadUnitInterval(element, "opacity", 1f);
        var fillOpacity = element.Attribute("fill-opacity") is null
            ? parent.FillOpacity
            : ReadUnitInterval(element, "fill-opacity", parent.FillOpacity);
        return new SvgRenderState(localTransform * parent.Transform, fill, opacity, fillOpacity);
    }

    private Brush? ResolveBrush(string fill, float opacity, Matrix4x4 shapeTransform)
    {
        if (string.Equals(fill, "none", StringComparison.OrdinalIgnoreCase) || opacity <= 0f)
        {
            return null;
        }

        if (TryGetUrlReference(fill, out var id) &&
            _elementsById.TryGetValue(id, out var definition))
        {
            return definition.Name.LocalName switch
            {
                "linearGradient" => CreateLinearGradient(definition, opacity, shapeTransform),
                "radialGradient" => CreateRadialGradient(definition, opacity, shapeTransform),
                _ => null
            };
        }

        return TryParseColor(fill, out var color)
            ? new SolidColorBrush(WithOpacity(color, opacity))
            : null;
    }

    private LinearGradientBrush? CreateLinearGradient(
        XElement gradient,
        float opacity,
        Matrix4x4 shapeTransform)
    {
        var stops = ReadGradientStops(gradient, opacity);
        if (stops.Length == 0)
        {
            return null;
        }

        var transform = ParseTransform((string?)gradient.Attribute("gradientTransform")) * shapeTransform;
        var start = Vector2.Transform(new Vector2(
            ReadCoordinate(gradient, "x1", 0f),
            ReadCoordinate(gradient, "y1", 0f)), transform);
        var end = Vector2.Transform(new Vector2(
            ReadCoordinate(gradient, "x2", _unitsPerEm),
            ReadCoordinate(gradient, "y2", 0f)), transform);
        return new LinearGradientBrush(start, end, stops)
        {
            SpreadMethod = ReadSpreadMethod(gradient)
        };
    }

    private RadialGradientBrush? CreateRadialGradient(
        XElement gradient,
        float opacity,
        Matrix4x4 shapeTransform)
    {
        var stops = ReadGradientStops(gradient, opacity);
        if (stops.Length == 0)
        {
            return null;
        }

        var cx = ReadCoordinate(gradient, "cx", _unitsPerEm * 0.5f);
        var cy = ReadCoordinate(gradient, "cy", _unitsPerEm * 0.5f);
        var radius = ReadCoordinate(gradient, "r", _unitsPerEm * 0.5f);
        var fx = ReadCoordinate(gradient, "fx", cx);
        var fy = ReadCoordinate(gradient, "fy", cy);
        var transform = ParseTransform((string?)gradient.Attribute("gradientTransform")) * shapeTransform;
        var center = Vector2.Transform(new Vector2(cx, cy), transform);
        var origin = Vector2.Transform(new Vector2(fx, fy), transform);
        var radiusXPoint = Vector2.Transform(new Vector2(cx + radius, cy), transform);
        var radiusYPoint = Vector2.Transform(new Vector2(cx, cy + radius), transform);
        return new RadialGradientBrush(
            center,
            origin,
            Vector2.Distance(center, radiusXPoint),
            Vector2.Distance(center, radiusYPoint),
            stops)
        {
            SpreadMethod = ReadSpreadMethod(gradient)
        };
    }

    private static GradientStop[] ReadGradientStops(XElement gradient, float opacity)
    {
        var stops = new List<GradientStop>();
        foreach (var stop in gradient.Elements().Where(static child => child.Name.LocalName == "stop"))
        {
            if (!TryParseColor((string?)stop.Attribute("stop-color") ?? "black", out var color))
            {
                continue;
            }

            var offset = ReadPercentageOrNumber((string?)stop.Attribute("offset"), 0f);
            var stopOpacity = ReadUnitInterval(stop, "stop-opacity", 1f);
            stops.Add(new GradientStop(
                WithOpacity(color, opacity * stopOpacity),
                Math.Clamp(offset, 0f, 1f)));
        }

        return stops.OrderBy(static stop => stop.Offset).ToArray();
    }

    private static GradientSpreadMethod ReadSpreadMethod(XElement gradient) =>
        ((string?)gradient.Attribute("spreadMethod"))?.ToLowerInvariant() switch
        {
            "reflect" => GradientSpreadMethod.Reflect,
            "repeat" => GradientSpreadMethod.Repeat,
            _ => GradientSpreadMethod.Pad
        };

    private float ReadCoordinate(XElement element, string name, float defaultValue)
    {
        var text = (string?)element.Attribute(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return defaultValue;
        }

        text = text.Trim();
        if (text.EndsWith('%') &&
            float.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return percent * 0.01f * _unitsPerEm;
        }

        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    private static float ReadFloat(XElement element, string name, float defaultValue = 0f)
    {
        var text = (string?)element.Attribute(name);
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    private static float ReadUnitInterval(XElement element, string name, float defaultValue)
    {
        var text = (string?)element.Attribute(name);
        return Math.Clamp(ReadPercentageOrNumber(text, defaultValue), 0f, 1f);
    }

    private static float ReadPercentageOrNumber(string? text, float defaultValue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return defaultValue;
        }

        text = text.Trim();
        var scale = 1f;
        if (text.EndsWith('%'))
        {
            text = text[..^1];
            scale = 0.01f;
        }

        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value * scale
            : defaultValue;
    }

    private static bool TryGetUrlReference(string value, out string id)
    {
        id = string.Empty;
        value = value.Trim();
        if (!value.StartsWith("url(#", StringComparison.OrdinalIgnoreCase) || !value.EndsWith(')'))
        {
            return false;
        }

        id = value[5..^1].Trim();
        return id.Length != 0;
    }

    private static bool TryParseColor(string value, out Vector4 color)
    {
        color = default;
        value = value.Trim();
        if (value.StartsWith("var(", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            value = comma >= 0 ? value[(comma + 1)..].TrimEnd(')', ' ') : "black";
        }

        if (value.Length > 1 && value[0] == '#')
        {
            var hex = value.AsSpan(1);
            if (hex.Length is 3 or 4)
            {
                var a = 15;
                if (!TryHexNibble(hex[0], out var r) ||
                    !TryHexNibble(hex[1], out var g) ||
                    !TryHexNibble(hex[2], out var b) ||
                    (hex.Length == 4 && !TryHexNibble(hex[3], out a)))
                {
                    return false;
                }

                color = new Vector4(r / 15f, g / 15f, b / 15f, hex.Length == 4 ? a / 15f : 1f);
                return true;
            }

            if (hex.Length is 6 or 8 &&
                uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            {
                if (hex.Length == 6)
                {
                    packed = (packed << 8) | 0xffu;
                }
                color = new Vector4(
                    ((packed >> 24) & 0xff) / 255f,
                    ((packed >> 16) & 0xff) / 255f,
                    ((packed >> 8) & 0xff) / 255f,
                    (packed & 0xff) / 255f);
                return true;
            }
        }

        color = value.ToLowerInvariant() switch
        {
            "black" or "currentcolor" => new Vector4(0f, 0f, 0f, 1f),
            "white" => new Vector4(1f, 1f, 1f, 1f),
            "red" => new Vector4(1f, 0f, 0f, 1f),
            "green" => new Vector4(0f, 0.5f, 0f, 1f),
            "blue" => new Vector4(0f, 0f, 1f, 1f),
            "transparent" => Vector4.Zero,
            _ => default
        };
        return value.Equals("black", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("currentColor", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("white", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("red", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("green", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("blue", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("transparent", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryHexNibble(char value, out int nibble)
    {
        if (value is >= '0' and <= '9')
        {
            nibble = value - '0';
            return true;
        }
        if (value is >= 'a' and <= 'f')
        {
            nibble = value - 'a' + 10;
            return true;
        }
        if (value is >= 'A' and <= 'F')
        {
            nibble = value - 'A' + 10;
            return true;
        }

        nibble = 0;
        return false;
    }

    private static Vector4 WithOpacity(Vector4 color, float opacity)
    {
        color.W *= Math.Clamp(opacity, 0f, 1f);
        return color;
    }

    private static Matrix4x4 ParseTransform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Matrix4x4.Identity;
        }

        var result = Matrix4x4.Identity;
        var index = 0;
        while (index < text.Length)
        {
            SkipSeparators(text, ref index);
            var nameStart = index;
            while (index < text.Length && char.IsLetter(text[index]))
            {
                index++;
            }
            if (nameStart == index)
            {
                break;
            }

            var name = text[nameStart..index];
            SkipSeparators(text, ref index);
            if (index >= text.Length || text[index++] != '(')
            {
                break;
            }

            var end = text.IndexOf(')', index);
            if (end < 0)
            {
                break;
            }

            var values = ParseNumberList(text[index..end]);
            var transform = CreateTransform(name, values);
            result = transform * result;
            index = end + 1;
        }

        return result;
    }

    private static Matrix4x4 CreateTransform(string name, IReadOnlyList<float> values)
    {
        switch (name.ToLowerInvariant())
        {
            case "matrix" when values.Count >= 6:
                return new Matrix4x4(
                    values[0], values[1], 0f, 0f,
                    values[2], values[3], 0f, 0f,
                    0f, 0f, 1f, 0f,
                    values[4], values[5], 0f, 1f);

            case "translate" when values.Count >= 1:
                return Matrix4x4.CreateTranslation(values[0], values.Count > 1 ? values[1] : 0f, 0f);

            case "scale" when values.Count >= 1:
                return Matrix4x4.CreateScale(values[0], values.Count > 1 ? values[1] : values[0], 1f);

            case "rotate" when values.Count >= 1:
                var radians = values[0] * (MathF.PI / 180f);
                if (values.Count < 3)
                {
                    return Matrix4x4.CreateRotationZ(radians);
                }
                return Matrix4x4.CreateTranslation(-values[1], -values[2], 0f) *
                       Matrix4x4.CreateRotationZ(radians) *
                       Matrix4x4.CreateTranslation(values[1], values[2], 0f);

            case "skewx" when values.Count >= 1:
                var skewX = Matrix4x4.Identity;
                skewX.M21 = MathF.Tan(values[0] * (MathF.PI / 180f));
                return skewX;

            case "skewy" when values.Count >= 1:
                var skewY = Matrix4x4.Identity;
                skewY.M12 = MathF.Tan(values[0] * (MathF.PI / 180f));
                return skewY;

            default:
                return Matrix4x4.Identity;
        }
    }

    private static List<float> ParseNumberList(string text)
    {
        var values = new List<float>();
        var index = 0;
        while (index < text.Length)
        {
            SkipSeparators(text, ref index);
            if (index >= text.Length)
            {
                break;
            }

            var start = index;
            if (text[index] is '+' or '-')
            {
                index++;
            }
            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
            }
            if (index < text.Length && text[index] == '.')
            {
                index++;
                while (index < text.Length && char.IsDigit(text[index]))
                {
                    index++;
                }
            }
            if (index < text.Length && text[index] is 'e' or 'E')
            {
                index++;
                if (index < text.Length && text[index] is '+' or '-')
                {
                    index++;
                }
                while (index < text.Length && char.IsDigit(text[index]))
                {
                    index++;
                }
            }

            if (start == index ||
                !float.TryParse(text.AsSpan(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                break;
            }
            values.Add(value);
        }

        return values;
    }

    private static void SkipSeparators(string text, ref int index)
    {
        while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == ','))
        {
            index++;
        }
    }

    private readonly record struct SvgRenderState(
        Matrix4x4 Transform,
        string Fill,
        float Opacity,
        float FillOpacity)
    {
        public static SvgRenderState Default { get; } =
            new(Matrix4x4.Identity, "black", 1f, 1f);
    }
}
