using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// Ritual and Fusion Summons by card effects, in all the ways official cards do them: Tributing from the hand or field,
// banishing from the GY, sending from the Deck or Extra Deck, Tributing the opponent's monsters, total ATK instead of
// Levels (Drytron), shuffling Fusion Materials into the Deck, using the opponent's monsters (Super Polymerization) or
// the Pendulum Zones, "including this card", and what happens to the Summoned monster afterwards.

/// <summary>
/// What happens to the monster a Ritual or Fusion Summon brings out: ", and if you do, it cannot be destroyed by card
/// effects", ", but it cannot attack this turn", ", but destroy it during the End Phase".
/// </summary>
public static class SummonedMonsterAfter
{
    public static ParamSpec Param => ParamSpec.Choice("after", "Afterwards",
        new("none", "", "nothing more"),
        new("indestructible_battle", ", and if you do, it cannot be destroyed by battle", "it cannot be destroyed by battle"),
        new("indestructible_effect", ", and if you do, it cannot be destroyed by card effects", "it cannot be destroyed by card effects"),
        new("indestructible_unnegatable", ", and if you do, it cannot be destroyed by card effects, and its effects cannot be negated", "it cannot be destroyed by card effects, and its effects cannot be negated"),
        new("unnegatable", ", and if you do, its effects cannot be negated", "its effects cannot be negated"),
        new("cannot_attack", ", but it cannot attack this turn", "it cannot attack this turn"),
        new("destroy_end", ", but destroy it during the End Phase", "destroy it during the End Phase"));

    /// <summary>The lines that do it to <paramref name="tc"/>, the monster just Summoned.</summary>
    public static List<string> Lines(BlockContext ctx, string tc)
    {
        var v = ctx.Effect.NewLocal("ae");
        List<string> Single(string code, string? value, string reset) =>
        [
            $"local {v}=Effect.CreateEffect(e:GetHandler())",
            $"{v}:SetType(EFFECT_TYPE_SINGLE)",
            $"{v}:SetCode({code})",
            .. value is null ? Array.Empty<string>() : [$"{v}:SetValue({value})"],
            $"{v}:SetReset({reset})",
            $"{tc}:RegisterEffect({v},true)",
        ];
        List<string> Unnegatable()
        {
            // The card's effects cannot be negated, and its activations' effects cannot be negated (as official cards write it).
            var filter = ctx.Script.AddFunction("unnegfilter", "e,ct",
                ["local te=Duel.GetChainInfo(ct,CHAININFO_TRIGGERING_EFFECT)", "return te:GetHandler()==e:GetHandler()"]);
            var d = ctx.Effect.NewLocal("ae");
            return
            [
                .. Single("EFFECT_CANNOT_DISABLE", null, "RESET_EVENT+RESETS_STANDARD"),
                $"local {d}=Effect.CreateEffect(e:GetHandler())",
                $"{d}:SetType(EFFECT_TYPE_FIELD)",
                $"{d}:SetCode(EFFECT_CANNOT_DISEFFECT)",
                $"{d}:SetRange(LOCATION_MZONE)",
                $"{d}:SetValue({filter})",
                $"{d}:SetReset(RESET_EVENT+RESETS_STANDARD)",
                $"{tc}:RegisterEffect({d},true)",
            ];
        }
        switch (ctx.Args.Choice("after"))
        {
            case "indestructible_battle": return Single("EFFECT_INDESTRUCTABLE_BATTLE", "1", "RESET_EVENT+RESETS_STANDARD");
            case "indestructible_effect": return Single("EFFECT_INDESTRUCTABLE_EFFECT", "1", "RESET_EVENT+RESETS_STANDARD");
            case "indestructible_unnegatable": return [.. Single("EFFECT_INDESTRUCTABLE_EFFECT", "1", "RESET_EVENT+RESETS_STANDARD"), .. Unnegatable()];
            case "unnegatable": return Unnegatable();
            case "cannot_attack": return Single("EFFECT_CANNOT_ATTACK", null, "RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END");
            case "destroy_end":
            {
                // As Forbidden Ritual Art: a flag marks this monster, so a monster that left and came back isn't destroyed.
                var con = ctx.Script.AddFunction("enddescon", "e,tp,eg,ep,ev,re,r,rp",
                [
                    "local tc=e:GetLabelObject()",
                    "if tc:GetFlagEffectLabel(id)~=e:GetLabel() then",
                    "\te:Reset()",
                    "\treturn false",
                    "else return true end",
                ]);
                var op = ctx.Script.AddFunction("enddesop", "e,tp,eg,ep,ev,re,r,rp", ["Duel.Hint(HINT_CARD,0,id)", "Duel.Destroy(e:GetLabelObject(),REASON_EFFECT)"]);
                var fid = ctx.Effect.NewLocal("fid");
                return
                [
                    $"local {fid}=e:GetHandler():GetFieldID()",
                    $"{tc}:RegisterFlagEffect(id,RESET_EVENT+RESETS_STANDARD,0,1,{fid})",
                    $"local {v}=Effect.CreateEffect(e:GetHandler())",
                    $"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
                    $"{v}:SetCode(EVENT_PHASE+PHASE_END)",
                    $"{v}:SetCountLimit(1)",
                    $"{v}:SetProperty(EFFECT_FLAG_IGNORE_IMMUNE)",
                    $"{v}:SetLabel({fid})",
                    $"{v}:SetLabelObject({tc})",
                    $"{v}:SetCondition({con})",
                    $"{v}:SetOperation({op})",
                    $"Duel.RegisterEffect({v},tp)",
                ];
            }
            default: return [];
        }
    }
}

