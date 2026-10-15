using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// I:P Masquerena's "Link Summon 1 Link Monster using materials you control, including this card", and the same for
/// Synchro and Xyz Summons: an Extra Deck Summon in the middle of an effect, with this card as one of the materials.
/// </summary>
public sealed class SummonUsingThisAction : ActionBlock
{
    public const string BlockId = "do.summon_using_this";
    public override string Id => BlockId;
    public override string Group => "Summon";
    public override string Name => "Link/Synchro/Xyz Summon using this card";
    public override string Sentence => "{kind} Summon 1 {cards} using materials you control, including this card";
    public override string Help => "Like I:P Masquerena. Combine it with a Quick Effect \"during the Main Phase\" and \"during your opponent's turn\" " +
                                   "to Summon on the opponent's turn. The Summoned monster must be able to use this card as one of its materials.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("kind", "Summon", new("link", "Link"), new("synchro", "Synchro"), new("xyz", "Xyz")),
        ParamSpec.Cards("cards", "Which monster", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster }, Zone.ExtraDeck),
    ];

    private static CardFilter Target(BlockArgs args)
    {
        var f = args.Cards("cards").Clone();
        f.CardType = FilterCardType.Monster;
        f.Owner = CardOwner.You;
        f.Zones = Zone.ExtraDeck;
        f.MonsterKind = args.Choice("kind") switch { "synchro" => MonsterKind.Synchro, "xyz" => MonsterKind.Xyz, _ => MonsterKind.Link };
        return f;
    }

    public override string Describe(BlockArgs args) =>
        $"{args.Describe("kind")} Summon 1 {FilterText.Describe(Target(args), args.Pack, withLocation: false)} using materials you control, including this card";

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var kind = ctx.Args.Choice("kind");
        var tuner = ctx.Card.Abilities.HasFlag(MonsterAbility.Tuner);
        var conds = FilterCompiler.Conditions(Target(ctx.Args));
        // A Tuner is the Synchro Summon's Tuner; any other material is made a must-use material for the check and the Summon.
        var must = kind == "xyz" || kind == "synchro" && !tuner ? (kind == "xyz" ? "EFFECT_MUST_BE_XMATERIAL" : "EFFECT_MUST_BE_SMATERIAL") : null;
        conds.Add(kind switch
        {
            "synchro" => tuner ? "c:IsSynchroSummonable(mc)" : "c:IsSynchroSummonable(nil)",
            "xyz" => "c:IsXyzSummonable(nil)",
            _ => "c:IsLinkSummonable(nil,mc)",
        });
        var filter = ctx.Script.AddFunction("ustfilter", "c,mc", [$"return {string.Join(" and ", conds)}"]);
        string? mustFn = must is null ? null : ctx.Script.AddFunction("ustmust", "mc",
        [
            "local e1=Effect.CreateEffect(mc)",
            "e1:SetType(EFFECT_TYPE_FIELD)",
            "e1:SetRange(LOCATION_MZONE)",
            $"e1:SetCode({must})",
            "e1:SetProperty(EFFECT_FLAG_PLAYER_TARGET+EFFECT_FLAG_CANNOT_DISABLE)",
            "e1:SetTargetRange(1,0)",
            "e1:SetReset(RESET_CHAIN)",
            "mc:RegisterEffect(e1,true)",
            "return e1",
        ]);
        var check = ctx.Script.AddFunction("ustcheck", "e,tp", mustFn is null
            ? [$"return Duel.IsExistingMatchingCard({filter},tp,LOCATION_EXTRA,0,1,nil,e:GetHandler())"]
            : [$"local me={mustFn}(e:GetHandler())", $"local res=Duel.IsExistingMatchingCard({filter},tp,LOCATION_EXTRA,0,1,nil,e:GetHandler())", "me:Reset()", "return res"]);
        ctx.Effect.AddCategory("CATEGORY_SPECIAL_SUMMON");
        ctx.Effect.TargetChecks.Add($"{check}(e,tp)");
        ctx.Effect.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_EXTRA)");
        var r = ctx.Effect.NewLocal("r");
        var g = ctx.Effect.NewLocal("g");
        var sc = ctx.Effect.NewLocal("sc");
        var summon = kind switch
        {
            "synchro" => tuner ? $"Duel.SynchroSummon(tp,{sc},c)" : $"Duel.SynchroSummon(tp,{sc},nil)",
            "xyz" => $"Duel.XyzSummon(tp,{sc},nil)",
            _ => $"Duel.LinkSummon(tp,{sc},nil,c)",
        };
        var body = new List<string>
        {
            $"local {r}=0",
            "if c:IsRelateToEffect(e) and c:IsFaceup() and c:IsControler(tp) then",
        };
        if (mustFn is not null) body.Add($"\t{mustFn}(c)");
        body.AddRange(
        [
            $"\tlocal {g}=Duel.GetMatchingGroup({filter},tp,LOCATION_EXTRA,0,nil,c)",
            $"\tif {g}:GetCount()>0 then",
            "\t\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)",
            $"\t\tlocal {sc}={g}:Select(tp,1,1,nil):GetFirst()",
            $"\t\t{summon}",
            $"\t\t{r}=1",
            "\tend",
            "end",
        ]);
        ctx.Effect.AddStep(body, r, ctx.Link);
    }
}

