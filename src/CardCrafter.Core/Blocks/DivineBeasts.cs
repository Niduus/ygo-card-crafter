using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// The Egyptian Gods and their support: Summons that need 3 Tributes, the Tributed monsters' stats for a Tribute Summon this
// turn (Ancient Chant), and effects a monster gains for the turn (Blaze Cannon).

/// <summary>
/// "Requires 3 Tributes to Normal Summon (cannot be Normal Set)." (Obelisk the Tormentor, Slifer the Sky Dragon, The
/// Winged Dragon of Ra), "Requires 3 "Qli" Tributes to Normal Summon/Set." (Apoqliphort Towers): the card's only way to be
/// Normal Summoned (EFFECT_LIMIT_SUMMON_PROC), as the official scripts write it. The Winged Dragon of Ra - Sphere Mode:
/// "Requires 3 Tributes from either side of the field to Normal Summon to that side of the field (cannot be Normal Set),
/// then shift control to this card's owner during the End Phase of the next turn."
/// </summary>
public sealed class TributeSummonPassive : PassiveBlock
{
    public const string BlockId = "passive.tribute_summon";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "Requires 3 Tributes to Normal Summon (cannot be Normal Set)";
    public override string Sentence => "Requires {count} {cards} Tributes {side} to Normal Summon {set}";
    public override string Help => "The only way this card can be Normal Summoned. Pick monsters for Tributes of one kind (\"Requires 3 \"Qli\" Tributes\"); " +
                                   "plain monsters for any.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "Tributes", 3, 1, 5),
        new ParamSpec
        {
            Key = "cards", Label = "Tributes of (plain monsters for any)", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { CardType = FilterCardType.Monster, Zones = Zone.None },
        },
        ParamSpec.Choice("set", "Set", new("no_set", "(cannot be Normal Set)"), new("set", "/Set", "it can be Set with the same Tributes (\"Normal Summon/Set\")"))
            .ShowIf(b => b.Values.GetValueOrDefault("side", "own") != "either"),
        ParamSpec.Choice("side", "Tributes from", new("own", "", "your side of the field"),
            new("either", "from either side of the field", "either side of the field, to Summon it to that side (control goes back at the next turn's End Phase)")),
    ];

    private static bool EitherSide(BlockArgs args) => args.Choice("side") == "either";

    /// <summary>"" for any monster, or what the Tributes must be: ""Qli"", "Dragon".</summary>
    private static string TributeKind(BlockArgs args)
    {
        var f = args.Cards("cards").Clone();
        f.Zones = Zone.None;
        var noun = FilterText.Describe(f, args.Pack, plural: true, withLocation: false, ownName: args.CardName);
        foreach (var tail in new[] { " monsters", "monsters" })
            if (noun.EndsWith(tail, StringComparison.Ordinal)) return noun[..^tail.Length].Trim();
        return noun;
    }

    public override string Describe(BlockArgs args)
    {
        var kind = TributeKind(args) is { Length: > 0 } k ? k + " " : "";
        var n = args.Int("count");
        var tributes = n == 1 ? "Tribute" : "Tributes";
        if (EitherSide(args))
            return $"requires {n} {kind}{tributes} from either side of the field to Normal Summon to that side of the field (cannot be Normal Set), " +
                   "then shift control to this card's owner during the End Phase of the next turn";
        return args.Choice("set") == "set"
            ? $"requires {n} {kind}{tributes} to Normal Summon/Set"
            : $"requires {n} {kind}{tributes} to Normal Summon (cannot be Normal Set)";
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        if (ctx.Effect.Conditions.Count > 0) ctx.Effect.Warn("\"Requires Tributes to Normal Summon\" always applies; the IF blocks were ignored.");
        ctx.Effect.Conditions.Clear();
        var n = ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture);
        var f = ctx.Args.Cards("cards").Clone();
        f.Zones = Zone.None;
        var kind = ctx.Script.MaterialFilter(f, "tributefilter");
        // Tributes of one kind: the others can't be Tributed for its Summon (EFFECT_TRIBUTE_LIMIT, as the Qli monsters write it).
        if (kind != "nil")
        {
            var limit = ctx.Script.AddFunction("tlimit", "e,c", [$"return not {kind}(c)"]);
            ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_SINGLE)", $"{v}:SetCode(EFFECT_TRIBUTE_LIMIT)", $"{v}:SetValue({limit})"]);
        }
        var con = ctx.Script.AddFunction("ttcon", "e,c,minc", ["if c==nil then return true end", $"return minc<={n} and Duel.CheckTribute(c,{n})"]);
        var op = ctx.Script.AddFunction("ttop", "e,tp,eg,ep,ev,re,r,rp,c",
            [$"local g=Duel.SelectTribute(tp,c,{n},{n})", "c:SetMaterial(g)", "Duel.Release(g,REASON_SUMMON+REASON_MATERIAL)"]);
        var index = Math.Min(ctx.Effect.Index, 16) - 1;
        IEnumerable<string> Proc(string v, string code) =>
        [
            $"{v}:SetDescription(aux.Stringid(id,{index}))",
            $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)",
            $"{v}:SetType(EFFECT_TYPE_SINGLE)",
            $"{v}:SetCode({code})",
            $"{v}:SetCondition({con})",
            $"{v}:SetOperation({op})",
            $"{v}:SetValue(SUMMON_TYPE_ADVANCE)",
        ];
        ctx.Effect.AddPassive(v => Proc(v, "EFFECT_LIMIT_SUMMON_PROC"));
        if (EitherSide(ctx.Args)) OpponentSide(ctx, n, index);
        else if (ctx.Args.Choice("set") == "set")
        {
            ctx.Effect.AddPassive(v => Proc(v, "EFFECT_LIMIT_SET_PROC"));
            return;
        }
        // "Cannot be Normal Set": the only way to Set it is one that never applies.
        var setcon = ctx.Script.AddFunction("setcon", "e,c,minc", ["if not c then return true end", "return false"]);
        ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_SINGLE)", $"{v}:SetCode(EFFECT_LIMIT_SET_PROC)", $"{v}:SetCondition({setcon})"]);
    }

    /// <summary>
    /// Sphere Mode: the same Summon with your opponent's monsters, to their field, and control goes back to the owner
    /// during the End Phase of the next turn, as its script writes it.
    /// </summary>
    private static void OpponentSide(BlockContext ctx, string n, int index)
    {
        var con = ctx.Script.AddFunction("ttcon", "e,c,minc",
            ["if c==nil then return true end", "local tp=c:GetControler()", "local mg=Duel.GetFieldGroup(tp,0,LOCATION_MZONE)", $"return minc<={n} and Duel.CheckTribute(c,{n},{n},mg,1-tp)"]);
        var op = ctx.Script.AddFunction("ttop", "e,tp,eg,ep,ev,re,r,rp,c",
            ["local mg=Duel.GetFieldGroup(tp,0,LOCATION_MZONE)", $"local g=Duel.SelectTribute(tp,c,{n},{n},mg,1-tp)", "c:SetMaterial(g)", "Duel.Release(g,REASON_SUMMON+REASON_MATERIAL)"]);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetDescription(aux.Stringid(id,{index}))",
            $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE+EFFECT_FLAG_SPSUM_PARAM)",
            $"{v}:SetType(EFFECT_TYPE_SINGLE)",
            $"{v}:SetCode(EFFECT_LIMIT_SUMMON_PROC)",
            $"{v}:SetTargetRange(POS_FACEUP_ATTACK,1)",
            $"{v}:SetCondition({con})",
            $"{v}:SetOperation({op})",
            $"{v}:SetValue(SUMMON_TYPE_ADVANCE)",
        ]);
        // "Then shift control to this card's owner during the End Phase of the next turn".
        ctx.Script.AddInitialLine("Duel.EnableGlobalFlag(GLOBALFLAG_BRAINWASHING_CHECK)");
        var flag = $"id+{8780 + ctx.Script.Slot("controlback")}";
        var rettg = ctx.Script.AddFunction("rettg", "e,c", [$"return c==e:GetLabelObject() and c:GetFlagEffect({flag})~=0"]);
        var reset = ctx.Script.AddFunction("retreset", "e,tp,eg,ep,ev,re,r,rp", ["e:GetLabelObject():Reset()", "e:Reset()"]);
        var retcon = ctx.Script.AddFunction("retcon", "e,tp,eg,ep,ev,re,r,rp", [$"return Duel.GetTurnCount()==e:GetLabel() and e:GetOwner():GetFlagEffect({flag})~=0"]);
        var retop = ctx.Script.AddFunction("retop", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local c=e:GetOwner()",
            "local e1=Effect.CreateEffect(e:GetHandler())",
            "e1:SetType(EFFECT_TYPE_FIELD)",
            "e1:SetCode(EFFECT_REMOVE_BRAINWASHING)",
            "e1:SetProperty(EFFECT_FLAG_IGNORE_IMMUNE+EFFECT_FLAG_SET_AVAILABLE)",
            "e1:SetTargetRange(LOCATION_MZONE,LOCATION_MZONE)",
            "e1:SetLabelObject(c)",
            $"e1:SetTarget({rettg})",
            "Duel.RegisterEffect(e1,tp)",
            "local e2=Effect.CreateEffect(e:GetHandler())",
            "e2:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "e2:SetCode(EVENT_ADJUST)",
            "e2:SetLabelObject(e1)",
            $"e2:SetOperation({reset})",
            "Duel.RegisterEffect(e2,tp)",
        ]);
        var retreg = ctx.Script.AddFunction("retreg", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local c=e:GetHandler()",
            $"c:RegisterFlagEffect({flag},RESET_EVENT+RESETS_STANDARD-RESET_TURN_SET+RESET_PHASE+PHASE_END,0,2)",
            "local e1=Effect.CreateEffect(c)",
            "e1:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "e1:SetCode(EVENT_PHASE+PHASE_END)",
            "e1:SetProperty(EFFECT_FLAG_IGNORE_IMMUNE)",
            "e1:SetLabel(Duel.GetTurnCount()+1)",
            "e1:SetCountLimit(1)",
            $"e1:SetCondition({retcon})",
            $"e1:SetOperation({retop})",
            "e1:SetReset(RESET_PHASE+PHASE_END,2)",
            "Duel.RegisterEffect(e1,tp)",
        ]);
        ctx.Effect.AddPassive(v =>
            [$"{v}:SetType(EFFECT_TYPE_SINGLE+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetCode(EVENT_SUMMON_SUCCESS)", $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)", $"{v}:SetOperation({retreg})"]);
    }
}