/// <summary>
/// "Ritual Summon 1 Reptile Ritual Monster from your hand, by Tributing monsters from your hand or field whose total
/// Levels equal its Level", and its family: "..., and/or banishing monsters from your GY, ..." (Nekroz), "by sending
/// Normal Monsters from your Deck to the GY" (Advanced Ritual Art), "whose total ATK equal or exceed its ATK" (Drytron),
/// "by Tributing 1 monster whose Level ...", "Ritual Summon this card". Written as the engine's own Ritual helpers do it.
/// </summary>
public sealed class RitualSummonAction : ActionBlock
{
    public const string BlockId = "do.ritual_summon";
    public override string Id => BlockId;
    public override string Group => "Summon";
    public override string Name => "Ritual Summon a monster";
    public override string Sentence => "Ritual Summon 1 {cards}, by {source} (monsters used: {mats}; from the other place: {mats2}), whose total {stat} {how} its own (at most {max} monsters){after}";
    public override string Help => "A Ritual Spell's effect (or any card's, like Mitsurugi Magatama). Pick where the Ritual Monster can come from " +
                                   "(hand, Deck, GY, banishment, or this card itself), what is used to Summon it (Tributes, monsters banished from the GY, " +
                                   "sent from the Deck...), and whether their Levels or their ATK are added up.";