/// <summary>
/// I:P Masquerena's "A Link Monster that used this card as material cannot be destroyed by your opponent's card
/// effects": the monster Summoned with this card as material gains an ability. It isn't an effect that activates.
/// </summary>
public sealed class MaterialGrantPassive : PassiveBlock
{
    public const string BlockId = "passive.material_grant";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "A monster Summoned using this card as material gains...";
    public override string Sentence => "A {kind} that used this card as material {gets} {amount}";
    public override string Help => "Like I:P Masquerena. Put it under \"While this card is face-up\"; it works from wherever this card is when it is used as material.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("kind", "Summon",
            new("link", "Link Monster"), new("synchro", "Synchro Monster"), new("xyz", "Xyz Monster"),
            new("fusion", "Fusion Monster"), new("ritual", "Ritual Monster"), new("any", "monster", "any monster (Fusion, Synchro, Xyz, Link or Ritual)")),
        ParamSpec.Choice("gets", "Gains",
            new("indes_opp_effect", "cannot be destroyed by your opponent's card effects"),
            new("indes_battle", "cannot be destroyed by battle"),
            new("untargetable_opp", "cannot be targeted by your opponent's card effects"),
            new("immune_opp", "is unaffected by your opponent's card effects"),
            new("atk", "gains", "gains ATK"),
            new("pierce", "inflicts piercing battle damage", "inflicts piercing battle damage"),
            new("double_attack", "can make a second attack during each Battle Phase")),
        ParamSpec.Int("amount", "ATK", 500, 100, 9999).ShowIf(b => b.Values.GetValueOrDefault("gets") == "atk"),
    ];

    public override string Describe(BlockArgs args)
    {
        var gets = args.Choice("gets") == "atk" ? $"gains {args.Int("amount")} ATK" : args.Describe("gets");
        var kind = args.Describe("kind");
        return $"{("AEIOUX".Contains(kind[0]) ? "An" : "A")} {kind} that used this card as material {gets}";
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var reason = ctx.Args.Choice("kind") switch
        {
            "synchro" => "REASON_SYNCHRO",
            "xyz" => "REASON_XYZ",
            "fusion" => "REASON_FUSION",
            "ritual" => "REASON_RITUAL",
            "any" => "REASON_FUSION+REASON_SYNCHRO+REASON_XYZ+REASON_LINK+REASON_RITUAL",
            _ => "REASON_LINK",
        };
        var (code, value) = ctx.Args.Choice("gets") switch
        {
            "indes_battle" => ("EFFECT_INDESTRUCTABLE_BATTLE", "1"),
            "untargetable_opp" => ("EFFECT_CANNOT_BE_EFFECT_TARGET", ctx.Script.AddFunction("mgtgval", "e,re,rp", ["return rp==1-e:GetOwnerPlayer()"])),
            "immune_opp" => ("EFFECT_IMMUNE_EFFECT", ctx.Script.AddFunction("mgimval", "e,te", ["return te:GetOwnerPlayer()~=e:GetOwnerPlayer()"])),
            "atk" => ("EFFECT_UPDATE_ATTACK", Lua.Num(ctx.Args.Int("amount"))),
            "pierce" => ("EFFECT_PIERCE", null),
            "double_attack" => ("EFFECT_EXTRA_ATTACK", "1"),
            _ => ("EFFECT_INDESTRUCTABLE_EFFECT", ctx.Script.AddFunction("mgindval", "e,re,rp", ["return rp==1-e:GetOwnerPlayer()"])),
        };
        var desc = Math.Min(ctx.Effect.Index, 16) - 1;
        var op = ctx.Script.AddFunction("mgop", "e,tp,eg,ep,ev,re,r,rp",
        [
            $"if bit.band(r,{reason})==0 then return end",
            "local c=e:GetHandler()",
            "local rc=c:GetReasonCard()",
            "if not rc then return end",
            "local e1=Effect.CreateEffect(c)",
            $"e1:SetDescription(aux.Stringid(id,{desc}))",
            "e1:SetType(EFFECT_TYPE_SINGLE)",
            "e1:SetProperty(EFFECT_FLAG_CLIENT_HINT)",
            $"e1:SetCode({code})",
            .. value is null ? Array.Empty<string>() : [$"e1:SetValue({value})"],
            "e1:SetOwnerPlayer(ep)",
            "e1:SetReset(RESET_EVENT+RESETS_STANDARD)",
            "rc:RegisterEffect(e1,true)",
        ]);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_SINGLE+EFFECT_TYPE_CONTINUOUS)",
            $"{v}:SetCode(EVENT_BE_MATERIAL)",
            $"{v}:SetProperty(EFFECT_FLAG_EVENT_PLAYER)",
            $"{v}:SetOperation({op})",
        ]);
    }
}

