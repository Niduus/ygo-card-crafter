using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// Lasting rules: activations that cannot be negated, battle destruction that banishes, monsters Summoned while this card
// is face-up, and the like.

/// <summary>
/// "The activated effects of your "Reindsurm" monsters cannot be negated" (Reindsurm Carnation), "The activation of your
/// "Millennium Ankh" cannot be negated" (Golem that Guards the Millennium Treasures), "The activation of your cards and
/// effects that include an effect that Fusion Summons a Fusion Monster cannot be negated, also your opponent's cards and
/// effects cannot activate when a monster is Fusion Summoned this way" (Magical Meltdown). The engine asks each chain
/// link's triggering effect and player, as official scripts read them (EFFECT_CANNOT_INACTIVATE, EFFECT_CANNOT_DISEFFECT).
/// </summary>
public sealed class CannotNegatePassive : PassiveBlock
{
    public const string BlockId = "passive.cannot_negate";
    public override string Id => BlockId;
    public override string Group => "Protection";
    public override string Name => "Activations or activated effects of some cards cannot be negated";
    public override string Sentence => "{what} {whose} {cards} cannot be negated {fusion_lock}";
    public override string Help => "Pick monsters for their effects (\"your Thunder monsters' effects\"), or a card by name for its activation " +
                                   "(\"your \"Millennium Ankh\"\"). Add IF blocks for when it applies.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "What cannot be negated",
            new("effects", "The activated effects of"),
            new("activation", "The activation of"),
            new("both", "The activation and the activated effects of"),
            new("fusion", "The activation of your cards and effects that include an effect that Fusion Summons a Fusion Monster", "the activation of your effects that Fusion Summon")),
        ParamSpec.Choice("whose", "Whose", new("you", "your"), new("opponent", "your opponent's"), new("any", "", "anyone's"))
            .ShowIf(b => b.Values.GetValueOrDefault("what") != "fusion"),
        new ParamSpec
        {
            Key = "cards", Label = "Which cards", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { Zones = Zone.None, CardType = FilterCardType.Monster },
        }.ShowIf(b => b.Values.GetValueOrDefault("what") != "fusion"),
        // Magical Meltdown, Branded Lost, Dragonroid.
        ParamSpec.Flag("fusion_lock", "also your opponent's cards and effects cannot activate when a monster is Fusion Summoned this way", false)
            .ShowIf(b => b.Values.GetValueOrDefault("what") == "fusion"),
    ];

    private static bool Monsters(CardFilter f) => f.CardType == FilterCardType.Monster;

    public override string Describe(BlockArgs args)
    {
        var what = args.Choice("what");
        if (what == "fusion")
            return "the activation of your cards and effects that include an effect that Fusion Summons a Fusion Monster cannot be negated"
                   + (args.Bool("fusion_lock") ? ", also your opponent's cards and effects cannot activate when a monster is Fusion Summoned this way" : "");
        var f = args.Cards("cards").Clone();
        f.Zones = Zone.None;
        var named = f.CardCode != 0;
        var noun = FilterText.Describe(f, args.Pack, plural: !named, withLocation: false, ownName: args.CardName);
        var whose = args.Choice("whose") switch { "you" => "your ", "opponent" => "your opponent's ", _ => "" };
        // "the activation of your Thunder monsters' effects": a monster's activation is of its effect.
        if (what == "activation" && Monsters(f) && !named) return $"the activation of {whose}{noun}' effects cannot be negated";
        if (what == "activation" && Monsters(f)) return $"the activation of {whose}{noun}'s effects cannot be negated";
        var lead = what switch { "activation" => "the activation of", "both" => "the activation and the activated effects of", _ => "the activated effects of" };
        return $"{lead} {whose}{noun} cannot be negated";
    }

    public override void Compile(BlockContext ctx)
    {
        var what = ctx.Args.Choice("what");
        var range = RangeOf(ctx);
        string value;
        if (what == "fusion")
        {
            value = ctx.Script.AddFunction("inactval", "e,ct",
            [
                "local te,p=Duel.GetChainInfo(ct,CHAININFO_TRIGGERING_EFFECT,CHAININFO_TRIGGERING_PLAYER)",
                "return p==e:GetHandlerPlayer() and te:IsHasCategory(CATEGORY_FUSION_SUMMON)",
            ]);
            ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetCode(EFFECT_CANNOT_INACTIVATE)", $"{v}:SetRange({range})", $"{v}:SetValue({value})"]);
            if (ctx.Args.Bool("fusion_lock")) FusionLock(ctx, range);
            return;
        }
        var f = ctx.Args.Cards("cards");
        var tests = new List<string>();
        switch (ctx.Args.Choice("whose"))
        {
            case "you": tests.Add("p==e:GetHandlerPlayer()"); break;
            case "opponent": tests.Add("p~=e:GetHandlerPlayer()"); break;
        }
        var type = f.CardType switch
        {
            FilterCardType.Monster => "TYPE_MONSTER",
            FilterCardType.Spell => "TYPE_SPELL",
            FilterCardType.Trap => "TYPE_TRAP",
            FilterCardType.SpellTrap => "TYPE_SPELL+TYPE_TRAP",
            _ => null,
        };
        if (type is not null) tests.Add($"te:IsActiveType({type})");
        // "The activation of your "Millennium Ankh"": a Spell or Trap's activation is the card's own.
        if (what == "activation" && !Monsters(f)) tests.Add("te:IsHasType(EFFECT_TYPE_ACTIVATE)");
        var mf = ctx.Script.MaterialFilter(f, "nonegfilter");
        if (mf != "nil") tests.Add($"{mf}(te:GetHandler())");
        value = ctx.Script.AddFunction("nonegval", "e,ct",
        [
            "local te,p=Duel.GetChainInfo(ct,CHAININFO_TRIGGERING_EFFECT,CHAININFO_TRIGGERING_PLAYER)",
            $"return {(tests.Count == 0 ? "true" : string.Join(" and ", tests))}",
        ]);
        var codes = what switch
        {
            "activation" => new[] { "EFFECT_CANNOT_INACTIVATE" },
            "both" => ["EFFECT_CANNOT_INACTIVATE", "EFFECT_CANNOT_DISEFFECT"],
            _ => ["EFFECT_CANNOT_DISEFFECT"],
        };
        foreach (var code in codes)
            ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetCode({code})", $"{v}:SetRange({range})", $"{v}:SetValue({value})"]);
    }

    /// <summary>
    /// "Your opponent's cards and effects cannot activate when a monster is Fusion Summoned this way": after such a Fusion
    /// Summon only you can chain, until the chain ends, as Magical Meltdown does it.
    /// </summary>
    private static void FusionLock(BlockContext ctx, string range)
    {
        var flag = $"id+{8400 + ctx.Script.Slot("fusionlock")}";
        var lm = ctx.Script.AddFunction("fuschainlm", "e,rp,tp", ["return tp==rp"]);
        var filter = ctx.Script.AddFunction("fuslimfilter", "c,tp",
            ["return c:IsSummonPlayer(tp) and c:IsSummonType(SUMMON_TYPE_FUSION) and c:GetSpecialSummonInfo(SUMMON_INFO_REASON_EFFECT):IsHasCategory(CATEGORY_FUSION_SUMMON)"]);
        var reset = ctx.Script.AddFunction("fuslimreset", "e,tp,eg,ep,ev,re,r,rp", [$"e:GetHandler():ResetFlagEffect({flag})", "e:Reset()"]);
        var op = ctx.Script.AddFunction("fuslimop", "e,tp,eg,ep,ev,re,r,rp",
        [
            // The effect's condition is its IF blocks: the Fusion Summon is checked here.
            $"if not eg:IsExists({filter},1,nil,tp) then return end",
            "if Duel.GetCurrentChain()==0 then",
            $"\tDuel.SetChainLimitTillChainEnd({lm})",
            "elseif Duel.GetCurrentChain()==1 then",
            // Summoned by the first chain link: the limit starts when the chain ends, unless another chain starts first.
            $"\te:GetHandler():RegisterFlagEffect({flag},RESET_EVENT+RESETS_STANDARD,0,1)",
            "\tlocal e1=Effect.CreateEffect(e:GetHandler())",
            "\te1:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "\te1:SetCode(EVENT_CHAINING)",
            $"\te1:SetOperation({reset})",
            "\tDuel.RegisterEffect(e1,tp)",
            "\tlocal e2=e1:Clone()",
            "\te2:SetCode(EVENT_BREAK_EFFECT)",
            "\te2:SetReset(RESET_CHAIN)",
            "\tDuel.RegisterEffect(e2,tp)",
            "end",
        ]);
        var endOp = ctx.Script.AddFunction("fuslimop", "e,tp,eg,ep,ev,re,r,rp",
        [
            $"if e:GetHandler():GetFlagEffect({flag})~=0 then Duel.SetChainLimitTillChainEnd({lm}) end",
            $"e:GetHandler():ResetFlagEffect({flag})",
        ]);
        ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetCode(EVENT_SPSUMMON_SUCCESS)", $"{v}:SetRange({range})", $"{v}:SetOperation({op})"]);
        ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetCode(EVENT_CHAIN_END)", $"{v}:SetRange({range})", $"{v}:SetOperation({endOp})"]);
    }
}

