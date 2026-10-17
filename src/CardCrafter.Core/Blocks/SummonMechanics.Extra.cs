using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>Wording shared by the Summon and Pendulum blocks below.</summary>
internal static class SummonWording
{
    /// <summary>
    /// Whether the block reads after the effect's "You can", with nothing before it: it is optional itself, or it is
    /// the first thing an optional effect does, with no cost ("You can make this card's Pendulum Scale become 7").
    /// </summary>
    public static bool AfterYouCan(BlockArgs args)
    {
        if (args.Instance.Optional) return true;
        if (args.Effect is not { When: { } when } effect || effect.Cost.Count > 0 || effect.Do.Count == 0 || !ReferenceEquals(effect.Do[0], args.Instance)) return false;
        var trigger = BlockRegistry.Find(when.BlockId);
        if (trigger is null or ActivateTrigger or WhileFaceUpTrigger or SummonProcedureTrigger || DetailedTriggers.IsCardActivation(trigger, when)) return false;
        var optional = when.Values.GetValueOrDefault("optional");
        return trigger is FlipTrigger ? optional == "true" : optional != "false";
    }

    /// <summary>"a" or "an" before a noun, looking past an opening quote: "an Xyz Monster", "a "D/D" monster".</summary>
    public static string Article(string noun)
    {
        var first = noun.TrimStart('"').FirstOrDefault();
        // "an Xyz Monster": Xyz is said with a vowel.
        if (noun.TrimStart('"').StartsWith("Xyz", StringComparison.Ordinal)) return "an";
        return "AEIOUaeiou".Contains(first) && first != default ? "an" : "a";
    }
}

