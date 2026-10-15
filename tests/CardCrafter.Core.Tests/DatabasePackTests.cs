using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;

namespace CardCrafter.Core.Tests;

public class DatabasePackTests
{
    /// <summary>A folder laid out like YGO Omega's Files folder.</summary>
    private static (string Db, string Scripts, string Arts) OmegaLayout(TempDir tmp, string name = "My Pack")
    {
        var files = Path.Combine(tmp.Path, "YGO Omega_Data", "Files");
        Directory.CreateDirectory(Path.Combine(files, "Databases"));
        Directory.CreateDirectory(Path.Combine(files, "Scripts"));
        Directory.CreateDirectory(Path.Combine(files, "Arts"));
        return (Path.Combine(files, "Databases", name + ".db"), Path.Combine(files, "Scripts"), Path.Combine(files, "Arts"));
    }

    [Fact]
    public void SavedPack_OpensWithItsEventSheets()
    {
        using var tmp = new TempDir();
        var (db, scripts, _) = OmegaLayout(tmp);
        var dc = Samples.DragonCommander();
        var pack = Samples.Pack(dc);
        pack.Archetypes.Add(new Archetype { Code = 0x1234, Name = "Commanders" });

        var saved = DatabasePack.Save(pack, db, null, Path.Combine(tmp.Path, "backups"));
        Assert.True(saved.Success, saved.Error);
        Assert.True(File.Exists(Path.Combine(scripts, $"c{dc.Card.Id}.lua")));
        Assert.Equal(scripts, saved.ScriptFolder);

        var opened = DatabasePack.Open(db);
        Assert.True(opened.HadCardCrafterData);
        Assert.Equal("Test Pack", opened.Pack.Name);
        Assert.Equal("Commanders", opened.Pack.Archetypes.Single().Name);
        var card = opened.Pack.Cards.Single();
        Assert.False(card.UseCustomLua);
        Assert.Equal(PackSerializer.ToJson(Samples.Pack(dc)), PackSerializer.ToJson(Samples.Pack(card)));
        // The game still sees an ordinary database.
        Assert.Equal(dc.Card.Name, CdbFile.Read(db).Single().Name);
    }

    [Fact]
    public void ForeignDatabase_OpensWithScriptsAndArt_AndSavesBackUnchanged()
    {
        using var tmp = new TempDir();
        var (db, scripts, arts) = OmegaLayout(tmp, "Someone Else");
        var row = new CdbRow
        {
            Id = 123456789, Ot = 4, Type = 0x21 | 0x4000, Atk = 1200, Def = 800, Level = 4, Race = 0x1, Attribute = 0x20,
            Category = 0x80000, Name = "Their Card", Desc = "Their text.", Setcode = 0x00ab,
        };
        row.Strings[0] = "First";
        row.Strings[1] = "Second";
        var normal = new CdbRow { Id = 123456790, Ot = 4, Type = 0x11, Atk = 1000, Def = 1000, Level = 3, Race = 0x1, Attribute = 0x10, Name = "Vanilla", Desc = "Flavor." };
        CdbFile.WriteFresh(db, [row, normal]);
        const string lua = "--hand written\nlocal s,id=c123456789,123456789\nfunction s.initial_effect(c)\nend\n";
        File.WriteAllText(Path.Combine(scripts, "c123456789.lua"), lua);
        File.WriteAllBytes(Path.Combine(arts, "123456789.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);

        var opened = DatabasePack.Open(db);
        Assert.False(opened.HadCardCrafterData);
        Assert.Equal("Someone Else", opened.Pack.Name);
        Assert.Equal(4, opened.Pack.Ot);
        Assert.Equal(1, opened.Scripts);
        Assert.Equal(1, opened.Arts);
        var card = opened.Pack.Cards.First(c => c.Card.Id == 123456789);
        Assert.True(card.UseCustomLua);
        Assert.Equal(lua, card.CustomLua);
        Assert.Equal(["First", "Second"], card.ScriptStrings);
        Assert.Equal(Path.Combine(arts, "123456789.jpg"), card.ArtworkPath);

        var saved = DatabasePack.Save(opened.Pack, db, null, Path.Combine(tmp.Path, "backups"));
        Assert.True(saved.Success, saved.Error);
        Assert.Contains(db, saved.BackedUp);
        Assert.True(File.Exists(Path.Combine(saved.BackupFolder!, "Databases", "Someone Else.db")));

        var after = CdbFile.Read(db);
        Assert.Equal(2, after.Count);
        foreach (var (before, now) in new[] { row, normal }.Zip(after))
        {
            Assert.Equal(before.Id, now.Id);
            Assert.Equal(before.Ot, now.Ot);
            Assert.Equal(before.Type, now.Type);
            Assert.Equal(before.Category, now.Category);
            Assert.Equal(before.Setcode, now.Setcode);
            Assert.Equal(before.Atk, now.Atk);
            Assert.Equal(before.Def, now.Def);
            Assert.Equal(before.Level, now.Level);
            Assert.Equal(before.Race, now.Race);
            Assert.Equal(before.Attribute, now.Attribute);
            Assert.Equal(before.Name, now.Name);
            Assert.Equal(before.Desc, now.Desc);
            Assert.Equal(before.Strings, now.Strings);
        }
        Assert.Equal(lua, File.ReadAllText(Path.Combine(scripts, "c123456789.lua")));
        // Reopened, it is now a Card Crafter database and still has its art.
        var again = DatabasePack.Open(db);
        Assert.True(again.HadCardCrafterData);
        Assert.Equal(Path.Combine(arts, "123456789.jpg"), again.Pack.Cards.First(c => c.Card.Id == 123456789).ArtworkPath);
    }

    [Fact]
    public void ClassicLayout_UsesScriptAndPicsNextToTheDatabase()
    {
        using var tmp = new TempDir();
        var db = Path.Combine(tmp.Path, "cards.cdb");
        var saved = DatabasePack.Save(Samples.Pack(Samples.DragonCommander()), db, null, Path.Combine(tmp.Path, "backups"));
        Assert.True(saved.Success, saved.Error);
        Assert.Equal(Path.Combine(tmp.Path, "script"), saved.ScriptFolder);
        Assert.Single(Directory.GetFiles(Path.Combine(tmp.Path, "script"), "c*.lua"));
    }

    [Fact]
    public void NeverSavesOverTheGamesOwnDatabase()
    {
        using var tmp = new TempDir();
        var db = Path.Combine(tmp.Path, "db");
        CdbFile.WriteFresh(db, Enumerable.Range(1, OmegaInventory.OfficialDatabaseMinCards).Select(i => new CdbRow { Id = i, Name = "Card " + i }));
        var before = File.ReadAllBytes(db);
        var saved = DatabasePack.Save(Samples.Pack(Samples.DragonCommander()), db, null, Path.Combine(tmp.Path, "backups"));
        Assert.False(saved.Success);
        Assert.Contains("game's own", saved.Error);
        Assert.Equal(before, File.ReadAllBytes(db));
    }

    [Fact]
    public void DamagedCardCrafterData_FallsBackToTheRows()
    {
        using var tmp = new TempDir();
        var db = Path.Combine(tmp.Path, "x.cdb");
        CdbFile.WriteFresh(db, [new CdbRow { Id = 100000001, Type = 0x21, Name = "A" }]);
        CdbFile.WritePackJson(db, "{ not json");
        var opened = DatabasePack.Open(db);
        Assert.False(opened.HadCardCrafterData);
        Assert.Equal("A", opened.Pack.Cards.Single().Card.Name);
    }
}
