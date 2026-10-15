using System.Globalization;
using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>What became of one effect of a script.</summary>
public sealed record TranslatedPiece
{
    public required ScriptPiece Piece { get; init; }
    /// <summary>The effect as blocks, or null when it couldn't be translated exactly.</summary>
    public EffectEvent? Effect { get; init; }
    /// <summary>Set for a summon procedure line (Synchro, Xyz, Fusion, Link materials).</summary>
    public SummonProcedure? Procedure { get; init; }
    /// <summary>True for lines that need no block, such as EnableReviveLimit.</summary>
    public bool Ignored { get; init; }
    /// <summary>Further effects the same piece registers (an effect and its clones that do different things).</summary>
    public List<EffectEvent> MoreEffects { get; init; } = [];
    /// <summary>A summoning rule the piece sets: "Must be Fusion Summoned", "Cannot be Normal Summoned/Set".</summary>
    public SummonRule? Rule { get; init; }
    /// <summary>"Cannot be Special Summoned."</summary>
    public bool NoSpecialSummon { get; init; }
    /// <summary>An Equip Spell's targets: what its activation and equip limit allow.</summary>
    public CardFilter? EquipTo { get; init; }
    /// <summary>A card-wide setting the piece stands for, like "You can only control 1 "Card Name"."</summary>
    public Action<CardData>? Setting { get; init; }
    /// <summary>Why it stays as script.</summary>
    public string? Reason { get; init; }
    public bool Translated => Effect is not null || Procedure is not null || Ignored || Rule is not null || NoSpecialSummon || EquipTo is not null || Setting is not null;

    /// <summary>Every effect the piece became.</summary>
    public IEnumerable<EffectEvent> Effects => Effect is null ? MoreEffects : [Effect, .. MoreEffects];
}

public sealed class TranslationResult
{
    public List<TranslatedPiece> Pieces { get; } = [];
    public bool Complete => Pieces.Count > 0 && Pieces.All(p => p.Translated);
    public int TranslatedCount => Pieces.Count(p => p.Translated);
}

/// <summary>
/// Turns card scripts into event-sheet blocks: each effect whose trigger, limit, condition, cost, targeting and
/// operation match patterns the blocks express exactly becomes a WHEN/IF/COST/DO effect. Anything else is left as
/// script with the reason, so a translation never silently changes what a card does.
/// </summary>
public static partial class ScriptTranslator
{
    private sealed class Untranslatable(string reason) : Exception(reason);

    /// <summary>The passcode of the script being read, for "not c:IsCode(id)" ("except this card's name").</summary>
    [ThreadStatic] private static long _sourceId;

    private static Untranslatable No(string reason) => new(reason);

    public static TranslationResult Translate(string lua, long id, CardData card, IReadOnlyList<string>? strings = null)
    {
        var result = new TranslationResult();
        _sourceId = id;
        var pieces = LuaPieces.Split(lua, id, strings);
        foreach (var piece in pieces)
        {
            try
            {
                result.Pieces.Add(TranslatePiece(piece, card));
            }
            catch (Untranslatable ex)
            {
                result.Pieces.Add(new TranslatedPiece { Piece = piece, Reason = ex.Message });
            }
        }
        return result;
    }

    /// <summary>
    /// Turns a card's hand-written script (<see cref="CardProject.CustomLua"/>) into blocks when every part of it
    /// translates and the blocks compile cleanly. The script itself is kept, so the card can switch back to it.
    /// Returns the translation; <paramref name="applied"/> says whether the card now uses blocks.
    /// </summary>
    public static TranslationResult ApplyTo(CardProject project, CardPack? pack, out bool applied)
    {
        applied = false;
        var result = Translate(project.CustomLua, project.Card.Id, project.Card, project.ScriptStrings);
        if (!result.Complete) return result;
        var effects = result.Pieces.SelectMany(p => p.Effects).ToList();
        // "Must be Special Summoned by its own way" includes "Cannot be Normal Summoned/Set": the strictest rule wins.
        var rule = result.Pieces.Where(p => p.Rule is not null).Select(p => p.Rule!.Value).DefaultIfEmpty(SummonRule.Normal).Max() is var mr && mr != SummonRule.Normal ? mr : (SummonRule?)null;
        var procedure = result.Pieces.LastOrDefault(p => p.Procedure is not null)?.Procedure ?? project.Procedure;
        var check = PackSerializer.Clone(project.Card);
        if (rule is { } cr) check.SummonRule = cr;
        check.CannotBeSpecialSummoned |= result.Pieces.Any(p => p.NoSpecialSummon);
        foreach (var p in result.Pieces) p.Setting?.Invoke(check);
        var compiled = ScriptCompiler.Compile(new CardProject { Card = check, Procedure = procedure, Effects = effects, ScriptStrings = project.ScriptStrings }, pack);
        if (compiled.HasErrors) return result;
        project.Effects = effects;
        project.Procedure = procedure;
        if (rule is { } r) project.Card.SummonRule = r;
        if (result.Pieces.Any(p => p.NoSpecialSummon)) project.Card.CannotBeSpecialSummoned = true;
        foreach (var p in result.Pieces) p.Setting?.Invoke(project.Card);
        if (EquipTarget(result) is { } equip) project.EquipFilter = equip;
        project.UseCustomLua = false;
        applied = true;
        return result;
    }

    /// <summary>What an Equip Spell can be equipped to: whose monsters from its activation, what they must be from its equip limit.</summary>
    private static CardFilter? EquipTarget(TranslationResult result)
    {
        var filters = result.Pieces.Where(p => p.EquipTo is not null).Select(p => p.EquipTo!).ToList();
        if (filters.Count == 0) return null;
        var activation = filters.FirstOrDefault(f => f.Zones != Model.Zone.None);
        var limit = filters.LastOrDefault(f => f.Zones == Model.Zone.None);
        var result2 = (limit ?? activation)!.Clone();
        result2.Owner = activation?.Owner ?? CardOwner.Either;
        result2.Zones = Model.Zone.MonsterZone;
        result2.CardType = FilterCardType.Monster;
        result2.FaceUpOnly = false;
        return result2;
    }

    /// <summary>One line per part that stays script, for telling the user why.</summary>
    public static IEnumerable<string> Untranslated(TranslationResult result) =>
        result.Pieces.Where(p => !p.Translated).Select(p => $"\"{p.Piece.Title}\": {p.Reason}");

    // ---------------------------------------------------------------- one piece

    private sealed record Functions(Dictionary<string, (string[] Params, List<string> Body)> ByName, string Prefix, long Id)
    {
        public (string[] Params, List<string> Body) Get(string reference)
        {
            var name = Regex.Match(reference.Trim(), $@"^{Regex.Escape(Prefix)}\.(\w+)$");
            if (!name.Success || !ByName.TryGetValue(name.Groups[1].Value, out var fn)) throw No($"it calls {reference.Trim()}, which isn't a simple function of this card");
            return fn;
        }
    }

