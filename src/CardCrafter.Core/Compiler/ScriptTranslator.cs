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
    /// <summary>A setting of the Summoning conditions the piece stands for, like a Contact Fusion.</summary>
    public Action<SummonProcedure>? ProcedureSetting { get; init; }
    /// <summary>Why it stays as script.</summary>
    public string? Reason { get; init; }
    public bool Translated => Effect is not null || Procedure is not null || Ignored || Rule is not null || NoSpecialSummon || EquipTo is not null || Setting is not null || ProcedureSetting is not null;

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
    [ThreadStatic] private static CardData? _sourceCard;

    private static Untranslatable No(string reason) => new(reason);

    public static TranslationResult Translate(string lua, long id, CardData card, IReadOnlyList<string>? strings = null)
    {
        var result = new TranslationResult();
        _sourceId = id;
        _sourceCard = card;
        _sourceLua = lua;
        _chainCounter = Regex.Match(lua, @"Duel\.AddCustomActivityCounter\((?:id|\d+),ACTIVITY_CHAIN,(s|c\d+)\.(\w+)\)") is { Success: true } cm
            && Regex.Match(lua, $@"function {cm.Groups[1].Value}\.{cm.Groups[2].Value}\(re,tp,cid\)\s*\n(.*?)\nend", RegexOptions.Singleline) is { Success: true } fm
            ? string.Join(" ", fm.Groups[1].Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)) : null;
        // "Return it to the hand/Extra Deck": sending to the hand puts Extra Deck monsters back there.
        lua = lua.Replace("aux.OR(Card.IsAbleToHand,Card.IsAbleToExtra)", "Card.IsAbleToHand", StringComparison.Ordinal);
        lua = lua.Replace("\r\n", "\n", StringComparison.Ordinal);
        lua = UsesPerMaterials(lua);
        lua = EqualToStat(lua);
        lua = MechanicsRewrite(lua);
        lua = TreatedAsRewrite(lua);
        lua = NegatedSummonRewrite(lua);
        lua = BanishReturnRewrite(lua);
        lua = BanishUntilRewrite(lua);
        var pieces = LuaPieces.Split(lua, id, strings);
        _ritualCalls = null;
        foreach (var piece in pieces)
        {
            try
            {
                result.Pieces.Add(SettleRequirements(TranslatePiece(piece, card)));
            }
            catch (Untranslatable ex)
            {
                result.Pieces.Add(new TranslatedPiece { Piece = piece, Reason = ex.Message });
            }
        }
        CounterPermits(result);
        ContactFusionRules(result);
        ToonDirectUnless(result);
        // One WHEN block per event, as the editor offers them.
        foreach (var e in result.Pieces.SelectMany(p => p.Effects)) TriggerSplit.Normalize(e);
        return result;
    }

    /// <summary>
    /// Red-Eyes Dark Dragoon's "a number of times per turn up to the number of Normal Monsters used as material": a
    /// material check counts the materials into a flag when the card is Summoned, the effect's condition reads the flag
    /// and its target counts the uses. All of it becomes one IF block, so its pieces of script are taken out here and
    /// the condition is replaced with a marker <see cref="Conditions"/> reads.
    /// </summary>
    private static string UsesPerMaterials(string lua)
    {
        var val = Regex.Match(lua, @"function ([\w.]+)\(e,c\)\s*local g=c:GetMaterial\(\)\s*local ct=g:FilterCount\(Card\.Is(?:Fusion)?Type,nil,(TYPE_\w+)\)\s*e:GetLabelObject\(\):SetLabel\(ct\)\s*end");
        var mat = Regex.Match(lua, @"function ([\w.]+)\(e,tp,eg,ep,ev,re,r,rp\)\s*e:GetHandler\(\):RegisterFlagEffect\((\d+|id\+\d+),RESET_EVENT\+RESETS_STANDARD,0,1,e:GetLabel\(\)\)\s*end");
        if (!val.Success || !mat.Success) return lua;
        var kind = val.Groups[2].Value switch
        {
            "TYPE_NORMAL" => "normal", "TYPE_EFFECT" => "effect", "TYPE_TUNER" => "tuner", "TYPE_FUSION" => "fusion", "TYPE_SYNCHRO" => "synchro",
            "TYPE_XYZ" => "xyz", "TYPE_LINK" => "link", "TYPE_RITUAL" => "ritual", "TYPE_MONSTER" => "monster", _ => null,
        };
        if (kind is null) return lua;
        var label = Regex.Escape(mat.Groups[2].Value);
        var con = Regex.Match(lua, $@"function ([\w.]+)\(e,tp,eg,ep,ev,re,r,rp\)\s*return e:GetHandler\(\):GetFlagEffectLabel\({label}\) and e:GetHandler\(\):GetFlagEffectLabel\({label}\)>0\s*end");
        var use = Regex.Match(lua, $@"\s*and (?:c|e:GetHandler\(\)):GetFlagEffect\((\d+|id)\)<(?:c|e:GetHandler\(\)):GetFlagEffectLabel\({label}\)");
        if (!con.Success || !use.Success) return lua;
        var flag = Regex.Escape(use.Groups[1].Value);
        var reg = Regex.Match(lua, $@"\n\s*(?:c|e:GetHandler\(\)):RegisterFlagEffect\({flag},RESET_EVENT\+RESETS_STANDARD\+RESET_PHASE\+PHASE_END,0,1\)");
        // The two effects that count the materials: one on EVENT_SPSUMMON_SUCCESS, one EFFECT_MATERIAL_CHECK.
        var effects = Regex.Matches(lua, @"\n[ \t]*(?:--[^\n]*\n[ \t]*)?local (\w+)=Effect\.CreateEffect\(c\)\n(?:(?![ \t]*c:RegisterEffect\(\1\)).*\n)*?[ \t]*c:RegisterEffect\(\1\)")
            .Where(m => m.Value.Contains($"SetOperation({mat.Groups[1].Value})", StringComparison.Ordinal) || m.Value.Contains($"SetValue({val.Groups[1].Value})", StringComparison.Ordinal))
            .ToList();
        if (!reg.Success || effects.Count != 2) return lua;
        foreach (var m in effects.OrderByDescending(m => m.Index)) lua = lua.Remove(m.Index, m.Length);
        lua = lua.Replace(reg.Value, "", StringComparison.Ordinal).Replace(use.Value, "", StringComparison.Ordinal);
        return Regex.Replace(lua, Regex.Escape(con.Value), $"function {con.Groups[1].Value}(e,tp,eg,ep,ev,re,r,rp)\n\treturn aux.CardCrafterUsesPerMaterials(\"{kind}\")\nend");
    }

    /// <summary>
    /// "Destroy it, and if you do, inflict damage equal to its original ATK": the ATK kept in a local before the card
    /// leaves becomes the damage block's "equal to that monster's original ATK", read from the cards just destroyed.
    /// </summary>
    private static string EqualToStat(string lua)
    {
        return Regex.Replace(lua, @"(?ms)^function ([\w.]+)\(e,tp,eg,ep,ev,re,r,rp\)\n(.*?)^end", fn =>
        {
            var body = fn.Groups[2].Value;
            // Not this card's own ATK ("c:GetBaseAttack()"): that is no card the step before affected.
            var local = Regex.Match(body, @"^[ \t]*local (\w+)=(?!c:)\w+(?::GetFirst\(\))?:(GetTextAttack|GetAttack|GetBaseAttack|GetTextDefense)\(\)[ \t]*\n", RegexOptions.Multiline);
            if (!local.Success) return fn.Value;
            var v = local.Groups[1].Value;
            var kind = local.Groups[2].Value switch { "GetAttack" => "atk", "GetTextDefense" => "orig_def", _ => "orig_atk" };
            var uses = Regex.Matches(body, $@"\b{v}\b").Count;
            var clamp = Regex.Match(body, $@"^[ \t]*if {v}<0 then {v}=0 end[ \t]*\n", RegexOptions.Multiline);
            var calls = Regex.Matches(body, $@"Duel\.(?:Damage|Recover)\((?:1-tp|tp),{v},REASON_EFFECT\)");
            // Only when the local is used for nothing else.
            if (calls.Count == 0 || uses != 1 + calls.Count + (clamp.Success ? 2 : 0)) return fn.Value;
            body = body.Remove(local.Index, local.Length);
            if (clamp.Success) body = body.Replace(clamp.Value, "", StringComparison.Ordinal);
            body = Regex.Replace(body, $@"(Duel\.(?:Damage|Recover)\((?:1-tp|tp)),{v},REASON_EFFECT\)", $"$1,CCEQ_{kind},REASON_EFFECT)");
            return $"function {fn.Groups[1].Value}(e,tp,eg,ep,ev,re,r,rp)\n{body}end";
        });
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
        // "(This card is always treated as a "HERO" card.)", which the card text says and the database entry holds.
        if (AlwaysTreatedAsReminder(project.Card, pack) is { } reminder) effects.Insert(0, reminder);
        // "Must be Special Summoned by its own way" includes "Cannot be Normal Summoned/Set": the strictest rule wins.
        var rule = result.Pieces.Where(p => p.Rule is not null).Select(p => p.Rule!.Value).DefaultIfEmpty(SummonRule.Normal).Max() is var mr && mr != SummonRule.Normal ? mr : (SummonRule?)null;
        var procedure = result.Pieces.LastOrDefault(p => p.Procedure is not null)?.Procedure ?? project.Procedure;
        if (result.Pieces.Any(p => p.ProcedureSetting is not null)) procedure = PackSerializer.Clone(procedure);
        foreach (var p in result.Pieces) p.ProcedureSetting?.Invoke(procedure);
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
        if (IsXyzBattledWatcher(lines, fns)) return new TranslatedPiece { Piece = piece, Ignored = true };

        // Every effect the piece creates or clones, with its settings, and the ones it registers on the card.
        var vars = new Dictionary<string, Dictionary<string, string>>();
        var registered = new List<string>();
        foreach (var line in lines)
        {
            Match m;
            if ((m = Regex.Match(line, @"^local\s+(\w+)\s*=\s*Effect\.CreateEffect\(c\)$")).Success) { vars[m.Groups[1].Value] = []; continue; }
            // "local e1=aux.AddRitualProcGreater2(c,...,true)": an effect the Ritual helper makes.
            if ((m = Regex.Match(line, @"^local\s+(\w+)\s*=\s*(aux\.AddRitualProc\w+\(c,.*\))$")).Success)
            {
                var (ritualSet, ritualRegistered) = RitualProcEffect(m.Groups[2].Value, fns);
                vars[m.Groups[1].Value] = ritualSet;
                if (ritualRegistered) registered.Add(m.Groups[1].Value);
                continue;
            }
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
        // Stargazer Magician's "this card's Pendulum Scale becomes 4": its left and right Scales (ScriptTranslator.SummonByEffect.cs).
        if (PendulumScalePieceOf(piece, sets, fns) is { } scales) return scales;
        // A Toon monster's rules (ScriptTranslator.Chance.cs).
        if (ToonPieceOf(piece, sets, fns) is { } toon) return toon;

        // "Treated as" a name, archetype, Attribute, Type or Normal Monster, and material treatments (ScriptTranslator.TreatedAs.cs).
        if (TreatedAsPieceOf(piece, sets, fns, card) is { } treatedAs) return treatedAs;
        // Cyber Emergency's "If the activation of this card ... was negated" and its clone (ScriptTranslator.Negated.cs).
        if (NegatedPieceOf(piece, sets, fns, card) is { } negated) return negated;
        // Copies that differ only in what they apply to are one effect on the card ("ATK/DEF", "Normal or Special").
        var merge = Merge.Of(sets);
        if (merge is not null) return TranslateSet(piece, sets[0], merge, fns, card);
        var parts = sets.Select(set => TranslateSet(piece, set, Merge.None, fns, card)).ToList();
        if (parts.Count == 1) return parts[0];
        // Copies on different events that do the same: one effect with "or when" triggers.
        if (OrWhen(sets, parts, card) is { } combined) return new TranslatedPiece { Piece = piece, Effect = combined };
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

    /// <summary>
    /// "If this card is sent to the GY, or if this card is banished": clones of one effect that differ only in their event,
    /// each translated alone, as one effect whose first trigger has the others as "or when" triggers. Null otherwise.
    /// </summary>
    private static EffectEvent? OrWhen(List<Dictionary<string, string>> sets, List<TranslatedPiece> parts, CardData card)
    {
        if (parts.Any(p => p.Effect is null || p.MoreEffects.Count > 0 || p.Effect.When is null)) return null;
        // A condition the trigger reads ("sent from the field") ends up in its WHEN; any other one is compared as IF blocks below.
        var rest = sets.Select(s => s.Where(kv => kv.Key is not ("Code" or "Property" or "Condition")).OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToList()).ToList();
        if (rest.Any(r => !r.SequenceEqual(rest[0]))) return null;
        static string Shape(EffectEvent e) => System.Text.Json.JsonSerializer.Serialize(new { e.If, e.Cost, e.Do, e.Limit, e.SharedLimitGroup });
        var first = parts[0].Effect!;
        if (parts.Skip(1).Any(p => Shape(p.Effect!) != Shape(first))) return null;
        if (parts.Select(p => System.Text.Json.JsonSerializer.Serialize(p.Effect!.When)).Distinct().Count() != parts.Count) return null;
        var combined = PackSerializer.Clone(first);
        var whens = parts.Select(p => p.Effect!.When!).ToList();
        combined.AlsoWhen.AddRange(whens.Skip(1));
        // Only triggers that can share one effect (same kind, used from the same place).
        var check = PackSerializer.Clone(card);
        check.Id = 100000000;
        return ScriptCompiler.Compile(new CardProject { Card = check, Effects = [combined] }).HasErrors ? null : combined;
    }

    /// <summary>How copies of one effect combine into one block: the codes they use, the first being the main one.</summary>
    private sealed record Merge(bool AlsoSpecial, bool AlsoFlip, bool BothStats, bool BothIndestructible, bool CannotTribute, bool AlsoTributed = false)
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
            // Mitsurugi: "If this card is Normal or Special Summoned, or if this card is Tributed".
            if (Is("EVENT_SUMMON_SUCCESS", "EVENT_SPSUMMON_SUCCESS", "EVENT_RELEASE")) return None with { AlsoSpecial = true, AlsoTributed = true };
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
        _lpPaid = false;

        var effect = new EffectEvent { Label = piece.Title };
        var type = Flags(set.GetValueOrDefault("Type") ?? throw No("it has no effect type"));
        var code = set.GetValueOrDefault("Code") ?? "";
        var range = set.GetValueOrDefault("Range");
        var props = Flags(set.GetValueOrDefault("Property") ?? "");
        effect.Limit = CountLimit(set.GetValueOrDefault("CountLimit"), piece.SourceId, out var group, out var times);
        effect.SharedLimitGroup = group;
        effect.Times = times;
        // "Once while face-up on the field": the effect's own count isn't given back each turn.
        if (props.Remove("EFFECT_FLAG_NO_TURN_RESET"))
        {
            if (times != 1 || effect.Limit is not (OncePerTurn.Soft or OncePerTurn.Hard)) throw No("its use comes back in a way the limits don't say");
            (effect.Limit, effect.SharedLimitGroup) = effect.Limit == OncePerTurn.Soft ? (OncePerTurn.FaceUp, 0) : (OncePerTurn.FaceUpAndHard, group);
        }

        // "Activate this card" with nothing else: Card Crafter adds that itself for Continuous, Field and Equip cards.
        if (type.SetEquals(["EFFECT_TYPE_ACTIVATE"]) && code == "EVENT_FREE_CHAIN" && !set.ContainsKey("Operation") && !set.ContainsKey("Target")
            && (!set.ContainsKey("Cost") || IsActivationFlag(set["Cost"])) && !set.ContainsKey("Condition") && effect.Limit == OncePerTurn.None)
            return new TranslatedPiece { Piece = piece, Ignored = true };
        if (IsCannotNegateCompanion(type, code, set, card)) return new TranslatedPiece { Piece = piece, Ignored = true };
        // "If this card destroyed a monster by battle": the watcher on its battles, which the IF block writes itself.
        if (BattleWatcher(type, code, set) is not null) return new TranslatedPiece { Piece = piece, Ignored = true };

        // Yummy Way's "you can treat 1 Link-1 monster you control as a Level 1 Tuner": the Card tab's Synchro procedure writes it.
        if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && code == "EFFECT_SYNCHRO_LEVEL_EX" && range == "LOCATION_EXTRA"
            && (set.GetValueOrDefault("TargetRange") ?? "").Replace(" ", "") == "LOCATION_MZONE,LOCATION_MZONE"
            && set.GetValueOrDefault("Target") is { } stg && Regex.Replace(Body(stg, fns), @"\s+", " ") == "return c:IsType(TYPE_LINK) and c:IsLink(1)"
            && set.GetValueOrDefault("Value") is { } sval && Regex.Replace(Body(sval, fns), @"\s+", " ") == "if e:GetHandler()==syncard then return 1 else return 0 end")
            return new TranslatedPiece { Piece = piece, Ignored = true };
        // "Must be Fusion Summoned" and the like: a rule on the Card tab, not an effect.
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_SPSUMMON_CONDITION"
            && set.GetValueOrDefault("Value") is "aux.FALSE" or "aux.fuslimit" or "aux.synlimit" or "aux.xyzlimit" or "aux.linklimit" or "aux.ritlimit")
            return new TranslatedPiece { Piece = piece, Rule = SummonRule.ProcedureOnly };
        // "Must be Fusion Summoned" written out, and "Must first be ... Summoned" (it can be revived afterwards).
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_SPSUMMON_CONDITION" && set.GetValueOrDefault("Value") is { } scv
            && !Regex.IsMatch(scv, @"^aux\.") && SummonConditionRule(scv, fns) is { } scRule)
            return new TranslatedPiece { Piece = piece, Rule = scRule };
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

        // Floodgates: locks on players, "banished instead", negating effects on the field.
        if (IsNegateHelper(type, set, fns)) return new TranslatedPiece { Piece = piece, Ignored = true };
        // "If another Reptile monster(s) you control would be destroyed..., you can Tribute this card instead."
        if (type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_CONTINUOUS"]) && code == "EFFECT_DESTROY_REPLACE" && range is not null)
        {
            effect.When = Block("when.while_face_up").With("zone", Zone(range));
            effect.Do.Add(ReplaceOf(set, fns));
            if (set.GetValueOrDefault("Condition") is { } replaceCondition) effect.If.AddRange(Conditions(replaceCondition, fns));
            return new TranslatedPiece { Piece = piece, Effect = effect };
        }
        // "If your opponent controls a card, you can activate this card from your hand."
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_TRAP_ACT_IN_HAND" && card.Kind == CardKind.Trap && !set.ContainsKey("Value"))
        {
            effect.Limit = OncePerTurn.None;
            effect.When = Block("when.while_face_up");
            effect.Do.Add(Block(ActivateFromHandPassive.BlockId));
            if (set.GetValueOrDefault("Condition") is { } handCondition) effect.If.AddRange(Conditions(handCondition, fns));
            return new TranslatedPiece { Piece = piece, Effect = effect };
        }
        // "Your opponent must keep their hand revealed."
        if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && code == "EFFECT_PUBLIC" && range is not null
            && (set.GetValueOrDefault("TargetRange") ?? "").Replace(" ", "") is "0,LOCATION_HAND" or "LOCATION_HAND,LOCATION_HAND" && !set.ContainsKey("Target"))
        {
            effect.Limit = OncePerTurn.None;
            effect.When = Block("when.while_face_up").With("zone", Zone(range));
            effect.Do.Add(Block("passive.reveal_hand").With("who", set["TargetRange"].Replace(" ", "") == "0,LOCATION_HAND" ? "opponent" : "both"));
            if (set.GetValueOrDefault("Condition") is { } publicCondition) effect.If.AddRange(Conditions(publicCondition, fns));
            return new TranslatedPiece { Piece = piece, Effect = effect };
        }
        if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && range is not null && !set.ContainsKey("Reset"))
        {
            BlockInstance? gate = null;
            if (LockOf(set, fns) is { } lockout && CombinedLock([lockout.What]) is not null)
                gate = Block("passive.lock").With("who", lockout.Who).With("what", lockout.What).With("cards", lockout.Except);
            else if (BanishInsteadOf(set, fns) is { } instead)
                gate = Block("passive.banish_instead").With("what", instead.What).With("whose", instead.Whose).With("from", instead.From);
            else if (NegateAllOf(type, set, fns) is { } negate)
                gate = Block("passive.negate_all").With("what", negate);
            if (gate is not null)
            {
                effect.Limit = OncePerTurn.None;
                effect.When = Block("when.while_face_up").With("zone", Zone(range));
                effect.Do.Add(gate);
                if (set.GetValueOrDefault("Condition") is { } gateCondition) effect.If.AddRange(Conditions(gateCondition, fns));
                return new TranslatedPiece { Piece = piece, Effect = effect };
            }
        }

        // "Each time a Spell Card is activated, place 1 Spell Counter on this card when that Spell resolves."
        if (CounterAccumulator(piece, effect, type, code, set, range, props, fns) is { } accumulator) return accumulator;
        // "An Xyz Monster whose original Type is Machine and has this card as material gains this effect": the same lasting
        // effect, given to the Xyz Monster this card is under; its condition says what that monster must be.
        if (type.Contains("EFFECT_TYPE_XMATERIAL") && !type.Overlaps(["EFFECT_TYPE_IGNITION", "EFFECT_TYPE_QUICK_O", "EFFECT_TYPE_QUICK_F", "EFFECT_TYPE_TRIGGER_O", "EFFECT_TYPE_TRIGGER_F", "EFFECT_TYPE_CONTINUOUS"]))
        {
            var inner = type.Where(t => t != "EFFECT_TYPE_XMATERIAL").ToHashSet();
            if (inner.Count == 0) inner.Add("EFFECT_TYPE_SINGLE");
            var rest = new Dictionary<string, string>(set);
            var xyz = rest.Remove("Condition", out var xcon) ? XyzGainer(xcon, fns)
                : new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz };
            var gained = Passive(effect, inner, code, rest, range, props, fns, card, merge);
            gained.When = Block("when.while_face_up").With("as", "xyz").With("xyz", xyz);
            return new TranslatedPiece { Piece = piece, Effect = gained };
        }
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
        var (when, usedCondition) = TriggerOrDetailed(type, code, range, condition, alsoSpecial, fns, merge.AlsoTributed, props);
        if (merge.AlsoFlip)
        {
            if (when.BlockId != "when.summoned") throw No("it registers more than one effect");
            when = when.With("how", "any");
        }
        if (damageStep) when = when.BlockId is "when.quick" or "when.activate" ? when.With("damage_step", true) : throw No("it can be used in the Damage Step in a way its WHEN block doesn't say");
        effect.When = when;
        effect.AlsoWhen.AddRange(TakePendingAlsoWhen());
        if (condition is not null && !usedCondition) effect.If.AddRange(Conditions(condition, fns));
        effect.If.AddRange(TakePendingIf());
        // An activation watching for its own negation: the negated trigger block writes that (ScriptTranslator.Negated.cs).
        if (set.GetValueOrDefault("Cost") is { } cost && !(type.SetEquals(["EFFECT_TYPE_ACTIVATE"]) && (IsNegationWatch(cost) || IsActivationFlag(cost)))) effect.Cost.AddRange(Costs(cost, fns));
        effect.Cost.AddRange(MechanicsCosts(type, props));
        // A Ritual helper's effect, and Polymerization's family: one Ritual or Fusion Summon block.
        if (RitualOf(set) is { } ritual)
        {
            effect.Do.Add(ritual);
            return new TranslatedPiece { Piece = piece, Effect = effect };
        }
        // Equips, excavation, Deck placement, Trap Monsters and the other mechanics (ScriptTranslator.Mechanics.cs).
        if (WholeEffectOf(type, set, fns, card) is { } mechanic) { effect.Do.Add(mechanic); return new TranslatedPiece { Piece = piece, Effect = effect }; }
        if (set.TryGetValue("Operation", out var fusionOp) && FusionEffect(effect, fusionOp, set.GetValueOrDefault("Target"), fns))
            return new TranslatedPiece { Piece = piece, Effect = effect };
        if (set.TryGetValue("Operation", out fusionOp) && FusionOf(fusionOp, set.GetValueOrDefault("Target"), fns) is { } fusion)
        {
            effect.Do.Add(fusion);
            return new TranslatedPiece { Piece = piece, Effect = effect };
        }
        // Summons by effect, Rank-Ups, Pendulum Scales and placing this card in the Pendulum Zone (ScriptTranslator.SummonByEffect.cs).
        if (set.TryGetValue("Operation", out var wholeOp) && SummonByEffectOf(wholeOp, set.GetValueOrDefault("Target"), fns) is { } whole)
        {
            effect.Do.AddRange(whole);
            return new TranslatedPiece { Piece = piece, Effect = effect };
        }
        _eventCode = code;
        _effectRange = range;
        var hasTarget = set.TryGetValue("Target", out var tg);
        // "Activate 1 of these effects": each option's own target step and operation (ScriptTranslator.Selections.cs).
        ResetCounts();
        var options = OptionsOf(set, fns, card);
        var targeting = hasTarget && options is null ? Targeting(tg!, fns) : new TargetInfo();
        effect.Cost.AddRange(TakeChainLimits());
        if (!set.TryGetValue("Operation", out var op)) throw No("it has no operation");
        // Coin tosses, die rolls and declarations (ScriptTranslator.Chance.cs).
        effect.Do.AddRange(options ?? ChanceOperation(effect, op, set.GetValueOrDefault("Target"), fns, targeting, card) ?? Operation(op, fns, targeting, card));
        if (PairTargets(targeting, effect.Do) is { } pair) effect.Do = [pair];
        VerifyCounts(targeting, set.GetValueOrDefault("Condition"), fns);
        TreatedAsCostCheck(effect, range);
        if (effect.Do.Count == 0) throw No("its operation does nothing the blocks recognise");
        CounterActivationChecks(effect, targeting, type);
        OneTargetingBlock(effect);
        // Value blocks: the activation must ask what the blocks ask, or IF blocks say the rest.
        if (effect.Do.Any(b => b.BlockId is StatsFromValueAction.BlockId or LifeFromValueAction.BlockId))
            effect.If.AddRange(ValueActivationChecks(effect, targeting, tg is not null, fns));
        else if (targeting.CheckOnItsOwnLines is { } ownLines) throw No($"its target step does something the blocks don't ({ownLines})");
        VerifyChainChecks(effect, card);
        return new TranslatedPiece { Piece = piece, Effect = effect };
    }

    /// <summary>Single statements in initial_effect: summon procedures and revive limits.</summary>
    private static TranslatedPiece? TranslateStatement(string line, CardData card, Functions fns)
    {
        Match m;
        if (RitualFusionStatement(line, fns) is { } ritualFusion) return ritualFusion;
        if (MechanicsStatement(line, fns) is { } mechanics) return mechanics;
        // Lines that only describe the card, or that Card Crafter writes itself from the Card tab.
        if (line == "c:EnableUnsummonable()") return new TranslatedPiece { Piece = null!, Rule = SummonRule.SpecialSummonOnly };
        if ((m = Regex.Match(line, @"^c:SetUniqueOnField\(1,0,(id|\d+)\)$")).Success && (m.Groups[1].Value == "id" || Int64(m.Groups[1].Value) == _sourceId))
            return new TranslatedPiece { Piece = null!, Setting = c => c.OnlyOneOnField = true };
        if ((m = Regex.Match(line, @"^c:SetSPSummonOnce\((id|\d+)\)$")).Success && (m.Groups[1].Value == "id" || Int64(m.Groups[1].Value) == _sourceId))
            return new TranslatedPiece { Piece = null!, Setting = c => c.SpecialSummonOncePerTurn = true };
        // "This card's name becomes "X" while on the field or in the GY."
        if (ChangeCodeStatement(line, card, fns) is { } changeCode) return changeCode;
        // Gemini and Spirit monsters get these lines from the Card tab's abilities.
        if ((line == "aux.EnableDualAttribute(c)" && card.Abilities.HasFlag(MonsterAbility.Gemini))
            || (line == "aux.EnableSpiritReturn(c,EVENT_SUMMON_SUCCESS,EVENT_FLIP)" && card.Abilities.HasFlag(MonsterAbility.Spirit)))
            return new TranslatedPiece { Piece = null!, Ignored = true };
        if (CounterPermit(line) is { } permit) return permit;
        // The counter behind "the turn you activate this" locks: the lock block writes its own.
        if (Regex.IsMatch(line, @"^Duel\.AddCustomActivityCounter\((?:id|\d+),ACTIVITY_(?:SPSUMMON|SUMMON|CHAIN|ATTACK),[\w.]+\)$"))
            return new TranslatedPiece { Piece = null!, Ignored = true };
        // "Send this card to the GY" (EFFECT_SELF_TOGRAVE): the block enables the engine's check itself.
        if (line == "Duel.EnableGlobalFlag(GLOBALFLAG_SELF_TOGRAVE)") return new TranslatedPiece { Piece = null!, Ignored = true };
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
        m = Regex.Match(line, @"^aux\.AddFusionProcCode(?:2|3|4)?\(c,([\d,]+),(true|false),(?:true|false)\)$");
        if (m.Success)
        {
            var codes = m.Groups[1].Value.Split(',').Select(s => long.Parse(s, CultureInfo.InvariantCulture)).ToList();
            return new TranslatedPiece { Piece = null!, Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = codes, MinMaterials = codes.Count, FusionNoSubstitutes = m.Groups[2].Value == "false" } };
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
        // Apollousa: "2+ monsters ..., except Tokens".
        if (expr == "aux.NOT(aux.FilterBoolFunction(Card.IsLinkType,TYPE_TOKEN))") { f.MonsterKind = MonsterKind.NonToken; return f; }
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
        var m = Regex.Match(line, @"^aux\.(AddSynchroProcedure|AddSynchroMixProcedure|AddXyzProcedure|AddXyzProcedureLevelFree|AddLinkProcedure|AddFusionProcFun2|AddFusionProcFunRep|AddFusionProcFunRep2|AddFusionProcCodeFun|AddFusionProcCodeRep|AddFusionProcMix)\(c,(.*)\)$");
        if (!m.Success) return null;
        var args = SplitArgs(m.Groups[2].Value);
        int Num(int i) => args.Count > i && Regex.IsMatch(args[i], @"^\d+$") ? Int(args[i]) : throw No("its material count is unusual");
        bool Absent(int i) => args.Count <= i || args[i] == "nil";
        MaterialSlot Slot(string expr, int min = 1, int max = 1)
        {
            // {74677422,s.mfilter}: "Red-Eyes Black Dragon" or a card the filter allows.
            if (expr.StartsWith('{') && expr.EndsWith('}'))
            {
                var either = SplitArgs(expr[1..^1]);
                if (either.Count != 2) throw No("one of its Fusion Materials can be any of more than 2 kinds");
                return new() { Filter = Material(either[0], fns), Or = Material(either[1], fns), Min = min, Max = max };
            }
            return new() { Filter = Material(expr, fns), Min = min, Max = max };
        }
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
            // "You can also Xyz Summon this card by using 1 Xyz Monster you control": its other material, prompt, check and cost.
            case "AddXyzProcedure" when args.Count is >= 5 and <= 7 && !Absent(3):
            {
                var lv = Num(1);
                var min = Num(2);
                var alt = Filter(args[3], fns);
                alt.Owner = CardOwner.You;
                alt.Zones = Model.Zone.MonsterZone;
                alt.FaceUpOnly = false;
                if (alt.CardType == FilterCardType.Any) alt.CardType = FilterCardType.Monster;
                var max = args.Count >= 6 && !Absent(5) ? Num(5) : min;
                var proc = new SummonProcedure
                {
                    Kind = SummonProcedureKind.Xyz, MaterialFilter = Material(args[0], fns), MinMaterials = min, MaxMaterials = max > min ? max : 0,
                    XyzMaterialLevel = lv == card.Level ? 0 : lv, XyzAlternate = true, XyzAlternateFilter = alt,
                };
                if (args.Count == 7 && !Absent(6)) XyzAlternateOp(args[6], fns, proc);
                return proc;
            }
            case "AddLinkProcedure" when args.Count is 2 or 3 or 4:
            {
                var proc = new SummonProcedure { Kind = SummonProcedureKind.Link, MaterialFilter = Material(args[0], fns), MinMaterials = Num(1), MaxMaterials = Absent(2) ? 0 : Num(2) };
                if (args.Count == 4)
                {
                    // "including a "Sky Striker" monster", "with different names": the group check, part by part.
                    var (_, body) = fns.Get(args[3]);
                    var text = string.Join(" ", body.Select(l => l.Trim()));
                    if (!text.StartsWith("return ", StringComparison.Ordinal)) throw No("its materials are checked as a group in a way the blocks can't");
                    var distinct = new HashSet<string>();
                    foreach (var part in SplitTopLevelAnd(text["return ".Length..]))
                    {
                        Match dm;
                        if ((dm = Regex.Match(part, @"^g:GetClassCount\(Card\.(GetLinkCode|GetLinkRace|GetLinkAttribute)\)==(?:g:GetCount\(\)|#g)$")).Success) { distinct.Add(dm.Groups[1].Value); continue; }
                        var inc = Regex.Match(part, @"^g:IsExists\(([\w.]+)(?:,1,nil(?:,([\w+]+))?)?\)$");
                        if (!inc.Success || proc.Including) throw No("its materials are checked as a group in a way the blocks can't");
                        proc.Including = true;
                        proc.IncludeFilter = inc.Groups[1].Value.StartsWith("Card.", StringComparison.Ordinal)
                            ? Material($"aux.FilterBoolFunction({inc.Groups[1].Value},{(inc.Groups[2].Success ? inc.Groups[2].Value : throw No("its materials are checked as a group in a way the blocks can't"))})", fns)
                            : Material(inc.Groups[1].Value, fns);
                    }
                    proc.MaterialRule = (distinct.Contains("GetLinkCode"), distinct.Contains("GetLinkRace"), distinct.Contains("GetLinkAttribute")) switch
                    {
                        (false, false, false) => MaterialGroupRule.None,
                        (true, false, false) => MaterialGroupRule.DifferentNames,
                        (false, true, true) => MaterialGroupRule.DifferentTypesAndAttributes,
                        (false, false, true) => MaterialGroupRule.DifferentAttributes,
                        (false, true, false) => MaterialGroupRule.DifferentTypes,
                        _ => throw No("its materials are checked as a group in a way the blocks can't"),
                    };
                }
                return proc;
            }
            // "2 Xyz Monsters with the same Rank" (Number F0: Utopic Future), maybe with the other way to Xyz Summon.
            case "AddXyzProcedureLevelFree" when args.Count is 4 or 6 or 7:
            {
                if (Regex.Replace(Body(args[1], fns), @"\s+", " ") != "return g:GetClassCount(Card.GetRank)==1") throw No("its Xyz materials are checked as a group in a way the blocks can't");
                var min = Num(2);
                var max = Num(3);
                var proc = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MaterialFilter = Material(args[0], fns), MinMaterials = min, MaxMaterials = max > min ? max : 0, XyzSameRank = true };
                if (args.Count >= 6)
                {
                    var alt = Filter(args[4], fns);
                    alt.Owner = CardOwner.You;
                    alt.Zones = Model.Zone.MonsterZone;
                    alt.FaceUpOnly = false;
                    if (alt.CardType == FilterCardType.Any) alt.CardType = FilterCardType.Monster;
                    proc.XyzAlternate = true;
                    proc.XyzAlternateFilter = alt;
                    if (args.Count == 7 && !Absent(6)) XyzAlternateOp(args[6], fns, proc);
                }
                return proc;
            }
            // "For this card's Synchro Summon, you can treat 1 Link-1 monster you control as a Level 1 Tuner" (Yummy Way):
            // the Level the Link monster has for it is its own effect (EFFECT_SYNCHRO_LEVEL_EX), which the Card tab writes too.
            case "AddSynchroMixProcedure" when args.Count == 6 && Absent(1) && Absent(2):
            {
                if (Regex.Replace(Body(args[0], fns), @"\s+", " ") != "return c:IsTuner(syncard) or c:IsType(TYPE_LINK) and c:IsLink(1)"
                    || Regex.Replace(Body(args[3], fns), @"\s+", " ") != "return c:IsNotTuner(syncard) and not c:IsType(TYPE_LINK)")
                    throw No("its Synchro materials are picked in a way the blocks can't");
                var max = Num(5);
                return new SummonProcedure { Kind = SummonProcedureKind.Synchro, MinMaterials = Num(4), MaxMaterials = max >= 99 ? 0 : max, SynchroLinkAsTuner = true };
            }
            // The helpers that take no "sub" can't use Fusion substitutes for named materials.
            case "AddFusionProcFun2" when args.Count == 3:
                return NoSub(Fusion([Slot(args[0]), Slot(args[1])]), true);
            case "AddFusionProcFunRep" when args.Count == 3:
                return NoSub(Fusion([Slot(args[0], Num(1), Num(1))]), true);
            case "AddFusionProcFunRep2" when args.Count == 4:
                return NoSub(Fusion([Slot(args[0], Num(1), Num(2) >= 63 ? 0 : Num(2))]), true);
            case "AddFusionProcCodeFun" when args.Count == 5:
                return NoSub(Fusion([Slot(args[0]), Slot(args[1], Num(2), Num(2))]), args[3] == "false");
            case "AddFusionProcCodeRep" when args.Count == 4:
                return NoSub(Fusion([Slot(args[0], Num(1), Num(1))]), args[2] == "false");
            case "AddFusionProcMix" when args.Count >= 4 && args[0] is "true" or "false" && args[1] is "true" or "false":
                return NoSub(Fusion(args.Skip(2).Select(a => Slot(a)).ToList()), args[0] == "false");
        }
        throw No($"its Summon procedure is written in a way the blocks can't ({Short(line)})");

        static SummonProcedure NoSub(SummonProcedure p, bool noSub)
        {
            p.FusionNoSubstitutes = noSub;
            return p;
        }

        static SummonProcedure Fusion(List<MaterialSlot> slots)
        {
            if (slots.Count(s => s.Repeats) > 1) throw No("more than one of its Fusion Materials repeats");
            return new SummonProcedure { Kind = SummonProcedureKind.Fusion, Materials = slots, MinMaterials = slots.Sum(s => s.Min) };
        }
    }

    /// <summary>
    /// The check and cost of the other way to Xyz Summon: once per turn (a player flag), "if an Xyz Monster battled this
    /// turn" (a flag the battle watcher sets) and detaching materials from the monster used.
    /// </summary>
    private static void XyzAlternateOp(string expr, Functions fns, SummonProcedure proc)
    {
        var (_, body) = fns.Get(expr);
        var text = string.Join(" ", body.Select(l => l.Trim()));
        var m = Regex.Match(text, @"^if chk==0 then return (.+?) end(.*)$");
        if (!m.Success) throw No("its other way to Xyz Summon is checked in a way the blocks can't");
        string? once = null;
        string? discard = null;
        foreach (var raw in SplitTopLevelAnd(m.Groups[1].Value))
        {
            var t = raw.Trim();
            Match tm;
            if (t == "true") continue;
            // "by discarding 1 card and using ..." (Gustav Rocket).
            if ((tm = Regex.Match(t, @"^Duel\.IsExistingMatchingCard\(([\w.]+),tp,LOCATION_HAND,0,(\d+),nil\)$")).Success
                && Regex.Replace(Body(tm.Groups[1].Value, fns), @"\s+", " ") == "return c:IsDiscardable(REASON_SPSUMMON)")
            {
                discard = tm.Groups[1].Value;
                proc.XyzAlternateDiscard = Int(tm.Groups[2].Value);
                continue;
            }
            // "if an effect of "Kashtira Shangri-Ira" was successfully activated this turn" (Kashtira Arise-Heart).
            if (Regex.IsMatch(t, @"^\(Duel\.GetCustomActivityCount\((?:id|\d+),tp,ACTIVITY_CHAIN\)>0 or Duel\.GetCustomActivityCount\((?:id|\d+),1-tp,ACTIVITY_CHAIN\)>0\)$")
                && _chainCounter is { } counted && Regex.Match(counted, @"^return not re:GetHandler\(\):IsCode\((\d+)\)$") is { Success: true } cm)
            {
                proc.XyzAlternateAfterActivationOf = Int64(cm.Groups[1].Value);
                continue;
            }
            if ((tm = Regex.Match(t, @"^Duel\.GetFlagEffect\(tp,([\w+]+)\)==0$")).Success) { once = tm.Groups[1].Value; continue; }
            if (Regex.IsMatch(t, @"^Duel\.GetFlagEffect\(tp,[\w+]+\)>0$")) { proc.XyzAlternateAfterXyzBattle = true; continue; }
            if ((tm = Regex.Match(t, @"^mc:CheckRemoveOverlayCard\(tp,(\d+),REASON_COST\)$")).Success) { proc.XyzAlternateDetach = Int(tm.Groups[1].Value); continue; }
            throw No($"its other way to Xyz Summon is checked in a way the blocks can't ({Short(t)})");
        }
        var pay = m.Groups[2].Value.Trim();
        if (proc.XyzAlternateDetach > 0)
        {
            var detach = $"mc:RemoveOverlayCard(tp,{proc.XyzAlternateDetach},{proc.XyzAlternateDetach},REASON_COST)";
            if (!pay.Contains(detach, StringComparison.Ordinal)) throw No("its other way to Xyz Summon has a cost the blocks can't");
            pay = pay.Replace(detach, "", StringComparison.Ordinal);
        }
        if (discard is not null)
        {
            var n = proc.XyzAlternateDiscard;
            var pays = $"Duel.DiscardHand(tp,{discard},{n},{n},REASON_COST+REASON_DISCARD)";
            if (!pay.Contains(pays, StringComparison.Ordinal)) throw No("its other way to Xyz Summon has a cost the blocks can't");
            pay = pay.Replace(pays, "", StringComparison.Ordinal);
        }
        if (once is not null)
        {
            var flag = $"Duel.RegisterFlagEffect(tp,{once},RESET_PHASE+PHASE_END,EFFECT_FLAG_OATH,1)";
            if (!pay.Contains(flag, StringComparison.Ordinal)) throw No("its other way to Xyz Summon is limited in a way the blocks can't");
            pay = pay.Replace(flag, "", StringComparison.Ordinal);
            proc.XyzAlternateOncePerTurn = true;
        }
        if (pay.Trim().Length > 0) throw No($"its other way to Xyz Summon does something the blocks can't ({Short(pay.Trim())})");
    }

    /// <summary>
    /// "if not s.global_check then ... EVENT_BATTLED ... end" that marks both players when an Xyz Monster battles: what
    /// "if an Xyz Monster battled this turn, you can also Xyz Summon" checks, which the Card tab writes itself.
    /// </summary>
    private static bool IsXyzBattledWatcher(List<string> lines, Functions fns)
    {
        var text = string.Join(" ", lines);
        var m = Regex.Match(text, @"^if not ([\w.]+)\.global_check then \1\.global_check=true local (\w+)=Effect\.CreateEffect\(c\) \2:SetType\(EFFECT_TYPE_FIELD\+EFFECT_TYPE_CONTINUOUS\) \2:SetCode\(EVENT_BATTLED\) \2:SetOperation\(([\w.]+)\) Duel\.RegisterEffect\(\2,0\) end$");
        if (!m.Success) return false;
        var op = string.Join(" ", fns.Get(m.Groups[3].Value).Body.Select(l => l.Trim()));
        var check = Regex.Match(op, @"^if ([\w.]+)\(Duel\.GetAttacker\(\)\) or \1\(Duel\.GetAttackTarget\(\)\) then Duel\.RegisterFlagEffect\(tp,([\w+]+),RESET_PHASE\+PHASE_END,0,1\) Duel\.RegisterFlagEffect\(1-tp,\2,RESET_PHASE\+PHASE_END,0,1\) end$");
        if (!check.Success) return false;
        var fn = string.Join(" ", fns.Get(check.Groups[1].Value).Body.Select(l => l.Trim()));
        return fn == "return c and c:IsType(TYPE_XYZ)";
    }

    // ---------------------------------------------------------------- parts

    private static HashSet<string> Flags(string expr) => expr.Split('+').Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet();

    private static OncePerTurn CountLimit(string? args, long id, out int group, out int times)
    {
        group = 0;
        times = 1;
        if (args is null) return OncePerTurn.None;
        var parts = args.Split(',').Select(a => a.Trim()).ToArray();
        // "Up to twice per turn", "You can only use this effect of X thrice per turn".
        if (parts[0] is not ("1" or "2" or "3" or "4" or "5")) throw No("it can be used more than once per turn");
        times = Int(parts[0]);
        if (parts.Length == 1) return OncePerTurn.Soft;
        if (parts.Length == 2 && parts[1] == "EFFECT_COUNT_CODE_CHAIN" && times == 1) return OncePerTurn.Chain;
        // "Once per turn" kept by this copy of the card even when it changes control or its effects are split, and
        // shared by every effect of the card that keeps it (Voloferniges' Ignition and Quick Effects).
        if (parts.Length == 2 && parts[1] == "EFFECT_COUNT_CODE_SINGLE") { group = 1; return OncePerTurn.Soft; }
        var key = parts[1].Replace(id.ToString(CultureInfo.InvariantCulture), "id").Replace("+EFFECT_COUNT_CODE_OATH", "").Replace(" ", "");
        var perDuel = key.Contains("+EFFECT_COUNT_CODE_DUEL", StringComparison.Ordinal);
        key = key.Replace("+EFFECT_COUNT_CODE_DUEL", "");
        // The second and third codes are often written out: the passcode plus 1, 2, 100 or 200.
        if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var literal) && (literal - id) is 1 or 2 or 100 or 200)
            key = "id+" + (literal - id).ToString(CultureInfo.InvariantCulture);
        group = key switch { "id" => 1, "id+1" or "id+o" or "id+100" => 2, "id+2" or "id+o*2" or "id+200" => 3, _ => throw No($"its once-per-turn code is unusual ({args})") };
        return perDuel ? OncePerTurn.Duel : OncePerTurn.Hard;
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
    private static (BlockInstance When, bool UsedCondition) Trigger(HashSet<string> type, string code, string? range, string? condition, bool alsoSpecial, Functions fns, bool tributed = false)
    {
        string? body = condition is null || condition.StartsWith("aux.", StringComparison.Ordinal) ? condition : Body(condition, fns);
        // "If your opponent Special Summons a monster(s)": leaving out this card changes nothing, it is yours.
        body = body?.Replace("eg:IsExists(Card.IsSummonPlayer,1,e:GetHandler(),1-tp)", "eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)", StringComparison.Ordinal);
        // Dominus Purge: "local ex4=re:IsHasCategory(CATEGORY_DRAW) local ex5=re:IsHasCategory(CATEGORY_SEARCH) return (ex4 or ex5) and ...".
        if (code == "EVENT_CHAINING" && body is not null)
            body = Regex.Replace(body, @"^local (\w+)=re:IsHasCategory\(CATEGORY_(?:DRAW|SEARCH)\) local (\w+)=re:IsHasCategory\(CATEGORY_(?:DRAW|SEARCH)\) return \(\1 or \2\)",
                "return re:IsHasCategory(CATEGORY_SEARCH)");
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
                case "EVENT_SUMMON_SUCCESS": return (Block("when.summoned").With("how", tributed ? "normal_special_tributed" : alsoSpecial ? "normal_or_special" : "normal").With("optional", optional), false);
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
                        // A Pendulum Monster's "If this card in the Monster Zone / Pendulum Zone is destroyed" (ScriptTranslator.SummonByEffect.cs).
                        _ when PendulumDestroyed(body) is { } pendulumBy => pendulumBy,
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
                    // Any other check ("if this card battled this turn") becomes IF blocks, read like any condition.
                    _ => null,
                };
                return (Block("when.phase").With("phase", phase).With("whose", whose ?? "each").With("zone", Zone(range)).With("optional", optional), body is not null && whose is not null);
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
                ("EVENT_CHAIN_NEGATED", null) => "activation_negated",
                ("EVENT_DRAW", "return ep==tp and r==REASON_RULE" or "return ep==tp and bit.band(r,REASON_RULE)~=0" or "return ep==tp and (r&REASON_RULE)>0") => "you_normal_draw",
                ("EVENT_DRAW", "return ep~=tp and r==REASON_RULE" or "return ep==1-tp and r==REASON_RULE" or "return ep==1-tp and bit.band(r,REASON_RULE)~=0") => "opp_normal_draw",
                ("EVENT_CHAINING", "return rp==tp and re:IsActiveType(TYPE_MONSTER)") => "you_monster_effect",
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
        bool opp = false, monster = false, activate = false, spell = false, trap = false, onField = false;
        string? category = null;
        foreach (var part in SplitTopLevelAnd(body[7..]).Select(p => p.Trim()))
        {
            switch (part)
            {
                case "re:IsHasCategory(CATEGORY_SPECIAL_SUMMON)": category = "chain_summons"; break;
                case "re:IsHasCategory(CATEGORY_SEARCH)" or "re:IsHasCategory(CATEGORY_SEARCH+CATEGORY_TOHAND)" or "(re:IsHasCategory(CATEGORY_SEARCH) or re:IsHasCategory(CATEGORY_DRAW))": category = "chain_searches"; break;
                case "re:GetActivateLocation()&LOCATION_ONFIELD~=0" or "re:GetActivateLocation()&LOCATION_ONFIELD>0" or "re:GetActivateLocation()==LOCATION_MZONE": onField = true; break;
                case "re:IsHasCategory(CATEGORY_DESTROY)": category = "chain_destroys"; break;
                case "rp==1-tp" or "rp~=tp" or "ep==1-tp" or "ep~=tp": opp = true; break;
                case "re:IsActiveType(TYPE_MONSTER)": monster = true; break;
                case "re:IsHasType(EFFECT_TYPE_ACTIVATE)": activate = true; break;
                case "re:IsActiveType(TYPE_SPELL)": spell = true; break;
                case "re:IsActiveType(TYPE_TRAP)": trap = true; break;
                default: return null;
            }
        }
        if (category is not null) return opp || monster || activate || spell || trap || onField ? null : category;
        if (onField) return opp && monster && !activate && !spell && !trap ? "chain_monster_field" : null;
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
        // "destroyed by battle or an opponent's card effect" / "by an opponent's card effect".
        bool battleOrOpponent = false, byOpponent = false;
        var opp = Regex.Match(text, @" and \(c:IsReason\(REASON_BATTLE\) or (?:\()?c:IsReason\(REASON_EFFECT\) and c:GetReasonPlayer\(\)==1-tp(?:\))?\)");
        if (opp.Success) { battleOrOpponent = true; text = text.Remove(opp.Index, opp.Length); }
        opp = Regex.Match(text, @" and c:IsReason\(REASON_EFFECT\) and c:GetReasonPlayer\(\)==1-tp\b");
        if (opp.Success) { byOpponent = true; text = text.Remove(opp.Index, opp.Length); }
        var r = Regex.Match(text, @"^return (.+)$");
        if (!r.Success || text.Contains(" or ", StringComparison.Ordinal)) throw No("it picks cards in a way the blocks can't");

        // "This card isn't one of them" is not "another card is one of them": the detailed block says it exactly.
        if (other) return null;
        var f = new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = other };
        string? summonType = null, ownerCheck = null;
        bool battle = false, battleOrEffect = false, wasOnField = false, anyCard = false, monster = false, faceUpNow = false, previousState = false;
        foreach (var raw in r.Groups[1].Value.Split(" and "))
        {
            var term = raw.Trim();
            Match t;
            if ((t = Regex.Match(term, @"^c:(IsControler|IsSummonPlayer|IsPreviousControler)\((tp|1-tp)\)$")).Success)
            {
                f.Owner = t.Groups[2].Value == "tp" ? CardOwner.You : CardOwner.Opponent;
                ownerCheck = t.Groups[1].Value;
                continue;
            }
            if (term == "c:IsFaceup()") faceUpNow = true;
            if (term.Contains("Previous", StringComparison.Ordinal) && term.Contains("OnField", StringComparison.Ordinal) || term.StartsWith("c:IsPreviousSetCard(", StringComparison.Ordinal)) previousState = true;
            switch (term)
            {
                case "c:IsPreviousLocation(LOCATION_MZONE)": wasOnField = true; continue;
                // "another card(s) you control": any card that was on the field.
                case "c:IsPreviousLocation(LOCATION_ONFIELD)": wasOnField = true; anyCard = true; continue;
                case "c:IsLocation(LOCATION_MZONE)" or "c:IsType(TYPE_MONSTER)": monster = true; continue;
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
        if (anyCard && !monster && f.CardType == FilterCardType.Monster && code is "EVENT_DESTROYED" or "EVENT_LEAVE_FIELD")
        {
            f.CardType = FilterCardType.Any;
            f.Zones = Model.Zone.Field;
        }
        else
        {
            f.CardType = FilterCardType.Monster;
            f.Zones = Model.Zone.MonsterZone;
        }
        if ((battleOrOpponent || byOpponent) && (code != "EVENT_DESTROYED" || battle || battleOrEffect || summonType is not null)) throw No("its \"destroyed\" condition is unusual");

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
                ev = battleOrOpponent ? "destroyed_battle_opp" : byOpponent ? "destroyed_opp" : code == "EVENT_BATTLE_DESTROYED" || battle ? "destroyed_battle" : battleOrEffect ? "destroyed_any" : "destroyed";
                break;
        }
        // The block checks who controls them now for Summons and the GY, and before for the rest, and that the rest were on
        // the field and face-up before: a script that checks anything else is read by the detailed block instead.
        var summonOrGy = code is "EVENT_SUMMON_SUCCESS" or "EVENT_SPSUMMON_SUCCESS" or "EVENT_TO_GRAVE";
        if (ownerCheck is not null && ownerCheck != (summonOrGy ? "IsControler" : "IsPreviousControler")) return null;
        if (!summonOrGy && (!wasOnField || faceUpNow || previousState)) return null;
        return Block(CardsEventTrigger.BlockId).With("cards", f).With("event", ev);
    }

    /// <summary>A Special Summon procedure: where from, its conditions (the game's check for a free zone aside) and its costs.</summary>
    private static EffectEvent Procedure(EffectEvent effect, Dictionary<string, string> set, string? range, Functions fns, CardData card)
    {
        if (!card.IsMonster || card.IsExtraDeck) throw No("its own Special Summon procedure is for an Extra Deck monster");
        // "to your center Main Monster Zone": a value of 0,0x4.
        var toCenter = false;
        // "Special Summoned this way": Card Crafter's procedures carry SUMMON_VALUE_SELF too.
        if (set.GetValueOrDefault("Value") is { } pval && pval != "SUMMON_VALUE_SELF")
        {
            if (Body(pval, fns) != "return 0,0x4") throw No("its Summon procedure picks cards in a way the blocks don't");
            toCenter = true;
        }
        // Modern procedures pick what they pay in their target step: "local g=Duel.GetMatchingGroup(...)" then
        // "g:SelectUnselect(...)", kept with e:SetLabelObject for the operation.
        (CardFilter Filter, int Min, int Max)? picks = null;
        string? pickFilter = null;
        if (set.GetValueOrDefault("Target") is { } ptg)
        {
            var (_, tbody) = fns.Get(ptg);
            var text = string.Join(" ", tbody.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Duel.Hint(", StringComparison.Ordinal)));
            var pm = Regex.Match(text, @"^local g=Duel\.GetMatchingGroup\(([\w.]+),tp,([\w+]+),0,(?:c|e:GetHandler\(\)|nil)(?:,[\w.,]+)?\) local tc=g:SelectUnselect\(nil,tp,false,true,1,1\) if tc then e:SetLabelObject\(tc\) return true else return false end$");
            if (!pm.Success) throw No("its Summon procedure picks cards in a way the blocks don't");
            var pf = Filter(pm.Groups[1].Value, fns);
            Locate(pf, pm.Groups[2].Value, "0");
            pf.ExcludeThisCard = true;
            picks = (pf, 1, 1);
            pickFilter = pm.Groups[1].Value;
        }
        var zone = range switch
        {
            "LOCATION_HAND" => "hand",
            "LOCATION_GRAVE" => "graveyard",
            "LOCATION_REMOVED" => "banished",
            _ => throw No($"its Summon procedure works from {range ?? "nowhere"}"),
        };
        effect.When = Block("when.summon_procedure").With("zone", zone).With("to", toCenter ? "center" : "any");
        // "You can Special Summon this card (from your hand) in Attack Position." (Jester Confit).
        if (set.GetValueOrDefault("Property") is { } pprop && pprop.Contains("EFFECT_FLAG_SPSUM_PARAM", StringComparison.Ordinal))
        {
            var position = (set.GetValueOrDefault("TargetRange") ?? "").Replace(" ", "") switch
            {
                "POS_FACEUP_ATTACK,0" => "attack",
                "POS_FACEUP_DEFENSE,0" => "defense",
                _ => throw No("its Summon procedure Summons in a way the blocks don't"),
            };
            effect.When = effect.When.With("position", position);
        }
        if (set.GetValueOrDefault("Condition") is { } cond)
        {
            var body = Body(cond, fns).Replace("if c==nil then return true end", "").Replace("local tp=c:GetControler()", "")
                .Replace("c:GetControler()", "tp").Trim();
            if (!body.StartsWith("return ", StringComparison.Ordinal)) throw No($"its Summon procedure's condition isn't one the blocks have ({Short(body)})");
            foreach (var part in SplitTopLevelAnd(body["return ".Length..]))
            {
                if (part is "Duel.GetLocationCount(tp,LOCATION_MZONE)>0" or "Duel.GetMZoneCount(tp)>0") continue;
                if (toCenter && part == "Duel.GetLocationCount(tp,LOCATION_MZONE,tp,LOCATION_REASON_TOFIELD,0x4)>0") continue;
                // The check that the cards it pays with exist: the cost block checks that itself.
                if (pickFilter is not null && part.StartsWith($"Duel.IsExistingMatchingCard({pickFilter},", StringComparison.Ordinal)) continue;
                effect.If.AddRange(ConditionBody("return " + part, fns));
            }
        }
        if (set.GetValueOrDefault("Operation") is { } op) effect.Cost.AddRange(Costs(op, fns, picks));
        else if (picks is not null) throw No("its Summon procedure picks cards and does nothing with them");
        return effect;
    }

    /// <summary>
    /// What an Xyz Monster must be to gain this card's effect while this card is its material: the condition on the
    /// effect's handler ("e:GetHandler():GetOriginalRace()==RACE_MACHINE", "c:IsSetCard(0xba)") as a card picker.
    /// </summary>
    private static CardFilter XyzGainer(string condition, Functions fns)
    {
        var body = Body(condition, fns).Replace("e:GetHandler():", "c:", StringComparison.Ordinal);
        if (!body.StartsWith("return ", StringComparison.Ordinal)) throw No("its Xyz Monster gains the effect under a condition the blocks don't have");
        var f = new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz };
        foreach (var part in SplitTopLevelAnd(body["return ".Length..]))
        {
            // A single original Type or Attribute, compared as the card is printed.
            var t = Regex.Replace(part, @"^c:GetOriginalRace\(\)==(RACE_\w+)$", "c:IsOriginalRace($1)");
            t = Regex.Replace(t, @"^c:GetOriginalAttribute\(\)==(ATTRIBUTE_\w+)$", "c:IsOriginalAttribute($1)");
            if (t == "c:IsType(TYPE_XYZ)") continue;
            if (!t.StartsWith("c:", StringComparison.Ordinal) || t.Contains("tp", StringComparison.Ordinal) || t.Contains("Duel.", StringComparison.Ordinal))
                throw No($"its Xyz Monster gains the effect under a condition the blocks don't have ({Short(part)})");
            Term(t, f);
        }
        f.MonsterKind = MonsterKind.Xyz;
        return f;
    }

    private static List<BlockInstance> Conditions(string condition, Functions fns)
    {
        if (MechanicsCondition(condition) is { } gemini) return gemini;
        if (condition == "aux.exccon") return [Block("if.not_sent_this_turn")];
        // Dragoon: "up to the number of Normal Monsters used as material", read by UsesPerMaterials.
        if (Regex.Match(Body(condition, fns), @"^return aux\.CardCrafterUsesPerMaterials\(""(\w+)""\)$") is { Success: true } upm)
            return [Block(UsesPerMaterialsCondition.BlockId).With("kind", upm.Groups[1].Value)];
        var body = Body(condition, fns);
        // "(you control no monsters or none of them is not X)" is the same check as "none of them is not X".
        body = Regex.Replace(body, @"\(Duel\.GetFieldGroupCount\(tp,LOCATION_MZONE,0\)==0 or (not Duel\.IsExistingMatchingCard\([\w.]+,tp,LOCATION_MZONE,0,1,nil\))\)", "$1");
        if (body.StartsWith("return ", StringComparison.Ordinal) && !body.Contains(" or ", StringComparison.Ordinal) && !body.Contains('(' + "Duel.GetCurrentPhase", StringComparison.Ordinal))
        {
            // "A and B": each part on its own IF block, when every part is one the blocks have.
            var parts = SplitTopLevelAnd(body["return ".Length..]);
            if (parts.Count > 1)
                return MergeAllAre(parts.SelectMany(p => ConditionBody("return " + p, fns)).ToList());
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

    /// <summary>
    /// "not Duel.IsExistingMatchingCard(s.cfilter,tp,LOCATION_MZONE,0,1,nil)" with cfilter "c:IsFacedown() or not X":
    /// "you control no monsters, or all monsters you control are X". Null for any other check.
    /// </summary>
    private static BlockInstance? AllAreCheck(string term, Functions fns)
    {
        var m = Regex.Match(term, @"^not Duel\.IsExistingMatchingCard\(([\w.]+),tp,([\w+]+),([\w+]+),1,(nil|e:GetHandler\(\)|c)\)$");
        if (!m.Success) return null;
        string text;
        try { text = string.Join(" ", fns.Get(m.Groups[1].Value).Body.Select(l => l.Trim())); } catch (Untranslatable) { return null; }
        var k = Regex.Match(text, @"^return (?:c:IsFacedown\(\) or not (.+)|not (.+) or c:IsFacedown\(\))$");
        if (!k.Success) return null;
        var inner = (k.Groups[1].Success ? k.Groups[1].Value : k.Groups[2].Value).Trim();
        if (inner.StartsWith('(') && inner.EndsWith(')') && Balanced(inner[1..^1])) inner = inner[1..^1];
        if (inner.Contains(" or ", StringComparison.Ordinal) || inner.Contains("not ", StringComparison.Ordinal)) return null;
        var kind = new CardFilter { CardType = FilterCardType.Monster };
        foreach (var t in SplitTopLevelAnd(inner)) Term(t.Trim(), kind);
        kind.Zones = Model.Zone.None;
        var group = new CardFilter { CardType = FilterCardType.Monster };
        Locate(group, m.Groups[2].Value, m.Groups[3].Value);
        if ((group.Zones & ~Model.Zone.MonsterZone) != Model.Zone.None) return null;
        group.ExcludeThisCard = m.Groups[4].Value != "nil";
        return Block(AllAreCondition.BlockId).With("mode", "none_or_all").With("cards", group).With("kind", kind);
    }

    /// <summary>"you control a monster" and "no monsters, or all are X" together: "all monsters you control are X".</summary>
    private static List<BlockInstance> MergeAllAre(List<BlockInstance> blocks)
    {
        var all = blocks.FirstOrDefault(b => b.BlockId == AllAreCondition.BlockId && b.Values.GetValueOrDefault("mode") == "none_or_all");
        if (all is null) return blocks;
        var group = all.Filters["cards"];
        var some = blocks.FirstOrDefault(b => b.BlockId == "if.cards_exist" && b.Values.GetValueOrDefault("count", "1") == "1"
            && b.Values.GetValueOrDefault("cmp", "at_least") == "at_least" && b.Filters.TryGetValue("cards", out var f)
            && f.Owner == group.Owner && f.Zones == group.Zones && f.ExcludeThisCard == group.ExcludeThisCard
            && FilterCompiler.Conditions(f).Where(x => x != "c:IsType(TYPE_MONSTER)").Count() == 0);
        if (some is null) return blocks;
        all.Values["mode"] = "all";
        return blocks.Where(b => b != some).ToList();
    }

    private static List<BlockInstance> ConditionBody(string body, Functions fns)
    {
        if (MechanicsConditionBody(body) is { } geminiBody) return geminiBody;
        if (LastingCondition(body) is { } battling) return battling;
        Match m;
        if (body.StartsWith("return ", StringComparison.Ordinal) && AllAreCheck(body[7..], fns) is { } allAre) return [allAre];
        if (CounterCondition(body, fns) is { } counterIf) return [counterIf];
        // "If you control more monsters than your opponent does" and other counts compared.
        if ((m = Regex.Match(body, @"^return Duel\.GetFieldGroupCount\(tp,([\w+]+),([\w+]+)\)(<=|>=|==|<|>)Duel\.GetFieldGroupCount\(tp,([\w+]+),([\w+]+)\)$")).Success)
        {
            var a = new CardFilter { CardType = FilterCardType.Any };
            var b = new CardFilter { CardType = FilterCardType.Any };
            Locate(a, m.Groups[1].Value, m.Groups[2].Value);
            Locate(b, m.Groups[4].Value, m.Groups[5].Value);
            foreach (var f in new[] { a, b }) if (f.Zones == Model.Zone.MonsterZone) f.CardType = FilterCardType.Monster;
            var cmp = m.Groups[3].Value switch { ">" => "more", "<" => "fewer", "==" => "same", ">=" => "at_least", _ => "at_most" };
            return [Block("if.compare_counts").With("cmp", cmp).With("cards", a).With("other", b)];
        }
        // "While "Umi" is on the field": the face-up Field Spell.
        if ((m = Regex.Match(body, @"^return (not )?Duel\.IsEnvironment\((\d+)(?:,PLAYER_ALL(?:,LOCATION_FZONE)?)?\)$")).Success)
            return [Block("if.card_present").With("card", new CardFilter { CardCode = Int64(m.Groups[2].Value), Owner = CardOwner.Either, Zones = Model.Zone.SpellTrapZone, CardType = FilterCardType.Any, FaceUpOnly = true })
                .With("where", "field_zone").With("is", m.Groups[1].Success ? "not" : "is")];
        if ((m = Regex.Match(body, @"^return (not )?Duel\.IsExistingMatchingCard\(([\w.]+),tp,([\w+]+),([\w+]+),(\d+),(nil|e:GetHandler\(\))\)$")).Success)
        {
            var f = Filter(m.Groups[2].Value, fns);
            Locate(f, m.Groups[3].Value, m.Groups[4].Value);
            f.ExcludeThisCard = m.Groups[6].Value != "nil";
            var n = Int(m.Groups[5].Value);
            // "If "Dark Magician" is on the field" and "while you control "Blue-Eyes White Dragon"".
            if (n == 1 && CardPresentCondition.NameOnly(f) && CardPresentCondition.WhereOf(f) is { } where)
                return [Block("if.card_present").With("card", f).With("where", where).With("is", m.Groups[1].Success ? "not" : "is")];
            if (m.Groups[1].Success) return n == 1 ? [Block("if.no_cards").With("cards", f)] : throw No("its condition counts cards in a way the blocks don't");
            return [Block("if.cards_exist").With("count", n).With("cards", f)];
        }
        if ((m = Regex.Match(body, @"^return Duel\.GetFieldGroupCount\(tp,(LOCATION_MZONE|0),(LOCATION_MZONE|0)\)(==0|>0)$")).Success && (m.Groups[1].Value == "0") != (m.Groups[2].Value == "0"))
        {
            var f = new CardFilter { CardType = FilterCardType.Monster, Zones = Model.Zone.MonsterZone, Owner = m.Groups[1].Value == "0" ? CardOwner.Opponent : CardOwner.You };
            return [m.Groups[3].Value == "==0" ? Block("if.no_cards").With("cards", f) : Block("if.cards_exist").With("count", 1).With("cards", f)];
        }
        // "If you control exactly 1 monster", "if you control 2 or fewer monsters", "3 or more cards in your hand".
        if ((m = Regex.Match(body, @"^return Duel\.GetFieldGroupCount\(tp,(LOCATION_MZONE|LOCATION_ONFIELD|LOCATION_GRAVE|LOCATION_REMOVED|LOCATION_SZONE|0),(LOCATION_MZONE|LOCATION_ONFIELD|LOCATION_GRAVE|LOCATION_REMOVED|LOCATION_SZONE|0)\)(==|<=|<|>=|>)(\d+)$")).Success
            && (m.Groups[1].Value == "0") != (m.Groups[2].Value == "0") && CountBlock(m.Groups[3].Value, Int(m.Groups[4].Value)) is { } counted)
        {
            var f = new CardFilter { CardType = FilterCardType.Any };
            Locate(f, m.Groups[1].Value, m.Groups[2].Value);
            if (f.Zones == Model.Zone.MonsterZone) f.CardType = FilterCardType.Monster;
            return [counted.With("cards", f)];
        }
        // "If you have no cards in your GY" and "if there are 3 or more cards in your opponent's GY".
        if ((m = Regex.Match(body, @"^return Duel\.GetFieldGroupCount\(tp,(LOCATION_GRAVE|LOCATION_REMOVED|LOCATION_ONFIELD|0),(LOCATION_GRAVE|LOCATION_REMOVED|LOCATION_ONFIELD|0)\)(==0|>0|~=0|>=(\d+))$")).Success
            && (m.Groups[1].Value == "0") != (m.Groups[2].Value == "0"))
        {
            var f = new CardFilter { CardType = FilterCardType.Any };
            Locate(f, m.Groups[1].Value, m.Groups[2].Value);
            return [m.Groups[3].Value == "==0" ? Block("if.no_cards").With("cards", f)
                : Block("if.cards_exist").With("count", m.Groups[4].Success ? Int(m.Groups[4].Value) : 1).With("cards", f)];
        }
        if ((m = Regex.Match(body, @"^return Duel\.GetFieldGroupCount\((tp|1-tp),LOCATION_HAND,0\)(==|<=|>=)(\d+)$")).Success)
            return [Block("if.hand_count").With("who", m.Groups[1].Value == "tp" ? "you" : "opponent")
                .With("cmp", m.Groups[2].Value switch { "==" => "eq", ">=" => "ge", _ => "le" }).With("count", Int(m.Groups[3].Value))];
        if (body.StartsWith("return ", StringComparison.Ordinal) && ExistsCheck(body[7..], fns) is { } exists) return [exists];
        // Dominus Spark: "During the turn in which your opponent has activated a monster effect in the hand or GY".
        if ((m = Regex.Match(body, @"^return Duel\.GetCustomActivityCount\((?:id|\d+),1-tp,ACTIVITY_CHAIN\)(?:>0|~=0)$")).Success && ChainCounterKind() is { } kind)
            return [Block("if.opponent_activated").With("what", kind.What).With("during", kind.During)];
        // "If this card was Normal or Special Summoned this turn", "except during the turn this card was Special Summoned".
        if ((m = Regex.Match(body, @"^return (not )?e:GetHandler\(\):IsStatus\((STATUS_[A-Z_]+(?:[+|]STATUS_[A-Z_]+)*)\)$")).Success && SummonedTurn(m.Groups[2].Value) is { } how)
            return [Block("if.summoned_this_turn").With("how", how).With("was", m.Groups[1].Success ? "not" : "was")];
        // "If this card destroyed a monster by battle", "if this card was activated this turn": a flag the script keeps.
        if (FlagConditionBlock(body) is { } flagged) return [flagged];
        return ConditionMatch(body);
    }

    /// <summary>Which Summons "this card was Summoned this turn" names, from the statuses checked; null for other statuses.</summary>
    private static string? SummonedTurn(string statuses) => string.Join("+", statuses.Split('+', '|').Order(StringComparer.Ordinal)) switch
    {
        "STATUS_SUMMON_TURN" => "normal",
        "STATUS_SPSUMMON_TURN" => "special",
        "STATUS_FLIP_SUMMON_TURN" => "flip",
        "STATUS_SPSUMMON_TURN+STATUS_SUMMON_TURN" => "normal_special",
        "STATUS_FLIP_SUMMON_TURN+STATUS_SPSUMMON_TURN+STATUS_SUMMON_TURN" => "any",
        _ => null,
    };

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
        "return e:GetHandler():GetBattledGroupCount()>0" or "return c:GetBattledGroupCount()>0" => [Block("if.battled_this_turn")],
        "return Duel.GetCurrentPhase()==PHASE_MAIN1" => [Block("if.phase").With("phase", "main1")],
        "return Duel.GetCurrentPhase()==PHASE_MAIN2" => [Block("if.phase").With("phase", "main2")],
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
        "return Duel.GetCurrentPhase()==PHASE_DRAW" => [Block("if.phase").With("phase", "draw")],
        "return Duel.GetCurrentPhase()~=PHASE_DRAW" or "return not Duel.IsPhase(PHASE_DRAW)" => [Block("if.phase").With("phase", "draw").With("is", "not")],
        // "and this card is already face-up in your Spell & Trap Zone": it wasn't just activated.
        "return e:GetHandler():IsStatus(STATUS_EFFECT_ENABLED)" => [Block(AlreadyFaceUpCondition.BlockId)],
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
        ["return e:GetHandler():GetSummonType()==SUMMON_TYPE_SPECIAL+SUMMON_VALUE_SELF"] = "self",
        ["return e:GetHandler():IsSummonType(SUMMON_TYPE_SPECIAL+SUMMON_VALUE_SELF)"] = "self",
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

    private static List<BlockInstance> Costs(string reference, Functions fns, (CardFilter Filter, int Min, int Max)? preset = null)
    {
        if (reference == "aux.bfgcost") return [Block("cost.this_card").With("how", "banish")];
        if (LpMultiplesCost(reference, fns) is { } lpCost) return lpCost;
        var (_, rawBody) = fns.Get(reference);
        var (body, temps) = FoldTempEffects(rawBody);
        var blocks = new List<BlockInstance>();
        bool inCheck = false;
        (string Var, CardFilter Filter, int Min, int Max)? picked = null;
        // "the turn you activate this": the check asks that nothing was Special Summoned (or activated) yet this turn.
        var wholeTurn = rawBody.Any(l => l.Contains("Duel.GetCustomActivityCount(", StringComparison.Ordinal));
        foreach (var raw in body)
        {
            // A Summon procedure pays "to Special Summon": the same as a cost.
            var line = raw.Trim().Replace("REASON_SPSUMMON", "REASON_COST", StringComparison.Ordinal);
            Match pm;
            if (preset is { } pre && (pm = Regex.Match(line, @"^local (\w+)=e:GetLabelObject\(\)$")).Success) { picked = (pm.Groups[1].Value, pre.Filter, pre.Min, pre.Max); continue; }
            if (preset is not null && (line == "end" || line.StartsWith("Duel.ConfirmCards(", StringComparison.Ordinal)
                || Regex.IsMatch(line, @"^if \w+:IsLocation\(LOCATION_\w+\) then$"))) continue;
            // The "can it be paid" check, on one line or several.
            if (line.StartsWith("if chk==0 then return", StringComparison.Ordinal)) { inCheck = !line.EndsWith(" end", StringComparison.Ordinal); continue; }
            if (inCheck) { if (line.EndsWith(" end", StringComparison.Ordinal) || line == "end") inCheck = false; continue; }
            if (line.Length == 0 || ChkReturn.IsMatch(line) || line == "local c=e:GetHandler()" || line.StartsWith("Duel.Hint(", StringComparison.Ordinal)) continue;
            Match m;
            if (line.StartsWith("@TEMP ", StringComparison.Ordinal))
            {
                var temp = temps[Int(line[6..])];
                if (LastingCost(temp, rawBody, fns) is { } lastingCost) { blocks.Add(lastingCost); continue; }
                if (temp.Parts is [{ Holder: "player" } only] && UntilEndOfTurn(only.Set) && LockOf(only.Set, fns) is { Who: "you" } r
                    && Locks.TurnWhat.Any(w => w.Value == r.What))
                {
                    blocks.Add(Block("cost.summon_restriction").With("what", r.What).With("when", wholeTurn ? "whole_turn" : "rest").With("cards", r.Except));
                    continue;
                }
                throw No($"its cost puts an effect on the turn the blocks don't have ({string.Join("+", temp.Parts.Select(p => p.Set.GetValueOrDefault("Code")))})");
            }
            // Cards picked first, then moved: "local g=Duel.SelectMatchingCard(...)" then "Duel.Remove(g,POS_FACEUP,REASON_COST)".
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectMatchingCard\(tp,([\w.()]+),tp,([\w+]+),0,(\d+),(\d+),(nil|e:GetHandler\(\)|c)(?:,([\w,]+))?\)$")).Success)
            {
                var f = FilterWith(m.Groups[2].Value, m.Groups[7].Value, fns);
                Locate(f, m.Groups[3].Value, "0");
                picked = (m.Groups[1].Value, f, Int(m.Groups[4].Value), Int(m.Groups[5].Value));
                continue;
            }
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectReleaseGroup\(tp,([\w.()]+),(\d+),\3,(nil|e:GetHandler\(\)|c)(?:,([\w,]+))?\)$")).Success)
            {
                var f = FilterWith(m.Groups[2].Value, m.Groups[5].Value, fns);
                f.Owner = CardOwner.You;
                f.Zones = Model.Zone.MonsterZone;
                picked = (m.Groups[1].Value, f, Int(m.Groups[3].Value), -Int(m.Groups[3].Value));
                continue;
            }
            if (line.StartsWith("Duel.HintSelection(", StringComparison.Ordinal)) continue;
            // "Reveal 1 EARTH monster in your hand": picked from the hand, then shown to the opponent.
            if (picked is { } rp && line == $"Duel.ConfirmCards(1-tp,{rp.Var})" && rp.Filter.Zones == Model.Zone.Hand && rp.Min == rp.Max && rp.Max > 0)
            {
                blocks.Add(Block(RevealCost.BlockId).With("count", rp.Max).With("cards", rp.Filter));
                picked = null;
                continue;
            }
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
            if (line is "Duel.ConfirmCards(1-tp,c)" or "Duel.ConfirmCards(1-tp,e:GetHandler())") { if (!blocks.Any(b => b.BlockId == "cost.reveal_self")) blocks.Add(Block("cost.reveal_self")); }
            else if (line is "Duel.ShuffleHand(tp)") { }
            else if ((m = Regex.Match(line, @"^Duel\.PayLPCost\(tp,(\d+)\)$")).Success) blocks.Add(Block("cost.pay_lp").With("amount", Int(m.Groups[1].Value)));
            else if (line is "Duel.SendtoGrave(c,REASON_COST+REASON_DISCARD)" or "Duel.SendtoGrave(e:GetHandler(),REASON_COST+REASON_DISCARD)") blocks.Add(Block("cost.this_card").With("how", "discard"));
            else if (line is "Duel.SendtoGrave(c,REASON_COST)" or "Duel.SendtoGrave(e:GetHandler(),REASON_COST)") blocks.Add(Block("cost.this_card").With("how", "grave"));
            else if (line is "Duel.Remove(c,POS_FACEUP,REASON_COST)" or "Duel.Remove(e:GetHandler(),POS_FACEUP,REASON_COST)") blocks.Add(Block("cost.this_card").With("how", "banish"));
            else if (line is "Duel.Release(c,REASON_COST)" or "Duel.Release(e:GetHandler(),REASON_COST)") blocks.Add(Block("cost.this_card").With("how", "tribute"));
            else if (Regex.Match(line, @"^Duel\.SendtoDeck\((?:c|e:GetHandler\(\)),nil,(SEQ_DECKSHUFFLE|2|SEQ_DECKTOP|0),REASON_COST\)$") is { Success: true } sd
                     && (sd.Groups[1].Value is "SEQ_DECKSHUFFLE" or "2" || _sourceCard?.IsExtraDeck == true))
                blocks.Add(Block("cost.this_card").With("how", _sourceCard?.IsExtraDeck == true ? "extra" : "deck"));
            else if (line is "Duel.SendtoHand(c,nil,REASON_COST)" or "Duel.SendtoHand(e:GetHandler(),nil,REASON_COST)") blocks.Add(Block("cost.this_card").With("how", "hand"));
            else if ((m = Regex.Match(line, @"^e:GetHandler\(\):RemoveOverlayCard\(tp,(\d+),\1,REASON_COST\)$")).Success) blocks.Add(Block("cost.detach").With("count", Int(m.Groups[1].Value)));
            else if ((m = Regex.Match(line, @"^Duel\.DiscardHand\(tp,([\w.]+),(\d+),\2,REASON_COST\+REASON_DISCARD(?:,nil)?\)$")).Success)
            {
                var filter = Filter(m.Groups[1].Value, fns);
                filter.Zones = Model.Zone.Hand;
                blocks.Add(Block("cost.discard").With("count", Int(m.Groups[2].Value)).With("cards", filter));
            }
            else if (CounterCost(line) is { } counterCost) blocks.Add(counterCost);
            else throw No($"its cost does something the blocks don't ({Short(line)})");
        }
        if (picked is not null) throw No("its cost picks cards and does something the blocks don't with them");
        // "You can reveal this card in your hand;": the check is that it isn't revealed already.
        if (rawBody.Any(l => Regex.IsMatch(l, @"return not (?:e:GetHandler\(\)|c):IsPublic\(\)")) && !blocks.Any(b => b.BlockId == "cost.reveal_self"))
            blocks.Insert(0, Block("cost.reveal_self"));
        // A cost that only says "it can be paid": no cost at all.
        if (blocks.Count == 0 && rawBody.Select(l => l.Trim()).Where(l => l.Length > 0).All(l => l is "if chk==0 then return true end" or "return true"))
            return blocks;
        if (blocks.Count == 0) throw No("its cost is empty");
        return blocks;
    }

    private sealed class TargetInfo
    {
        public CardFilter? Filter { get; set; }
        public int Count { get; set; }
        public string? Player { get; set; }
        public int? Param { get; set; }
        /// <summary>Duel.SetTargetParam(ev): the amount of damage or LP that started the effect.</summary>
        public bool ParamEvent { get; set; }
        /// <summary>Duel.SetTargetParam(ct*300): an amount worked out at activation, and what it is.</summary>
        public bool ParamDynamic => ParamLive is not null;
        public LiveValue? ParamLive { get; set; }
        /// <summary>What "if chk==0 then return ... end" asks before the effect can be activated, with its locals in place.</summary>
        public string? Check { get; set; }
        /// <summary>The check when it is written over lines of its own, which only the value blocks read.</summary>
        public string? CheckOnItsOwnLines { get; set; }
        /// <summary>Targets picked as a group ("pick"="names"), and a second kind of target (ScriptTranslator.Selections.cs).</summary>
        public Dictionary<string, string>? Pick { get; set; }
        public (CardFilter Filter, int Count)? Second { get; set; }
        public string? FirstVar { get; set; }
        public string? SecondVar { get; set; }
        /// <summary>"up to the number of ...": the cards counted.</summary>
        public CardFilter? CountFilter { get; set; }
    }

    /// <summary>
    /// "if Duel.NegateEffect(ev) and Duel.IsExistingMatchingCard(Card.IsType,tp,LOCATION_GRAVE,0,1,nil,TYPE_TRAP) then": an "if"
    /// that mixes actions with checks of their own ("then if you have a Trap in your GY"), or lets the opponent choose.
    /// </summary>
    private static bool IsCompound(string line, List<string> body, Functions fns)
    {
        var m = Regex.Match(line, @"^if (.+) then$");
        if (!m.Success || line.EndsWith(" then return end", StringComparison.Ordinal)) return false;
        var terms = SplitTopLevelAnd(m.Groups[1].Value).Select(t => t.Trim()).ToList();
        if (terms.Count < 2) return false;
        if (terms.Any(t => t.StartsWith("Duel.SelectYesNo(1-tp,", StringComparison.Ordinal) || t is "Duel.NegateEffect(ev)" or "Duel.NegateActivation(ev)")) return true;
        // A check on cards no later step picks is a condition of its own, not the optional step's.
        return terms.Any(t => ExistsCheck(t, fns) is not null
            && !(Regex.Match(t, @"^Duel\.IsExistingMatchingCard\(([\w.()]+),tp,") is { Success: true } om
                 && body.Any(l => l.Contains($"SelectMatchingCard(tp,{om.Groups[1].Value},", StringComparison.Ordinal) || l.Contains($"SelectMatchingCard(tp,aux.NecroValleyFilter({om.Groups[1].Value}),", StringComparison.Ordinal))));
    }

    /// <summary>The lines of Polymerization's operation and target, as card scripts write them.</summary>
    private static readonly Regex[] FusionTemplate =
    [
        .. new[]
        {
            @"local chkf=tp", @"local c=e:GetHandler\(\)", @"if chk==0 then", @"end", @"else", @"elseif ce then", @"elseif ce and mg[23] then",
            @"local mg[23]=nil", @"mg[23]=fgroup\(ce,e,tp\)", @"local mg[23]=fgroup\(ce,e,tp\)", @"local sg2=nil", @"local ce=Duel\.GetChainMaterial\(tp\)",
            @"if ce~=nil then", @"if ce then", @"local fgroup=ce:GetTarget\(\)", @"local mf=ce:GetValue\(\)",
            @"sg2=Duel\.GetMatchingGroup\([\w.]+,tp,LOCATION_EXTRA,0,nil,e,tp,mg[23],mf,chkf\)",
            @"if (?:sg1:GetCount\(\)>0|#sg1>0) or \(sg2~=nil and (?:sg2:GetCount\(\)>0|#sg2>0)\) then", @"local sg=sg1:Clone\(\)", @"if sg2 then sg:Merge\(sg2\) end",
            @"Duel\.Hint\(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON\)", @"local tg=sg:Select\(tp,1,1,nil\)", @"local tc=tg:GetFirst\(\)", @"if sg1:IsContains\(tc\) and .*then",
            @"local mat1=Duel\.SelectFusionMaterial\(tp,tc,mg1,nil,chkf\)", @"tc:SetMaterial\(mat1\)", @"Duel\.BreakEffect\(\)",
            @"Duel\.SpecialSummon\(tc,SUMMON_TYPE_FUSION,tp,tp,false,false,POS_FACEUP\)", @"local mat2=Duel\.SelectFusionMaterial\(tp,tc,mg[23],nil,chkf\)",
            @"local fop=ce:GetOperation\(\)", @"fop\(ce,e,tp,tc,mat2\)", @"tc:CompleteProcedure\(\)",
            @"local res=Duel\.IsExistingMatchingCard\([\w.]+,tp,LOCATION_EXTRA,0,1,nil,e,tp,mg1,nil,chkf\)", @"if not res then",
            @"res=Duel\.IsExistingMatchingCard\([\w.]+,tp,LOCATION_EXTRA,0,1,nil,e,tp,mg[23],mf,chkf\)", @"return res", @"Duel\.SetOperationInfo\(.*\)",
            @"local sg1=Duel\.GetMatchingGroup\([\w.]+,tp,LOCATION_EXTRA,0,nil,e,tp,mg1,nil,chkf\)", @"local mg1=.*", @"local mg2=Duel\.GetMatchingGroup\(.*\)", @"mg1:Merge\(mg2\)",
            @"Duel\.(?:SendtoGrave|Remove|SendtoDeck)\(mat1,.*REASON_EFFECT\+REASON_MATERIAL\+REASON_FUSION\)",
        }.Select(p => new Regex("^" + p + "$")),
    ];

    /// <summary>
    /// Polymerization, Miracle Fusion, Fusion Destiny and the like: "Fusion Summon 1 ... from your Extra Deck, using ..."
    /// as one block. Null when the operation isn't a Fusion Summon; untranslatable when it does more than the block.
    /// </summary>
    private static BlockInstance? FusionOf(string operation, string? target, Functions fns)
    {
        var (_, opBody) = fns.Get(operation);
        var lines = JoinContinuations(opBody).Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => Regex.Replace(l, @"\b(?:c\d+|s)\.", "s.")).ToList();
        if (!lines.Any(l => l.Contains("SelectFusionMaterial(", StringComparison.Ordinal)) || !lines.Any(l => l.Contains("SUMMON_TYPE_FUSION", StringComparison.Ordinal))) return null;
        var all = new List<string>(lines);
        if (target is not null) all.AddRange(JoinContinuations(fns.Get(target).Body).Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => Regex.Replace(l, @"\b(?:c\d+|s)\.", "s.")));
        if (all.FirstOrDefault(l => !FusionTemplate.Any(r => r.IsMatch(l))) is { } odd) throw No($"its Fusion Summon does more than the block ({Short(odd)})");

        // The Fusion Monster: its filter, without the checks every Fusion Summon makes.
        var sg1 = lines.Select(l => Regex.Match(l, @"^local sg1=Duel\.GetMatchingGroup\(([\w.]+),tp,LOCATION_EXTRA,0,nil,e,tp,mg1,nil,chkf\)$")).FirstOrDefault(m => m.Success)
                  ?? throw No("its Fusion Summon picks the Fusion Monster in a way the block can't");
        var fusion = new CardFilter { Owner = CardOwner.You, Zones = Model.Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion };
        foreach (var term in FunctionTerms(sg1.Groups[1].Value, fns))
        {
            if (term is "c:IsType(TYPE_FUSION)" or "(not f or f(c))" or "c:IsCanBeSpecialSummoned(e,SUMMON_TYPE_FUSION,tp,false,false)" or "c:CheckFusionMaterial(m,nil,chkf)") continue;
            Term(term, fusion);
        }
        fusion.MonsterKind = MonsterKind.Fusion;
        fusion.Zones = Model.Zone.ExtraDeck;

        // Where the materials come from, and what happens to them.
        var mats = new CardFilter { CardType = FilterCardType.Monster };
        var field = false;
        void MatTerms(string fn)
        {
            foreach (var term in FunctionTerms(fn, fns))
            {
                if (term is "not c:IsImmuneToEffect(e)" or "c:IsCanBeFusionMaterial()" or "c:IsAbleToRemove()" or "c:IsAbleToDeck()" or "c:IsAbleToGrave()" or "c:IsType(TYPE_MONSTER)"
                    or "(not c:IsLocation(LOCATION_MZONE) or not c:IsImmuneToEffect(e))" or "(c:IsLocation(LOCATION_HAND) or not c:IsImmuneToEffect(e))") continue;
                if (term is "c:IsOnField()" or "c:IsLocation(LOCATION_MZONE)") { field = true; continue; }
                Term(term, mats);
            }
            mats.CardType = FilterCardType.Monster;
            mats.Zones = Model.Zone.None;
        }
        var mg1 = lines.First(l => l.StartsWith("local mg1=", StringComparison.Ordinal))["local mg1=".Length..];
        var send = lines.First(l => l.StartsWith("Duel.SendtoGrave(mat1,", StringComparison.Ordinal) || l.StartsWith("Duel.Remove(mat1,", StringComparison.Ordinal) || l.StartsWith("Duel.SendtoDeck(mat1,", StringComparison.Ordinal));
        var how = send.StartsWith("Duel.Remove", StringComparison.Ordinal) ? "banish" : send.StartsWith("Duel.SendtoDeck", StringComparison.Ordinal) ? "deck" : "grave";
        var deck = lines.Any(l => l == "mg1:Merge(mg2)");
        string option;
        Match m;
        if ((m = Regex.Match(mg1, @"^Duel\.GetFusionMaterial\(tp\)((?::Filter\([\w.()]+,nil(?:,e)?\))*)$")).Success && how == "grave")
        {
            foreach (Match f in Regex.Matches(m.Groups[1].Value, @":Filter\(([\w.()]+),nil(?:,e)?\)"))
            {
                var fn = f.Groups[1].Value;
                if (fn == "Card.IsOnField") field = true;
                else if (fn != "aux.NOT(Card.IsImmuneToEffect)") MatTerms(fn);
            }
            if (deck)
            {
                var mg2 = lines.First(l => l.StartsWith("local mg2=Duel.GetMatchingGroup(", StringComparison.Ordinal));
                var dm = Regex.Match(mg2, @"^local mg2=Duel\.GetMatchingGroup\(([\w.]+),tp,LOCATION_DECK,0,nil(?:,e)?\)$");
                if (!dm.Success || field) throw No("its Fusion Summon takes materials from more places than the block");
                MatTerms(dm.Groups[1].Value);
                option = "hand_field_deck";
            }
            else option = field ? "field" : "hand_field";
        }
        else if ((m = Regex.Match(mg1, @"^Duel\.GetMatchingGroup\((?:aux\.NecroValleyFilter\(([\w.]+)\)|([\w.]+)),tp,(LOCATION_GRAVE|LOCATION_MZONE\+LOCATION_GRAVE),0,nil(?:,e|,tp)?\)$")).Success && !deck)
        {
            MatTerms(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            option = (m.Groups[3].Value, how) switch
            {
                ("LOCATION_GRAVE", "banish") => "gy_banish",
                ("LOCATION_MZONE+LOCATION_GRAVE", "banish") => "field_gy_banish",
                ("LOCATION_GRAVE", "deck") => "gy_deck",
                _ => throw No("its Fusion Summon uses its materials in a way the block can't"),
            };
        }
        else throw No($"its Fusion Summon takes materials in a way the block can't ({Short(mg1)})");
        return Block(FusionSummonAction.BlockId).With("cards", fusion).With("mats", option).With("matcards", mats);
    }

    /// <summary>The "and" terms of a filter function's one return line.</summary>
    private static List<string> FunctionTerms(string reference, Functions fns)
    {
        if (reference.StartsWith("aux.NecroValleyFilter(", StringComparison.Ordinal)) reference = reference["aux.NecroValleyFilter(".Length..^1];
        // Fusion lines are read with the script's prefix written as "s.".
        if (reference.StartsWith("s.", StringComparison.Ordinal)) reference = fns.Prefix + reference[1..];
        var body = string.Join(" ", fns.Get(reference).Body.Select(l => l.Trim()).Where(l => l.Length > 0));
        var r = Regex.Match(body, @"^return (.+)$");
        if (!r.Success) throw No($"it picks cards in a way the blocks can't ({Short(body)})");
        return SplitTopLevelAnd(r.Groups[1].Value).Select(t => t.Trim()).ToList();
    }

    /// <summary>"if a\n\tand b then": one statement split over lines, as one line.</summary>
    private static List<string> JoinContinuations(List<string> lines)
    {
        var joined = new List<string>();
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (joined.Count > 0 && (t.StartsWith("and ", StringComparison.Ordinal) || t.StartsWith("or ", StringComparison.Ordinal))
                && !joined[^1].TrimEnd().EndsWith(" then", StringComparison.Ordinal) && !joined[^1].TrimEnd().EndsWith(" end", StringComparison.Ordinal))
                joined[^1] = joined[^1].TrimEnd() + " " + t;
            else joined.Add(raw);
        }
        return joined;
    }

    /// <summary>
    /// Dominus: the target step remembers whether this card was activated from the hand ("e:SetLabel(100)"), for
    /// "If you activated this card from your hand, ..." in the operation. The block checks that itself.
    /// </summary>
    private static bool FromHandLabel(string line) =>
        line is "e:SetLabel(0)" or "e:SetLabel(100)" or "if e:GetHandler():IsStatus(STATUS_ACT_FROM_HAND) then" or "if c:IsStatus(STATUS_ACT_FROM_HAND) then"
            or "if e:GetHandler():IsStatus(STATUS_ACT_FROM_HAND) then e:SetLabel(100) end";

    private static TargetInfo Targeting(string reference, Functions fns)
    {
        var info = new TargetInfo();
        _declaration = null;
        var (_, body) = fns.Get(reference);
        // Numbers worked out for the operation info or the check ("local ct=Duel.GetMatchingGroupCount(...)").
        var values = new OperationValues { Fns = fns, Target = info };
        string? check = null, block = null;
        foreach (var raw in body)
        {
            var line = raw.Trim();
            // "if chk==0 then" / "return ..." / "end" on lines of their own.
            if (block is not null)
            {
                if (line == "end")
                {
                    if (!block.StartsWith("return ", StringComparison.Ordinal) || block.Contains(" end", StringComparison.Ordinal))
                        throw No($"its target step does something the blocks don't ({Short(block)})");
                    info.Check = Substitute(block["return ".Length..], values.Locals).Trim();
                    info.CheckOnItsOwnLines = Short(block);
                    block = null;
                    continue;
                }
                block = (block + " " + line).Trim();
                continue;
            }
            if (line == "if chk==0 then") { block = ""; continue; }
            if (check is not null && !check.EndsWith(" end", StringComparison.Ordinal))
            {
                check += " " + line;
                if (check.EndsWith(" end", StringComparison.Ordinal)) info.Check = Substitute(check[..^4], values.Locals).Trim();
            }
            else if (line.StartsWith("if chk==0 then return ", StringComparison.Ordinal))
            {
                check = line["if chk==0 then return ".Length..];
                if (check.EndsWith(" end", StringComparison.Ordinal)) info.Check = Substitute(check[..^4], values.Locals).Trim();
            }
            if (RecordValueLocal(line, values)) continue;
            // Several targets: "up to 3", two kinds, a group (ScriptTranslator.Selections.cs).
            if (SelectionTargetLine(line, info, fns, values)) continue;
            if (Regex.Match(line, @"^Duel\.SetTargetParam\((.+)\)$") is { Success: true } tpm && !Regex.IsMatch(tpm.Groups[1].Value, @"^\d+$"))
            {
                var param = Squash(Substitute(tpm.Groups[1].Value, values.Locals));
                if (param == "ev") { info.ParamEvent = true; continue; }
                if (OperationValue(param, "c", values) is { } snapshot) { info.ParamLive = snapshot; continue; }
            }
            if (Regex.Match(line, @"^if (\w+)(?:>0|~=0) then$") is { Success: true } zm && values.Locals.Any(l => l.Key == zm.Groups[1].Value)) continue;
            if (line.Length == 0 || line.StartsWith("if chkc then", StringComparison.Ordinal) || line.StartsWith("if chk==0 then", StringComparison.Ordinal)
                || line.StartsWith("and ", StringComparison.Ordinal) || line == "end" || line.EndsWith(" end", StringComparison.Ordinal) && line.StartsWith("and ", StringComparison.Ordinal)
                || line.StartsWith("Duel.Hint(", StringComparison.Ordinal) || line.StartsWith("Duel.SetOperationInfo(", StringComparison.Ordinal)
                || line == "local c=e:GetHandler()" || FromHandLabel(line)
                || Regex.IsMatch(line, @"^local ft=Duel\.GetLocationCount\(tp,LOCATION_MZONE\)$")
                // A group only for the operation info ("destroy all monsters your opponent controls").
                || Regex.IsMatch(line, @"^local \w+=Duel\.GetMatchingGroup\(.*\)$") || Regex.IsMatch(line, @"^if (?:\w+:GetCount\(\)>0|#\w+>0) then$")
                // Categories that change with where the card is: the blocks set their own.
                || line is "if e:GetActivateLocation()==LOCATION_GRAVE then" or "else" || line.StartsWith("e:SetCategory(", StringComparison.Ordinal)
                || line is "if re:GetHandler():IsDestructable() and re:GetHandler():IsRelateToEffect(re) then" or "if re:GetHandler():IsRelateToEffect(re) and re:GetHandler():IsDestructable() then")
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
            else if (CounterTarget(line, info)) { }
            else if (ChainLimitLine(line, fns)) { }
            else if (DeclarationLine(line, $"{fns.Id}:{reference}")) { }
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
    /// <summary>
    /// A lock on players: "neither player can Special Summon monsters, except WIND monsters", "you cannot activate monster
    /// effects", "neither player can add cards from their Deck to their hand except by drawing them". Null when the
    /// effect isn't a lock; throws when it is one the blocks can't say.
    /// </summary>
    private static (string Who, string What, CardFilter Except)? LockOf(Dictionary<string, string> set, Functions fns)
    {
        var code = set.GetValueOrDefault("Code");
        if (code is not ("EFFECT_CANNOT_SPECIAL_SUMMON" or "EFFECT_CANNOT_ACTIVATE" or "EFFECT_CANNOT_TO_HAND" or "EFFECT_CANNOT_ATTACK_ANNOUNCE"
            or "EFFECT_CANNOT_SUMMON" or "EFFECT_CANNOT_MSET" or "EFFECT_CANNOT_FLIP_SUMMON" or "EFFECT_CANNOT_SSET" or "EFFECT_CANNOT_BP")) return null;
        var range = (set.GetValueOrDefault("TargetRange") ?? "").Replace(" ", "");
        var except = new CardFilter { CardType = FilterCardType.Monster };
        string Who(string r) => r switch
        {
            "1,1" or "LOCATION_MZONE,LOCATION_MZONE" => "both",
            "1,0" or "LOCATION_MZONE,0" => "you",
            "0,1" or "0,LOCATION_MZONE" => "opponent",
            _ => throw No($"its lock affects players in a way the blocks can't ({r})"),
        };
        // "not X" parts become the exception ("except WIND monsters"); a card's own checks use c.
        void Except(string terms, string card)
        {
            foreach (var t in SplitTopLevelAnd(terms)) Term(t.Trim().Replace(card + ":", "c:", StringComparison.Ordinal), except);
            except.CardType = FilterCardType.Monster;
            except.Zones = Model.Zone.None;
        }
        string? TargetBody() => set.GetValueOrDefault("Target") is { } t
            ? t.StartsWith("aux.TargetBoolFunction(", StringComparison.Ordinal) ? t : Body(t, fns)
            : null;
        // The monsters spared: "not X" parts of the target; where they come from is kept apart.
        List<string> Spared(string body)
        {
            if (!body.StartsWith("return ", StringComparison.Ordinal)) throw No($"its lock picks monsters in a way the blocks can't ({Short(body)})");
            var parts = SplitTopLevelAnd(body[7..]).Select(x => x.Trim()).ToList();
            var places = parts.Where(x => Regex.IsMatch(x, @"^c:IsLocation\(LOCATION_(EXTRA|GRAVE|HAND|DECK)\)$")).ToList();
            foreach (var part in parts.Except(places))
            {
                Match m;
                if ((m = Regex.Match(part, @"^not \((.*)\)$")).Success && Balanced(m.Groups[1].Value)) Except(m.Groups[1].Value, "c");
                else if ((m = Regex.Match(part, @"^not (c:\w+\([^()]*\))$")).Success) Except(m.Groups[1].Value, "c");
                else if ((m = Regex.Match(part, @"^c:Get(Attribute|Race)\(\)~=(\w+)$")).Success) Except($"c:Is{m.Groups[1].Value}({m.Groups[2].Value})", "c");
                else throw No($"its lock picks monsters in a way the blocks can't ({Short(part)})");
            }
            return places;
        }
        switch (code)
        {
            case "EFFECT_CANNOT_SPECIAL_SUMMON":
            {
                var who = Who(range);
                var body = TargetBody();
                if (body is null) return (who, "special", except);
                if (body == "aux.TargetBoolFunction(Card.IsLocation,LOCATION_EXTRA)") return (who, "special_extra", except);
                if (PendulumLock(body, set) is { } pendulumExcept) return (who, "pendulum", pendulumExcept);
                var places = Spared(body);
                return (who, places switch
                {
                    [] => "special",
                    ["c:IsLocation(LOCATION_EXTRA)"] => "special_extra",
                    ["c:IsLocation(LOCATION_GRAVE)"] => "special_gy",
                    ["c:IsLocation(LOCATION_HAND)"] => "special_hand",
                    ["c:IsLocation(LOCATION_DECK)"] => "special_deck",
                    _ => throw No("its lock covers several places at once"),
                }, except);
            }
            // Normal Summons, Sets and Flip Summons: parts of "you cannot Normal or Special Summon"; CombinedLock joins them.
            case "EFFECT_CANNOT_SUMMON" or "EFFECT_CANNOT_MSET" or "EFFECT_CANNOT_FLIP_SUMMON" or "EFFECT_CANNOT_SSET":
            {
                var who = Who(range);
                if (TargetBody() is { } body && Spared(body).Count > 0) throw No("its lock on Normal Summons looks at where monsters are");
                return (who, code switch { "EFFECT_CANNOT_SUMMON" => "x_summon", "EFFECT_CANNOT_MSET" => "x_mset", "EFFECT_CANNOT_FLIP_SUMMON" => "x_flip", _ => "sset" }, except);
            }
            case "EFFECT_CANNOT_BP":
                if (set.ContainsKey("Target")) throw No("its Battle Phase lock is unusual");
                return (Who(range), "bp", except);
            case "EFFECT_CANNOT_TO_HAND":
            {
                var body = TargetBody();
                if (body is not ("aux.TargetBoolFunction(Card.IsLocation,LOCATION_DECK)" or "return c:IsLocation(LOCATION_DECK)"))
                    throw No("its lock on adding cards to the hand is unusual");
                return (Who(range), "search", except);
            }
            case "EFFECT_CANNOT_ATTACK_ANNOUNCE":
            {
                var body = TargetBody();
                if (body is not null)
                {
                    var m = Regex.Match(body, @"^return not (.+)$");
                    if (!m.Success) throw No($"its attack lock picks monsters in a way the blocks can't ({Short(body)})");
                    Except(m.Groups[1].Value.Trim('(', ')'), "c");
                }
                return (Who(range), "attack", except);
            }
            default:
            {
                var who = Who(range);
                var value = set.GetValueOrDefault("Value") ?? throw No("its lock on activations has no value");
                if (value is "1" or "aux.TRUE") return (who, "activate_any", except);
                var body = Body(value, fns);
                // "local c=re:GetHandler() return c:IsAttribute(...) and ...": the activated card, under its own name.
                if (body.StartsWith("local c=re:GetHandler() ", StringComparison.Ordinal))
                    body = Regex.Replace(body["local c=re:GetHandler() ".Length..], @"\bc:", "re:GetHandler():");
                body = body
                    .Replace("local rc=re:GetHandler() ", "").Replace("rc:", "re:GetHandler():")
                    .Replace("local loc=re:GetActivateLocation() ", "").Replace("loc==", "re:GetActivateLocation()==");
                if (ActivationLock(body) is { } cardsLock) return (who, cardsLock, except);
                if (!body.StartsWith("return ", StringComparison.Ordinal)) throw No($"its lock on activations is unusual ({Short(body)})");
                string? type = null;
                bool onField = false, hand = false, gy = false, mzone = false, offField = false, named = false, handGy = false;
                foreach (var part in SplitTopLevelAnd(body[7..]).Select(x => x.Trim()))
                {
                    Match m;
                    if ((m = Regex.Match(part, @"^re:IsActiveType\((TYPE_MONSTER|TYPE_SPELL|TYPE_TRAP|TYPE_SPELL\+TYPE_TRAP)\)$")).Success) type = m.Groups[1].Value;
                    else if (part is "(re:GetHandler():IsOnField() or re:IsHasType(EFFECT_TYPE_ACTIVATE))" or "re:IsHasType(EFFECT_TYPE_ACTIVATE)") onField = true;
                    else if (part is "re:GetHandler():IsLocation(LOCATION_HAND)" or "re:GetActivateLocation()==LOCATION_HAND") hand = true;
                    else if (part is "re:GetActivateLocation()==LOCATION_MZONE" or "re:GetHandler():IsLocation(LOCATION_MZONE)") mzone = true;
                    else if (part is "(re:GetActivateLocation()==LOCATION_GRAVE or re:GetActivateLocation()==LOCATION_REMOVED)") gy = true;
                    else if ((m = Regex.Match(part, @"^not re:GetHandler\(\):(\w+\([^()]*\))$")).Success) Except("c:" + m.Groups[1].Value, "c");
                    // Dominus: "you cannot activate the effects of LIGHT, EARTH and WIND monsters".
                    else if ((m = Regex.Match(part, @"^re:GetHandler\(\):(Is(?:Attribute|Race|SetCard|Code|Level)\([^()]*\))$")).Success) { Except("c:" + m.Groups[1].Value, "c"); named = true; }
                    // Songs of the Dominators: "the effects of monsters in your hand, GY, and banishment".
                    else if (Regex.IsMatch(part, @"^re:GetActivateLocation\(\)&\(?LOCATION_HAND[|+]LOCATION_GRAVE[|+]LOCATION_REMOVED\)?~=0$")) offField = true;
                    else if (IsHandOrGyLocation(part)) handGy = true;
                    else throw No($"its lock on activations is unusual ({Short(part)})");
                }
                if (named)
                {
                    if (type != "TYPE_MONSTER" || hand || mzone || gy || onField || offField) throw No($"its lock on activations is unusual ({Short(body)})");
                    return (who, "activate_monster_of", except);
                }
                if (handGy)
                {
                    if (type != "TYPE_MONSTER" || hand || mzone || gy || onField || offField) throw No($"its lock on activations is unusual ({Short(body)})");
                    return (who, "activate_monster_hand_gy", except);
                }
                if (offField)
                {
                    if (type != "TYPE_MONSTER" || hand || mzone || gy || onField) throw No($"its lock on activations is unusual ({Short(body)})");
                    return (who, "activate_monster_offfield", except);
                }
                var what = (type, hand, mzone, gy, onField) switch
                {
                    ("TYPE_MONSTER", false, false, false, false) => "activate_monster",
                    ("TYPE_MONSTER", true, false, false, false) => "activate_monster_hand",
                    ("TYPE_MONSTER", false, true, false, false) => "activate_monster_field",
                    ("TYPE_MONSTER", false, false, true, false) => "activate_monster_gy",
                    ("TYPE_SPELL", false, false, false, _) => "activate_spell",
                    ("TYPE_TRAP", false, false, false, _) => "activate_trap",
                    ("TYPE_SPELL+TYPE_TRAP", false, false, false, _) => "activate_spelltrap",
                    _ => throw No($"its lock on activations is unusual ({Short(body)})"),
                };
                return (who, what, except);
            }
        }
    }

    /// <summary>
    /// The lock several player effects make together: "Normal or Special Summon" is EFFECT_CANNOT_SUMMON plus
    /// EFFECT_CANNOT_SPECIAL_SUMMON. Null when they aren't one of the locks the blocks have.
    /// </summary>
    private static string? CombinedLock(IEnumerable<string> whats) =>
        string.Join("+", whats.OrderBy(w => w, StringComparer.Ordinal)) switch
        {
            "special+x_summon" => "normal_special",
            "special+x_flip+x_summon" => "summon_any",
            "x_mset+x_summon" => "normal",
            var one when !one.Contains('+') && !one.StartsWith("x_", StringComparison.Ordinal) => one,
            _ => null,
        };

    /// <summary>"Tribute this card instead": the cards it protects, against what, and what it does with itself.</summary>
    private static BlockInstance ReplaceOf(Dictionary<string, string> set, Functions fns)
    {
        var value = set.GetValueOrDefault("Value") ?? throw No("its replacement protects cards the blocks can't follow");
        var vm = Regex.Match(Body(value, fns), @"^return ([\w.]+)\(c,(?:e:GetHandlerPlayer\(\)|tp|e,e:GetHandlerPlayer\(\))\)$");
        if (!vm.Success) throw No($"its replacement protects cards the blocks can't follow ({Short(value)})");
        var (_, fbody) = fns.Get(vm.Groups[1].Value);
        var text = string.Join(" ", fbody.Select(l => l.Trim()));
        var rm = Regex.Match(text, @"^return (.+)$");
        if (!rm.Success || text.Contains(" or ", StringComparison.Ordinal)) throw No("its replacement protects cards the blocks can't follow");
        var f = new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.Field, CardType = FilterCardType.Any };
        var by = "any";
        foreach (var raw in rm.Groups[1].Value.Split(" and "))
        {
            var term = raw.Trim();
            switch (term)
            {
                case "c:IsControler(tp)": f.Owner = CardOwner.You; continue;
                case "c:IsControler(1-tp)": f.Owner = CardOwner.Opponent; continue;
                case "c:IsLocation(LOCATION_MZONE)": f.Zones = Model.Zone.MonsterZone; f.CardType = FilterCardType.Monster; continue;
                case "c:IsLocation(LOCATION_ONFIELD)" or "c:IsOnField()": continue;
                case "c:IsReason(REASON_BATTLE+REASON_EFFECT)" or "c:IsReason(REASON_EFFECT+REASON_BATTLE)": by = "any"; continue;
                case "c:IsReason(REASON_EFFECT)": by = "effect"; continue;
                case "c:IsReason(REASON_BATTLE)": by = "battle"; continue;
                case "not c:IsReason(REASON_REPLACE)": continue;
            }
            Term(term, f);
        }
        if (f.MonsterKind != MonsterKind.Any || f.Race != CardRace.None || f.Attribute != CardAttribute.None) f.CardType = FilterCardType.Monster;
        var target = set.GetValueOrDefault("Target") is { } tg ? string.Join(" ", fns.Get(tg).Body.Select(l => l.Trim())) : "";
        f.ExcludeThisCard = Regex.IsMatch(target, @"eg:IsExists\([\w.]+,1,(?:c|e:GetHandler\(\)),");
        var op = set.GetValueOrDefault("Operation") ?? throw No("its replacement does nothing");
        var lines = fns.Get(op).Body.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Duel.Hint(", StringComparison.Ordinal) && l != "local c=e:GetHandler()").ToList();
        if (lines.Count != 1) throw No("its replacement does something the blocks don't");
        var how = Regex.Replace(lines[0].Replace("e:GetHandler()", "SELF", StringComparison.Ordinal), @"\bc\b", "SELF") switch
        {
            var l when Regex.IsMatch(l, @"^Duel\.Release\(SELF,REASON_EFFECT(\+REASON_REPLACE)?\)$") => "tribute",
            var l when Regex.IsMatch(l, @"^Duel\.Destroy\(SELF,REASON_EFFECT(\+REASON_REPLACE)?\)$") => "destroy",
            var l when Regex.IsMatch(l, @"^Duel\.Remove\(SELF,POS_FACEUP,REASON_EFFECT(\+REASON_REPLACE)?\)$") => "banish",
            var l when Regex.IsMatch(l, @"^Duel\.SendtoGrave\(SELF,REASON_EFFECT(\+REASON_REPLACE)?\)$") => "grave",
            var l when Regex.IsMatch(l, @"^SELF:RemoveOverlayCard\(tp,1,1,REASON_EFFECT(\+REASON_REPLACE)?\)$") => "detach",
            _ => throw No($"its replacement does something the blocks don't ({Short(lines[0])})"),
        };
        return Block(ReplaceDestroyPassive.BlockId).With("cards", f).With("by", by).With("how", how);
    }

    /// <summary>"Any card sent to the GY is banished instead": who and what it covers, or null for another effect.</summary>
    private static (string What, string Whose, string From)? BanishInsteadOf(Dictionary<string, string> set, Functions fns)
    {
        if (set.GetValueOrDefault("Code") != "EFFECT_TO_GRAVE_REDIRECT") return null;
        if (set.GetValueOrDefault("Value") != "LOCATION_REMOVED") throw No("it sends cards somewhere else instead of the GY");
        // Herald of the Arc Light: only cards sent from the hand or Main Deck (the range counts without EFFECT_FLAG_IGNORE_RANGE).
        const string handDeck = "LOCATION_HAND+LOCATION_DECK";
        var range = (set.GetValueOrDefault("TargetRange") ?? "").Replace(" ", "").Replace("LOCATION_DECK+LOCATION_HAND", handDeck, StringComparison.Ordinal);
        var from = range.Contains(handDeck, StringComparison.Ordinal) && !(set.GetValueOrDefault("Property") ?? "").Contains("EFFECT_FLAG_IGNORE_RANGE", StringComparison.Ordinal)
            ? "hand_deck" : "anywhere";
        var whose = range switch
        {
            "LOCATION_DECK,LOCATION_DECK" or "0xff,0xff" or handDeck + "," + handDeck => "either",
            "0,LOCATION_DECK" or "0,0xff" or "0," + handDeck => "opponent",
            "LOCATION_DECK,0" or "0xff,0" or handDeck + ",0" => "you",
            var r => throw No($"its \"banished instead\" covers cards the blocks can't ({r})"),
        };
        if (from == "anywhere" && range.Contains("LOCATION_HAND", StringComparison.Ordinal)) throw No($"its \"banished instead\" covers cards the blocks can't ({range})");
        var what = "card";
        if (set.GetValueOrDefault("Target") is { } target)
        {
            if (target == "aux.DimensionalFissureTarget") what = "monster";
            // Cards from the hand or Deck are what they are printed as: a monster there is a Monster Card.
            else if (from == "hand_deck" && Body(target, fns) == "return c:IsType(TYPE_MONSTER)") what = "monster";
            else
            {
                var body = Body(target, fns);
                if (body is "return c:GetOwner()~=e:GetHandlerPlayer()" or "return c:GetOwner()==1-e:GetHandlerPlayer()") { if (whose == "either") whose = "opponent"; }
                else if (body == "return c:GetOwner()==e:GetHandlerPlayer()") { if (whose == "either") whose = "you"; }
                else throw No($"its \"banished instead\" picks cards in a way the blocks can't ({Short(body)})");
            }
        }
        return (what, whose, from);
    }

    /// <summary>Negating Spell/Trap/monster effects on the field (Imperial Order, Royal Decree, Skill Drain), or null.</summary>
    private static string? NegateAllOf(HashSet<string> type, Dictionary<string, string> set, Functions fns)
    {
        if (!type.SetEquals(["EFFECT_TYPE_FIELD"]) || set.GetValueOrDefault("Code") != "EFFECT_DISABLE") return null;
        var range = (set.GetValueOrDefault("TargetRange") ?? "").Replace(" ", "");
        var body = set.GetValueOrDefault("Target") is { } t ? Body(t, fns) : null;
        return (range, body) switch
        {
            ("LOCATION_MZONE,LOCATION_MZONE", null or "return c:IsType(TYPE_EFFECT) or c:GetOriginalType()&TYPE_EFFECT~=0" or "return c:IsType(TYPE_EFFECT) or bit.band(c:GetOriginalType(),TYPE_EFFECT)==TYPE_EFFECT") => "monster",
            ("LOCATION_SZONE,LOCATION_SZONE", "return c:IsType(TYPE_SPELL)") => "spell",
            ("LOCATION_SZONE,LOCATION_SZONE", "return c~=e:GetHandler() and c:IsType(TYPE_SPELL)") => "other_spell",
            ("LOCATION_SZONE,LOCATION_SZONE", "return c:IsType(TYPE_TRAP)") => "trap",
            ("LOCATION_SZONE,LOCATION_SZONE", "return c~=e:GetHandler() and c:IsType(TYPE_TRAP)") => "other_trap",
            _ => null,
        };
    }

    /// <summary>
    /// The parts of a Spell/Trap negation the "negate all" block writes itself: negating them as they resolve, and
    /// Trap Monsters.
    /// </summary>
    private static bool IsNegateHelper(HashSet<string> type, Dictionary<string, string> set, Functions fns)
    {
        if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && set.GetValueOrDefault("Code") == "EFFECT_DISABLE_TRAPMONSTER") return true;
        if (!type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_CONTINUOUS"]) || set.GetValueOrDefault("Code") != "EVENT_CHAIN_SOLVING"
            || set.GetValueOrDefault("Operation") is not { } op || set.ContainsKey("Condition")) return false;
        var body = Body(op, fns);
        return Regex.IsMatch(body, @"^local tl=Duel\.GetChainInfo\(ev,CHAININFO_TRIGGERING_LOCATION\) if (?:bit\.band\(tl,LOCATION_SZONE\)~=0|tl==LOCATION_SZONE|tl&LOCATION_SZONE~=0) and re:IsActiveType\(TYPE_(?:SPELL|TRAP)\)(?: and re:GetHandler\(\)~=e:GetHandler\(\))? then Duel\.NegateEffect\(ev\) end$");
    }

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
            var reset = (first.GetValueOrDefault("Reset") ?? "").Replace(" ", "");
            var lasts = reset switch
            {
                "RESET_PHASE+PHASE_END" => "turn",
                "RESET_PHASE+PHASE_END,2" => "next",
                "" => "duel",
                _ => throw No($"it puts an effect on a player for a time the blocks don't have ({Short(reset)})"),
            };
            if (parts.Count is >= 1 and <= 3 && parts.Select(p => LockOf(p.Set, fns)).ToList() is var locks && locks.All(x => x is not null))
            {
                var l = locks[0]!.Value;
                // Each part spares the same monsters and stops the same player.
                var same = locks.All(x => x!.Value.Who == l.Who && FilterCompiler.Conditions(x.Value.Except).SequenceEqual(FilterCompiler.Conditions(l.Except)));
                if (same && parts.All(p => (p.Set.GetValueOrDefault("Reset") ?? "").Replace(" ", "") == reset) && CombinedLock(locks.Select(x => x!.Value.What)) is { } what)
                    return Block("do.summon_restriction").With("duration", lasts).With("who", l.Who).With("what", what).With("cards", l.Except);
            }
            if (parts.Count == 1 && BanishInsteadOf(first, fns) is { } bi)
                return Block("do.banish_instead").With("duration", lasts).With("what", bi.What).With("whose", bi.Whose).With("from", bi.From);
            return LastingTemp(temp, target, targetCard, fns) ?? throw No($"it puts an effect on a player the blocks don't have ({codes[0]})");
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
            if (LiveTempStats(temp, target, targetCard) is { } liveSelf) return liveSelf;
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
        if (LiveTempStats(temp, target, targetCard) is { } liveTarget) return liveTarget;
        return LastingTemp(temp, target, targetCard, fns) ?? throw No($"it gives the target an effect the blocks don't have ({codes[0]})");
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
        var (body, temps) = FoldTempEffects(JoinContinuations(rawBody));
        // Murakumo: "your opponent can discard 1 card, or that effect is negated".
        var whole = string.Join(" ", body.Select(l => l.Trim()).Where(l => l.Length > 0));
        if (SummonNegation(whole) is { } negateSummon) return negateSummon;
        // "Reveal 3 ..., your opponent randomly picks 1 for you to add to your hand" (ScriptTranslator.Selections.cs).
        ResetPicks();
        if (RevealPickOf(body, fns) is { } revealed) return revealed;
        if (Regex.IsMatch(whole, @"^if Duel\.(?:GetMatchingGroupCount\(Card\.IsDiscardable,tp,0,LOCATION_HAND,nil,REASON_EFFECT\)>0|IsExistingMatchingCard\(Card\.IsDiscardable,1-tp,LOCATION_HAND,0,1,nil(?:,REASON_EFFECT)?\)) and Duel\.SelectYesNo\(1-tp,aux\.Stringid\(\w+,\d+\)\) then Duel\.DiscardHand\(1-tp,(?:aux\.TRUE|nil),1,1,REASON_EFFECT\+REASON_DISCARD(?:,nil)?\) else Duel\.NegateEffect\(ev\) end$"))
            return [Block("do.negate_unless").With("pay", "discard")];
        if (Regex.IsMatch(whole, @"^if Duel\.CheckLPCost\(1-tp,1000\) and Duel\.SelectYesNo\(1-tp,aux\.Stringid\(\w+,\d+\)\) then Duel\.PayLPCost\(1-tp,1000\) else Duel\.NegateEffect\(ev\) end$"))
            return [Block("do.negate_unless").With("pay", "lp")];
        var blocks = new List<BlockInstance>();
        var groups = new Dictionary<string, (CardFilter Filter, int Count)>();
        var link = ActionLink.And;
        bool targetCard = false, self = false, fromHand = false;
        // "Special Summon up to 2": the free Monster Zones ("ft"), capped by the card.
        int? ftCap = null;
        var discards = new HashSet<string>();
        BlockInstance? setBlock = null;
        // "..., then you can ...": the next action is optional; the checks before the question must be about the cards it picks.
        var optional = false;
        var optionalChecks = new HashSet<string>();
        string Bare(string f) => Regex.Replace(f, @"^aux\.NecroValleyFilter\((.+)\)$", "$1");
        BlockInstance Add(BlockInstance block)
        {
            if (optionalChecks.Count > 0) throw No("its optional step has a condition the blocks can't say");
            block.Optional = optional;
            optional = false;
            return block;
        }
        // Numbers and groups the steps work out ("local ct=Duel.GetMatchingGroupCount(...)") for the value blocks.
        var values = _opValues = new OperationValues { Fns = fns, Target = target };
        var sawIf = false;
        for (int index = 0; index < body.Count; index++)
        {
            values.Blocks = blocks.Count;
            var line = body[index].Trim();
            if (RecordValueLocal(line, values)) continue;
            line = WithoutZeroCheck(line, body, index, temps, values);
            // A check before this step ("if c:IsRelateToEffect(e) then"), which the LP blocks don't make.
            values.Guarded = sawIf;
            if (line.StartsWith("if ", StringComparison.Ordinal)) sawIf = true;
            Match ym;
            if ((ym = Regex.Match(line, @"^if (.*?)Duel\.SelectYesNo\(tp,aux\.Stringid\((?:id|\d+),\d+\)\) then$")).Success && !IsCompound(line, body, fns))
            {
                foreach (var part in SplitTopLevelAnd(Regex.Replace(ym.Groups[1].Value, @"\s+and\s*$", "")).Select(p => p.Trim()).Where(p => p.Length > 0))
                {
                    Match cm;
                    // "if Duel.Destroy(g,REASON_EFFECT)~=0 and Duel.SelectYesNo(...)": the action, then the question.
                    if ((cm = Regex.Match(part, @"^(Duel\.(?!GetLocationCount|IsExisting|GetMatchingGroupCount|GetFieldGroupCount)\w+\(.*\))(?:~=0|>0)$")).Success)
                    {
                        blocks.Add(Add(Action(cm.Groups[1].Value, groups, target, targetCard, card, ref self).Linked(link)));
                        link = ActionLink.IfYouDo;
                        continue;
                    }
                    if (Regex.IsMatch(part, @"^(?:#\w+>0|\w+:GetCount\(\)>0|Duel\.GetLocationCount\(tp,LOCATION_(?:MZONE|SZONE)\)>0|(?:c|tc|e:GetHandler\(\)):IsRelateToEffect\(e\)|(?:c|tc|e:GetHandler\(\)):IsRelateToChain\(\)|(?:c|tc):IsCanBeSpecialSummoned\(e,0,tp,false,false\)|aux\.NecroValleyFilter\(\)\((?:c|tc)\))$")) continue;
                    if ((cm = Regex.Match(part, @"^Duel\.IsExistingMatchingCard\(([\w.()]+),tp,")).Success) { optionalChecks.Add(Bare(cm.Groups[1].Value)); continue; }
                    throw No($"its optional step has a condition the blocks can't say ({Short(part)})");
                }
                optional = true;
                continue;
            }
            if (line.Length == 0 || line == "local c=e:GetHandler()" || line.StartsWith("Duel.Hint(", StringComparison.Ordinal)
                || line.StartsWith("Duel.HintSelection(", StringComparison.Ordinal) || line.StartsWith("Duel.ConfirmCards(", StringComparison.Ordinal)
                || line.StartsWith("Duel.ShuffleHand(", StringComparison.Ordinal) || line.StartsWith("Duel.ShuffleDeck(", StringComparison.Ordinal)
                || line == "end" || line == "local p,d=Duel.GetChainInfo(0,CHAININFO_TARGET_PLAYER,CHAININFO_TARGET_PARAM)"
                || Regex.IsMatch(line, @"^if \w+:GetCount\(\)>0 then$") || Regex.IsMatch(line, @"^if #\w+>0 then$")
                || Regex.IsMatch(line, @"^if (?:\w+ and )?\w+:IsRelateToEffect\(e\)(?: and \w+:IsFaceup\(\))?(?: and aux\.NecroValleyFilter\(\)\(\w+\))? then$")
                || Regex.IsMatch(line, @"^if Duel\.GetLocationCount\(tp,LOCATION_MZONE\)<=0 then return end$")
                // The game's own check for Necrovalley: the blocks write it themselves.
                || Regex.IsMatch(line, @"^if aux\.NecroValleyNegateCheck\(\w+\) then return end$")
                || Regex.IsMatch(line, @"^if not \w+:IsRelateToEffect\(e\) then return end$")
                // "if c:IsRelateToEffect(e) and tc:IsRelateToEffect(e) and tc:IsFaceup() then": the blocks check the same.
                || Regex.IsMatch(line, @"^if (?:c|e:GetHandler\(\)):IsRelateToEffect\(e\) and tc:IsRelateToEffect\(e\)(?: and tc:IsFaceup\(\))? then$")
                || Regex.IsMatch(line, @"^if tc and tc:IsRelateToEffect\(e\)(?: and tc:IsFaceup\(\))? then$")
                || IsGuard(line))
                continue;
            // Cards picked as a group, at random or by the opponent, and the targets (ScriptTranslator.Selections.cs).
            if (SelectionLine(line, body, index, groups, target, fns)) continue;
            Match m;
            // "Each face-up monster you control gains 500 ATK": a loop over every card of a group.
            if (GroupLoop(body, index, groups, temps) is { } loop)
            {
                if (loop.Block is { } each) { blocks.Add(Add(each.Linked(link))); link = ActionLink.And; }
                index = loop.End;
                continue;
            }
            if (line == "local tc=Duel.GetFirstTarget()" && target.Filter is not null) { targetCard = true; continue; }
            if (line == "local ft=Duel.GetLocationCount(tp,LOCATION_MZONE)") { ftCap = 5; continue; }
            if (ftCap is not null && Regex.IsMatch(line, @"^if (?:ft>1 and )?Duel\.IsPlayerAffectedByEffect\(tp,59822133\) then ft=1 end$")) continue;
            if (ftCap is not null && line is "if ft<=0 then return end" or "if ft<1 then return end") continue;
            if (ftCap is not null && Regex.Match(line, @"^if ft>(\d+) then ft=\1 end$") is { Success: true } cap) { ftCap = Int(cap.Groups[1].Value); continue; }
            if (ftCap is { } ftc && Regex.Match(line, @"^local (\w+)=Duel\.SelectMatchingCard\(tp,([\w.()]+),tp,([\w+]+),([\w+]+),1,ft,(nil|e:GetHandler\(\)|c)(?:,e,tp)?\)$") is { Success: true } ftm)
            {
                var f = Filter(ftm.Groups[2].Value, fns);
                Locate(f, ftm.Groups[3].Value, ftm.Groups[4].Value);
                f.ExcludeThisCard = ftm.Groups[5].Value != "nil";
                // A negative count: "up to" that many.
                groups[ftm.Groups[1].Value] = (f, -ftc);
                continue;
            }
            if (line is "if e:GetLabel()==100 then" or "if e:GetHandler():IsStatus(STATUS_ACT_FROM_HAND) then" or "if c:IsStatus(STATUS_ACT_FROM_HAND) then")
            {
                fromHand = true;
                continue;
            }
            if (setBlock is not null && line.StartsWith("@TEMP ", StringComparison.Ordinal) && temps[Int(line[6..])].Parts is [{ } setOnly]
                && setOnly.Set.GetValueOrDefault("Code") is "EFFECT_TRAP_ACT_IN_SET_TURN" or "EFFECT_QP_ACT_IN_SET_TURN")
            {
                setBlock.Values["this_turn"] = "true";
                link = ActionLink.And;
                continue;
            }
            if (line.StartsWith("@TEMP ", StringComparison.Ordinal) && LastingTempLine(index, body, temps, blocks, target, targetCard)) { link = ActionLink.And; continue; }
            if (line.StartsWith("@TEMP ", StringComparison.Ordinal))
            {
                var temp = TempAction(temps[Int(line[6..])], target, targetCard, fns);
                if (fromHand)
                {
                    // "If you activated this card from your hand, you cannot ... for the rest of this Duel": apart from the steps before it.
                    if (temp.BlockId != "do.summon_restriction") throw No("it does something the blocks don't when activated from the hand");
                    blocks.Add(Add(temp.With("from_hand", "true").Linked(ActionLink.And)));
                    fromHand = false;
                }
                else blocks.Add(Add(temp.Linked(link)));
                link = ActionLink.And;
                continue;
            }
            // Part of negating a card's effects, which the block does too.
            if (line == "Duel.NegateRelatedChain(tc,RESET_TURN_SET)") continue;
            // "then discard 1 card": picked from the hand, then sent to the GY as a discard.
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectMatchingCard\(tp,Card\.IsDiscardable,tp,LOCATION_HAND,0,(\d+),\2,nil(?:,REASON_\w+(?:\+REASON_\w+)*)?\)$")).Success)
            {
                groups[m.Groups[1].Value] = (new CardFilter { Owner = CardOwner.You, Zones = Model.Zone.Hand, CardType = FilterCardType.Any }, Int(m.Groups[2].Value));
                discards.Add(m.Groups[1].Value);
                continue;
            }
            if ((m = Regex.Match(line, @"^Duel\.SendtoGrave\((\w+),REASON_(?:EFFECT\+REASON_DISCARD|DISCARD\+REASON_EFFECT)\)$")).Success && discards.Contains(m.Groups[1].Value))
            {
                blocks.Add(Add(Block("do.discard").With("who", "you").With("count", groups[m.Groups[1].Value].Count).Linked(link)));
                link = ActionLink.And;
                continue;
            }
            // "Set 1 Trap from your Deck. It can be activated this turn."
            if ((m = Regex.Match(line, @"^if (?:#(\w+)>0|(\w+):GetCount\(\)>0) and Duel\.SSet\(tp,(\w+)\)(?:>0|~=0) then$")).Success && groups.TryGetValue(m.Groups[3].Value, out var setGroup))
            {
                setBlock = Block("do.set").With("count", Math.Max(1, setGroup.Count)).With("cards", setGroup.Filter);
                blocks.Add(Add(setBlock.Linked(link)));
                link = ActionLink.IfYouDo;
                continue;
            }
            if (setBlock is not null && line == "local tc=g:GetFirst()") continue;
            if (setBlock is not null && line.StartsWith("@TEMP ", StringComparison.Ordinal) && temps[Int(line[6..])].Parts is [{ } only]
                && only.Set.GetValueOrDefault("Code") is "EFFECT_TRAP_ACT_IN_SET_TURN" or "EFFECT_QP_ACT_IN_SET_TURN")
            {
                setBlock.Values["this_turn"] = "true";
                link = ActionLink.And;
                continue;
            }
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectMatchingCard\(tp,([\w.()]+),tp,([\w+]+),([\w+]+),(\d+),\5,(nil|e:GetHandler\(\)|c)(?:,e,tp|,e)?\)$")).Success)
            {
                var f = Filter(m.Groups[2].Value, fns);
                Locate(f, m.Groups[3].Value, m.Groups[4].Value);
                f.ExcludeThisCard = m.Groups[6].Value != "nil";
                groups[m.Groups[1].Value] = (f, Int(m.Groups[5].Value));
                optionalChecks.Remove(Bare(m.Groups[2].Value));
                continue;
            }
            // "local sg=g:Select(tp,1,1,nil)": some of a group of matching cards.
            if ((m = Regex.Match(line, @"^local (\w+)=(\w+):Select\(tp,(\d+),\3,nil\)$")).Success && groups.TryGetValue(m.Groups[2].Value, out var pool))
            {
                groups[m.Groups[1].Value] = (pool.Filter, Int(m.Groups[3].Value));
                continue;
            }
            // "local tc=g:GetFirst()" after picking 1 card: the same card under another name.
            if ((m = Regex.Match(line, @"^local (\w+)=(\w+):GetFirst\(\)$")).Success && groups.TryGetValue(m.Groups[2].Value, out var picked) && picked.Count == 1)
            {
                groups[m.Groups[1].Value] = picked;
                continue;
            }
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.GetMatchingGroup\(([\w.()]+),tp,([\w+]+),([\w+]+),(nil|e:GetHandler\(\)|c|aux\.ExceptThisCard\(e\))(?:,e,tp)?\)$")).Success)
            {
                var f = Filter(m.Groups[2].Value, fns);
                Locate(f, m.Groups[3].Value, m.Groups[4].Value);
                f.ExcludeThisCard = m.Groups[5].Value != "nil";
                groups[m.Groups[1].Value] = (f, 0);
                optionalChecks.Remove(Bare(m.Groups[2].Value));
                continue;
            }
            if (line is "local p=Duel.GetChainInfo(0,CHAININFO_TARGET_PLAYER)" or "local d=Duel.GetChainInfo(0,CHAININFO_TARGET_PARAM)") continue;
            // "then Tribute 1 monster": the monsters you could Tribute.
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.GetReleaseGroup\(tp(?:,false)?(?:,REASON_EFFECT)?\)$")).Success)
            {
                groups[m.Groups[1].Value] = (new CardFilter { Owner = CardOwner.You, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster }, 0);
                continue;
            }
            if (Regex.IsMatch(line, @"^if \w+ and \w+:GetCount\(\)>0 then$") || Regex.IsMatch(line, @"^if \w+ and #\w+>0 then$")) continue;
            // "Only once this turn": a flag the target step checks so the effect can't stack (Dimension Shifter).
            if ((m = Regex.Match(line, @"^Duel\.RegisterFlagEffect\(tp,(id|\d+),RESET_PHASE\+PHASE_END,0,1\)$")).Success
                && (m.Groups[1].Value == "id" || Int64(m.Groups[1].Value) == _sourceId)) continue;
            // After "then if ...", the IF block already says "then".
            if (line == "Duel.BreakEffect()") { link = blocks.Count > 0 && BlockRegistry.Find(blocks[^1].BlockId) is ConditionBlock ? ActionLink.And : ActionLink.Then; continue; }
            // "your opponent can Special Summon 1 monster from their hand": they pick, then Summon to their field.
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.GetMatchingGroup\(([\w.()]+),1-tp,([\w+]+),0,nil(?:,e,1-tp)?\)$")).Success)
            {
                var f = Filter(m.Groups[2].Value, fns);
                Locate(f, "0", m.Groups[3].Value);
                groups[m.Groups[1].Value] = (f, 0);
                continue;
            }
            if ((m = Regex.Match(line, @"^local (\w+)=(\w+):Select\(1-tp,(\d+),\3,nil\)$")).Success && groups.TryGetValue(m.Groups[2].Value, out var theirPool))
            {
                groups[m.Groups[1].Value] = (theirPool.Filter, Int(m.Groups[3].Value));
                continue;
            }
            if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectMatchingCard\(1-tp,([\w.()]+),1-tp,([\w+]+),0,(\d+),\4,nil(?:,e,1-tp)?\)$")).Success)
            {
                var f = Filter(m.Groups[2].Value, fns);
                Locate(f, "0", m.Groups[3].Value);
                groups[m.Groups[1].Value] = (f, Int(m.Groups[4].Value));
                continue;
            }
            if ((m = Regex.Match(line, @"^Duel\.SpecialSummon\((\w+),0,1-tp,1-tp,false,false,POS_FACEUP\)$")).Success && groups.TryGetValue(m.Groups[1].Value, out var theirs)
                && theirs.Filter.Owner == CardOwner.Opponent)
            {
                blocks.Add(Add(Block("do.opponent_special_summons").With("count", Math.Max(1, theirs.Count)).With("cards", theirs.Filter).Linked(link)));
                link = ActionLink.And;
                continue;
            }
            // "if Duel.NegateEffect(ev) and Duel.IsExistingMatchingCard(Card.IsType,tp,LOCATION_GRAVE,0,1,nil,TYPE_TRAP) then":
            // an action, then IF blocks for the rest ("then if you have a Trap in your GY, ...").
            if ((m = Regex.Match(line, @"^if (.+) then$")).Success && IsCompound(line, body, fns) && SplitTopLevelAnd(m.Groups[1].Value) is var terms)
            {
                var opponentChooses = terms.Any(t => t.Trim().StartsWith("Duel.SelectYesNo(1-tp,", StringComparison.Ordinal));
                foreach (var raw2 in terms)
                {
                    var t = raw2.Trim();
                    Match tm;
                    if (GuardTerm.IsMatch(t) || t is "re:GetHandler():IsRelateToEffect(re)" or "tc:IsRelateToChain()" or "c:IsRelateToChain()") continue;
                    // The opponent's own checks before they choose ("they have room and a monster to Summon").
                    if (opponentChooses && (t.StartsWith("Duel.SelectYesNo(1-tp,", StringComparison.Ordinal) || Regex.IsMatch(t, @"^Duel\.GetLocationCount\(1-tp,LOCATION_MZONE\)>0$")
                        || Regex.IsMatch(t, @"^Duel\.IsExistingMatchingCard\([\w.]+,1-tp,"))) continue;
                    // Checks the next step makes itself.
                    if (Regex.IsMatch(t, @"^(?:(?:c|tc):IsLocation\(LOCATION_\w+(?:\+LOCATION_\w+)*\)|\w+:GetCount\(\)>0|#\w+>0|Duel\.GetLocationCount\(tp,LOCATION_(?:MZONE|SZONE)\)>0|(?:c|tc):IsType\(TYPE_MONSTER\)|(?:c|tc):IsCanBeSpecialSummoned\(e,0,tp,false,false\)|aux\.NecroValleyFilter\(\)\((?:c|tc)\)|(?:c|tc):IsRelateToEffect\(e\))$")) continue;
                    if ((tm = Regex.Match(t, @"^(Duel\.(?!GetLocationCount|IsExisting|GetMatchingGroupCount|GetFieldGroupCount)\w+\(.*\))(?:~=0|>0)$")).Success || (tm = Regex.Match(t, @"^(Duel\.Negate(?:Effect|Activation)\(ev\))$")).Success)
                    {
                        blocks.Add(Add(Action(tm.Groups[1].Value, groups, target, targetCard, card, ref self).Linked(link)));
                        link = ActionLink.IfYouDo;
                        continue;
                    }
                    // The opponent's own checks before they choose ("they have room and a monster to Summon").
                    if (opponentChooses && (t.StartsWith("Duel.SelectYesNo(1-tp,", StringComparison.Ordinal) || Regex.IsMatch(t, @"^Duel\.GetLocationCount\(1-tp,LOCATION_MZONE\)>0$")
                        || Regex.IsMatch(t, @"^Duel\.IsExistingMatchingCard\([\w.]+,1-tp,"))) continue;
                    if (t.StartsWith("Duel.SelectYesNo(tp,", StringComparison.Ordinal)) { optional = true; continue; }
                    if (ExistsCheck(t, fns) is { } gate)
                    {
                        // A check on the cards the next optional step picks belongs to that step.
                        if (terms.Any(x => x.Trim().StartsWith("Duel.SelectYesNo(tp,", StringComparison.Ordinal))
                            && Regex.Match(t, @"^Duel\.IsExistingMatchingCard\(([\w.()]+),tp,") is { Success: true } om
                            && body.Any(l => l.Contains($"SelectMatchingCard(tp,{om.Groups[1].Value},", StringComparison.Ordinal) || l.Contains($"SelectMatchingCard(tp,aux.NecroValleyFilter({om.Groups[1].Value}),", StringComparison.Ordinal)))
                        {
                            optionalChecks.Add(Bare(om.Groups[1].Value));
                            continue;
                        }
                        blocks.Add(gate.Linked(blocks.Count == 0 ? link : ActionLink.Then));
                        link = ActionLink.And;
                        continue;
                    }
                    throw No($"it does something the blocks don't ({Short(t)})");
                }
                continue;
            }
            if ((m = Regex.Match(line, @"^if (.+?)(?:~=0|>0) then$")).Success)
            {
                // "if Duel.Draw(...)>0 then": the next action happens only if this one did.
                var inner = Regex.Replace(m.Groups[1].Value, @"^(?:(?:(?:c|tc|e:GetHandler\(\)):IsRelateToEffect\(e\)|#\w+>0|\w+:GetCount\(\)>0|tc) and )+", "");
                blocks.Add(Add(Action(inner, groups, target, targetCard, card, ref self).Linked(link)));
                link = ActionLink.IfYouDo;
                continue;
            }
            blocks.Add(Add(Action(line, groups, target, targetCard, card, ref self).Linked(link)));
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
        if (CounterAction(line, target, targetCard) is { } counter) return counter;
        if (MechanicsAction(line, groups, target, targetCard, card, ref self) is { } mechanicsAction) return mechanicsAction;
        if (MemoryAction(line, groups, target, targetCard) is { } memoryAction) return memoryAction;
        if (SelfToHandAction(line, target) is { } selfToHand) return selfToHand;
        if (TreatedAsSummonAction(line, groups, target, targetCard) is { } treatedSummon) return treatedSummon;
        if (NegatedSummonAction(line, groups, target, targetCard, card, ref self) is { } negatedSummon) return negatedSummon;

        // "inflict 300 damage for each card in your opponent's hand": an amount worked out when it resolves.
        if ((m = Regex.Match(line, @"^Duel\.(Damage|Recover)\((\w+|1-tp),(.+),REASON_EFFECT\)$")).Success && !Regex.IsMatch(m.Groups[3].Value, @"^(?:\d+|CCEQ_\w+)$")
            && (m.Groups[3].Value != "d" || target.ParamEvent || target.ParamDynamic) && _opValues is { } ov
            && LifeValue(m.Groups[1].Value, Who(m.Groups[2].Value), m.Groups[3].Value, ov) is { } life)
            return life;
        if ((m = Regex.Match(line, @"^Duel\.Draw\((\w+|1-tp),(\w+),REASON_EFFECT\)$")).Success)
            return Block("do.draw").With("who", Who(m.Groups[1].Value)).With("count", Amount(m.Groups[2].Value));
        // "equal to that monster's original ATK", from EqualToStat.
        if ((m = Regex.Match(line, @"^Duel\.(Damage|Recover)\((\w+|1-tp),CCEQ_(\w+),REASON_EFFECT\)$")).Success)
            return Block(m.Groups[1].Value == "Damage" ? "do.damage" : "do.gain_lp").With("who", Who(m.Groups[2].Value)).With("equal", m.Groups[3].Value);
        if ((m = Regex.Match(line, @"^Duel\.Damage\((\w+|1-tp),(\w+),REASON_EFFECT\)$")).Success)
            return Block("do.damage").With("who", Who(m.Groups[1].Value) == "you" ? "you" : "opponent").With("amount", Amount(m.Groups[2].Value));
        if ((m = Regex.Match(line, @"^Duel\.Recover\((\w+|1-tp),(\w+),REASON_EFFECT\)$")).Success)
            return Block("do.gain_lp").With("who", Who(m.Groups[1].Value)).With("amount", Amount(m.Groups[2].Value));
        if ((m = Regex.Match(line, @"^Duel\.DiscardDeck\((tp|1-tp),(\d+),REASON_EFFECT\)$")).Success)
            return Block("do.mill").With("whose", m.Groups[1].Value == "tp" ? "you" : "opponent").With("count", Int(m.Groups[2].Value));
        // "Lose 1000 LP" (The Unstoppable Exodia Incarnate): the LP set lower, not damage.
        if ((m = Regex.Match(line, @"^Duel\.SetLP\((tp|1-tp),Duel\.GetLP\(\1\)-(\d+)\)$")).Success)
            return Block(SetLifeAction.BlockId).With("who", m.Groups[1].Value == "tp" ? "you" : "opponent").With("how", "lose").With("amount", Int(m.Groups[2].Value));
        // Mimighoul: "Special Summon this card from your hand to your opponent's field in face-down Defense Position".
        if (Regex.IsMatch(line, @"^(?:if )?Duel\.SpecialSummon\(c,0,tp,1-tp,false,false,POS_FACEDOWN_DEFENSE\)(?:>0 then Duel\.ConfirmCards\(tp,c\) end)?$"))
        {
            self = true;
            return Block("do.special_summon_self").With("side", "opponent").With("position", "defense_facedown");
        }
        // Mimighoul: "Give control of this card to your opponent".
        if (line is "Duel.GetControl(c,1-tp)" or "Duel.GetControl(e:GetHandler(),1-tp)")
        {
            self = true;
            return Block("do.this_card").With("how", "control");
        }
        if (line == "Duel.NegateActivation(ev)") return Block("do.negate").With("what", "activation");
        if (line == "Duel.NegateAttack()") return Block("do.negate_attack");
        if ((m = Regex.Match(line, @"^Duel\.DiscardHand\((tp|1-tp),nil,(\d+),\2,REASON_EFFECT\+REASON_DISCARD\)$")).Success)
            return Block("do.discard").With("who", m.Groups[1].Value == "tp" ? "you" : "opponent").With("count", Int(m.Groups[2].Value));
        if (line == "Duel.NegateEffect(ev)") return Block("do.negate").With("what", "effect");
        // "destroy that card": the card whose effect was activated.
        if (line is "Duel.Destroy(eg,REASON_EFFECT)" or "Duel.Destroy(re:GetHandler(),REASON_EFFECT)" or "Duel.Destroy(rc,REASON_EFFECT)") return Block("do.that_card").With("how", "destroy");
        if (line is "Duel.Remove(eg,POS_FACEUP,REASON_EFFECT)" or "Duel.Remove(re:GetHandler(),POS_FACEUP,REASON_EFFECT)" or "Duel.Remove(rc,POS_FACEUP,REASON_EFFECT)") return Block("do.that_card").With("how", "banish");

        // Actions on cards: a selected group, the target, or this card.
        var verbs = new (string Pattern, string Block)[]
        {
            (@"^Duel\.Destroy\((\w+),REASON_EFFECT\)$", "do.destroy"),
            (@"^Duel\.Remove\((\w+),POS_FACEUP,REASON_EFFECT\)$", "do.banish"),
            (@"^Duel\.SendtoGrave\((\w+),REASON_EFFECT\)$", "do.send_to_gy"),
            (@"^Duel\.SendtoHand\((\w+),nil,REASON_EFFECT\)$", "hand"),
            (@"^Duel\.SendtoDeck\((\w+),nil,(?:2|SEQ_DECKSHUFFLE),REASON_EFFECT\)$", "do.shuffle_into_deck"),
            (@"^Duel\.Release\((\w+),REASON_EFFECT\)$", "do.tribute"),
            (@"^Duel\.SpecialSummon\((\w+),0,tp,tp,false,false,(POS_FACEUP|POS_FACEUP_ATTACK|POS_FACEUP_DEFENSE)\)$", "do.special_summon"),
            (@"^Duel\.SpecialSummon\((\w+),0,tp,tp,true,false,(POS_FACEUP|POS_FACEUP_ATTACK|POS_FACEUP_DEFENSE)\)$", "ignoring"),
            (@"^Duel\.GetControl\((\w+),tp,PHASE_END,1\)$", "control_turn"),
            (@"^Duel\.GetControl\((\w+),tp,PHASE_BATTLE,1\)$", "control_battle"),
            (@"^Duel\.GetControl\((\w+),tp\)$", "control"),
            (@"^Duel\.ChangePosition\((\w+),POS_FACEUP_DEFENSE,POS_FACEUP_DEFENSE,POS_FACEUP_ATTACK,POS_FACEUP_ATTACK\)$", "pos_toggle"),
            (@"^Duel\.ChangePosition\((\w+),POS_FACEDOWN_DEFENSE\)$", "pos_facedown"),
            (@"^Duel\.Overlay\((?:c|e:GetHandler\(\)),(\w+)\)$", "do.attach"),
        };
        foreach (var (pattern, blockId) in verbs)
        {
            m = Regex.Match(line, pattern);
            if (!m.Success) continue;
            var what = m.Groups[1].Value;
            var position = blockId is "do.special_summon" or "ignoring" && m.Groups.Count > 2 && m.Groups[2].Success ? m.Groups[2].Value switch { "POS_FACEUP_ATTACK" => "attack", "POS_FACEUP_DEFENSE" => "defense", _ => "faceup" } : null;
            if (what is "c" && blockId == "do.special_summon")
            {
                self = true;
                return Block("do.special_summon_self").With("position", position!);
            }
            CardFilter filter;
            int count;
            bool targeted;
            if (groups.TryGetValue(what, out var g)) (filter, count, targeted) = (g.Filter, g.Count, false);
            else if ((what == "tc" && targetCard || IsTargetGroup(what)) && target.Filter is not null) (filter, count, targeted) = (target.Filter, target.Count, true);
            else throw No($"it acts on {what}, which the blocks can't follow");

            var id = blockId switch
            {
                "hand" => (filter.Zones & (Model.Zone.Deck | Model.Zone.Graveyard | Model.Zone.Banished)) != 0 && (filter.Zones & Model.Zone.Field) == 0 ? "do.add_to_hand" : "do.return_to_hand",
                "control" or "control_turn" or "control_battle" => "do.take_control",
                "pos_toggle" or "pos_facedown" => "do.change_position",
                "ignoring" => "do.special_summon",
                _ => blockId,
            };
            // Only monsters can be Special Summoned: "Special Summon 1 "Mitsurugi" monster".
            if (id == "do.special_summon" && filter.CardType == FilterCardType.Any) { filter = filter.Clone(); filter.CardType = FilterCardType.Monster; }
            var block = Block(id).With("count", Math.Max(1, Math.Abs(count))).With("cards", filter);
            if (count < 0)
            {
                if (BlockRegistry.Find(id)!.Param("mode")?.Choices.Any(c => c.Value == "up_to") != true) throw No("it acts on a varying number of cards, which that block can't");
                block = block.With("mode", "up_to");
            }
            // A group of every matching card: "destroy all monsters your opponent controls".
            if (count == 0)
            {
                if (BlockRegistry.Find(id)!.Param("mode")?.Choices.Any(c => c.Value == "all") != true) throw No("it acts on every matching card, which that block can't");
                block = block.With("mode", "all");
            }
            if (blockId is "control" or "control_turn" or "control_battle") block = block.With("duration", blockId switch { "control_turn" => "turn", "control_battle" => "battle", _ => "always" });
            if (blockId is "pos_toggle" or "pos_facedown") block = block.With("to", blockId == "pos_toggle" ? "toggle" : "facedown");
            if (blockId == "ignoring") block = block.With("ignore", true);
            if (BlockRegistry.Find(id)!.Param("target") is not null) block = block.With("target", targeted);
            else if (targeted) throw No("it targets in a way that block can't");
            if (position is not null && BlockRegistry.Find(id)!.Param("position") is not null) block = block.With("position", position);
            ApplyPicks(what, targeted, target, block);
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
        // (A Summon without Tributing reads its own condition: it is checked from the hand.)
        if (set.GetValueOrDefault("Condition") is { } lastingCondition && !result.Do.Any(b => b.BlockId == NoTributeSummonPassive.BlockId))
            result.If.AddRange(Conditions(lastingCondition, fns));
        return result;
    }

    private static EffectEvent LastingEffect(EffectEvent effect, HashSet<string> type, string code, Dictionary<string, string> set, string? range, HashSet<string> props, Functions fns, CardData card)
    {
        effect.Limit = OncePerTurn.None;
        effect.When = Block("when.while_face_up").With("zone", Zone(range ?? "LOCATION_MZONE"));
        var value = set.GetValueOrDefault("Value");
        if (set.ContainsKey("Target") && !type.Contains("EFFECT_TYPE_FIELD"))
            throw No($"its lasting effect picks its cards in a way the blocks don't ({code})");

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
                case "EFFECT_CANNOT_BE_EFFECT_TARGET" when value == "1": effect.Do.Add(Block("passive.untargetable").With("who", "any")); return effect;
                case "EFFECT_CANNOT_ATTACK": effect.Do.Add(Block("passive.self_rule").With("rule", "cannot_attack")); return effect;
                case "EFFECT_CANNOT_DIRECT_ATTACK": effect.Do.Add(Block("passive.self_rule").With("rule", "cannot_direct")); return effect;
                case "EFFECT_CANNOT_BE_BATTLE_TARGET" when value == "aux.imval1": effect.Do.Add(Block("passive.self_rule").With("rule", "not_attack_target")); return effect;
                case "EFFECT_AVOID_BATTLE_DAMAGE" when value == "1": effect.Do.Add(Block("passive.self_rule").With("rule", "no_battle_damage")); return effect;
                case "EFFECT_CANNOT_CHANGE_POSITION": effect.Do.Add(Block("passive.self_rule").With("rule", "cannot_change_position")); return effect;
                // "Control of this card cannot switch" (Heart of the Blue-Eyes).
                case "EFFECT_CANNOT_CHANGE_CONTROL" when value is null or "1": effect.Do.Add(Block("passive.self_rule").With("rule", "control_fixed")); return effect;
                // The Crystal Beasts: "If this card is destroyed while in the Monster Zone, you can place it ... as a Continuous Spell".
                case "EFFECT_TO_GRAVE_REDIRECT_CB" when PlacedInstead(set, fns):
                    // Its condition is the block's own check, not an IF block.
                    set.Remove("Condition");
                    effect.Do.Add(Block(PlaceInsteadPassive.BlockId));
                    return effect;
                case "EFFECT_ATTACK_ALL" when value == "1": effect.Do.Add(Block("passive.self_rule").With("rule", "attack_all")); return effect;
                case "EFFECT_MUST_ATTACK": effect.Do.Add(Block("passive.self_rule").With("rule", "must_attack")); return effect;
                case "EFFECT_INDESTRUCTABLE_EFFECT" when value == "aux.indoval": effect.Do.Add(Block("passive.indestructible").With("by", "opponent_effect")); return effect;
                case "EFFECT_INDESTRUCTABLE_EFFECT" when value is not null && IndestructibleEffectBy(value, fns) is { } indBy2: effect.Do.Add(Block("passive.indestructible").With("by", indBy2)); return effect;
                // "If ..., destroy this card": the IF blocks are the lasting effect's condition.
                case "EFFECT_SELF_DESTROY" when value is null: effect.Do.Add(Block(SelfDestroyPassive.BlockId)); return effect;
                case "EFFECT_IMMUNE_EFFECT" when value is not null && ImmuneValue(value, fns) is { } whose:
                    effect.Do.Add(Block("passive.immune").With("whose", whose)); return effect;
                case "EFFECT_UPDATE_ATTACK" or "EFFECT_UPDATE_DEFENSE" when value is not null && !Regex.IsMatch(value, @"^-?\d+$") && range is not null
                                                                          && PerCard(value, fns) is { } per:
                    var perBlock = Block("passive.self_stats_per").With("dir", per.Amount < 0 ? "lose" : "gain").With("amount", Math.Abs(per.Amount))
                        .With("stat", code == "EFFECT_UPDATE_ATTACK" ? "atk" : "def").With("per", per.Per);
                    if (per.Counter is { } perCounter) perBlock.WithCounter(perCounter);
                    effect.Do.Add(per.Filter is null ? perBlock : perBlock.With("cards", per.Filter));
                    return effect;
                case "EFFECT_UPDATE_ATTACK" or "EFFECT_UPDATE_DEFENSE" when value is not null && Regex.IsMatch(value, @"^-?\d+$") && range is not null:
                    var n = Int(value);
                    effect.Do.Add(Block("passive.self_stats").With("dir", n < 0 ? "lose" : "gain").With("amount", Math.Abs(n)).With("stat", code == "EFFECT_UPDATE_ATTACK" ? "atk" : "def"));
                    return effect;
            }
        }
        // "You can Normal Summon this card without Tributing", and when.
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_SUMMON_PROC" && !set.ContainsKey("Operation") && set.GetValueOrDefault("Condition") is { } ntcon
            && (value is null or "SUMMON_TYPE_NORMAL"))
        {
            var lines = fns.Get(ntcon).Body.Select(l => l.Trim()).Where(l => l.Length > 0 && l is not "if c==nil then return true end" and not "local tp=c:GetControler()").ToList();
            var text = string.Join(" ", lines).Replace("c:GetControler()", "tp", StringComparison.Ordinal);
            var nm = Regex.Match(text, @"^return minc==0 and c:IsLevelAbove\(5\) and Duel\.GetLocationCount\(tp,LOCATION_MZONE\)>0(?: and (.+))?$");
            if (!nm.Success) throw No($"its Summon without Tributing has a condition the blocks can't say ({Short(text)})");
            if (nm.Groups[1].Success) effect.If.AddRange(ConditionBody("return " + nm.Groups[1].Value, fns));
            effect.Do.Add(Block(NoTributeSummonPassive.BlockId));
            return effect;
        }
        // "This card cannot be used as Synchro Material, except for the Synchro Summon of a WATER monster."
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && Regex.Match(code, @"^EFFECT_CANNOT_BE_(SYNCHRO|XYZ|LINK|FUSION)_MATERIAL$") is { Success: true } mbm && value is not null
            && !set.ContainsKey("Condition"))
        {
            var except = new CardFilter { CardType = FilterCardType.Monster };
            if (value is not ("1" or "aux.TRUE"))
            {
                var vb = Body(value, fns);
                var vm = Regex.Match(vb, @"^if not c then return false end return not (c:\w+\([^()]*\))$");
                if (!vm.Success) throw No($"its material rule is unusual ({Short(vb)})");
                Term(vm.Groups[1].Value, except);
                except.CardType = FilterCardType.Monster;
                except.Zones = Model.Zone.None;
            }
            effect.Do.Add(Block(MaterialBanPassive.BlockId).With("kind", mbm.Groups[1].Value.ToLowerInvariant()).With("cards", except));
            return effect;
        }
        // "Once per turn, if this card would be destroyed by battle, it is not destroyed."
        if (code == "EFFECT_INDESTRUCTABLE_COUNT" && value is not null && IndestructibleCountBy(value, fns) is { } indBy)
        {
            if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && set.GetValueOrDefault("CountLimit") == "1")
            {
                effect.Limit = OncePerTurn.None;
                effect.Do.Add(Block(NotDestroyedOncePassive.BlockId).With("by", indBy).With("cards", new CardFilter { ThisCard = true, Owner = CardOwner.You, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster }));
                return effect;
            }
            if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && set.GetValueOrDefault("TargetRange") is { } itr && !set.ContainsKey("CountLimit"))
            {
                effect.Do.Add(Block(NotDestroyedOncePassive.BlockId).With("by", indBy).With("cards", FieldTargets(itr, set.GetValueOrDefault("Target"), fns)));
                return effect;
            }
        }
        // "You can Normal Summon 1 Fiend monster in addition to your Normal Summon/Set."
        if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && code == "EFFECT_EXTRA_SUMMON_COUNT" && (set.GetValueOrDefault("TargetRange") ?? "").Replace(" ", "") == "LOCATION_HAND+LOCATION_MZONE,0"
            && value is null or "1")
        {
            var who = new CardFilter { Owner = CardOwner.You, Zones = Model.Zone.Hand, CardType = FilterCardType.Monster };
            if (set.GetValueOrDefault("Target") is { } et)
            {
                if (Regex.Match(et, @"^aux\.TargetBoolFunction\((Card\.\w+),([\w+]+)\)$") is { Success: true } tb) Term($"c:{tb.Groups[1].Value[5..]}({tb.Groups[2].Value})", who);
                else foreach (var t in FunctionTerms(et, fns)) Term(t, who);
            }
            who.Zones = Model.Zone.Hand;
            who.CardType = FilterCardType.Monster;
            effect.Do.Add(Block(ExtraSummonPassive.BlockId).With("cards", who));
            return effect;
        }
        // "Your opponent cannot target other monsters you control for attacks."
        if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && code == "EFFECT_CANNOT_SELECT_BATTLE_TARGET" && (set.GetValueOrDefault("TargetRange") ?? "").Replace(" ", "") == "0,LOCATION_MZONE"
            && value is not null)
        {
            var mine = new CardFilter { Owner = CardOwner.You, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster };
            if (value.StartsWith("aux.TargetBoolFunction(", StringComparison.Ordinal))
            {
                var tb = Regex.Match(value, @"^aux\.TargetBoolFunction\((Card\.\w+)(?:,([\w+]+))?\)$");
                if (!tb.Success) throw No("its attack rule picks monsters in a way the blocks don't");
                Term($"c:{tb.Groups[1].Value[5..]}({tb.Groups[2].Value})", mine);
            }
            else
            {
                var vb = Body(value, fns);
                var vr = Regex.Match(vb, @"^return (.+)$");
                if (!vr.Success || vb.Contains(" or ", StringComparison.Ordinal)) throw No($"its attack rule picks monsters in a way the blocks don't ({Short(vb)})");
                foreach (var t in SplitTopLevelAnd(vr.Groups[1].Value).Select(x => x.Trim()))
                {
                    if (t is "c~=e:GetHandler()" or "e:GetHandler()~=c") mine.ExcludeThisCard = true;
                    else Term(t, mine);
                }
            }
            mine.CardType = FilterCardType.Monster;
            mine.Zones = Model.Zone.MonsterZone;
            effect.Do.Add(Block(AttackTargetsPassive.BlockId).With("cards", mine));
            return effect;
        }
        // Field-wide rules for the monsters a filter picks: "Dragon monsters you control gain 500 ATK / can't be targeted...".
        if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && set.GetValueOrDefault("TargetRange") is { } tr
            && FieldBlock(code, value, set, fns) is { } fieldBlock)
        {
            var filter = FieldTargets(tr, set.GetValueOrDefault("Target"), fns);
            effect.Do.Add(fieldBlock.With("cards", filter));
            return effect;
        }
        if (type.SetEquals(["EFFECT_TYPE_EQUIP"]) && !(card.Kind == CardKind.Spell && card.SpellKind != SpellKind.Equip))
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
        // "This card gains 300 ATK for each ...", "monsters you control gain ATK equal to ...": a value worked out live.
        if (LiveStatsPassive(type, code, set, range, props, fns) is { } live)
        {
            effect.Do.Add(live);
            return effect;
        }
        if (MoreLasting(effect, type, code, set, range, fns, card)) return effect;
        if (MechanicsLasting(effect, type, code, set, fns)) return effect;
        throw No($"its lasting effect isn't one the blocks have ({string.Join("+", type.Select(t => t.Replace("EFFECT_TYPE_", "")))} {code})");
    }

    /// <summary>The block for a field-wide rule on monsters, without its card filter; null for other effects.</summary>
    private static BlockInstance? FieldBlock(string code, string? value, Dictionary<string, string> set, Functions fns)
    {
        bool Literal(out int n) { n = 0; return value is not null && Regex.IsMatch(value, @"^-?\d+$") && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n); }
        switch (code)
        {
            case "EFFECT_UPDATE_ATTACK" or "EFFECT_UPDATE_DEFENSE" when Literal(out var n):
                return Block("passive.boost_cards").With("dir", n < 0 ? "lose" : "gain").With("amount", Math.Abs(n)).With("stat", code == "EFFECT_UPDATE_ATTACK" ? "atk" : "def");
            case "EFFECT_CANNOT_BE_EFFECT_TARGET" when value == "aux.tgoval": return Block("passive.cards_rule").With("rule", "untargetable");
            case "EFFECT_CANNOT_BE_EFFECT_TARGET" when value == "1": return Block("passive.cards_rule").With("rule", "untargetable_any");
            case "EFFECT_INDESTRUCTABLE_BATTLE" when value == "1": return Block("passive.protect_cards").With("by", "battle");
            case "EFFECT_INDESTRUCTABLE_EFFECT" when value == "1": return Block("passive.protect_cards").With("by", "effect");
            case "EFFECT_INDESTRUCTABLE_EFFECT" when value == "aux.indoval": return Block("passive.protect_cards").With("by", "opponent_effect");
            case "EFFECT_CANNOT_ATTACK" or "EFFECT_CANNOT_ATTACK_ANNOUNCE" when set.GetValueOrDefault("Property") is null: return Block("passive.cards_rule").With("rule", "cannot_attack");
            case "EFFECT_PIERCE": return Block("passive.cards_rule").With("rule", "pierce");
            case "EFFECT_DISABLE" when !set.ContainsKey("Value"): return Block("passive.cards_rule").With("rule", "negated");
            case "EFFECT_IMMUNE_EFFECT" when value is not null && ImmuneValue(value, fns) == "opponent": return Block("passive.cards_rule").With("rule", "unaffected");
            case "EFFECT_CANNOT_BE_BATTLE_TARGET" when value == "aux.imval1": return Block("passive.cards_rule").With("rule", "not_attack_target");
        }
        return null;
    }

    /// <summary>
    /// The monsters a field effect applies to: its target range ("LOCATION_MZONE,0" is "you control") and its target
    /// function, such as aux.TargetBoolFunction(Card.IsRace,RACE_DRAGON) or one of the script's own.
    /// </summary>
    private static CardFilter FieldTargets(string range, string? target, Functions fns)
    {
        var filter = new CardFilter { CardType = FilterCardType.Monster, Zones = Model.Zone.MonsterZone, FaceUpOnly = true };
        var rm = Regex.Match(range.Replace(" ", ""), @"^(LOCATION_MZONE|LOCATION_SZONE|LOCATION_ONFIELD|0),(LOCATION_MZONE|LOCATION_SZONE|LOCATION_ONFIELD|0)$");
        if (!rm.Success || rm.Groups[1].Value == "0" && rm.Groups[2].Value == "0"
            || rm.Groups[1].Value != "0" && rm.Groups[2].Value != "0" && rm.Groups[1].Value != rm.Groups[2].Value)
            throw No("it affects zones the blocks don't");
        // "Face-down cards in your Spell & Trap Zone": any cards there, not monsters.
        var monsters = (rm.Groups[1].Value is "LOCATION_MZONE" or "0") && (rm.Groups[2].Value is "LOCATION_MZONE" or "0");
        if (!monsters)
        {
            filter.CardType = FilterCardType.Any;
            filter.FaceUpOnly = false;
        }
        Locate(filter, rm.Groups[1].Value, rm.Groups[2].Value);
        if (target is null) return filter;
        var m = Regex.Match(target, @"^aux\.TargetBoolFunction\((Card\.\w+),([\w+]+)\)$");
        if (m.Success)
        {
            Term($"c:{m.Groups[1].Value[5..]}({m.Groups[2].Value})", filter);
            return filter;
        }
        var body = Body(target, fns);
        var r = Regex.Match(body, @"^return (.+)$");
        if (!r.Success || body.Contains(" or ", StringComparison.Ordinal)) throw No($"its lasting effect picks its monsters in a way the blocks don't ({Short(body)})");
        foreach (var raw in SplitTopLevelAnd(r.Groups[1].Value))
        {
            var term = raw.Trim();
            switch (term)
            {
                case "c~=e:GetHandler()" or "e:GetHandler()~=c": filter.ExcludeThisCard = true; continue;
                case "e:GetHandler():GetLinkedGroup():IsContains(c)" or "e:GetHandler():GetLinkedGroup():IsContains(c)": filter.PointedToByThisCard = true; continue;
            }
            Term(term, filter);
        }
        if (monsters)
        {
            filter.CardType = FilterCardType.Monster;
            filter.Zones = Model.Zone.MonsterZone;
        }
        else Locate(filter, rm.Groups[1].Value, rm.Groups[2].Value);
        return filter;
    }

    /// <summary>"300 for each Dragon monster in your GY": a value function that counts cards, times an amount.</summary>
    private static (CardFilter? Filter, string Per, int Amount, int? Counter)? PerCard(string value, Functions fns)
    {
        string body;
        try { body = Body(value, fns).Replace("c:GetControler()", "tp", StringComparison.Ordinal).Replace("e:GetHandler():", "c:", StringComparison.Ordinal); }
        catch (Untranslatable) { return null; }
        Match m;
        // "for each material attached to it", "for each of its Levels", "for each monster it points to".
        if ((m = Regex.Match(body, @"^return c:(GetOverlayCount|GetLevel|GetLinkedGroupCount)\(\)\*(-?\d+)$")).Success)
            return (null, m.Groups[1].Value switch { "GetOverlayCount" => "materials", "GetLevel" => "level", _ => "linked" }, Int(m.Groups[2].Value), null);
        // "for each Spell Counter on it".
        if ((m = Regex.Match(body, $@"^return c:GetCounter\({CounterNumber}\)\*(-?\d+)$")).Success && CounterOf(m.Groups[1].Value) is { } counter)
            return (null, "counters", Int(m.Groups[2].Value), counter);
        // "for each Dragon monster in your GY": a filter, or a Card function with its argument.
        if ((m = Regex.Match(body, @"^return Duel\.GetMatchingGroupCount\(([\w.]+),tp,([\w+]+),([\w+]+),(nil|c|e:GetHandler\(\))(?:,(\w+))?\)\*(-?\d+)$")).Success)
        {
            CardFilter f;
            if (m.Groups[5].Success)
            {
                if (!m.Groups[1].Value.StartsWith("Card.", StringComparison.Ordinal)) return null;
                f = new CardFilter { CardType = FilterCardType.Any };
                try { Term($"c:{m.Groups[1].Value[5..]}({m.Groups[5].Value})", f); } catch (Untranslatable) { return null; }
            }
            else f = Filter(m.Groups[1].Value, fns);
            Locate(f, m.Groups[2].Value, m.Groups[3].Value);
            f.ExcludeThisCard = m.Groups[4].Value != "nil";
            return (f, "cards", Int(m.Groups[6].Value), null);
        }
        m = Regex.Match(body, @"^return Duel\.GetFieldGroupCount\(tp,([\w+]+),([\w+]+)\)\*(-?\d+)$");
        if (m.Success)
        {
            var f = new CardFilter { CardType = FilterCardType.Any };
            Locate(f, m.Groups[1].Value, m.Groups[2].Value);
            if (f.Zones == Model.Zone.MonsterZone) f.CardType = FilterCardType.Monster;
            return (f, "cards", Int(m.Groups[3].Value), null);
        }
        return null;
    }

    /// <summary>The "unaffected by" choice for common EFFECT_IMMUNE_EFFECT value functions.</summary>
    private static string? ImmuneValue(string value, Functions fns)
    {
        string body;
        // Body() writes e:GetHandlerPlayer() as tp; the value function has no tp of its own.
        try { body = Regex.Replace(Body(value, fns).Replace("re:", "te:", StringComparison.Ordinal), @"\btp\b", "e:GetHandlerPlayer()"); } catch (Untranslatable) { return null; }
        return body switch
        {
            "return te:GetOwnerPlayer()~=e:GetHandlerPlayer()" or "return te:GetOwnerPlayer()~=e:GetOwnerPlayer()" or "return e:GetHandlerPlayer()~=te:GetOwnerPlayer()" => "opponent",
            "return te:GetOwner()~=e:GetOwner()" => "other",
            "return te:IsActiveType(TYPE_SPELL+TYPE_TRAP)" => "spelltrap",
            "return te:IsActiveType(TYPE_SPELL)" => "spell",
            "return te:IsActiveType(TYPE_TRAP)" => "trap",
            "return te:IsActiveType(TYPE_MONSTER) and te:GetOwner()~=e:GetOwner()" => "monster",
            "return te:IsActiveType(TYPE_MONSTER) and te:GetOwnerPlayer()~=e:GetHandlerPlayer()" => "opp_monster",
            "return te:IsActiveType(TYPE_SPELL) and te:GetOwnerPlayer()~=e:GetHandlerPlayer()" => "opp_spell",
            "return te:IsActiveType(TYPE_TRAP) and te:GetOwnerPlayer()~=e:GetHandlerPlayer()" => "opp_trap",
            "return te:GetOwnerPlayer()~=e:GetHandlerPlayer() and te:IsActivated()" or "return e:GetHandlerPlayer()~=te:GetOwnerPlayer() and te:IsActivated()" => "opp_activated",
            "return te:IsActiveType(TYPE_LINK) and te:IsActivated() and te:GetOwner()~=e:GetOwner()" => "other_link",
            "if te:IsActiveType(TYPE_SPELL+TYPE_TRAP) then return true else return te:IsActiveType(TYPE_LINK) and te:IsActivated() and te:GetOwner()~=e:GetOwner() end" => "spelltrap_other_link",
            _ => ImmuneWhose(body),
        };
    }

    /// <summary>
    /// "Place it face-up in your Spell &amp; Trap Zone as a Continuous Spell, instead of sending it to the GY": the redirect's
    /// check is that this face-up monster is destroyed in the Monster Zone, and it only makes the card a Continuous Spell.
    /// </summary>
    private static bool PlacedInstead(Dictionary<string, string> set, Functions fns)
    {
        if (set.GetValueOrDefault("Condition") is not { } con || set.GetValueOrDefault("Operation") is not { } op) return false;
        string body, operation;
        try { (body, operation) = (Body(con, fns), Body(op, fns)); } catch (Untranslatable) { return false; }
        if (body != "return c:IsFaceup() and c:IsLocation(LOCATION_MZONE) and c:IsReason(REASON_DESTROY)") return false;
        return operation.Contains("e1:SetCode(EFFECT_CHANGE_TYPE)", StringComparison.Ordinal) && operation.Contains("e1:SetValue(TYPE_SPELL+TYPE_CONTINUOUS)", StringComparison.Ordinal)
               && !operation.Contains("Duel.", StringComparison.Ordinal) && Regex.Matches(operation, @"RegisterEffect\(").Count == 1;
    }

    /// <summary>"Cannot be destroyed by Spell/Trap effects" and the like: the block's choice for a value function, or null.</summary>
    private static string? IndestructibleEffectBy(string value, Functions fns)
    {
        string body;
        try { body = Regex.Replace(Body(value, fns).Replace("te:", "re:", StringComparison.Ordinal), @"\btp\b", "e:GetHandlerPlayer()"); } catch (Untranslatable) { return null; }
        return body switch
        {
            "return re:IsActiveType(TYPE_SPELL+TYPE_TRAP)" => "spelltrap_effect",
            "return re:IsActiveType(TYPE_MONSTER)" => "monster_effect",
            "return rp==1-e:GetHandlerPlayer()" or "return rp~=e:GetHandlerPlayer()" or "return e:GetHandlerPlayer()~=rp" => "opponent_effect",
            _ => null,
        };
    }

    /// <summary>What an "it is not destroyed (once)" value function protects from, as the block's choice, or null.</summary>
    private static string? IndestructibleCountBy(string value, Functions fns)
    {
        string body;
        try { body = Regex.Replace(Body(value, fns), @"\btp\b", "e:GetHandlerPlayer()"); } catch (Untranslatable) { return null; }
        body = Regex.Replace(body, @"bit\.band\(r,([\w+]+)\)", "r&$1");
        var m = Regex.Match(body, @"^(?:return (.+)|if (.+) then return (?:1|true) else return (?:0|false) end)$");
        if (!m.Success) return null;
        var test = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Replace("(", "").Replace(")", "").Replace(" ", "");
        return test switch
        {
            "r&REASON_BATTLE~=0" or "r&REASON_BATTLE>0" => "battle",
            "r&REASON_EFFECT~=0" or "r&REASON_EFFECT>0" => "effect",
            "r&REASON_BATTLE+REASON_EFFECT~=0" or "r&REASON_EFFECT+REASON_BATTLE~=0" or "r&REASON_BATTLE+REASON_EFFECT>0" => "both",
            "r&REASON_EFFECT~=0andrp==1-e:GetHandlerPlayer" or "r&REASON_EFFECT>0andrp==1-e:GetHandlerPlayer" => "opponent_effect",
            _ => null,
        };
    }

    /// <summary>A Special Summon condition written out, as the Card tab's summoning rule, or null.</summary>
    private static SummonRule? SummonConditionRule(string value, Functions fns)
    {
        string body;
        try { body = Body(value, fns); } catch (Untranslatable) { return null; }
        const string kind = @"(?:bit\.band\(st,SUMMON_TYPE_(?:FUSION|SYNCHRO|XYZ|LINK|RITUAL)\)==SUMMON_TYPE_(?:FUSION|SYNCHRO|XYZ|LINK|RITUAL)|st&SUMMON_TYPE_(?:FUSION|SYNCHRO|XYZ|LINK|RITUAL)==SUMMON_TYPE_(?:FUSION|SYNCHRO|XYZ|LINK|RITUAL)|aux\.(?:fus|syn|xyz|link|rit)limit\(e,se,sp,st\))";
        const string notExtra = @"(?:not e:GetHandler\(\):IsLocation\(LOCATION_EXTRA\)|e:GetHandler\(\):GetLocation\(\)~=LOCATION_EXTRA)";
        // "Must be Fusion Summoned": only that way.
        if (Regex.IsMatch(body, $"^return {kind}$")) return SummonRule.ProcedureOnly;
        // "Must first be Special Summoned (from your Extra Deck) ...": never from the Extra Deck except its own way, then free.
        if (Regex.IsMatch(body, $"^return {notExtra}(?: or {kind})?$")) return SummonRule.SpecialSummonOnly;
        // "Must be Special Summoned by a card effect" (Machina Citadel).
        if (body == "return se:IsHasType(EFFECT_TYPE_ACTIONS)") return SummonRule.ByCardEffect;
        return null;
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

    /// <summary>
    /// "Duel.IsExistingMatchingCard(Card.IsType,tp,LOCATION_GRAVE,0,1,nil,TYPE_TRAP)" and the like as an IF block: "you have a Trap in your GY".
    /// Null when the check isn't one of those.
    /// </summary>
    private static BlockInstance? ExistsCheck(string term, Functions fns)
    {
        var m = Regex.Match(term, @"^(not )?Duel\.IsExistingMatchingCard\(([\w.]+),tp,([\w+|]+),([\w+|]+),(\d+),(nil|e:GetHandler\(\)|c)(?:,(.+))?\)$");
        bool none;
        int n;
        string fn, mine, theirs, exclude, extra;
        if (m.Success)
        {
            (none, fn, mine, theirs, n, exclude, extra) = (m.Groups[1].Success, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, Int(m.Groups[5].Value), m.Groups[6].Value, m.Groups[7].Value);
        }
        else if ((m = Regex.Match(term, @"^Duel\.GetMatchingGroupCount\(([\w.]+),tp,([\w+|]+),([\w+|]+),(nil|e:GetHandler\(\)|c)(?:,(.+))?\)(==0|>0|~=0|>=(\d+))$")).Success)
        {
            (fn, mine, theirs, exclude, extra) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value);
            none = m.Groups[6].Value == "==0";
            n = m.Groups[7].Success ? Int(m.Groups[7].Value) : 1;
        }
        // "exactly 2", "2 or fewer": a count compared.
        else if ((m = Regex.Match(term, @"^Duel\.GetMatchingGroupCount\(([\w.]+),tp,([\w+|]+),([\w+|]+),(nil|e:GetHandler\(\)|c)(?:,(.+))?\)(==|<=|<|>)(\d+)$")).Success
            && CountBlock(m.Groups[6].Value, Int(m.Groups[7].Value)) is { } counted)
        {
            CardFilter cf;
            var cextra = m.Groups[5].Value;
            if (cextra is "" or "e,tp" or "tp" or "e,tp,c") cf = Filter(m.Groups[1].Value, fns);
            else if (m.Groups[1].Value.StartsWith("Card.", StringComparison.Ordinal) && !cextra.Contains(','))
            {
                cf = new CardFilter { CardType = FilterCardType.Any };
                Term($"c:{m.Groups[1].Value[5..]}({cextra})", cf);
            }
            else return null;
            Locate(cf, m.Groups[2].Value.Replace('|', '+'), m.Groups[3].Value.Replace('|', '+'));
            cf.ExcludeThisCard = m.Groups[4].Value != "nil";
            return counted.With("cards", cf);
        }
        else return null;
        CardFilter f;
        if (extra is "" or "e,tp" or "tp" or "e,tp,c") f = Filter(fn, fns);
        else if (fn.StartsWith("Card.", StringComparison.Ordinal) && !extra.Contains(','))
        {
            f = new CardFilter { CardType = FilterCardType.Any };
            Term($"c:{fn[5..]}({extra})", f);
        }
        else return null;
        Locate(f, mine.Replace('|', '+'), theirs.Replace('|', '+'));
        f.ExcludeThisCard = exclude != "nil";
        if (none) return n == 1 ? Block("if.no_cards").With("cards", f) : null;
        return Block("if.cards_exist").With("count", n).With("cards", f);
    }

    /// <summary>A count compared with a number, as a cards-exist or no-cards block without its cards; null when it can't be said.</summary>
    private static BlockInstance? CountBlock(string op, int n) => (op, n) switch
    {
        ("==", 0) or ("<", 1) or ("<=", 0) => Block("if.no_cards"),
        ("==", > 0) => Block("if.cards_exist").With("cmp", "exactly").With("count", n),
        ("<=", > 0) => Block("if.cards_exist").With("cmp", "at_most").With("count", n),
        ("<", > 1) => Block("if.cards_exist").With("cmp", "at_most").With("count", n - 1),
        (">", >= 0) => Block("if.cards_exist").With("count", n + 1),
        (">=", > 0) => Block("if.cards_exist").With("count", n),
        _ => null,
    };

    [ThreadStatic] private static string? _chainCounter;

    /// <summary>What the script's activity counter counts, as a choice of "your opponent has activated ... this turn".</summary>
    private static (string What, string During)? ChainCounterKind() => _chainCounter switch
    {
        "local loc=Duel.GetChainInfo(cid,CHAININFO_TRIGGERING_LOCATION) return not (re:IsActiveType(TYPE_MONSTER) and loc&(LOCATION_HAND|LOCATION_GRAVE)>0)"
            or "local loc=Duel.GetChainInfo(cid,CHAININFO_TRIGGERING_LOCATION) return not (re:IsActiveType(TYPE_MONSTER) and loc&(LOCATION_HAND+LOCATION_GRAVE)>0)" => ("monster_hand_gy", "any"),
        "return not re:IsActiveType(TYPE_MONSTER)" => ("monster", "any"),
        "return not re:IsHasType(EFFECT_TYPE_ACTIVATE)" => ("spelltrap", "any"),
        "return false" => ("any", "any"),
        // Triple Tactics Talent: a monster effect during a Main Phase.
        "local ph=Duel.GetCurrentPhase() return not (re:IsActiveType(TYPE_MONSTER) and (ph==PHASE_MAIN1 or ph==PHASE_MAIN2))" => ("monster", "main"),
        _ => null,
    };

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
            "LOCATION_PZONE" => Model.Zone.PendulumZone,
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
        if (f.CardType == FilterCardType.SpellTrap && (f.Zones & ~Model.Zone.Field) == 0)
        {
            // On the field, Spells and Traps are exactly the cards in the Spell & Trap Zone.
            f.Zones = Model.Zone.SpellTrapZone;
        }
    }

    private static CardFilter Filter(string reference, Functions fns)
    {
        var f = new CardFilter();
        reference = reference.Trim();
        var unwrap = Regex.Match(reference, @"^aux\.NecroValleyFilter\(([\w.]+)\)$");
        if (unwrap.Success) reference = unwrap.Groups[1].Value;
        if (KnownFilter(reference, f, fns)) return f;
        if (reference is "nil" or "aux.TRUE" or "Card.IsAbleToHand" or "Card.IsAbleToGrave" or "Card.IsAbleToRemove" or "Card.IsAbleToDeck"
            or "Card.IsDestructable" or "Card.IsDiscardable" or "Card.IsAbleToHandAsCost" or "Card.IsAbleToGraveAsCost")
            return f;
        if (reference == "Card.IsFaceup") { f.FaceUpOnly = true; return f; }
        // Card.IsFacedown, Card.IsAttackPos, Card.IsCanChangePosition...: one check as a function.
        if (Regex.IsMatch(reference, @"^Card\.\w+$") && reference is not ("Card.IsCode" or "Card.IsSetCard" or "Card.IsType" or "Card.IsRace" or "Card.IsAttribute"))
        {
            Term($"c:{reference[5..]}()", f);
            return f;
        }
        // "A face-up monster whose effects can be negated."
        if (reference == "aux.NegateMonsterFilter") { f.FaceUpOnly = true; f.CardType = FilterCardType.Monster; return f; }
        var (pars, body) = fns.Get(reference);
        var text = string.Join(" ", body.Select(l => l.Trim()));
        // "(c:IsAbleToDeckAsCost() or c:IsAbleToExtraAsCost())": whether it can be moved, which the blocks check.
        text = Regex.Replace(text, @"\(c:IsAbleTo\w+\(\) or c:IsAbleTo\w+\(\)\)( and )?", "");
        // A check passed in as a parameter ("f(c)" with f=Card.IsAbleToGraveAsCost) and room to Summon.
        foreach (var par in pars.Skip(1)) text = Regex.Replace(text, $@"(?: and )?\b{Regex.Escape(par)}\(c\)", "");
        text = Regex.Replace(text, @"(?: and )?Duel\.GetMZoneCount\(tp,c\)>0", "");
        // "not immune to this effect", which the game checks anyway when it moves the card.
        text = Regex.Replace(text, @"(?: and )?\(not e or not c:IsImmuneToEffect\(e\)\)|(?: and )?not c:IsImmuneToEffect\(e\)", "").Replace("return  and ", "return ").Replace("return and ", "return ");
        if (text.Trim() == "return") return f;
        text = InlineLocals(text);
        var m = Regex.Match(text, @"^return (.+)$");
        if (!m.Success || !Conjunction(m.Groups[1].Value, f)) throw No($"it picks cards in a way the blocks can't ({Short(Regex.Replace(text, @"c\d+\.|s\.", "s."))})");
        return f;
    }

    private static void Term(string term, CardFilter f)
    {
        Match m;
        if (CounterTerm(term, f)) return;
        var original = term;
        // Materials are checked with their material forms (IsLinkRace, IsFusionSetCard...); the filter says what they must be.
        term = Regex.Replace(term, @"^(not )?c:Is(?:Fusion|Link|Synchro|Xyz)(Type|Attribute|Race|SetCard|Code)\(", "$1c:Is$2(");
        if (Overwrites(term, original, f)) return;
        if (Regex.IsMatch(term, @"^c:(IsAbleTo\w+|IsDiscardable|IsDestructable|IsSSetable|IsCanBeSpecialSummoned|IsCanChangePosition|IsCanTurnSet|IsSummonable|IsMSetable|IsControlerCanBeChanged|IsCanOverlay|IsReleasable|IsReleasableByEffect|IsCanBeFusionMaterial|IsCanBeSynchroMaterial|IsCanBeXyzMaterial|IsCanBeLinkMaterial|IsCanBeRitualMaterial|IsCanAddCounter)\(.*\)$")) return;
        // The checks the blocks make themselves: "can be placed", "isn't a Token you can't move".
        if (term is "not c:IsForbidden()" or "c:IsCanBeEffectTarget(e)" or "Duel.GetLocationCountFromEx(tp,tp,nil,c)>0" or "Duel.GetLocationCountFromEx(tp)>0") return;
        Match nm;
        // How it was Summoned, and its battle position.
        switch (term)
        {
            case "c:IsSummonLocation(LOCATION_EXTRA)": f.Summoned = FilterSummoned.FromExtra; f.CardType = FilterCardType.Monster; return;
            case "c:IsSummonType(SUMMON_TYPE_SPECIAL)": f.Summoned = FilterSummoned.Special; f.CardType = FilterCardType.Monster; return;
            case "c:IsSummonType(SUMMON_TYPE_ADVANCE)": f.Summoned = FilterSummoned.Tribute; f.CardType = FilterCardType.Monster; return;
            case "e:GetHandler():GetColumnGroup():IsContains(c)": f.InThisColumn = true; return;
            case "c:IsAttackPos()" or "c:IsPosition(POS_ATTACK)": f.Position = FilterPosition.Attack; return;
            case "c:IsDefensePos()" or "c:IsPosition(POS_DEFENSE)": f.Position = FilterPosition.Defense; return;
            case "c:IsPosition(POS_FACEUP_ATTACK)": f.Position = FilterPosition.FaceUpAttack; return;
            case "c:IsPosition(POS_FACEUP_DEFENSE)": f.Position = FilterPosition.FaceUpDefense; return;
            case "c:IsPosition(POS_FACEDOWN_DEFENSE)": f.Position = FilterPosition.FaceDownDefense; return;
            case "not c:IsPublic()": f.NotRevealed = true; return;
            case "c:IsType(TYPE_EQUIP)": f.SpellTrapKind = SpellTrapKind.Equip; if (f.CardType == FilterCardType.Any) f.CardType = FilterCardType.Spell; return;
            case "c:IsType(TYPE_FIELD)": f.SpellTrapKind = SpellTrapKind.Field; if (f.CardType == FilterCardType.Any) f.CardType = FilterCardType.Spell; return;
            case "c:IsType(TYPE_QUICKPLAY)": f.SpellTrapKind = SpellTrapKind.QuickPlay; if (f.CardType == FilterCardType.Any) f.CardType = FilterCardType.Spell; return;
            case "c:IsType(TYPE_COUNTER)": f.SpellTrapKind = SpellTrapKind.Counter; if (f.CardType == FilterCardType.Any) f.CardType = FilterCardType.Trap; return;
            case "c:IsType(TYPE_CONTINUOUS)": f.SpellTrapKind = SpellTrapKind.Continuous; if (f.CardType == FilterCardType.Any) f.CardType = FilterCardType.SpellTrap; return;
            case "c:GetType()==TYPE_SPELL+TYPE_CONTINUOUS" or "c:GetType()==TYPE_CONTINUOUS+TYPE_SPELL": f.SpellTrapKind = SpellTrapKind.Continuous; f.CardType = FilterCardType.Spell; return;
            case "c:GetType()==TYPE_TRAP+TYPE_CONTINUOUS" or "c:GetType()==TYPE_CONTINUOUS+TYPE_TRAP": f.SpellTrapKind = SpellTrapKind.Continuous; f.CardType = FilterCardType.Trap; return;
            case "c:GetType()==TYPE_SPELL": f.SpellTrapKind = SpellTrapKind.Normal; f.CardType = FilterCardType.Spell; return;
            case "c:GetType()==TYPE_TRAP": f.SpellTrapKind = SpellTrapKind.Normal; f.CardType = FilterCardType.Trap; return;
        }
        // ""Dark Magician" or "Dark Magician Girl"".
        if ((nm = Regex.Match(term, @"^c:IsCode\((\d+(?:,\d+)+)\)$")).Success)
        {
            var codes = nm.Groups[1].Value.Split(',').Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            f.CardCode = codes[0];
            f.AltCodes = codes[1..];
            return;
        }
        // "a card that mentions "Dark Magician"".
        if ((nm = Regex.Match(term, @"^aux\.IsCode(Or)?Listed\(c,(\d+)\)$")).Success)
        {
            f.MentionsCode = long.Parse(nm.Groups[2].Value, CultureInfo.InvariantCulture);
            f.MentionsOrIs = nm.Groups[1].Success;
            return;
        }
        if ((nm = Regex.Match(term, @"^c:Is(Attack|Defense)\((\d+)\)$")).Success)
        {
            var v = Int(nm.Groups[2].Value);
            if (nm.Groups[1].Value == "Attack") f.AtkMin = f.AtkMax = v; else f.DefMin = f.DefMax = v;
            f.CardType = FilterCardType.Monster;
            return;
        }
        if ((nm = Regex.Match(term, @"^c:IsDefense(Above|Below)\((\d+)\)$")).Success)
        {
            if (nm.Groups[1].Value == "Above") f.DefMin = Int(nm.Groups[2].Value); else f.DefMax = Int(nm.Groups[2].Value);
            f.CardType = FilterCardType.Monster;
            return;
        }
        if (term == "c:IsFaceup()") { f.FaceUpOnly = true; return; }
        // "Tribute 1 monster" as a cost in a chain: the engine's own check that it isn't already destroyed.
        if (term == "not c:IsStatus(STATUS_BATTLE_DESTROYED)") return;
        if (term == "c:IsFacedown()") { f.FaceDownOnly = true; f.FaceUpOnly = false; return; }
        // Not the Extra Monster Zone or the Field Zone: SettleRequirements says it in the words of the filter's zones.
        if (term == "c:GetSequence()<5") { f.Requirements.Add(new() { Property = "main_zone", Op = RequirementOp.Is }); return; }
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
            f.CardType = m.Groups[1].Value switch { "TYPE_MONSTER" => FilterCardType.Monster, "TYPE_SPELL" => FilterCardType.Spell, "TYPE_TRAP" => FilterCardType.Trap, _ => FilterCardType.SpellTrap };
            return;
        }
        if ((m = Regex.Match(term, @"^c:IsLevelBelow\((\d+)\)$")).Success) { f.LevelMax = Int(m.Groups[1].Value); f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsLevelAbove\((\d+)\)$")).Success) { f.LevelMin = Int(m.Groups[1].Value); f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsRank(Above|Below)?\((\d+)\)$")).Success)
        {
            var rank = Int(m.Groups[2].Value);
            if (m.Groups[1].Value != "Below") f.LevelMin = rank;
            if (m.Groups[1].Value != "Above") f.LevelMax = rank;
            f.MonsterKind = MonsterKind.Xyz;
            f.CardType = FilterCardType.Monster;
            return;
        }
        if ((m = Regex.Match(term, @"^c:IsLevel\((\d+)\)$")).Success) { f.LevelMin = f.LevelMax = Int(m.Groups[1].Value); f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsAttackBelow\((\d+)\)$")).Success) { f.AtkMax = Int(m.Groups[1].Value); f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsAttackAbove\((\d+)\)$")).Success) { f.AtkMin = Int(m.Groups[1].Value); f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsRace\((\w+)\)$")).Success && Races.TryGetValue(m.Groups[1].Value, out var race)) { f.Race = race; f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsAttribute\((\w+)\)$")).Success && Attributes.TryGetValue(m.Groups[1].Value, out var attr)) { f.Attribute = attr; f.CardType = FilterCardType.Monster; return; }
        if ((m = Regex.Match(term, @"^c:IsSetCard\(0x([0-9a-fA-F]+)\)$")).Success) { f.Archetype = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture); return; }
        // ""Dominators" or "Dominus Purge"": either archetype.
        if ((m = Regex.Match(term, @"^c:IsSetCard\(0x([0-9a-fA-F]+),0x([0-9a-fA-F]+)\)$")).Success || (m = Regex.Match(term, @"^\(c:IsSetCard\(0x([0-9a-fA-F]+)\) or c:IsSetCard\(0x([0-9a-fA-F]+)\)\)$")).Success)
        {
            f.Archetype = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            f.Archetype2 = int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return;
        }
        if ((m = Regex.Match(term, @"^c:IsCode\((\d+)\)$")).Success) { f.CardCode = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); return; }
        if (RequirementTerm(term, original, f)) return;
        if (DeclaredTerm(term, f)) return;
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
