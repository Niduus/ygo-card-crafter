using System.Text.Json;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>What Organize changed on a card, one line per change, for the user to read.</summary>
public sealed class OrganizeResult
{
    public List<string> Changes { get; } = [];
    public bool Changed => Changes.Count > 0;
    /// <summary>Why nothing could be organized, such as a card on a hand-written script.</summary>
    public string? Skipped { get; init; }

    public string Summary => Skipped ?? (Changed ? string.Join("\n", Changes.Select(c => "• " + c)) : "Already organized: nothing to change.");
}

/// <summary>
/// Tidies a card so its text reads like an official one, without changing what it does. The effects go in the order
/// printed cards use: Summon procedures, a Spell/Trap's activation, lasting effects, effects used from the hand, "If this
/// card is Summoned" effects, effects you activate, Quick Effects, other triggers, and effects in the GY or when this card
/// leaves the field, last (the Pendulum Effect box first, as it is printed first); effects that share a once-per-turn are
/// put together. Inside an effect, conditions go in reading order ("During the Main Phase, if ...") and costs as cards
/// print them (costs on this card first), repeated conditions and "Or when" triggers go away, and effects that only differ
/// in what starts them become one with "Or when". DO blocks are never reordered: their order is what the effect does.
/// Every change is checked on a copy first: it is kept only when the compiled script does the same as before
/// (<see cref="ScriptEquivalence"/>), and the strings typed over the generated ones follow their effects.
/// </summary>
public static class CardOrganizer
{
    public static OrganizeResult Organize(CardProject project, CardPack? pack = null)
    {
        if (project.UseCustomLua)
            return new OrganizeResult { Skipped = "This card uses a hand-written script, so its blocks aren't used. Organize works on blocks." };
        var result = new OrganizeResult();
        var work = PackSerializer.Clone(project);
        var start = ScriptCompiler.Compile(work, pack);
        var current = start;
        bool descriptions = false, costs = false;

        // Tries a change on a copy; keeps it only when the card still does the same.
        bool Try(CardProject candidate, string change, bool ignoreDescriptions = false, bool costOrder = false)
        {
            var compiled = ScriptCompiler.Compile(candidate, pack);
            if (!SameProblems(current, compiled) || !ScriptEquivalence.Same(current, compiled, ignoreDescriptions, costOrder)) return false;
            work = candidate;
            current = compiled;
            descriptions |= ignoreDescriptions;
            costs |= costOrder;
            result.Changes.Add(change);
            return true;
        }

        // Older combined WHEN choices: one WHEN per event, which the compiler does anyway.
        var split = PackSerializer.Clone(work);
        if (split.Effects.Count(TriggerSplit.Normalize) is > 0 and var n)
            Try(split, n == 1 ? "A combined WHEN became one WHEN per event, with \"Or when\"." : $"{n} combined WHENs became one WHEN per event, with \"Or when\".", ignoreDescriptions: true);

        for (int i = 0; i < work.Effects.Count; i++)
        {
            var number = i + 1;
            if (Edit(work, i, e => e.AlsoWhen = Distinct(e.AlsoWhen.Where(w => Key(w) != Key(e.When))).ToList()) is { } alsos)
                Try(alsos, $"Effect {number}: removed an \"Or when\" that repeated a trigger.", ignoreDescriptions: true);
            if (Edit(work, i, e => e.If = Distinct(e.If).ToList()) is { } ifs)
                Try(ifs, $"Effect {number}: removed a repeated condition.", ignoreDescriptions: true);
            if (Edit(work, i, e => e.If = e.If.OrderBy(b => ConditionRank(b, e, pack)).ToList()) is { } order)
                Try(order, $"Effect {number}: conditions in reading order (\"During ..., if ...\").", ignoreDescriptions: true);
            if (CostsCanMove(work, i, current) && Edit(work, i, e => e.Cost = e.Cost.OrderBy(CostRank).ToList()) is { } paid)
                Try(paid, $"Effect {number}: costs in the order cards print them (costs on this card first).", ignoreDescriptions: true, costOrder: true);
        }

        MergeTriggers();

        // The effects in printed order.
        var ordered = Order(work, pack);
        if (!ordered.SequenceEqual(work.Effects))
        {
            var candidate = Reordered(work, ordered, pack);
            var moved = ordered.Select(e => work.Effects.IndexOf(e) + 1);
            Try(candidate, $"Effects in the order cards print them: now {string.Join(", ", moved)} (old numbers).");
        }

        // Shared once-per-turn groups numbered 1, 2... in order; a group of one effect is its own limit.
        var groups = PackSerializer.Clone(work);
        if (Renumber(groups))
            Try(groups, "Shared once-per-turn groups renumbered in order; a group used by one effect became its own limit.");

        var labels = PackSerializer.Clone(work);
        if (TidyLabels(labels))
            Try(labels, "Effect names tidied (no \"(copy)\", no blank or repeated names).");

        if (!result.Changed) return result;
        // The whole of it, once more, against the card as it was.
        if (!ScriptEquivalence.Same(start, current, descriptions, costs)) return new OrganizeResult { Skipped = "Organizing would change what the card does, so nothing was changed." };
        project.Effects = work.Effects;
        project.StringOverrides = work.StringOverrides;
        return result;

        // Two effects that differ only in what starts them: "If this card is Normal Summoned" and "If this card is Special
        // Summoned" become "If this card is Normal or Special Summoned". Only when they use the same once-per-turn.
        void MergeTriggers()
        {
            for (int i = 0; i < work.Effects.Count; i++)
                for (int j = i + 1; j < work.Effects.Count; j++)
                {
                    var (a, b) = (work.Effects[i], work.Effects[j]);
                    if (!Mergeable(a, b) || Overridden(work, b, pack)) continue;
                    var candidate = PackSerializer.Clone(work);
                    var merged = candidate.Effects[i];
                    merged.AlsoWhen.AddRange([candidate.Effects[j].When!, .. candidate.Effects[j].AlsoWhen]);
                    candidate.Effects.RemoveAt(j);
                    candidate.StringOverrides = Remap(work, candidate, pack, k => k == j ? -1 : k > j ? k - 1 : k);
                    if (Try(candidate, $"Effects {i + 1} and {j + 1} do the same: one effect with \"or when\".", ignoreDescriptions: true)) j--;
                }
        }
    }

