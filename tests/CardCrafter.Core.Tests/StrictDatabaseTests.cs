using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using Microsoft.Data.Sqlite;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Newer YGO Omega databases use STRICT tables with setcode as a BLOB, which refuse ygopro's integers
/// ("cannot store INT value in BLOB column datas.setcode"). Card Crafter writes archetypes the way the game did.
/// </summary>
public class StrictDatabaseTests
{
    private static string Layout(string blueEyes, string darkMagician, string extraRows = "") => $"""
        CREATE TABLE datas(id INTEGER PRIMARY KEY, ot INTEGER NOT NULL DEFAULT 0, alias INTEGER NOT NULL DEFAULT 0, setcode BLOB,
            type INTEGER NOT NULL DEFAULT 0, atk INTEGER NOT NULL DEFAULT 0, def INTEGER NOT NULL DEFAULT 0, level INTEGER NOT NULL DEFAULT 0,
            race INTEGER NOT NULL DEFAULT 0, attribute INTEGER NOT NULL DEFAULT 0, category INTEGER NOT NULL DEFAULT 0,
            genre INTEGER NOT NULL DEFAULT 0, script BLOB, support INTEGER NOT NULL DEFAULT 0,
            ocgdate INTEGER NOT NULL DEFAULT 253402207200, tcgdate INTEGER NOT NULL DEFAULT 253402207200) STRICT;
        CREATE TABLE texts(id INTEGER PRIMARY KEY, name TEXT NOT NULL DEFAULT '', desc TEXT NOT NULL DEFAULT '',
            str1 TEXT, str2 TEXT, str3 TEXT, str4 TEXT, str5 TEXT, str6 TEXT, str7 TEXT, str8 TEXT,
            str9 TEXT, str10 TEXT, str11 TEXT, str12 TEXT, str13 TEXT, str14 TEXT, str15 TEXT, str16 TEXT) STRICT;
        CREATE TABLE banlists(id INTEGER PRIMARY KEY, name TEXT NOT NULL, flags INTEGER) STRICT;
        CREATE TABLE bandatas(cardid INTEGER NOT NULL, banlistid INTEGER NOT NULL, limits INTEGER NOT NULL, PRIMARY KEY(cardid, banlistid)) STRICT;
        INSERT INTO banlists VALUES(1, 'TCG', 0);
        INSERT INTO datas(id, ot, setcode, type) VALUES(89631139, 3, {blueEyes}, 17), (46986414, 3, {darkMagician}, 17), (55144522, 3, X'', 2);
        INSERT INTO texts(id, name, desc) VALUES(89631139, 'Blue-Eyes White Dragon', ''), (46986414, 'Dark Magician', ''), (55144522, 'Pot of Greed', '');
        {extraRows}
        """;

    private static byte[] Stored(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT setcode FROM datas WHERE id = 210000001";
        return (byte[])cmd.ExecuteScalar()!;
    }

    private static CdbRow Custom(Limitation limit = Limitation.Limited)
    {
        var card = new CardData { Id = 210000001, Name = "My Custom", Text = "Text.", Archetypes = [0x3008, 0x1], Limitation = limit };
        var row = CdbEncoder.Encode(card, ["Search"], (int)CardPool.Custom);
        row.Script = "-- script";
        return row;
    }

