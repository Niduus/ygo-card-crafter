using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Ritual and Fusion Summons in official scripts: the engine's Ritual helpers (aux.AddRitualProcGreaterCode and the
/// rest), Polymerization-style operations whose materials come from the hand, field, GY, banishment, Deck or the
/// opponent's field, Contact Fusion procedures, and the Fusion Material procedures with a repeating material.
/// Each is read as the Ritual Summon or Fusion Summon block, or the Card tab's Summoning conditions, only when they
/// do exactly the same.
/// </summary>
public static partial class ScriptTranslator
{
    /// <summary>The Ritual Summon blocks of "local e1=aux.AddRitualProc...(...)" effects, read back by <see cref="RitualOf"/>.</summary>
    [ThreadStatic] private static List<BlockInstance>? _ritualCalls;

    private const string RitualTarget = "aux.CardCrafterRitualTarget";

    // ---------------------------------------------------------------- statements of initial_effect

    /// <summary>Ritual Spells' helpers, Contact Fusion procedures and Fusion Material procedures; null for other lines.</summary>
    private static TranslatedPiece? RitualFusionStatement(string line, Functions fns)
    {
        // "aux.AddRitualProcGreaterCode(c,5405694)": the card's activation, a Ritual Summon.
        if (Regex.IsMatch(line, @"^aux\.AddRitualProc\w+\(c,.*\)$"))
        {
            var (block, pause) = RitualProc(line, fns);
            if (pause) throw No("it makes an effect it never applies");
            return new TranslatedPiece { Piece = null!, Effect = new EffectEvent { Label = "Ritual Summon", When = Block("when.activate"), Do = [block] } };
        }
        if (line.StartsWith("aux.AddContactFusionProcedure(c,", StringComparison.Ordinal)) return ContactProcedure(line);
        var m = Regex.Match(line, @"^aux\.(AddFusionProcCodeFunRep|AddFusionProcCode2FunRep|AddFusionProcFunFunRep|AddFusionProcFunFun|AddFusionProcCodeRep2)\(c,(.*)\)$");
        if (m.Success) return new TranslatedPiece { Piece = null!, Procedure = RepeatingFusion(m.Groups[1].Value, SplitArgs(m.Groups[2].Value), fns) };
        return null;
    }

