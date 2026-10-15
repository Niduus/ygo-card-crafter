using CardCrafter.Core.Compiler;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Tests;

public class CatalogTests
{
    [Fact]
    public void Generator_AvoidsUsedBlocks()
    {
        var used = new List<long>();
        var rng = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            var id = PasscodeGenerator.Generate(used, rng);
            Assert.InRange(id, 100_000_000, 999_990_000);
            Assert.Equal(0, id % 10_000);
            Assert.DoesNotContain(used, u => u >= id && u < id + 10_000);
            used.Add(id + 5); // something else lives in that block now
        }
    }

    [Theory]
    [InlineData(new[] { "Elemental HERO Neos", "Elemental HERO Avian", "Elemental HERO Burstinatrix" }, "Elemental HERO")]
    [InlineData(new[] { "Blue-Eyes White Dragon", "Blue-Eyes Alternative White Dragon", "Blue-Eyes Chaos MAX Dragon", "The White Stone of Ancients" }, "Blue-Eyes")]
    [InlineData(new[] { "Solo" }, "Solo")]
    public void ArchetypeNames_AreGuessedFromCardNames(string[] names, string expected) =>
        Assert.Equal(expected, OmegaCatalog.GuessName(names));

    [Fact]
    public void Catalog_ReadsPasscodesArchetypesAndCustomTypes()
    {
        using var tmp = new TempDir();
        var db = Path.Combine(tmp.Path, "db");
        CdbFile.WriteFresh(db,
        [
            Row(1, "Star Knight Alpha", 0xa50, CardRace.Warrior),
            Row(2, "Star Knight Beta", 0xa50, CardRace.Warrior),
            Row(3, "Cyber Gizmo", 0, (CardRace)0x8000000),
        ]);
        var cat = OmegaCatalog.Load([db], tmp.Path);
        Assert.Equal("Star Knight Beta (db)", cat.Clash(2));
        Assert.Null(cat.Clash(4));
        var arch = Assert.Single(cat.Archetypes);
        Assert.Equal(("Star Knight", 0xa50, 2), (arch.Name, arch.Code, arch.Cards));
        var custom = Assert.Single(cat.UnknownTypes);
        Assert.Equal(0x8000000, custom.Value);
        Assert.NotEqual(0xa50, cat.FreeArchetypeCode([]) & 0xfff);
    }

    private static CdbRow Row(long id, string name, int setcode, CardRace race) =>
        CdbEncoder.Encode(new CardData { Id = id, Name = name, Archetypes = setcode == 0 ? [] : [setcode], Race = race }, [], 3);

    [Fact]
    public void CustomType_CompilesAsHexAndReadsByName()
    {
        var pack = new CardPack { CustomTypes = [new CustomType { Value = 0x8000000, Name = "Cyborg" }] };
        var p = Samples.PotOfGreedClone();
        p.Effects[0].Do = [new BlockInstance("do.add_to_hand").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = (CardRace)0x8000000 })];
        pack.Cards.Add(p);
        var lua = ScriptCompiler.Compile(p, pack).Lua;
        Assert.Contains("c:IsRace(0x8000000)", lua);
        Assert.Contains("Cyborg monster", EffectTextGenerator.Generate(p, pack));
        Assert.Equal("[Cyborg/Effect]", CardFaceText.TypeLine(new CardData { Race = (CardRace)0x8000000 }, pack));
    }

    [Theory]
    [InlineData(MonsterFrame.Synchro, MonsterAbility.Tuner, "[Dragon/Synchro/Tuner/Effect]")]
    [InlineData(MonsterFrame.Normal, MonsterAbility.None, "[Dragon]")]
    [InlineData(MonsterFrame.Normal, MonsterAbility.Pendulum, "[Dragon/Pendulum/Normal]")]
    [InlineData(MonsterFrame.Effect, MonsterAbility.Flip, "[Dragon/Flip/Effect]")]
    public void TypeLine_MatchesRealCards(MonsterFrame frame, MonsterAbility abilities, string expected) =>
        Assert.Equal(expected, CardFaceText.TypeLine(new CardData { Race = CardRace.Dragon, Frame = frame, Abilities = abilities }, null));

    [Fact]
    public void CustomType_RoundTripsThroughPackJson()
    {
        var pack = new CardPack { CustomTypes = [new CustomType { Value = 0x8000000, Name = "Cyborg" }] };
        pack.Cards.Add(new CardProject { Card = new CardData { Race = (CardRace)0x8000000 } });
        var back = PackSerializer.FromJson(PackSerializer.ToJson(pack));
        Assert.Equal((CardRace)0x8000000, back.Cards[0].Card.Race);
        Assert.Equal("Cyborg", back.CustomTypes[0].Name);
    }
}

