using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "When your opponent activates a Spell Card in their hand that targets a monster you control (Quick Effect)", "If a
/// "Dragonmaid" monster effect is activated in the GY": an activation, with who, what kind, where from, which card and what
/// it targets as details.
/// </summary>
public sealed class ActivationTrigger : BlockDefinition
{
    public const string BlockId = "when.activation";
    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "Chain";
    public override string Name => "A card or effect is activated (who, what, where, which card, targets)";
    public override string Sentence => "{mode} {who} activates {what} {from} {card} {targets}, {zone}";
    public override string Help => "\"When ... (Quick Effect)\" responds in the chain and can negate it; \"If ...\" is a trigger that happens after.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("mode", "How", new("quick", "When", "When ... (Quick Effect, in the chain)"), new("trigger", "If", "If ... (a trigger)")),
        ParamSpec.Choice("who", "Who", new("opponent", "your opponent"), new("you", "you"), new("either", "a player")),
        ParamSpec.Choice("what", "What",
            new("any", "a card or effect"),
            new("card", "a card (not an effect of a card already out)", "a card (its activation)"),
            new("effect", "an effect (of a card already out)", "an effect, not a card's activation"),
            new("monster", "a monster effect"),
            new("spelltrap_card", "a Spell/Trap Card"),
            new("spell_card", "a Spell Card"),
            new("trap_card", "a Trap Card"),
            new("spelltrap_any", "a Spell/Trap Card or effect"),
            new("spell_any", "a Spell Card or effect"),
            new("trap_any", "a Trap Card or effect")),
        ParamSpec.Choice("from", "Activated in",
            new("any", "", "anywhere"), new("hand", "in the hand"), new("field", "on the field"), new("mzone", "in the Monster Zone"),
            new("szone", "in the Spell & Trap Zone"), new("gy", "in the GY"), new("banished", "while banished"), new("hand_gy", "in the hand or GY")),
        ParamSpec.Cards("card", "Which card (any)", () => new CardFilter { CardType = FilterCardType.Any, Zones = Zone.None }),
        ParamSpec.Choice("targets", "That targets",
            new("none", "", "(anything)"), new("your_card", "that targets a card(s) you control"), new("your_monster", "that targets a monster(s) you control"),
            new("this_card", "that targets this card"), new("any_target", "that targets a card(s)")),
        ParamSpec.Choice("zone", "This card is", Zones.RangeChoices),
        ParamSpec.Flag("optional", "optional (\"You can\")", true).ShowIf(b => b.Values.GetValueOrDefault("mode") == "trigger"),
    ];

    private static bool AnyCard(CardFilter f) => FilterCompiler.Conditions(f).Count == 0 && f.CardCode == 0;

    /// <summary>"your opponent activates a Spell Card in their hand that targets a monster you control".</summary>
    private static string Event(BlockArgs args)
    {
        var who = args.Choice("who");
        var subject = who switch { "you" => "you activate", "either" => "a player activates", _ => "your opponent activates" };
        var f = args.Cards("card");
        var what = args.Choice("what") switch
        {
            "card" => "a card",
            "effect" => "an effect",
            "monster" => "a monster effect",
            "spelltrap_card" => "a Spell/Trap Card",
            "spell_card" => "a Spell Card",
            "trap_card" => "a Trap Card",
            "spelltrap_any" => "a Spell/Trap Card or effect",
            "spell_any" => "a Spell Card or effect",
            "trap_any" => "a Trap Card or effect",
            _ => "a card or effect",
        };
        // "a "Dragonmaid" monster effect", "the effect of a DARK monster".
        if (!AnyCard(f))
        {
            var noun = FilterText.Describe(f, args.Pack, false, "", withLocation: false);
            what = args.Choice("what") switch
            {
                "monster" => $"the effect of {A(noun)}",
                "effect" or "any" => $"{A(noun)} or its effect",
                _ => $"{what} that is {A(noun)}",
            };
        }
        var their = who switch { "you" => "your", "opponent" => "their", _ => "the" };
        var from = args.Choice("from") switch
        {
            "hand" => $" in {their} hand",
            "field" => " on the field",
            "mzone" => " in the Monster Zone",
            "szone" => " in the Spell & Trap Zone",
            "gy" => $" in {their} GY",
            "banished" => " while it is banished",
            "hand_gy" => $" in {their} hand or GY",
            _ => "",
        };
        var targets = args.Choice("targets") switch
        {
            "your_card" => " that targets a card(s) you control",
            "your_monster" => " that targets a monster(s) you control",
            "this_card" => " that targets this card",
            "any_target" => " that targets a card(s)",
            _ => "",
        };
        return $"{subject} {what}{from}{targets}";
    }

    private static string A(string noun) => noun.StartsWith('"') || !"AEIOUaeiou".Contains(noun[0]) ? "a " + noun : "an " + noun;

    public override string Describe(BlockArgs args) =>
        args.Choice("mode") == "quick" ? $"When {Event(args)} (Quick Effect)" : $"If {Event(args)}";

    public override EventPhrase? Phrase(BlockArgs args) => args.Choice("mode") == "quick" ? null : new("If", Event(args));

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var quick = ctx.Args.Choice("mode") == "quick";
        if (quick)
        {
            e.Types.Add("EFFECT_TYPE_QUICK_O");
        }
        else
        {
            var optional = ctx.Args.Bool("optional");
            e.Types.Add("EFFECT_TYPE_FIELD");
            e.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
            e.AddProperty("EFFECT_FLAG_DELAY");
            e.IsMandatory = !optional;
        }
        e.Code = "EVENT_CHAINING";
        e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
        var tests = new List<string>();
        switch (ctx.Args.Choice("who"))
        {
            case "opponent": tests.Add("rp==1-tp"); break;
            case "you": tests.Add("rp==tp"); break;
        }
        tests.Add(ctx.Args.Choice("what") switch
        {
            "card" => "re:IsHasType(EFFECT_TYPE_ACTIVATE)",
            "effect" => "not re:IsHasType(EFFECT_TYPE_ACTIVATE)",
            "monster" => "re:IsActiveType(TYPE_MONSTER)",
            "spelltrap_card" => "re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:IsActiveType(TYPE_SPELL+TYPE_TRAP)",
            "spell_card" => "re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:IsActiveType(TYPE_SPELL)",
            "trap_card" => "re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:IsActiveType(TYPE_TRAP)",
            "spelltrap_any" => "re:IsActiveType(TYPE_SPELL+TYPE_TRAP)",
            "spell_any" => "re:IsActiveType(TYPE_SPELL)",
            "trap_any" => "re:IsActiveType(TYPE_TRAP)",
            _ => "true",
        });
        var loc = ctx.Args.Choice("from") switch
        {
            "hand" => "LOCATION_HAND", "field" => "LOCATION_ONFIELD", "mzone" => "LOCATION_MZONE", "szone" => "LOCATION_SZONE",
            "gy" => "LOCATION_GRAVE", "banished" => "LOCATION_REMOVED", "hand_gy" => "LOCATION_HAND+LOCATION_GRAVE", _ => null,
        };
        if (loc is not null) tests.Add($"Duel.GetChainInfo(ev,CHAININFO_TRIGGERING_LOCATION)&({loc})~=0");
        var card = ctx.Args.Cards("card").Clone();
        card.Zones = Zone.None;
        if (!AnyCard(card)) tests.Add($"{ctx.Script.Filter(card, null, "actfilter").Function}(re:GetHandler(),e,tp)");
        var targets = ctx.Args.Choice("targets");
        if (targets != "none")
        {
            var test = targets switch
            {
                "your_card" => "tc:IsControler(tp) and tc:IsOnField()",
                "your_monster" => "tc:IsControler(tp) and tc:IsLocation(LOCATION_MZONE)",
                "this_card" => "tc==e:GetHandler()",
                _ => "true",
            };
            var fn = ctx.Script.AddFunction("acttgfilter", "tc,e,tp", [$"return {test}"]);
            tests.Add("re:IsHasProperty(EFFECT_FLAG_CARD_TARGET)");
            tests.Add($"Duel.GetChainInfo(ev,CHAININFO_TARGET_CARDS) and Duel.GetChainInfo(ev,CHAININFO_TARGET_CARDS):IsExists({fn},1,nil,e,tp)");
        }
        // The checks need the chain link (ev), so they go into a function of their own.
        var con = ctx.Script.AddFunction("actcon", "e,tp,eg,ep,ev,re,r,rp", [$"return {string.Join(" and ", tests.Where(t => t != "true").DefaultIfEmpty("true"))}"]);
        e.Conditions.Add($"{con}(e,tp,eg,ep,ev,re,r,rp)");
    }
}