    /// <summary>
    /// "aux.AddRitualProcGreater2(c,filter,summon_location,grave_filter,mat_filter,pause,extra_operation,extra_target)"
    /// and its Code, Code2 and original-Level ("Greater", "Equal") forms, as the Ritual Summon block.
    /// </summary>
    private static (BlockInstance Block, bool Pause) RitualProc(string call, Functions fns)
    {
        var m = Regex.Match(call, @"^aux\.AddRitualProc(Greater|Equal)(2)?(Code2|Code)?\(c,(.*)\)$");
        if (!m.Success) throw No($"its Ritual Summon is written in a way the blocks can't ({Short(call)})");
        var args = SplitArgs(m.Groups[4].Value);
        var codes = m.Groups[3].Value switch { "Code2" => 2, "Code" => 1, _ => 0 };
        // Code2 exists only with the Ritual Monster's current Level.
        if (codes == 2 && !m.Groups[2].Success) throw No("its Ritual Summon is written in a way the blocks can't");
        var ritual = new CardFilter { Owner = CardOwner.You, Zones = Model.Zone.Hand, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Ritual };
        if (args.Count < Math.Max(1, codes)) throw No("its Ritual Summon is written in a way the blocks can't");
        if (codes > 0)
        {
            if (!args.Take(codes).All(a => Regex.IsMatch(a, @"^\d+$"))) throw No("its Ritual Monster is named in a way the blocks can't");
            ritual.CardCode = Int64(args[0]);
            if (codes == 2) ritual.AltCodes = [Int64(args[1])];
        }
        else FilterTerms(args[0], ritual, fns);
        ritual.MonsterKind = MonsterKind.Ritual;
        ritual.CardType = FilterCardType.Monster;
        var first = Math.Max(1, codes);
        string Arg(int k) => args.Count > first + k ? args[first + k] : "nil";
        if (args.Count > first + 6) throw No("its Ritual Summon is written in a way the blocks can't");
        if (Arg(4) != "nil" || Arg(5) != "nil") throw No("its Ritual Summon does more than the block (an extra operation or target)");

        ritual.Zones = Model.Zone.None;
        var location = Arg(0) == "nil" ? "LOCATION_HAND" : Arg(0);
        foreach (var part in location.Split('+'))
            ritual.Zones |= part switch
            {
                "LOCATION_HAND" => Model.Zone.Hand,
                "LOCATION_DECK" => Model.Zone.Deck,
                "LOCATION_GRAVE" => Model.Zone.Graveyard,
                _ => throw No($"its Ritual Monster comes from {Short(location)}"),
            };

        // The materials: Tributes the mat_filter allows ("aux.FALSE": none), and monsters banished from the GY the grave_filter allows.
        var mats = new CardFilter { CardType = FilterCardType.Monster };
        var gyMats = new CardFilter { CardType = FilterCardType.Monster };
        var grave = Arg(1);
        var mat = Arg(2);
        var source = "hand_field";
        if (mat == "aux.FALSE")
        {
            if (grave == "nil") throw No("its Ritual Summon has no materials");
            source = "gy";
            FilterTerms(grave, mats, fns);
        }
        else
        {
            FilterTerms(mat, mats, fns, term =>
            {
                switch (term)
                {
                    case "c:IsLocation(LOCATION_MZONE)" or "c:IsOnField()": source = "field"; return true;
                    case "c:IsLocation(LOCATION_HAND)": source = "hand"; return true;
                    // The mat_filter is also told whether the check is the activation's ("chk"): only plain filters translate.
                    default: return false;
                }
            });
            if (grave != "nil")
            {
                if (source != "hand_field") throw No("its Ritual Summon takes materials from more places than the block");
                source = "hand_field_gy";
                FilterTerms(grave, gyMats, fns);
            }
        }
        var block = Block(RitualSummonAction.BlockId).With("cards", ritual).With("mats", mats).With("source", source)
            .With("how", m.Groups[1].Value == "Greater" ? "greater" : "equal").With("stat", m.Groups[2].Success ? "level" : "original");
        if (source == "hand_field_gy") block = block.With("mats2", gyMats);
        return (block, Arg(3) == "true");
    }

    /// <summary>A filter argument of a helper: nil, aux.TRUE, aux.FilterBoolFunction(Card.IsSetCard,0x17c) or a function of the card.</summary>
    private static void FilterTerms(string expr, CardFilter f, Functions fns, Func<string, bool>? special = null)
    {
        if (expr is "nil" or "aux.TRUE") return;
        var fb = Regex.Match(expr, @"^aux\.FilterBoolFunction\(Card\.(\w+),([\w+]+)\)$");
        if (fb.Success)
        {
            Term($"c:{fb.Groups[1].Value}({fb.Groups[2].Value})", f);
            return;
        }
        if (!Regex.IsMatch(expr, @"^[\w]+\.\w+$")) throw No($"its Ritual Summon picks cards by {Short(expr)}");
        var (pars, _) = fns.Get(expr);
        foreach (var term in FunctionTerms(expr, fns))
        {
            if (special?.Invoke(term) == true) continue;
            // A check of the effect or the player passed in: not a plain card filter.
            if (pars.Skip(1).Any(p => Regex.IsMatch(term, $@"\b{Regex.Escape(p)}\b"))) throw No($"its Ritual Summon picks cards by {Short(term)}");
            Term(term, f);
        }
    }

    /// <summary>
    /// "local e1=aux.AddRitualProcGreater2(c,...,true)" followed by e1:Set...: the helper's settings, with the Ritual
    /// Summon as a marker <see cref="RitualOf"/> reads. Also whether the helper registered the effect already.
    /// </summary>
    private static (Dictionary<string, string> Set, bool Registered) RitualProcEffect(string call, Functions fns)
    {
        var (block, pause) = RitualProc(call, fns);
        (_ritualCalls ??= []).Add(block);
        var set = new Dictionary<string, string>
        {
            ["Category"] = "CATEGORY_SPECIAL_SUMMON",
            ["Type"] = "EFFECT_TYPE_ACTIVATE",
            ["Code"] = "EVENT_FREE_CHAIN",
            ["Target"] = RitualTarget,
            ["Operation"] = $"aux.CardCrafterRitual({_ritualCalls.Count - 1})",
        };
        return (set, !pause);
    }