/// <summary>
/// "Any monster destroyed by battle with your "Nekroz" monster is banished" (Nekroz of Metaltron, Ars Magna DoomZ:
/// "with your linked monster"), "Monsters destroyed by battle with this card are placed on the bottom of the Deck
/// instead of going to the GY" (Amazoness Trainee): where the monsters these destroy by battle go
/// (EFFECT_BATTLE_DESTROY_REDIRECT on the monster that destroys them).
/// </summary>
public sealed class BattleRedirectPassive : PassiveBlock
{
    public const string BlockId = "passive.battle_redirect";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Monsters destroyed by battle with some monsters are banished instead";
    public override string Sentence => "Any monster destroyed by battle with {cards} is {where}";
    public override string Help => "Pick this card, or monsters you control (\"your linked monster\": add the requirement \"Is linked\").";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Destroyed by battle with", () => new CardFilter { ThisCard = true, Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster },
            Zone.MonsterZone, thisCard: true),
        ParamSpec.Choice("where", "Goes", new("banish", "banished"), new("deck_bottom", "placed on the bottom of the Deck instead of going to the GY"),
            new("deck", "shuffled into the Deck instead of going to the GY"), new("hand", "returned to the hand instead of going to the GY")),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        var where = args.Describe("where");
        if (f.ThisCard) return $"monsters destroyed by battle with this card are {where}";
        var noun = FilterText.Describe(f, args.Pack, preposition: "", withLocation: false, ownName: args.CardName);
        var whose = f.Owner switch { CardOwner.You => "your ", CardOwner.Opponent => "your opponent's ", _ => "a " };
        return $"any monster destroyed by battle with {whose}{noun} is {where}";
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var loc = ctx.Args.Choice("where") switch
        {
            "deck_bottom" => "LOCATION_DECKBOT",
            "deck" => "LOCATION_DECKSHF",
            "hand" => "LOCATION_HAND",
            _ => "LOCATION_REMOVED",
        };
        var range = RangeOf(ctx);
        var f = ctx.Args.Cards("cards");
        if (f.ThisCard)
        {
            ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_BATTLE_DESTROY_REDIRECT", loc));
            return;
        }
        var filter = f.Clone();
        filter.Zones = Zone.MonsterZone;
        var fr = ctx.Script.Filter(filter);
        var target = ctx.Script.AddFunction("redirtg", "e,c", [$"return {fr.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")]);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetProperty(EFFECT_FLAG_IGNORE_IMMUNE)", $"{v}:SetCode(EFFECT_BATTLE_DESTROY_REDIRECT)",
            $"{v}:SetRange({range})", $"{v}:SetTargetRange({fr.Self},{fr.Opp})", $"{v}:SetTarget({target})", $"{v}:SetValue({loc})",
        ]);
    }
}

