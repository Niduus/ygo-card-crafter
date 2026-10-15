using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using Microsoft.Data.Sqlite;

namespace CardCrafter.Core.Tests;

/// <summary>YGO Omega's database layouts, card pool ("ot") and limitation (banlist) tags.</summary>
public class OmegaFormatTests
{
    /// <summary>The layout DataEditorX-Omega writes for .db files, with banlists the way newer Omega versions keep them.</summary>
    public const string OmegaLayout = """
        CREATE TABLE datas(id integer primary key default 0, ot integer default 0, alias integer default 0, setcode integer default 0,
            type integer default 0, atk integer default 0, def integer default 0, level integer default 0, race integer default 0,
            attribute integer default 0, category integer default 0, genre integer default 0, script blob, support integer default 0,
            ocgdate integer default 253402207200, tcgdate integer default 253402207200);
        CREATE TABLE texts(id integer primary key, name text, desc text, str1 text, str2 text, str3 text, str4 text, str5 text, str6 text,
            str7 text, str8 text, str9 text, str10 text, str11 text, str12 text, str13 text, str14 text, str15 text, str16 text);
        CREATE TABLE banlists(id integer primary key, name text not null, flags integer);
        CREATE TABLE bandatas(id integer primary key autoincrement, flag integer not null, banlistid integer not null,
            flagtype integer not null, limits integer not null, mode integer, location integer);
        INSERT INTO banlists VALUES(1, 'TCG', 0), (2, 'OCG', 0);
        INSERT INTO datas(id, ot, type, genre, category) VALUES(89631139, 3, 17, 0, 0);
        INSERT INTO texts(id, name, desc) VALUES(89631139, 'Blue-Eyes White Dragon', 'This legendary dragon...');
        """;

    /// <summary>The older layout in OmegaCustom's Databases/Test.db: archetypes as text, no str columns.</summary>
    public const string OldOmegaLayout = """
        CREATE TABLE "datas" ("id" INTEGER NOT NULL DEFAULT 0, "ot" INTEGER NOT NULL DEFAULT 0, "alias" INTEGER NOT NULL DEFAULT 0,
            "type" INTEGER NOT NULL DEFAULT 0, "atk" INTEGER NOT NULL DEFAULT 0, "def" INTEGER NOT NULL DEFAULT 0, "level" INTEGER NOT NULL DEFAULT 0,
            "race" INTEGER NOT NULL DEFAULT 0, "attribute" INTEGER NOT NULL DEFAULT 0, "category" INTEGER NOT NULL DEFAULT 0,
            "setid" TEXT NOT NULL DEFAULT '', PRIMARY KEY("id"));
        CREATE TABLE "texts" ("id" integer NOT NULL DEFAULT 0, "name" TEXT NOT NULL DEFAULT '', "desc" TEXT NOT NULL DEFAULT '', PRIMARY KEY("id"));
        CREATE TABLE "banlists" ("id" INTEGER NOT NULL UNIQUE, "name" TEXT NOT NULL, "flags" INTEGER, PRIMARY KEY("id"));
        CREATE TABLE "bandatas" ("cardid" INTEGER NOT NULL, "banlistid" INTEGER NOT NULL, "limits" INTEGER NOT NULL, PRIMARY KEY("cardid","banlistid"));
        INSERT INTO banlists VALUES(7, 'Custom List', 0);
        INSERT INTO datas VALUES(6467, 3, 0, 524290, 0, 0, 0, 0, 0, 4, '0x3008,0x1');
        INSERT INTO texts VALUES(6467, 'The Seal of Orichalcos', '[The Seal of Orichalcos] - Anime');
        """;

