using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// Older WHEN choices that started an effect on several events at once ("Normal or Special Summoned", "sent from the
/// hand or field", the tick-several block) become one WHEN per event joined with "Or when", which reads and compiles
/// the same. Applied when packs load, templates are added and scripts are translated, so the menus only offer
/// single events.
/// </summary>
public static class TriggerSplit
{
    /// <summary>Splits the effect's combined WHEN blocks in place. True when something changed.</summary>
    public static bool Normalize(EffectEvent effect)
    {
        if (effect.When is null || !NeedsSplit(effect)) return false;
        var all = new List<BlockInstance>();
        foreach (var w in new[] { effect.When }.Concat(effect.AlsoWhen)) all.AddRange(Split(w));
        // The same event twice starts the effect once.
        var distinct = all.GroupBy(Key).Select(g => g.First()).ToList();
        effect.When = distinct[0];
        effect.AlsoWhen = distinct.Skip(1).ToList();
        return true;
    }

    public static void Normalize(CardProject project)
    {
        foreach (var e in project.Effects) Normalize(e);
    }

    public static bool NeedsSplit(EffectEvent effect) =>
        effect.When is not null && new[] { effect.When }.Concat(effect.AlsoWhen).Any(w => Split(w) is not [var only] || !ReferenceEquals(only, w));

    private static string Key(BlockInstance b) => System.Text.Json.JsonSerializer.Serialize(new { b.BlockId, Values = b.Values.OrderBy(kv => kv.Key).ToList(), b.Filters });

    private static BlockInstance Copy(BlockInstance b, params (string Key, string Value)[] set)
    {
        var copy = new BlockInstance(b.BlockId) { Values = new(b.Values), Filters = b.Filters.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()) };
        foreach (var (k, v) in set) copy.Values[k] = v;
        return copy;
    }

    private static BlockInstance New(string blockId, string optional, params (string Key, string Value)[] set)
    {
        var b = BlockRegistry.Find(blockId)!.CreateInstance();
        foreach (var (k, v) in set) b.Values[k] = v;
        b.Values["optional"] = optional;
        return b;
    }

    /// <summary>The single-event WHEN blocks a block stands for: itself when it already is one.</summary>
    public static List<BlockInstance> Split(BlockInstance b)
    {
        var v = (string key) => b.Values.GetValueOrDefault(key);
        var optional = v("optional") ?? "true";
        switch (b.BlockId)
        {
            case SummonedTrigger.BlockId:
                return v("how") switch
                {
                    "normal_or_special" => [Copy(b, ("how", "normal")), Copy(b, ("how", "special"))],
                    "any" => [Copy(b, ("how", "normal")), Copy(b, ("how", "flip")), Copy(b, ("how", "special"))],
                    "normal_special_tributed" => [Copy(b, ("how", "normal")), Copy(b, ("how", "special")), New(ThisCardEventTrigger.BlockId, optional, ("event", "tributed"))],
                    _ => [b],
                };
            case CardsEventTrigger.BlockId:
                return v("event") switch
                {
                    "summoned" => [Copy(b, ("event", "normal")), Copy(b, ("event", "special"))],
                    "destroyed_any" => [Copy(b, ("event", "destroyed_battle")), Copy(b, ("event", "destroyed_effect"))],
                    _ => [b],
                };
            case EventTrigger.BlockId:
                return v("event") switch
                {
                    "opp_summon" => [Copy(b, ("event", "opp_normal")), Copy(b, ("event", "opp_special"))],
                    "you_summon" => [Copy(b, ("event", "you_normal")), Copy(b, ("event", "you_special"))],
                    "any_summon" => [Copy(b, ("event", "any_normal")), Copy(b, ("event", "any_special"))],
                    _ => [b],
                };
            case "when.quick" or "when.activate":
                return v("timing") switch
                {
                    "summon" => [Copy(b, ("timing", "summon_normal")), Copy(b, ("timing", "summon_special"))],
                    "summon_you" => [Copy(b, ("timing", "summon_you_normal")), Copy(b, ("timing", "summon_you_special"))],
                    "any_summon" => [Copy(b, ("timing", "any_normal")), Copy(b, ("timing", "any_flip")), Copy(b, ("timing", "any_special"))],
                    "would_summon" => [Copy(b, ("timing", "would_normal")), Copy(b, ("timing", "would_special"))],
                    _ => [b],
                };
            case "when.sent_to_gy":
                return v("from") == "hand_or_field" ? [Copy(b, ("from", "hand")), Copy(b, ("from", "field"))] : [b];
            case UsedAsMaterialTrigger.BlockId:
                return v("kind") == "any" ? [Copy(b, ("kind", "fusion")), Copy(b, ("kind", "synchro")), Copy(b, ("kind", "link"))] : [b];
            case ThisCardEventsTrigger.BlockId:
                return Ticked(b, optional);
            default:
                return [b];
        }
    }

    /// <summary>The tick-several block: one single-event WHEN per ticked box, in its order.</summary>
    private static List<BlockInstance> Ticked(BlockInstance b, string optional)
    {
        var result = new List<BlockInstance>();
        BlockInstance Summoned(string how, string from = "any") => New(SummonedTrigger.BlockId, optional, ("how", how), ("from", from));
        BlockInstance Event(string ev) => New(ThisCardEventTrigger.BlockId, optional, ("event", ev));
        foreach (var e in ThisCardEventsTrigger.All.Where(e => b.Values.GetValueOrDefault(e.Key) == "true"))
        {
            result.AddRange(e.Key switch
            {
                "normal" or "special" or "ritual" or "fusion" or "synchro" or "xyz" or "link" or "pendulum" => [Summoned(e.Key)],
                "tribute_summon" => [Summoned("tribute")],
                "flip_summon" => [Summoned("flip")],
                "special_gy" => [Summoned("special", "gy")],
                "special_hand" => [Summoned("special", "hand")],
                "sent_gy" => [New("when.sent_to_gy", optional, ("from", "any"))],
                "sent_gy_field" => [New("when.sent_to_gy", optional, ("from", "field"))],
                "sent_gy_hand" => [New("when.sent_to_gy", optional, ("from", "hand"))],
                "sent_gy_deck" => [New("when.sent_to_gy", optional, ("from", "deck"))],
                "destroyed" => [New("when.destroyed", optional, ("by", "any"))],
                "destroyed_battle" => [New("when.destroyed", optional, ("by", "battle"))],
                "destroyed_effect" => [New("when.destroyed", optional, ("by", "effect"))],
                "destroyed_opponent" => [New("when.destroyed", optional, ("by", "opponent"))],
                "returned_hand" => [Event("to_hand")],
                "added_hand" => [Event("added_to_hand")],
                "returned_deck" => [Event("to_deck")],
                "material" => [New(UsedAsMaterialTrigger.BlockId, optional, ("kind", "fusion")), New(UsedAsMaterialTrigger.BlockId, optional, ("kind", "synchro")),
                               New(UsedAsMaterialTrigger.BlockId, optional, ("kind", "link"))],
                "destroys_battle" => [New("when.destroys_by_battle", optional, ("sent", "any"))],
                _ => [Event(e.Key)], // tributed, banished, discarded, flipped, targeted, leaves_field, battle_damage
            });
        }
        return result.Count > 0 ? result : [b];
    }
}