    /// <summary>The same problems (errors, warnings, notes), whichever effect they are on.</summary>
    private static bool SameProblems(CompileResult a, CompileResult b) =>
        a.Diagnostics.Select(d => (d.Severity, d.Message)).Order().SequenceEqual(b.Diagnostics.Select(d => (d.Severity, d.Message)).Order());

    private static string Key(BlockInstance? b) => JsonSerializer.Serialize(b, PackSerializer.Options);

    private static IEnumerable<BlockInstance> Distinct(IEnumerable<BlockInstance> blocks) => blocks.GroupBy(Key).Select(g => g.First());

    /// <summary>A copy of the card with effect i changed, or null when the change leaves it as it is.</summary>
    private static CardProject? Edit(CardProject project, int i, Action<EffectEvent> change)
    {
        var copy = PackSerializer.Clone(project);
        change(copy.Effects[i]);
        return Key(copy.Effects[i]) == Key(project.Effects[i]) ? null : copy;
    }

    private static string Key(EffectEvent e) => JsonSerializer.Serialize(e, PackSerializer.Options);

    // ---------------------------------------------------------------- inside an effect

    /// <summary>"During the Main Phase", then "during your opponent's turn", then the rest as they are.</summary>
    private static int ConditionRank(BlockInstance b, EffectEvent effect, CardPack? pack)
    {
        if (BlockRegistry.Find(b.BlockId) is not { } def) return 2;
        var text = def.Describe(new BlockArgs(def, b, pack) { Effect = effect });
        if (!text.StartsWith("during ", StringComparison.OrdinalIgnoreCase)) return 2;
        return text.Contains(" Phase", StringComparison.Ordinal) ? 0 : 1;
    }