    /// <summary>The Ritual Summon block of an effect made by a Ritual helper, or null.</summary>
    private static BlockInstance? RitualOf(Dictionary<string, string> set)
    {
        if (!set.TryGetValue("Operation", out var op) || Regex.Match(op, @"^aux\.CardCrafterRitual\((\d+)\)$") is not { Success: true } m) return null;
        if (set.GetValueOrDefault("Target") != RitualTarget) throw No("its Ritual Summon is given another target step");
        return PackSerializer.Clone(_ritualCalls![Int(m.Groups[1].Value)]);
    }

    // ---------------------------------------------------------------- Contact Fusion

    /// <summary>
    /// "aux.AddContactFusionProcedure(c,Card.IsAbleToDeckOrExtraAsCost,LOCATION_ONFIELD,0,aux.ContactFusionSendToDeck(c))":
    /// the Summoning conditions' Contact Fusion.
    /// </summary>
    private static TranslatedPiece ContactProcedure(string line)
    {
        var m = Regex.Match(line, @"^aux\.AddContactFusionProcedure\(c,(.*)\)$");
        if (!m.Success) throw No($"its Contact Fusion is written in a way the blocks can't ({Short(line)})");
        var args = SplitArgs(m.Groups[1].Value);
        if (args.Count < 4) throw No("its Contact Fusion is written in a way the blocks can't");
        var how = string.Join(",", args.Skip(3));
        var move = (args[0], how) switch
        {
            ("Card.IsAbleToDeckOrExtraAsCost", "aux.ContactFusionSendToDeck(c)") => ContactFusionMove.ShuffleIntoDeck,
            ("Card.IsAbleToRemoveAsCost", "Duel.Remove,POS_FACEUP,REASON_COST") => ContactFusionMove.Banish,
            ("Card.IsAbleToGraveAsCost", "Duel.SendtoGrave,REASON_COST") => ContactFusionMove.SendToGrave,
            ("aux.FilterBoolFunction(Card.IsReleasable,REASON_SPSUMMON)", "Duel.Release,REASON_SPSUMMON+REASON_MATERIAL") => ContactFusionMove.Tribute,
            _ => throw No($"its Contact Fusion moves or picks its materials in a way the blocks can't ({Short(args[0] + ", " + how)})"),
        };
        var zones = Model.Zone.None;
        foreach (var part in args[1].Split('+'))
            zones |= part switch
            {
                "LOCATION_MZONE" => Model.Zone.MonsterZone,
                "LOCATION_ONFIELD" => Model.Zone.Field,
                "LOCATION_HAND" => Model.Zone.Hand,
                "LOCATION_GRAVE" => Model.Zone.Graveyard,
                _ => throw No($"its Contact Fusion takes materials from {Short(args[1])}"),
            };
        if ((zones & Model.Zone.Field) == Model.Zone.None) throw No($"its Contact Fusion takes materials from {Short(args[1])}");
        var fieldPart = Lua.Locations(zones & Model.Zone.Field);
        var opponent = args[2] switch
        {
            "0" => false,
            var o when o == fieldPart => true,
            _ => throw No($"its Contact Fusion takes the opponent's cards from {Short(args[2])}"),
        };
        return new TranslatedPiece
        {
            Piece = null!,
            ProcedureSetting = p =>
            {
                p.Contact = true;
                p.ContactMove = move;
                p.ContactZones = zones;
                p.ContactOpponent = opponent;
            },
        };
    }