/// <summary>
/// S:P Little Knight's "If this card is Link Summoned using a Fusion, Synchro, Xyz, or Link Monster as material", and
/// "using 3 or more materials": what this card's Summon used.
/// </summary>
public sealed class SummonedUsingCondition : ConditionBlock
{
    public const string BlockId = "if.summoned_using";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card was Summoned using certain materials";
    public override string Sentence => "This card was Summoned using {count} {kind} as material";
    public override string Help => "Like S:P Little Knight. With \"If this card is Link Summoned\" the card reads \"If this card is Link Summoned using ... as material\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "At least", 1, 1, 6),
        ParamSpec.Choice("kind", "Material", Kinds),
    ];

    /// <summary>"using a Fusion, Synchro, Xyz, or Link Monster as material", "using 3 or more monsters as material".</summary>
    public static string Using(BlockArgs args)
    {
        var n = args.Int("count");
        var kind = args.Describe("kind");
        if (n == 1) return $"using {("AEIOUX".Contains(kind[0]) ? "an" : "a")} {kind} as material";
        var plural = args.Choice("kind") switch { "extra" => "Fusion, Synchro, Xyz, or Link Monsters", "monster" => "monsters", _ => kind + "s" };
        return $"using {n} or more {plural} as material";
    }

    public override string Describe(BlockArgs args) => "this card was Summoned " + Using(args);

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Script.AddFunction("matfilter", "c", [$"return {Check(ctx.Args.Choice("kind"))}"]);
        ctx.Effect.Conditions.Add($"e:GetHandler():GetMaterial():FilterCount({f},nil)>={ctx.Args.Int("count")}");
    }

    /// <summary>The check a material must pass for each kind.</summary>
    public static string Check(string kind) => kind switch
        {
            "extra" => "c:IsType(TYPE_FUSION+TYPE_SYNCHRO+TYPE_XYZ+TYPE_LINK)",
            "effect" => "c:IsType(TYPE_EFFECT)",
            "normal" => "c:IsType(TYPE_NORMAL)",
            "tuner" => "c:IsType(TYPE_TUNER)",
            "fusion" => "c:IsType(TYPE_FUSION)",
            "synchro" => "c:IsType(TYPE_SYNCHRO)",
            "xyz" => "c:IsType(TYPE_XYZ)",
            "link" => "c:IsType(TYPE_LINK)",
            "ritual" => "c:IsType(TYPE_RITUAL)",
            "token" => "c:IsType(TYPE_TOKEN)",
            _ => "c:IsType(TYPE_MONSTER)",
        };

    public static readonly ChoiceOption[] Kinds =
    [
        new("extra", "Fusion, Synchro, Xyz, or Link Monster"),
        new("monster", "monster"), new("effect", "Effect Monster"), new("normal", "Normal Monster"), new("tuner", "Tuner"),
        new("fusion", "Fusion Monster"), new("synchro", "Synchro Monster"), new("xyz", "Xyz Monster"), new("link", "Link Monster"),
        new("ritual", "Ritual Monster"), new("token", "Token"),
    ];
}