public class OddDatabaseTests
{
    /// <summary>A database like other tools write: NULLs, numbers stored as text, names stored as bytes.</summary>
    [Fact]
    public void Catalog_ReadsDatabasesWithNullsAndOddTypes()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE datas(id integer primary key, ot integer, alias integer, setcode integer, type integer,
                    atk integer, def integer, level integer, race integer, attribute integer, category integer);
                CREATE TABLE texts(id integer primary key, name text, desc text,
                    str1 text, str2 text, str3 text, str4 text, str5 text, str6 text, str7 text, str8 text,
                    str9 text, str10 text, str11 text, str12 text, str13 text, str14 text, str15 text, str16 text);
                INSERT INTO datas VALUES(10, NULL, NULL, '0x1f', 33, '1800', NULL, 4, 1, 32, NULL);
                INSERT INTO datas VALUES(11, 3, 0, 31, 33, 1000, 1000, 4, 1, 32, 0);
                INSERT INTO datas VALUES(12, 3, 0, 0, 33, 1000, 1000, 4, 1, 32, 0);
                INSERT INTO texts(id, name, desc) VALUES(10, 'Neo Something', NULL);
                INSERT INTO texts(id, name, desc) VALUES(11, CAST('Neo Other' AS BLOB), 'x');
                """;
            cmd.ExecuteNonQuery();
        }
        Assert.True(CdbFile.IsCardDatabase(path, out var n) && n == 3);
        var rows = CdbFile.Read(path);
        Assert.Equal(3, rows.Count);
        Assert.Equal(0x1f, rows[0].Setcode);
        Assert.Equal(1800, rows[0].Atk);
        Assert.Equal("Neo Other", rows[1].Name);
        Assert.Equal("", rows[2].Name);

        var cat = CardCrafter.Core.Omega.OmegaCatalog.Load([path], tmp.Path);
        Assert.Empty(cat.Problems);
        Assert.Equal(3, cat.Passcodes.Count);
        Assert.Contains(cat.Archetypes, a => a.Code == 0x1f && a.Cards == 2);
    }

    [Fact]
    public void Catalog_SkipsUnreadableFilesWithoutThrowing()
    {
        using var tmp = new TempDir();
        var bad = Path.Combine(tmp.Path, "broken.db");
        File.WriteAllText(bad, "SQLite format 3\0 but not really");
        var cat = CardCrafter.Core.Omega.OmegaCatalog.Load([bad], tmp.Path);
        Assert.Single(cat.Problems);
    }
}

public class ArchetypeNameGuessTests
{
    [Fact]
    public void QuotedNames_InCardTexts_NameTheArchetype()
    {
        var quoted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        OmegaCatalog.AddQuoted("You can add 1 \"Mitsurugi\" monster from your Deck to your hand, except \"Ame no Murakumo no Mitsurugi\".", quoted);
        OmegaCatalog.AddQuoted("Add 1 \"Dominus\" card. Set 1 \"Dominators\" or \"Dominus Purge\" Trap.", quoted);
        Assert.Equal("Mitsurugi", OmegaCatalog.GuessName(["Ame no Murakumo no Mitsurugi", "Futsu no Mitama no Mitsurugi", "Mitsurugi Ritual", "Mitsurugi no Mikoto, Saji"], quoted));
        Assert.Equal("Dominus", OmegaCatalog.GuessName(["Dominus Spark", "Dominus Impulse", "Dominus Purge", "Dominusteer Rosepamela"], quoted));
        Assert.Equal("Dominus Purge", OmegaCatalog.GuessName(["Dominus Purge"], quoted));
        Assert.Equal("Dominators", OmegaCatalog.GuessName(["Songs of the Dominators"], quoted));
    }

    /// <summary>With a real card database (CARDCRAFTER_CARDS_CDB), well-known archetypes get their names.</summary>
    [Fact]
    public void RealDatabase_NamesArchetypes()
    {
        if (Environment.GetEnvironmentVariable("CARDCRAFTER_CARDS_CDB") is not { } cdb || !File.Exists(cdb)) return;
        using var tmp = new TempDir();
        var copy = Path.Combine(tmp.Path, "cards.cdb");
        File.Copy(cdb, copy);
        var catalog = OmegaCatalog.Load([copy], tmp.Path);
        string Name(int code) => catalog.Archetypes.First(a => a.Code == code).Name;
        Assert.Equal("Mitsurugi", Name(0x1c3));
        Assert.Equal("Dominus", Name(0x1c6));
        Assert.Equal("Blue-Eyes", Name(0xdd));
        if (Environment.GetEnvironmentVariable("CARDCRAFTER_ARCHETYPE_REPORT") is { } report)
            File.WriteAllLines(report, catalog.Archetypes.OrderBy(a => a.Code).Select(a => $"0x{a.Code:x}\t{a.Name}\t{string.Join(" | ", a.Examples)}"));
    }
}