/// <summary>
/// "The Level of any monster your opponent Normal or Special Summons in Attack Position, while this card is in your
/// Monster Zone, is increased by 7" (Heart of Gaia): each monster Summoned so while this card is face-up changes, and
/// keeps the change after this card leaves.
/// </summary>
public sealed class SummonedChangePassive : PassiveBlock
{
    public const string BlockId = "passive.summoned_change";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Monsters Summoned while this card is face-up change Level or ATK";
    public override string Sentence => "The {stat} of any monster {who} {how} {position}, while this card is in your Monster Zone, is {dir} by {amount}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("stat", "What changes", new("level", "Level"), new("atk", "ATK"), new("def", "DEF")),
        ParamSpec.Choice("who", "Summoned by", new("opponent", "your opponent"), new("you", "you"), new("any", "either player")),
        ParamSpec.Choice("how", "Summons", new("normal_special", "Normal or Special Summons"), new("normal", "Normal Summons"), new("special", "Special Summons")),
        ParamSpec.Choice("position", "In", new("any", "", "either position"), new("attack", "in Attack Position"), new("defense", "in Defense Position")),
        ParamSpec.Choice("dir", "Change", new("up", "increased"), new("down", "decreased")),
        ParamSpec.Int("amount", "By", 1, 1, 99999),
    ];

    public override string Describe(BlockArgs args)
    {
        var position = args.Describe("position");
        return $"the {args.Describe("stat")} of any monster {args.Describe("who")} {args.Describe("how")}{(position.Length > 0 ? " " + position : "")}, " +
               $"while this card is in your Monster Zone, is {args.Describe("dir")} by {args.Int("amount").ToString(CultureInfo.InvariantCulture)}";
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var tests = new List<string> { "c:IsLocation(LOCATION_MZONE)", "c:IsFaceup()" };
        switch (ctx.Args.Choice("who"))
        {
            case "opponent": tests.Add("c:IsSummonPlayer(1-tp)"); break;
            case "you": tests.Add("c:IsSummonPlayer(tp)"); break;
        }
        switch (ctx.Args.Choice("position"))
        {
            case "attack": tests.Add("c:IsPosition(POS_FACEUP_ATTACK)"); break;
            case "defense": tests.Add("c:IsPosition(POS_FACEUP_DEFENSE)"); break;
        }
        var stat = ctx.Args.Choice("stat");
        tests.Add(stat == "level" ? "c:IsLevelAbove(1)" : stat == "def" ? "not c:IsType(TYPE_LINK)" : "true");
        tests.Remove("true");
        var filter = ctx.Script.AddFunction("sumchgfilter", "c,tp", [$"return {string.Join(" and ", tests)}"]);
        var amount = ctx.Args.Int("amount") * (ctx.Args.Choice("dir") == "down" ? -1 : 1);
        var code = stat switch { "atk" => "EFFECT_UPDATE_ATTACK", "def" => "EFFECT_UPDATE_DEFENSE", _ => "EFFECT_UPDATE_LEVEL" };
        var op = ctx.Script.AddFunction("sumchgop", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local c=e:GetHandler()",
            // The effect's condition is its IF blocks: what was Summoned is checked here.
            "if eg:IsContains(c) then return end",
            $"for tc in aux.Next(eg:Filter({filter},nil,tp)) do",
            "\tlocal e1=Effect.CreateEffect(c)",
            "\te1:SetType(EFFECT_TYPE_SINGLE)",
            $"\te1:SetCode({code})",
            $"\te1:SetValue({amount.ToString(CultureInfo.InvariantCulture)})",
            "\te1:SetReset(RESET_EVENT+RESETS_STANDARD)",
            "\ttc:RegisterEffect(e1)",
            "end",
        ]);
        var range = RangeOf(ctx);
        var events = ctx.Args.Choice("how") switch
        {
            "normal" => new[] { "EVENT_SUMMON_SUCCESS" },
            "special" => ["EVENT_SPSUMMON_SUCCESS"],
            _ => ["EVENT_SUMMON_SUCCESS", "EVENT_SPSUMMON_SUCCESS"],
        };
        foreach (var ev in events)
            ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetCode({ev})", $"{v}:SetRange({range})", $"{v}:SetOperation({op})"]);
    }
}
