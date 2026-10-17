using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// "Add to your hand, or Special Summon, 1 ...", "add it to your hand or send it to the GY", read from official scripts:
/// the card is picked, then Duel.SelectOption with the game's own strings (1190 "Add to hand", 1152 "Special Summon"...)
/// decides which of two things happens to it, in an if/else.
/// </summary>
public static partial class ScriptTranslator
{
    /// <summary>The system strings official scripts offer, and what each does to the card.</summary>
    private static readonly Dictionary<string, string> EitherStrings = new()
    {
        ["1190"] = "hand", ["1152"] = "summon", ["1191"] = "grave", ["1192"] = "banish", ["1153"] = "set",
    };

    /// <summary>The way an action line does something to card <paramref name="v"/>, or null.</summary>
    private static string? EitherWay(string line, string v)
    {
        var x = Regex.Escape(v);
        if (Regex.IsMatch(line, $@"^Duel\.SendtoHand\({x},nil,REASON_EFFECT\)$")) return "hand";
        if (Regex.IsMatch(line, $@"^Duel\.SpecialSummon\({x},0,tp,tp,false,false,POS_FACEUP\)$")) return "summon";
        if (Regex.IsMatch(line, $@"^Duel\.SendtoGrave\({x},REASON_EFFECT\)$")) return "grave";
        if (Regex.IsMatch(line, $@"^Duel\.Remove\({x},POS_FACEUP,REASON_EFFECT\)$")) return "banish";
        if (Regex.IsMatch(line, $@"^Duel\.SSet\(tp,{x}\)$")) return "set";
        return null;
    }

    /// <summary>
    /// What an if/elseif around the choice may check besides it: whether the card can go each way and there is room
    /// ("tc:IsAbleToHand()", "ft>0", "Duel.GetLocationCount(tp,LOCATION_MZONE)>0"), which the block checks for each way too.
    /// </summary>
    private static bool OnlyAbilities(string condition, string v)
    {
        var x = Regex.Escape(v);
        var rest = Regex.Replace(condition, @"Duel\.SelectOption\(tp,\d+,\d+\)==[01]", "");
        rest = Regex.Replace(rest, $@"\b{x}:(?:IsAbleTo(?:Hand|Grave|Remove)\(\)|IsCanBeSpecialSummoned\(e,0,tp,false,false\)|IsSSetable\(\))", "");
        rest = Regex.Replace(rest, @"\bft(?:>0|<=0|<1|>=1)|Duel\.GetLocationCount\(tp,LOCATION_MZONE\)(?:>0|<=0|<1)", "");
        rest = Regex.Replace(rest, $@"\b(?:not|and|or|{x})\b|[()\s]", "");
        return rest.Length == 0;
    }

    /// <summary>
    /// "if tc:IsAbleToHand() and (not tc:IsAbleToGrave() or Duel.SelectOption(tp,1190,1191)==0) then SendtoHand ... else
    /// SendtoGrave ... end" at <paramref name="index"/>: the do.either block and the index of its last line, or null when the
    /// lines are not that.
    /// </summary>
    private static (BlockInstance Block, int End)? EitherOf(List<string> body, int index, Dictionary<string, (CardFilter Filter, int Count)> groups, TargetInfo target, bool targetCard)
    {
        var m = Regex.Match(body[index].Trim(), @"^if (.*Duel\.SelectOption\(tp,(\d+),(\d+)\)==([01]).*) then$");
        if (!m.Success || !EitherStrings.TryGetValue(m.Groups[2].Value, out var first) || !EitherStrings.TryGetValue(m.Groups[3].Value, out var second)) return null;
        var picked = m.Groups[4].Value == "0" ? first : second;
        // The two branches: each one action on the card, perhaps shown to the opponent.
        var branches = new List<(string? Condition, List<string> Lines)> { (null, []) };
        var i = index + 1;
        for (; i < body.Count; i++)
        {
            var line = body[i].Trim();
            if (line == "end") break;
            if (line == "else") { branches.Add((null, [])); continue; }
            if (Regex.Match(line, @"^elseif (.+) then$") is { Success: true } em) { branches.Add((em.Groups[1].Value, [])); continue; }
            if (line.StartsWith("Duel.ConfirmCards(", StringComparison.Ordinal) || line.StartsWith("Duel.ShuffleHand(", StringComparison.Ordinal) || line.Length == 0) continue;
            branches[^1].Lines.Add(line);
        }
        if (i >= body.Count || branches.Count != 2 || branches.Any(b => b.Lines.Count != 1)) return null;
        var act = Regex.Match(branches[0].Lines[0], @"\((\w+)[,)]");
        var v = act.Success ? act.Groups[1].Value : "";
        if (branches[0].Lines[0].StartsWith("Duel.SSet(", StringComparison.Ordinal) && Regex.Match(branches[0].Lines[0], @"^Duel\.SSet\(tp,(\w+)\)$") is { Success: true } sm) v = sm.Groups[1].Value;
        if (v.Length == 0 || EitherWay(branches[0].Lines[0], v) != picked || EitherWay(branches[1].Lines[0], v) != (picked == first ? second : first)) return null;
        if (!OnlyAbilities(m.Groups[1].Value, v) || branches[1].Condition is { } elseif && !OnlyAbilities(elseif, v)) return null;
        var block = Block(EitherAction.BlockId).With("first", first).With("second", second);
        if (groups.TryGetValue(v, out var group) && group.Count == 1) block.With("cards", group.Filter.Clone());
        else if (v == "tc" && targetCard && target.Filter is not null) block.With("target", true).With("cards", target.Filter.Clone());
        else return null;
        // "Take 1 ... from your Deck, and either add it to your hand or send it to the GY", as cards from the Deck say it.
        if (block.Filters["cards"].Zones == Model.Zone.Deck && first is "hand" or "grave" && second is "grave" or "banish") block.With("wording", "take");
        return (block, i);
    }
}