/// <summary>
/// Wonder Xyz's "Immediately after this effect resolves, Xyz Summon 1 Xyz Monster using monsters you control", and the
/// same for Synchro and Link Summons: an Extra Deck Summon by effect, with any materials you control (or only some).
/// </summary>
public sealed class ExtraDeckSummonAction : ActionBlock
{
    public const string BlockId = "do.extra_deck_summon";
    public override string Id => BlockId;
    public override string Group => "Summon";
    public override string Name => "Synchro/Xyz/Link Summon using monsters you control";
    public override string Sentence => "{kind} Summon 1 {cards} using {using} {mats} {opp_mats}";
    public override string Help => "Like Wonder Xyz or Urgent Tuning. Use \"Link/Synchro/Xyz Summon using this card\" when this card must be one of the materials. " +
                                   "Combine it with a Quick Effect and \"during your opponent's turn\" to Summon on the opponent's turn.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("kind", "Summon", new("synchro", "Synchro"), new("xyz", "Xyz"), new("link", "Link")),
        ParamSpec.Cards("cards", "Which monster", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster }, Zone.ExtraDeck),
        ParamSpec.Choice("using", "Materials", new("control", "monsters you control", "any monsters you control"), new("only", "only", "only some monsters you control (picked below)"),
            // Yummy★Snatchy: "Synchro Summon using monsters you control as material, including a "Yummy" monster".
            new("including", "monsters you control, including", "monsters you control, including at least 1 picked below")),
        new ParamSpec
        {
            Key = "mats", Label = "Only these materials / including", Type = ParamType.Cards, WhereFromBlock = true, AllowedZones = Zone.MonsterZone,
            DefaultFilter = () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster },
        }.ShowIf(b => b.Values.GetValueOrDefault("using") is "only" or "including"),
        // W:P Fancy Ball: "Link Summon 1 Link Monster, and if this card you control would be used as material for that Link
        // Summon, you can also use 1 Link-2 or lower monster your opponent controls".
        ParamSpec.Flag("also_opp", "and if this card would be used as material, you can also use 1 monster your opponent controls (picked below)", false)
            .ShowIf(b => b.Values.GetValueOrDefault("kind") == "link" && b.Values.GetValueOrDefault("using", "control") == "control"),
        new ParamSpec
        {
            Key = "opp_mats", Label = "Your opponent's monster", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter
            {
                CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Link,
                Requirements = [new CardRequirement { Property = "link", Op = RequirementOp.LessOrEqual, Value = 2 }],
            },
        }.Unprinted().WhenUsed().ShowIf(b => b.Values.GetValueOrDefault("also_opp") == "true"),
    ];

    private static bool AlsoOpponents(BlockArgs args) => args.Choice("kind") == "link" && args.Choice("using") == "control" && args.Bool("also_opp");

    /// <summary>"1 Link-2 or lower monster": the opponent's monster this card lets the Link Summon use.</summary>
    private static CardFilter OpponentMaterial(BlockArgs args)
    {
        var f = args.Cards("opp_mats").Clone();
        f.Owner = CardOwner.Opponent;
        f.Zones = Zone.MonsterZone;
        f.CardType = FilterCardType.Monster;
        return f;
    }

    private static CardFilter Target(BlockArgs args)
    {
        var f = args.Cards("cards").Clone();
        f.CardType = FilterCardType.Monster;
        f.Owner = CardOwner.You;
        f.Zones = Zone.ExtraDeck;
        f.MonsterKind = args.Choice("kind") switch { "xyz" => MonsterKind.Xyz, "link" => MonsterKind.Link, _ => MonsterKind.Synchro };
        return f;
    }

    /// <summary>The monsters the Summon may use: face-up, in your Monster Zone.</summary>
    private static CardFilter Materials(BlockArgs args)
    {
        var f = args.Cards("mats").Clone();
        f.Owner = CardOwner.You;
        f.Zones = Zone.MonsterZone;
        f.CardType = FilterCardType.Monster;
        return f;
    }

    public override string Describe(BlockArgs args)
    {
        var kind = args.Describe("kind");
        var mats = args.Choice("using") == "only"
            ? $"using only {FilterText.Describe(Materials(args), args.Pack, plural: true, withLocation: false)} you control"
            : args.Choice("kind") == "link" ? "using monster(s) you control as material" : "using monsters you control as material";
        if (args.Choice("using") == "including")
        {
            var inc = FilterText.Describe(Materials(args), args.Pack, plural: false, withLocation: false);
            mats += $", including {SummonWording.Article(inc)} {inc}";
        }
        if (AlsoOpponents(args))
        {
            var opp = OpponentMaterial(args).Clone();
            opp.Owner = CardOwner.You;
            mats = $", and if this card you control would be used as material for that Link Summon, you can also use 1 {FilterText.Describe(opp, args.Pack, withLocation: false)} your opponent controls";
        }
        var text = $"{kind} Summon 1 {FilterText.Describe(Target(args), args.Pack, withLocation: false)}{(mats.StartsWith(',') ? "" : " ")}{mats}";
        // "You can Xyz Summon ...", but "..., then immediately after this effect resolves, Xyz Summon ...".
        return SummonWording.AfterYouCan(args) ? text : "immediately after this effect resolves, " + text;
    }

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Args.Choice("using") == "including")
        {
            CompileIncluding(ctx);
            return;
        }
        var kind = ctx.Args.Choice("kind");
        var only = ctx.Args.Choice("using") == "only";
        string? mg = null;
        var conds = FilterCompiler.Conditions(Target(ctx.Args));
        if (only)
        {
            var matConds = FilterCompiler.Conditions(Materials(ctx.Args));
            if (!matConds.Contains("c:IsFaceup()")) matConds.Insert(0, "c:IsFaceup()");
            mg = ctx.Script.AddFunction("edsmat", "c,e,tp", [$"return {string.Join(" and ", matConds)}"]);
        }
        var mgArg = mg is null ? "" : ",mg";
        conds.Add(kind switch
        {
            "xyz" => $"c:IsXyzSummonable({(mg is null ? "nil" : "mg")})",
            "link" => $"c:IsLinkSummonable({(mg is null ? "nil" : "mg")})",
            _ => mg is null ? "c:IsSynchroSummonable(nil)" : "c:IsSynchroSummonable(nil,mg)",
        });
        var filter = ctx.Script.AddFunction("edsfilter", "c,e,tp" + mgArg, [$"return {string.Join(" and ", conds)}"]);
        string Group(string v) => mg is null ? "" : $"local {v}=Duel.GetMatchingGroup({mg},tp,LOCATION_MZONE,0,nil,e,tp)";
        ctx.Effect.AddCategory("CATEGORY_SPECIAL_SUMMON");
        if (AlsoOpponents(ctx.Args))
        {
            OpponentMaterialSummon(ctx, filter);
            return;
        }
        // Only checked when the materials are there at activation: after an action that Summons them, it may fail.
        if (!ctx.Effect.HasSteps)
        {
            ctx.Effect.TargetChecks.Add(mg is null
                ? $"Duel.IsExistingMatchingCard({filter},tp,LOCATION_EXTRA,0,1,nil,e,tp)"
                : $"Duel.IsExistingMatchingCard({filter},tp,LOCATION_EXTRA,0,1,nil,e,tp,Duel.GetMatchingGroup({mg},tp,LOCATION_MZONE,0,nil,e,tp))");
        }
        ctx.Effect.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_EXTRA)");
        var r = ctx.Effect.NewLocal("r");
        var g = ctx.Effect.NewLocal("g");
        var sc = ctx.Effect.NewLocal("sc");
        var mv = ctx.Effect.NewLocal("mg");
        var summon = kind switch
        {
            "xyz" => $"Duel.XyzSummon(tp,{sc},{(mg is null ? "nil" : mv)})",
            "link" => $"Duel.LinkSummon(tp,{sc},{(mg is null ? "nil" : mv)})",
            _ => mg is null ? $"Duel.SynchroSummon(tp,{sc},nil)" : $"Duel.SynchroSummon(tp,{sc},nil,{mv})",
        };
        var body = new List<string> { $"local {r}=0" };
        if (mg is not null) body.Add(Group(mv));
        body.AddRange(
        [
            $"local {g}=Duel.GetMatchingGroup({filter},tp,LOCATION_EXTRA,0,nil,e,tp{(mg is null ? "" : "," + mv)})",
            $"if {g}:GetCount()>0 then",
            "\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)",
            $"\tlocal {sc}={g}:Select(tp,1,1,nil):GetFirst()",
            $"\t{summon}",
            $"\t{r}=1",
            "end",
        ]);
        ctx.Effect.AddStep(body, r, ctx.Link);
    }

    /// <summary>
    /// "..., and if this card you control would be used as material for that Link Summon, you can also use 1 Link-2 or
    /// lower monster your opponent controls": while the Link Summon is checked and done, an extra Link Material rule lets
    /// one such monster of the opponent's be a material alongside this card (as W:P Fancy Ball writes it).
    /// </summary>
    private static void OpponentMaterialSummon(BlockContext ctx, string filter)
    {
        var e = ctx.Effect;
        var opp = OpponentMaterial(ctx.Args).Clone();
        opp.Owner = CardOwner.Either;
        opp.Zones = Zone.None;
        var tests = FilterCompiler.Conditions(opp, "c", SummonProcedureKind.None, hasEffect: false);
        tests.Insert(0, "c:IsControler(1-tp) and c:IsFaceup()");
        var eligible = ctx.Script.AddFunction("oppmatfilter", "c,tp", [$"return {string.Join(" and ", tests)}"]);
        var mattg = ctx.Script.AddFunction("oppmattg", "e,c", [$"return {eligible}(c,e:GetHandlerPlayer())"]);
        var matval = ctx.Script.AddFunction("oppmatval", "e,lc,mg,c,tp",
        [
            "local h=e:GetLabelObject()",
            // This very card, still controlled by the Summoning player, must be among the materials; then one such monster is allowed.
            "if h:GetFieldID()~=e:GetLabel() or h:GetControler()~=tp then return false,nil end",
            $"if not {eligible}(c,tp) then return false,nil end",
            "if not mg or not mg:IsContains(h) then return true,false end",
            $"if mg:IsExists({eligible},1,c,tp) then return true,false end",
            "return true,true",
        ]);
        var reg = ctx.Script.AddFunction("oppmatreg", "c,tp,reset",
        [
            "local le=Effect.CreateEffect(c)",
            "le:SetType(EFFECT_TYPE_FIELD)",
            "le:SetCode(EFFECT_EXTRA_LINK_MATERIAL)",
            "le:SetTargetRange(0,LOCATION_MZONE)",
            "le:SetLabel(c:GetFieldID())",
            "le:SetLabelObject(c)",
            $"le:SetTarget({mattg})",
            $"le:SetValue({matval})",
            "if reset then le:SetReset(reset) end",
            "Duel.RegisterEffect(le,tp)",
            "return le",
        ]);
        var check = ctx.Script.AddFunction("oppmatcheck", "e,tp",
        [
            $"local le={reg}(e:GetHandler(),tp,nil)",
            $"local res=Duel.IsExistingMatchingCard({filter},tp,LOCATION_EXTRA,0,1,nil,e,tp)",
            "le:Reset()",
            "return res",
        ]);
        if (!e.HasSteps) e.TargetChecks.Add($"{check}(e,tp)");
        e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_EXTRA)");
        var r = e.NewLocal("r");
        var g = e.NewLocal("g");
        e.AddStep(
        [
            $"local {r}=0",
            $"if c:IsRelateToEffect(e) and c:IsControler(tp) and c:IsFaceup() then {reg}(c,tp,RESET_PHASE+PHASE_MAIN1+PHASE_MAIN2) end",
            $"local {g}=Duel.GetMatchingGroup({filter},tp,LOCATION_EXTRA,0,nil,e,tp)",
            $"if {g}:GetCount()>0 then",
            "\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)",
            $"\tDuel.LinkSummon(tp,{g}:Select(tp,1,1,nil):GetFirst(),nil)",
            $"\t{r}=1",
            "end",
        ], r, ctx.Link);
    }

    /// <summary>
    /// "Synchro Summon using monsters you control as material, including a "Yummy" monster": the materials are picked as a
    /// group that has such a monster, as Yummy★Snatchy's script does (Synchro and Xyz), or the Link Summon must use one.
    /// </summary>
    private void CompileIncluding(BlockContext ctx)
    {
        var kind = ctx.Args.Choice("kind");
        var conds = FilterCompiler.Conditions(Target(ctx.Args));
        var inc = ctx.Script.Filter(Materials(ctx.Args), "c:IsFaceup()", "edsinc").Function;
        var target = ctx.Script.AddFunction("edstarget", "c,e,tp", [$"return {string.Join(" and ", conds)}"]);
        // The monsters the Summon may use, and a check that a group of them, with one of the included kind, can Summon sc.
        var pool = kind == "synchro" ? "Duel.GetSynchroMaterial(tp)" : "Duel.GetMatchingGroup(Card.IsFaceup,tp,LOCATION_MZONE,0,nil)";
        string fits, summon;
        string? check = null;
        switch (kind)
        {
            case "link":
                // The Link Summon itself picks the rest: lc is a monster it must use.
                fits = ctx.Script.AddFunction("edsfits", "c,e,tp,mg",
                    [$"return {target}(c,e,tp) and mg:IsExists(function(lc) return {inc}(lc,e,tp) and c:IsLinkSummonable(nil,lc) end,1,nil)"]);
                summon = "";
                break;
            case "xyz":
                check = ctx.Script.AddFunction("edscheck", "g,e,tp,sc", [$"return g:IsExists({inc},1,nil,e,tp) and sc:IsXyzSummonable(g,#g,#g)"]);
                fits = ctx.Script.AddFunction("edsfits", "c,e,tp,mg", [$"return {target}(c,e,tp) and mg:CheckSubGroup({check},1,#mg,e,tp,c)"]);
                summon = "Duel.XyzSummon(tp,{0},{1},#{1},#{1})";
                break;
            default:
                check = ctx.Script.AddFunction("edscheck", "g,e,tp,sc", [$"return g:IsExists({inc},1,nil,e,tp) and sc:IsSynchroSummonable(nil,g,#g-1,#g-1)"]);
                fits = ctx.Script.AddFunction("edsfits", "c,e,tp,mg",
                [
                    $"if not {target}(c,e,tp) then return false end",
                    "aux.GCheckAdditional=aux.SynGroupCheckLevelAddition(c)",
                    $"local res=mg:CheckSubGroup({check},2,#mg,e,tp,c)",
                    "aux.GCheckAdditional=nil",
                    "return res",
                ]);
                summon = "Duel.SynchroSummon(tp,{0},nil,{1},#{1}-1,#{1}-1)";
                break;
        }
        ctx.Effect.AddCategory("CATEGORY_SPECIAL_SUMMON");
        if (!ctx.Effect.HasSteps)
            ctx.Effect.TargetChecks.Add($"Duel.IsPlayerCanSpecialSummon(tp) and Duel.IsExistingMatchingCard({fits},tp,LOCATION_EXTRA,0,1,nil,e,tp,{pool})");
        ctx.Effect.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_EXTRA)");
        var (r, mg, g, sc, tg) = (ctx.Effect.NewLocal("r"), ctx.Effect.NewLocal("mg"), ctx.Effect.NewLocal("g"), ctx.Effect.NewLocal("sc"), ctx.Effect.NewLocal("tg"));
        var body = new List<string>
        {
            $"local {r}=0",
            $"local {mg}={pool}",
            $"local {g}=Duel.GetMatchingGroup({fits},tp,LOCATION_EXTRA,0,nil,e,tp,{mg})",
            $"if {g}:GetCount()>0 then",
            "	Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)",
            $"	local {sc}={g}:Select(tp,1,1,nil):GetFirst()",
        };
        if (kind == "link")
        {
            body.AddRange(
            [
                "	Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_LMATERIAL)",
                $"	local {tg}={mg}:FilterSelect(tp,function(lc) return {inc}(lc,e,tp) and {sc}:IsLinkSummonable(nil,lc) end,1,1,nil):GetFirst()",
                $"	Duel.LinkSummon(tp,{sc},nil,{tg})",
            ]);
        }
        else
        {
            var hint = kind == "xyz" ? "HINTMSG_XMATERIAL" : "HINTMSG_SMATERIAL";
            if (kind == "synchro") body.Add($"	aux.GCheckAdditional=aux.SynGroupCheckLevelAddition({sc})");
            body.Add($"	Duel.Hint(HINT_SELECTMSG,tp,{hint})");
            body.Add($"	local {tg}={mg}:SelectSubGroup(tp,{check},false,{(kind == "xyz" ? "1" : "2")},#{mg},e,tp,{sc})");
            if (kind == "synchro") body.Add("	aux.GCheckAdditional=nil");
            body.Add($"	{string.Format(System.Globalization.CultureInfo.InvariantCulture, summon, sc, tg)}");
        }
        body.AddRange([$"	{r}=1", "end"]);
        ctx.Effect.AddStep(body, r, ctx.Link);
    }
}

