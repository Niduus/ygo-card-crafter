using CardCrafter.Core.Compiler;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("cardcrafter-").FullName;
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class DatabaseTests
{
    [Fact]
    public void Monster_RoundTripsThroughCdb()
    {
        using var tmp = new TempDir();
        var card = new CardData
        {
            Id = 100000050, Name = "Round Trip", Text = "Some text.", Kind = CardKind.Monster, Frame = MonsterFrame.Synchro,
            Abilities = MonsterAbility.Tuner, Attribute = CardAttribute.Wind, Race = CardRace.WingedBeast,
            Level = 7, Atk = 2500, Def = -2, Archetypes = [0x10, 0x2a],
        };
        var path = System.IO.Path.Combine(tmp.Path, "t.cdb");
        CdbFile.WriteFresh(path, [CdbEncoder.Encode(card, ["Effect one"], 3)]);

        var row = Assert.Single(CdbFile.Read(path));
        Assert.Equal(TypeBits.Monster | TypeBits.Synchro | TypeBits.Effect | TypeBits.Tuner, row.Type);
        Assert.Equal(0x2a0010, row.Setcode);
        Assert.Equal("Effect one", row.Strings[0]);
        Assert.Equal("", row.Strings[15]);

        var back = CdbEncoder.Decode(row);
        Assert.Equal(card.Name, back.Name);
        Assert.Equal(card.Frame, back.Frame);
        Assert.Equal(card.Abilities, back.Abilities);
        Assert.Equal(card.Race, back.Race);
        Assert.Equal(card.Attribute, back.Attribute);
        Assert.Equal(-2, back.Def);
        Assert.Equal([0x10, 0x2a], back.Archetypes);
        Assert.True(CdbFile.IsCardDatabase(path, out var n) && n == 1);
    }

    [Fact]
    public void LinkAndPendulum_PackTheirSpecialFields()
    {
        var link = new CardData { Frame = MonsterFrame.Link, LinkMarkers = LinkMarker.BottomLeft | LinkMarker.BottomRight | LinkMarker.Top };
        var row = CdbEncoder.Encode(link, [], 3);
        Assert.Equal(3, row.Level);
        Assert.Equal(0x1 | 0x4 | 0x80, row.Def);

        var pend = new CardData { Frame = MonsterFrame.Effect, Abilities = MonsterAbility.Pendulum, Level = 4, LeftScale = 1, RightScale = 8 };
        var prow = CdbEncoder.Encode(pend, [], 3);
        Assert.Equal((1L << 24) | (8L << 16) | 4, prow.Level);
        var back = CdbEncoder.Decode(prow);
        Assert.Equal((4, 1, 8), (back.Level, back.LeftScale, back.RightScale));
    }

    [Theory]
    [InlineData(SpellKind.QuickPlay, TypeBits.Spell | TypeBits.QuickPlay)]
    [InlineData(SpellKind.Field, TypeBits.Spell | TypeBits.Field)]
    [InlineData(SpellKind.Normal, TypeBits.Spell)]
    public void SpellTypes(SpellKind kind, long expected) =>
        Assert.Equal(expected, CdbEncoder.TypeOf(new CardData { Kind = CardKind.Spell, SpellKind = kind }));

    [Fact]
    public void NormalTunerStaysNormal() =>
        Assert.Equal(TypeBits.Monster | TypeBits.Normal | TypeBits.Tuner,
            CdbEncoder.TypeOf(new CardData { Frame = MonsterFrame.Normal, Abilities = MonsterAbility.Tuner }));

    [Fact]
    public void NonDatabaseFiles_AreNotCardDatabases()
    {
        using var tmp = new TempDir();
        var path = System.IO.Path.Combine(tmp.Path, "x.db");
        File.WriteAllText(path, "not sqlite");
        Assert.False(CdbFile.IsCardDatabase(path, out _));
    }

    [Fact]
    public void Pack_RoundTripsThroughJson()
    {
        var pack = Samples.Pack(Samples.DragonCommander(), Samples.PotOfGreedClone());
        pack.Archetypes.Add(new Archetype { Code = 0x1234, Name = "Commander" });
        var json = PackSerializer.ToJson(pack);
        var back = PackSerializer.FromJson(json);
        Assert.Equal(json, PackSerializer.ToJson(back));
        Assert.Contains("\"race\": \"dragon\"", json);
        Assert.Equal(ScriptCompiler.Compile(pack.Cards[0]).Lua, ScriptCompiler.Compile(back.Cards[0]).Lua);
    }
}