/// <summary>
/// Ancient Chant: "If you Tribute Summon "The Winged Dragon of Ra" this turn, its original ATK/DEF become the combined
/// original ATK/DEF of the monsters Tributed for its Summon": every such Tribute Summon this turn is checked as it
/// succeeds, and the monster keeps its new stats while it stays face-up.
/// </summary>
public sealed class TributedStatsAction : ActionBlock
{
    public const string BlockId = "do.tributed_stats";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "If you Tribute Summon a monster this turn, it gets the Tributed monsters' combined ATK/DEF";
    public override string Sentence => "If you Tribute Summon {cards} this turn, {what}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new ParamSpec
        {
            Key = "cards", Label = "Which monster", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { CardType = FilterCardType.Monster, Zones = Zone.None },
        },
        ParamSpec.Choice("what", "It",
            new("base", "its original ATK/DEF become the combined original ATK/DEF of the monsters Tributed for its Summon"),
            new("gain", "it gains ATK/DEF equal to the combined original ATK/DEF of the monsters Tributed for its Summon")),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards").Clone();
        f.Zones = Zone.None;
        var noun = FilterText.Describe(f, args.Pack, plural: false, withLocation: false, ownName: args.CardName);
        if (f.CardCode == 0 && !noun.StartsWith('"')) noun = SummonWording.Article(noun) + " " + noun;
        return $"if you Tribute Summon {noun} this turn, {args.Describe("what")}";
    }

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Args.Cards("cards").Clone();
        f.Zones = Zone.None;
        var mf = ctx.Script.MaterialFilter(f, "tsumfilter");
        var (atk, def) = ctx.Args.Choice("what") == "gain" ? ("EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE") : ("EFFECT_SET_BASE_ATTACK", "EFFECT_SET_BASE_DEFENSE");
        var op = ctx.Script.AddFunction("tsumop", "e,tp,eg,ep,ev,re,r,rp",
        [
            "for tc in aux.Next(eg) do",
            $"\tif tc:IsSummonType(SUMMON_TYPE_ADVANCE) and tc:IsSummonPlayer(tp){(mf == "nil" ? "" : $" and {mf}(tc)")} then",
            "\t\tlocal atk,def=0,0",
            "\t\tfor mc in aux.Next(tc:GetMaterial()) do",
            "\t\t\tatk=atk+math.max(mc:GetTextAttack(),0)",
            "\t\t\tdef=def+math.max(mc:GetTextDefense(),0)",
            "\t\tend",
            "\t\tlocal e1=Effect.CreateEffect(e:GetHandler())",
            "\t\te1:SetType(EFFECT_TYPE_SINGLE)",
            $"\t\te1:SetCode({atk})",
            "\t\te1:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
            "\t\te1:SetValue(atk)",
            "\t\te1:SetReset(RESET_EVENT+RESETS_STANDARD)",
            "\t\ttc:RegisterEffect(e1)",
            "\t\tlocal e2=e1:Clone()",
            $"\t\te2:SetCode({def})",
            "\t\te2:SetValue(def)",
            "\t\ttc:RegisterEffect(e2)",
            "\tend",
            "end",
        ]);
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddStep(
        [
            "local ts=Effect.CreateEffect(e:GetHandler())",
            "ts:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "ts:SetCode(EVENT_SUMMON_SUCCESS)",
            $"ts:SetOperation({op})",
            "ts:SetReset(RESET_PHASE+PHASE_END)",
            "Duel.RegisterEffect(ts,tp)",
            $"local {r}=1",
        ], r, ctx.Link);
    }
}

