using Microsoft.Data.Sqlite;

namespace CardCrafter.Core.Database;

public sealed record CdbColumn(string Name, string Type, bool NotNull, string? Default);

/// <summary>A banlist found in a database, such as Omega's TCG or OCG lists.</summary>
public sealed record Banlist(long Id, string Name);

/// <summary>
/// The table layout of a card database. ygopro's .cdb files and YGO Omega's .db files differ:
/// Omega adds genre, support, release dates and a script column, keeps banlists inside the database,
/// and some versions store archetypes as text (setid). Card Crafter reads the layout of the game's own
/// database and writes custom cards in exactly the same layout, so the game reads them like its own.
/// </summary>
public sealed class CdbSchema
{
    public List<CdbColumn> Datas { get; init; } = [];
    public List<CdbColumn> Texts { get; init; } = [];
    public List<CdbColumn> Bandatas { get; init; } = [];
    public string? DatasSql { get; init; }
    public string? TextsSql { get; init; }
    public string? BandatasSql { get; init; }
    public List<Banlist> Banlists { get; init; } = [];
    /// <summary>How archetypes are written in a text "setid" column: separator and whether hex, learned from the data.</summary>
    public string SetidSeparator { get; init; } = ",";
    public bool SetidHex { get; init; } = true;
    /// <summary>The most common "ot" values and how many cards use each, for the scan report.</summary>
    public List<(long Ot, int Count)> OtValues { get; init; } = [];

    public bool Has(string column) => Datas.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
    public bool HasText(string column) => Texts.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
    /// <summary>Omega's layout: search categories in "genre", Omega flags in "category".</summary>
    public bool CategoryIsFlags => Has("genre");
    public bool HasBanlists => Bandatas.Count > 0 && BandataCardColumn is not null && Bandatas.Any(c => c.Name == "banlistid") && Bandatas.Any(c => c.Name == "limits");
    /// <summary>The bandatas column holding the card's passcode: "cardid" in older Omega files, "flag" in newer ones.</summary>
    public string? BandataCardColumn => Bandatas.FirstOrDefault(c => c.Name is "cardid" or "flag")?.Name;

    private const string YgoproSql = """
        CREATE TABLE datas(
            id integer primary key, ot integer, alias integer, setcode integer, type integer,
            atk integer, def integer, level integer, race integer, attribute integer, category integer);
        CREATE TABLE texts(
            id integer primary key, name text, desc text,
            str1 text, str2 text, str3 text, str4 text, str5 text, str6 text, str7 text, str8 text,
            str9 text, str10 text, str11 text, str12 text, str13 text, str14 text, str15 text, str16 text);
        """;

    /// <summary>The standard ygopro .cdb layout.</summary>
    public static CdbSchema Ygopro { get; } = new()
    {
        Datas = "id ot alias setcode type atk def level race attribute category".Split(' ').Select(n => new CdbColumn(n, "integer", false, null)).ToList(),
        Texts = new[] { "id", "name", "desc" }.Concat(Enumerable.Range(1, 16).Select(i => "str" + i))
            .Select(n => new CdbColumn(n, n == "id" ? "integer" : "text", false, null)).ToList(),
        DatasSql = YgoproSql.Split(';')[0] + ";",
        TextsSql = YgoproSql.Split(';')[1].Trim() + ";",
    };

    /// <summary>Reads the layout of an existing database, or null when it isn't a card database.</summary>
    public static CdbSchema? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var datas = Columns(conn, "datas");
        var texts = Columns(conn, "texts");
        if (datas.Count == 0 || texts.Count == 0) return null;
        var bandatas = Columns(conn, "bandatas");

        var banlists = new List<Banlist>();
        if (Columns(conn, "banlists") is { Count: > 0 } bl && bl.Any(c => c.Name == "id") && bl.Any(c => c.Name == "name"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name FROM banlists ORDER BY id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) banlists.Add(new Banlist(rd.IsDBNull(0) ? 0 : rd.GetInt64(0), rd.IsDBNull(1) ? "" : rd.GetValue(1)?.ToString() ?? ""));
        }

