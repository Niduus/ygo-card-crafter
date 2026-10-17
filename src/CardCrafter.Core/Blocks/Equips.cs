using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "Special Summon 1 "Salamangreat" monster from your hand or GY, and if you do, equip this card to it", "then you can
/// equip this card to it as an Equip Spell that gives it 1000 ATK", "equip 1 "Marincess" monster from your GY to 1 of
/// those Link Monsters as an Equip Spell that gives it 600 ATK": a card becomes an Equip Card of a face-up monster, which
/// it can only stay equipped to. The monster is usually "that card": the one the step before Summoned, the target, or one
/// of the cards that started the effect.
/// </summary>
public sealed class EquipAction : ActionBlock
{
    public const string BlockId = "do.equip";
    public override string Id => BlockId;
    public override string Group => "Equip";
    public override string Name => "Equip a card to a monster (this card, or 1 monster from the GY, to it)";
    public override string Sentence => "Equip {cards} to {to} {gives} {amount}";
    public override string Help => "The card equipped can only stay equipped to that monster. \"As an Equip Spell that gives it 1000 ATK\" is for a monster " +
                                   "(or this card) that becomes an Equip Card. For a monster equipped to this card, \"Equip a monster to this card\" does the same.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "What is equipped", () => new CardFilter { ThisCard = true, Zones = Zone.Graveyard, CardType = FilterCardType.Monster }, thisCard: true).ThatCards(),
        ParamSpec.Cards("to", "To which monster", () => new CardFilter { That = CardRef.Affected, Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })
            .ThatCards(),
        ParamSpec.Choice("gives", "As an Equip Spell that gives it",
            new("none", "", "(nothing)"), new("atk", "as an Equip Spell that gives it", "ATK"), new("def", "as an Equip Spell that gives it", "DEF"),
            new("both", "as an Equip Spell that gives it", "ATK/DEF")),
        ParamSpec.Int("amount", "Amount", 1000, 0, 99999).ShowIf(b => b.Values.GetValueOrDefault("gives", "none") != "none"),
    ];

    /// <summary>"this card", "1 "Marincess" monster from your GY", "it".</summary>
    private static string What(BlockArgs args)
    {
        var f = args.Cards("cards");
        if (f.ThisCard) return "this card";
        if (f.That != CardRef.None) return CardRefs.Words(args, f);
        return "1 " + FilterText.Describe(f, args.Pack, ownName: args.CardName, withExcept: false);
    }

    /// <summary>"it", "1 of those Link Monsters", "1 Warrior monster you control".</summary>
    private static string To(BlockArgs args)
    {
        var f = args.Cards("to");
        return f.That switch
        {
            CardRef.None => "1 " + FilterText.Describe(f, args.Pack, ownName: args.CardName, withExcept: false),
            CardRef.Event => "1 of " + CardRefs.EventNoun(f, plural: true),
            CardRef.Target or CardRef.Affected => CardRefs.Plural(args, f.That) ? "1 of them" : "it",
            _ => FilterText.Describe(f, args.Pack),
        };
    }

    private static string Gives(BlockArgs args) => args.Choice("gives") switch
    {
        "atk" => $" as an Equip Spell that gives it {args.Int("amount")} ATK",
        "def" => $" as an Equip Spell that gives it {args.Int("amount")} DEF",
        "both" => $" as an Equip Spell that gives it {args.Int("amount")} ATK/DEF",
        _ => "",
    };

    public override string Describe(BlockArgs args) => $"equip {What(args)} to {To(args)}{Gives(args)}{args.ExceptText()}";

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var a = ctx.Args;
        var (what, to) = (a.Cards("cards"), a.Cards("to"));
        if (to.ThisCard)
        {
            e.Error($"\"{Name}\": to equip a card to this card, use \"Equip a monster to this card\".");
            return;
        }
        if (what.ThisCard && to.That == CardRef.Equipped)
        {
            e.Error($"\"{Name}\": this card can't be equipped to the monster it is equipped to already.");
            return;
        }
        if (!what.IsSpecific() && (what.Zones == Zone.None || what.CardType != FilterCardType.Monster))
        {
            e.Error($"\"{Name}\": the card equipped is this card, \"that card\", or a monster picked from somewhere (its GY, Deck...).");
            return;
        }
        if (to.That == CardRef.None && (to.Zones & ~Zone.MonsterZone) != Zone.None && to.Zones != Zone.Field)
        {
            e.Error($"\"{Name}\": the monster it is equipped to is a face-up monster on the field.");
            return;
        }
        e.AddCategory("CATEGORY_EQUIP");
        if (!what.IsSpecific() && what.Zones.HasFlag(Zone.Graveyard) || what.ThisCard && e.Range == "LOCATION_GRAVE") e.AddCategory("CATEGORY_LEAVE_GRAVE");

        // The monster: face-up in a Monster Zone, and what the picker says.
        var toCards = to.Clone();
        if (to.That != CardRef.None) (toCards.Zones, toCards.Owner) = (Zone.None, CardOwner.Either);
        else toCards.Zones = Zone.MonsterZone;
        var tf = ctx.Script.Filter(toCards, "c:IsFaceup() and c:IsLocation(LOCATION_MZONE)");
        // The card equipped: a monster that can be on the field only once is checked as official scripts do.
        var whatCards = what.Clone();
        if (what.That != CardRef.None) (whatCards.Zones, whatCards.Owner) = (Zone.None, CardOwner.Either);
        var wf = what.ThisCard ? null : ctx.Script.Filter(whatCards, "c:CheckUniqueOnField(tp) and not c:IsForbidden()");

        // A Spell/Trap activated to equip itself is already on the field; anything else needs a free Spell & Trap Zone.
        var spellItself = what.ThisCard && !ctx.Card.IsMonster && e.IsActivation;
        var room = spellItself ? null : "Duel.GetLocationCount(tp,LOCATION_SZONE)>0";
        if (room is not null && !e.CurrentOptional) e.TargetChecks.Add(room);
        if (wf is not null && what.That == CardRef.None)
            e.TargetChecks.Add($"Duel.IsExistingMatchingCard({wf.Function},tp,{wf.Self},{wf.Opp},1,{wf.Exclude},e,tp)");
        if (to.That == CardRef.None)
            e.TargetChecks.Add($"Duel.IsExistingMatchingCard({tf.Function},tp,{tf.Self},{tf.Opp},1,{tf.Exclude},e,tp)");
        else if (CardRefs.ActivationCheck(to.That, tf.Function) is { } known) e.TargetChecks.Add(known);
        if (what.ThisCard && !spellItself) e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_EQUIP,e:GetHandler(),1,0,0)");

        var (tc, ec, tg, r) = (e.NewLocal("tc"), e.NewLocal("ec"), e.NewLocal("tg"), e.NewLocal("r"));
        var lines = new List<string> { $"local {r}=0" };
        if (to.That != CardRef.None)
        {
            if (CardRefs.GroupLua(ctx, to.That) is not { } group) return;
            lines.Add($"local {tg}={group}:Filter({tf.Function},nil,e,tp)");
            lines.Add($"if {tg}:GetCount()>1 then Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_FACEUP) {tg}={tg}:Select(tp,1,1,nil) end");
        }
        else
        {
            lines.Add("Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_FACEUP)");
            lines.Add($"local {tg}=Duel.SelectMatchingCard(tp,{tf.Function},tp,{tf.Self},{tf.Opp},1,1,{tf.Exclude},e,tp)");
        }
        lines.Add($"local {tc}={tg}:GetFirst()");
        if (what.ThisCard) lines.Add($"local {ec}=c:IsRelateToEffect(e) and c or nil");
        else if (what.That != CardRef.None)
        {
            if (CardRefs.GroupLua(ctx, what.That) is not { } group) return;
            lines.Add($"local {ec}={group}:Filter({wf!.Function},nil,e,tp):GetFirst()");
        }
        else
        {
            var exclude = what.ExcludeThisCard ? $"Group.FromCards(e:GetHandler(),{tc})" : tc;
            lines.Add($"local {ec}=nil");
            lines.Add($"if {tc} then");
            lines.Add("\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_EQUIP)");
            lines.Add($"\t{ec}=Duel.SelectMatchingCard(tp,{wf!.ForSelection},tp,{wf.Self},{wf.Opp},1,1,{exclude},e,tp):GetFirst()");
            lines.Add("end");
        }
        var limit = ctx.Script.AddFunction("eqlimitto", "e,c", ["return c==e:GetLabelObject()"]);
        lines.Add($"if {tc} and {ec} and {ec}~={tc}{(room is null ? "" : " and " + room)} and Duel.Equip(tp,{ec},{tc}) then");
        lines.AddRange(
        [
            $"\tlocal eq=Effect.CreateEffect({ec})",
            "\teq:SetType(EFFECT_TYPE_SINGLE)",
            "\teq:SetCode(EFFECT_EQUIP_LIMIT)",
            "\teq:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
            $"\teq:SetLabelObject({tc})",
            $"\teq:SetValue({limit})",
            "\teq:SetReset(RESET_EVENT+RESETS_STANDARD)",
            $"\t{ec}:RegisterEffect(eq)",
        ]);
        var n = a.Int("amount").ToString(CultureInfo.InvariantCulture);
        var codes = a.Choice("gives") switch
        {
            "atk" => new[] { "EFFECT_UPDATE_ATTACK" },
            "def" => ["EFFECT_UPDATE_DEFENSE"],
            "both" => ["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"],
            _ => [],
        };
        foreach (var code in codes)
        {
            var v = e.NewLocal("ge");
            lines.AddRange(
            [
                $"\tlocal {v}=Effect.CreateEffect({ec})",
                $"\t{v}:SetType(EFFECT_TYPE_EQUIP)",
                $"\t{v}:SetCode({code})",
                $"\t{v}:SetValue({n})",
                $"\t{v}:SetReset(RESET_EVENT+RESETS_STANDARD)",
                $"\t{ec}:RegisterEffect({v})",
            ]);
        }
        lines.Add($"\t{r}=1");
        lines.Add("end");
        e.AddStep(lines, r, ctx.Link);
    }
}