    public static string MakeDb(string path, string sql)
    {
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    private static CardProject Custom(Limitation limit = Limitation.Unlimited) => new()
    {
        Card = new CardData { Id = 210000001, Name = "My Custom", Text = "Text.", Archetypes = [0x3008, 0x1], Category = 0x200, Limitation = limit },
    };

    [Fact]
    public void NewPacks_AreInTheCustomCardPool()
    {
        Assert.Equal((int)CardPool.Custom, new CardPack().Ot);
        var row = CdbEncoder.Encode(Custom().Card, [], new CardPack().Ot);
        Assert.Equal(4, row.Ot);
        Assert.Equal(CdbRow.Unreleased, row.TcgDate);
    }

    [Fact]
    public void OldPacks_MoveFromOcgTcgToCustom()
    {
        var json = PackSerializer.ToJson(new CardPack { Ot = 3 }).Replace("\"formatVersion\": 2", "\"formatVersion\": 1");
        Assert.Contains("\"formatVersion\": 1", json);
        Assert.Equal(4, PackSerializer.FromJson(json).Ot);
        // A pool chosen on purpose in a current pack stays.
        Assert.Equal(3, PackSerializer.FromJson(PackSerializer.ToJson(new CardPack { Ot = 3 })).Ot);
    }

    [Fact]
    public void OcgTcgCards_GetAReleaseDate_SoOmegaListsThem()
    {
        var row = CdbEncoder.Encode(Custom().Card, [], (int)CardPool.OcgTcg);
        Assert.Equal(CdbEncoder.DefaultReleaseDate, row.OcgDate);
        Assert.Equal(CdbEncoder.DefaultReleaseDate, row.TcgDate);
        var tcg = CdbEncoder.Encode(Custom().Card, [], (int)CardPool.Tcg);
        Assert.Equal(CdbRow.Unreleased, tcg.OcgDate);
        Assert.Equal(CdbEncoder.DefaultReleaseDate, tcg.TcgDate);
    }

    [Fact]
    public void WritesInOmegasLayout_WithGenre_Flags_AndLimitsOnEveryBanlist()
    {
        using var tmp = new TempDir();
        var official = MakeDb(Path.Combine(tmp.Path, "db"), OmegaLayout);
        var schema = CdbSchema.Read(official)!;
        Assert.True(schema.CategoryIsFlags);
        Assert.Equal("flag", schema.BandataCardColumn);
        Assert.Equal(2, schema.Banlists.Count);
        Assert.Contains("ocgdate", schema.Describe());

        var custom = Path.Combine(tmp.Path, "custom.db");
        var card = Custom(Limitation.Limited).Card;
        card.OmegaFlags = 0x8; // Beta Card
        CdbFile.WriteFresh(custom, [CdbEncoder.Encode(card, ["Search"], (int)CardPool.Custom)], schema);

        using (var conn = new SqliteConnection($"Data Source={custom};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ot, genre, category, tcgdate, setcode FROM datas";
            using (var rd = cmd.ExecuteReader())
            {
                Assert.True(rd.Read());
                Assert.Equal(4, rd.GetInt64(0));
                Assert.Equal(0x200, rd.GetInt64(1)); // search categories go in genre
                Assert.Equal(0x8, rd.GetInt64(2));   // Omega's flags go in category
                Assert.Equal(CdbRow.Unreleased, rd.GetInt64(3));
                Assert.Equal(CdbEncoder.PackSetcodes([0x3008, 0x1]), rd.GetInt64(4));
            }
            cmd.CommandText = "SELECT flag, banlistid, limits, flagtype FROM bandatas ORDER BY banlistid";
            using var b = cmd.ExecuteReader();
            Assert.True(b.Read()); Assert.Equal(210000001, b.GetInt64(0)); Assert.Equal(1, b.GetInt64(1)); Assert.Equal(1, b.GetInt64(2));
            Assert.True(b.Read()); Assert.Equal(2, b.GetInt64(1));
            Assert.False(b.Read());
        }

        var back = CdbFile.Read(custom).Single();
        Assert.Equal(0x200, back.Category);
        Assert.Equal(0x8, back.OmegaFlags);
        var decoded = CdbEncoder.Decode(back);
        Assert.Equal(Limitation.Limited, decoded.Limitation);
        Assert.Equal("Search", back.Strings[0]);
    }

    [Fact]
    public void UnlimitedCards_AreOnNoBanlist()
    {
        using var tmp = new TempDir();
        var schema = CdbSchema.Read(MakeDb(Path.Combine(tmp.Path, "db"), OmegaLayout))!;
        var custom = Path.Combine(tmp.Path, "custom.db");
        CdbFile.WriteFresh(custom, [CdbEncoder.Encode(Custom().Card, [], 4)], schema);
        Assert.Empty(CdbFile.Read(custom).Single().Limits);
    }

    [Fact]
    public void ReadsAndWritesTheOlderOmegaLayout()
    {
        using var tmp = new TempDir();
        var old = MakeDb(Path.Combine(tmp.Path, "Test.db"), OldOmegaLayout);
        var row = CdbFile.Read(old).Single();
        Assert.Equal("The Seal of Orichalcos", row.Name);
        Assert.Equal([0x3008, 0x1], CdbEncoder.UnpackSetcodes(row.Setcode));
        Assert.Equal(4, row.Category); // no genre column: category is still the search categories

        var schema = CdbSchema.Read(old)!;
        var custom = Path.Combine(tmp.Path, "custom.db");
        CdbFile.WriteFresh(custom, [CdbEncoder.Encode(Custom(Limitation.Forbidden).Card, ["unused"], 4)], schema);
        using var conn = new SqliteConnection($"Data Source={custom};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT setid FROM datas";
        Assert.Equal("0x3008,0x1", cmd.ExecuteScalar());
        cmd.CommandText = "SELECT cardid, banlistid, limits FROM bandatas";
        using var rd = cmd.ExecuteReader();
        Assert.True(rd.Read());
        Assert.Equal(7, rd.GetInt64(1));
        Assert.Equal(0, rd.GetInt64(2));
    }

    [Fact]
    public void Installer_UsesTheGamesLayout()
    {
        using var tmp = new TempDir();
        var files = Path.Combine(tmp.Path, "YGO Omega", "YGO Omega_Data", "Files");
        Directory.CreateDirectory(Path.Combine(files, "Scripts"));
        Directory.CreateDirectory(Path.Combine(files, "Databases"));
        Directory.CreateDirectory(Path.Combine(files, "Bundles"));
        File.WriteAllText(Path.Combine(files, "Scripts", "c1.lua"), "");
        // An official database big enough to be recognised as the game's own.
        var official = MakeDb(Path.Combine(files, "Bundles", "db"), OmegaLayout);
        var schema0 = CdbSchema.Read(official)!;
        CdbFile.Write(official, Enumerable.Range(1, OmegaInventory.OfficialDatabaseMinCards).Select(i => new CdbRow { Id = i, Ot = 3, Name = "Card " + i }), schema0);
        CdbFile.WriteFresh(Path.Combine(files, "Databases", "Other.db"), [new CdbRow { Id = 555000001, Name = "Other" }]);

        var inventory = OmegaInventory.Scan(Path.Combine(tmp.Path, "YGO Omega"));
        Assert.True(inventory.GameSchema?.CategoryIsFlags);
        Assert.Contains("genre", inventory.Report());

        var pack = new CardPack { Name = "Mine", Cards = [Custom()] };
        var export = PackExporter.Export(pack, Path.Combine(tmp.Path, "export"), null, inventory.GameSchema);
        var result = OmegaInstaller.Install(export, inventory, Path.Combine(tmp.Path, "backups"));
        Assert.True(result.Success, result.Error);
        var installed = Path.Combine(files, "Databases", "Mine.db");
        Assert.True(CdbSchema.Read(installed)!.Has("tcgdate"));
        Assert.Equal(4, CdbFile.Read(installed).Single().Ot);
    }
}