        var (sep, hex) = (",", true);
        if (datas.Any(c => c.Name == "setid") && !datas.Any(c => c.Name == "setcode"))
            (sep, hex) = LearnSetidFormat(conn);

        var ots = new List<(long, int)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ot, count(*) FROM datas GROUP BY ot ORDER BY count(*) DESC LIMIT 8";
            try
            {
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) ots.Add((rd.IsDBNull(0) ? 0 : Convert.ToInt64(rd.GetValue(0), System.Globalization.CultureInfo.InvariantCulture), rd.GetInt32(1)));
            }
            catch (Exception ex) when (ex is SqliteException or FormatException or InvalidCastException) { }
        }

        return new CdbSchema
        {
            Datas = datas, Texts = texts, Bandatas = bandatas,
            DatasSql = TableSql(conn, "datas"), TextsSql = TableSql(conn, "texts"), BandatasSql = bandatas.Count > 0 ? TableSql(conn, "bandatas") : null,
            Banlists = banlists, SetidSeparator = sep, SetidHex = hex, OtValues = ots,
        };
    }

    private static List<CdbColumn> Columns(SqliteConnection conn, string table)
    {
        var list = new List<CdbColumn>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add(new CdbColumn(rd.GetString(1).ToLowerInvariant(), rd.IsDBNull(2) ? "" : rd.GetString(2).ToLowerInvariant(),
                rd.GetInt32(3) != 0, rd.IsDBNull(4) ? null : rd.GetValue(4)?.ToString()));
        return list;
    }

    private static string? TableSql(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$t";
        cmd.Parameters.AddWithValue("$t", table);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Looks at archetype text the game wrote, e.g. "0x3008,0x1", to write ours the same way.</summary>
    private static (string Separator, bool Hex) LearnSetidFormat(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT setid FROM datas WHERE setid IS NOT NULL AND setid <> '' AND setid <> '0' LIMIT 200";
        var samples = new List<string>();
        try
        {
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) samples.Add(rd.GetValue(0)?.ToString() ?? "");
        }
        catch (SqliteException) { }
        var sep = samples.Select(s => s.FirstOrDefault(ch => !char.IsLetterOrDigit(ch))).FirstOrDefault(ch => ch != default);
        var hex = samples.Count == 0 || samples.Any(s => s.Contains("0x", StringComparison.OrdinalIgnoreCase) || s.Any(ch => ch is >= 'a' and <= 'f' or >= 'A' and <= 'F'));
        return (sep == default ? "," : sep.ToString(), hex);
    }

    /// <summary>Archetype codes in a "setid" text value, in either hex or decimal.</summary>
    public static List<int> ParseSetid(string text)
    {
        var list = new List<int>();
        foreach (var raw in text.Split(new[] { ',', ';', ' ', '|', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            var isHex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (isHex) t = t[2..];
            if (isHex ? int.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v)
                      : int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)
                      || int.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out v))
                if (v != 0) list.Add(v);
        }
        return list;
    }

    public string FormatSetid(IEnumerable<int> codes) =>
        string.Join(SetidSeparator, codes.Where(c => c != 0).Select(c => SetidHex ? "0x" + c.ToString("x", System.Globalization.CultureInfo.InvariantCulture) : c.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>A few lines for the scan report.</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  datas columns: {string.Join(", ", Datas.Select(c => c.Name))}");
        sb.AppendLine($"  texts columns: {string.Join(", ", Texts.Select(c => c.Name))}");
        if (OtValues.Count > 0) sb.AppendLine($"  ot (card pool) values: {string.Join(", ", OtValues.Select(o => $"{o.Ot} ×{o.Count}"))}");
        if (Bandatas.Count > 0) sb.AppendLine($"  bandatas columns: {string.Join(", ", Bandatas.Select(c => c.Name))}");
        if (Banlists.Count > 0) sb.AppendLine($"  banlists: {string.Join(", ", Banlists.Take(12).Select(b => $"{b.Id} {b.Name}"))}{(Banlists.Count > 12 ? $" and {Banlists.Count - 12} more" : "")}");
        return sb.ToString();
    }
}
