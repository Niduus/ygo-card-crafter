using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CardCrafter.Core.Database;

/// <summary>How a database's "setcode" column holds a card's archetypes.</summary>
public enum SetcodeStorage
{
    /// <summary>ygopro's packed integer: up to four 16-bit codes.</summary>
    Integer,
    /// <summary>Codes as bytes, e.g. 16-bit little-endian codes one after another.</summary>
    Bytes,
    /// <summary>Codes as text, e.g. "0xdd,0x3008".</summary>
    Text,
}

/// <summary>
/// The archetype format of a database, learned from the cards the game wrote. Newer YGO Omega databases declare
/// setcode as a BLOB in a STRICT table, which refuses ygopro's integers; Card Crafter writes whatever the game wrote.
/// </summary>
public sealed record SetcodeFormat
{
    public SetcodeStorage Storage { get; init; } = SetcodeStorage.Integer;
    /// <summary>Bytes per code (2 or 4).</summary>
    public int Width { get; init; } = 2;
    public bool BigEndian { get; init; }
    /// <summary>When every value has the same length (e.g. 8 bytes), codes are padded with zeros to it; 0 means no padding.</summary>
    public int FixedLength { get; init; }
    /// <summary>What a card without archetypes holds: NULL, or an empty value (zero bytes, or zeros up to <see cref="FixedLength"/>).</summary>
    public bool EmptyIsNull { get; init; }
    public string TextSeparator { get; init; } = ",";
    public bool TextHex { get; init; } = true;

    public static SetcodeFormat Integer { get; } = new();

    public object? Encode(long packed)
    {
        var codes = CdbEncoder.UnpackSetcodes(packed);
        switch (Storage)
        {
            case SetcodeStorage.Bytes:
                if (codes.Count == 0 && EmptyIsNull) return null;
                var bytes = new List<byte>();
                foreach (var code in codes)
                {
                    var b = new byte[Width];
                    for (int i = 0; i < Width; i++) b[i] = (byte)(code >> (8 * i));
                    if (BigEndian) Array.Reverse(b);
                    bytes.AddRange(b);
                }
                while (bytes.Count < FixedLength) bytes.Add(0);
                return bytes.ToArray();
            case SetcodeStorage.Text:
                if (codes.Count == 0) return EmptyIsNull ? null : "";
                return string.Join(TextSeparator, codes.Select(c => TextHex ? "0x" + c.ToString("x", CultureInfo.InvariantCulture) : c.ToString(CultureInfo.InvariantCulture)));
            default:
                return packed;
        }
    }

    /// <summary>The packed archetypes of a stored value, whatever form it is in.</summary>
    public long Decode(object? value) => value switch
    {
        null or DBNull => 0,
        long l => l,
        int n => n,
        double d => (long)d,
        byte[] b => CdbEncoder.PackSetcodes(Codes(b, Width, BigEndian)),
        string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : CdbEncoder.PackSetcodes(CdbSchema.ParseSetid(s)),
        _ => 0,
    };

    private static List<int> Codes(byte[] bytes, int width, bool bigEndian)
    {
        var list = new List<int>();
        for (int i = 0; i + width <= bytes.Length; i += width)
        {
            long code = 0;
            for (int k = 0; k < width; k++)
                code |= (long)bytes[i + (bigEndian ? width - 1 - k : k)] << (8 * k);
            if (code is > 0 and <= 0xffff) list.Add((int)code);
        }
        return list;
    }

    private static int Gaps(byte[] bytes, int width, bool bigEndian)
    {
        int gaps = 0;
        bool empty = false;
        for (int i = 0; i + width <= bytes.Length; i += width)
        {
            bool zero = bytes.Skip(i).Take(width).All(b => b == 0);
            if (!zero && empty) gaps++;
            empty |= zero;
        }
        return gaps;
    }

    /// <summary>Cards whose archetype is known, to tell byte orders apart.</summary>
    private static readonly (long Id, int Code)[] Known =
    [
        (89631139, 0xdd),   // Blue-Eyes White Dragon
        (46986414, 0x10a2), // Dark Magician
        (21844576, 0x3008), // Elemental HERO Avian
        (70095154, 0x1093), // Cyber Dragon
        (44508094, 0xa3),   // Stardust Dragon
    ];