/// <summary>
/// Blaze Cannon: "1 "The Winged Dragon of Ra" you control gains the following effects until the end of this turn. ●
/// ...": the monster gets this card's effects that are marked "given to a monster" (<see cref="EffectEvent.Granted"/>),
/// in which "this card" is that monster. They are written once in the script and registered on the monster as it
/// resolves, gone at the end of the turn.
/// </summary>
public sealed class GrantEffectsAction : CardGroupAction
{
    public const string BlockId = "do.grant_effects";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Monsters gain the following effects (this card's \"given\" effects) until the end of this turn";
    public override string Sentence => "{count} {cards} {target} gain(s) the following effects until the end of this turn";
    public override string Help => "Tick \"given to a monster\" on the effects it gives: they are listed under this one (\"●\"), \"this card\" in them is the " +
                                   "monster that gains them, and this card doesn't have them itself.";
    protected override string Category => "";
    protected override string Hint => "HINTMSG_FACEUP";
    protected override string TargetedWording => "{0}";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    /// <summary>The script function that gives the monster tc every given effect of card c (written by <see cref="ScriptCompiler"/>).</summary>
    public const string GrantFunction = "s.cc_grant";

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ModeParam,
        ParamSpec.Cards("cards", "Which monsters", DefaultCards, Zone.MonsterZone),
        ParamSpec.Flag("target", "(targeting)", false),
    ];

