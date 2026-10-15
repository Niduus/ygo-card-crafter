using System.Text.Json;
using System.Text.Json.Serialization;

namespace CardCrafter.Core.Model;

/// <summary>Saves and loads card packs as readable JSON (.ycc files) that are easy to share and diff.</summary>
public static class PackSerializer
{
    public const string Extension = ".ycc";

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string ToJson(CardPack pack) => JsonSerializer.Serialize(pack, Options);

    public static CardPack FromJson(string json) =>
        Upgrade(JsonSerializer.Deserialize<CardPack>(json, Options) ?? throw new InvalidDataException("The pack file is empty."));

    /// <summary>Brings packs saved by older versions up to date.</summary>
    private static CardPack Upgrade(CardPack pack)
    {
        // Version 1 put every card in the OCG/TCG pool (ot 3), where YGO Omega doesn't list custom cards.
        if (pack.FormatVersion < 2 && pack.Ot == (int)CardPool.OcgTcg) pack.Ot = (int)CardPool.Custom;
        pack.FormatVersion = CardPack.CurrentFormat;
        // Older combined WHEN choices ("Normal or Special Summoned") become one WHEN per event with "Or when".
        foreach (var card in pack.Cards) Blocks.TriggerSplit.Normalize(card);
        return pack;
    }

    public static void Save(CardPack pack, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson(pack));
        File.Move(tmp, path, overwrite: true);
    }

    public static CardPack Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>A deep copy, used for duplicating cards and effects.</summary>
    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
