using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ProGPU.Samples.Suntrail.Game;

/// <summary>
/// Original readers of the documented Tiled map contracts, plus Suntrail v1.
/// Bounded import parsing; tile decoding/coalescing costs are documented in LevelFiles.Tiles.cs.
/// Files are capped at 1 MiB and compiled levels at 256 objects; no reflection,
/// file references, code execution, or parsing during simulation/rendering.
/// Unsupported geometry fails transactionally instead of changing collision rules.
/// </summary>
public static partial class LevelFiles
{
    private static readonly string[] Kinds = ["ground", "ledge", "moving", "crate", "pipe", "stone", "coin", "relic", "enemy", "hazard", "checkpoint", "spawn", "exit", "saw", "flame", "crusher"];
    public static string KindName(LevelObjectKind kind) => Kinds[(int)kind];
    private static LevelObjectKind ParseKind(string text)
    {
        int index = Array.IndexOf(Kinds, text.ToLowerInvariant());
        return index >= 0 ? (LevelObjectKind)index : throw new FormatException($"Unsupported object class '{text}'. Assign a Suntrail gameplay class before importing.");
    }

    public static LevelDocument Read(ReadOnlyMemory<byte> bytes, string fileName)
    {
        if (bytes.Length > LevelDocument.MaximumBytes) throw new FormatException("Level files must be at most 1 MiB.");
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".json" or ".suntrail" => ReadJson(bytes, Path.GetFileNameWithoutExtension(fileName)),
                ".tmx" => ReadTmx(bytes, Path.GetFileNameWithoutExtension(fileName)),
                ".nes" => throw new FormatException("NES cartridge level decoding is not available yet. This loader currently accepts Suntrail and Tiled maps."),
                ".lvl" or ".lvlx" => throw new FormatException("SMBX level decoding is not available yet. This loader currently accepts Suntrail and Tiled maps."),
                _ => throw new FormatException("Choose a Suntrail .suntrail, Tiled .json or .tmx map.")
            };
        }
        catch (Exception e) when (e is JsonException or XmlException or InvalidOperationException or OverflowException or KeyNotFoundException or IOException)
        { throw new FormatException("The level file is malformed: " + e.Message, e); }
    }

    private static LevelDocument ReadJson(ReadOnlyMemory<byte> bytes, string fallbackName)
    {
        using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        var root = json.RootElement;
        var items = new List<LevelObject>();
        bool native = Text(root, "format") == "suntrail";
        if (native)
        {
            if (Number(root, "version") != 1) throw new FormatException("Unsupported Suntrail document version.");
            foreach (var item in root.GetProperty("objects").EnumerateArray()) ReadObject(item, default, true, items);
        }
        else
        {
            if (Text(root, "type") != "map" || Text(root, "orientation") != "orthogonal" || Flag(root, "infinite"))
                throw new FormatException("Only finite orthogonal Tiled maps are supported.");
            ReadLayers(root.GetProperty("layers"), default, items, 0, new TiledTiles(root));
        }
        return new(native ? Text(root, "name") : PropertyText(root, "suntrail.name", fallbackName),
            Integer(native ? Number(root, "biome") : PropertyNumber(root, "suntrail.biome")), items.ToArray());
    }

    private static void ReadLayers(JsonElement layers, System.Numerics.Vector2 offset, List<LevelObject> items, int depth, TiledTiles tiles)
    {
        if (depth > 16) throw new FormatException("Tiled groups may be nested at most 16 levels.");
        foreach (var layer in layers.EnumerateArray())
        {
            var position = offset + new System.Numerics.Vector2(Number(layer, "offsetx"), Number(layer, "offsety"));
            string type = Text(layer, "type");
            if (type == "group") ReadLayers(layer.GetProperty("layers"), position, items, depth + 1, tiles);
            else if (type == "objectgroup")
                foreach (var item in layer.GetProperty("objects").EnumerateArray()) ReadObject(item, position, false, items);
            else if (type == "tilelayer") tiles.Read(layer, position, items);
            else throw new FormatException($"Layer '{Text(layer, "name")}' uses {type}. Image layers are not supported yet.");
        }
    }

    private static void ReadObject(JsonElement item, System.Numerics.Vector2 offset, bool native, List<LevelObject> items)
    {
        if (!native && (Number(item, "rotation") != 0 || Flag(item, "ellipse") || Flag(item, "capsule") || item.TryGetProperty("polygon", out _) ||
            item.TryGetProperty("polyline", out _) || item.TryGetProperty("gid", out _) || item.TryGetProperty("template", out _) || item.TryGetProperty("text", out _)))
            throw new FormatException("Tiled objects must be unrotated rectangles or points without templates or tile images.");
        string kind = native ? Text(item, "kind") : Text(item, "type", Text(item, "class"));
        Add(items, new(ParseKind(kind), new(Number(item, "x") + offset.X, Number(item, "y") + offset.Y,
            Number(item, "width"), Number(item, "height")),
            native ? Number(item, "travel") : PropertyNumber(item, "travel"),
            native ? Number(item, "phase") : PropertyNumber(item, "phase"),
            native ? Number(item, "verticalTravel") : PropertyNumber(item, "verticalTravel")));
    }

    private static LevelDocument ReadTmx(ReadOnlyMemory<byte> bytes, string fallbackName)
    {
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = LevelDocument.MaximumBytes, IgnoreComments = true });
        var root = XDocument.Load(reader).Root ?? throw new FormatException("Missing TMX map.");
        if (root.Name != "map" || Attribute(root, "orientation") != "orthogonal" || Attribute(root, "infinite", "0") != "0")
            throw new FormatException("Only finite orthogonal Tiled maps are supported.");
        var items = new List<LevelObject>();
        ReadXmlLayers(root, default, items, 0, new TiledTiles(root));
        return new(XmlProperty(root, "suntrail.name", fallbackName), Integer(ParseNumber(XmlProperty(root, "suntrail.biome", "0"))), items.ToArray());
    }

    private static void ReadXmlLayers(XElement parent, System.Numerics.Vector2 offset, List<LevelObject> items, int depth, TiledTiles tiles)
    {
        if (depth > 16) throw new FormatException("Tiled groups may be nested at most 16 levels.");
        foreach (var layer in parent.Elements())
        {
            if (layer.Name == "properties" || layer.Name == "tileset") continue;
            var position = offset + new System.Numerics.Vector2(XmlNumber(layer, "offsetx"), XmlNumber(layer, "offsety"));
            if (layer.Name == "group") { ReadXmlLayers(layer, position, items, depth + 1, tiles); continue; }
            if (layer.Name == "layer") { tiles.Read(layer, position, items); continue; }
            if (layer.Name != "objectgroup") throw new FormatException($"TMX layer '{Attribute(layer, "name")}' uses unsupported {layer.Name} geometry.");
            foreach (var item in layer.Elements("object"))
            {
                if (XmlNumber(item, "rotation") != 0 || item.Attribute("gid") is not null || item.Attribute("template") is not null ||
                    item.Elements().Any(e => e.Name != "properties" && e.Name != "point"))
                    throw new FormatException("TMX objects must be unrotated rectangles or points without templates or tile images.");
                Add(items, new(ParseKind(Attribute(item, "type", Attribute(item, "class"))),
                    new(XmlNumber(item, "x") + position.X, XmlNumber(item, "y") + position.Y, XmlNumber(item, "width"), XmlNumber(item, "height")),
                    ParseNumber(XmlProperty(item, "travel", "0")), ParseNumber(XmlProperty(item, "phase", "0")), ParseNumber(XmlProperty(item, "verticalTravel", "0"))));
            }
        }
    }

    private static void Add(List<LevelObject> items, LevelObject item)
    {
        if (items.Count == LevelDocument.MaximumObjects) throw new FormatException("This level contains too many objects.");
        items.Add(item);
    }
    private static int Integer(float value) => value == MathF.Truncate(value) ? checked((int)value) : throw new FormatException("An integer value is required.");
    private static string Text(JsonElement e, string key, string fallback = "") => e.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
    private static float Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.GetSingle() : 0;
    private static bool Flag(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.GetBoolean();
    private static JsonElement Property(JsonElement e, string key)
    {
        if (e.TryGetProperty("properties", out var properties))
            foreach (var p in properties.EnumerateArray()) if (Text(p, "name") == key) return p.GetProperty("value");
        return default;
    }
    private static float PropertyNumber(JsonElement e, string key) { var p = Property(e, key); return p.ValueKind == JsonValueKind.Undefined ? 0 : p.GetSingle(); }
    private static string PropertyText(JsonElement e, string key, string fallback) { var p = Property(e, key); return p.ValueKind == JsonValueKind.Undefined ? fallback : p.GetString() ?? fallback; }
    private static string Attribute(XElement e, string key, string fallback = "") => (string?)e.Attribute(key) ?? fallback;
    private static float XmlNumber(XElement e, string key) => ParseNumber(Attribute(e, key, "0"));
    private static float ParseNumber(string text) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : throw new FormatException($"Invalid number '{text}'.");
    private static string XmlProperty(XElement e, string key, string fallback) =>
        e.Element("properties")?.Elements("property").FirstOrDefault(p => Attribute(p, "name") == key) is { } p ? Attribute(p, "value", p.Value) : fallback;

    public static byte[] Write(LevelDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("format", "suntrail"); writer.WriteNumber("version", 1);
            writer.WriteString("name", document.Name); writer.WriteNumber("biome", document.Biome); writer.WriteStartArray("objects");
            foreach (var item in document.Objects)
            {
                writer.WriteStartObject(); writer.WriteString("kind", KindName(item.Kind));
                writer.WriteNumber("x", item.Bounds.X); writer.WriteNumber("y", item.Bounds.Y);
                writer.WriteNumber("width", item.Bounds.Width); writer.WriteNumber("height", item.Bounds.Height);
                writer.WriteNumber("travel", item.Travel); writer.WriteNumber("phase", item.Phase); writer.WriteNumber("verticalTravel", item.VerticalTravel);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