/// <summary>
/// Rank-Up-Magic: "Target 1 Xyz Monster you control; Special Summon from your Extra Deck, 1 Xyz Monster that is 1 Rank
/// higher than that monster you control, by using it as material. (This is treated as an Xyz Summon. Transfer its
/// materials to the Summoned monster.)" Also Raptor's Force (Special Summon it from the GY first), Rank-Down-Magic,
/// and this card ranking itself up (Tellarknight Ptolemaeus).
/// </summary>
public sealed class RankUpAction : ActionBlock
{
    public const string BlockId = "do.rank_up";
    public override string Id => BlockId;
    public override string Group => "Summon";
    public override string Name => "Rank-Up: Xyz Summon using an Xyz Monster as material";
    public override string Sentence => "Target 1 {material}; Special Summon from your Extra Deck 1 {cards} {rank} {ranks} than it {same}, by using it as material";
    public override string Help => "Like the \"Rank-Up-Magic\" Spells. The monster's materials go to the new one. Pick the GY for Raptor's Force " +
                                   "(\"Special Summon it, then ...\"), or \"This card itself\" for a monster that ranks itself up.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new ParamSpec
        {
            Key = "material", Label = "Which Xyz Monster to use", Type = ParamType.Cards, AllowThisCard = true, AllowedZones = Zone.MonsterZone | Zone.Graveyard,
            DefaultFilter = () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz },
        },
        ParamSpec.Cards("cards", "Which Xyz Monster to Summon", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz }, Zone.ExtraDeck),
        ParamSpec.Choice("rank", "Its Rank",
            new("up", "higher", "N Ranks higher"), new("down", "lower", "N Ranks lower"), new("either", "higher or lower", "N Ranks higher or lower"),
            new("lower", "lower", "any lower Rank"), new("same", "same", "the same Rank"), new("any", "", "any Rank")),
        ParamSpec.Int("ranks", "Ranks", 1, 1, 12).ShowIf(b => b.Values.GetValueOrDefault("rank", "up") is "up" or "down" or "either"),
        ParamSpec.Choice("same", "Also the same", new("none", "", "nothing else"), new("race", "Type"), new("attribute", "Attribute"), new("both", "Type and Attribute")),
    ];

    /// <summary>"field" (an Xyz Monster you control), "gy" (Special Summoned from the GY first) or "self" (this card).</summary>
    private static string Where(BlockArgs args) =>
        args.Cards("material").ThisCard ? "self" : args.Cards("material").Zones == Zone.Graveyard ? "gy" : "field";

    private static CardFilter Material(BlockArgs args)
    {
        var f = args.Cards("material").Clone();
        f.Owner = CardOwner.You;
        f.CardType = FilterCardType.Monster;
        if (f.MonsterKind == MonsterKind.Any) f.MonsterKind = MonsterKind.Xyz;
        if (Where(args) == "field") f.Zones = Zone.MonsterZone;
        return f;
    }

    private static CardFilter Summoned(BlockArgs args)
    {
        var f = args.Cards("cards").Clone();
        f.Owner = CardOwner.You;
        f.Zones = Zone.ExtraDeck;
        f.CardType = FilterCardType.Monster;
        return f;
    }

    /// <summary>"that is 1 Rank higher than that monster you control", "with the same Type as that monster but 1 Rank higher".</summary>
    private static string Relation(BlockArgs args, string that)
    {
        var n = args.Int("ranks");
        var ranks = $"{n} Rank{(n == 1 ? "" : "s")}";
        var how = args.Choice("rank") switch
        {
            "up" => $"{ranks} higher",
            "down" => $"{ranks} lower",
            "either" => $"{ranks} higher or lower",
            _ => null,
        };
        var same = args.Choice("same") switch { "race" => "Type", "attribute" => "Attribute", "both" => "Type and Attribute", _ => null };
        if (same is null)
        {
            return args.Choice("rank") switch
            {
                "lower" => $"with a lower Rank than {that}",
                "same" => $"with the same Rank as {that}",
                "any" => "",
                _ => $"that is {how} than {that}",
            };
        }
        return args.Choice("rank") switch
        {
            "lower" => $"with the same {same} as {that} but a lower Rank",
            "same" => $"with the same Rank and {same} as {that}",
            "any" => $"with the same {same} as {that}",
            _ => $"with the same {same} as {that} but {how}",
        };
    }

    private static string Summon(BlockArgs args, string that, string usingIt)
    {
        var relation = Relation(args, that);
        var noun = FilterText.Describe(Summoned(args), args.Pack, withLocation: false);
        return $"Special Summon from your Extra Deck, 1 {noun}{(relation.Length == 0 ? "" : " " + relation)}, by using {usingIt} as material";
    }

    public override (string Target, string Action)? DescribeTargeting(BlockArgs args)
    {
        var where = Where(args);
        if (where == "self") return null;
        var target = $"target 1 {FilterText.Describe(Material(args), args.Pack, preposition: "in")}";
        return where == "gy"
            ? (target, "Special Summon it, then " + Summon(args, "that monster", "it"))
            : (target, Summon(args, "that monster you control", "it"));
    }

    public override string Describe(BlockArgs args) => DescribeTargeting(args) is { } t
        ? $"{t.Target}; {t.Action}"
        : Summon(args, "this card", "this face-up card you control");

    public override string Afterword(BlockArgs args) => Where(args) == "gy"
        ? "(This is treated as an Xyz Summon.)"
        : "(This is treated as an Xyz Summon. Transfer its materials to the Summoned monster.)";

    /// <summary>The check on the new Xyz Monster, given the monster <c>mc</c> used as material.</summary>
    private static List<string> SummonedChecks(BlockArgs args)
    {
        var checks = new List<string>();
        var n = Lua.Num(args.Int("ranks"));
        switch (args.Choice("rank"))
        {
            case "up": checks.Add($"c:IsRank(mc:GetRank()+{n})"); break;
            case "down": checks.Add($"c:IsRank(mc:GetRank()-{n})"); break;
            case "either": checks.Add($"(c:IsRank(mc:GetRank()+{n}) or c:IsRank(mc:GetRank()-{n}))"); break;
            case "lower": checks.Add("c:IsRankBelow(mc:GetRank()-1)"); break;
            case "same": checks.Add("c:IsRank(mc:GetRank())"); break;
        }
        if (args.Choice("same") is "race" or "both") checks.Add("c:IsRace(mc:GetRace())");
        if (args.Choice("same") is "attribute" or "both") checks.Add("c:IsAttribute(mc:GetAttribute())");
        checks.Add("c:IsType(TYPE_XYZ)");
        foreach (var cond in FilterCompiler.Conditions(Summoned(args)))
            if (!checks.Contains(cond)) checks.Add(cond);
        checks.Add("mc:IsCanBeXyzMaterial(c)");
        checks.Add("c:IsCanBeSpecialSummoned(e,SUMMON_TYPE_XYZ,tp,false,false) and Duel.GetLocationCountFromEx(tp,tp,mc,c)>0");
        return checks;
    }

    public override void Compile(BlockContext ctx)
    {
        var args = ctx.Args;
        var e = ctx.Effect;
        var where = Where(args);
        if (args.Cards("cards").Zones is not (Zone.ExtraDeck or Zone.None))
            e.Warn("\"Rank-Up\" Summons its new Xyz Monster from the Extra Deck.");
        if (where == "self" && !ctx.Card.IsXyz) e.Warn("\"Rank-Up\" using this card needs this card to be an Xyz Monster.");
        // As the official Rank-Up-Magic cards, "Number C88" needs "Number 88" under it.
        var sfilter = ctx.Script.AddFunction("rumfilter", "c,e,tp,mc",
        [
            "if c:GetOriginalCode()==6165656 and not mc:IsCode(48995978) then return false end",
            $"return {string.Join("\n\t\tand ", SummonedChecks(args))}",
        ]);
        e.AddCategory("CATEGORY_SPECIAL_SUMMON");
        var r = e.NewLocal("r");
        var tc = e.NewLocal("tc");
        var sc = e.NewLocal("sc");
        var mg = e.NewLocal("mg");
        var transfer = new List<string>
        {
            $"\tlocal {mg}={tc}:GetOverlayGroup()",
            $"\tif {mg}:GetCount()~=0 then",
            $"\t\tDuel.Overlay({sc},{mg})",
            "\tend",
        };
        List<string> SummonLines(bool breakFirst) =>
        [
            "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)",
            $"local {sc}=Duel.SelectMatchingCard(tp,{sfilter},tp,LOCATION_EXTRA,0,1,1,nil,e,tp,{tc}):GetFirst()",
            $"if {sc} then",
            .. breakFirst ? ["\tDuel.BreakEffect()"] : transfer,
            $"\t{sc}:SetMaterial(Group.FromCards({tc}))",
            $"\tDuel.Overlay({sc},Group.FromCards({tc}))",
            $"\t{r}=Duel.SpecialSummon({sc},SUMMON_TYPE_XYZ,tp,tp,false,false,POS_FACEUP)",
            $"\t{sc}:CompleteProcedure()",
            "end",
        ];
        var body = new List<string> { $"local {r}=0" };
        switch (where)
        {
            case "self":
                e.TargetChecks.Add("aux.MustMaterialCheck(e:GetHandler(),tp,EFFECT_MUST_BE_XMATERIAL)");
                e.TargetChecks.Add($"Duel.IsExistingMatchingCard({sfilter},tp,LOCATION_EXTRA,0,1,nil,e,tp,e:GetHandler())");
                e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_EXTRA)");
                body.Add($"local {tc}=c");
                body.Add($"if aux.MustMaterialCheck({tc},tp,EFFECT_MUST_BE_XMATERIAL) and {tc}:IsFaceup() and {tc}:IsRelateToEffect(e) and {tc}:IsControler(tp) and not {tc}:IsImmuneToEffect(e) then");
                body.AddRange(SummonLines(false).Select(l => "\t" + l));
                body.Add("end");
                break;
            case "gy":
            {
                var mconds = FilterCompiler.Conditions(Material(args));
                mconds.Remove("c:IsType(TYPE_XYZ)");
                var mfilter = ctx.Script.AddFunction("rummat", "c,e,tp",
                [
                    $"return {string.Join(" and ", ["c:IsType(TYPE_XYZ)", .. mconds])} and c:IsCanBeSpecialSummoned(e,0,tp,false,false)",
                    $"\t\tand aux.MustMaterialCheck(c,tp,EFFECT_MUST_BE_XMATERIAL)",
                    $"\t\tand Duel.IsExistingMatchingCard({sfilter},tp,LOCATION_EXTRA,0,1,nil,e,tp,c)",
                ]);
                e.SetChkc($"chkc:IsControler(tp) and chkc:IsLocation(LOCATION_GRAVE) and {mfilter}(chkc,e,tp)");
                e.TargetChecks.Add("Duel.IsPlayerCanSpecialSummonCount(tp,2)");
                e.TargetChecks.Add("Duel.GetLocationCount(tp,LOCATION_MZONE)>0");
                e.TargetChecks.Add($"Duel.IsExistingTarget({mfilter},tp,LOCATION_GRAVE,0,1,nil,e,tp)");
                var tg = e.NewLocal("tg");
                e.TargetLines.Add("Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)");
                e.TargetLines.Add($"local {tg}=Duel.SelectTarget(tp,{mfilter},tp,LOCATION_GRAVE,0,1,1,nil,e,tp)");
                e.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,{tg},2,tp,LOCATION_EXTRA)");
                body.Add($"local {tc}=Duel.GetFirstTarget()");
                body.Add($"if Duel.GetLocationCount(tp,LOCATION_MZONE)>0 and {tc} and {tc}:IsRelateToEffect(e) and not {tc}:IsImmuneToEffect(e)");
                body.Add($"\t\tand Duel.SpecialSummon({tc},0,tp,tp,false,false,POS_FACEUP)~=0 and aux.MustMaterialCheck({tc},tp,EFFECT_MUST_BE_XMATERIAL) then");
                body.AddRange(SummonLines(true).Select(l => "\t" + l));
                body.Add("end");
                break;
            }
            default:
            {
                var mconds = FilterCompiler.Conditions(Material(args));
                mconds.Remove("c:IsFaceup()");
                mconds.Remove("c:IsType(TYPE_XYZ)");
                var mfilter = ctx.Script.AddFunction("rummat", "c,e,tp",
                [
                    $"return {string.Join(" and ", ["c:IsFaceup()", "c:IsType(TYPE_XYZ)", .. mconds])}",
                    $"\t\tand Duel.IsExistingMatchingCard({sfilter},tp,LOCATION_EXTRA,0,1,nil,e,tp,c)",
                    "\t\tand aux.MustMaterialCheck(c,tp,EFFECT_MUST_BE_XMATERIAL)",
                ]);
                e.SetChkc($"chkc:IsControler(tp) and chkc:IsLocation(LOCATION_MZONE) and {mfilter}(chkc,e,tp)");
                e.TargetChecks.Add($"Duel.IsExistingTarget({mfilter},tp,LOCATION_MZONE,0,1,nil,e,tp)");
                e.TargetLines.Add("Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_TARGET)");
                e.TargetLines.Add($"Duel.SelectTarget(tp,{mfilter},tp,LOCATION_MZONE,0,1,1,nil,e,tp)");
                e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_EXTRA)");
                body.Add($"local {tc}=Duel.GetFirstTarget()");
                body.Add($"if {tc} and aux.MustMaterialCheck({tc},tp,EFFECT_MUST_BE_XMATERIAL) and {tc}:IsFaceup() and {tc}:IsRelateToEffect(e) and {tc}:IsControler(tp) and not {tc}:IsImmuneToEffect(e) then");
                body.AddRange(SummonLines(false).Select(l => "\t" + l));
                body.Add("end");
                break;
            }
        }
        e.AddStep(body, r, ctx.Link);
    }
}