    /// <summary>
    /// A Contact Fusion Monster's "Must first be either Fusion Summoned, or Special Summoned ..." is written
    /// "not e:GetHandler():IsLocation(LOCATION_EXTRA) or aux.fuslimit(...)": the Card tab's usual rule, while the same
    /// test alone ("You do not use "Polymerization"") is its "must first be Special Summoned" rule.
    /// </summary>
    private static void ContactFusionRules(TranslationResult result)
    {
        if (!result.Pieces.Any(p => p.ProcedureSetting is not null)) return;
        for (int i = 0; i < result.Pieces.Count; i++)
        {
            var p = result.Pieces[i];
            if (p.Rule != SummonRule.SpecialSummonOnly || p.Piece is null) continue;
            var text = Regex.Replace(string.Join("\n", p.Piece.Functions), @"\s+", " ");
            if (Regex.IsMatch(text, @"(?:not e:GetHandler\(\):IsLocation\(LOCATION_EXTRA\)|e:GetHandler\(\):GetLocation\(\)~=LOCATION_EXTRA) or "))
                result.Pieces[i] = p with { Rule = SummonRule.Normal };
        }
    }

    /// <summary>
    /// "aux.AddFusionProcCodeFunRep(c,code,f,minc,maxc,sub,insf)" and the like: a named or filtered material plus one
    /// that repeats, as the Summoning conditions' material slots.
    /// </summary>
    private static SummonProcedure RepeatingFusion(string helper, List<string> args, Functions fns)
    {
        int Num(int i) => args.Count > i && Regex.IsMatch(args[i], @"^\d+$") ? Int(args[i]) : throw No("its material count is unusual");
        MaterialSlot Slot(string expr, int min = 1, int max = 1) => new() { Filter = Material(expr, fns), Min = min, Max = max >= 63 ? 0 : max };
        bool Code(int i) => args.Count > i && Regex.IsMatch(args[i], @"^\d+$");
        List<MaterialSlot> slots;
        bool sub;
        switch (helper)
        {
            case "AddFusionProcCodeFunRep" when args.Count == 6 && Code(0):
                slots = [Slot(args[0]), Slot(args[1], Num(2), Num(3))];
                sub = args[4] == "true";
                break;
            case "AddFusionProcCode2FunRep" when args.Count == 7 && Code(0) && Code(1):
                slots = [Slot(args[0]), Slot(args[1]), Slot(args[2], Num(3), Num(4))];
                sub = args[5] == "true";
                break;
            case "AddFusionProcCodeRep2" when args.Count == 5 && Code(0) && Num(1) >= 2:
                slots = [Slot(args[0], Num(1), Num(2))];
                sub = args[3] == "true";
                break;
            case "AddFusionProcFunFunRep" when args.Count == 5:
                slots = [Slot(args[0]), Slot(args[1], Num(2), Num(3))];
                sub = false;
                break;
            case "AddFusionProcFunFun" when args.Count == 4:
                slots = [Slot(args[0]), Slot(args[1], Num(2), Num(2))];
                sub = false;
                break;
            default: throw No($"its Summon procedure is written in a way the blocks can't ({helper})");
        }
        if (slots.Count(s => s.Repeats) > 1) throw No("more than one of its Fusion Materials repeats");
        return new SummonProcedure { Kind = SummonProcedureKind.Fusion, Materials = slots, MinMaterials = slots.Sum(s => s.Min), FusionNoSubstitutes = !sub };
    }

    // ---------------------------------------------------------------- Fusion Summons by effect

    /// <summary>What a Polymerization-style operation's materials group is made of.</summary>
    private sealed record FusionParts(bool Core, string? CoreOnly, bool Gy, bool Banished, bool Deck, bool Opponent, bool Pzone, string Send, CardFilter Mats);