    /// <summary>The ways to pay for the Summon whose second kind of material has its own picker.</summary>
    private static bool HasSecond(string source) => source is "hand_field_gy" or "hand_field_deck" or "hand_field_extra" or "hand_field_opponent";

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Ritual Monster", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Ritual },
            Zone.Hand | Zone.Deck | Zone.Graveyard | Zone.Banished, thisCard: true),
        ParamSpec.Cards("mats", "Monsters used", () => new CardFilter { CardType = FilterCardType.Monster }, Zone.MonsterZone),
        ParamSpec.Choice("how", "Levels", new("equal", "equal"), new("greater", "equal or exceed")),
        ParamSpec.Choice("source", "Using",
            new("hand_field", "Tributing monsters from your hand or field"),
            new("field", "Tributing monsters you control"),
            new("hand", "Tributing monsters from your hand"),
            new("hand_field_gy", "Tributing from your hand or field, and/or banishing from your GY"),
            new("gy", "banishing monsters from your GY"),
            new("deck", "sending monsters from your Deck to the GY"),
            new("hand_field_deck", "Tributing from your hand or field, and/or sending from your Deck to the GY"),
            new("hand_field_extra", "Tributing from your hand or field, and/or sending from your Extra Deck to the GY"),
            new("hand_field_opponent", "Tributing from your hand or field, and/or face-up monsters your opponent controls")),
        ParamSpec.Cards("mats2", "Monsters banished, sent or taken from the other place", () => new CardFilter { CardType = FilterCardType.Monster }, Zone.MonsterZone)
            .ShowIf(b => HasSecond(b.Values.GetValueOrDefault("source") ?? "hand_field")),
        ParamSpec.Choice("stat", "Added up", new("level", "Levels", "Levels (its Level)"), new("original", "Levels", "Levels (its original Level)"), new("atk", "ATK", "ATK (its ATK)")),
        ParamSpec.Int("max", "At most how many monsters (0: any number)", 0, 0, 5),
        SummonedMonsterAfter.Param,
    ];

    private static CardFilter Ritual(BlockArgs args)
    {
        var f = args.Cards("cards").Clone();
        f.MonsterKind = MonsterKind.Ritual;
        f.CardType = FilterCardType.Monster;
        return f;
    }

    /// <summary>"from your hand", "from your hand or GY", "from your hand, Deck, GY, or banishment".</summary>
    private static string From(Zone zones)
    {
        var places = new List<string>();
        if (zones.HasFlag(Zone.Hand)) places.Add("hand");
        if (zones.HasFlag(Zone.Deck)) places.Add("Deck");
        if (zones.HasFlag(Zone.Graveyard)) places.Add("GY");
        if (zones.HasFlag(Zone.Banished)) places.Add("banishment");
        return "from your " + (places.Count switch
        {
            0 => "hand",
            1 => places[0],
            2 => $"{places[0]} or {places[1]}",
            _ => string.Join(", ", places.Take(places.Count - 1)) + ", or " + places[^1],
        });
    }

    public override string Describe(BlockArgs args)
    {
        var ritual = Ritual(args);
        var who = ritual.ThisCard ? "this card" : $"1 {FilterText.Describe(ritual, args.Pack, withLocation: false)} {From(ritual.Zones)}";
        var max = args.Int("max");
        string M(string key)
        {
            var f = args.Cards(key);
            var noun = FilterText.Describe(f, args.Pack, plural: max != 1, withLocation: false);
            return max switch { 0 => noun, 1 => "1 " + noun, _ => $"up to {max} {noun}" };
        }
        var m = M("mats");
        var m2 = FilterText.Describe(args.Cards("mats2"), args.Pack, plural: true, withLocation: false);
        var by = args.Choice("source") switch
        {
            "field" => $"Tributing {m} you control",
            "hand" => $"Tributing {m} from your hand",
            "hand_field_gy" => $"Tributing {m} from your hand or field, and/or banishing {m2} from your GY,",
            "gy" => $"banishing {m} from your GY",
            "deck" => $"sending {m} from your Deck to the GY",
            "hand_field_deck" => $"Tributing {m} from your hand or field, and/or sending {m2} from your Deck to the GY,",
            "hand_field_extra" => $"Tributing {m} from your hand or field, and/or sending {m2} from your Extra Deck to the GY,",
            "hand_field_opponent" => $"Tributing {m} from your hand or field, and/or face-up {m2} your opponent controls,",
            _ => $"Tributing {m} from your hand or field",
        };
        return $"Ritual Summon {who}, by {by} {Compare(args, ritual)}{args.Describe("after")}";
    }

    /// <summary>"whose total Levels equal or exceed its Level", "whose total Levels equal 8 or more", "whose Level equals its Level".</summary>
    private static string Compare(BlockArgs args, CardFilter ritual)
    {
        var greater = args.Choice("how") == "greater";
        var one = args.Int("max") == 1;
        var stat = args.Choice("stat");
        var noun = stat == "atk" ? "ATK" : one ? "Level" : "Levels";
        var whose = one ? $"whose {noun}" : $"whose total {noun}";
        // "This card is used to Ritual Summon "Black Luster Soldier". ... whose total Levels equal 8 or more."
        int? level = stat == "original" && !ritual.ThisCard && ritual.CardCode != 0 && ritual.AltCodes.Length == 0
            ? args.Pack?.Cards.FirstOrDefault(c => c.Card.Id == ritual.CardCode)?.Card.Level : null;
        if (level is { } n)
            return one
                ? greater ? $"{whose} is {n} or higher" : $"{whose} is exactly {n}"
                : greater ? $"{whose} equal {n} or more" : $"{whose} exactly equal {n}";
        var target = stat switch { "atk" => "its ATK", "original" => "its original Level", _ => "its Level" };
        if (one) return greater ? $"{whose} equals or exceeds {target}" : $"{whose} equals {target}";
        if (stat == "atk") return greater ? $"{whose} equal or exceed {target}" : $"{whose} exactly equal {target}";
        return $"{whose} {args.Describe("how")} {target}";
    }

    public override void Compile(BlockContext ctx)
    {
        var a = ctx.Args;
        var script = ctx.Script;
        var source = a.Choice("source");
        var atk = a.Choice("stat") == "atk";
        var how = a.Choice("how") == "greater" ? "\"Greater\"" : "\"Equal\"";
        var max = a.Int("max");
        var ritual = Ritual(a);
        string Join(IEnumerable<string> parts) => string.Join(" and ", parts);
        List<string> Conds(CardFilter f) => FilterCompiler.Conditions(f).Where(c => c != "c:IsType(TYPE_MONSTER)").ToList();

        // The Ritual Monster: where it comes from and what it must be.
        string loc, rf;
        if (ritual.ThisCard)
        {
            if (ctx.Card.Frame != MonsterFrame.Ritual) { ctx.Effect.Error("\"Ritual Summon this card\" needs this card to be a Ritual Monster."); return; }
            loc = ctx.Effect.Range ?? "LOCATION_HAND";
            rf = script.AddFunction("ritselffilter", "c,e,tp,chk", ["return c==e:GetHandler()"]);
        }
        else
        {
            var where = new List<string>();
            if (ritual.Zones.HasFlag(Zone.Hand)) where.Add("LOCATION_HAND");
            if (ritual.Zones.HasFlag(Zone.Deck)) where.Add("LOCATION_DECK");
            if (ritual.Zones.HasFlag(Zone.Graveyard)) where.Add("LOCATION_GRAVE");
            if (ritual.Zones.HasFlag(Zone.Banished)) where.Add("LOCATION_REMOVED");
            if (where.Count == 0) { ctx.Effect.Error("\"Ritual Summon\": pick the hand, Deck, GY or banishment for the Ritual Monster."); return; }
            loc = string.Join("+", where);
            var conds = Conds(ritual).Where(c => c != "c:IsType(TYPE_RITUAL)").ToList();
            if (ritual.ExcludeThisCard) conds.Add("c~=e:GetHandler()");
            if (ritual.Zones.HasFlag(Zone.Banished)) conds.Add("(not c:IsLocation(LOCATION_REMOVED) or c:IsFaceup())");
            rf = conds.Count == 0 ? "nil" : script.AddFunction("ritfilter", "c,e,tp,chk", [$"return {Join(conds)}"]);
        }
        if (atk && source is not ("hand_field" or "field" or "hand"))
        {
            ctx.Effect.Error("A Ritual Summon by total ATK Tributes monsters from the hand or field only.");
            return;
        }

        // The materials: the ones the engine checks as Ritual Material (hand, field, the opponent's field), and the
        // extra ones (GY, Deck, Extra Deck) that are merged in as they are, like the engine's Ritual helpers do.
        var mats = a.Cards("mats");
        var second = HasSecond(source) ? a.Cards("mats2") : mats;
        string mat;
        if (source is "gy" or "deck") mat = script.AddFunction("ritmat", "e,tp", ["return Group.CreateGroup()"]);
        else
        {
            var conds = Conds(mats);
            if (source == "field") conds.Add("c:IsLocation(LOCATION_MZONE)");
            if (source == "hand") conds.Add("c:IsLocation(LOCATION_HAND)");
            var body = new List<string> { $"local mg=Duel.{(atk ? "GetRitualMaterialEx" : "GetRitualMaterial")}(tp)" };
            if (conds.Count > 0) body.Add($"mg=mg:Filter({script.AddFunction("ritmatfilter", "c,e,tp", [$"return {Join(conds)}"])},nil,e,tp)");
            if (source == "hand_field_opponent")
            {
                var opp = script.AddFunction("ritoppfilter", "c,e,tp", [$"return {Join(["c:IsFaceup()", "c:GetLevel()>0", "not c:IsImmuneToEffect(e)", "c:IsReleasable()", .. Conds(second)])}"]);
                body.Add($"mg:Merge(Duel.GetMatchingGroup({opp},tp,0,LOCATION_MZONE,nil,e,tp))");
            }
            body.Add("return mg");
            mat = script.AddFunction("ritmat", "e,tp", body);
        }
        string extra = "nil";
        var (extraLoc, extraTest) = source switch
        {
            "hand_field_gy" or "gy" => ("LOCATION_GRAVE", new[] { "c:GetLevel()>0", "c:IsType(TYPE_MONSTER)", "c:IsAbleToRemove()" }),
            "deck" or "hand_field_deck" => ("LOCATION_DECK", ["c:IsType(TYPE_MONSTER)", "c:IsLevelAbove(1)", "c:IsAbleToGrave()"]),
            "hand_field_extra" => ("LOCATION_EXTRA", ["c:IsType(TYPE_MONSTER)", "c:IsLevelAbove(1)", "c:IsAbleToGrave()"]),
            _ => ((string?)null, Array.Empty<string>()),
        };
        if (extraLoc is not null)
        {
            var xf = script.AddFunction("ritexfilter", "c,e,tp", [$"return {Join([.. extraTest, .. Conds(second)])}"]);
            extra = script.AddFunction("ritextra", "e,tp", [$"return Duel.GetMatchingGroup({xf},tp,{extraLoc},0,nil,e,tp)"]);
        }

        // "by Tributing 1 monster" / "up to 2 monsters": the engine's extra group checks.
        var on = new List<string>();
        var off = new List<string>();
        if (max > 0)
        {
            var rc = script.AddFunction("ritrcheck", "tp,g,c", [$"return #g<={max}"]);
            var rgc = script.AddFunction("ritrgcheck", "g,ec", [$"return #g<={max}"]);
            on.AddRange([$"aux.RCheckAdditional={rc}", $"aux.RGCheckAdditional={rgc}"]);
            off.AddRange(["aux.RCheckAdditional=nil", "aux.RGCheckAdditional=nil"]);
        }

        // Levels use the engine's own checks; ATK uses the same checks with ATK, as Meteonis Drytron writes them.
        string filter, check, gcheck, lvfn, lvOf, subMax;
        if (atk)
        {
            check = script.AddFunction("ritatkcheck", "g,tp,c,atk,greater_or_equal",
            [
                "if atk==0 then return false end",
                "local ok",
                "if greater_or_equal==\"Greater\" then",
                "\tDuel.SetSelectedCard(g)",
                "\tok=g:CheckWithSumGreater(aux.GetCappedAttack,atk)",
                "else",
                "\tok=g:CheckWithSumEqual(aux.GetCappedAttack,atk,#g,#g)",
                "end",
                "return ok and Duel.GetMZoneCount(tp,g,tp)>0 and (not c.mat_group_check or c.mat_group_check(g,tp))",
                "\tand (not aux.RCheckAdditional or aux.RCheckAdditional(tp,g,c))",
            ]);
            gcheck = script.AddFunction("ritatkgcheck", "c,atk,greater_or_equal",
            [
                "if greater_or_equal==\"Equal\" then",
                "\treturn function(g)",
                "\t\treturn (not aux.RGCheckAdditional or aux.RGCheckAdditional(g)) and g:GetSum(aux.GetCappedAttack)<=atk",
                "\tend",
                "end",
                "return function(g,ec)",
                "\tif atk==0 then return #g<=1 end",
                "\tif ec then",
                "\t\treturn (not aux.RGCheckAdditional or aux.RGCheckAdditional(g,ec)) and g:GetSum(aux.GetCappedAttack)-aux.GetCappedAttack(ec)<=atk",
                "\tend",
                "\treturn not aux.RGCheckAdditional or aux.RGCheckAdditional(g)",
                "end",
            ]);
            filter = script.AddFunction("ritatkfilter", "c,filter,e,tp,m1,m2,attack_function,greater_or_equal,chk",
            [
                "if bit.band(c:GetType(),0x81)~=0x81 or (filter and not filter(c,e,tp,chk)) or not c:IsCanBeSpecialSummoned(e,SUMMON_TYPE_RITUAL,tp,false,true) then return false end",
                "local mg=m1:Filter(Card.IsCanBeRitualMaterial,c,c)",
                "if m2 then mg:Merge(m2) end",
                "if c.mat_filter then",
                "\tmg=mg:Filter(c.mat_filter,c,tp)",
                "else",
                "\tmg:RemoveCard(c)",
                "end",
                "local atk=attack_function(c)",
                $"aux.GCheckAdditional={gcheck}(c,atk,greater_or_equal)",
                $"local res=mg:CheckSubGroup({check},1,#mg,tp,c,atk,greater_or_equal)",
                "aux.GCheckAdditional=nil",
                "return res",
            ]);
            lvfn = "aux.GetCappedAttack";
            lvOf = "tc:GetAttack()";
            subMax = "#mg";
        }
        else
        {
            filter = "aux.RitualUltimateFilter";
            check = "aux.RitualCheck";
            gcheck = "aux.RitualCheckAdditional";
            lvfn = a.Choice("stat") == "original" ? "Card.GetOriginalLevel" : "Card.GetLevel";
            lvOf = $"{lvfn}(tc)";
            subMax = "lv";
        }

        var target = script.AddFunction("rittg", "e,tp",
        [
            .. on,
            $"local res=Duel.IsExistingMatchingCard({filter},tp,{loc},0,1,nil,{rf},e,tp,{mat}(e,tp),{(extra == "nil" ? "nil" : extra + "(e,tp)")},{lvfn},{how},true)",
            .. off,
            "return res",
        ]);
        var op = script.AddFunction("ritop", "e,tp",
        [
            "::cancel::",
            .. on,
            $"local m1={mat}(e,tp)",
            $"local m2={(extra == "nil" ? "nil" : extra + "(e,tp)")}",
            "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)",
            $"local tc=Duel.SelectMatchingCard(tp,aux.NecroValleyFilter({filter}),tp,{loc},0,1,1,nil,{rf},e,tp,m1,m2,{lvfn},{how}):GetFirst()",
            "if not tc then",
            .. off.Select(l => "\t" + l),
            "\treturn nil",
            "end",
            "local mg=m1:Filter(Card.IsCanBeRitualMaterial,tc,tc)",
            "if m2 then mg:Merge(m2) end",
            "if tc.mat_filter then",
            "\tmg=mg:Filter(tc.mat_filter,tc,tp)",
            "else",
            "\tmg:RemoveCard(tc)",
            "end",
            $"local lv={lvOf}",
            "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_RELEASE)",
            $"aux.GCheckAdditional={gcheck}(tc,lv,{how})",
            $"local mat=mg:SelectSubGroup(tp,{check},true,1,{subMax},tp,tc,lv,{how})",
            "aux.GCheckAdditional=nil",
            "if not mat then",
            .. off.Select(l => "\t" + l),
            "\tgoto cancel",
            "end",
            "tc:SetMaterial(mat)",
            // Cards from the Deck are sent to the GY; the engine banishes the ones from the GY and sends the Extra Deck's to the GY.
            "local dmat=mat:Filter(Card.IsLocation,nil,LOCATION_DECK)",
            "if #dmat>0 then",
            "\tmat:Sub(dmat)",
            "\tDuel.SendtoGrave(dmat,REASON_EFFECT+REASON_MATERIAL+REASON_RITUAL)",
            "end",
            "if #mat>0 then Duel.ReleaseRitualMaterial(mat) end",
            "Duel.BreakEffect()",
            "Duel.SpecialSummon(tc,SUMMON_TYPE_RITUAL,tp,tp,false,true,POS_FACEUP)",
            "tc:CompleteProcedure()",
            .. off,
            "return tc",
        ]);

        ctx.Effect.AddCategory("CATEGORY_SPECIAL_SUMMON");
        if (source is not ("gy" or "deck")) ctx.Effect.AddCategory("CATEGORY_RELEASE");
        if (source is "gy" or "hand_field_gy") ctx.Effect.AddCategory("CATEGORY_REMOVE");
        if (source is "deck" or "hand_field_deck") ctx.Effect.AddCategory("CATEGORY_DECKDES");
        ctx.Effect.TargetChecks.Add($"{target}(e,tp)");
        ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,{loc})");
        var rc1 = ctx.Effect.NewLocal("rc");
        var r = ctx.Effect.NewLocal("r");
        var after = SummonedMonsterAfter.Lines(ctx, rc1);
        ctx.Effect.AddStep(
        [
            $"local {rc1}={op}(e,tp)",
            $"local {r}={rc1} and 1 or 0",
            .. after.Count == 0 ? Array.Empty<string>() : [$"if {rc1} then", .. after.Select(l => "\t" + l), "end"],
        ], r, ctx.Link);
    }
}

