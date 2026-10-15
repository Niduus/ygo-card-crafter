using Microsoft.Data.Sqlite;

namespace CardCrafter.Core.Database;

/// <summary>Reads and writes ygopro-format card databases (.cdb files, which are SQLite).</summary>
public static class CdbFile
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS datas(
            id integer primary key, ot integer, alias integer, setcode integer, type integer,
            atk integer, def integer, level integer, race integer, attribute integer, category integer);
        CREATE TABLE IF NOT EXISTS texts(
            id integer primary key, name text, desc text,
            str1 text, str2 text, str3 text, str4 text, str5 text, str6 text, str7 text, str8 text,
            str9 text, str10 text, str11 text, str12 text, str13 text, str14 text, str15 text, str16 text);
        """;

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    /// <summary>Writes rows into the database, creating it if needed. Existing cards with the same id are replaced.</summary>
    public static void Write(string path, IEnumerable<CdbRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var conn = Open(path, SqliteOpenMode.ReadWriteCreate);
        using (var create = conn.CreateCommand())
        {
            create.CommandText = Schema;
            create.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        using var datas = conn.CreateCommand();
        datas.Transaction = tx;
        datas.CommandText = "INSERT OR REPLACE INTO datas VALUES($id,$ot,$alias,$setcode,$type,$atk,$def,$level,$race,$attribute,$category)";
        foreach (var p in new[] { "id", "ot", "alias", "setcode", "type", "atk", "def", "level", "race", "attribute", "category" })
            datas.Parameters.Add("$" + p, SqliteType.Integer);

        using var texts = conn.CreateCommand();
        texts.Transaction = tx;
        var strCols = string.Join(",", Enumerable.Range(1, 16).Select(i => "$str" + i));
        texts.CommandText = $"INSERT OR REPLACE INTO texts VALUES($id,$name,$desc,{strCols})";
        texts.Parameters.Add("$id", SqliteType.Integer);
        texts.Parameters.Add("$name", SqliteType.Text);
        texts.Parameters.Add("$desc", SqliteType.Text);
        for (int i = 1; i <= 16; i++) texts.Parameters.Add("$str" + i, SqliteType.Text);

        foreach (var r in rows)
        {
            datas.Parameters["$id"].Value = r.Id;
            datas.Parameters["$ot"].Value = r.Ot;
            datas.Parameters["$alias"].Value = r.Alias;
            datas.Parameters["$setcode"].Value = r.Setcode;
            datas.Parameters["$type"].Value = r.Type;
            datas.Parameters["$atk"].Value = r.Atk;
            datas.Parameters["$def"].Value = r.Def;
            datas.Parameters["$level"].Value = r.Level;
            datas.Parameters["$race"].Value = r.Race;
            datas.Parameters["$attribute"].Value = r.Attribute;
            datas.Parameters["$category"].Value = r.Category;
            datas.ExecuteNonQuery();

            texts.Parameters["$id"].Value = r.Id;
            texts.Parameters["$name"].Value = r.Name;
            texts.Parameters["$desc"].Value = r.Desc;
            // Empty strings, never NULL: some clients crash on NULL text columns.
            for (int i = 0; i < 16; i++) texts.Parameters["$str" + (i + 1)].Value = r.Strings.ElementAtOrDefault(i) ?? "";
            texts.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Replaces the whole file with exactly these rows.</summary>
    public static void WriteFresh(string path, IEnumerable<CdbRow> rows)
    {
        if (File.Exists(path)) File.Delete(path);
        Write(path, rows);
    }

    public static List<CdbRow> Read(string path)
    {
        using var conn = Open(path, SqliteOpenMode.ReadOnly);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.id, d.ot, d.alias, d.setcode, d.type, d.atk, d.def, d.level, d.race, d.attribute, d.category,
                   t.name, t.desc, t.str1, t.str2, t.str3, t.str4, t.str5, t.str6, t.str7, t.str8,
                   t.str9, t.str10, t.str11, t.str12, t.str13, t.str14, t.str15, t.str16
            FROM datas d LEFT JOIN texts t ON t.id = d.id ORDER BY d.id
            """;
        using var rd = cmd.ExecuteReader();
        var rows = new List<CdbRow>();
        while (rd.Read())
        {
            var row = new CdbRow
            {
                Id = rd.GetInt64(0), Ot = rd.GetInt64(1), Alias = rd.GetInt64(2), Setcode = rd.GetInt64(3),
                Type = rd.GetInt64(4), Atk = rd.GetInt64(5), Def = rd.GetInt64(6), Level = rd.GetInt64(7),
                Race = rd.GetInt64(8), Attribute = rd.GetInt64(9), Category = rd.GetInt64(10),
                Name = rd.IsDBNull(11) ? "" : rd.GetString(11),
                Desc = rd.IsDBNull(12) ? "" : rd.GetString(12),
            };
            for (int i = 0; i < 16; i++) row.Strings[i] = rd.IsDBNull(13 + i) ? "" : rd.GetString(13 + i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>True when the file is SQLite and has the card tables, without throwing on other files.</summary>
    public static bool IsCardDatabase(string path, out int cardCount)
    {
        cardCount = 0;
        try
        {
            if (!LooksLikeSqlite(path)) return false;
            using var conn = Open(path, SqliteOpenMode.ReadOnly);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('datas','texts')";
            if (Convert.ToInt32(cmd.ExecuteScalar()) != 2) return false;
            cmd.CommandText = "SELECT count(*) FROM datas";
            cardCount = Convert.ToInt32(cmd.ExecuteScalar());
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public static bool LooksLikeSqlite(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[16];
            using var fs = File.OpenRead(path);
            return fs.Read(header) == 16 && header.SequenceEqual("SQLite format 3\0"u8);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