/// <summary>
/// Red-Eyes Dark Dragoon's "You can use this effect a number of times per turn up to the number of Normal Monsters
/// used as material for this card": how often the effect can be used comes from this card's materials.
/// </summary>
public sealed class UsesPerMaterialsCondition : ConditionBlock
{
    public const string BlockId = "if.uses_per_materials";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "Usable as many times per turn as the materials of a kind";
    public override string Sentence => "Usable a number of times per turn up to the number of {kind}s used as material for this card";
    public override string Help => "Like Red-Eyes Dark Dragoon. Leave the effect's own once-per-turn limit off.";
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Choice("kind", "Material", SummonedUsingCondition.Kinds)];

    /// <summary>It reads as a sentence after the effect, as the card prints it.</summary>
    public override string Describe(BlockArgs args) => "";

    public override string Afterword(BlockArgs args)
    {
        var kind = args.Describe("kind");
        var plural = args.Choice("kind") switch { "extra" => "Fusion, Synchro, Xyz, or Link Monsters", "monster" => "monsters", _ => kind + "s" };
        return $"You can use this effect a number of times per turn up to the number of {plural} used as material for this card.";
    }

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Script.AddFunction("upmfilter", "c", [$"return {SummonedUsingCondition.Check(ctx.Args.Choice("kind"))}"]);
        // Materials only count when this card was Summoned with them, the way its frame is Summoned.
        var summon = ctx.Card.Frame switch
        {
            MonsterFrame.Fusion => "SUMMON_TYPE_FUSION", MonsterFrame.Synchro => "SUMMON_TYPE_SYNCHRO", MonsterFrame.Xyz => "SUMMON_TYPE_XYZ",
            MonsterFrame.Link => "SUMMON_TYPE_LINK", MonsterFrame.Ritual => "SUMMON_TYPE_RITUAL", _ => null,
        };
        var count = ctx.Script.AddFunction("upmcount", "c",
        [
            summon is null ? "if c:GetMaterial()==nil then return 0 end" : $"if not c:IsSummonType({summon}) then return 0 end",
            $"return c:GetMaterial():FilterCount({f},nil)",
        ]);
        var flag = $"id+{7000 + ctx.Effect.Index}";
        ctx.Effect.Conditions.Add($"e:GetHandler():GetFlagEffect({flag})<{count}(e:GetHandler())");
        // Each use this turn is remembered on the card.
        ctx.Effect.TargetLines.Add($"e:GetHandler():RegisterFlagEffect({flag},RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,0,1)");
    }
}