/// <summary>
/// Polymerization and its family: "Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters from your hand
/// or field as Fusion Material", by banishing materials from the GY (Miracle Fusion), from the Deck (Fusion Destiny),
/// by shuffling them into the Deck, using the opponent's monsters (Super Polymerization), the Pendulum Zones or the Extra
/// Deck (Odd-Eyes Fusion), and "including this card". Written out like Polymerization's script, Chain Material included.
/// </summary>
public sealed class FusionSummonAction : ActionBlock
{
    public const string BlockId = "do.fusion_summon";
    public override string Id => BlockId;
    public override string Group => "Summon";
    public override string Name => "Fusion Summon a monster";
    public override string Sentence => "Fusion Summon 1 {cards} from your Extra Deck, {mats} (materials: {matcards}){self}{after}";
    public override string Help => "Polymerization's effect. Pick the Fusion Monster (an archetype, an Attribute...), where the materials come from, and " +
                                   "which monsters may be materials (\"using only \"HERO\" monsters\"). Add \"Players can't respond to this activation\" for " +
                                   "Super Polymerization's \"Cards and effects cannot be activated in response\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Fusion Monster", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion }, Zone.ExtraDeck),
        ParamSpec.Choice("mats", "Materials",
            new("hand_field", "using monsters from your hand or field as Fusion Material"),
            new("field", "using monsters you control as Fusion Material"),
            new("hand", "using monsters from your hand as Fusion Material"),
            new("hand_field_deck", "using monsters from your hand, field, or Deck as Fusion Material"),
            new("field_gy_banish", "by banishing Fusion Materials from your field or GY"),
            new("gy_banish", "by banishing Fusion Materials from your GY"),
            new("gy_deck", "by shuffling Fusion Materials from your GY into the Deck"),
            new("deck", "using monsters from your Deck as Fusion Material"),
            new("hand_deck", "using monsters from your hand or Deck as Fusion Material"),
            new("hand_field_pzone", "using monsters from your hand, field, and/or Pendulum Zones as Fusion Material"),
            new("field_pzone", "using monsters from your field and/or Pendulum Zones as Fusion Material"),
            new("either_field", "using monsters from either field as Fusion Material"),
            new("hand_either_field", "using monsters from your hand and/or either field as Fusion Material"),
            new("hand_field_banish", "by banishing Fusion Materials from your hand or field"),
            new("hand_field_gy_banish", "by banishing Fusion Materials from your hand, field, and/or GY"),
            new("field_gy_deck", "by shuffling Fusion Materials from your field and/or GY into the Deck"),
            new("hand_field_gy_deck", "by shuffling Fusion Materials from your hand, field, and/or GY into the Deck"),
            new("gy_banished_deck", "by shuffling Fusion Materials from your GY and/or banishment into the Deck"),
            new("field_gy_banished_deck", "by shuffling Fusion Materials from your field, GY, and/or banishment into the Deck"),
            new("banished_deck", "by shuffling your banished Fusion Materials into the Deck"),
            // Odd-Eyes Fusion and Dinomorphia Frenzy add monsters from the Extra Deck to the materials and send them to the GY.
            new("extra_field", "using monsters from your Extra Deck and/or field"),
            new("hand_field_extra", "using monsters from your hand, field, and/or Extra Deck"),
            new("extra", "using monsters from your Extra Deck"),
            new("deck_extra", "using monsters from your Deck and/or Extra Deck")),
        ParamSpec.Cards("matcards", "Which monsters can be material", () => new CardFilter { CardType = FilterCardType.Monster }, Zone.MonsterZone),
        ParamSpec.Flag("self", ", including this card", false),
        SummonedMonsterAfter.Param,
    ];

    /// <summary>Where one way of Fusion Summoning takes its materials from, and what it does with them.</summary>
    private sealed record Sources(string? Core, string? CoreOnly, bool Gy, bool Banished, bool Deck, bool Opponent, string Send, bool Pzone = false, bool Extra = false);

    private static Sources Of(string mats) => mats switch
    {
        "field" => new("Duel.GetFusionMaterial(tp)", "c:IsOnField()", false, false, false, false, "grave"),
        "hand" => new("Duel.GetFusionMaterial(tp)", "c:IsLocation(LOCATION_HAND)", false, false, false, false, "grave"),
        "hand_field_deck" => new("Duel.GetFusionMaterial(tp)", null, false, false, true, false, "grave"),
        "deck" => new(null, null, false, false, true, false, "grave"),
        "hand_deck" => new("Duel.GetFusionMaterial(tp)", "c:IsLocation(LOCATION_HAND)", false, false, true, false, "grave"),
        "hand_field_pzone" => new("Duel.GetFusionMaterial(tp)", null, false, false, false, false, "grave", true),
        "field_pzone" => new("Duel.GetFusionMaterial(tp)", "c:IsOnField()", false, false, false, false, "grave", true),
        "either_field" => new("Duel.GetFusionMaterial(tp)", "c:IsOnField()", false, false, false, true, "grave"),
        "hand_either_field" => new("Duel.GetFusionMaterial(tp)", null, false, false, false, true, "grave"),
        "field_gy_banish" => new("Duel.GetFusionMaterial(tp)", "c:IsOnField()", true, false, false, false, "banish"),
        "gy_banish" => new(null, null, true, false, false, false, "banish"),
        "hand_field_banish" => new("Duel.GetFusionMaterial(tp)", null, false, false, false, false, "banish"),
        "hand_field_gy_banish" => new("Duel.GetFusionMaterial(tp)", null, true, false, false, false, "banish"),
        "gy_deck" => new(null, null, true, false, false, false, "deck"),
        "field_gy_deck" => new("Duel.GetFusionMaterial(tp)", "c:IsOnField()", true, false, false, false, "deck"),
        "hand_field_gy_deck" => new("Duel.GetFusionMaterial(tp)", null, true, false, false, false, "deck"),
        "gy_banished_deck" => new(null, null, true, true, false, false, "deck"),
        "field_gy_banished_deck" => new("Duel.GetFusionMaterial(tp)", "c:IsOnField()", true, true, false, false, "deck"),
        "banished_deck" => new(null, null, false, true, false, false, "deck"),
        "extra_field" => new("Duel.GetFusionMaterial(tp)", "c:IsOnField()", false, false, false, false, "grave", Extra: true),
        "hand_field_extra" => new("Duel.GetFusionMaterial(tp)", null, false, false, false, false, "grave", Extra: true),
        "extra" => new(null, null, false, false, false, false, "grave", Extra: true),
        "deck_extra" => new(null, null, false, false, true, false, "grave", Extra: true),
        _ => new("Duel.GetFusionMaterial(tp)", null, false, false, false, false, "grave"),
    };

    private static CardFilter Fusion(BlockArgs args)
    {
        var f = args.Cards("cards").Clone();
        f.MonsterKind = MonsterKind.Fusion;
        f.CardType = FilterCardType.Monster;
        f.Owner = CardOwner.You;
        f.Zones = Zone.ExtraDeck;
        return f;
    }

    public override string Describe(BlockArgs args)
    {
        var fusion = FilterText.Describe(Fusion(args), args.Pack, withLocation: false);
        var mats = args.Cards("matcards");
        var mons = FilterText.Describe(mats, args.Pack, plural: true, withLocation: false);
        var how = args.Describe("mats");
        // "using "HERO" monsters from your hand or field as Fusion Material", "by shuffling Spellcaster monsters from your GY...".
        if (mons != "monsters") how = how.Replace("using monsters", $"using {mons}", StringComparison.Ordinal).Replace("Fusion Materials", mons, StringComparison.Ordinal);
        var self = args.Bool("self") ? ", including this card" : "";
        return $"Fusion Summon 1 {fusion} from your Extra Deck, {how}{self}{args.Describe("after")}";
    }

    public override void Compile(BlockContext ctx)
    {
        var script = ctx.Script;
        var src = Of(ctx.Args.Choice("mats"));
        var self = ctx.Args.Bool("self");
        var matConds = FilterCompiler.Conditions(ctx.Args.Cards("matcards")).Where(c => c != "c:IsType(TYPE_MONSTER)").ToList();
        string Join(IEnumerable<string> parts) => string.Join(" and ", parts);
        var able = src.Send switch { "banish" => "c:IsAbleToRemove()", "deck" => "c:IsAbleToDeck()", _ => null };
        List<string> Base(params string?[] first) => [.. first.OfType<string>(), "c:IsCanBeFusionMaterial()", .. matConds, .. able is null ? [] : new[] { able }];

        // The materials. Immunity is checked when the effect resolves, as Polymerization does; not when it is activated.
        var body = new List<string>();
        if (src.Core is { } core)
        {
            var f = script.AddFunction("fusmatfilter", "c,e,tp,chk", [$"return {Join([.. Base(src.CoreOnly), "(chk or not c:IsOnField() or not c:IsImmuneToEffect(e))"])}"]);
            body.Add($"local mg={core}:Filter({f},nil,e,tp,chk)");
        }
        else body.Add("local mg=Group.CreateGroup()");
        if (src.Pzone)
        {
            // The Pendulum Zones' monsters, as Pendulum Fusion adds them.
            var f = script.AddFunction("fuspzfilter", "c,e,tp,chk", [$"return {Join([.. Base(), "(chk or not c:IsImmuneToEffect(e))"])}"]);
            body.Add($"mg:Merge(Duel.GetMatchingGroup({f},tp,LOCATION_PZONE,0,nil,e,tp,chk))");
        }
        if (src.Gy)
        {
            var f = script.AddFunction("fusgyfilter", "c,e,tp,chk", [$"return {Join(Base())}"]);
            body.Add($"mg:Merge(Duel.GetMatchingGroup(aux.NecroValleyFilter({f}),tp,LOCATION_GRAVE,0,nil,e,tp,chk))");
        }
        if (src.Banished)
        {
            var f = script.AddFunction("fusrmfilter", "c,e,tp,chk", [$"return {Join(Base("c:IsFaceup()"))}"]);
            body.Add($"mg:Merge(Duel.GetMatchingGroup({f},tp,LOCATION_REMOVED,0,nil,e,tp,chk))");
        }
        if (src.Deck)
        {
            var f = script.AddFunction("fusdeckfilter", "c,e,tp,chk", [$"return {Join([.. Base("c:IsType(TYPE_MONSTER)"), "c:IsAbleToGrave()"])}"]);
            body.Add($"mg:Merge(Duel.GetMatchingGroup({f},tp,LOCATION_DECK,0,nil,e,tp,chk))");
            ctx.Effect.AddCategory("CATEGORY_DECKDES");
        }
        if (src.Extra)
        {
            // Monsters in the Extra Deck, as Odd-Eyes Fusion adds them (they are sent to the GY with the other materials).
            var f = script.AddFunction("fusexfilter", "c,e,tp,chk", [$"return {Join([.. Base(), "c:IsAbleToGrave()", "(chk or not c:IsImmuneToEffect(e))"])}"]);
            body.Add($"mg:Merge(Duel.GetMatchingGroup({f},tp,LOCATION_EXTRA,0,nil,e,tp,chk))");
        }
        if (src.Opponent)
        {
            var f = script.AddFunction("fusoppfilter", "c,e,tp,chk", [$"return {Join([.. Base("c:IsFaceup()"), "(chk or not c:IsImmuneToEffect(e))"])}"]);
            body.Add($"mg:Merge(Duel.GetMatchingGroup({f},tp,0,LOCATION_MZONE,nil,e,tp,chk))");
        }
        body.Add("return mg");
        var mats = script.AddFunction("fusmat", "e,tp,chk", body);

        var fusConds = FilterCompiler.Conditions(Fusion(ctx.Args)).Where(c => c is not "c:IsType(TYPE_MONSTER)" and not "c:IsType(TYPE_FUSION)").ToList();
        var fus = script.AddFunction("fusfilter", "c,e,tp,m,f,gc,chkf",
            [$"return {Join(["c:IsType(TYPE_FUSION)", .. fusConds, "(not f or f(c))", "c:IsCanBeSpecialSummoned(e,SUMMON_TYPE_FUSION,tp,false,false)", "c:CheckFusionMaterial(m,gc,chkf)"])}"]);
        var gc = self ? "c" : "nil";
        var handler = self ? new[] { "local c=e:GetHandler()" } : [];

        var target = script.AddFunction("fustg", "e,tp",
        [
            "local chkf=tp",
            .. handler,
            $"local mg={mats}(e,tp,true)",
            $"local res=Duel.IsExistingMatchingCard({fus},tp,LOCATION_EXTRA,0,1,nil,e,tp,mg,nil,{gc},chkf)",
            "if not res then",
            "\tlocal ce=Duel.GetChainMaterial(tp)",
            "\tif ce~=nil then",
            "\t\tlocal fgroup=ce:GetTarget()",
            "\t\tlocal mg3=fgroup(ce,e,tp)",
            "\t\tlocal mf=ce:GetValue()",
            $"\t\tres=Duel.IsExistingMatchingCard({fus},tp,LOCATION_EXTRA,0,1,nil,e,tp,mg3,mf,{gc},chkf)",
            "\tend",
            "end",
            "return res",
        ]);

        var send = src.Send switch
        {
            "banish" => new List<string> { "Duel.Remove(mat1,POS_FACEUP,REASON_EFFECT+REASON_MATERIAL+REASON_FUSION)" },
            // Face-down materials are shown to the opponent, the ones in the GY or banished are pointed out.
            "deck" =>
            [
                "local cg=mat1:Filter(Card.IsFacedown,nil)",
                "if #cg>0 then Duel.ConfirmCards(1-tp,cg) end",
                "local hg=mat1:Filter(Card.IsLocation,nil,LOCATION_GRAVE+LOCATION_REMOVED)",
                "if #hg>0 then Duel.HintSelection(hg) end",
                "Duel.SendtoDeck(mat1,nil,SEQ_DECKSHUFFLE,REASON_EFFECT+REASON_MATERIAL+REASON_FUSION)",
            ],
            _ => ["Duel.SendtoGrave(mat1,REASON_EFFECT+REASON_MATERIAL+REASON_FUSION)"],
        };
        switch (src.Send)
        {
            case "banish": ctx.Effect.AddCategory("CATEGORY_REMOVE"); break;
            case "deck": ctx.Effect.AddCategory("CATEGORY_TODECK"); break;
        }
        var op = script.AddFunction("fusop", "e,tp",
        [
            "local chkf=tp",
            .. handler,
            // "including this card": it must still be there and affected.
            .. self ? new[] { "if not c:IsRelateToEffect(e) or c:IsImmuneToEffect(e) then return nil end" } : [],
            $"local mg1={mats}(e,tp,false)",
            $"local sg1=Duel.GetMatchingGroup({fus},tp,LOCATION_EXTRA,0,nil,e,tp,mg1,nil,{gc},chkf)",
            "local mg2=nil",
            "local sg2=nil",
            "local ce=Duel.GetChainMaterial(tp)",
            "if ce~=nil then",
            "\tlocal fgroup=ce:GetTarget()",
            "\tmg2=fgroup(ce,e,tp)",
            "\tlocal mf=ce:GetValue()",
            $"\tsg2=Duel.GetMatchingGroup({fus},tp,LOCATION_EXTRA,0,nil,e,tp,mg2,mf,{gc},chkf)",
            "end",
            "if #sg1==0 and (sg2==nil or #sg2==0) then return nil end",
            "local sg=sg1:Clone()",
            "if sg2 then sg:Merge(sg2) end",
            "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)",
            "local tc=sg:Select(tp,1,1,nil):GetFirst()",
            // With Extra Deck materials, the Fusion Monster is taken out of them first, as Odd-Eyes Fusion does.
            .. src.Extra ? new[] { "mg1:RemoveCard(tc)" } : [],
            "if sg1:IsContains(tc) and (sg2==nil or not sg2:IsContains(tc) or not Duel.SelectYesNo(tp,ce:GetDescription())) then",
            $"\tlocal mat1=Duel.SelectFusionMaterial(tp,tc,mg1,{gc},chkf)",
            "\ttc:SetMaterial(mat1)",
            .. send.Select(l => "\t" + l),
            "\tDuel.BreakEffect()",
            "\tDuel.SpecialSummon(tc,SUMMON_TYPE_FUSION,tp,tp,false,false,POS_FACEUP)",
            "else",
            $"\tlocal mat2=Duel.SelectFusionMaterial(tp,tc,mg2,{gc},chkf)",
            "\tlocal fop=ce:GetOperation()",
            "\tfop(ce,e,tp,tc,mat2)",
            "end",
            "tc:CompleteProcedure()",
            "return tc",
        ]);

        ctx.Effect.AddCategory("CATEGORY_SPECIAL_SUMMON");
        ctx.Effect.AddCategory("CATEGORY_FUSION_SUMMON");
        ctx.Effect.TargetChecks.Add($"{target}(e,tp)");
        ctx.Effect.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_EXTRA)");
        var fc = ctx.Effect.NewLocal("fc");
        var r = ctx.Effect.NewLocal("r");
        var after = SummonedMonsterAfter.Lines(ctx, fc);
        ctx.Effect.AddStep(
        [
            $"local {fc}={op}(e,tp)",
            $"local {r}={fc} and 1 or 0",
            .. after.Count == 0 ? Array.Empty<string>() : [$"if {fc} then", .. after.Select(l => "\t" + l), "end"],
        ], r, ctx.Link);
    }
}
