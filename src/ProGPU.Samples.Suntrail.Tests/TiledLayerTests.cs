using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ProGPU.Samples.Suntrail.Game;
using Xunit;

namespace ProGPU.Samples.Suntrail.Tests;

public sealed class TiledLayerTests
{
    private const int Width = 80, Height = 26;
    private static uint[] Cells()
    {
        var cells = new uint[Width * Height];
        for (int row = 19; row < Height; row++)
            for (int col = 0; col < Width; col++)
                if (col is < 24 or >= 28 and < 52 or >= 56) cells[row * Width + col] = 1;
        cells[19 * Width + 1] |= 0xe000_0000; // Symmetric solid; all orthogonal transforms.
        cells[18 * Width + 4] = 0x1000_0002; // Stale hex flag must not corrupt the local ID.
        cells[18 * Width + 72] = 3;
        cells[17 * Width + 10] = 4;
        cells[15 * Width + 35] = 101;
        return cells;
    }
    private static JsonObject JsonMap(uint[]? cells = null)
    {
        var map = JsonNode.Parse("""
            {"type":"map","orientation":"orthogonal","infinite":false,"width":80,"height":26,"tilewidth":32,"tileheight":32,
            "properties":[{"name":"suntrail.name","value":"Tile crossing"},{"name":"suntrail.biome","value":1}],
            "tilesets":[{"firstgid":1,"tiles":[{"id":0,"type":"ground"},{"id":1,"type":"spawn"},{"id":2,"class":"exit"},{"id":3,"type":"coin"}]},
                        {"firstgid":101,"tiles":[{"id":0,"type":"moving","properties":[{"name":"travel","value":64}]}]}],
            "layers":[{"type":"group","offsetx":32,"offsety":16,"layers":[{"type":"tilelayer","width":80,"height":26,"offsetx":16}]}]}
            """)!.AsObject();
        JsonLayer(map)["data"] = new JsonArray((cells ?? Cells()).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return map;
    }
    private static JsonObject JsonLayer(JsonObject map) => map["layers"]![0]!["layers"]![0]!.AsObject();
    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
    private static XElement XmlMap(uint[] cells, string encoding, string compression)
    {
        var map = XElement.Parse("""
            <map orientation="orthogonal" width="80" height="26" tilewidth="32" tileheight="32">
              <properties><property name="suntrail.name" value="Tile crossing"/><property name="suntrail.biome" type="int" value="1"/></properties>
              <tileset firstgid="1"><tile id="0" type="ground"/><tile id="1" type="spawn"/><tile id="2" class="exit"/><tile id="3" type="coin"/></tileset>
              <tileset firstgid="101"><tile id="0" type="moving"><properties><property name="travel" value="64"/></properties></tile></tileset>
              <group offsetx="32" offsety="16"><layer width="80" height="26" offsetx="16"/></group>
            </map>
            """);
        XElement data;
        if (encoding == "xml") data = new("data", cells.Select(v => new XElement("tile", new XAttribute("gid", v))));
        else data = new("data", new XAttribute("encoding", encoding), encoding == "csv" ? string.Join(",\n", cells) : Encoded(cells, compression));
        if (compression.Length > 0) data.SetAttributeValue("compression", compression);
        map.Element("group")!.Element("layer")!.Add(data);
        return map;
    }
    private static string Encoded(uint[] cells, string compression)
    {
        var bytes = new byte[cells.Length * 4];
        for (int i = 0; i < cells.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), cells[i]);
        if (compression.Length == 0) return Convert.ToBase64String(bytes);
        using var result = new MemoryStream();
        using (Stream compressor = compression == "gzip" ? new GZipStream(result, CompressionLevel.SmallestSize, true) : new ZLibStream(result, CompressionLevel.SmallestSize, true)) compressor.Write(bytes);
        return Convert.ToBase64String(result.ToArray());
    }