    private static TranslatedPiece TranslatePiece(ScriptPiece piece, CardData card)
    {
        var lines = piece.Code.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)).ToList();
        var fns = new Functions(ParseFunctions(piece), piece.Prefix, piece.SourceId);
        if (lines.Count == 1 && TranslateStatement(lines[0], card, fns) is { } single) return single with { Piece = piece };

        // Every effect the piece creates or clones, with its settings, and the ones it registers on the card.
        var vars = new Dictionary<string, Dictionary<string, string>>();
        var registered = new List<string>();
        foreach (var line in lines)
        {
            Match m;
            if ((m = Regex.Match(line, @"^local\s+(\w+)\s*=\s*Effect\.CreateEffect\(c\)$")).Success) { vars[m.Groups[1].Value] = []; continue; }
            if ((m = Regex.Match(line, @"^local\s+(\w+)\s*=\s*(\w+):Clone\(\)$")).Success && vars.TryGetValue(m.Groups[2].Value, out var from))
            {
                vars[m.Groups[1].Value] = new Dictionary<string, string>(from);
                continue;
            }
            if ((m = Regex.Match(line, @"^(\w+):Set(\w+)\((.*)\)$")).Success && vars.TryGetValue(m.Groups[1].Value, out var set))
            {
                set[m.Groups[2].Value] = m.Groups[3].Value.Trim();
                continue;
            }
            if ((m = Regex.Match(line, @"^c:RegisterEffect\((\w+)\)$")).Success && vars.ContainsKey(m.Groups[1].Value)) { registered.Add(m.Groups[1].Value); continue; }
            throw No(vars.Count == 0 ? $"it isn't a single effect created on this card ({Short(Regex.Replace(line, @"\(.*", "(…)"))})" : $"it does something beside its effects ({Short(line)})");
        }
        if (registered.Count == 0) throw No("it isn't a single effect created on this card");
        var sets = registered.Select(r => vars[r]).ToList();

        // Copies that differ only in what they apply to are one effect on the card ("ATK/DEF", "Normal or Special").
        var merge = Merge.Of(sets);
        if (merge is not null) return TranslateSet(piece, sets[0], merge, fns, card);
        var parts = sets.Select(set => TranslateSet(piece, set, Merge.None, fns, card)).ToList();
        if (parts.Count == 1) return parts[0];
        var effects = parts.SelectMany(p => p.Effects).ToList();
        return new TranslatedPiece
        {
            Piece = piece,
            Effect = effects.FirstOrDefault(),
            MoreEffects = effects.Skip(1).ToList(),
            Rule = parts.LastOrDefault(p => p.Rule is not null)?.Rule,
            Ignored = effects.Count == 0 && parts.All(p => p.Ignored || p.Rule is not null),
        };
    }

    /// <summary>How copies of one effect combine into one block: the codes they use, the first being the main one.</summary>
    private sealed record Merge(bool AlsoSpecial, bool AlsoFlip, bool BothStats, bool BothIndestructible, bool CannotTribute)
    {
        public static readonly Merge None = new(false, false, false, false, false);

        public static Merge? Of(List<Dictionary<string, string>> sets)
        {
            if (sets.Count < 2) return null;
            var rest = sets.Select(s => s.Where(kv => kv.Key != "Code").OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToList()).ToList();
            if (rest.Any(r => !r.SequenceEqual(rest[0]))) return null;
            var codes = sets.Select(s => s.GetValueOrDefault("Code") ?? "").ToList();
            bool Is(params string[] want) => codes.Count == want.Length && codes.ToHashSet().SetEquals(want) && codes[0] == want[0];
            if (Is("EVENT_SUMMON_SUCCESS", "EVENT_SPSUMMON_SUCCESS")) return None with { AlsoSpecial = true };
            if (Is("EVENT_SUMMON_SUCCESS", "EVENT_SPSUMMON_SUCCESS", "EVENT_FLIP_SUMMON_SUCCESS") || Is("EVENT_SUMMON_SUCCESS", "EVENT_FLIP_SUMMON_SUCCESS", "EVENT_SPSUMMON_SUCCESS"))
                return None with { AlsoSpecial = true, AlsoFlip = true };
            if (Is("EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE")) return None with { BothStats = true };
            if (Is("EFFECT_INDESTRUCTABLE_BATTLE", "EFFECT_INDESTRUCTABLE_EFFECT")) return None with { BothIndestructible = true };
            if (Is("EFFECT_UNRELEASABLE_SUM", "EFFECT_UNRELEASABLE_NONSUM")) return None with { CannotTribute = true };
            return null;
        }
    }

    /// <summary>One effect's settings as blocks.</summary>
    private static TranslatedPiece TranslateSet(ScriptPiece piece, Dictionary<string, string> set, Merge merge, Functions fns, CardData card)
    {
        if (set.ContainsKey("Reset") || set.ContainsKey("Label") || set.ContainsKey("LabelObject"))
            throw No("it keeps extra state the blocks don't model");
        bool alsoSpecial = merge.AlsoSpecial;

        var effect = new EffectEvent { Label = piece.Title };
        var type = Flags(set.GetValueOrDefault("Type") ?? throw No("it has no effect type"));
        var code = set.GetValueOrDefault("Code") ?? "";
        var range = set.GetValueOrDefault("Range");
        var props = Flags(set.GetValueOrDefault("Property") ?? "");
        effect.Limit = CountLimit(set.GetValueOrDefault("CountLimit"), piece.SourceId, out var group);
        effect.SharedLimitGroup = group;

        // "Activate this card" with nothing else: Card Crafter adds that itself for Continuous, Field and Equip cards.
        if (type.SetEquals(["EFFECT_TYPE_ACTIVATE"]) && code == "EVENT_FREE_CHAIN" && !set.ContainsKey("Operation") && !set.ContainsKey("Target")
            && !set.ContainsKey("Cost") && !set.ContainsKey("Condition") && effect.Limit == OncePerTurn.None)
            return new TranslatedPiece { Piece = piece, Ignored = true };

        // "Must be Fusion Summoned" and the like: a rule on the Card tab, not an effect.
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_SPSUMMON_CONDITION"
            && set.GetValueOrDefault("Value") is "aux.FALSE" or "aux.fuslimit" or "aux.synlimit" or "aux.xyzlimit" or "aux.linklimit" or "aux.ritlimit")
            return new TranslatedPiece { Piece = piece, Rule = SummonRule.ProcedureOnly };
        // No value at all: "Cannot be Special Summoned."
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_SPSUMMON_CONDITION" && !set.ContainsKey("Value"))
            return new TranslatedPiece { Piece = piece, NoSpecialSummon = true };

        // An Equip Spell's activation (equip to a face-up monster) and its equip limit: the "Equip to" picker on the Card tab.
        if (card.Kind == CardKind.Spell && card.SpellKind == SpellKind.Equip)
        {
            if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_EQUIP_LIMIT")
            {
                var value = set.GetValueOrDefault("Value");
                if (value == "1") return new TranslatedPiece { Piece = piece, EquipTo = new CardFilter { CardType = FilterCardType.Monster } };
                if (value is not null)
                {
                    var limit = Filter(value, fns);
                    limit.Zones = Model.Zone.None;
                    return new TranslatedPiece { Piece = piece, EquipTo = limit };
                }
            }
            if (type.SetEquals(["EFFECT_TYPE_ACTIVATE"]) && code == "EVENT_FREE_CHAIN" && (set.GetValueOrDefault("Category") ?? "").Contains("CATEGORY_EQUIP")
                && set.TryGetValue("Target", out var etg) && set.TryGetValue("Operation", out var eop) && !set.ContainsKey("Cost") && !set.ContainsKey("Condition"))
            {
                var t = Targeting(etg, fns);
                var (_, body) = fns.Get(eop);
                var rest = body.Select(l => l.Trim()).Where(l => l.Length > 0 && l is not ("local c=e:GetHandler()" or "local tc=Duel.GetFirstTarget()" or "end")
                    && !l.StartsWith("if ", StringComparison.Ordinal)).ToList();
                if (t.Filter is not null && t.Count == 1 && rest.Count == 1 && rest[0] is "Duel.Equip(tp,c,tc)" or "Duel.Equip(tp,e:GetHandler(),tc)")
                    return new TranslatedPiece { Piece = piece, EquipTo = t.Filter };
            }
        }

        // A built-in way to Special Summon this card, like Cyber Dragon's.
        if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && code == "EFFECT_SPSUMMON_PROC")
            return new TranslatedPiece { Piece = piece, Effect = Procedure(effect, set, range, fns, card) };

        // Lasting effects: no activation at all.
        if (!type.Overlaps(["EFFECT_TYPE_ACTIVATE", "EFFECT_TYPE_IGNITION", "EFFECT_TYPE_QUICK_O", "EFFECT_TYPE_QUICK_F", "EFFECT_TYPE_TRIGGER_O", "EFFECT_TYPE_TRIGGER_F", "EFFECT_TYPE_FLIP"]))
            return new TranslatedPiece { Piece = piece, Effect = Passive(effect, type, code, set, range, props, fns, card, merge) };

        var condition = set.GetValueOrDefault("Condition");
        // aux.dscon: a Quick Effect that can also be used in the Damage Step.
        bool damageStep = false;
        if (condition == "aux.dscon" && (type.Contains("EFFECT_TYPE_QUICK_O") || type.SetEquals(["EFFECT_TYPE_ACTIVATE"])) && props.Contains("EFFECT_FLAG_DAMAGE_STEP"))
        {
            condition = null;
            damageStep = true;
        }
        var (when, usedCondition) = Trigger(type, code, range, condition, alsoSpecial, fns);
        if (merge.AlsoFlip)
        {
            if (when.BlockId != "when.summoned") throw No("it registers more than one effect");
            when = when.With("how", "any");
        }
        if (damageStep) when = when.With("damage_step", true);
        effect.When = when;
        if (condition is not null && !usedCondition) effect.If.AddRange(Conditions(condition, fns));
        if (set.GetValueOrDefault("Cost") is { } cost) effect.Cost.AddRange(Costs(cost, fns));
        var targeting = set.TryGetValue("Target", out var tg) ? Targeting(tg, fns) : new TargetInfo();
        if (!set.TryGetValue("Operation", out var op)) throw No("it has no operation");
        effect.Do.AddRange(Operation(op, fns, targeting, card));
        if (effect.Do.Count == 0) throw No("its operation does nothing the blocks recognise");
        return new TranslatedPiece { Piece = piece, Effect = effect };
    }

    /// <summary>Single statements in initial_effect: summon procedures and revive limits.</summary>
    private static TranslatedPiece? TranslateStatement(string line, CardData card, Functions fns)
    {
        Match m;
        // Lines that only describe the card, or that Card Crafter writes itself from the Card tab.
        if (line == "c:EnableUnsummonable()") return new TranslatedPiece { Piece = null!, Rule = SummonRule.SpecialSummonOnly };
        if ((m = Regex.Match(line, @"^c:SetUniqueOnField\(1,0,(id|\d+)\)$")).Success && (m.Groups[1].Value == "id" || Int64(m.Groups[1].Value) == _sourceId))
            return new TranslatedPiece { Piece = null!, Setting = c => c.OnlyOneOnField = true };
        if ((m = Regex.Match(line, @"^c:SetSPSummonOnce\((id|\d+)\)$")).Success && (m.Groups[1].Value == "id" || Int64(m.Groups[1].Value) == _sourceId))
            return new TranslatedPiece { Piece = null!, Setting = c => c.SpecialSummonOncePerTurn = true };
        // Gemini and Spirit monsters get these lines from the Card tab's abilities.
        if ((line == "aux.EnableDualAttribute(c)" && card.Abilities.HasFlag(MonsterAbility.Gemini))
            || (line == "aux.EnableSpiritReturn(c,EVENT_SUMMON_SUCCESS,EVENT_FLIP)" && card.Abilities.HasFlag(MonsterAbility.Spirit)))
            return new TranslatedPiece { Piece = null!, Ignored = true };
        if (line is "c:EnableReviveLimit()" or "aux.EnablePendulumAttribute(c)" || line.StartsWith("aux.AddCodeList(c,", StringComparison.Ordinal)
            || line.StartsWith("aux.AddSetNameMonsterList(c,", StringComparison.Ordinal) || line.StartsWith("aux.AddMaterialCodeList(c,", StringComparison.Ordinal))
            return new TranslatedPiece { Piece = null!, Ignored = true };
        var filtered = Regex.Match(line, @"^aux\.Add(Xyz|Link)Procedure\(c,aux\.FilterBoolFunction\((Card\.\w+),(\w+)\),(\d+)(?:,(\d+))?(?:,nil,nil,(\d+))?\)$");
        if (filtered.Success)
        {
            var f = new CardFilter { CardType = FilterCardType.Monster };
            Term($"c:{filtered.Groups[2].Value[5..]}({filtered.Groups[3].Value})", f);
            var xyz = filtered.Groups[1].Value == "Xyz";
            var n = Int(filtered.Groups[4].Value);
            if (xyz && n != card.Level) throw No("its Xyz Level differs from the card's");
            var min = xyz ? Int(filtered.Groups[5].Value) : n;
            var max = xyz ? (filtered.Groups[6].Success ? Int(filtered.Groups[6].Value) : 0) : (filtered.Groups[5].Success ? Int(filtered.Groups[5].Value) : 0);
            return new TranslatedPiece { Piece = null!, Procedure = new SummonProcedure { Kind = xyz ? SummonProcedureKind.Xyz : SummonProcedureKind.Link, MaterialFilter = f, MinMaterials = min, MaxMaterials = max } };
        }
        m = Regex.Match(line, @"^aux\.AddSynchroProcedure\(c,aux\.Tuner\(nil\),aux\.NonTuner\(nil\),(\d+)(?:,(\d+))?\)$");
        if (m.Success) return Proc(SummonProcedureKind.Synchro, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0);
        m = Regex.Match(line, @"^aux\.AddXyzProcedure\(c,nil,(\d+),(\d+)(?:,nil,nil,(\d+))?\)$");
        if (m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == card.Level)
            return Proc(SummonProcedureKind.Xyz, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
        m = Regex.Match(line, @"^aux\.AddLinkProcedure\(c,(nil|aux\.FilterBoolFunction\(Card\.IsLinkType,TYPE_EFFECT\)),(\d+)(?:,(\d+))?\)$");
        if (m.Success) return Proc(SummonProcedureKind.Link, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
        m = Regex.Match(line, @"^aux\.AddFusionProcCode(?:2|3|4)?\(c,([\d,]+),(?:true|false),(?:true|false)\)$");
        if (m.Success)
        {
            var codes = m.Groups[1].Value.Split(',').Select(s => long.Parse(s, CultureInfo.InvariantCulture)).ToList();
            return new TranslatedPiece { Piece = null!, Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = codes, MinMaterials = codes.Count } };
        }
        if (MaterialProcedure(line, card, fns) is { } procedure) return new TranslatedPiece { Piece = null!, Procedure = procedure };
        return null;

        static TranslatedPiece Proc(SummonProcedureKind kind, int min, int max) =>
            new() { Piece = null!, Procedure = new SummonProcedure { Kind = kind, MinMaterials = min, MaxMaterials = max } };
    }

    /// <summary>Splits "a,f(b,c),d" at its top-level commas.</summary>
    private static List<string> SplitArgs(string args)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case '(' or '{': depth++; break;
                case ')' or '}': depth--; break;
                case ',' when depth == 0: parts.Add(args[start..i].Trim()); start = i + 1; break;
            }
        }
        parts.Add(args[start..].Trim());
        return parts;
    }

    /// <summary>
    /// A material in a Summon procedure: nil (any monster), a passcode, aux.FilterBoolFunction(Card.IsRace,RACE_DRAGON)
    /// or a filter function of the script.
    /// </summary>
    private static CardFilter Material(string expr, Functions fns)
    {
        var f = new CardFilter { CardType = FilterCardType.Monster };
        Match m;
        if (expr is "nil" or "aux.TRUE") return f;
        if (Regex.IsMatch(expr, @"^\d+$")) { f.CardCode = Int64(expr); return f; }
        if ((m = Regex.Match(expr, @"^aux\.FilterBoolFunction\(Card\.(\w+),([\w+]+)\)$")).Success) { Term($"c:{m.Groups[1].Value}({m.Groups[2].Value})", f); f.CardType = FilterCardType.Monster; return f; }
        if ((m = Regex.Match(expr, @"^Card\.(Is\w+)$")).Success && m.Groups[1].Value != "IsFaceup") throw No($"its materials are picked by {expr}");
        var filter = Filter(expr, fns);
        filter.CardType = FilterCardType.Monster;
        if (filter.FaceUpOnly) throw No("its materials are picked in a way the blocks can't");
        return filter;
    }

    /// <summary>"aux.NonTuner(nil)", "aux.NonTuner(Card.IsAttribute,ATTRIBUTE_FIRE)", "aux.NonTuner(s.filter)".</summary>
    private static CardFilter? TunerSide(string expr, string wrapper, Functions fns)
    {
        if (expr == "nil" && wrapper == "Tuner") return new CardFilter { CardType = FilterCardType.Monster };
        var m = Regex.Match(expr, $@"^aux\.{wrapper}\((.*)\)$");
        if (!m.Success) return wrapper == "Tuner" ? Material(expr, fns) : null;
        var args = SplitArgs(m.Groups[1].Value);
        return args.Count switch
        {
            1 => Material(args[0], fns),
            2 when args[0].StartsWith("Card.", StringComparison.Ordinal) => Material($"aux.FilterBoolFunction({args[0]},{args[1]})", fns),
            _ => null,
        };
    }

    /// <summary>The Synchro, Xyz, Link and Fusion procedure lines of Extra Deck monsters, as the Summoning conditions section.</summary>
    private static SummonProcedure? MaterialProcedure(string line, CardData card, Functions fns)
    {
        var m = Regex.Match(line, @"^aux\.(AddSynchroProcedure|AddXyzProcedure|AddLinkProcedure|AddFusionProcFun2|AddFusionProcFunRep|AddFusionProcFunRep2|AddFusionProcCodeFun|AddFusionProcCodeRep|AddFusionProcMix)\(c,(.*)\)$");
        if (!m.Success) return null;
        var args = SplitArgs(m.Groups[2].Value);
        int Num(int i) => args.Count > i && Regex.IsMatch(args[i], @"^\d+$") ? Int(args[i]) : throw No("its material count is unusual");
        bool Absent(int i) => args.Count <= i || args[i] == "nil";
        MaterialSlot Slot(string expr, int min = 1, int max = 1) => new() { Filter = Material(expr, fns), Min = min, Max = max };
        switch (m.Groups[1].Value)
        {
            case "AddSynchroProcedure" when args.Count is 3 or 4:
            {
                var tuner = TunerSide(args[0], "Tuner", fns) ?? throw No("its Tuner is picked in a way the blocks can't");
                var non = TunerSide(args[1], "NonTuner", fns) ?? throw No("its non-Tuners are picked in a way the blocks can't");
                return new SummonProcedure { Kind = SummonProcedureKind.Synchro, TunerFilter = tuner, MaterialFilter = non, MinMaterials = Num(2), MaxMaterials = args.Count == 4 ? Num(3) : 0 };
            }
            case "AddXyzProcedure" when args.Count == 3 || (args.Count == 6 && Absent(3) && Absent(4)):
            {
                var lv = Num(1);
                return new SummonProcedure
                {
                    Kind = SummonProcedureKind.Xyz, MaterialFilter = Material(args[0], fns), MinMaterials = Num(2),
                    MaxMaterials = args.Count == 6 ? Num(5) : 0, XyzMaterialLevel = lv == card.Level ? 0 : lv,
                };
            }
            case "AddLinkProcedure" when args.Count is 2 or 3 or 4:
            {
                var proc = new SummonProcedure { Kind = SummonProcedureKind.Link, MaterialFilter = Material(args[0], fns), MinMaterials = Num(1), MaxMaterials = Absent(2) ? 0 : Num(2) };
                if (args.Count == 4)
                {
                    // "including a "Sky Striker" monster": the group check asks for one card of a kind.
                    var (_, body) = fns.Get(args[3]);
                    var text = string.Join(" ", body.Select(l => l.Trim()));
                    var inc = Regex.Match(text, @"^return g:IsExists\(([\w.]+)(?:,1,nil(?:,([\w+]+))?)?\)$");
                    if (!inc.Success) throw No("its materials are checked as a group in a way the blocks can't");
                    proc.Including = true;
                    proc.IncludeFilter = inc.Groups[1].Value.StartsWith("Card.", StringComparison.Ordinal)
                        ? Material($"aux.FilterBoolFunction({inc.Groups[1].Value},{(inc.Groups[2].Success ? inc.Groups[2].Value : throw No("its materials are checked as a group in a way the blocks can't"))})", fns)
                        : Material(inc.Groups[1].Value, fns);
                }
                return proc;
            }
            case "AddFusionProcFun2" when args.Count == 3:
                return Fusion([Slot(args[0]), Slot(args[1])]);
            case "AddFusionProcFunRep" when args.Count == 3:
                return Fusion([Slot(args[0], Num(1), Num(1))]);
            case "AddFusionProcFunRep2" when args.Count == 4:
                return Fusion([Slot(args[0], Num(1), Num(2) >= 63 ? 0 : Num(2))]);
            case "AddFusionProcCodeFun" when args.Count == 5:
                return Fusion([Slot(args[0]), Slot(args[1], Num(2), Num(2))]);
            case "AddFusionProcCodeRep" when args.Count == 4:
                return Fusion([Slot(args[0], Num(1), Num(1))]);
            case "AddFusionProcMix" when args.Count >= 4 && args[0] is "true" or "false" && args[1] is "true" or "false":
                return Fusion(args.Skip(2).Select(a => Slot(a)).ToList());
        }
        throw No($"its Summon procedure is written in a way the blocks can't ({Short(line)})");

        static SummonProcedure Fusion(List<MaterialSlot> slots)
        {
            if (slots.Count(s => s.Repeats) > 1) throw No("more than one of its Fusion Materials repeats");
            return new SummonProcedure { Kind = SummonProcedureKind.Fusion, Materials = slots, MinMaterials = slots.Sum(s => s.Min) };
        }
    }

    // ---------------------------------------------------------------- parts

    private static HashSet<string> Flags(string expr) => expr.Split('+').Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet();

    private static OncePerTurn CountLimit(string? args, long id, out int group)
    {
        group = 0;
        if (args is null) return OncePerTurn.None;
        var parts = args.Split(',').Select(a => a.Trim()).ToArray();
        if (parts[0] != "1") throw No("it can be used more than once per turn");
        if (parts.Length == 1) return OncePerTurn.Soft;
        var key = parts[1].Replace(id.ToString(CultureInfo.InvariantCulture), "id").Replace("+EFFECT_COUNT_CODE_OATH", "").Replace(" ", "");
        // The second and third codes are often written out: the passcode plus 1, 2, 100 or 200.
        if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var literal) && (literal - id) is 1 or 2 or 100 or 200)
            key = "id+" + (literal - id).ToString(CultureInfo.InvariantCulture);
        group = key switch { "id" => 1, "id+1" or "id+o" or "id+100" => 2, "id+2" or "id+o*2" or "id+200" => 3, _ => throw No($"its once-per-turn code is unusual ({args})") };
        return OncePerTurn.Hard;
    }

    private static string Zone(string? range) => range switch
    {
        null => "auto",
        "LOCATION_MZONE" => "monster_zone",
        "LOCATION_HAND" => "hand",
        "LOCATION_GRAVE" => "graveyard",
        "LOCATION_SZONE" => "spell_trap_zone",
        "LOCATION_FZONE" => "field_zone",
        "LOCATION_PZONE" => "pendulum_zone",
        "LOCATION_REMOVED" => "banished",
        "LOCATION_HAND+LOCATION_GRAVE" or "LOCATION_GRAVE+LOCATION_HAND" => "hand_or_graveyard",
        _ => throw No($"it works from {range}"),
    };

    private static BlockInstance Block(string id) => BlockRegistry.Find(id)!.CreateInstance();

    /// <summary>The WHEN block, and whether the trigger used up the effect's condition (e.g. "sent from the field").</summary>
    private static (BlockInstance When, bool UsedCondition) Trigger(HashSet<string> type, string code, string? range, string? condition, bool alsoSpecial, Functions fns)
    {
        string? body = condition is null || condition.StartsWith("aux.", StringComparison.Ordinal) ? condition : Body(condition, fns);
        if (code == "EVENT_CHAINING" && body is not null && ChainBody(body) is var stripped && stripped != body)
        {
            body = stripped;
            // Only the Negate block's own checks: any activation, and the condition is used up.
            if (body is null && type.Overlaps(["EFFECT_TYPE_QUICK_O", "EFFECT_TYPE_ACTIVATE"]))
                return (type.Contains("EFFECT_TYPE_ACTIVATE") ? Block("when.activate").With("timing", "chain_either")
                    : Block("when.quick").With("timing", "chain_either").With("zone", Zone(range)), true);
        }
        if (code == "EVENT_CHAINING" && body is not null && ChainWho(body) is { } chain && type.Overlaps(["EFFECT_TYPE_QUICK_O", "EFFECT_TYPE_ACTIVATE"]))
        {
            var block = type.Contains("EFFECT_TYPE_ACTIVATE") ? Block("when.activate") : Block("when.quick").With("zone", Zone(range));
            return (block.With("timing", chain), true);
        }
        (string Timing, bool Used) Timing(string c) => (c, body) switch
        {
            ("EVENT_FREE_CHAIN", _) => ("any", false),
            ("EVENT_ATTACK_ANNOUNCE", "return Duel.GetAttacker():IsControler(1-tp)" or "return tp~=Duel.GetTurnPlayer()" or "return Duel.GetTurnPlayer()~=tp") => ("attack", true),
            ("EVENT_ATTACK_ANNOUNCE", "return Duel.GetAttacker():IsControler(1-tp) and Duel.GetAttackTarget()==nil") => ("attack_direct", true),
            ("EVENT_ATTACK_ANNOUNCE", null) => ("attack_any", false),
            ("EVENT_SUMMON_SUCCESS", "return eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)") => (alsoSpecial ? "summon" : "summon_normal", true),
            ("EVENT_SPSUMMON_SUCCESS", "return eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)") => ("summon_special", true),
            ("EVENT_FLIP_SUMMON_SUCCESS", "return eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)") => ("summon_flip", true),
            ("EVENT_CHAINING", null) => ("chain_either", false),
            ("EVENT_CHAINING", "return rp==1-tp") => ("chain_any", true),
            ("EVENT_CHAINING", "return rp==1-tp and re:IsActiveType(TYPE_MONSTER)") => ("chain_monster", true),
            ("EVENT_CHAINING", "return rp==1-tp and re:IsHasType(EFFECT_TYPE_ACTIVATE)") => ("chain_spelltrap", true),
            _ => throw No($"it starts on {c}{(body is null ? "" : $" ({Short(body)})")}"),
        };

        if (type.SetEquals(["EFFECT_TYPE_ACTIVATE"]))
        {
            var (t, used) = Timing(code);
            return (Block("when.activate").With("timing", t), used);
        }
        if (type.SetEquals(["EFFECT_TYPE_IGNITION"]))
            return (Block("when.ignition").With("zone", Zone(range)), false);
        if (type.SetEquals(["EFFECT_TYPE_QUICK_O"]))
        {
            var (t, used) = Timing(code);
            return (Block("when.quick").With("timing", t).With("zone", Zone(range)), used);
        }
        if (type.SetEquals(["EFFECT_TYPE_SINGLE", "EFFECT_TYPE_FLIP"]))
            return (Block("when.flip"), false);
        if (type.SetEquals(["EFFECT_TYPE_SINGLE", "EFFECT_TYPE_FLIP", "EFFECT_TYPE_TRIGGER_O"]))
            return (Block("when.flip").With("optional", true), false);

        bool optional = type.Contains("EFFECT_TYPE_TRIGGER_O");
        if (type.SetEquals(["EFFECT_TYPE_SINGLE", optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F"]))
        {
            switch (code)
            {
                case "EVENT_SUMMON_SUCCESS": return (Block("when.summoned").With("how", alsoSpecial ? "normal_or_special" : "normal").With("optional", optional), false);
                case "EVENT_SPSUMMON_SUCCESS" when body is not null && SummonTypes.TryGetValue(body, out var how):
                    return (Block("when.summoned").With("how", how).With("optional", optional), true);
                case "EVENT_SPSUMMON_SUCCESS": return (Block("when.summoned").With("how", "special").With("optional", optional), false);
                case "EVENT_FLIP_SUMMON_SUCCESS": return (Block("when.summoned").With("how", "flip").With("optional", optional), false);
                case "EVENT_TO_GRAVE":
                    var from = body switch
                    {
                        null => "any",
                        "return e:GetHandler():IsPreviousLocation(LOCATION_ONFIELD)" => "field",
                        "return e:GetHandler():IsPreviousLocation(LOCATION_HAND)" => "hand",
                        _ => throw No($"its \"sent to the GY\" condition is unusual ({Short(body)})"),
                    };
                    return (Block("when.sent_to_gy").With("from", from).With("optional", optional), body is not null);
                case "EVENT_DESTROYED":
                    var by = body switch
                    {
                        null => "any",
                        "return e:GetHandler():IsReason(REASON_BATTLE)" => "battle",
                        "return e:GetHandler():IsReason(REASON_EFFECT)" or "return bit.band(r,REASON_EFFECT)~=0" => "effect",
                        "return bit.band(r,REASON_BATTLE)~=0" => "battle",
                        "return bit.band(r,REASON_EFFECT+REASON_BATTLE)~=0" => "any",
                        "return rp==1-tp and e:GetHandler():IsPreviousControler(tp)" or "return rp==1-tp and c:IsPreviousControler(tp)" => "opponent",
                        _ => throw No($"its \"destroyed\" condition is unusual ({Short(body)})"),
                    };
                    return (Block("when.destroyed").With("by", by).With("optional", optional), body is not null);
                case "EVENT_BATTLE_DESTROYING" when body == "aux.bdocon":
                    return (Block("when.destroys_by_battle").With("optional", optional), true);
                case "EVENT_BATTLE_DESTROYING" when body == "aux.bdogcon":
                    return (Block("when.destroys_by_battle").With("sent", "gy").With("optional", optional), true);
                case "EVENT_BATTLE_DESTROYED" when body is "return e:GetHandler():IsLocation(LOCATION_GRAVE)"
                                                    or "return e:GetHandler():IsLocation(LOCATION_GRAVE) and e:GetHandler():IsReason(REASON_BATTLE)":
                    return (Block("when.destroyed").With("by", "battle_gy").With("optional", optional), true);
                case "EVENT_BE_MATERIAL" when body is not null && Regex.Match(body, @"^return e:GetHandler\(\):IsLocation\(LOCATION_GRAVE\) and r==REASON_(SYNCHRO|FUSION|LINK)$") is { Success: true } mat:
                    return (Block("when.used_as_material").With("kind", mat.Groups[1].Value.ToLowerInvariant()).With("gy", true).With("optional", optional), true);
                case "EVENT_FLIP" when body is null: return (Block("when.this_card").With("event", "flipped").With("optional", optional), false);
                case "EVENT_DISCARD" when body is null: return (Block("when.this_card").With("event", "discarded").With("optional", optional), false);
                case "EVENT_TO_HAND" when body == "return not e:GetHandler():IsReason(REASON_DRAW)":
                    return (Block("when.this_card").With("event", "added_to_hand").With("optional", optional), true);
                case "EVENT_BATTLE_DAMAGE" when body is "return ep~=tp and Duel.GetAttackTarget()==nil" or "return ep==1-tp and Duel.GetAttackTarget()==nil":
                    return (Block("when.this_card").With("event", "direct_damage").With("optional", optional), true);
                case "EVENT_REMOVE" when body is null: return (Block("when.this_card").With("event", "banished").With("optional", optional), false);
                case "EVENT_BATTLE_DESTROYED" when body is null: return (Block("when.destroyed").With("by", "battle").With("optional", optional), false);
                case "EVENT_BE_MATERIAL" when body is "return r==REASON_SYNCHRO" or "return r==REASON_LINK" or "return r==REASON_FUSION":
                    return (Block("when.used_as_material").With("kind", body["return r==REASON_".Length..].ToLowerInvariant()).With("optional", optional), true);
                case "EVENT_LEAVE_FIELD" when body is null: return (Block("when.this_card").With("event", "leaves_field").With("optional", optional), false);
                case "EVENT_RELEASE" when body is null: return (Block("when.this_card").With("event", "tributed").With("optional", optional), false);
                case "EVENT_BECOME_TARGET" when body is null: return (Block("when.this_card").With("event", "targeted").With("optional", optional), false);
                case "EVENT_BATTLE_DAMAGE" when body is "return ep~=tp" or "return ep==1-tp":
                    return (Block("when.this_card").With("event", "battle_damage").With("optional", optional), true);
                case "EVENT_ATTACK_ANNOUNCE" when body is null: return (Block("when.this_card").With("event", "declares_attack").With("optional", optional), false);
                case "EVENT_BE_BATTLE_TARGET" when body is null: return (Block("when.this_card").With("event", "attacked").With("optional", optional), false);
                case "EVENT_BATTLE_START" when body is null: return (Block("when.this_card").With("event", "battle_start").With("optional", optional), false);
            }
        }
        if (type.SetEquals(["EFFECT_TYPE_FIELD", optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F"]))
        {
            var phase = code switch
            {
                "EVENT_PHASE+PHASE_END" => "end",
                "EVENT_PHASE+PHASE_STANDBY" => "standby",
                "EVENT_PHASE+PHASE_BATTLE" => "battle_end",
                "EVENT_PHASE+PHASE_BATTLE_START" => "battle_start",
                "EVENT_PHASE+PHASE_DRAW" => "draw",
                _ => null,
            };
            if (phase is not null)
            {
                var whose = body switch
                {
                    null => "each",
                    "return Duel.GetTurnPlayer()==tp" or "return tp==Duel.GetTurnPlayer()" => "your",
                    "return Duel.GetTurnPlayer()~=tp" or "return Duel.GetTurnPlayer()==1-tp" or "return tp~=Duel.GetTurnPlayer()" => "opponent",
                    _ => throw No($"its phase condition isn't one the blocks have ({Short(body)})"),
                };
                return (Block("when.phase").With("phase", phase).With("whose", whose).With("zone", Zone(range)).With("optional", optional), body is not null);
            }
            var ev = (code, body) switch
            {
                ("EVENT_SPSUMMON_SUCCESS", "return eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)") => "opp_special",
                ("EVENT_SUMMON_SUCCESS", "return eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)") => alsoSpecial ? "opp_summon" : "opp_normal",
                ("EVENT_SPSUMMON_SUCCESS", "return eg:IsExists(Card.IsSummonPlayer,1,nil,tp)") => "you_special",
                ("EVENT_ATTACK_ANNOUNCE", "return Duel.GetAttacker():IsControler(1-tp)" or "return tp~=Duel.GetTurnPlayer()" or "return Duel.GetTurnPlayer()~=tp"
                    or "return Duel.GetAttacker():GetControler()~=tp") => "opp_attack",
                ("EVENT_ATTACK_ANNOUNCE", "return Duel.GetAttacker():IsControler(1-tp) and Duel.GetAttackTarget()==nil" or "return tp~=Duel.GetTurnPlayer() and Duel.GetAttackTarget()==nil"
                    or "local at=Duel.GetAttacker() return at:IsControler(1-tp) and Duel.GetAttackTarget()==nil") => "opp_direct_attack",
                ("EVENT_DAMAGE", "return ep==tp and bit.band(r,REASON_BATTLE+REASON_EFFECT)~=0") => "you_damage",
                ("EVENT_RECOVER", "return ep==tp") => "you_gain_lp",
                ("EVENT_CHAINING", "return rp==1-tp and re:IsActiveType(TYPE_MONSTER)") => "opp_monster_effect",
                ("EVENT_DRAW", "return ep==1-tp" or "return ep~=tp") => "opp_draw",
                ("EVENT_DAMAGE", "return ep==tp") => "you_damage",
                ("EVENT_BATTLE_DAMAGE", "return ep==tp") => "you_battle_damage",
                ("EVENT_BATTLE_DAMAGE", "return ep==1-tp" or "return ep~=tp") => "opp_battle_damage",
                ("EVENT_CHAINING", "return rp==1-tp") => "opp_activates",
                ("EVENT_REMOVE", null) => "banished_any",
                _ => null,
            };
            if (ev is not null)
                return (Block("when.event").With("event", ev).With("zone", Zone(range)).With("optional", optional), body is not null);
            if (CardsEvent(code, body, alsoSpecial, fns) is { } cards)
                return (cards.With("zone", Zone(range)).With("optional", optional), true);
        }
        throw No($"its trigger isn't one the blocks have ({string.Join("+", type.Select(t => t.Replace("EFFECT_TYPE_", "")).OrderBy(t => t))} {code}"
                 + (body is null ? ")" : $": {Short(body)})"));
    }

    /// <summary>A chain condition without the checks the Negate block writes itself (still able to respond, the chain can be negated).</summary>
    private static string? ChainBody(string body)
    {
        if (!body.StartsWith("return ", StringComparison.Ordinal)) return body;
        var parts = SplitTopLevelAnd(body[7..]).Select(p => p.Trim())
            .Where(p => p is not ("not e:GetHandler():IsStatus(STATUS_BATTLE_DESTROYED)" or "not c:IsStatus(STATUS_BATTLE_DESTROYED)"
                                  or "Duel.IsChainNegatable(ev)" or "Duel.IsChainDisablable(ev)")).ToList();
        return parts.Count == 0 ? null : "return " + string.Join(" and ", parts);
    }

    /// <summary>Who activated what, from a chain condition such as "rp==1-tp and re:IsActiveType(TYPE_MONSTER)": a timing of the chain.</summary>
    private static string? ChainWho(string body)
    {
        if (!body.StartsWith("return ", StringComparison.Ordinal)) return null;
        bool opp = false, monster = false, activate = false, spell = false, trap = false;
        foreach (var part in SplitTopLevelAnd(body[7..]).Select(p => p.Trim()))
        {
            switch (part)
            {
                case "rp==1-tp" or "rp~=tp" or "ep==1-tp" or "ep~=tp": opp = true; break;
                case "re:IsActiveType(TYPE_MONSTER)": monster = true; break;
                case "re:IsHasType(EFFECT_TYPE_ACTIVATE)": activate = true; break;
                case "re:IsActiveType(TYPE_SPELL)": spell = true; break;
                case "re:IsActiveType(TYPE_TRAP)": trap = true; break;
                default: return null;
            }
        }
        return (opp, monster, activate, spell, trap) switch
        {
            (true, false, false, false, false) => "chain_any",
            (true, true, false, false, false) => "chain_monster",
            (true, false, true, false, false) => "chain_spelltrap",
            (true, false, true, true, false) => "chain_spell",
            (true, false, true, false, true) => "chain_trap",
            (false, true, false, false, false) => "chain_monster_any",
            (false, false, true, false, false) => "chain_spelltrap_any",
            (false, false, true, true, false) => "chain_spell_any",
            (false, false, true, false, true) => "chain_trap_any",
            _ => null,
        };
    }

    private static readonly Regex EventExists = new(@"^return (?:not eg:IsContains\(e:GetHandler\(\)\) and )?eg:IsExists\(([\w.]+),1,nil,tp\)( and not eg:IsContains\(e:GetHandler\(\)\))?$");

    /// <summary>
    /// "If a Dragon monster(s) is Special Summoned to your field": a trigger whose condition is eg:IsExists(filter,1,nil,tp),
    /// read into a card filter plus what happened to the cards.
    /// </summary>
    private static BlockInstance? CardsEvent(string code, string? body, bool alsoSpecial, Functions fns)
    {
        if (body is null || code is not ("EVENT_SUMMON_SUCCESS" or "EVENT_SPSUMMON_SUCCESS" or "EVENT_DESTROYED" or "EVENT_BATTLE_DESTROYED" or "EVENT_TO_GRAVE")) return null;
        var m = EventExists.Match(body);
        if (!m.Success) return null;
        var other = body.Contains("eg:IsContains(e:GetHandler())", StringComparison.Ordinal);
        var (_, fbody) = fns.Get(m.Groups[1].Value);
        var text = string.Join(" ", fbody.Select(l => l.Trim()));
        var r = Regex.Match(text, @"^return (.+)$");
        if (!r.Success || text.Contains(" or ", StringComparison.Ordinal)) throw No("it picks cards in a way the blocks can't");

        var f = new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = other };
        string? summonType = null;
        bool battle = false, battleOrEffect = false, wasOnField = false;
        foreach (var raw in r.Groups[1].Value.Split(" and "))
        {
            var term = raw.Trim();
            Match t;
            if ((t = Regex.Match(term, @"^c:(IsControler|IsSummonPlayer|IsPreviousControler)\((tp|1-tp)\)$")).Success)
            {
                f.Owner = t.Groups[2].Value == "tp" ? CardOwner.You : CardOwner.Opponent;
                continue;
            }
            switch (term)
            {
                case "c:IsPreviousLocation(LOCATION_MZONE)": wasOnField = true; continue;
                case "c:IsLocation(LOCATION_MZONE)" or "c:IsType(TYPE_MONSTER)": continue;
                case "c:IsPreviousPosition(POS_FACEUP)" or "c:IsFaceup()": f.FaceUpOnly = true; continue;
                case "c:IsReason(REASON_BATTLE+REASON_EFFECT)" or "c:IsReason(REASON_EFFECT+REASON_BATTLE)": battleOrEffect = true; continue;
                case "c:IsReason(REASON_BATTLE)": battle = true; continue;
            }
            if ((t = Regex.Match(term, @"^c:IsSummonType\(SUMMON_TYPE_(\w+)\)$")).Success) { summonType = t.Groups[1].Value; continue; }
            if ((t = Regex.Match(term, @"^bit\.band\(c:GetPrevious(Race|Attribute)OnField\(\),(\w+)\)~=0$")).Success
                || (t = Regex.Match(term, @"^c:GetPrevious(Race|Attribute)OnField\(\)&(\w+)~=0$")).Success)
            {
                Term($"c:Is{t.Groups[1].Value}({t.Groups[2].Value})", f);
                continue;
            }
            if ((t = Regex.Match(term, @"^c:IsPreviousSetCard\((0x[0-9a-fA-F]+)\)$")).Success) { Term($"c:IsSetCard({t.Groups[1].Value})", f); continue; }
            Term(term, f);
        }
        f.CardType = FilterCardType.Monster;
        f.Zones = Model.Zone.MonsterZone;

        string ev;
        switch (code)
        {
            case "EVENT_SUMMON_SUCCESS":
                ev = summonType switch { null => alsoSpecial ? "summoned" : "normal", "ADVANCE" => "tribute", _ => throw No("its Summon condition is unusual") };
                break;
            case "EVENT_SPSUMMON_SUCCESS":
                if (alsoSpecial) throw No("its Summon condition is unusual");
                ev = summonType switch
                {
                    null => "special",
                    "RITUAL" or "FUSION" or "SYNCHRO" or "XYZ" or "PENDULUM" or "LINK" => summonType.ToLowerInvariant(),
                    _ => throw No("its Summon condition is unusual"),
                };
                break;
            case "EVENT_TO_GRAVE":
                if (wasOnField || battle || battleOrEffect || summonType is not null) throw No("its \"sent to the GY\" condition is unusual");
                ev = "to_gy";
                break;
            default:
                if (summonType is not null) throw No("its \"destroyed\" condition is unusual");
                ev = code == "EVENT_BATTLE_DESTROYED" || battle ? "destroyed_battle" : battleOrEffect ? "destroyed_any" : "destroyed";
                break;
        }
        return Block(CardsEventTrigger.BlockId).With("cards", f).With("event", ev);
    }

    /// <summary>A Special Summon procedure: where from, its conditions (the game's check for a free zone aside) and its costs.</summary>
    private static EffectEvent Procedure(EffectEvent effect, Dictionary<string, string> set, string? range, Functions fns, CardData card)
    {
        if (!card.IsMonster || card.IsExtraDeck) throw No("its own Special Summon procedure is for an Extra Deck monster");
        if (set.ContainsKey("Target") || set.ContainsKey("Value")) throw No("its Summon procedure picks cards in a way the blocks don't");
        var zone = range switch
        {
            "LOCATION_HAND" => "hand",
            "LOCATION_GRAVE" => "graveyard",
            "LOCATION_REMOVED" => "banished",
            _ => throw No($"its Summon procedure works from {range ?? "nowhere"}"),
        };
        effect.When = Block("when.summon_procedure").With("zone", zone);
        if (set.GetValueOrDefault("Condition") is { } cond)
        {
            var body = Body(cond, fns).Replace("if c==nil then return true end", "").Replace("local tp=c:GetControler()", "")
                .Replace("c:GetControler()", "tp").Trim();
            if (!body.StartsWith("return ", StringComparison.Ordinal)) throw No($"its Summon procedure's condition isn't one the blocks have ({Short(body)})");
            foreach (var part in SplitTopLevelAnd(body["return ".Length..]))
            {
                if (part is "Duel.GetLocationCount(tp,LOCATION_MZONE)>0" or "Duel.GetMZoneCount(tp)>0") continue;
                effect.If.AddRange(ConditionBody("return " + part, fns));
            }
        }
        if (set.GetValueOrDefault("Operation") is { } op) effect.Cost.AddRange(Costs(op, fns));
        return effect;
    }

    private static List<BlockInstance> Conditions(string condition, Functions fns)
    {
        if (condition == "aux.exccon") return [Block("if.not_sent_this_turn")];
        var body = Body(condition, fns);
        if (body.StartsWith("return ", StringComparison.Ordinal) && !body.Contains(" or ", StringComparison.Ordinal) && !body.Contains('(' + "Duel.GetCurrentPhase", StringComparison.Ordinal))
        {
            // "A and B": each part on its own IF block, when every part is one the blocks have.
            var parts = SplitTopLevelAnd(body["return ".Length..]);
            if (parts.Count > 1)
                return parts.SelectMany(p => ConditionBody("return " + p, fns)).ToList();
        }
        return ConditionBody(body, fns);
    }

    /// <summary>Splits on " and " outside brackets.</summary>
    private static List<string> SplitTopLevelAnd(string expr)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            if (expr[i] == '(') depth++;
            else if (expr[i] == ')') depth--;
            else if (depth == 0 && string.CompareOrdinal(expr, i, " and ", 0, 5) == 0)
            {
                parts.Add(expr[start..i].Trim());
                start = i + 5;
                i += 4;
            }
        }
        parts.Add(expr[start..].Trim());
        return parts;
    }

    private static List<BlockInstance> ConditionBody(string body, Functions fns)
    {
        Match m;
        if ((m = Regex.Match(body, @"^return (not )?Duel\.IsExistingMatchingCard\(([\w.]+),tp,([\w+]+),([\w+]+),(\d+),(nil|e:GetHandler\(\))\)$")).Success)
        {
            var f = Filter(m.Groups[2].Value, fns);
            Locate(f, m.Groups[3].Value, m.Groups[4].Value);
            f.ExcludeThisCard = m.Groups[6].Value != "nil";
            var n = Int(m.Groups[5].Value);
            if (m.Groups[1].Success) return n == 1 ? [Block("if.no_cards").With("cards", f)] : throw No("its condition counts cards in a way the blocks don't");
            return [Block("if.cards_exist").With("count", n).With("cards", f)];
        }
        if ((m = Regex.Match(body, @"^return Duel\.GetFieldGroupCount\(tp,(LOCATION_MZONE|0),(LOCATION_MZONE|0)\)(==0|>0)$")).Success && (m.Groups[1].Value == "0") != (m.Groups[2].Value == "0"))
        {
            var f = new CardFilter { CardType = FilterCardType.Monster, Zones = Model.Zone.MonsterZone, Owner = m.Groups[1].Value == "0" ? CardOwner.Opponent : CardOwner.You };
            return [m.Groups[3].Value == "==0" ? Block("if.no_cards").With("cards", f) : Block("if.cards_exist").With("count", 1).With("cards", f)];
        }
        if ((m = Regex.Match(body, @"^return Duel\.GetFieldGroupCount\((tp|1-tp),LOCATION_HAND,0\)(==|<=|>=)(\d+)$")).Success)
            return [Block("if.hand_count").With("who", m.Groups[1].Value == "tp" ? "you" : "opponent")
                .With("cmp", m.Groups[2].Value switch { "==" => "eq", ">=" => "ge", _ => "le" }).With("count", Int(m.Groups[3].Value))];
        return ConditionMatch(body);
    }

    private static List<BlockInstance> ConditionMatch(string body) => body switch
    {
        var b when b.Length == 0 => throw No("its condition is empty"),
        "local ph=Duel.GetCurrentPhase() return ph==PHASE_MAIN1 or ph==PHASE_MAIN2" => [Block("if.phase").With("phase", "main")],
        "local ph=Duel.GetCurrentPhase() return ph>=PHASE_BATTLE_START and ph<=PHASE_BATTLE" => [Block("if.phase").With("phase", "battle")],
        "return e:GetHandler():IsPosition(POS_FACEUP_ATTACK)" => [Block("if.position").With("pos", "attack")],
        "return e:GetHandler():IsPosition(POS_FACEUP_DEFENSE)" => [Block("if.position").With("pos", "faceup_defense")],
        "return e:GetHandler():IsSummonType(SUMMON_TYPE_NORMAL)" => [Block("if.summoned_by").With("how", "normal")],
        "return e:GetHandler():IsSummonType(SUMMON_TYPE_ADVANCE)" => [Block("if.summoned_by").With("how", "tribute")],
        "return Duel.IsAbleToEnterBP()" => [Block("if.can_battle")],
        "return Duel.GetCurrentPhase()==PHASE_MAIN1" => [Block("if.phase").With("phase", "main1")],
        "return Duel.GetLP(tp)<Duel.GetLP(1-tp)" or "return Duel.GetLP(1-tp)>Duel.GetLP(tp)" => [Block("if.lp_compare").With("cmp", "less")],
        "return Duel.GetLP(tp)>Duel.GetLP(1-tp)" or "return Duel.GetLP(1-tp)<Duel.GetLP(tp)" => [Block("if.lp_compare").With("cmp", "more")],
        var b when Regex.Match(b, @"^return \((.*)\)$") is { Success: true } paren && Balanced(paren.Groups[1].Value)
            => ConditionMatch("return " + paren.Groups[1].Value),
        "return Duel.GetTurnPlayer()~=tp" or "return Duel.GetTurnPlayer()==1-tp" => [Block("if.turn").With("whose", "opponent")],
        "return Duel.GetTurnPlayer()==tp" => [Block("if.turn").With("whose", "your")],
        "return Duel.IsMainPhase()" or "return Duel.GetCurrentPhase()==PHASE_MAIN1 or Duel.GetCurrentPhase()==PHASE_MAIN2" => [Block("if.phase").With("phase", "main")],
        "return Duel.IsBattlePhase()" or "return Duel.GetCurrentPhase()>=PHASE_BATTLE_START and Duel.GetCurrentPhase()<=PHASE_BATTLE" => [Block("if.phase").With("phase", "battle")],
        "return Duel.GetCurrentPhase()==PHASE_STANDBY" => [Block("if.phase").With("phase", "standby")],
        "return Duel.GetCurrentPhase()==PHASE_END" => [Block("if.phase").With("phase", "end")],
        "return e:GetHandler():IsAttackPos()" => [Block("if.position").With("pos", "attack")],
        "return e:GetHandler():IsDefensePos()" => [Block("if.position").With("pos", "defense")],
        var b when SummonTypes.TryGetValue(b, out var how) => [Block("if.summoned_by").With("how", how)],
        var other => throw No($"its condition isn't one the blocks have ({Short(other)})"),
    };

    /// <summary>True when every bracket in the text closes inside it, so "(a) and (b)" is not taken for one bracketed whole.</summary>
    private static bool Balanced(string s)
    {
        int depth = 0;
        foreach (var ch in s)
        {
            if (ch == '(') depth++;
            else if (ch == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    /// <summary>"This card was Link Summoned" and the like, as condition bodies.</summary>
    private static readonly Dictionary<string, string> SummonTypes = new()
    {
        ["return e:GetHandler():IsSummonType(SUMMON_TYPE_LINK)"] = "link",
        ["return e:GetHandler():IsSummonType(SUMMON_TYPE_SYNCHRO)"] = "synchro",
        ["return e:GetHandler():IsSummonType(SUMMON_TYPE_XYZ)"] = "xyz",
        ["return e:GetHandler():IsSummonType(SUMMON_TYPE_FUSION)"] = "fusion",
        ["return e:GetHandler():IsSummonType(SUMMON_TYPE_RITUAL)"] = "ritual",
        ["return e:GetHandler():IsSummonType(SUMMON_TYPE_PENDULUM)"] = "pendulum",
    };

    /// <summary>
    /// The one-line body of a function, e.g. "return Duel.GetTurnPlayer()~=tp". Lasting effects' conditions get the
    /// player as e:GetHandlerPlayer(); that is written as tp, like activated effects.
    /// </summary>
    private static string Body(string reference, Functions fns)
    {
        var (_, body) = fns.Get(reference);
        var text = string.Join(" ", body.Select(l => l.Trim()).Where(l => l is not ("local tp=e:GetHandlerPlayer()" or "local c=e:GetHandler()" or "local tp=c:GetControler()"))).Trim();
        return text.Replace("e:GetHandlerPlayer()", "tp", StringComparison.Ordinal);
    }

    private static readonly Regex ChkReturn = new(@"^if chk==0 then return .* end$");

    private static List<BlockInstance> Costs(string reference, Functions fns)
    {
        if (reference == "aux.bfgcost") return [Block("cost.this_card").With("how", "banish")];
        var (_, rawBody) = fns.Get(reference);
        var (body, temps) = FoldTempEffects(rawBody);
        var blocks = new List<BlockInstance>();
        bool inCheck = false;
        (string Var, CardFilter Filter, int Min, int Max)? picked = null;
        foreach (var raw in body)
        {
            var line = raw.Trim();
            // The "can it be paid" check, on one line or several.
            if (line.StartsWith("if chk==0 then return", StringComparison.Ordinal)) { inCheck = !line.EndsWith(" end", StringComparison.Ordinal); continue; }
            if (inCheck) { if (line.EndsWith(" end", StringComparison.Ordinal) || line == "end") inCheck = false; continue; }
            if (line.Length == 0 || ChkReturn.IsMatch(line) || line == "local c=e:GetHandler()" || line.StartsWith("Duel.Hint(", StringComparison.Ordinal)) continue;
            Match m;
            if (line.StartsWith("@TEMP ", StringComparison.Ordinal))
            {
                var temp = temps[Int(line[6..])];
                if (temp.Parts is [{ Holder: "player" } only] && Restriction(only.Set, fns) is { } r)
                {
                    blocks.Add(Block("cost.summon_restriction").With("from", r.Extra ? "extra" : "anywhere").With("cards", r.Except));
                    continue;
                }
                throw No("its cost puts an effect on the turn the blocks don't have");
            }
            // Cards picked first, then moved: "local g=Duel.SelectMatchingCard(...)" then "Duel.Remove(g,POS_FACEUP,REASON_COST)".
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectMatchingCard\(tp,([\w.()]+),tp,([\w+]+),0,(\d+),(\d+),(nil|e:GetHandler\(\)|c)(?:,[\w,]+)?\)$")).Success)
            {
                var f = Filter(m.Groups[2].Value, fns);
                Locate(f, m.Groups[3].Value, "0");
                picked = (m.Groups[1].Value, f, Int(m.Groups[4].Value), Int(m.Groups[5].Value));
                continue;
            }
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectReleaseGroup\(tp,([\w.()]+),(\d+),\3,(nil|e:GetHandler\(\)|c)(?:,[\w,]+)?\)$")).Success)
            {
                var f = Filter(m.Groups[2].Value, fns);
                f.Owner = CardOwner.You;
                f.Zones = Model.Zone.MonsterZone;
                picked = (m.Groups[1].Value, f, Int(m.Groups[3].Value), -Int(m.Groups[3].Value));
                continue;
            }
            if (line.StartsWith("Duel.HintSelection(", StringComparison.Ordinal)) continue;
            if (picked is { } p && Regex.Match(line, $@"^(?:e:SetLabel\()?(Duel\.\w+)\({p.Var},(.*)\)\)?$") is { Success: true } mv)
            {
                var (how, args) = (mv.Groups[1].Value, mv.Groups[2].Value);
                if (p.Max < 0)
                {
                    if (how != "Duel.Release" || args != "REASON_COST") throw No($"its cost does something the blocks don't ({Short(line)})");
                    blocks.Add(Block("cost.tribute").With("count", -p.Max).With("cards", p.Filter));
                }
                else
                {
                    var way = (how, args) switch
                    {
                        ("Duel.Remove", "POS_FACEUP,REASON_COST") => "banish",
                        ("Duel.SendtoGrave", "REASON_COST") => "grave",
                        ("Duel.SendtoGrave", "REASON_COST+REASON_DISCARD") => "discard",
                        ("Duel.SendtoDeck", "nil,SEQ_DECKSHUFFLE,REASON_COST" or "nil,2,REASON_COST") => p.Filter.Zones.HasFlag(Model.Zone.ExtraDeck) ? null : "deck",
                        ("Duel.SendtoDeck", "nil,SEQ_DECKBOTTOM,REASON_COST" or "nil,1,REASON_COST") => "deck_bottom",
                        ("Duel.SendtoHand", "nil,REASON_COST") => "hand",
                        ("Duel.Release", "REASON_COST") => "tribute",
                        _ => null,
                    } ?? throw No($"its cost does something the blocks don't ({Short(line)})");
                    if (way == "discard")
                    {
                        if (p.Min != p.Max) throw No("its cost discards a varying number of cards");
                        p.Filter.Zones = Model.Zone.Hand;
                        blocks.Add(Block("cost.discard").With("count", p.Max).With("cards", p.Filter));
                    }
                    else if (way == "tribute")
                    {
                        if (p.Min != p.Max) throw No("its cost Tributes a varying number of monsters");
                        blocks.Add(Block("cost.tribute").With("count", p.Max).With("cards", p.Filter));
                    }
                    else
                    {
                        if (p.Min != p.Max && p.Min != 1) throw No("its cost takes a number of cards the blocks can't say");
                        blocks.Add(Block("cost.move_cards").With("how", way).With("count", p.Max).With("mode", p.Min == p.Max ? "exact" : "up_to").With("cards", p.Filter));
                    }
                }
                picked = null;
                continue;
            }
            if ((m = Regex.Match(line, @"^Duel\.PayLPCost\(tp,(\d+)\)$")).Success) blocks.Add(Block("cost.pay_lp").With("amount", Int(m.Groups[1].Value)));
            else if (line is "Duel.SendtoGrave(c,REASON_COST+REASON_DISCARD)" or "Duel.SendtoGrave(e:GetHandler(),REASON_COST+REASON_DISCARD)") blocks.Add(Block("cost.this_card").With("how", "discard"));
            else if (line is "Duel.SendtoGrave(c,REASON_COST)" or "Duel.SendtoGrave(e:GetHandler(),REASON_COST)") blocks.Add(Block("cost.this_card").With("how", "grave"));
            else if (line is "Duel.Remove(c,POS_FACEUP,REASON_COST)" or "Duel.Remove(e:GetHandler(),POS_FACEUP,REASON_COST)") blocks.Add(Block("cost.this_card").With("how", "banish"));
            else if (line is "Duel.Release(c,REASON_COST)" or "Duel.Release(e:GetHandler(),REASON_COST)") blocks.Add(Block("cost.this_card").With("how", "tribute"));
            else if ((m = Regex.Match(line, @"^e:GetHandler\(\):RemoveOverlayCard\(tp,(\d+),\1,REASON_COST\)$")).Success) blocks.Add(Block("cost.detach").With("count", Int(m.Groups[1].Value)));
            else if ((m = Regex.Match(line, @"^Duel\.DiscardHand\(tp,([\w.]+),(\d+),\2,REASON_COST\+REASON_DISCARD(?:,nil)?\)$")).Success)
            {
                var filter = Filter(m.Groups[1].Value, fns);
                filter.Zones = Model.Zone.Hand;
                blocks.Add(Block("cost.discard").With("count", Int(m.Groups[2].Value)).With("cards", filter));
            }
            else throw No($"its cost does something the blocks don't ({Short(line)})");
        }
        if (picked is not null) throw No("its cost picks cards and does something the blocks don't with them");
        if (blocks.Count == 0) throw No("its cost is empty");
        return blocks;
    }

    private sealed class TargetInfo
    {
        public CardFilter? Filter { get; set; }
        public int Count { get; set; }
        public string? Player { get; set; }
        public int? Param { get; set; }
    }

    private static TargetInfo Targeting(string reference, Functions fns)
    {
        var info = new TargetInfo();
        var (_, body) = fns.Get(reference);
        foreach (var raw in body)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("if chkc then", StringComparison.Ordinal) || line.StartsWith("if chk==0 then", StringComparison.Ordinal)
                || line.StartsWith("and ", StringComparison.Ordinal) || line == "end" || line.EndsWith(" end", StringComparison.Ordinal) && line.StartsWith("and ", StringComparison.Ordinal)
                || line.StartsWith("Duel.Hint(", StringComparison.Ordinal) || line.StartsWith("Duel.SetOperationInfo(", StringComparison.Ordinal)
                || line == "local c=e:GetHandler()")
                continue;
            Match m;
            if ((m = Regex.Match(line, @"^Duel\.SetTargetPlayer\((tp|1-tp)\)$")).Success) info.Player = m.Groups[1].Value;
            else if ((m = Regex.Match(line, @"^Duel\.SetTargetParam\((\d+)\)$")).Success) info.Param = Int(m.Groups[1].Value);
            else if ((m = Regex.Match(line, @"^(?:(?:local\s+)?\w+\s*=\s*)?Duel\.SelectTarget\(tp,([\w.]+),tp,([\w+]+),([\w+]+),(\d+),\4,(nil|e:GetHandler\(\)|c)(?:,e,tp)?\)$")).Success)
            {
                info.Filter = Filter(m.Groups[1].Value, fns);
                Locate(info.Filter, m.Groups[2].Value, m.Groups[3].Value);
                info.Filter.ExcludeThisCard = m.Groups[5].Value != "nil";
                info.Count = Int(m.Groups[4].Value);
            }
            else throw No($"its target step does something the blocks don't ({Short(line)})");
        }
        return info;
    }

    // ---------------------------------------------------------------- temporary effects in operations and costs

    /// <summary>An effect an operation or cost creates and puts on a card or a player: its settings and who gets it.</summary>
    private sealed record TempEffect(List<(Dictionary<string, string> Set, string Holder)> Parts);

    /// <summary>
    /// Folds each "local e1=Effect.CreateEffect(...) ... tc:RegisterEffect(e1)" run of lines into one "@TEMP n" line,
    /// so the operation reads as one statement per thing it does.
    /// </summary>
    private static (List<string> Lines, List<TempEffect> Temps) FoldTempEffects(IEnumerable<string> body)
    {
        var lines = new List<string>();
        var temps = new List<TempEffect>();
        Dictionary<string, Dictionary<string, string>>? vars = null;
        List<(Dictionary<string, string>, string)>? parts = null;
        void Close()
        {
            if (vars is null) return;
            if (parts!.Count == 0) throw No("it makes an effect it never applies");
            temps.Add(new TempEffect(parts));
            lines.Add("@TEMP " + (temps.Count - 1).ToString(CultureInfo.InvariantCulture));
            vars = null;
            parts = null;
        }
        foreach (var raw in body)
        {
            var line = raw.Trim();
            Match m;
            if ((m = Regex.Match(line, @"^local (\w+)=Effect\.CreateEffect\((?:c|e:GetHandler\(\))\)$")).Success)
            {
                if (vars is not null && parts!.Count > 0) Close();
                vars ??= [];
                parts ??= [];
                vars[m.Groups[1].Value] = [];
                continue;
            }
            if (vars is not null)
            {
                if ((m = Regex.Match(line, @"^local (\w+)=(\w+):Clone\(\)$")).Success && vars.TryGetValue(m.Groups[2].Value, out var src)) { vars[m.Groups[1].Value] = new(src); continue; }
                if ((m = Regex.Match(line, @"^(\w+):Set(\w+)\((.*)\)$")).Success && vars.TryGetValue(m.Groups[1].Value, out var set)) { set[m.Groups[2].Value] = m.Groups[3].Value.Trim(); continue; }
                if ((m = Regex.Match(line, @"^(c|tc|e:GetHandler\(\)):RegisterEffect\((\w+)(?:,true)?\)$")).Success && vars.TryGetValue(m.Groups[2].Value, out var reg))
                { parts!.Add((reg, m.Groups[1].Value == "tc" ? "tc" : "c")); continue; }
                if ((m = Regex.Match(line, @"^Duel\.RegisterEffect\((\w+),tp\)$")).Success && vars.TryGetValue(m.Groups[1].Value, out var preg)) { parts!.Add((preg, "player")); continue; }
                Close();
            }
            lines.Add(line);
        }
        Close();
        return (lines, temps);
    }

    private static bool UntilEndOfTurn(Dictionary<string, string> set) => (set.GetValueOrDefault("Reset") ?? "").Contains("PHASE_END", StringComparison.Ordinal);

    /// <summary>"you cannot Special Summon monsters (from the Extra Deck), except ..." from an EFFECT_CANNOT_SPECIAL_SUMMON's target.</summary>
    private static (bool Extra, CardFilter Except)? Restriction(Dictionary<string, string> set, Functions fns)
    {
        if (set.GetValueOrDefault("Code") != "EFFECT_CANNOT_SPECIAL_SUMMON" || set.GetValueOrDefault("TargetRange")?.Replace(" ", "") != "1,0"
            || !UntilEndOfTurn(set) || set.GetValueOrDefault("Target") is not { } target) return null;
        var body = Body(target, fns);
        var m = Regex.Match(body, @"^return (c:IsLocation\(LOCATION_EXTRA\) and )?not (c:\w+\([^()]*\))$");
        if (!m.Success) return null;
        var f = new CardFilter { CardType = FilterCardType.Monster };
        Term(m.Groups[2].Value, f);
        f.Zones = Model.Zone.None;
        return (m.Groups[1].Success, f);
    }

    /// <summary>A temporary effect from an operation, as a DO block.</summary>
    private static BlockInstance TempAction(TempEffect temp, TargetInfo target, bool targetCard, Functions fns)
    {
        var parts = temp.Parts;
        var holder = parts[0].Holder;
        if (parts.Any(p => p.Holder != holder)) throw No("it puts effects on different cards at once");
        var codes = parts.Select(p => p.Set.GetValueOrDefault("Code") ?? "").ToList();
        var first = parts[0].Set;
        var value = first.GetValueOrDefault("Value");
        var duration = UntilEndOfTurn(first) ? "turn" : "always";
        bool Int(string? v, out int n) { n = 0; return v is not null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n); }
        string Stat() => codes.Count == 2 && codes.Contains("EFFECT_UPDATE_ATTACK") && codes.Contains("EFFECT_UPDATE_DEFENSE") ? "both"
            : codes.Count == 1 && codes[0] == "EFFECT_UPDATE_ATTACK" ? "atk" : codes.Count == 1 && codes[0] == "EFFECT_UPDATE_DEFENSE" ? "def" : "";

        if (holder == "player")
        {
            if (parts.Count == 1 && Restriction(first, fns) is { } r)
                return Block("do.summon_restriction").With("from", r.Extra ? "extra" : "anywhere").With("cards", r.Except);
            throw No($"it puts an effect on a player the blocks don't have ({codes[0]})");
        }
        if (holder == "c")
        {
            if (Stat() is { Length: > 0 } stat && Int(value, out var amount) && parts.All(p => p.Set.GetValueOrDefault("Value") == value))
                return Block("do.change_self_stats").With("dir", amount < 0 ? "lose" : "gain").With("amount", Math.Abs(amount)).With("stat", stat).With("duration", duration);
            if (parts.Count == 1 && duration == "turn")
            {
                var rule = (codes[0], value) switch
                {
                    ("EFFECT_DIRECT_ATTACK", _) => "direct",
                    ("EFFECT_EXTRA_ATTACK", "1") => "twice",
                    ("EFFECT_ATTACK_ALL", "1") => "all",
                    ("EFFECT_PIERCE", _) => "pierce",
                    ("EFFECT_INDESTRUCTABLE_BATTLE", "1") => "cannot_be_destroyed",
                    _ => null,
                };
                if (rule is not null) return Block("do.self_battle_rule").With("rule", rule);
            }
            if (parts.Count == 1 && codes[0] is "EFFECT_CHANGE_LEVEL" or "EFFECT_UPDATE_LEVEL" && Int(value, out var lv))
                return Block("do.self_level").With("mode", codes[0] == "EFFECT_CHANGE_LEVEL" ? "becomes" : lv < 0 ? "down" : "up").With("amount", Math.Abs(lv)).With("duration", duration);
            throw No($"it gives this card an effect the blocks don't have ({codes[0]})");
        }
        // The target.
        if (!targetCard || target.Filter is null) throw No("it gives an effect to a card the blocks can't follow");
        BlockInstance OnTarget(string id) => Block(id).With("count", target.Count).With("cards", target.Filter).With("target", true);
        if (Stat() is { Length: > 0 } tstat && Int(value, out var tamount) && parts.All(p => p.Set.GetValueOrDefault("Value") == value))
            return OnTarget("do.change_stats").With("dir", tamount < 0 ? "lose" : "gain").With("amount", Math.Abs(tamount)).With("stat", tstat).With("duration", duration);
        if (parts.Count == 1 && codes[0] == "EFFECT_SET_ATTACK_FINAL")
        {
            if (value == "0") return OnTarget("do.set_atk").With("mode", "zero").With("duration", duration);
            if (value is "math.ceil(tc:GetAttack()/2)" or "tc:GetAttack()/2") return OnTarget("do.set_atk").With("mode", "half").With("duration", duration);
            if (Int(value, out var atk)) return OnTarget("do.set_atk").With("mode", "value").With("amount", atk).With("duration", duration);
        }
        if (codes.Count == 2 && codes.Contains("EFFECT_DISABLE") && codes.Contains("EFFECT_DISABLE_EFFECT"))
            return OnTarget("do.negate_effects").With("duration", duration);
        if (parts.Count == 1 && codes[0] == "EFFECT_CANNOT_ATTACK" && duration == "turn") return OnTarget("do.battle_rule").With("rule", "cannot_attack");
        if (parts.Count == 1 && codes[0] == "EFFECT_CHANGE_LEVEL" && Int(value, out var tlv))
            return OnTarget("do.change_level").With("mode", "becomes").With("amount", tlv).With("duration", duration);
        throw No($"it gives the target an effect the blocks don't have ({codes[0]})");
    }

    /// <summary>The operation's statements, matched to DO blocks in order, with "and if you do" / "then" links.</summary>
    private static readonly Regex GuardTerm = new(@"^(?:(?:c|tc|e:GetHandler\(\)):(?:IsRelateToEffect\(e\)|IsRelateToChain\(\)|IsFaceup\(\)|IsOnField\(\))|tc|aux\.NecroValleyFilter\(\)\(\w+\))$");
    private static readonly Regex GuardNotTerm = new(@"^(?:not (?:c|tc|e:GetHandler\(\)):(?:IsRelateToEffect\(e\)|IsFaceup\(\)|IsOnField\(\))|(?:c|tc|e:GetHandler\(\)):IsFacedown\(\)|not tc|Duel\.GetLocationCount\(tp,LOCATION_(?:MZONE|SZONE)\)(?:<=0|<1|==0)|Duel\.IsPlayerAffectedByEffect\(tp,59822133\))$");

    /// <summary>
    /// "if tc:IsFaceup() and tc:IsRelateToEffect(e) then" or "if not c:IsRelateToEffect(e) or c:IsFacedown() then return end":
    /// the checks every block makes on its own cards before it acts.
    /// </summary>
    private static bool IsGuard(string line)
    {
        Match m;
        if ((m = Regex.Match(line, @"^if (.+) then$")).Success && !line.EndsWith(" then return end", StringComparison.Ordinal))
            return m.Groups[1].Value.Split(" and ").All(t => GuardTerm.IsMatch(t.Trim()));
        if ((m = Regex.Match(line, @"^if (.+) then return end$")).Success)
            return m.Groups[1].Value.Split(" or ").All(t => GuardNotTerm.IsMatch(t.Trim()));
        return false;
    }

    private static List<BlockInstance> Operation(string reference, Functions fns, TargetInfo target, CardData card)
    {
        var (_, rawBody) = fns.Get(reference);
        var (body, temps) = FoldTempEffects(rawBody);
        var blocks = new List<BlockInstance>();
        var groups = new Dictionary<string, (CardFilter Filter, int Count)>();
        var link = ActionLink.And;
        bool targetCard = false, self = false;
        foreach (var raw in body)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "local c=e:GetHandler()" || line.StartsWith("Duel.Hint(", StringComparison.Ordinal)
                || line.StartsWith("Duel.HintSelection(", StringComparison.Ordinal) || line.StartsWith("Duel.ConfirmCards(", StringComparison.Ordinal)
                || line.StartsWith("Duel.ShuffleHand(", StringComparison.Ordinal) || line.StartsWith("Duel.ShuffleDeck(", StringComparison.Ordinal)
                || line == "end" || line == "local p,d=Duel.GetChainInfo(0,CHAININFO_TARGET_PLAYER,CHAININFO_TARGET_PARAM)"
                || Regex.IsMatch(line, @"^if \w+:GetCount\(\)>0 then$") || Regex.IsMatch(line, @"^if #\w+>0 then$")
                || Regex.IsMatch(line, @"^if (?:\w+ and )?\w+:IsRelateToEffect\(e\)(?: and \w+:IsFaceup\(\))?(?: and aux\.NecroValleyFilter\(\)\(\w+\))? then$")
                || Regex.IsMatch(line, @"^if Duel\.GetLocationCount\(tp,LOCATION_MZONE\)<=0 then return end$")
                || Regex.IsMatch(line, @"^if not \w+:IsRelateToEffect\(e\) then return end$")
                // "if c:IsRelateToEffect(e) and tc:IsRelateToEffect(e) and tc:IsFaceup() then": the blocks check the same.
                || Regex.IsMatch(line, @"^if (?:c|e:GetHandler\(\)):IsRelateToEffect\(e\) and tc:IsRelateToEffect\(e\)(?: and tc:IsFaceup\(\))? then$")
                || Regex.IsMatch(line, @"^if tc and tc:IsRelateToEffect\(e\)(?: and tc:IsFaceup\(\))? then$")
                || IsGuard(line))
                continue;
            Match m;
            if (line == "local tc=Duel.GetFirstTarget()" && target.Filter is not null) { targetCard = true; continue; }
            if (line.StartsWith("@TEMP ", StringComparison.Ordinal))
            {
                blocks.Add(TempAction(temps[Int(line[6..])], target, targetCard, fns).Linked(link));
                link = ActionLink.And;
                continue;
            }
            // Part of negating a card's effects, which the block does too.
            if (line == "Duel.NegateRelatedChain(tc,RESET_TURN_SET)") continue;
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectMatchingCard\(tp,([\w.()]+),tp,([\w+]+),([\w+]+),(\d+),\5,(nil|e:GetHandler\(\)|c)(?:,e,tp)?\)$")).Success)
            {
                var f = Filter(m.Groups[2].Value, fns);
                Locate(f, m.Groups[3].Value, m.Groups[4].Value);
                f.ExcludeThisCard = m.Groups[6].Value != "nil";
                groups[m.Groups[1].Value] = (f, Int(m.Groups[5].Value));
                continue;
            }
            // "local tc=g:GetFirst()" after picking 1 card: the same card under another name.
            if ((m = Regex.Match(line, @"^local (\w+)=(\w+):GetFirst\(\)$")).Success && groups.TryGetValue(m.Groups[2].Value, out var picked) && picked.Count == 1)
            {
                groups[m.Groups[1].Value] = picked;
                continue;
            }
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.GetMatchingGroup\(([\w.()]+),tp,([\w+]+),([\w+]+),(nil|e:GetHandler\(\)|c)(?:,e,tp)?\)$")).Success)
            {
                var f = Filter(m.Groups[2].Value, fns);
                Locate(f, m.Groups[3].Value, m.Groups[4].Value);
                f.ExcludeThisCard = m.Groups[5].Value != "nil";
                groups[m.Groups[1].Value] = (f, 0);
                continue;
            }
            if (line is "local p=Duel.GetChainInfo(0,CHAININFO_TARGET_PLAYER)" or "local d=Duel.GetChainInfo(0,CHAININFO_TARGET_PARAM)") continue;
            if (line == "Duel.BreakEffect()") { link = ActionLink.Then; continue; }
            if ((m = Regex.Match(line, @"^if (.+?)(?:~=0|>0) then$")).Success)
            {
                // "if Duel.Draw(...)>0 then": the next action happens only if this one did.
                var inner = Regex.Replace(m.Groups[1].Value, @"^(?:(?:c|tc|e:GetHandler\(\)):IsRelateToEffect\(e\) and )+", "");
                blocks.Add(Action(inner, groups, target, targetCard, card, ref self).Linked(link));
                link = ActionLink.IfYouDo;
                continue;
            }
            blocks.Add(Action(line, groups, target, targetCard, card, ref self).Linked(link));
            link = ActionLink.And;
        }
        return blocks;
    }

    private static BlockInstance Action(string line, Dictionary<string, (CardFilter Filter, int Count)> groups, TargetInfo target, bool targetCard, CardData card, ref bool self)
    {
        Match m;
        string Who(string p) => p switch
        {
            "tp" => "you",
            "1-tp" => "opponent",
            "p" when target.Player is "tp" => "you",
            "p" when target.Player is "1-tp" => "opponent",
            _ => throw No("it is unclear who it affects"),
        };
        int Amount(string d) => d == "d" ? target.Param ?? throw No("its amount is unclear")
            : Regex.IsMatch(d, @"^\d+$") ? Int(d) : throw No($"its amount comes from the duel ({d})");

        if ((m = Regex.Match(line, @"^Duel\.Draw\((\w+|1-tp),(\w+),REASON_EFFECT\)$")).Success)
            return Block("do.draw").With("who", Who(m.Groups[1].Value)).With("count", Amount(m.Groups[2].Value));
        if ((m = Regex.Match(line, @"^Duel\.Damage\((\w+|1-tp),(\w+),REASON_EFFECT\)$")).Success)
            return Block("do.damage").With("who", Who(m.Groups[1].Value) == "you" ? "you" : "opponent").With("amount", Amount(m.Groups[2].Value));
        if ((m = Regex.Match(line, @"^Duel\.Recover\((\w+|1-tp),(\w+),REASON_EFFECT\)$")).Success)
            return Block("do.gain_lp").With("who", Who(m.Groups[1].Value)).With("amount", Amount(m.Groups[2].Value));
        if ((m = Regex.Match(line, @"^Duel\.DiscardDeck\((tp|1-tp),(\d+),REASON_EFFECT\)$")).Success)
            return Block("do.mill").With("whose", m.Groups[1].Value == "tp" ? "you" : "opponent").With("count", Int(m.Groups[2].Value));
        if (line == "Duel.NegateActivation(ev)") return Block("do.negate").With("what", "activation");
        if (line == "Duel.NegateAttack()") return Block("do.negate_attack");
        if ((m = Regex.Match(line, @"^Duel\.DiscardHand\((tp|1-tp),nil,(\d+),\2,REASON_EFFECT\+REASON_DISCARD\)$")).Success)
            return Block("do.discard").With("who", m.Groups[1].Value == "tp" ? "you" : "opponent").With("count", Int(m.Groups[2].Value));
        if (line == "Duel.NegateEffect(ev)") return Block("do.negate").With("what", "effect");

        // Actions on cards: a selected group, the target, or this card.
        var verbs = new (string Pattern, string Block)[]
        {
            (@"^Duel\.Destroy\((\w+),REASON_EFFECT\)$", "do.destroy"),
            (@"^Duel\.Remove\((\w+),POS_FACEUP,REASON_EFFECT\)$", "do.banish"),
            (@"^Duel\.SendtoGrave\((\w+),REASON_EFFECT\)$", "do.send_to_gy"),
            (@"^Duel\.SendtoHand\((\w+),nil,REASON_EFFECT\)$", "hand"),
            (@"^Duel\.SendtoDeck\((\w+),nil,(?:2|SEQ_DECKSHUFFLE),REASON_EFFECT\)$", "do.shuffle_into_deck"),
            (@"^Duel\.SpecialSummon\((\w+),0,tp,tp,false,false,(POS_FACEUP|POS_FACEUP_ATTACK|POS_FACEUP_DEFENSE)\)$", "do.special_summon"),
            (@"^Duel\.GetControl\((\w+),tp,PHASE_END,1\)$", "control_turn"),
            (@"^Duel\.GetControl\((\w+),tp\)$", "control"),
            (@"^Duel\.ChangePosition\((\w+),POS_FACEUP_DEFENSE,POS_FACEUP_DEFENSE,POS_FACEUP_ATTACK,POS_FACEUP_ATTACK\)$", "pos_toggle"),
            (@"^Duel\.ChangePosition\((\w+),POS_FACEDOWN_DEFENSE\)$", "pos_facedown"),
        };
        foreach (var (pattern, blockId) in verbs)
        {
            m = Regex.Match(line, pattern);
            if (!m.Success) continue;
            var what = m.Groups[1].Value;
            var position = blockId == "do.special_summon" && m.Groups.Count > 2 && m.Groups[2].Success ? m.Groups[2].Value switch { "POS_FACEUP_ATTACK" => "attack", "POS_FACEUP_DEFENSE" => "defense", _ => "faceup" } : null;
            if (what is "c" && blockId == "do.special_summon")
            {
                self = true;
                return Block("do.special_summon_self").With("position", position!);
            }
            CardFilter filter;
            int count;
            bool targeted;
            if (groups.TryGetValue(what, out var g)) (filter, count, targeted) = (g.Filter, g.Count, false);
            else if (what == "tc" && targetCard && target.Filter is not null) (filter, count, targeted) = (target.Filter, target.Count, true);
            else throw No($"it acts on {what}, which the blocks can't follow");

            var id = blockId switch
            {
                "hand" => (filter.Zones & (Model.Zone.Deck | Model.Zone.Graveyard | Model.Zone.Banished)) != 0 && (filter.Zones & Model.Zone.Field) == 0 ? "do.add_to_hand" : "do.return_to_hand",
                "control" or "control_turn" => "do.take_control",
                "pos_toggle" or "pos_facedown" => "do.change_position",
                _ => blockId,
            };
            var block = Block(id).With("count", Math.Max(1, count)).With("cards", filter);
            // A group of every matching card: "destroy all monsters your opponent controls".
            if (count == 0)
            {
                if (BlockRegistry.Find(id)!.Param("mode")?.Choices.Any(c => c.Value == "all") != true) throw No("it acts on every matching card, which that block can't");
                block = block.With("mode", "all");
            }
            if (blockId is "control" or "control_turn") block = block.With("duration", blockId == "control_turn" ? "turn" : "always");
            if (blockId is "pos_toggle" or "pos_facedown") block = block.With("to", blockId == "pos_toggle" ? "toggle" : "facedown");
            if (BlockRegistry.Find(id)!.Param("target") is not null) block = block.With("target", targeted);
            else if (targeted) throw No("it targets in a way that block can't");
            if (position is not null && BlockRegistry.Find(id)!.Param("position") is not null) block = block.With("position", position);
            return block;
        }
        throw No($"it does something the blocks don't ({Short(line)})");
    }

    // ---------------------------------------------------------------- lasting effects

    private static EffectEvent Passive(EffectEvent effect, HashSet<string> type, string code, Dictionary<string, string> set, string? range, HashSet<string> props, Functions fns, CardData card, Merge merge)
    {
        if (merge.CannotTribute)
        {
            effect.Limit = OncePerTurn.None;
            effect.When = Block("when.while_face_up").With("zone", Zone(range ?? "LOCATION_MZONE"));
            if (set.ContainsKey("Target") || set.GetValueOrDefault("Value") != "1") throw No("its lasting effect picks its cards in a way the blocks don't");
            effect.Do.Add(Block("passive.self_rule").With("rule", "cannot_tribute"));
            if (set.GetValueOrDefault("Condition") is { } cc) effect.If.AddRange(Conditions(cc, fns));
            return effect;
        }
        var result = LastingEffect(effect, type, code, set, range, props, fns, card);
        if (merge.BothStats)
        {
            var b = result.Do.Single();
            if (BlockRegistry.Find(b.BlockId)?.Param("stat") is null) throw No("it registers more than one effect");
            result.Do[0] = b.With("stat", "both");
        }
        if (merge.BothIndestructible)
        {
            var b = result.Do.Single();
            if (b.BlockId == "passive.indestructible") result.Do[0] = b.With("by", "both");
            else if (b.BlockId == "passive.equipped") throw No("it registers more than one effect");
            else throw No("it registers more than one effect");
        }
        // "While ...": the same IF blocks, with the player being whoever controls this card.
        if (set.GetValueOrDefault("Condition") is { } lastingCondition) result.If.AddRange(Conditions(lastingCondition, fns));
        return result;
    }

    private static EffectEvent LastingEffect(EffectEvent effect, HashSet<string> type, string code, Dictionary<string, string> set, string? range, HashSet<string> props, Functions fns, CardData card)
    {
        effect.Limit = OncePerTurn.None;
        effect.When = Block("when.while_face_up").With("zone", Zone(range ?? "LOCATION_MZONE"));
        var value = set.GetValueOrDefault("Value");
        if (set.ContainsKey("Target") && !type.Contains("EFFECT_TYPE_FIELD"))
            throw No("its lasting effect picks its cards in a way the blocks don't");

        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]))
        {
            switch (code)
            {
                case "EFFECT_INDESTRUCTABLE_BATTLE" when value == "1": effect.Do.Add(Block("passive.indestructible").With("by", "battle")); return effect;
                case "EFFECT_INDESTRUCTABLE_EFFECT" when value == "1": effect.Do.Add(Block("passive.indestructible").With("by", "effect")); return effect;
                case "EFFECT_PIERCE": effect.Do.Add(Block("passive.battle_ability").With("ability", "pierce")); return effect;
                case "EFFECT_DIRECT_ATTACK": effect.Do.Add(Block("passive.battle_ability").With("ability", "direct")); return effect;
                case "EFFECT_EXTRA_ATTACK" when value == "1": effect.Do.Add(Block("passive.battle_ability").With("ability", "extra")); return effect;
                case "EFFECT_CANNOT_BE_EFFECT_TARGET" when value == "aux.tgoval": effect.Do.Add(Block("passive.untargetable")); return effect;
                case "EFFECT_CANNOT_ATTACK": effect.Do.Add(Block("passive.self_rule").With("rule", "cannot_attack")); return effect;
                case "EFFECT_CANNOT_DIRECT_ATTACK": effect.Do.Add(Block("passive.self_rule").With("rule", "cannot_direct")); return effect;
                case "EFFECT_CANNOT_BE_BATTLE_TARGET" when value == "aux.imval1": effect.Do.Add(Block("passive.self_rule").With("rule", "not_attack_target")); return effect;
                case "EFFECT_AVOID_BATTLE_DAMAGE" when value == "1": effect.Do.Add(Block("passive.self_rule").With("rule", "no_battle_damage")); return effect;
                case "EFFECT_CANNOT_CHANGE_POSITION": effect.Do.Add(Block("passive.self_rule").With("rule", "cannot_change_position")); return effect;
                case "EFFECT_ATTACK_ALL" when value == "1": effect.Do.Add(Block("passive.self_rule").With("rule", "attack_all")); return effect;
                case "EFFECT_MUST_ATTACK": effect.Do.Add(Block("passive.self_rule").With("rule", "must_attack")); return effect;
                case "EFFECT_IMMUNE_EFFECT" when value is not null && ImmuneValue(value, fns) is { } whose:
                    effect.Do.Add(Block("passive.immune").With("whose", whose)); return effect;
                case "EFFECT_UPDATE_ATTACK" or "EFFECT_UPDATE_DEFENSE" when value is not null && !Regex.IsMatch(value, @"^-?\d+$") && range is not null
                                                                          && PerCard(value, fns) is { } per:
                    effect.Do.Add(Block("passive.self_stats_per").With("dir", per.Amount < 0 ? "lose" : "gain").With("amount", Math.Abs(per.Amount))
                        .With("stat", code == "EFFECT_UPDATE_ATTACK" ? "atk" : "def").With("cards", per.Filter));
                    return effect;
                case "EFFECT_UPDATE_ATTACK" or "EFFECT_UPDATE_DEFENSE" when value is not null && Regex.IsMatch(value, @"^-?\d+$") && range is not null:
                    var n = Int(value);
                    effect.Do.Add(Block("passive.self_stats").With("dir", n < 0 ? "lose" : "gain").With("amount", Math.Abs(n)).With("stat", code == "EFFECT_UPDATE_ATTACK" ? "atk" : "def"));
                    return effect;
            }
        }
        if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && code is "EFFECT_UPDATE_ATTACK" or "EFFECT_UPDATE_DEFENSE" && value is not null && Regex.IsMatch(value, @"^-?\d+$")
            && set.GetValueOrDefault("TargetRange") is { } tr)
        {
            var filter = new CardFilter { CardType = FilterCardType.Monster, Zones = Model.Zone.MonsterZone, FaceUpOnly = true };
            filter.Owner = tr.Replace(" ", "") switch
            {
                "LOCATION_MZONE,0" => CardOwner.You,
                "0,LOCATION_MZONE" => CardOwner.Opponent,
                "LOCATION_MZONE,LOCATION_MZONE" => CardOwner.Either,
                _ => throw No("it affects zones the blocks don't"),
            };
            if (set.GetValueOrDefault("Target") is { } t)
            {
                var m = Regex.Match(t, @"^aux\.TargetBoolFunction\((Card\.\w+),(\w+)\)$");
                if (!m.Success) throw No("it picks its monsters in a way the blocks don't");
                Term($"c:{m.Groups[1].Value[5..]}({m.Groups[2].Value})", filter);
            }
            var n = Int(value);
            effect.Do.Add(Block("passive.boost_cards").With("cards", filter).With("dir", n < 0 ? "lose" : "gain").With("amount", Math.Abs(n))
                .With("stat", code == "EFFECT_UPDATE_ATTACK" ? "atk" : "def"));
            return effect;
        }
        if (type.SetEquals(["EFFECT_TYPE_EQUIP"]) && card.Kind == CardKind.Spell && card.SpellKind == SpellKind.Equip)
        {
            switch (code)
            {
                case "EFFECT_UPDATE_ATTACK" when value is not null && Regex.IsMatch(value, @"^-?\d+$"):
                    var n = Int(value);
                    effect.Do.Add(Block("passive.equipped").With("rule", n < 0 ? "lose_atk" : "gain_atk").With("amount", Math.Abs(n)));
                    return effect;
                case "EFFECT_INDESTRUCTABLE_BATTLE" when value == "1": effect.Do.Add(Block("passive.equipped").With("rule", "indestructible_battle")); return effect;
                case "EFFECT_INDESTRUCTABLE_EFFECT" when value == "1": effect.Do.Add(Block("passive.equipped").With("rule", "indestructible_effect")); return effect;
                case "EFFECT_PIERCE": effect.Do.Add(Block("passive.equipped").With("rule", "pierce")); return effect;
                case "EFFECT_CANNOT_ATTACK": effect.Do.Add(Block("passive.equipped").With("rule", "cannot_attack")); return effect;
            }
        }
        throw No($"its lasting effect isn't one the blocks have ({string.Join("+", type.Select(t => t.Replace("EFFECT_TYPE_", "")))} {code})");
    }

    /// <summary>"300 for each Dragon monster in your GY": a value function that counts cards, times an amount.</summary>
    private static (CardFilter Filter, int Amount)? PerCard(string value, Functions fns)
    {
        string body;
        try { body = Body(value, fns).Replace("c:GetControler()", "tp", StringComparison.Ordinal); } catch (Untranslatable) { return null; }
        var m = Regex.Match(body, @"^return Duel\.GetMatchingGroupCount\(([\w.]+),tp,([\w+]+),([\w+]+),(nil|c|e:GetHandler\(\))\)\*(-?\d+)$");
        if (m.Success)
        {
            var f = Filter(m.Groups[1].Value, fns);
            Locate(f, m.Groups[2].Value, m.Groups[3].Value);
            f.ExcludeThisCard = m.Groups[4].Value != "nil";
            return (f, Int(m.Groups[5].Value));
        }
        m = Regex.Match(body, @"^return Duel\.GetFieldGroupCount\(tp,([\w+]+),([\w+]+)\)\*(-?\d+)$");
        if (m.Success)
        {
            var f = new CardFilter();
            Locate(f, m.Groups[1].Value, m.Groups[2].Value);
            return (f, Int(m.Groups[3].Value));
        }
        return null;
    }

    /// <summary>The "unaffected by" choice for common EFFECT_IMMUNE_EFFECT value functions.</summary>
    private static string? ImmuneValue(string value, Functions fns)
    {
        string body;
        try { body = Body(value, fns); } catch (Untranslatable) { return null; }
        return body switch
        {
            "return te:GetOwnerPlayer()~=e:GetHandlerPlayer()" or "return te:GetOwnerPlayer()~=e:GetOwnerPlayer()" => "opponent",
            "return te:GetOwner()~=e:GetOwner()" => "other",
            "return te:IsActiveType(TYPE_SPELL+TYPE_TRAP)" => "spelltrap",
            _ => null,
        };
    }

    // ---------------------------------------------------------------- filters

    private static readonly Dictionary<string, CardRace> Races = Enum.GetValues<CardRace>().Where(r => r != CardRace.None)
        .ToDictionary(r => EnumNames.LuaConstant(r), r => r);
    private static readonly Dictionary<string, CardAttribute> Attributes = Enum.GetValues<CardAttribute>().Where(a => a != CardAttribute.None)
        .ToDictionary(a => EnumNames.LuaConstant(a), a => a);

    /// <summary>c:IsType(TYPE_EFFECT) and the like, as the filter's monster kind.</summary>
    private static readonly Dictionary<string, MonsterKind> MonsterKinds = new()
    {
        ["TYPE_EFFECT"] = MonsterKind.Effect, ["not TYPE_EFFECT"] = MonsterKind.NonEffect, ["TYPE_NORMAL"] = MonsterKind.Normal,
        ["TYPE_TUNER"] = MonsterKind.Tuner, ["not TYPE_TUNER"] = MonsterKind.NonTuner, ["TYPE_FUSION"] = MonsterKind.Fusion,
        ["TYPE_SYNCHRO"] = MonsterKind.Synchro, ["TYPE_XYZ"] = MonsterKind.Xyz, ["TYPE_LINK"] = MonsterKind.Link,
        ["TYPE_RITUAL"] = MonsterKind.Ritual, ["TYPE_PENDULUM"] = MonsterKind.Pendulum, ["TYPE_FLIP"] = MonsterKind.Flip,
        ["TYPE_TOON"] = MonsterKind.Toon, ["TYPE_SPIRIT"] = MonsterKind.Spirit, ["TYPE_UNION"] = MonsterKind.Union,
        ["TYPE_DUAL"] = MonsterKind.Gemini, ["TYPE_TOKEN"] = MonsterKind.Token, ["not TYPE_TOKEN"] = MonsterKind.NonToken,
    };

    /// <summary>Filters that asked for "Spell or Trap", which the card picker can only say as "in the Spell &amp; Trap Zone".</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CardFilter, object> SpellOrTrap = new();

    private static void Locate(CardFilter f, string mine, string theirs)
    {
        Model.Zone Zones(string expr) => expr == "0" ? Model.Zone.None : expr.Split('+').Aggregate(Model.Zone.None, (z, s) => z | s.Trim() switch
        {
            "LOCATION_DECK" => Model.Zone.Deck,
            "LOCATION_HAND" => Model.Zone.Hand,
            "LOCATION_MZONE" => Model.Zone.MonsterZone,
            "LOCATION_SZONE" => Model.Zone.SpellTrapZone,
            "LOCATION_ONFIELD" => Model.Zone.Field,
            "LOCATION_GRAVE" => Model.Zone.Graveyard,
            "LOCATION_REMOVED" => Model.Zone.Banished,
            "LOCATION_EXTRA" => Model.Zone.ExtraDeck,
            _ => throw No($"it looks in {s}"),
        });
        var (a, b) = (Zones(mine), Zones(theirs));
        (f.Owner, f.Zones) = (a, b) switch
        {
            (not Model.Zone.None, Model.Zone.None) => (CardOwner.You, a),
            (Model.Zone.None, not Model.Zone.None) => (CardOwner.Opponent, b),
            _ when a == b => (CardOwner.Either, a),
            _ => throw No("it looks in different zones on each side"),
        };
        if (SpellOrTrap.TryGetValue(f, out _))
        {
            // On the field, Spells and Traps are exactly the cards in the Spell & Trap Zone.
            if ((f.Zones & ~Model.Zone.Field) != 0) throw No("it picks Spells and Traps outside the field");
            f.Zones = Model.Zone.SpellTrapZone;
        }
    }

    private static CardFilter Filter(string reference, Functions fns)
    {
        var f = new CardFilter();
        reference = reference.Trim();
        var unwrap = Regex.Match(reference, @"^aux\.NecroValleyFilter\(([\w.]+)\)$");
        if (unwrap.Success) reference = unwrap.Groups[1].Value;
        if (reference is "nil" or "aux.TRUE" or "Card.IsAbleToHand" or "Card.IsAbleToGrave" or "Card.IsAbleToRemove" or "Card.IsAbleToDeck"
            or "Card.IsDestructable" or "Card.IsDiscardable" or "Card.IsAbleToHandAsCost" or "Card.IsAbleToGraveAsCost")
            return f;
        if (reference == "Card.IsFaceup") { f.FaceUpOnly = true; return f; }
        var (_, body) = fns.Get(reference);
        var text = string.Join(" ", body.Select(l => l.Trim()));
        var m = Regex.Match(text, @"^return (.+)$");
        if (!m.Success || text.Contains(" or ", StringComparison.Ordinal)) throw No($"it picks cards in a way the blocks can't ({Short(Regex.Replace(text, @"c\d+\.|s\.", "s."))})");
        foreach (var term in m.Groups[1].Value.Split(" and ")) Term(term.Trim(), f);
        return f;
    }

    private static void Term(string term, CardFilter f)
    {
        Match m;
        // Materials are checked with their material forms (IsLinkRace, IsFusionSetCard...); the filter says what they must be.
        term = Regex.Replace(term, @"^(not )?c:Is(?:Fusion|Link|Synchro|Xyz)(Type|Attribute|Race|SetCard|Code)\(", "$1c:Is$2(");
        if (Regex.IsMatch(term, @"^c:(IsAbleTo\w+|IsDiscardable|IsDestructable|IsSSetable|IsCanBeSpecialSummoned)\(.*\)$")) return;
        if (term == "c:IsFaceup()") { f.FaceUpOnly = true; return; }
        if (term == "c:IsFaceupEx()") { f.FaceUpOnly = true; return; }
        if ((m = Regex.Match(term, @"^not c:IsCode\((\d+|id)\)$")).Success)
        {
            if (m.Groups[1].Value == "id" || long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == _sourceId) f.ExceptOwnName = true;
            else f.ExceptCode = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return;
        }
        if ((m = Regex.Match(term, @"^(not )?c:IsType\((TYPE_\w+)\)$")).Success && MonsterKinds.TryGetValue((m.Groups[1].Success ? "not " : "") + m.Groups[2].Value, out var kind))
        {
            f.MonsterKind = kind;
            f.CardType = FilterCardType.Monster;
            return;
        }
        if ((m = Regex.Match(term, @"^c:IsRace\(([\w+]+)\)$")).Success && m.Groups[1].Value.Contains('+') && m.Groups[1].Value.Split('+').All(Races.ContainsKey))
        {
            f.Race = m.Groups[1].Value.Split('+').Aggregate(CardRace.None, (r, x) => r | Races[x]);
            f.CardType = FilterCardType.Monster;
            return;
        }
        if ((m = Regex.Match(term, @"^c:IsAttribute\(([\w+]+)\)$")).Success && m.Groups[1].Value.Contains('+') && m.Groups[1].Value.Split('+').All(Attributes.ContainsKey))
        {
            f.Attribute = m.Groups[1].Value.Split('+').Aggregate(CardAttribute.None, (a, x) => a | Attributes[x]);
            f.CardType = FilterCardType.Monster;
            return;
        }
        if ((m = Regex.Match(term, @"^c:IsType\((TYPE_MONSTER|TYPE_SPELL|TYPE_TRAP|TYPE_SPELL\+TYPE_TRAP)\)$")).Success)
        {
            f.CardType = m.Groups[1].Value switch { "TYPE_MONSTER" => FilterCardType.Monster, "TYPE_SPELL" => FilterCardType.Spell, "TYPE_TRAP" => FilterCardType.Trap, _ => FilterCardType.Any };
            if (f.CardType == FilterCardType.Any) SpellOrTrap.AddOrUpdate(f, true);
            return;
        }
        if ((m = Regex.Match(term, @"^c:IsLevelBelow\((\d+)\)$")).Success) { f.LevelMax = Int(m.Groups[1].Value); f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsLevelAbove\((\d+)\)$")).Success) { f.LevelMin = Int(m.Groups[1].Value); f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsLevel\((\d+)\)$")).Success) { f.LevelMin = f.LevelMax = Int(m.Groups[1].Value); f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsAttackBelow\((\d+)\)$")).Success) { f.AtkMax = Int(m.Groups[1].Value); f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsAttackAbove\((\d+)\)$")).Success) { f.AtkMin = Int(m.Groups[1].Value); f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsRace\((\w+)\)$")).Success && Races.TryGetValue(m.Groups[1].Value, out var race)) { f.Race = race; f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsAttribute\((\w+)\)$")).Success && Attributes.TryGetValue(m.Groups[1].Value, out var attr)) { f.Attribute = attr; f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsSetCard\(0x([0-9a-fA-F]+)\)$")).Success) { f.Archetype = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture); return; }
        if ((m = Regex.Match(term, @"^c:IsCode\((\d+)\)$")).Success) { f.CardCode = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); return; }
        throw No($"it picks cards by {Short(term)}");
    }

    // ---------------------------------------------------------------- helpers

    private static Dictionary<string, (string[] Params, List<string> Body)> ParseFunctions(ScriptPiece piece)
    {
        var result = new Dictionary<string, (string[], List<string>)>();
        foreach (var fn in piece.Functions)
        {
            var lines = fn.Split('\n');
            var m = Regex.Match(lines[0], $@"^function\s+{Regex.Escape(piece.Prefix)}\.(\w+)\s*\(([^)]*)\)");
            if (!m.Success) continue;
            var body = lines.Skip(1).Take(Math.Max(0, lines.Length - 2)).ToList();
            result[m.Groups[1].Value] = (m.Groups[2].Value.Split(',').Select(p => p.Trim()).ToArray(), body);
        }
        return result;
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    private static long Int64(string s) => long.Parse(s, CultureInfo.InvariantCulture);

    private static string Short(string s) => s.Length > 90 ? s[..90] + "…" : s;
}