    /// <summary>The block's material options by what they take and where the materials go.</summary>
    private static readonly Dictionary<string, (bool Core, string? CoreOnly, bool Gy, bool Banished, bool Deck, bool Opponent, bool Pzone, string Send)> FusionModes = new()
    {
        ["hand_field"] = (true, null, false, false, false, false, false, "grave"),
        ["field"] = (true, "field", false, false, false, false, false, "grave"),
        ["hand"] = (true, "hand", false, false, false, false, false, "grave"),
        ["hand_field_deck"] = (true, null, false, false, true, false, false, "grave"),
        ["deck"] = (false, null, false, false, true, false, false, "grave"),
        ["hand_deck"] = (true, "hand", false, false, true, false, false, "grave"),
        ["either_field"] = (true, "field", false, false, false, true, false, "grave"),
        ["hand_either_field"] = (true, null, false, false, false, true, false, "grave"),
        ["field_gy_banish"] = (true, "field", true, false, false, false, false, "banish"),
        ["gy_banish"] = (false, null, true, false, false, false, false, "banish"),
        ["hand_field_banish"] = (true, null, false, false, false, false, false, "banish"),
        ["hand_field_gy_banish"] = (true, null, true, false, false, false, false, "banish"),
        ["gy_deck"] = (false, null, true, false, false, false, false, "deck"),
        ["field_gy_deck"] = (true, "field", true, false, false, false, false, "deck"),
        ["hand_field_gy_deck"] = (true, null, true, false, false, false, false, "deck"),
        ["gy_banished_deck"] = (false, null, true, true, false, false, false, "deck"),
        ["field_gy_banished_deck"] = (true, "field", true, true, false, false, false, "deck"),
        ["banished_deck"] = (false, null, false, true, false, false, false, "deck"),
        ["hand_field_pzone"] = (true, null, false, false, false, false, true, "grave"),
        ["field_pzone"] = (true, "field", false, false, false, false, true, "grave"),
    };

    /// <summary>The lines a Polymerization-style script may have besides its materials, with Super Polymerization's and "including this card"'s.</summary>
    private static readonly Regex[] FusionLines =
    [
        .. new[]
        {
            @"local chkf=tp", @"local c=e:GetHandler\(\)", @"if chk==0 then", @"end", @"else", @"elseif ce then", @"elseif ce and mg[23] then", @"local mg[23]=nil", @"mg[23]=fgroup\(ce,e,tp\)",
            @"local mg[23]=fgroup\(ce,e,tp\)", @"local sg2=nil", @"local ce=Duel\.GetChainMaterial\(tp\)", @"if ce~=nil then", @"local fgroup=ce:GetTarget\(\)",
            @"local mf=ce:GetValue\(\)", @"sg2=Duel\.GetMatchingGroup\(s\.\w+,tp,LOCATION_EXTRA,0,nil,e,tp,mg[23],mf,(?:c,)?chkf\)",
            @"if (?:sg1:GetCount\(\)>0|#sg1>0) or \(sg2~=nil and (?:sg2:GetCount\(\)>0|#sg2>0)\) then", @"local sg=sg1:Clone\(\)", @"if sg2 then sg:Merge\(sg2\) end",
            @"Duel\.Hint\(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON\)", @"local tg=sg:Select\(tp,1,1,nil\)", @"local tc=tg:GetFirst\(\)",
            @"if sg1:IsContains\(tc\) and \(sg2==nil or not sg2:IsContains\(tc\) or (?:ce and )?not Duel\.SelectYesNo\(tp,ce:GetDescription\(\)\)\) then",
            @"local mat1=Duel\.SelectFusionMaterial\(tp,tc,mg1,(?:nil|c),chkf\)", @"tc:SetMaterial\(mat1\)", @"Duel\.BreakEffect\(\)",
            @"Duel\.SpecialSummon\(tc,SUMMON_TYPE_FUSION,tp,tp,false,false,POS_FACEUP\)", @"local mat2=Duel\.SelectFusionMaterial\(tp,tc,mg[23],(?:nil|c),chkf\)",
            @"local fop=ce:GetOperation\(\)", @"fop\(ce,e,tp,tc,mat2\)", @"tc:CompleteProcedure\(\)",
            @"local res=Duel\.IsExistingMatchingCard\(s\.\w+,tp,LOCATION_EXTRA,0,1,nil,e,tp,mg1,nil,(?:c,)?chkf\)", @"if not res then",
            @"res=Duel\.IsExistingMatchingCard\(s\.\w+,tp,LOCATION_EXTRA,0,1,nil,e,tp,mg[23],mf,(?:c,)?chkf\)", @"return res",
            @"Duel\.SetOperationInfo\(0,CATEGORY_(?:SPECIAL_SUMMON|REMOVE|TODECK|TOGRAVE|DECKDES),(?:nil|c),\d,(?:tp|0),[\w+]+\)",
            @"local sg1=Duel\.GetMatchingGroup\(s\.\w+,tp,LOCATION_EXTRA,0,nil,e,tp,mg1,nil,(?:c,)?chkf\)",
            @"local mg1=.*", @"local mg2=Duel\.GetMatchingGroup\(.*\)", @"local mg3=Duel\.GetMatchingGroup\(.*\)", @"mg1:Merge\(mg[23]\)",
            @"if not c:IsRelateTo(?:Effect\(e\)|Chain\(\)) or c:IsImmuneToEffect\(e\) then return end",
            @"mg1:Merge\(Duel\.GetMatchingGroup\(.*\)\)",
            @"if e:IsHasType\(EFFECT_TYPE_ACTIVATE\) then", @"Duel\.SetChainLimit\(aux\.FALSE\)",
            @"Duel\.HintSelection\(mat1\)",
            @"Duel\.(?:SendtoGrave|Remove|SendtoDeck)\(mat1,(?:POS_FACEUP,|nil,(?:SEQ_DECKSHUFFLE|2),)?REASON_EFFECT\+REASON_MATERIAL\+REASON_FUSION\)",
        }.Select(p => new Regex("^" + p + "$")),
    ];