    private static string Tail(bool one) => (one ? "gains" : "gain") + " the following effects until the end of this turn";

    public override string Describe(BlockArgs args) => $"{Subject(args)} {Tail(One(args))}{args.ExceptText()}";

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) => $"{(single ? "it" : "they")} {Tail(single)}";

    protected override string? AbleCheck(BlockContext ctx) => "c:IsFaceup()";

    public override void Compile(BlockContext ctx)
    {
        if (!ctx.Script.Project.Effects.Any(x => x.Granted))
            ctx.Effect.Error($"\"{Name}\": tick \"given to a monster\" on the effects of this card it gives.");
        if (ctx.Effect.Source.Granted) ctx.Effect.Error($"\"{Name}\" can't be in an effect that is itself given to a monster.");
        base.Compile(ctx);
    }

    protected override string Apply(string g, BlockContext ctx)
    {
        var fn = ctx.Script.AddFunction("grantop", "g,e",
            ["local n=0", "for tc in aux.Next(g) do", "\tif tc:IsFaceup() then", $"\t\t{GrantFunction}(e:GetHandler(),tc)", "\t\tn=n+1", "\tend", "end", "return n"]);
        return $"{fn}({g},e)";
    }

    /// <summary>
    /// The lines that give monster tc a given effect: the effect as it would go on this card (<paramref name="built"/>),
    /// registered on tc until the end of the turn instead.
    /// </summary>
    public static IEnumerable<string> GrantLines(IEnumerable<string> built) => built.Select(l =>
        System.Text.RegularExpressions.Regex.Match(l, @"^c:RegisterEffect\((\w+)\)$") is { Success: true } m
            ? $"{m.Groups[1].Value}:SetReset(RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END)\n\ttc:RegisterEffect({m.Groups[1].Value})"
            : l);

    /// <summary>A monster the given effects are compiled for: their "this card" is the monster that gains them.</summary>
    public static CardData Receiver(CardData card) => new() { Id = card.Id, Name = card.Name, Kind = CardKind.Monster, Frame = MonsterFrame.Effect };
}