/// <summary>
/// "Place 1 Continuous Trap from your Deck face-up in your Spell & Trap Zone", "place 1 Field Spell from your Deck
/// face-up in your Field Zone", "place this card face-up in your Spell & Trap Zone as a Continuous Spell".
/// </summary>
public sealed class PlaceAction : CardGroupAction
{
    public const string BlockId = "do.place";
    protected override bool AllowsThisCard => true;
    protected override bool AllowsAll => false;
    protected override int MaxCount => 5;
    protected override string TargetedWording => "place {0} face-up";
    public override string Id => BlockId;
    public override string Name => "Place cards face-up (Spell & Trap, Field or Pendulum Zone)";
    public override string Sentence => "Place {count} {cards} {target} face-up {where} {as}";
    public override string Help => "Like \"place 1 Continuous Trap from your Deck face-up in your Spell & Trap Zone\". Placed cards aren't activated. " +
                                   "\"As a Continuous Spell\" also places monsters, like the Crystal Beasts.";
    protected override string Category => "";
    protected override string Hint => "HINTMSG_TOFIELD";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.SpellTrap, SpellTrapKind = SpellTrapKind.Continuous };

    public override IReadOnlyList<MenuShortcut> Shortcuts { get; } =
    [
        new("Place 1 Continuous Spell/Trap from your hand or Deck face-up", MenuShortcut.NoValues,
            new Dictionary<string, Func<CardFilter>> { ["cards"] = () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck, CardType = FilterCardType.SpellTrap, SpellTrapKind = SpellTrapKind.Continuous } }),
        new("Place 1 Field Spell from your hand or Deck in your Field Zone", new Dictionary<string, string> { ["where"] = "fzone" },
            new Dictionary<string, Func<CardFilter>> { ["cards"] = () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck, CardType = FilterCardType.Spell, SpellTrapKind = SpellTrapKind.Field } }),
        new("Place this card face-up as a Continuous Spell", new Dictionary<string, string> { ["as"] = "continuous_spell" },
            new Dictionary<string, Func<CardFilter>> { ["cards"] = () => new CardFilter { ThisCard = true } }),
        new("Place cards face-up (pick which and where)", MenuShortcut.NoValues),
    ];

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        .. base.BuildParams(),
        ParamSpec.Choice("where", "Where", new("szone", "in your Spell & Trap Zone"), new("fzone", "in your Field Zone"), new("pzone", "in your Pendulum Zone")),
        ParamSpec.Choice("as", "As", new("keep", "", "as itself"), new("continuous_spell", "as a Continuous Spell"), new("continuous_trap", "as a Continuous Trap")),
    ];

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) =>
        $"place {pronoun} face-up {args.Describe("where")}" + (args.Choice("as") == "keep" ? "" : " " + args.Describe("as"));

    private static string Location(BlockContext ctx) => ctx.Args.Choice("where") switch { "fzone" => "LOCATION_FZONE", "pzone" => "LOCATION_PZONE", _ => "LOCATION_SZONE" };

    protected override string? AbleCheck(BlockContext ctx) => ctx.Args.Choice("where") switch
    {
        "fzone" => "c:IsType(TYPE_FIELD) and not c:IsForbidden()",
        "pzone" => "c:IsType(TYPE_PENDULUM) and not c:IsForbidden()",
        _ => ctx.Args.Choice("as") == "keep" ? "c:IsType(TYPE_SPELL+TYPE_TRAP) and not c:IsType(TYPE_FIELD) and not c:IsForbidden() and c:CheckUniqueOnField(tp)"
            : "not c:IsForbidden() and c:CheckUniqueOnField(tp)",
    };

    protected override string? SpaceCheck(int count, CardFilter f, BlockContext ctx) => ctx.Args.Choice("where") switch
    {
        "fzone" => null,
        "pzone" => count > 1 ? "(Duel.CheckLocation(tp,LOCATION_PZONE,0) and Duel.CheckLocation(tp,LOCATION_PZONE,1))"
            : "(Duel.CheckLocation(tp,LOCATION_PZONE,0) or Duel.CheckLocation(tp,LOCATION_PZONE,1))",
        _ => $"Duel.GetLocationCount(tp,LOCATION_SZONE)>={count}",
    };

    protected override string Apply(string g, BlockContext ctx)
    {
        var loc = Location(ctx);
        var type = ctx.Args.Choice("as") switch { "continuous_spell" => "TYPE_SPELL+TYPE_CONTINUOUS", "continuous_trap" => "TYPE_TRAP+TYPE_CONTINUOUS", _ => null };
        if (ctx.Args.Choice("where") != "szone" && type is not null)
            ctx.Effect.Warn("\"As a Continuous Spell/Trap\" places cards in the Spell & Trap Zone; pick that zone.");
        var body = new List<string> { "local n=0", "local tc=g:GetFirst()", "while tc do" };
        // A new Field Spell replaces the old one, which goes to the GY.
        if (loc == "LOCATION_FZONE")
            body.AddRange(["\tlocal fc=Duel.GetFieldCard(tp,LOCATION_FZONE,0)", "\tif fc then Duel.SendtoGrave(fc,REASON_RULE) Duel.BreakEffect() end"]);
        body.Add($"\tif Duel.MoveToField(tc,tp,tp,{loc},POS_FACEUP,true) then");
        body.Add("\t\tn=n+1");
        if (type is not null)
        {
            body.AddRange(
            [
                "\t\tlocal e1=Effect.CreateEffect(e:GetHandler())",
                "\t\te1:SetCode(EFFECT_CHANGE_TYPE)",
                "\t\te1:SetType(EFFECT_TYPE_SINGLE)",
                "\t\te1:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
                "\t\te1:SetReset(RESET_EVENT+RESETS_STANDARD-RESET_TURN_SET)",
                $"\t\te1:SetValue({type})",
                "\t\ttc:RegisterEffect(e1)",
            ]);
        }
        body.AddRange(["\tend", "\ttc=g:GetNext()", "end", "return n"]);
        var fn = ctx.Script.AddFunction("placefn", "g,e,tp", body);
        return $"{fn}({g},e,tp)";
    }
}