    /// <summary>
    /// A Fusion Summon by effect as the Fusion Summon block (and, for Super Polymerization, "cards and effects cannot be
    /// activated in response"). False when the operation isn't one of these shapes, so the older reader can try it.
    /// </summary>
    private static bool FusionEffect(EffectEvent effect, string operation, string? target, Functions fns)
    {
        if (target is null) return false;
        try
        {
            if (FusionBlock(operation, target, fns) is not { } read) return false;
            effect.Do.Add(read.Block);
            if (read.Limit) effect.Cost.Add(Block("cost.chain_limit").With("who", "both").With("what", "any"));
            return true;
        }
        catch (Untranslatable)
        {
            // The older reader may still read it, or say why not.
            return false;
        }
    }

    private static (BlockInstance Block, bool Limit)? FusionBlock(string operation, string target, Functions fns)
    {
        static List<string> Norm(IEnumerable<string> body) =>
            JoinContinuations(body.ToList()).Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => Regex.Replace(l, @"\b(?:c\d+|s)\.", "s.")).ToList();
        var op = Norm(fns.Get(operation).Body);
        var tg = Norm(fns.Get(target).Body);
        if (!op.Any(l => l.Contains("SelectFusionMaterial(", StringComparison.Ordinal)) || !op.Any(l => l.Contains("SUMMON_TYPE_FUSION", StringComparison.Ordinal))) return null;
        if (op.Concat(tg).Any(l => !FusionLines.Any(r => r.IsMatch(l)))) return null;

        // "including this card": every check and selection is given this card.
        var selfIn = op.Any(l => l.StartsWith("local mat1=Duel.SelectFusionMaterial(tp,tc,mg1,c,", StringComparison.Ordinal));
        if (selfIn != (op.Contains("if not c:IsRelateToEffect(e) or c:IsImmuneToEffect(e) then return end")
                       || op.Contains("if not c:IsRelateToChain() or c:IsImmuneToEffect(e) then return end"))) return null;
        if (op.Concat(tg).Where(l => l.Contains("SelectFusionMaterial(", StringComparison.Ordinal) || l.Contains("LOCATION_EXTRA,0,", StringComparison.Ordinal))
            .Any(l => l.Contains(",c,chkf)", StringComparison.Ordinal) != selfIn)) return null;
        // Super Polymerization's chain limit, only while the card is activated.
        var limit = tg.Contains("Duel.SetChainLimit(aux.FALSE)");
        if (limit != tg.Contains("if e:IsHasType(EFFECT_TYPE_ACTIVATE) then")) return null;