    /// <summary>Learns the format from the values in the setcode column.</summary>
    public static SetcodeFormat Learn(SqliteConnection conn, CdbColumn column)
    {
        var samples = new List<(long Id, object? Value)>();
        int nulls = 0, empties = 0;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, setcode FROM datas LIMIT 4000";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var v = rd.IsDBNull(1) ? null : rd.GetValue(1);
                if (v is null) nulls++;
                else if (v is byte[] { Length: 0 } or "" || (v is byte[] z && z.All(x => x == 0))) empties++;
                samples.Add((rd.IsDBNull(0) ? 0 : rd.GetInt64(0), v));
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidCastException) { }

        var blobs = samples.Where(s => s.Value is byte[] { Length: > 0 } b && b.Any(x => x != 0)).Select(s => (s.Id, Bytes: (byte[])s.Value!)).ToList();
        var texts = samples.Where(s => s.Value is string { Length: > 0 } t && t != "0").Select(s => (string)s.Value!).ToList();
        bool declaredBlob = column.Type.Contains("blob");
        bool declaredText = column.Type.Contains("text") || column.Type.Contains("char");
        bool emptyIsNull = nulls > empties && !column.NotNull;

        if (texts.Count > blobs.Count || (blobs.Count == 0 && declaredText && !samples.Any(s => s.Value is long)))
        {
            var sep = texts.Select(s => s.FirstOrDefault(ch => !char.IsLetterOrDigit(ch))).FirstOrDefault(ch => ch != default);
            var hex = texts.Count == 0 || texts.Any(s => s.Contains("0x", StringComparison.OrdinalIgnoreCase) || s.Any(ch => ch is >= 'a' and <= 'f' or >= 'A' and <= 'F'));
            return new SetcodeFormat { Storage = SetcodeStorage.Text, TextSeparator = sep == default ? "," : sep.ToString(), TextHex = hex, EmptyIsNull = emptyIsNull };
        }
        if (blobs.Count == 0)
            // A BLOB column with no archetypes in it yet: 16-bit little-endian codes, the byte form of ygopro's integer.
            return declaredBlob ? new SetcodeFormat { Storage = SetcodeStorage.Bytes, FixedLength = 8, EmptyIsNull = emptyIsNull } : Integer;

        var best = (Score: double.MinValue, Width: 2, BigEndian: false);
        foreach (var (width, bigEndian) in new[] { (2, false), (2, true), (4, false), (4, true) })
        {
            double score = 0;
            foreach (var (id, bytes) in blobs)
            {
                if (bytes.Length % width != 0) { score -= 5; continue; }
                var codes = Codes(bytes, width, bigEndian);
                if (Known.FirstOrDefault(k => k.Id == id) is { Id: > 0 } known) score += codes.Contains(known.Code) ? 100 : -100;
                // Archetype codes are small: the low 12 bits are the archetype, the top 4 a sub-archetype.
                score += codes.Count(c => (c & 0x0f00) <= 0x0300) - codes.Count(c => (c & 0x0f00) > 0x0300);
                if (codes.Count == 0) score -= 2;
                // Writers put archetypes one after another: an empty code before a later one means the width is wrong.
                score -= 3 * Gaps(bytes, width, bigEndian);
            }
            if (score > best.Score) best = (score, width, bigEndian);
        }
        var lengths = blobs.Select(b => b.Bytes.Length).Distinct().ToList();
        return new SetcodeFormat
        {
            Storage = SetcodeStorage.Bytes,
            Width = best.Width,
            BigEndian = best.BigEndian,
            FixedLength = lengths.Count == 1 && lengths[0] > best.Width ? lengths[0] : 0,
            EmptyIsNull = emptyIsNull,
        };
    }

    public string Describe() => Storage switch
    {
        SetcodeStorage.Bytes => $"bytes, {Width * 8}-bit {(BigEndian ? "big" : "little")}-endian codes{(FixedLength > 0 ? $", {FixedLength} bytes long" : "")}",
        SetcodeStorage.Text => $"text, e.g. {string.Join(TextSeparator, TextHex ? new[] { "0xdd", "0x3008" } : ["221", "12296"])}",
        _ => "integer",
    };
}
