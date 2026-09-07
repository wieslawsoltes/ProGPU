using System.Numerics;
using System.Text;
using ProGPU.Samples.Suntrail.Game;
using Xunit;

namespace ProGPU.Samples.Suntrail.Tests;

public sealed class LevelEditorTests
{
    [Fact]
    public void DragIsOneUndoTransactionAndCancellationRestoresStart()
    {
        var editor = new LevelEditor(LevelDocument.CreateStarter());
        var original = editor.Objects[3];
        editor.BeginDrag(3);
        for (int i = 0; i < 100; i++) editor.MoveSelected(new(i * 2, -i));
        Assert.False(editor.CanUndo);
        editor.CommitDrag(); Assert.True(editor.CanUndo);
        var moved = editor.Objects[3]; Assert.NotEqual(original, moved);
        editor.Undo(); Assert.Equal(original, editor.Objects[3]); Assert.False(editor.CanUndo);
        editor.Redo(); Assert.Equal(moved, editor.Objects[3]);
        editor.BeginDrag(3); editor.MoveSelected(new(400, 50)); editor.CancelDrag();
        Assert.Equal(moved, editor.Objects[3]);
    }

    [Fact]
    public void NewEditDropsRedoAndSpawnToolRelocatesOneMarker()
    {
        var editor = new LevelEditor(LevelDocument.CreateStarter());
        editor.Add(LevelObjectKind.Spawn, new(320, 550));
        Assert.Single(editor.Objects, o => o.Kind == LevelObjectKind.Spawn);
        editor.Undo(); editor.Add(LevelObjectKind.Coin, new(350, 420));
        Assert.False(editor.CanRedo); Assert.Equal(8, editor.Objects.Count);
        editor.DeleteSelected(); Assert.Equal(7, editor.Objects.Count);
        editor.Undo(); Assert.Equal(8, editor.Objects.Count);
    }

    [Fact]
    public void InvalidDraftCannotPlayButRemainsUndoable()
    {
        var editor = new LevelEditor(LevelDocument.CreateStarter());
        editor.Select(0); editor.DeleteSelected();
        Assert.Throws<FormatException>(() => editor.Snapshot());
        editor.Undo(); Assert.NotNull(editor.Snapshot());
    }

    [Fact]
    public void PlaytestUsesSeparateMutableStateAndNeverUnlocksCampaign()
    {
        var document = LevelDocument.CreateStarter();
        var bytes = LevelFiles.Write(document);
        var session = new GameSession(); session.StartDocument(document);
        for (int i = 0; i < 2400 && session.Mode == GameMode.Playing; i++) session.Step(RoutePilot.GetInput(session));
        Assert.Equal(GameMode.Complete, session.Mode); Assert.Equal(0, session.Deaths);
        Assert.Equal(0, session.UnlockedLevel); Assert.Equal(bytes, LevelFiles.Write(document));
        session.Continue(); Assert.Same(document, session.Level.Document);
        Assert.All(session.Level.Pickups, p => Assert.False(p.Collected));
    }

    [Fact]
    public void NativeRoundtripIsDeterministicAndOwnsItsSnapshot()
    {
        var items = LevelDocument.CreateStarter().Objects.ToArray();
        var doc = new LevelDocument("A & B's trail", 6, items);
        items[0] = default;
        var encoded = LevelFiles.Write(doc);
        var parsed = LevelFiles.Read(encoded, "trail.suntrail");
        Assert.Equal(doc.Name, parsed.Name); Assert.Equal(doc.Biome, parsed.Biome);
        Assert.Equal(doc.Objects.ToArray(), parsed.Objects.ToArray());
        Assert.Equal(encoded, LevelFiles.Write(parsed));
    }

