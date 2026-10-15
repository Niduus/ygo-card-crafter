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
    /// <summary>Why it stays as script.</summary>
    public string? Reason { get; init; }
    public bool Translated => Effect is not null || Procedure is not null || Ignored;
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

    private static Untranslatable No(string reason) => new(reason);

    public static TranslationResult Translate(string lua, long id, CardData card, IReadOnlyList<string>? strings = null)
    {
        var result = new TranslationResult();
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
        var effects = result.Pieces.Where(p => p.Effect is not null).Select(p => p.Effect!).ToList();
        var procedure = result.Pieces.LastOrDefault(p => p.Procedure is not null)?.Procedure ?? project.Procedure;
        var compiled = ScriptCompiler.Compile(new CardProject { Card = project.Card, Procedure = procedure, Effects = effects, ScriptStrings = project.ScriptStrings }, pack);
        if (compiled.HasErrors) return result;
        project.Effects = effects;
        project.Procedure = procedure;
        project.UseCustomLua = false;
        applied = true;
        return result;
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
        if (lines.Count == 1 && TranslateStatement(lines[0], card) is { } single) return single with { Piece = piece };

        var fns = new Functions(ParseFunctions(piece), piece.Prefix, piece.SourceId);
        var create = lines.Select(l => Regex.Match(l, @"^local\s+(\w+)\s*=\s*Effect\.CreateEffect\(c\)$")).FirstOrDefault(m => m.Success)
                     ?? throw No("it isn't a single effect created on this card");
        var v = create.Groups[1].Value;
        var set = new Dictionary<string, string>();
        var clones = new List<string>();
        foreach (var line in lines)
        {
            if (line == create.Value || line == $"c:RegisterEffect({v})") continue;
            var m = Regex.Match(line, $@"^{v}:Set(\w+)\((.*)\)$");
            if (m.Success) { set[m.Groups[1].Value] = m.Groups[2].Value.Trim(); continue; }
            clones.Add(line);
        }
        if (set.ContainsKey("Reset") || set.ContainsKey("Label") || set.ContainsKey("LabelObject"))
            throw No("it keeps extra state the blocks don't model");

        // The only clone the blocks express: a "Normal or Special Summoned" trigger copied for Special Summons.
        bool alsoSpecial = false;
        if (clones.Count > 0)
        {
            var c2 = Regex.Match(clones[0], $@"^local\s+(\w+)\s*=\s*{v}:Clone\(\)$");
            if (clones.Count == 3 && c2.Success && clones[1] == $"{c2.Groups[1].Value}:SetCode(EVENT_SPSUMMON_SUCCESS)"
                && clones[2] == $"c:RegisterEffect({c2.Groups[1].Value})" && set.GetValueOrDefault("Code") == "EVENT_SUMMON_SUCCESS")
                alsoSpecial = true;
            else throw No("it registers more than one effect");
        }

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

        // Lasting effects: no activation at all.
        if (!type.Overlaps(["EFFECT_TYPE_ACTIVATE", "EFFECT_TYPE_IGNITION", "EFFECT_TYPE_QUICK_O", "EFFECT_TYPE_QUICK_F", "EFFECT_TYPE_TRIGGER_O", "EFFECT_TYPE_TRIGGER_F", "EFFECT_TYPE_FLIP"]))
            return new TranslatedPiece { Piece = piece, Effect = Passive(effect, type, code, set, range, props, fns, card) };

        var condition = set.GetValueOrDefault("Condition");
        var (when, usedCondition) = Trigger(type, code, range, condition, alsoSpecial, fns);
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
    private static TranslatedPiece? TranslateStatement(string line, CardData card)
    {
        // Lines that only describe the card, or that Card Crafter writes itself from the Card tab.
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
        var m = Regex.Match(line, @"^aux\.AddSynchroProcedure\(c,aux\.Tuner\(nil\),aux\.NonTuner\(nil\),(\d+)(?:,(\d+))?\)$");
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
        return null;

        static TranslatedPiece Proc(SummonProcedureKind kind, int min, int max) =>
            new() { Piece = null!, Procedure = new SummonProcedure { Kind = kind, MinMaterials = min, MaxMaterials = max } };
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
        group = key switch { "id" => 1, "id+1" or "id+o" or "id+100" => 2, "id+2" or "id+o*2" or "id+200" => 3, _ => throw No("its once-per-turn code is unusual") };
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
        _ => throw No($"it works from {range}"),
    };

    private static BlockInstance Block(string id) => BlockRegistry.Find(id)!.CreateInstance();

    /// <summary>The WHEN block, and whether the trigger used up the effect's condition (e.g. "sent from the field").</summary>
    private static (BlockInstance When, bool UsedCondition) Trigger(HashSet<string> type, string code, string? range, string? condition, bool alsoSpecial, Functions fns)
    {
        string? body = condition is null || condition.StartsWith("aux.", StringComparison.Ordinal) ? condition : Body(condition, fns);
        (string Timing, bool Used) Timing(string c) => c switch
        {
            "EVENT_FREE_CHAIN" => ("any", false),
            "EVENT_ATTACK_ANNOUNCE" when body is "return Duel.GetAttacker():IsControler(1-tp)" or "return tp~=Duel.GetTurnPlayer()" => ("attack", true),
            "EVENT_SUMMON_SUCCESS" => ("summon", false),
            "EVENT_CHAINING" => ("chain_any", false),
            _ => throw No($"it starts on {c}"),
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

        bool optional = type.Contains("EFFECT_TYPE_TRIGGER_O");
        if (type.SetEquals(["EFFECT_TYPE_SINGLE", optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F"]))
        {
            switch (code)
            {
                case "EVENT_SUMMON_SUCCESS": return (Block("when.summoned").With("how", alsoSpecial ? "normal_or_special" : "normal").With("optional", optional), false);
                case "EVENT_SPSUMMON_SUCCESS": return (Block("when.summoned").With("how", "special").With("optional", optional), false);
                case "EVENT_FLIP_SUMMON_SUCCESS": return (Block("when.summoned").With("how", "flip").With("optional", optional), false);
                case "EVENT_TO_GRAVE":
                    var from = body switch
                    {
                        null => "any",
                        "return e:GetHandler():IsPreviousLocation(LOCATION_ONFIELD)" => "field",
                        "return e:GetHandler():IsPreviousLocation(LOCATION_HAND)" => "hand",
                        _ => throw No("its \"sent to the GY\" condition is unusual"),
                    };
                    return (Block("when.sent_to_gy").With("from", from).With("optional", optional), body is not null);
                case "EVENT_DESTROYED":
                    var by = body switch
                    {
                        null => "any",
                        "return e:GetHandler():IsReason(REASON_BATTLE)" => "battle",
                        "return e:GetHandler():IsReason(REASON_EFFECT)" => "effect",
                        _ => throw No("its \"destroyed\" condition is unusual"),
                    };
                    return (Block("when.destroyed").With("by", by).With("optional", optional), body is not null);
                case "EVENT_BATTLE_DESTROYING" when body == "aux.bdocon":
                    return (Block("when.destroys_by_battle").With("optional", optional), true);
            }
        }
        throw No("its trigger isn't one the blocks have");
    }

    private static List<BlockInstance> Conditions(string condition, Functions fns) => Body(condition, fns) switch
    {
        "return Duel.GetTurnPlayer()~=tp" or "return Duel.GetTurnPlayer()==1-tp" => [Block("if.turn").With("whose", "opponent")],
        "return Duel.GetTurnPlayer()==tp" => [Block("if.turn").With("whose", "your")],
        _ => throw No("its condition isn't one the blocks have"),
    };

    /// <summary>The one-line body of a function, e.g. "return Duel.GetTurnPlayer()~=tp".</summary>
    private static string Body(string reference, Functions fns)
    {
        var (_, body) = fns.Get(reference);
        return string.Join(" ", body.Select(l => l.Trim())).Trim();
    }

    private static readonly Regex ChkReturn = new(@"^if chk==0 then return .* end$");

    private static List<BlockInstance> Costs(string reference, Functions fns)
    {
        if (reference == "aux.bfgcost") return [Block("cost.this_card").With("how", "banish")];
        var (_, body) = fns.Get(reference);
        var blocks = new List<BlockInstance>();
        bool inCheck = false;
        foreach (var raw in body)
        {
            var line = raw.Trim();
            // The "can it be paid" check, on one line or several.
            if (line.StartsWith("if chk==0 then return", StringComparison.Ordinal)) { inCheck = !line.EndsWith(" end", StringComparison.Ordinal); continue; }
            if (inCheck) { if (line.EndsWith(" end", StringComparison.Ordinal) || line == "end") inCheck = false; continue; }
            if (line.Length == 0 || ChkReturn.IsMatch(line) || line == "local c=e:GetHandler()" || line.StartsWith("Duel.Hint(", StringComparison.Ordinal)) continue;
            Match m;
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
            else if ((m = Regex.Match(line, @"^(?:local\s+)?\w+\s*=\s*Duel\.SelectTarget\(tp,([\w.]+),tp,([\w+]+),([\w+]+),(\d+),\4,(nil|e:GetHandler\(\)|c)(?:,e,tp)?\)$")).Success)
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

    /// <summary>The operation's statements, matched to DO blocks in order, with "and if you do" / "then" links.</summary>
    private static List<BlockInstance> Operation(string reference, Functions fns, TargetInfo target, CardData card)
    {
        var (_, body) = fns.Get(reference);
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
                || Regex.IsMatch(line, @"^if not \w+:IsRelateToEffect\(e\) then return end$"))
                continue;
            Match m;
            if (line == "local tc=Duel.GetFirstTarget()" && target.Filter is not null) { targetCard = true; continue; }
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectMatchingCard\(tp,([\w.()]+),tp,([\w+]+),([\w+]+),(\d+),\5,(nil|e:GetHandler\(\)|c)(?:,e,tp)?\)$")).Success)
            {
                var f = Filter(m.Groups[2].Value, fns);
                Locate(f, m.Groups[3].Value, m.Groups[4].Value);
                f.ExcludeThisCard = m.Groups[6].Value != "nil";
                groups[m.Groups[1].Value] = (f, Int(m.Groups[5].Value));
                continue;
            }
            if (line == "Duel.BreakEffect()") { link = ActionLink.Then; continue; }
            if ((m = Regex.Match(line, @"^if (.+?)(?:~=0|>0) then$")).Success)
            {
                // "if Duel.Draw(...)>0 then": the next action happens only if this one did.
                var inner = m.Groups[1].Value;
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
        int Amount(string d) => d == "d" ? target.Param ?? throw No("its amount is unclear") : Int(d);

        if ((m = Regex.Match(line, @"^Duel\.Draw\((\w+|1-tp),(\w+),REASON_EFFECT\)$")).Success)
            return Block("do.draw").With("who", Who(m.Groups[1].Value)).With("count", Amount(m.Groups[2].Value));
        if ((m = Regex.Match(line, @"^Duel\.Damage\((\w+|1-tp),(\w+),REASON_EFFECT\)$")).Success)
            return Block("do.damage").With("who", Who(m.Groups[1].Value) == "you" ? "you" : "opponent").With("amount", Amount(m.Groups[2].Value));
        if ((m = Regex.Match(line, @"^Duel\.Recover\((\w+|1-tp),(\w+),REASON_EFFECT\)$")).Success)
            return Block("do.gain_lp").With("who", Who(m.Groups[1].Value)).With("amount", Amount(m.Groups[2].Value));
        if ((m = Regex.Match(line, @"^Duel\.DiscardDeck\((tp|1-tp),(\d+),REASON_EFFECT\)$")).Success)
            return Block("do.mill").With("whose", m.Groups[1].Value == "tp" ? "you" : "opponent").With("count", Int(m.Groups[2].Value));
        if (line == "Duel.NegateActivation(ev)") return Block("do.negate").With("what", "activation");
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
        };
        foreach (var (pattern, blockId) in verbs)
        {
            m = Regex.Match(line, pattern);
            if (!m.Success) continue;
            var what = m.Groups[1].Value;
            var position = m.Groups.Count > 2 && m.Groups[2].Success ? m.Groups[2].Value switch { "POS_FACEUP_ATTACK" => "attack", "POS_FACEUP_DEFENSE" => "defense", _ => "faceup" } : null;
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

            var id = blockId == "hand"
                ? (filter.Zones & (Model.Zone.Deck | Model.Zone.Graveyard | Model.Zone.Banished)) != 0 && (filter.Zones & Model.Zone.Field) == 0 ? "do.add_to_hand" : "do.return_to_hand"
                : blockId;
            var block = Block(id).With("count", count).With("cards", filter);
            if (BlockRegistry.Find(id)!.Param("target") is not null) block = block.With("target", targeted);
            else if (targeted) throw No("it targets in a way that block can't");
            if (position is not null && BlockRegistry.Find(id)!.Param("position") is not null) block = block.With("position", position);
            return block;
        }
        throw No($"it does something the blocks don't ({Short(line)})");
    }

    // ---------------------------------------------------------------- lasting effects

    private static EffectEvent Passive(EffectEvent effect, HashSet<string> type, string code, Dictionary<string, string> set, string? range, HashSet<string> props, Functions fns, CardData card)
    {
        effect.Limit = OncePerTurn.None;
        effect.When = Block("when.while_face_up").With("zone", Zone(range ?? "LOCATION_MZONE"));
        var value = set.GetValueOrDefault("Value");
        if (set.ContainsKey("Condition") || set.ContainsKey("Target") && !type.Contains("EFFECT_TYPE_FIELD"))
            throw No("its lasting effect has a condition");
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
        throw No("its lasting effect isn't one the blocks have");
    }

    // ---------------------------------------------------------------- filters

    private static readonly Dictionary<string, CardRace> Races = Enum.GetValues<CardRace>().Where(r => r != CardRace.None)
        .ToDictionary(r => EnumNames.LuaConstant(r), r => r);
    private static readonly Dictionary<string, CardAttribute> Attributes = Enum.GetValues<CardAttribute>().Where(a => a != CardAttribute.None)
        .ToDictionary(a => EnumNames.LuaConstant(a), a => a);

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
        if (!m.Success || text.Contains(" or ", StringComparison.Ordinal)) throw No("it picks cards in a way the blocks can't");
        foreach (var term in m.Groups[1].Value.Split(" and ")) Term(term.Trim(), f);
        return f;
    }

    private static void Term(string term, CardFilter f)
    {
        Match m;
        if (Regex.IsMatch(term, @"^c:(IsAbleTo\w+|IsDiscardable|IsDestructable|IsSSetable|IsCanBeSpecialSummoned)\(.*\)$")) return;
        if (term == "c:IsFaceup()") { f.FaceUpOnly = true; return; }
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

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