    [Theory]
    [InlineData("json", "array", "")]
    [InlineData("json", "base64", "")]
    [InlineData("json", "base64", "gzip")]
    [InlineData("json", "base64", "zlib")]
    [InlineData("tmx", "xml", "")]
    [InlineData("tmx", "csv", "")]
    [InlineData("tmx", "base64", "")]
    [InlineData("tmx", "base64", "gzip")]
    [InlineData("tmx", "base64", "zlib")]
    public void IndependentlyAuthoredTileMapsCompileToPlayableCompactGeometry(string format, string encoding, string compression)
    {
        var cells = Cells(); byte[] bytes;
        if (format == "json")
        {
            var map = JsonMap();
            if (encoding != "array") { JsonLayer(map)["data"] = Encoded(cells, compression); JsonLayer(map)["encoding"] = encoding; JsonLayer(map)["compression"] = compression; }
            bytes = Bytes(map);
        }
        else bytes = Encoding.UTF8.GetBytes(XmlMap(cells, encoding, compression).ToString());
        var document = LevelFiles.Read(bytes, "tile-crossing." + format);
        Assert.Equal("Tile crossing", document.Name); Assert.Equal(1, document.Biome);
        var level = document.CreateLevel();
        Assert.Equal(new Vector2(177, 576), level.Spawn);
        Assert.Equal(new Vector2(2368, 624), level.Exit);
        var ground = level.Platforms.Where(p => p.Kind == PlatformKind.Ground).ToArray();
        Assert.Equal(3, ground.Length);
        Assert.Equal(new Box(48, 624, 768, 224), ground[0].Bounds);
        Assert.Equal(new Box(944, 624, 768, 224), ground[1].Bounds);
        Assert.Equal(new Box(1840, 624, 768, 224), ground[2].Bounds);
        Assert.Equal(64, Assert.Single(level.Platforms, p => p.Kind == PlatformKind.Moving).Travel);
        Assert.Equal(LevelFiles.Write(LevelFiles.Read(Bytes(JsonMap()), "reference.json")), LevelFiles.Write(document));
        var game = new GameSession(); game.StartDocument(document);
        for (int tick = 0; tick < 2400 && game.Mode == GameMode.Playing; tick++) game.Step(RoutePilot.GetInput(game));
        Assert.Equal(GameMode.Complete, game.Mode); Assert.Equal(0, game.Deaths);
        var editor = new LevelEditor(document); editor.SetBiome(4); editor.Undo();
        Assert.Equal(LevelFiles.Write(document), LevelFiles.Write(editor.Snapshot()));
        string artifacts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/suntrail"));
        Directory.CreateDirectory(artifacts);
        File.WriteAllBytes(Path.Combine(artifacts, $"tile-crossing-{encoding}-{compression}.{format}"), bytes);
    }

    [Fact]
    public void RectangleCoalescingPreservesEveryOccupiedCellWithoutOverlap()
    {
        var random = new Random(913);
        for (int sample = 0; sample < 30; sample++)
        {
            var cells = new uint[200];
            for (int i = 0; i < cells.Length; i++) cells[i] = random.Next(3) == 0 ? 1u : 0u;
            var map = JsonMap(cells); map["tilewidth"] = 16; map["tileheight"] = 16;
            JsonLayer(map)["width"] = 20; JsonLayer(map)["height"] = 10;
            map["layers"]!.AsArray().Add(JsonNode.Parse("""{"type":"objectgroup","objects":[{"type":"spawn","x":140,"y":200},{"type":"exit","x":700,"y":400}]}"""));
            var level = LevelFiles.Read(Bytes(map), "random.json").CreateLevel();
            for (int i = 0; i < cells.Length; i++)
            {
                float x = 48 + i % 20 * 16 + 8, y = 16 + i / 20 * 16 + 8;
                int covering = level.Platforms.Count(p => x > p.Bounds.X && x < p.Bounds.Right && y > p.Bounds.Y && y < p.Bounds.Bottom);
                Assert.Equal((int)cells[i], covering);
            }
        }
    }

    [Theory]
    [InlineData(-1)] [InlineData(1)]
    public void CompressedDataMustHaveExactlyTheDeclaredNumberOfCells(int delta)
    {
        var map = JsonMap(); var layer = JsonLayer(map);
        layer["data"] = Encoded(new uint[Width * Height + delta], "gzip"); layer["encoding"] = "base64"; layer["compression"] = "gzip";
        Assert.Throws<FormatException>(() => LevelFiles.Read(Bytes(map), "wrong-length.json"));
    }

    [Fact]
    public void InvalidTileIdsTransformsRangesAndUnboundedDataFailBeforeGameplay()
    {
        void Reject(Action<JsonObject> change)
        {
            var map = JsonMap(); change(map);
            Assert.Throws<FormatException>(() => LevelFiles.Read(Bytes(map), "invalid.json"));
        }
        Reject(m => JsonLayer(m)["data"]![0] = 999u);
        Reject(m => JsonLayer(m)["data"]![18 * Width + 4] = 0x8000_0002u);
        Reject(m => JsonLayer(m)["width"] = int.MaxValue);
        Reject(m => m["tileheight"] = 0);
        Reject(m => m["tilesets"]![0]!["source"] = "external.tsx");
        Reject(m => m["tilesets"]![0]!["tiles"]![0]!["id"] = 101);
        Reject(m => { JsonLayer(m)["data"] = "AAAA"; JsonLayer(m)["encoding"] = "base64"; });
        Reject(m => { JsonLayer(m)["data"] = "AAAA"; JsonLayer(m)["encoding"] = "base64"; JsonLayer(m)["compression"] = "zstd"; });
        Reject(m => JsonLayer(m)["data"]![0] = -1);
        Reject(m => m["tilesets"]![0]!["tiles"]![0]!["objectgroup"] = new JsonObject());
    }
}