    /// <summary>Costs as printed cards order them: "banish this card from your GY and discard 1 card", "detach 1 material from this card".</summary>
    private static int CostRank(BlockInstance b) =>
        b.BlockId is "cost.this_card" or "cost.reveal_self" || b.Filters.Values.Any(f => f.ThisCard) ? 0 : b.BlockId == "cost.detach" ? 1 : 2;

    /// <summary>Costs that only pay (move cards, pay LP, detach, remove counters) and each pick other cards than this one.</summary>
    private static readonly HashSet<string> Payments =
    [
        "cost.this_card", "cost.reveal_self", "cost.discard", "cost.tribute", "cost.move_cards", "cost.detach", "cost.pay_lp", "cost.remove_counter", "cost.reveal",
        "cost.summon_restriction", "cost.chain_limit", "cost.cannot_be_negated", "cost.this_card_turn",
    ];

    /// <summary>
    /// The costs can be put in another order when each one only pays and at most one of them remembers what it paid (the
    /// effect's label, which a later step may read): the order they are paid in then changes nothing.
    /// </summary>
    private static bool CostsCanMove(CardProject project, int i, CompileResult compiled)
    {
        var costs = project.Effects[i].Cost;
        if (costs.Count < 2 || costs.Any(b => !Payments.Contains(b.BlockId))) return false;
        var cost = Function(compiled.Lua, $"e{i + 1}cost");
        return cost is not null && System.Text.RegularExpressions.Regex.Matches(cost, @"SetLabel").Count <= 1;
    }

    private static string? Function(string lua, string name)
    {
        var start = lua.IndexOf($"function s.{name}(", StringComparison.Ordinal);
        if (start < 0) return null;
        var end = lua.IndexOf("\nend", start, StringComparison.Ordinal);
        return end < 0 ? lua[start..] : lua[start..end];
    }

    // ---------------------------------------------------------------- merging WHENs

    private static bool Mergeable(EffectEvent a, EffectEvent b)
    {
        if (a.When is null || b.When is null || !Starts(a.When) || !Starts(b.When)) return false;
        if (EffectTextGenerator.IsPendulumEffect(a) != EffectTextGenerator.IsPendulumEffect(b)) return false;
        if (a.Limit != b.Limit) return false;
        // Separate effects each with its own once per turn would share one: only when they share it already, or have none.
        var shared = a.Limit is OncePerTurn.Hard or OncePerTurn.Duel && a.SharedLimitGroup > 0 && a.SharedLimitGroup == b.SharedLimitGroup;
        if (a.Limit != OncePerTurn.None && !shared) return false;
        return Json(a.If) == Json(b.If) && Json(a.Cost) == Json(b.Cost) && Json(a.Do) == Json(b.Do) && Key(a.When) != Key(b.When);
    }

