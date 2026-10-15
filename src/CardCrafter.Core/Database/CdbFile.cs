using Microsoft.Data.Sqlite;

namespace CardCrafter.Core.Database;

/// <summary>Reads and writes ygopro-format card databases (.cdb files, which are SQLite).</summary>
public static class CdbFile
{
    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Writes rows into the database, creating it if needed, in the given layout (ygopro's by default, or a layout
    /// read from the game's own database). Existing cards with the same id are replaced.
    /// </summary>
    public static void Write(string path, IEnumerable<CdbRow> rows, CdbSchema? schema = null)
    {
        schema ??= CdbSchema.Ygopro;
        var list = rows.ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var conn = Open(path, SqliteOpenMode.ReadWriteCreate);
        Exec(conn, IfNotExists(schema.DatasSql ?? CdbSchema.Ygopro.DatasSql!));
        Exec(conn, IfNotExists(schema.TextsSql ?? CdbSchema.Ygopro.TextsSql!));
        // Effect descriptions and prompts (aux.Stringid) come from str1..str16. Some layouts, like older Omega
        // databases, have no such columns; without them the game shows a placeholder, so they are always added.
        var textCols = schema.Texts.ToList();
        var have = Columns(conn, "texts");
        for (int i = 1; i <= 16; i++)
        {
            var name = "str" + i;
            if (!have.Contains(name, StringComparer.OrdinalIgnoreCase)) Exec(conn, $"ALTER TABLE texts ADD COLUMN {name} text");
            if (!textCols.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) textCols.Add(new CdbColumn(name, "text", false, null));
        }
        bool limits = schema.HasBanlists && schema.BandatasSql is not null && list.Any(r => r.Limits.Count > 0);
        if (limits) Exec(conn, IfNotExists(schema.BandatasSql!));

        using var tx = conn.BeginTransaction();
        var datasCols = schema.Datas.Where(c => DatasValue(c, new CdbRow(), schema) is not Skip).ToList();
        using var datas = Insert(conn, tx, "datas", datasCols.Select(c => c.Name));
        var textsCols = textCols.Where(c => TextsValue(c, new CdbRow()) is not Skip).ToList();
        using var texts = Insert(conn, tx, "texts", textsCols.Select(c => c.Name));

        foreach (var r in list)
        {
            foreach (var c in datasCols) datas.Parameters["$" + c.Name].Value = As(c, DatasValue(c, r, schema), schema.DatasStrict) ?? DBNull.Value;
            datas.ExecuteNonQuery();
            foreach (var c in textsCols) texts.Parameters["$" + c.Name].Value = As(c, TextsValue(c, r), schema.TextsStrict) ?? DBNull.Value;
            texts.ExecuteNonQuery();
        }

        if (limits)
        {
            var card = schema.BandataCardColumn!;
            var extra = schema.Bandatas.Where(c => c.Name is not ("banlistid" or "limits") && c.Name != card && c.NotNull && c.Default is null
                                                   && !(c.Name == "id" && c.Type.Contains("int"))).ToList();
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM bandatas WHERE \"{card}\" = $id";
            del.Parameters.Add("$id", SqliteType.Integer);
            using var ins = Insert(conn, tx, "bandatas", new[] { card, "banlistid", "limits" }.Concat(extra.Select(c => c.Name)));
            foreach (var r in list)
            {
                del.Parameters["$id"].Value = r.Id;
                del.ExecuteNonQuery();
                var entries = r.Limits.TryGetValue(AllBanlists, out var all)
                    ? schema.Banlists.Select(b => (b.Id, all))
                    : r.Limits.Select(kv => (kv.Key, kv.Value));
                foreach (var (banlist, limit) in entries)
                {
                    var strict = schema.BandatasStrict;
                    ins.Parameters["$" + card].Value = As(Column(schema.Bandatas, card), r.Id, strict);
                    ins.Parameters["$banlistid"].Value = As(Column(schema.Bandatas, "banlistid"), banlist, strict);
                    ins.Parameters["$limits"].Value = As(Column(schema.Bandatas, "limits"), (long)limit, strict);
                    foreach (var c in extra) ins.Parameters["$" + c.Name].Value = As(c, c.Type.Contains("text") ? "" : 0L, strict);
                    ins.ExecuteNonQuery();
                }
            }
        }
        tx.Commit();
    }

    /// <summary>YGO Omega's banlist tables, for databases that don't have them yet.</summary>
    private const string DefaultBanlistsSql = """
        CREATE TABLE IF NOT EXISTS banlists(id INTEGER NOT NULL UNIQUE, name TEXT NOT NULL, flags INTEGER, PRIMARY KEY(id));
        CREATE TABLE IF NOT EXISTS bandatas(cardid INTEGER NOT NULL, banlistid INTEGER NOT NULL, limits INTEGER NOT NULL, PRIMARY KEY(cardid, banlistid));
        """;

    /// <summary>
    /// Writes banlists into the database's banlists tables (creating them in YGO Omega's layout when it has none).
    /// Each list replaces any list with the same id; its cards can be official ones or the database's own.
    /// </summary>
    public static void WriteBanlists(string path, IEnumerable<Model.PackBanlist> banlists, CdbSchema? schema = null)
    {
        var lists = banlists.ToList();
        if (lists.Count == 0) return;
        using var conn = Open(path, SqliteOpenMode.ReadWriteCreate);
        if (schema is { BanlistsSql: { } bsql, BandatasSql: { } dsql, HasBanlists: true })
        {
            Exec(conn, IfNotExists(bsql));
            Exec(conn, IfNotExists(dsql));
        }
        else Exec(conn, DefaultBanlistsSql);
        var read = CdbSchema.Read(path)!;
        var card = read.BandataCardColumn ?? "cardid";
        using var tx = conn.BeginTransaction();
        var listCols = new List<string> { "id", "name" };
        if (read.BanlistColumns.Any(c => c.Name == "flags")) listCols.Add("flags");
        using var insList = Insert(conn, tx, "banlists", listCols);
        var extra = read.Bandatas.Where(c => c.Name is not ("banlistid" or "limits") && c.Name != card && c.NotNull && c.Default is null
                                             && !(c.Name == "id" && c.Type.Contains("int"))).ToList();
        using var insData = Insert(conn, tx, "bandatas", new[] { card, "banlistid", "limits" }.Concat(extra.Select(c => c.Name)));
        using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM bandatas WHERE banlistid = $id";
        del.Parameters.Add("$id", SqliteType.Integer);
        foreach (var list in lists)
        {
            insList.Parameters["$id"].Value = list.Id;
            insList.Parameters["$name"].Value = list.Name;
            if (listCols.Contains("flags")) insList.Parameters["$flags"].Value = list.Flags;
            insList.ExecuteNonQuery();
            del.Parameters["$id"].Value = list.Id;
            del.ExecuteNonQuery();
            foreach (var entry in list.Entries.Where(e => e.Limit is >= 0 and <= 3).GroupBy(e => e.Code).Select(g => g.Last()))
            {
                insData.Parameters["$" + card].Value = entry.Code;
                insData.Parameters["$banlistid"].Value = list.Id;
                insData.Parameters["$limits"].Value = (long)entry.Limit;
                foreach (var c in extra) insData.Parameters["$" + c.Name].Value = c.Type.Contains("text") ? "" : 0L;
                insData.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>Every banlist in a database with its cards, e.g. YGO Omega's TCG and OCG lists. Names come from the database's texts.</summary>
    public static List<Model.PackBanlist> ReadBanlists(string path)
    {
        var schema = CdbSchema.Read(path);
        var result = new List<Model.PackBanlist>();
        if (schema is null || schema.Banlists.Count == 0) return result;
        using var conn = Open(path, SqliteOpenMode.ReadOnly);
        var byId = new Dictionary<long, Model.PackBanlist>();
        using (var cmd = conn.CreateCommand())
        {
            var flags = schema.BanlistColumns.Any(c => c.Name == "flags") ? "flags" : "0";
            cmd.CommandText = $"SELECT id, name, {flags} FROM banlists ORDER BY id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var list = new Model.PackBanlist { Id = Long(rd, 0), Name = Str(rd, 1), Flags = Long(rd, 2) };
                byId[list.Id] = list;
                result.Add(list);
            }
        }
        if (!schema.HasBanlists) return result;
        using (var cmd = conn.CreateCommand())
        {
            var hasTexts = schema.Texts.Count > 0;
            cmd.CommandText = $"SELECT b.\"{schema.BandataCardColumn}\", b.banlistid, b.limits{(hasTexts ? ", t.name" : ", NULL")} FROM bandatas b"
                              + (hasTexts ? $" LEFT JOIN texts t ON t.id = b.\"{schema.BandataCardColumn}\"" : "");
            try
            {
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                    if (byId.TryGetValue(Long(rd, 1), out var list))
                        list.Entries.Add(new Model.BanlistEntry { Code = Long(rd, 0), Limit = (int)Long(rd, 2), Name = Str(rd, 3) });
            }
            catch (SqliteException) { /* an unusual layout leaves the lists without cards */ }
        }
        foreach (var list in result) list.Entries.Sort((a, b) => a.Limit != b.Limit ? a.Limit.CompareTo(b.Limit) : string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>The banlists in ygopro's lflist.conf format: "!Name", then "passcode limit --name" lines.</summary>
    public static string LflistConf(IEnumerable<Model.PackBanlist> banlists)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var list in banlists)
        {
            sb.Append('!').AppendLine(list.Name);
            foreach (var e in list.Entries.Where(e => e.Limit is >= 0 and <= 2))
                sb.AppendLine(System.FormattableString.Invariant($"{e.Code} {e.Limit} --{e.Name}"));
        }
        return sb.ToString();
    }

    /// <summary>In <see cref="CdbRow.Limits"/>, this key means "on every banlist the game has".</summary>
    public const long AllBanlists = -1;

    /// <summary>Marks a column Card Crafter leaves to its default.</summary>
    private sealed class Skip { public static readonly Skip Value = new(); }

    private static object? DatasValue(CdbColumn c, CdbRow r, CdbSchema schema) => c.Name switch
    {
        "id" => r.Id,
        "ot" => r.Ot,
        "alias" => r.Alias,
        "setcode" => schema.Setcode.Encode(r.Setcode),
        "setid" => schema.FormatSetid(CdbEncoder.UnpackSetcodes(r.Setcode)),
        "type" => r.Type,
        "atk" => r.Atk,
        "def" => r.Def,
        "level" => r.Level,
        "race" => r.Race,
        "attribute" => r.Attribute,
        "category" => schema.CategoryIsFlags ? r.OmegaFlags : r.Category,
        "genre" => r.Category,
        "support" => r.Support,
        "ocgdate" => r.OcgDate,
        "tcgdate" => r.TcgDate,
        "script" => r.Script,
        _ when c.NotNull && c.Default is null => c.Type.Contains("text") || c.Type.Contains("char") ? "" : 0,
        _ => Skip.Value,
    };

    private static CdbColumn Column(List<CdbColumn> columns, string name) =>
        columns.FirstOrDefault(c => c.Name == name) ?? new CdbColumn(name, "", false, null);

    /// <summary>
    /// A value in the column's declared type. STRICT tables (newer YGO Omega databases) refuse anything else,
    /// e.g. an integer in a BLOB column; other tables take the value as it is.
    /// </summary>
    private static object? As(CdbColumn c, object? value, bool strict)
    {
        if (!strict || value is null or Skip) return value;
        var type = c.Type.ToLowerInvariant();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return type switch
        {
            _ when type.Contains("int") => value switch
            {
                long or int => Convert.ToInt64(value, ci),
                bool b => b ? 1L : 0L,
                string s => long.TryParse(s, System.Globalization.NumberStyles.Integer, ci, out var l) ? l : 0L,
                byte[] => 0L,
                _ => Convert.ToInt64(value, ci),
            },
            "text" => value switch
            {
                string => value,
                byte[] b => System.Text.Encoding.UTF8.GetString(b),
                _ => Convert.ToString(value, ci),
            },
            "blob" => value switch
            {
                byte[] => value,
                string s => System.Text.Encoding.UTF8.GetBytes(s),
                long l => BitConverter.GetBytes(l),
                int n => BitConverter.GetBytes((long)n),
                _ => value,
            },
            "real" => Convert.ToDouble(value, ci),
            _ => value,
        };
    }

    private static object? TextsValue(CdbColumn c, CdbRow r) => c.Name switch
    {
        "id" => r.Id,
        "name" => r.Name,
        "desc" => r.Desc,
        // Empty strings, never NULL: some clients crash on NULL text columns.
        _ when c.Name.StartsWith("str", StringComparison.Ordinal) && int.TryParse(c.Name[3..], out var i) && i is >= 1 and <= 16 => r.Strings.ElementAtOrDefault(i - 1) ?? "",
        _ when c.NotNull && c.Default is null => c.Type.Contains("int") ? 0 : "",
        _ => Skip.Value,
    };

    private static SqliteCommand Insert(SqliteConnection conn, SqliteTransaction tx, string table, IEnumerable<string> columns)
    {
        var cols = columns.ToList();
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT OR REPLACE INTO {table}({string.Join(",", cols.Select(c => $"\"{c}\""))}) VALUES({string.Join(",", cols.Select(c => "$" + c))})";
        foreach (var c in cols) cmd.Parameters.Add(new SqliteParameter("$" + c, null));
        return cmd;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string IfNotExists(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(sql.Trim().TrimEnd(';'), @"^\s*CREATE\s+TABLE\s+(?!IF\s+NOT\s+EXISTS)", "CREATE TABLE IF NOT EXISTS ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Replaces the whole file with exactly these rows.</summary>
    public static void WriteFresh(string path, IEnumerable<CdbRow> rows, CdbSchema? schema = null)
    {
        if (File.Exists(path)) File.Delete(path);
        Write(path, rows, schema);
    }

    /// <summary>
    /// Card Crafter keeps the pack it saved (event sheets, archetype names, custom Types) in its own table inside
    /// the database, so opening the database again gives back the whole pack. Games only read datas and texts.
    /// </summary>
    private const string PackTable = "cardcrafter_pack";

    public static void WritePackJson(string path, string json)
    {
        using var conn = Open(path, SqliteOpenMode.ReadWrite);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {PackTable}(key text primary key, value text);
            INSERT OR REPLACE INTO {PackTable} VALUES('pack', $json);
            """;
        cmd.Parameters.AddWithValue("$json", json);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The pack Card Crafter stored in this database, or null for databases made elsewhere.</summary>
    public static string? ReadPackJson(string path)
    {
        using var conn = Open(path, SqliteOpenMode.ReadOnly);
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$t";
        check.Parameters.AddWithValue("$t", PackTable);
        if (Convert.ToInt64(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0) return null;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT value FROM {PackTable} WHERE key='pack'";
        return cmd.ExecuteScalar() as string;
    }

    public static List<CdbRow> Read(string path)
    {
        // Read whatever columns this database has: ygopro's, Omega's, or an older Omega layout.
        var schema = CdbSchema.Read(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)} has no card tables.");
        using var conn = Open(path, SqliteOpenMode.ReadOnly);
        string D(string col) => schema.Has(col) ? $"d.\"{col}\"" : "NULL";
        string T(string col) => schema.HasText(col) ? $"t.\"{col}\"" : "NULL";
        var setcode = schema.Has("setcode") ? "d.setcode" : schema.Has("setid") ? "d.setid" : "NULL";
        var categories = schema.CategoryIsFlags ? D("genre") : D("category");
        var flags = schema.CategoryIsFlags ? D("category") : "NULL";
        var strs = string.Join(", ", Enumerable.Range(1, 16).Select(i => T("str" + i)));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT d.id, {D("ot")}, {D("alias")}, {setcode}, {D("type")}, {D("atk")}, {D("def")}, {D("level")}, {D("race")}, {D("attribute")},
                   {categories}, t.name, t.desc, {strs}, {flags}, {D("support")}, {D("ocgdate")}, {D("tcgdate")}, {D("script")}
            FROM datas d LEFT JOIN texts t ON t.id = d.id ORDER BY d.id
            """;
        var rows = new List<CdbRow>();
        using (var rd = cmd.ExecuteReader())
        {
            while (rd.Read())
            {
                var row = new CdbRow
                {
                    Id = Long(rd, 0), Ot = Long(rd, 1), Alias = Long(rd, 2),
                    Setcode = schema.Has("setcode") ? schema.Setcode.Decode(rd.IsDBNull(3) ? null : rd.GetValue(3)) : CdbEncoder.PackSetcodes(CdbSchema.ParseSetid(Str(rd, 3))),
                    Type = Long(rd, 4), Atk = Long(rd, 5), Def = Long(rd, 6), Level = Long(rd, 7),
                    Race = Long(rd, 8), Attribute = Long(rd, 9), Category = Long(rd, 10),
                    Name = Str(rd, 11),
                    Desc = Str(rd, 12),
                    OmegaFlags = Long(rd, 29), Support = Long(rd, 30),
                    OcgDate = rd.IsDBNull(31) ? CdbRow.Unreleased : Long(rd, 31),
                    TcgDate = rd.IsDBNull(32) ? CdbRow.Unreleased : Long(rd, 32),
                    Script = rd.IsDBNull(33) ? null : Str(rd, 33),
                };
                for (int i = 0; i < 16; i++) row.Strings[i] = Str(rd, 13 + i);
                rows.Add(row);
            }
        }

        FillMissingTexts(conn, rows);

        if (schema.HasBanlists)
        {
            var byId = rows.ToDictionary(r => r.Id);
            using var ban = conn.CreateCommand();
            ban.CommandText = $"SELECT \"{schema.BandataCardColumn}\", banlistid, limits FROM bandatas";
            try
            {
                using var rd = ban.ExecuteReader();
                while (rd.Read())
                    if (byId.TryGetValue(Long(rd, 0), out var row)) row.Limits[Long(rd, 1)] = (int)Long(rd, 2);
            }
            catch (SqliteException) { /* an unexpected banlist layout must not stop the cards loading */ }
        }
        return rows;
    }

    /// <summary>
    /// YGO Omega keeps card text in one table per language (texts, en_texts, ja_texts, ...). When the main texts
    /// table has no name for a card, take it from the others, English first.
    /// </summary>
    private static void FillMissingTexts(SqliteConnection conn, List<CdbRow> rows)
    {
        var missing = rows.Where(r => r.Name.Length == 0).ToDictionary(r => r.Id);
        if (missing.Count == 0) return;
        var tables = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE '%texts' AND name <> 'texts'";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) tables.Add(rd.GetString(0));
        }
        static int Rank(string t) => t.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 0 : t.StartsWith("us", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        foreach (var table in tables.OrderBy(Rank).ThenBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            if (missing.Count == 0) break;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT id, name, desc FROM \"{table}\" WHERE name IS NOT NULL AND name <> ''";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    if (!missing.Remove(Long(rd, 0), out var row)) continue;
                    row.Name = Str(rd, 1);
                    if (row.Desc.Length == 0) row.Desc = Str(rd, 2);
                }
            }
            catch (SqliteException)
            {
                // A text table with other columns is skipped.
            }
        }
    }

    private static List<string> Columns(SqliteConnection conn, string table)
    {
        var names = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) names.Add(rd.GetString(1));
        return names;
    }

    // Databases made by other tools (and the game's own) can hold NULLs, text in number columns,
    // or text stored as bytes. Read every value defensively instead of failing on one odd row.
    private static long Long(SqliteDataReader rd, int i)
    {
        if (rd.IsDBNull(i)) return 0;
        var v = rd.GetValue(i);
        return v switch
        {
            long l => l,
            int n => n,
            double d => (long)d,
            string s when long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
            string s when s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var h) => h,
            _ => 0,
        };
    }

    private static string Str(SqliteDataReader rd, int i)
    {
        if (rd.IsDBNull(i)) return "";
        return rd.GetValue(i) switch
        {
            string s => s,
            byte[] b => System.Text.Encoding.UTF8.GetString(b),
            var o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
    }

    /// <summary>Just the passcodes in a database. Fast even for the 20,000-card official database.</summary>
    public static List<long> ReadIds(string path)
    {
        using var conn = Open(path, SqliteOpenMode.ReadOnly);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM datas";
        using var rd = cmd.ExecuteReader();
        var ids = new List<long>();
        while (rd.Read()) ids.Add(Long(rd, 0));
        return ids;
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
        catch (Exception ex) when (ex is SqliteException or InvalidCastException or FormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// For the scan report: how many cards have effect strings (texts.str1), and one example, so it shows whether a
    /// database keeps its prompts there. Null when it has no str1 column.
    /// </summary>
    public static (int WithStrings, int Total, string? Example)? StringsSummary(string path)
    {
        try
        {
            using var conn = Open(path, SqliteOpenMode.ReadOnly);
            if (!Columns(conn, "texts").Contains("str1", StringComparer.OrdinalIgnoreCase)) return null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM texts";
            var total = Convert.ToInt32(cmd.ExecuteScalar());
            cmd.CommandText = "SELECT count(*) FROM texts WHERE str1 IS NOT NULL AND trim(str1)<>''";
            var with = Convert.ToInt32(cmd.ExecuteScalar());
            cmd.CommandText = "SELECT name, str1 FROM texts WHERE str1 IS NOT NULL AND trim(str1)<>'' ORDER BY id LIMIT 1";
            using var rd = cmd.ExecuteReader();
            string? example = rd.Read() ? $"{(rd.IsDBNull(0) ? "" : rd.GetValue(0))}: \"{rd.GetValue(1)}\"" : null;
            return (with, total, example);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidCastException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
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