/// <summary>
/// "You can make this card's Pendulum Scale become 7 until the end of this turn", "Increase this card's Pendulum Scale
/// by 1 (max. 12)", "Reduce this card's Pendulum Scale by 2 (min. 1)": a change to this card's Pendulum Scale.
/// </summary>
public sealed class PendulumScaleAction : ActionBlock
{
    public const string BlockId = "do.pendulum_scale";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "Change this card's Pendulum Scale";
    public override string Sentence => "This card's Pendulum Scale {mode} {amount} (limit {limit}) {duration}";
    public override string Help => "Like Performapal Trumpanda or Performapal Parrotrio. Put it under a WHEN block \"in the Pendulum Zone\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("mode", "Change", new("becomes", "becomes"), new("increase", "increases by", "increases by (max.)"), new("reduce", "decreases by", "decreases by (min.)")),
        ParamSpec.Int("amount", "Scale / by", 1, 0, 13),
        ParamSpec.Int("limit", "Max. / min.", 12, 0, 13).ShowIf(b => b.Values.GetValueOrDefault("mode", "becomes") != "becomes"),
        ParamSpec.Choice("duration", "Until", new("turn", "until the end of this turn"), new("always", "", "while face-up")),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("amount");
        var until = args.Choice("duration") == "turn" ? " until the end of this turn" : "";
        return args.Choice("mode") switch
        {
            "increase" => $"increase this card's Pendulum Scale by {n} (max. {args.Int("limit")}){until}",
            "reduce" => $"reduce this card's Pendulum Scale by {n} (min. {args.Int("limit")}){until}",
            _ => SummonWording.AfterYouCan(args)
                ? $"make this card's Pendulum Scale become {n}{until}"
                : $"this card's Pendulum Scale becomes {n}{until}",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        if (!ctx.Card.IsPendulum) ctx.Effect.Warn("Only Pendulum Monsters have a Pendulum Scale: tick Pendulum on the Card tab.");
        var n = ctx.Args.Int("amount");
        var limit = Lua.Num(ctx.Args.Int("limit"));
        var mode = ctx.Args.Choice("mode");
        var (check, guard, code, value) = mode switch
        {
            "increase" => ($"e:GetHandler():GetLeftScale()<{limit}", $"c:GetLeftScale()<{limit}", "UPDATE", $"math.min({limit}-c:GetLeftScale(),{Lua.Num(n)})"),
            "reduce" => ($"e:GetHandler():GetLeftScale()>{limit}", $"c:GetLeftScale()>{limit}", "UPDATE", $"-math.min(c:GetLeftScale()-{limit},{Lua.Num(n)})"),
            _ => ($"e:GetHandler():GetLeftScale()~={Lua.Num(n)}", $"c:GetLeftScale()~={Lua.Num(n)}", "CHANGE", Lua.Num(n)),
        };
        // A change that would do nothing can't be activated, as on the official cards.
        if (!ctx.Effect.HasSteps) ctx.Effect.TargetChecks.Add(check);
        var reset = ctx.Args.Choice("duration") == "turn"
            ? "RESET_EVENT+RESETS_STANDARD+RESET_DISABLE+RESET_PHASE+PHASE_END"
            : "RESET_EVENT+RESETS_STANDARD+RESET_DISABLE";
        var r = ctx.Effect.NewLocal("r");
        var e1 = ctx.Effect.NewLocal("se");
        var e2 = ctx.Effect.NewLocal("se");
        ctx.Effect.AddStep(
        [
            $"local {r}=0",
            $"if c:IsRelateToEffect(e) and {guard} then",
            $"\tlocal {e1}=Effect.CreateEffect(c)",
            $"\t{e1}:SetType(EFFECT_TYPE_SINGLE)",
            $"\t{e1}:SetCode(EFFECT_{code}_LSCALE)",
            $"\t{e1}:SetValue({value})",
            $"\t{e1}:SetReset({reset})",
            $"\tc:RegisterEffect({e1})",
            $"\tlocal {e2}={e1}:Clone()",
            $"\t{e2}:SetCode(EFFECT_{code}_RSCALE)",
            $"\tc:RegisterEffect({e2})",
            $"\t{r}=1",
            "end",
        ], r, ctx.Link);
    }
}