    // Independently authored fixtures from the public Tiled field definitions.
    private const string JsonMap = """
        {"type":"map","orientation":"orthogonal","infinite":false,
         "properties":[{"name":"suntrail.name","type":"string","value":"Fixture trail"},{"name":"suntrail.biome","type":"int","value":2}],
         "layers":[{"type":"group","offsetx":32,"offsety":16,"layers":[{"type":"objectgroup","offsetx":16,"objects":[
           {"type":"spawn","x":92,"y":536,"point":true},
           {"type":"ground","x":0,"y":584,"width":900,"height":500},
           {"class":"moving","x":300,"y":480,"width":150,"height":24,"properties":[{"name":"travel","type":"float","value":64}]},
           {"type":"exit","x":752,"y":584,"point":true}
         ]}]}]}
        """;
    private const string XmlMap = """
        <map orientation="orthogonal" infinite="0">
          <properties><property name="suntrail.name" value="Fixture trail"/><property name="suntrail.biome" type="int" value="2"/></properties>
          <group offsetx="32" offsety="16"><objectgroup offsetx="16">
            <object type="spawn" x="92" y="536"><point/></object>
            <object type="ground" x="0" y="584" width="900" height="500"/>
            <object class="moving" x="300" y="480" width="150" height="24"><properties><property name="travel" type="float" value="64"/></properties></object>
            <object type="exit" x="752" y="584"><point/></object>
          </objectgroup></group>
        </map>
        """;
    [Fact]
    public void TiledXmlAndJsonAgreeOnPropertiesClassesAndNestedOffsets()
    {
        var json = LevelFiles.Read(Encoding.UTF8.GetBytes(JsonMap), "fixture.json");
        var xml = LevelFiles.Read(Encoding.UTF8.GetBytes(XmlMap), "fixture.tmx");
        Assert.Equal(LevelFiles.Write(json), LevelFiles.Write(xml));
        Assert.Equal(new Vector2(140, 552), json.CreateLevel().Spawn);
        Assert.Equal(64, json.Objects[2].Travel);
        var session = new GameSession(); session.StartDocument(json);
        for (int i = 0; i < 1000 && session.Mode == GameMode.Playing; i++) session.Step(RoutePilot.GetInput(session));
        Assert.Equal(GameMode.Complete, session.Mode);
    }
    [Theory]
    [InlineData("{", "broken.json")]
    [InlineData("{}", "missing.json")]
    [InlineData("{}", "rom.nes")]
    [InlineData("{}", "level.lvlx")]
    [InlineData("<!DOCTYPE map [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><map>&x;</map>", "entity.tmx")]
    public void MalformedOrUnimplementedFilesFailExplicitly(string data, string name) =>
        Assert.Throws<FormatException>(() => LevelFiles.Read(Encoding.UTF8.GetBytes(data), name));

    [Theory]
    [InlineData("\"type\":\"moving\"", "\"type\":\"goomba\"")]
    [InlineData("\"class\":\"moving\"", "\"class\":\"moving\",\"rotation\":45")]
    [InlineData("\"infinite\":false", "\"infinite\":true")]
    [InlineData("\"type\":\"objectgroup\"", "\"type\":\"tilelayer\"")]
    public void UnsupportedTiledSemanticsAreNeverSilentlyDropped(string oldText, string replacement)
    {
        // First fixture uses the alternate class field for the moving platform.
        string input = JsonMap.Replace("\"class\":\"moving\"", "\"type\":\"moving\"");
        if (oldText.Contains("class")) input = JsonMap;
        Assert.NotEqual(input, input.Replace(oldText, replacement));
        Assert.Throws<FormatException>(() => LevelFiles.Read(Encoding.UTF8.GetBytes(input.Replace(oldText, replacement)), "fixture.json"));
    }
    [Fact]
    public void BoundsAndCountLimitsRejectHostileMapsBeforeGameplay()
    {
        Assert.Throws<FormatException>(() => LevelFiles.Read(new byte[LevelDocument.MaximumBytes + 1], "large.json"));
        var items = LevelDocument.CreateStarter().Objects.ToArray();
        items[1] = items[1] with { Bounds = new(float.NaN, 600, 900, 500) };
        Assert.Throws<FormatException>(() => new LevelDocument("Bad", 0, items));
        Assert.Throws<FormatException>(() => new LevelDocument("Huge", 0, new LevelObject[257]));
    }
}