        var sends = op.Where(l => l.StartsWith("Duel.SendtoGrave(mat1,", StringComparison.Ordinal) || l.StartsWith("Duel.Remove(mat1,", StringComparison.Ordinal)
                                  || l.StartsWith("Duel.SendtoDeck(mat1,", StringComparison.Ordinal)).ToList();
        if (sends.Count != 1) return null;
        var send = sends[0] switch
        {
            var s when s.StartsWith("Duel.Remove", StringComparison.Ordinal) && s.Contains("POS_FACEUP", StringComparison.Ordinal) => "banish",
            var s when s.StartsWith("Duel.SendtoDeck", StringComparison.Ordinal) && Regex.IsMatch(s, @"nil,(?:SEQ_DECKSHUFFLE|2),") => "deck",
            var s when s.StartsWith("Duel.SendtoGrave", StringComparison.Ordinal) && !s.Contains(",nil,", StringComparison.Ordinal) => "grave",
            _ => "",
        };
        if (send.Length == 0 || (op.Contains("Duel.HintSelection(mat1)") && send != "deck")) return null;

        var parts = FusionMaterials(op, send, fns);
        var check = FusionMaterials(tg, send, fns);
        if (parts is null || check is null) return null;
        if (parts with { Mats = check.Mats } != check || System.Text.Json.JsonSerializer.Serialize(parts.Mats) != System.Text.Json.JsonSerializer.Serialize(check.Mats)) return null;
        var mode = FusionModes.FirstOrDefault(kv => kv.Value == (parts.Core, parts.CoreOnly, parts.Gy, parts.Banished, parts.Deck, parts.Opponent, parts.Pzone, parts.Send)).Key;
        if (mode is null) return null;