/// <summary>Stargazer Magician's "this card's Pendulum Scale becomes 4" (usually with a condition): a lasting change to the Scale.</summary>
public sealed class PendulumScalePassive : PassiveBlock
{
    public const string BlockId = "passive.pendulum_scale";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card's Pendulum Scale becomes / changes";
    public override string Sentence => "This card's Pendulum Scale {mode} {amount}";
    public override string Help => "Like Stargazer Magician. Put it under \"While this card is face-up\" \"in the Pendulum Zone\", with an IF block for \"unless ...\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("mode", "Change", new("becomes", "becomes"), new("increase", "is increased by"), new("reduce", "is decreased by")),
        ParamSpec.Int("amount", "Scale / by", 4, 0, 13),
    ];

    public override void Compile(BlockContext ctx)
    {
        if (!ctx.Card.IsPendulum) ctx.Effect.Warn("Only Pendulum Monsters have a Pendulum Scale: tick Pendulum on the Card tab.");
        else if (ctx.Effect.Range != "LOCATION_PZONE") ctx.Effect.Warn("A Pendulum Scale only counts in the Pendulum Zone: set the WHEN block to \"in the Pendulum Zone\".");
        var n = ctx.Args.Int("amount");
        var (code, value) = ctx.Args.Choice("mode") switch
        {
            "increase" => ("UPDATE", Lua.Num(n)),
            "reduce" => ("UPDATE", Lua.Num(-n)),
            _ => ("CHANGE", Lua.Num(n)),
        };
        var range = RangeOf(ctx);
        foreach (var side in new[] { "LSCALE", "RSCALE" })
            ctx.Effect.AddPassive(v => SelfEffect(v, range, $"EFFECT_{code}_{side}", value));
    }
}