/// <summary>
/// "When an opponent's monster declares an attack", "When a "HERO" monster you control declares a direct attack", "When
/// your opponent's monster declares an attack targeting a DARK monster you control".
/// </summary>
public sealed class AttackDeclaredTrigger : BlockDefinition
{
    public const string BlockId = "when.attack_declared";
    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "Battle";
    public override string Name => "A monster declares an attack (which attacker, which target)";
    public override string Sentence => "{mode} {attacker} declares {how} {target}, {zone}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("mode", "How", new("quick", "When", "When ... (Quick Effect)"), new("trigger", "If", "If ... (a trigger)")),
        ParamSpec.Cards("attacker", "Which attacker", () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone),
        ParamSpec.Choice("how", "Attack", new("any", "an attack"), new("direct", "a direct attack"), new("targeting", "an attack targeting:")),
        ParamSpec.Cards("target", "Targeting", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone)
            .ShowIf(b => b.Values.GetValueOrDefault("how") == "targeting"),
        ParamSpec.Choice("zone", "This card is", Zones.RangeChoices),
        ParamSpec.Flag("optional", "optional (\"You can\")", true).ShowIf(b => b.Values.GetValueOrDefault("mode") == "trigger"),
    ];

    private static string Event(BlockArgs args)
    {
        var a = args.Cards("attacker");
        var owner = a.Owner switch { CardOwner.You => " you control", CardOwner.Opponent => " your opponent controls", _ => "" };
        var who = $"{(a.Owner == CardOwner.Opponent && FilterCompiler.Conditions(a).All(c => c == "c:IsType(TYPE_MONSTER)") ? "an opponent's monster" : "a " + FilterText.Describe(a, args.Pack, false, "", withLocation: false) + owner)}";
        var how = args.Choice("how") switch
        {
            "direct" => "a direct attack",
            "targeting" => $"an attack targeting a {FilterText.Describe(args.Cards("target"), args.Pack, false, "", withLocation: false)}{(args.Cards("target").Owner switch { CardOwner.You => " you control", CardOwner.Opponent => " your opponent controls", _ => "" })}",
            _ => "an attack",
        };
        return $"{who} declares {how}";
    }

    public override string Describe(BlockArgs args) =>
        args.Choice("mode") == "quick" ? $"When {Event(args)} (Quick Effect)" : $"If {Event(args)}";

    public override EventPhrase? Phrase(BlockArgs args) => args.Choice("mode") == "quick" ? null : new("If", Event(args));

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (ctx.Args.Choice("mode") == "quick") e.Types.Add("EFFECT_TYPE_QUICK_O");
        else
        {
            var optional = ctx.Args.Bool("optional");
            e.Types.Add("EFFECT_TYPE_FIELD");
            e.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
            e.IsMandatory = !optional;
        }
        e.Code = "EVENT_ATTACK_ANNOUNCE";
        e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
        var a = ctx.Args.Cards("attacker");
        var af = ctx.Script.Filter(a, null, "attackerfilter");
        var owner = a.Owner switch { CardOwner.You => " and Duel.GetAttacker():IsControler(tp)", CardOwner.Opponent => " and Duel.GetAttacker():IsControler(1-tp)", _ => "" };
        var tests = new List<string> { $"{af.Function}(Duel.GetAttacker(),e,tp){owner}" };
        if (a.ExcludeThisCard) tests.Add("Duel.GetAttacker()~=e:GetHandler()");
        switch (ctx.Args.Choice("how"))
        {
            case "direct": tests.Add("Duel.GetAttackTarget()==nil"); break;
            case "targeting":
            {
                var t = ctx.Args.Cards("target");
                var tf = ctx.Script.Filter(t, null, "attackedfilter");
                var towner = t.Owner switch { CardOwner.You => " and Duel.GetAttackTarget():IsControler(tp)", CardOwner.Opponent => " and Duel.GetAttackTarget():IsControler(1-tp)", _ => "" };
                tests.Add($"Duel.GetAttackTarget()~=nil and {tf.Function}(Duel.GetAttackTarget(),e,tp){towner}");
                break;
            }
        }
        e.Conditions.AddRange(tests);
    }
}