        // The Fusion Monster: its filter, without the checks every Fusion Summon makes.
        var sg1 = op.Select(l => Regex.Match(l, @"^local sg1=Duel\.GetMatchingGroup\((s\.\w+),tp,LOCATION_EXTRA,0,nil,e,tp,mg1,nil,(?:c,)?chkf\)$")).FirstOrDefault(x => x.Success);
        if (sg1 is null) return null;
        var fusion = new CardFilter { Owner = CardOwner.You, Zones = Model.Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion };
        foreach (var term in FunctionTerms(sg1.Groups[1].Value, fns))
        {
            if (term is "c:IsType(TYPE_FUSION)" or "(not f or f(c))" or "c:IsCanBeSpecialSummoned(e,SUMMON_TYPE_FUSION,tp,false,false)"
                or "c:CheckFusionMaterial(m,nil,chkf)" or "c:CheckFusionMaterial(m,gc,chkf)") continue;
            Term(term, fusion);
        }
        fusion.MonsterKind = MonsterKind.Fusion;
        fusion.Zones = Model.Zone.ExtraDeck;
        // With "including this card" the Fusion filter takes this card as gc.
        if (selfIn && !fns.Get(fns.Prefix + sg1.Groups[1].Value[1..]).Params.Contains("gc")) return null;
        return (Block(FusionSummonAction.BlockId).With("cards", fusion).With("mats", mode).With("matcards", parts.Mats).With("self", selfIn ? "true" : "false"), limit);
    }

    /// <summary>
    /// The materials group of a Fusion Summon's target or operation: "local mg1=Duel.GetFusionMaterial(tp):Filter(...)",
    /// "local mg2=Duel.GetMatchingGroup(...)" and "mg1:Merge(mg2)". Null when it is built another way.
    /// </summary>
    private static FusionParts? FusionMaterials(List<string> lines, string send, Functions fns)
    {
        // Each place's filter, which must all say the same: the block has one for every material.
        var filters = new List<CardFilter>();
        var mats = new CardFilter { CardType = FilterCardType.Monster };
        bool core = false, gy = false, banished = false, deck = false, opponent = false, pzone = false;
        string? only = null;
        var able = send switch { "banish" => "c:IsAbleToRemove()", "deck" => "c:IsAbleToDeck()", _ => "c:IsAbleToGrave()" };
        bool Terms(string fn, Func<string, bool> special)
        {
            foreach (var term in FunctionTerms(fn, fns))
            {
                if (special(term)) continue;
                if (term is "c:IsCanBeFusionMaterial()" or "not c:IsImmuneToEffect(e)" or "c:IsType(TYPE_MONSTER)" || term == able) continue;
                Term(term, mats);
            }
            return true;
        }
        var mg1 = lines.Where(l => l.StartsWith("local mg1=", StringComparison.Ordinal)).Distinct().ToList();
        if (mg1.Count != 1) return null;
        var groups = new List<string> { mg1[0]["local mg1=".Length..] };
        foreach (var n in new[] { "2", "3" })
        {
            var merged = lines.Contains($"mg1:Merge(mg{n})");
            var def = lines.FirstOrDefault(l => l.StartsWith($"local mg{n}=Duel.GetMatchingGroup(", StringComparison.Ordinal));
            if (merged != (def is not null)) return null;
            if (def is not null) groups.Add(def[$"local mg{n}=".Length..]);
        }
        foreach (var inline in lines.Where(l => l.StartsWith("mg1:Merge(Duel.GetMatchingGroup(", StringComparison.Ordinal)).Distinct())
            groups.Add(inline["mg1:Merge(".Length..^1]);
        foreach (var g in groups)
        {
            mats = new CardFilter { CardType = FilterCardType.Monster };
            filters.Add(mats);
            Match m;
            if ((m = Regex.Match(g, @"^Duel\.GetFusionMaterial\(tp\)((?::Filter\([\w.()]+,nil(?:,e|,LOCATION_HAND|,LOCATION_MZONE)?\))*)$")).Success)
            {
                if (core) return null;
                core = true;
                foreach (Match f in Regex.Matches(m.Groups[1].Value, @":Filter\(([\w.()]+),nil(,e|,LOCATION_HAND|,LOCATION_MZONE)?\)"))
                {
                    var fn = f.Groups[1].Value;
                    if (fn == "Card.IsOnField" || (fn, f.Groups[2].Value) == ("Card.IsLocation", ",LOCATION_MZONE")) { only = "field"; continue; }
                    if ((fn, f.Groups[2].Value) == ("Card.IsLocation", ",LOCATION_HAND")) { only = "hand"; continue; }
                    if (fn == "Card.IsAbleToRemove" && send == "banish") continue;
                    if (f.Groups[2].Value is ",LOCATION_HAND" or ",LOCATION_MZONE") return null;
                    if (fn == "aux.NOT(Card.IsImmuneToEffect)") continue;
                    if (!Regex.IsMatch(fn, @"^s\.\w+$")) return null;
                    Terms(fn, term =>
                    {
                        if (term is "c:IsOnField()" or "c:IsLocation(LOCATION_MZONE)") { only = "field"; return true; }
                        if (term == "c:IsLocation(LOCATION_HAND)") { only = "hand"; return true; }
                        return false;
                    });
                }
                continue;
            }
            m = Regex.Match(g, @"^Duel\.GetMatchingGroup\((?:aux\.NecroValleyFilter\((s\.\w+)\)|(s\.\w+)),tp,(LOCATION_GRAVE|LOCATION_REMOVED|LOCATION_DECK|LOCATION_PZONE|0),(0|LOCATION_MZONE),nil(?:,e)?\)$");
            if (!m.Success) return null;
            var fname = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var faceUp = false;
            Terms(fname, term => term == "c:IsFaceup()" && (faceUp = true));
            // Banished materials and the opponent's monsters are face-up ones; the GY's and Deck's are any.
            switch (m.Groups[3].Value, m.Groups[4].Value, faceUp)
            {
                case ("LOCATION_GRAVE", "0", false) when !gy: gy = true; break;
                case ("LOCATION_REMOVED", "0", true) when !banished: banished = true; break;
                case ("LOCATION_DECK", "0", false) when !deck: deck = true; break;
                case ("0", "LOCATION_MZONE", true) when !opponent: opponent = true; break;
                case ("LOCATION_PZONE", "0", false) when !pzone: pzone = true; break;
                default: return null;
            }
        }
        foreach (var f in filters)
        {
            f.CardType = FilterCardType.Monster;
            f.Zones = Model.Zone.None;
        }
        var shape = System.Text.Json.JsonSerializer.Serialize(filters[0]);
        if (filters.Any(f => System.Text.Json.JsonSerializer.Serialize(f) != shape)) return null;
        return new FusionParts(core, only, gy, banished, deck, opponent, pzone, send, filters[0]);
    }
}