/// <summary>
/// Extra Pendulum's "During your Main Phase this turn, you can conduct 1 Pendulum Summon of a monster(s) from your Extra
/// Deck in addition to your Pendulum Summon. (You can only gain this effect once per turn.)"
/// </summary>
public sealed class ExtraPendulumAction : ActionBlock
{
    public const string BlockId = "do.extra_pendulum";
    public override string Id => BlockId;
    public override string Group => "Turn";
    public override string Name => "You can Pendulum Summon an extra time this turn";
    public override string Sentence => "During your Main Phase this turn, you can conduct 1 Pendulum Summon of {cards} {from} in addition to your Pendulum Summon";
    public override string Help => "Like Extra Pendulum or D/D/D Sky King Zeus Ragnarok. The extra Pendulum Summon can only Summon the monsters picked here.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { CardType = FilterCardType.Monster }, Zone.Hand | Zone.ExtraDeck),
        ParamSpec.Choice("from", "From", new("any", "", "from the hand or Extra Deck"), new("hand", "from your hand"), new("extra", "from your Extra Deck")),
    ];

    /// <summary>The flag and count code of "you can only gain this effect once per turn".</summary>
    private const string Flag = "id+1150";

    private static CardFilter Monsters(BlockArgs args)
    {
        var f = args.Cards("cards").Clone();
        f.CardType = FilterCardType.Monster;
        f.Zones = Zone.None;
        f.FaceUpOnly = false;
        return f;
    }

    public override string Describe(BlockArgs args)
    {
        var noun = FilterText.Describe(Monsters(args), args.Pack, withLocation: false);
        var from = args.Describe("from") is { Length: > 0 } f ? " " + f : "";
        return $"during your Main Phase this turn, you can conduct 1 Pendulum Summon of {SummonWording.Article(noun)} {noun}(s){from} in addition to your Pendulum Summon";
    }

    public override string Afterword(BlockArgs args) => "(You can only gain this effect once per turn.)";

    public override void Compile(BlockContext ctx)
    {
        var conds = FilterCompiler.Conditions(Monsters(ctx.Args), hasEffect: false);
        conds.Remove("c:IsType(TYPE_MONSTER)");
        switch (ctx.Args.Choice("from"))
        {
            case "hand": conds.Add("c:IsLocation(LOCATION_HAND)"); break;
            case "extra": conds.Add("c:IsLocation(LOCATION_EXTRA)"); break;
        }
        var value = conds.Count == 0 ? "aux.TRUE" : ctx.Script.AddFunction("pendvalue", "e,c", [$"return {string.Join(" and ", conds)}"]);
        // On its own it can't be activated again once gained, as Extra Pendulum.
        if (ctx.Effect.Source.Do.Count == 1) ctx.Effect.TargetChecks.Add($"Duel.GetFlagEffect(tp,{Flag})==0");
        var desc = Math.Min(ctx.Effect.Index, 16) - 1;
        var v = ctx.Effect.NewLocal("pe");
        ctx.Effect.AddStep(
        [
            $"if Duel.GetFlagEffect(tp,{Flag})==0 then",
            $"\tlocal {v}=Effect.CreateEffect(e:GetHandler())",
            $"\t{v}:SetDescription(aux.Stringid(id,{desc.ToString(CultureInfo.InvariantCulture)}))",
            $"\t{v}:SetType(EFFECT_TYPE_FIELD)",
            $"\t{v}:SetCode(EFFECT_EXTRA_PENDULUM_SUMMON)",
            $"\t{v}:SetProperty(EFFECT_FLAG_PLAYER_TARGET)",
            $"\t{v}:SetTargetRange(1,0)",
            $"\t{v}:SetCountLimit(1,{Flag})",
            $"\t{v}:SetValue({value})",
            $"\t{v}:SetReset(RESET_PHASE+PHASE_END)",
            $"\tDuel.RegisterEffect({v},tp)",
            $"\tDuel.RegisterFlagEffect(tp,{Flag},RESET_PHASE+PHASE_END,0,1)",
            "end",
        ], null, ctx.Link);
    }
}
