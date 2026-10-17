using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// "Cannot be destroyed by that battle" for the monsters in a battle, and a lasting "negate the activated effects of ..."
// (Time Chain, Bye Bye Damage, Gagaga Gardna, Brave-Eyes Pendulum Dragon, Predaplant Dragostapelia).

/// <summary>
/// "Neither battling monster can be destroyed by that battle" (Time Chain), "your battling monster cannot be destroyed by
/// that battle" (Bye Bye Damage), "this card cannot be destroyed by that battle" (Gagaga Gardna): protection for the
/// current battle only, gone at the end of the Damage Step.
/// </summary>
public sealed class BattleSafeAction : ActionBlock
{
    public const string BlockId = "do.battle_safe";
    public override string Id => BlockId;
    public override string Group => "Battle";
    public override string Name => "Monsters cannot be destroyed by that battle";
    public override string Sentence => "{cards} cannot be destroyed by that battle";
    public override string Help => "For effects used during a battle: \"Both battling monsters\", \"Your monster that is battling\" or this card in the picker. " +
                                   "It lasts until the end of the Damage Step.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { That = CardRef.Battlers, CardType = FilterCardType.Monster }, Zone.MonsterZone, thisCard: true).ThatCards(),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        if (f.ThisCard) return "this card cannot be destroyed by that battle";
        // Time Chain: "Neither battling monster can be destroyed by that battle".
        if (f.That == CardRef.Battlers) return "neither battling monster can be destroyed by that battle";
        if (f.That != CardRef.None) return $"{CardRefs.Words(args, f)} cannot be destroyed by that battle";
        return $"{FilterText.Describe(f, args.Pack, plural: true, preposition: "", ownName: args.CardName)} cannot be destroyed by that battle";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var filter = ctx.Args.Cards("cards");
        string group;
        if (filter.ThisCard) group = "Group.FromCards(e:GetHandler()):Filter(Card.IsRelateToEffect,nil,e)";
        else if (filter.That != CardRef.None)
        {
            if (CardRefs.GroupLua(ctx, filter.That) is not { } g) return;
            if (CardRefs.ActivationCheck(filter.That, "aux.TRUE") is { } known) e.TargetChecks.Add(known);
            var cards = filter.Clone();
            cards.That = CardRef.None;
            cards.Zones = Zone.None;
            cards.Owner = CardOwner.Either;
            group = $"{g}:Filter({ctx.Script.Filter(cards).Function},nil,e,tp)";
        }
        else
        {
            var f = ctx.Script.Filter(filter);
            group = $"Duel.GetMatchingGroup({f.Function},tp,{f.Self},{f.Opp},{f.Exclude},e,tp)";
        }
        var r = e.NewLocal("r");
        var fn = ctx.Script.AddFunction("battlesafe", "g,e",
        [
            "local n=0",
            "for tc in aux.Next(g) do",
            "\tif tc:IsFaceup() and tc:IsLocation(LOCATION_MZONE) then",
            "\t\tlocal se=Effect.CreateEffect(e:GetHandler())",
            "\t\tse:SetType(EFFECT_TYPE_SINGLE)",
            "\t\tse:SetCode(EFFECT_INDESTRUCTABLE_BATTLE)",
            "\t\tse:SetValue(1)",
            "\t\tse:SetReset(RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_DAMAGE)",
            "\t\ttc:RegisterEffect(se)",
            "\t\tn=n+1",
            "\tend",
            "end",
            "return n",
        ]);
        e.AddStep([$"local {r}={fn}({group},e)"], r, ctx.Link);
    }
}

/// <summary>
/// "Negate the activated effects of monsters with 0 ATK" (Brave-Eyes Pendulum Dragon), "... of your opponent's monsters
/// that have Predator Counters" (Predaplant Dragostapelia), "... of monsters with the same original name as monsters
/// that are currently banished by this card's effect" (Time Chain): a lasting effect that negates each such effect as it
/// resolves. The picker says what the card that activated it is; its zones and owner are where it was activated and who
/// activated it.
/// </summary>
public sealed class NegateActivatedPassive : PassiveBlock
{
    public const string BlockId = "passive.negate_activated";
    public override string Id => BlockId;
    public override string Group => "Players";
    public override string Name => "Negate the activated effects of some cards (as they resolve)";
    public override string Sentence => "Negate the activated effects of {cards}";
    public override string Help => "Leave the picker's places empty for cards activated anywhere. Its owner is who activated the effect (\"your opponent's monsters\").";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Whose effects", () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Monster }),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        // "your opponent's monsters": who activated it, wherever it was.
        if (f.Zones == Zone.None && f.Owner == CardOwner.Opponent)
        {
            var any = f.Clone();
            any.Owner = CardOwner.Either;
            return $"negate the activated effects of your opponent's {FilterText.Describe(any, args.Pack, plural: true, preposition: "", ownName: args.CardName)}";
        }
        return $"negate the activated effects of {FilterText.Describe(f, args.Pack, plural: true, preposition: "", ownName: args.CardName)}";
    }

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards");
        var card = filter.Clone();
        card.Zones = Zone.None;
        card.Owner = CardOwner.Either;
        card.CardType = FilterCardType.Any;
        var f = ctx.Script.Filter(card);
        var tests = new List<string>
        {
            filter.CardType switch
            {
                FilterCardType.Monster => "re:IsActiveType(TYPE_MONSTER)",
                FilterCardType.Spell => "re:IsActiveType(TYPE_SPELL)",
                FilterCardType.Trap => "re:IsActiveType(TYPE_TRAP)",
                FilterCardType.SpellTrap => "re:IsActiveType(TYPE_SPELL+TYPE_TRAP)",
                _ => "true",
            },
        };
        if (filter.Owner != CardOwner.Either) tests.Add($"Duel.GetChainInfo(ev,CHAININFO_TRIGGERING_CONTROLER)=={(filter.Owner == CardOwner.You ? "tp" : "1-tp")}");
        if (filter.Zones != Zone.None) tests.Add($"Duel.GetChainInfo(ev,CHAININFO_TRIGGERING_LOCATION)&({Lua.Locations(filter.Zones)})~=0");
        tests.Add($"{f.Function}(re:GetHandler(),e,tp)");
        if (filter.ExcludeThisCard) tests.Add("re:GetHandler()~=e:GetHandler()");
        var op = ctx.Script.AddFunction("negactop", "e,tp,eg,ep,ev,re,r,rp", [$"if {string.Join(" and ", tests)} then Duel.NegateEffect(ev) end"]);
        var range = RangeOf(ctx);
        ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetCode(EVENT_CHAIN_SOLVING)", $"{v}:SetRange({range})", $"{v}:SetOperation({op})"]);
    }
}