    /// <summary>WHEN blocks that activate an effect (not lasting effects, Summon procedures or a Spell/Trap Card's activation).</summary>
    private static bool Starts(BlockInstance when) =>
        BlockRegistry.Find(when.BlockId) is { } def && def is not (WhileFaceUpTrigger or SummonProcedureTrigger or ActivateTrigger) && !DetailedTriggers.IsCardActivation(def, when);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, PackSerializer.Options);

    // ---------------------------------------------------------------- the order of the effects

    /// <summary>
    /// Where an effect goes on a printed card, from the order of effects across every official card text: Summon procedures
    /// first, then a Spell/Trap's activation; lasting effects that protect this card or are rules about it ("Unaffected by",
    /// "Cannot be destroyed", "Cannot be used as material") and effects used from the hand; "If this card is Summoned"
    /// effects; other lasting effects ("gains 500 ATK"); effects you activate; Quick Effects; other triggers; and effects in
    /// the GY or "if this card is sent to the GY / destroyed / banished" last.
    /// </summary>
    public static int Rank(EffectEvent e, CardPack? pack = null)
    {
        if (e.When is null || BlockRegistry.Find(e.When.BlockId) is not { } def) return 10;
        var zone = e.When.Values.GetValueOrDefault("zone");
        if (def is SummonProcedureTrigger) return 0;
        if (def is ActivateTrigger || DetailedTriggers.IsCardActivation(def, e.When)) return 1;
        if (def is WhileFaceUpTrigger) return e.Do.Count > 0 && e.Do.All(b => Rules.Contains(b.BlockId)) ? 2 : 5;
        if (zone == "hand") return 3;
        if (zone is "graveyard" or "banished" or "hand_or_graveyard" || def is NegatedActivationTrigger) return 9;
        if (def is IgnitionTrigger) return 6;
        if (def is QuickTrigger || DetailedTriggers.IsQuick(def, e.When)) return 7;
        if (def is SelfTriggerBlock or FlipTrigger or ThisCardDetailTrigger)
        {
            var text = def.Describe(new BlockArgs(def, e.When, pack) { Effect = e }).ToLowerInvariant();
            if (def is FlipTrigger || text.Contains("summoned", StringComparison.Ordinal) && !Leaves(text)) return 4;
            if (Leaves(text)) return 9;
        }
        return 8;
    }

    /// <summary>Lasting effects printed first: this card's protection and its rules.</summary>
    private static readonly HashSet<string> Rules =
    [
        "passive.immune", "passive.indestructible", "passive.untargetable", "passive.not_destroyed_once", "passive.cannot_be_summoned", "passive.no_tribute_summon",
        "passive.activate_from_hand", "passive.activate_set_turn", "passive.material_ban", "passive.treated_as", "passive.name_becomes", "passive.material_treated_as",
        "passive.double_tribute", "passive.toon", "passive.pendulum_scale",
    ];

    private static bool Leaves(string text) =>
        new[] { "sent to the gy", "destroyed", "banished", "leaves the field", "tributed", "used as", "detached", "returned", "discarded" }
            .Any(w => text.Contains(w, StringComparison.Ordinal));

    /// <summary>
    /// The effects in printed order: the Pendulum Effect box first, then by <see cref="Rank"/>, keeping the order of effects
    /// of the same kind (lasting effects apply in the order they are registered). Effects that share a once-per-turn follow
    /// the first of them.
    /// </summary>
    public static List<EffectEvent> Order(CardProject project, CardPack? pack = null)
    {
        var sorted = project.Effects.Select((e, i) => (e, i))
            .OrderBy(x => EffectTextGenerator.IsPendulumEffect(x.e) ? 0 : 1).ThenBy(x => Rank(x.e, pack)).ThenBy(x => x.i).Select(x => x.e).ToList();
        int? Group(EffectEvent e) => e.SharedLimitGroup > 0 && e.Limit is OncePerTurn.Hard or OncePerTurn.Duel && !Lasting(e) ? e.SharedLimitGroup : null;
        var result = new List<EffectEvent>();
        foreach (var e in sorted)
        {
            if (result.Contains(e)) continue;
            result.Add(e);
            if (Group(e) is { } g) result.AddRange(sorted.Where(o => o != e && Group(o) == g && EffectTextGenerator.IsPendulumEffect(o) == EffectTextGenerator.IsPendulumEffect(e)));
        }
        // The game applies lasting effects in the order they are registered ("becomes 0 ATK" and "gains 500 ATK"), so they
        // keep their order among themselves: the places lasting effects get are filled in their old order.
        var lasting = new Queue<EffectEvent>(project.Effects.Where(Lasting));
        for (int i = 0; i < result.Count; i++)
            if (Lasting(result[i])) result[i] = lasting.Dequeue();
        return result;
    }

    private static bool Lasting(EffectEvent e) => e.When?.BlockId == "when.while_face_up";

    /// <summary>A copy of the card with its effects in this order, the typed-over strings following their effects.</summary>
    private static CardProject Reordered(CardProject project, List<EffectEvent> order, CardPack? pack)
    {
        var copy = PackSerializer.Clone(project);
        var positions = order.Select(e => project.Effects.IndexOf(e)).ToList();
        copy.Effects = positions.Select(p => copy.Effects[p]).ToList();
        copy.StringOverrides = Remap(project, copy, pack, old => positions.IndexOf(old));
        return copy;
    }

    // ---------------------------------------------------------------- strings typed over the generated ones

    /// <summary>The str1..str16 slots by what they belong to: (effect number, option) with option -1 for the effect itself.</summary>
    private static List<(int Effect, int Option)> Slots(CardProject project, CardPack? pack)
    {
        var slots = Enumerable.Range(0, Math.Min(16, project.Effects.Count)).Select(i => (i, -1)).ToList();
        for (int i = 0; i < project.Effects.Count; i++)
        {
            var options = EffectTextGenerator.OptionTexts(project.Effects[i], project.Card, pack).Count;
            for (int o = 0; o < options && slots.Count < 16; o++) slots.Add((i, o));
        }
        if (project.Card.IsExtraDeck && project.Procedure is { Kind: SummonProcedureKind.Xyz, XyzAlternate: true } && slots.Count < 16) slots.Add((-1, -2));
        return slots;
    }

    /// <summary>The typed-over strings of <paramref name="before"/> in the slots of <paramref name="after"/>; newIndex maps an old effect number to its new one (-1: gone).</summary>
    private static List<string> Remap(CardProject before, CardProject after, CardPack? pack, Func<int, int> newIndex)
    {
        if (before.StringOverrides.All(s => s.Length == 0)) return [.. before.StringOverrides];
        var old = Slots(before, pack);
        var now = Slots(after, pack);
        var result = new List<string>();
        for (int s = 0; s < now.Count; s++)
        {
            var from = old.FindIndex(k => (k.Effect < 0 ? k.Effect : newIndex(k.Effect)) == now[s].Effect && k.Option == now[s].Option);
            result.Add(from >= 0 && from < before.StringOverrides.Count ? before.StringOverrides[from] : "");
        }
        while (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>Whether an effect (or one of its options) has a string typed over its generated one.</summary>
    private static bool Overridden(CardProject project, EffectEvent effect, CardPack? pack)
    {
        var i = project.Effects.IndexOf(effect);
        return Slots(project, pack).Select((k, s) => (k, s)).Any(x => x.k.Effect == i && x.s < project.StringOverrides.Count && project.StringOverrides[x.s].Length > 0);
    }

    // ---------------------------------------------------------------- tidying

    /// <summary>Shared once-per-turn groups numbered 1, 2... in order; a group only one effect uses becomes 0 ("its own").</summary>
    private static bool Renumber(CardProject project)
    {
        var used = project.Effects.Where(e => e.SharedLimitGroup > 0).GroupBy(e => e.SharedLimitGroup).ToList();
        var numbers = new Dictionary<int, int>();
        foreach (var g in used.Where(g => g.Count() > 1)) numbers[g.Key] = numbers.Count + 1;
        var changed = false;
        foreach (var e in project.Effects.Where(e => e.SharedLimitGroup > 0))
        {
            var n = numbers.GetValueOrDefault(e.SharedLimitGroup);
            if (n != e.SharedLimitGroup) { e.SharedLimitGroup = n; changed = true; }
        }
        return changed;
    }

    /// <summary>
    /// Effect names (shown in the editor, and the effect's database string when it has no text): no "(copy)" left by
    /// Duplicate, no blank or "Effect" names (they get what the effect does), and no two the same.
    /// </summary>
    private static bool TidyLabels(CardProject project)
    {
        var changed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in project.Effects)
        {
            var label = System.Text.RegularExpressions.Regex.Replace(e.Label ?? "", @"(\s*\(copy\))+\s*$", "").Trim();
            label = System.Text.RegularExpressions.Regex.Replace(label, @"\s{2,}", " ");
            if (label.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(label, @"^(New )?[Ee]ffect( \d+)?$")) label = Named(e) ?? (label.Length == 0 ? "Effect" : label);
            var unique = label;
            for (int n = 2; !seen.Add(unique); n++) unique = $"{label} {n}";
            if (unique != e.Label) { e.Label = unique; changed = true; }
        }
        return changed;
    }

    /// <summary>A name from what the effect does: its first DO block's name, or "Special Summon procedure".</summary>
    private static string? Named(EffectEvent e)
    {
        if (e.When?.BlockId == SummonProcedureTrigger.BlockId) return "Special Summon procedure";
        return e.Do.Select(b => BlockRegistry.Find(b.BlockId)?.Name).FirstOrDefault(n => n is { Length: > 0 });
    }
}