/// <summary>
/// Therion "King" Regulus: "also it can activate the 2nd effect listed above as if it were "Therion "King" Regulus"": the
/// monster this card is equipped to gets one of this card's effects (EFFECT_TYPE_GRANT), sharing its "once per turn".
/// </summary>
public sealed class GrantEffectPassive : PassiveBlock
{
    public const string BlockId = "passive.grant_effect";
    public override string Id => BlockId;
    public override string Group => "Equip";
    public override string Name => "The equipped monster can activate one of this card's effects";
    public override string Sentence => "The equipped monster {only} {equipped} can activate effect {effect} of this card as if it were this card";
    public override string Help => "While this card is an Equip Card, the monster it is equipped to can activate that effect (an effect listed before this one) " +
                                   "as if it were this card, sharing its once per turn. \"Only one like this\" gives it only to such monsters.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        EquippedPassive.OnlyParam,
        EquippedPassive.EquippedParam,
        ParamSpec.Int("effect", "Which effect (its number on the card)", 1, 1, 16),
    ];

    private static string Ordinal(int n) => n switch
    {
        1 => "1st", 2 => "2nd", 3 => "3rd", _ => n.ToString(CultureInfo.InvariantCulture) + "th",
    };

    public override string Describe(BlockArgs args)
    {
        var which = $"can activate the {Ordinal(args.Int("effect"))} effect listed above as if it were \"{args.CardName ?? "this card"}\"";
        // "A "Therion" monster equipped with this card gains 700 ATK, also it can activate ...".
        var dos = args.Effect?.Do;
        var at = dos?.FindIndex(b => ReferenceEquals(b, args.Instance)) ?? -1;
        if (at > 0 && dos![at - 1].BlockId == EquippedPassive.BlockId) return "it " + which;
        return $"{EquippedPassive.Subject(args)} {which}";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var n = ctx.Args.Int("effect");
        var effects = ctx.Script.Project.Effects;
        if (n >= e.Index || n > effects.Count)
        {
            e.Error($"\"{Name}\": pick an effect listed before this one (effect 1 to {e.Index - 1}).");
            return;
        }
        if (effects[n - 1].When?.BlockId is "when.while_face_up" or SummonProcedureTrigger.BlockId or null)
        {
            e.Error($"\"{Name}\": effect {n} is not one that is activated, so it can't be given.");
            return;
        }
        var test = EquippedPassive.EquippedTest(ctx, "c");
        var target = ctx.Script.AddFunction("granttg", "e,c", [$"return c:GetEquipGroup():IsContains(e:GetHandler()){(test is null ? "" : " and " + test)}"]);
        e.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_GRANT)",
            $"{v}:SetRange(LOCATION_SZONE)",
            $"{v}:SetTargetRange(LOCATION_MZONE,LOCATION_MZONE)",
            $"{v}:SetTarget({target})",
            $"{v}:SetLabelObject(e{n.ToString(CultureInfo.InvariantCulture)})",
        ]);
    }
}