    [Theory]
    // 16-bit little-endian codes, as many as the card has.
    [InlineData("X'dd00'", "X'a210'", "X'a8005400'", new byte[] { 0x08, 0x30, 0x01, 0x00 })]
    // The same, always 8 bytes: ygopro's integer as bytes.
    [InlineData("X'dd00000000000000'", "X'a210000000000000'", "X'a800540000000000'", new byte[] { 0x08, 0x30, 0x01, 0x00, 0, 0, 0, 0 })]
    // 16-bit big-endian codes.
    [InlineData("X'00dd'", "X'10a2'", "X'00a80054'", new byte[] { 0x30, 0x08, 0x00, 0x01 })]
    // 32-bit little-endian codes.
    [InlineData("X'dd000000'", "X'a2100000'", "X'a800000054000000'", new byte[] { 0x08, 0x30, 0, 0, 0x01, 0, 0, 0 })]
    public void SavesIntoAStrictBlobLayout_TheWayTheGameWroteIt(string blueEyes, string darkMagician, string twoArchetypes, byte[] expected)
    {
        using var tmp = new TempDir();
        // A card with two archetypes (0xa8, 0x54) tells 16-bit codes from 32-bit ones.
        var official = OmegaFormatTests.MakeDb(Path.Combine(tmp.Path, "official.db"), Layout(blueEyes, darkMagician,
            $"INSERT INTO datas(id, ot, setcode, type) VALUES(1000, 3, {twoArchetypes}, 17); INSERT INTO texts(id, name, desc) VALUES(1000, 'Two', '');"));
        var schema = CdbSchema.Read(official)!;
        Assert.True(schema.DatasStrict);
        Assert.Equal(SetcodeStorage.Bytes, schema.Setcode.Storage);

        // Reading the game's own cards gives their archetypes back.
        var cards = CdbFile.Read(official).ToDictionary(r => r.Id);
        Assert.Equal([0xdd], CdbEncoder.UnpackSetcodes(cards[89631139].Setcode));
        Assert.Equal([0x10a2], CdbEncoder.UnpackSetcodes(cards[46986414].Setcode));

        var custom = Path.Combine(tmp.Path, "custom.db");
        CdbFile.WriteFresh(custom, [Custom()], schema);
        Assert.Equal(expected, Stored(custom));

        var back = CdbFile.Read(custom).Single();
        Assert.Equal([0x3008, 0x1], CdbEncoder.UnpackSetcodes(back.Setcode));
        Assert.Equal("-- script", back.Script);
        Assert.Equal("Search", back.Strings[0]);
        Assert.Equal(1, back.Limits[1]);
    }

    [Fact]
    public void WritesIntoTheGamesStrictDatabaseItself_AlongsideItsCards()
    {
        using var tmp = new TempDir();
        var db = OmegaFormatTests.MakeDb(Path.Combine(tmp.Path, "custom.db"), Layout("X'dd00'", "X'a210'"));
        CdbFile.Write(db, [Custom(Limitation.Unlimited)], CdbSchema.Read(db));
        Assert.Equal(new byte[] { 0x08, 0x30, 0x01, 0x00 }, Stored(db));
        Assert.Equal(4, CdbFile.Read(db).Count);
    }

    [Fact]
    public void AStrictBlobColumnWithNoArchetypesYet_GetsYgoprosIntegerAsBytes()
    {
        using var tmp = new TempDir();
        var db = OmegaFormatTests.MakeDb(Path.Combine(tmp.Path, "empty.db"), Layout("X''", "X''"));
        var schema = CdbSchema.Read(db)!;
        var custom = Path.Combine(tmp.Path, "custom.db");
        CdbFile.WriteFresh(custom, [Custom()], schema);
        Assert.Equal(new byte[] { 0x08, 0x30, 0x01, 0x00, 0, 0, 0, 0 }, Stored(custom));
    }

    [Fact]
    public void ArchetypesAsText_AreWrittenAsText()
    {
        using var tmp = new TempDir();
        var sql = Layout("'0xdd'", "'0x10a2'").Replace("setcode BLOB", "setcode TEXT");
        var db = OmegaFormatTests.MakeDb(Path.Combine(tmp.Path, "text.db"), sql.Replace("X''", "''"));
        var schema = CdbSchema.Read(db)!;
        Assert.Equal(SetcodeStorage.Text, schema.Setcode.Storage);
        var custom = Path.Combine(tmp.Path, "custom.db");
        CdbFile.WriteFresh(custom, [Custom()], schema);
        using var conn = new SqliteConnection($"Data Source={custom};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT setcode FROM datas";
        Assert.Equal("0x3008,0x1", cmd.ExecuteScalar());
        Assert.Equal([0x3008, 0x1], CdbEncoder.UnpackSetcodes(CdbFile.Read(custom).Single().Setcode));
    }

    [Fact]
    public void YgoproIntegerLayout_IsUnchanged()
    {
        using var tmp = new TempDir();
        var schema = CdbSchema.Read(OmegaFormatTests.MakeDb(Path.Combine(tmp.Path, "db"), OmegaFormatTests.OmegaLayout))!;
        Assert.Equal(SetcodeStorage.Integer, schema.Setcode.Storage);
        Assert.False(schema.DatasStrict);
    }
}
